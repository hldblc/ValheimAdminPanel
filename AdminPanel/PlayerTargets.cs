using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== 2.5.6: ask, don't guess (remote skill levels, player positions) ====================
    // Two things the panel used to guess at, now asked of the machine that actually knows them:
    //
    //  * A remote skill target's LEVELS. Skills live in the target's own save, and the skill browser showed the
    //    ADMIN's levels under the target's name - so raising someone else's skills never moved a number (and
    //    the old self-only "All skills" buttons moved the admin's own numbers, which looked like success).
    //    AP_SrvSkillReq -> companion -> AP_SkillReq on the target's client -> AP_SkillData back to this admin.
    //    A 2.5.6 target also answers every AP_SkillRaise with a fresh AP_SkillData.
    //
    //  * A player's POSITION. The roster carries it only for players who chose to be visible on the map
    //    (ZNet.UpdatePlayerList / RPC_PlayerList, m_publicPosition); everyone else arrives as (0, 0, 0), so
    //    "TP to", Watch, Map and lightning all went to the world centre. AP_SrvPlayerPos asks the server,
    //    which always knows (ZNetPeer.m_refPos), and the requested action runs on the reply.
    public partial class AdminPanelPlugin
    {
        private enum PosAction { TpTo, Watch, Map, Smite }

        private const float PosReplyTimeout = 6f;     // a companion older than 2.5.6 never answers AP_SrvPlayerPos
        private const float SkillReplyWait = 6f;      // ... nor a target without a 2.5.6 companion AP_SkillReq
        private const float SkillRefreshEvery = 15f;  // the target's levels also move on their own while they play

        // One position lookup in flight at a time; a newer click replaces the older request.
        private long _posReqUid;
        private string _posReqName;
        private PosAction _posReqAction;
        private float _posReqAt;

        // The remote skill table, keyed by skill enum name, and whose it is.
        private long _rsFor;
        private Dictionary<string, float> _rsLevels;
        private float _rsAt;
        private long _rsReqFor;
        private float _rsReqAt, _rsNextReq;

        [HarmonyPatch]
        internal static class PlayerTargetRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_SkillData", OnSkillData);
                // Server truth: the network half gates on the server peer, the bridge half serves a listen host.
                ZRoutedRpc.instance.Register<ZPackage>("AP_PlayerPos", OnPlayerPosData);
                AdminPanelLocalBridge.Register("AP_PlayerPos", ParsePlayerPos);
            }
        }

        private void ResetPlayerTargets()
        {
            _posReqUid = 0L; _posReqName = null;
            _rsFor = 0L; _rsLevels = null; _rsReqFor = 0L; _rsReqAt = 0f; _rsNextReq = 0f;
        }

        private void TickPlayerTargets()
        {
            if (_posReqUid == 0L || Time.realtimeSinceStartup - _posReqAt < PosReplyTimeout) return;
            Message(Loc.T("players.msg_pos_no_reply", _posReqName ?? "?"));
            _posReqUid = 0L;
        }

        // ---------------- positions ----------------

        // Runs a map-position action on a roster row. A shared position is used as is; a hidden one is asked of
        // the server first and the action runs when AP_PlayerPos arrives.
        private void ActOnPlayerPos(ZNet.PlayerInfo info, PosAction action)
        {
            // Any newer click replaces a lookup still in flight, whatever it turns out to need - otherwise a late
            // reply for the older (hidden) player would still act after an immediate action on a public one.
            _posReqUid = 0L;
            if (LocalPlayer == null) { Message(Loc.T("players.not_connected")); return; }
            if (info.m_publicPosition) { RunPosAction(action, info.m_name, info.m_position, true); return; }
            var uid = PeerIdOf(info);
            if (uid == 0L) { Message(Loc.T("common.target_not_ready", info.m_name)); return; }
            _posReqUid = uid;
            _posReqName = info.m_name;
            _posReqAction = action;
            _posReqAt = Time.realtimeSinceStartup;
            // Message first: on a listen host the companion answers inside this very SrvRpc call.
            Message(Loc.T("players.msg_locating", info.m_name));
            SrvRpc("AP_SrvPlayerPos", uid);
        }

        private void RunPosAction(PosAction action, string name, Vector3 pos, bool shared)
        {
            // A reply can land after the player died or the admin gate closed; neither may still act.
            if (LocalPlayer == null || !GateAllowsCheats) return;
            switch (action)
            {
                case PosAction.TpTo:
                    LocalPlayer.TeleportTo(pos + Vector3.up, LocalPlayer.transform.rotation, true);
                    Message(Loc.T("world.msg_tp_to", name));
                    break;
                case PosAction.Watch:
                    if (!_ghost) { _ghost = true; LocalPlayer.SetGhostMode(true); }
                    if (!_fly) { _fly = true; Player.m_debugMode = true; LocalPlayer.ToggleDebugFly(); }
                    LocalPlayer.TeleportTo(pos + Vector3.up * 8f, LocalPlayer.transform.rotation, true);
                    Message(Loc.T("players.msg_watching", name));
                    break;
                case PosAction.Map:
                    // A shared position is on everyone's map already, so pinging it for all reveals nothing. A
                    // hidden one is shown on the ADMIN's map only: a server-wide ping would publish exactly what
                    // that player chose to keep off the map.
                    if (shared) { Chat.instance?.SendPing(pos); Message(Loc.T("players.msg_pinged", name)); }
                    else { Minimap.instance?.ShowPointOnMap(pos); Message(Loc.T("players.msg_shown_on_map", name)); }
                    break;
                case PosAction.Smite:
                    SendServerSpawn(1, "lightning", pos, 1, 1, false);
                    Message(Loc.T("players.msg_lightning", name));
                    break;
            }
        }

        private static void OnPlayerPosData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParsePlayerPos(self, pkg);
        }

        // AP_PlayerPos v1: int ver, long uid, bool known, Vector3 pos.
        internal static void ParsePlayerPos(AdminPanelPlugin self, ZPackage pkg)
        {
            long uid;
            bool known;
            Vector3 pos;
            try
            {
                if (pkg.ReadInt() != 1) return;
                uid = pkg.ReadLong();
                known = pkg.ReadBool();
                pos = pkg.ReadVector3();
            }
            catch (Exception) { return; }   // malformed reply - the timeout tells the admin
            // Only the lookup still pending: a late reply to a replaced or timed-out request is dropped.
            if (uid == 0L || uid != self._posReqUid) return;
            var name = self._posReqName ?? "?";
            var action = self._posReqAction;
            self._posReqUid = 0L;
            if (!known) { self.Message(Loc.T("players.msg_pos_unknown", name)); return; }
            // Outside the parse guard: a failing action must be reported, not swallowed with the timeout already off.
            try { self.RunPosAction(action, name, pos, false); }
            catch (Exception e)
            {
                self.Logger.LogWarning($"Player position action {action} for {name} failed: {e.Message}");
                self.Message(Loc.T("players.msg_pos_unknown", name));
            }
        }

        // ---------------- remote skill levels ----------------

        // Asks the picked target's client for its skill table: once when the pick changes, then every
        // SkillRefreshEvery seconds while the browser stays open. Layout pass only - it sends an RPC and moves
        // the throttle, which must happen once per frame, not once per OnGUI pass.
        private void RequestRemoteSkills()
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            var target = _skillTargetId;
            if (target == 0L) return;
            var now = Time.realtimeSinceStartup;
            if (target == _rsReqFor && now < _rsNextReq) return;
            if (!IsLive(target)) return;   // dead or gone: the Apply-to label already says so
            _rsReqFor = target;
            _rsReqAt = now;
            _rsNextReq = now + SkillRefreshEvery;
            SrvRpc("AP_SrvSkillReq", target);
        }

        private string RemoteSkillsStatus()
        {
            var name = SkillTargetLabel();
            // Dead or gone: nothing is asked (RequestRemoteSkills), so do not claim to be asking.
            if (!IsLive(_skillTargetId)) return Loc.T("common.target_not_ready", name);
            if (_rsFor == _skillTargetId && _rsLevels != null)
                return Loc.T("player.levels_of", name, Mathf.RoundToInt(Time.realtimeSinceStartup - _rsAt));
            if (_rsReqFor == _skillTargetId && Time.realtimeSinceStartup - _rsReqAt > SkillReplyWait)
                return Loc.T("player.levels_no_reply", name);
            return Loc.T("player.levels_waiting", name);
        }

        // The target reports only the skills it has, at their stored level; one it never trained is level 0. Floored
        // like the game's own Skills dialog (and this list's local rows, Skills.GetSkillLevel).
        private string RemoteSkillLevel(Skills.SkillType type)
        {
            if (_rsFor != _skillTargetId || _rsLevels == null) return "...";
            return _rsLevels.TryGetValue(type.ToString(), out var level) ? Mathf.Floor(level).ToString("0") : "0";
        }

        // AP_SkillData v1 (target client -> this admin): int ver, string playerName, int n, n x (string skill,
        // float level). Accepted only from the player currently picked: the sender is the target's own peer id,
        // re-stamped by the server companion's RouteRpcSanitizer, so no other client can paint these numbers.
        private static void OnSkillData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || sender == 0L || sender != self._skillTargetId) return;
            try
            {
                if (pkg.ReadInt() != 1) return;
                pkg.ReadString();   // their name for themselves; the picker already shows the roster name
                var n = pkg.ReadInt();
                if (n < 0 || n > 64) return;
                var levels = new Dictionary<string, float>(n, StringComparer.Ordinal);
                for (var i = 0; i < n; i++)
                {
                    var skill = pkg.ReadString();
                    var level = pkg.ReadSingle();
                    if (!string.IsNullOrEmpty(skill) && !float.IsNaN(level)) levels[skill] = Mathf.Clamp(level, 0f, 100f);
                }
                self._rsFor = sender;
                self._rsLevels = levels;
                self._rsAt = Time.realtimeSinceStartup;
            }
            catch (Exception) { /* malformed reply - keep the table we had */ }
        }
    }
}

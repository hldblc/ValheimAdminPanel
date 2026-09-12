using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #21 Death rules (server + client-side executor) ====================
    // A per-server death policy on top of the TIER-VANILLA death feed (Wave34Core.OnDeath):
    //
    //   DeathRuleMode 0  off (default) — nothing here runs, nothing is written.
    //   DeathRuleMode 1  lives — every player has LivesPerPlayer lives; a death costs one and the player is
    //                    whispered the remainder through the vanilla ShowMessage path (unmodded clients see
    //                    it). At zero the player is kicked and a "locked" flag in the "lives" table keeps them
    //                    out at RPC_PeerInfo time until an admin resets their lives or revives them.
    //   DeathRuleMode 2  permadeath — the same lockout plus the escrow the vault already offers: a vault
    //                    snapshot of the inventory (modded clients only) and a queued full inventory reset
    //                    that applies the next time the player joins (Wave4PlayerData.QueueReset). Wave 4 has
    //                    no separate "quarantine" API; those two are the reachable equivalents.
    //
    //   KeepGearOnDeath / KeepSkillsOnDeath are CLIENT-side: the executor AP_DeathRules(pkg) carries the two
    //   flags to every modded client (broadcast on change, and to each peer ~10 s after join); a Harmony
    //   prefix on Player.CreateTombStone returns false so the inventory never moves to a grave. Verified by
    //   decompiling: Player.OnDeath (owner-only) calls CreateTombStone(), then Game.RequestRespawn, and
    //   Game._RequestRespawn saves the LIVE player (m_playerProfile.SavePlayerData) before destroying it, so
    //   an inventory that never left the Player is what the respawn reloads. Skills.OnDeath() is the only
    //   caller of LowerAllSkills on death, so a prefix returning false keeps the skills. Unmodded clients
    //   keep vanilla behaviour — the admin card says so.
    //
    // Table "lives": platformId -> "remaining|lastDeathTicks|locked|name" (the last two fields are ours on top
    // of the contract's two; readers tolerate the short form). Admins are exempt from the lockout (like the
    // mod guard's kick): an owner locked out by their own rule could not reach the panel that switches it off.
    internal static class Wave8SystemsDeath
    {
        private const int Ver = 1;
        private const string TblLives = "lives";
        private const int RowCap = 100;                 // wire contract: rows <= 100
        private const int MaxIdLen = 64;
        private const float AnnounceDelay = 2f;         // after the +10 s join push: HUD is up by then
        private const float KickDelayLives = 1.5f;      // let the whisper reach the client before the socket closes
        private const float KickDelayPermadeath = 6f;   // the vault snapshot reply has to land first
        private const float ConfigWatchSeconds = 2f;
        private const int ErrBanned = 8;                // ZNet.ConnectionStatus.ErrorBanned — the code Wave1's gate uses

        // ---- config (read live; they can change at runtime) ----
        private static ConfigEntry<int> _modeCfg;
        private static ConfigEntry<int> _livesCfg;
        private static ConfigEntry<bool> _keepGearCfg;
        private static ConfigEntry<bool> _keepSkillsCfg;
        private static ConfigEntry<bool> _announceCfg;

        internal static int Mode => _modeCfg != null ? Mathf.Clamp(_modeCfg.Value, 0, 2) : 0;
        internal static int Lives => _livesCfg != null ? Mathf.Clamp(_livesCfg.Value, 1, 20) : 3;
        private static bool KeepGear => _keepGearCfg != null && _keepGearCfg.Value;
        private static bool KeepSkills => _keepSkillsCfg != null && _keepSkillsCfg.Value;
        private static bool AnnounceOnJoin => _announceCfg != null && _announceCfg.Value;

        // ---- server state ----
        private sealed class PendingKick { public long Uid; public float At; }
        private static readonly List<PendingKick> Kicks = new List<PendingKick>();

        private sealed class PendingAnnounce { public long Uid; public string HostId; public float At; }
        private static readonly List<PendingAnnounce> Announces = new List<PendingAnnounce>();

        private static float _nextConfigWatch;
        private static bool _lastKeepGear, _lastKeepSkills;
        private static int _lastMode = -1, _lastLives = -1;
        private static bool _inited;

        // ---- client state (written by the executor, read by the two prefixes; cleared on disconnect) ----
        private static bool _clientRulesSet;
        private static bool _clientKeepGear;
        private static bool _clientKeepSkills;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _modeCfg = cfg.Bind("Features", "DeathRuleMode", 0,
                    new ConfigDescription("Death rules: 0 = off, 1 = lives (LivesPerPlayer lives, then kicked and locked out until an admin resets/revives), 2 = permadeath (lockout + vault snapshot + queued inventory reset). Off by default: this changes gameplay.",
                        new AcceptableValueRange<int>(0, 2)));
                _livesCfg = cfg.Bind("Features", "LivesPerPlayer", 3,
                    new ConfigDescription("Lives each player starts with when DeathRuleMode is 1 or 2.",
                        new AcceptableValueRange<int>(1, 20)));
                _keepGearCfg = cfg.Bind("Features", "KeepGearOnDeath", false,
                    "Skip the tombstone on death so the inventory survives the respawn. Applies on clients that run AdminPanelCompanion.dll; unmodded clients keep vanilla graves.");
                _keepSkillsCfg = cfg.Bind("Features", "KeepSkillsOnDeath", false,
                    "Skip the skill loss on death. Applies on clients that run AdminPanelCompanion.dll; unmodded clients lose skills as in vanilla.");
                _announceCfg = cfg.Bind("Features", "DeathRuleAnnounceOnJoin", true,
                    "Tell joining players the active death rules and their remaining lives (only when DeathRuleMode is not 0).");
            }

            // Owner-only set (null grant), moderator-tier per-player actions. The read poll is NOT audited
            // (the panel re-requests it on a timer); its gate lives in OnRulesReq.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDeathRulesSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDeathRulesPlayer", "moderator");

            Wave8Systems.ApplyPatch("Wave8SystemsJoinGatePatch", typeof(Wave8SystemsJoinGatePatch),
                "locked-out players are not refused at join");
            Wave8Systems.ApplyPatch("Wave8SystemsTombstonePatch", typeof(Wave8SystemsTombstonePatch),
                "keep-gear-on-death unavailable on this client");
            Wave8Systems.ApplyPatch("Wave8SystemsSkillsDeathPatch", typeof(Wave8SystemsSkillsDeathPatch),
                "keep-skills-on-death unavailable on this client");

            try { Wave34Core.OnDeath += OnDeathObserved; }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Death rules: could not subscribe to the death event ({e.Message}); lives are not counted."); }
        }

        internal static void Tick()
        {
            if (!_inited) return;

            // CLIENT side: rules apply only while connected to the server that sent them.
            if (ZNet.instance == null)
            {
                if (_clientRulesSet) ClearClientRules();
                return;
            }
            if (!ZNet.instance.IsServer()) return;

            var now = Time.unscaledTime;

            if (Kicks.Count > 0)
            {
                for (var i = Kicks.Count - 1; i >= 0; i--)
                {
                    if (now < Kicks[i].At) continue;
                    var uid = Kicks[i].Uid;
                    Kicks.RemoveAt(i);
                    var ok = CompanionPlugin.FeatureKick(uid);
                    CompanionPlugin.FeatureLog($"Death rules: kick of peer {uid}: {(ok ? "kicked" : "peer not found")}");
                }
            }

            if (Announces.Count > 0)
            {
                for (var i = Announces.Count - 1; i >= 0; i--)
                {
                    if (now < Announces[i].At) continue;
                    var a = Announces[i];
                    Announces.RemoveAt(i);
                    if (!Wave8Systems.PeerConnected(a.Uid)) continue;
                    if (Mode == 0 || !AnnounceOnJoin) continue;
                    Wave8Systems.Tell(a.Uid, JoinSummary(a.HostId), false);
                }
            }

            // Config can change at runtime (panel Apply, or a hand edit): broadcast the client-side flags
            // whenever they differ from what was last sent.
            if (now >= _nextConfigWatch)
            {
                _nextConfigWatch = now + ConfigWatchSeconds;
                var kg = KeepGear; var ks = KeepSkills; var m = Mode; var l = Lives;
                if (kg != _lastKeepGear || ks != _lastKeepSkills || m != _lastMode || l != _lastLives)
                {
                    _lastKeepGear = kg; _lastKeepSkills = ks; _lastMode = m; _lastLives = l;
                    BroadcastRules();
                }
            }
        }

        // ==================== join / leave (called by the group's shared hooks) ====================

        // ~10 s after the handshake: push the client-side flags, then announce the rules a moment later.
        internal static void OnPeerReady(long uid, string hostId, string name)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (KeepGear || KeepSkills || Mode != 0) SendRules(uid);
            if (Mode != 0 && AnnounceOnJoin)
                Announces.Add(new PendingAnnounce { Uid = uid, HostId = hostId, At = Time.unscaledTime + AnnounceDelay });
        }

        internal static void OnPeerLeft(long uid)
        {
            for (var i = Kicks.Count - 1; i >= 0; i--) if (Kicks[i].Uid == uid) Kicks.RemoveAt(i);
            for (var i = Announces.Count - 1; i >= 0; i--) if (Announces[i].Uid == uid) Announces.RemoveAt(i);
        }

        private static string JoinSummary(string hostId)
        {
            int remaining; long last; bool locked; string name;
            ReadRow(hostId, out remaining, out last, out locked, out name);
            var mode = Mode == 2 ? "PERMADEATH" : "LIVES";
            var extra = (KeepGear ? " Gear is kept on death." : "") + (KeepSkills ? " Skills are kept on death." : "");
            return $"Death rules: {mode} mode, {Lives} lives per player. You have {remaining} left.{extra}";
        }

        // ==================== the death event (TIER-VANILLA source) ====================

        private static void OnDeathObserved(Wave34Core.DeathInfo d)
        {
            if (!_inited || Mode == 0) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var id = Wave8Systems.CleanId(d.PlatformId, MaxIdLen);
            if (id.Length == 0 || id == "?")
            {
                CompanionPlugin.FeatureLog($"Death rules: {d.PlayerName} died but their id is unknown (left before the death resolved) - not counted.");
                return;
            }
            if (!FeatureStore.Ready)
            {
                CompanionPlugin.FeatureLog("Death rules: store not ready - death not counted.");
                return;
            }

            int remaining; long last; bool locked; string name;
            ReadRow(id, out remaining, out last, out locked, out name);
            if (!string.IsNullOrEmpty(d.PlayerName) && d.PlayerName != "?") name = d.PlayerName;
            if (remaining > 0) remaining--;

            var uid = Wave8Systems.UidOfId(id);
            var isAdmin = CompanionPlugin.FeatureIsAdminId(id);

            if (remaining > 0)
            {
                WriteRow(id, remaining, DateTime.UtcNow.Ticks, false, name);
                if (uid != 0L) Wave8Systems.Tell(uid, $"Death rules: {remaining} of {Lives} lives left.");
                CompanionPlugin.FeatureLog($"Death rules: {name} ({id}) has {remaining}/{Lives} lives left.");
                return;
            }

            if (isAdmin)
            {
                // Counted, whispered, never locked out — see the header.
                WriteRow(id, 0, DateTime.UtcNow.Ticks, false, name);
                if (uid != 0L) Wave8Systems.Tell(uid, "Death rules: no lives left - admins are exempt from the lockout.");
                CompanionPlugin.FeatureLog($"Death rules: admin {name} ({id}) is out of lives (exempt from the lockout).");
                return;
            }

            WriteRow(id, 0, DateTime.UtcNow.Ticks, true, name);
            var permadeath = Mode == 2;
            CompanionPlugin.SrvAudit(0L, "DEATHRULES-LOCKOUT", $"id={id} name={name} mode={(permadeath ? "permadeath" : "lives")}");
            Wave1AuditRpc.PostModLog($"DEATHRULES {name} ({id}) is out of lives and locked out{(permadeath ? " (permadeath)" : "")}");
            Wave1Moderation.NotifyOnlineAdmins($"Death rules: {name} ({id}) is out of lives and locked out{(permadeath ? " (permadeath)" : "")}. Revive them from the Death Rules card.");

            if (permadeath)
            {
                // Escrow: snapshot what they carried (needs the companion on their client), then queue the
                // wipe that applies if an admin ever revives them. Both are best-effort and logged.
                if (uid != 0L && Wave34Core.HasMod(uid) == true)
                {
                    try { Wave4Vault.RequestSnapshot(uid, "permadeath " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "deathrules", 0L); }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Death rules: permadeath snapshot request failed: {e.Message}"); }
                }
                try
                {
                    if (!Wave4PlayerData.QueueReset(id, "permadeath"))
                        CompanionPlugin.FeatureLog($"Death rules: could not queue the permadeath inventory reset for {id} (queue full or store unavailable).");
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Death rules: permadeath reset queue failed: {e.Message}"); }
            }

            if (uid != 0L)
            {
                Wave8Systems.Tell(uid, permadeath
                    ? "PERMADEATH: you have used your last life. Your character is locked out of this server until an admin revives you."
                    : "You have used your last life. You are locked out of this server until an admin revives you.");
                CompanionPlugin.NotifySender(uid, permadeath
                    ? "Permadeath: locked out until an admin revives you."
                    : "Out of lives: locked out until an admin revives you.");
                Kicks.Add(new PendingKick { Uid = uid, At = Time.unscaledTime + (permadeath ? KickDelayPermadeath : KickDelayLives) });
            }
        }

        // ==================== table ====================

        private static void ReadRow(string id, out int remaining, out long lastDeath, out bool locked, out string name)
        {
            remaining = Lives; lastDeath = 0L; locked = false; name = "";
            try
            {
                var t = FeatureStore.Table(TblLives);
                var key = Wave8Systems.FindKey(t, id);
                if (key == null) return;
                ParseRow(t[key], out remaining, out lastDeath, out locked, out name);
            }
            catch (Exception) { }
        }

        private static void ParseRow(string raw, out int remaining, out long lastDeath, out bool locked, out string name)
        {
            remaining = Lives; lastDeath = 0L; locked = false; name = "";
            if (string.IsNullOrEmpty(raw)) return;
            var p = raw.Split(new[] { '|' }, 4);
            if (p.Length > 0) remaining = Mathf.Clamp(Wave8Systems.ParseInt(p[0], Lives), 0, 20);
            if (p.Length > 1) lastDeath = Wave8Systems.ParseLong(p[1], 0L);
            if (p.Length > 2) locked = p[2] == "1";
            if (p.Length > 3) name = p[3];
        }

        private static void WriteRow(string id, int remaining, long lastDeath, bool locked, string name)
        {
            try
            {
                var t = FeatureStore.Table(TblLives);
                var key = Wave8Systems.FindKey(t, id) ?? id;
                t[key] = remaining.ToString(CultureInfo.InvariantCulture) + "|" +
                         lastDeath.ToString(CultureInfo.InvariantCulture) + "|" +
                         (locked ? "1" : "0") + "|" + Wave8Systems.CleanText(name, 60);
                FeatureStore.SaveTable(TblLives);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Death rules: lives table write failed for {id}: {e.Message}"); }
        }

        internal static bool IsLockedOut(string hostId)
        {
            if (Mode == 0 || string.IsNullOrEmpty(hostId)) return false;
            int remaining; long last; bool locked; string name;
            ReadRow(hostId, out remaining, out last, out locked, out name);
            return locked;
        }

        // ==================== join gate (server) ====================

        // PREFIX on RPC_PeerInfo — the only place a connection can be refused before the peer is admitted;
        // identity is peer.m_socket.GetHostName() only (m_uid/m_playerName are assigned later). Same shape
        // and same fail-OPEN discipline as Wave1Moderation.JoinGatePatch: a bug here must never make the
        // server unjoinable. Stacked as its own prefix class on the same target.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave8SystemsJoinGatePatch
        {
            private static bool Prefix(ZNet __instance, ZRpc rpc)
            {
                try
                {
                    if (!_inited || Mode == 0) return true;
                    if (__instance == null || !__instance.IsServer() || rpc == null) return true;
                    ZNetPeer peer = null;
                    foreach (var p in __instance.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null || peer.m_socket == null) return true;
                    var host = peer.m_socket.GetHostName();
                    if (string.IsNullOrEmpty(host)) return true;
                    if (CompanionPlugin.FeatureIsAdminId(host)) return true;
                    if (!IsLockedOut(host)) return true;

                    CompanionPlugin.FeatureLog($"Death rules: refused {host} - out of lives (locked out until revived)");
                    Wave1Moderation.NotifyOnlineAdmins($"Death rules: {host} tried to join but is locked out (out of lives).");
                    try { rpc.Invoke("Error", ErrBanned); }
                    catch (Exception) { }
                    // Explicit teardown so the socket does not linger until the connect timeout (the socket's
                    // Close flushes its send queue first, so the Error above still reaches the client).
                    try
                    {
                        var m = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
                        if (m != null) m.Invoke(ZNet.instance, new object[] { peer });
                    }
                    catch (Exception) { }
                    return false;
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Death rules join gate error (connection allowed): {e.Message}");
                    return true;
                }
            }
        }

        // ==================== executor: server -> every modded client ====================

        private static ZPackage RulesPackage()
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(KeepGear);
            pkg.Write(KeepSkills);
            pkg.Write(Mode);
            pkg.Write(Lives);
            return pkg;
        }

        private static void SendRules(long uid)
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "AP_DeathRules", RulesPackage()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_DeathRules to {uid} failed: {e.Message}"); }
        }

        // Everybody also dispatches locally on a listen server, so the host's own client applies them too.
        private static void BroadcastRules()
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "AP_DeathRules", RulesPackage()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_DeathRules broadcast failed: {e.Message}"); }
        }

        // CLIENT side. Trust template: only packets the SERVER sent are honoured (SenderIsTrustedServer);
        // unknown payload versions are discarded whole.
        internal static void OnRulesExecutor(long sender, ZPackage pkg)
        {
            try
            {
                if (!Wave8Systems.SenderIsTrustedServer(sender)) return;
                if (pkg.ReadInt() != Ver) return;
                var keepGear = pkg.ReadBool();
                var keepSkills = pkg.ReadBool();
                var mode = pkg.ReadInt();
                var lives = pkg.ReadInt();
                var changed = !_clientRulesSet || keepGear != _clientKeepGear || keepSkills != _clientKeepSkills;
                _clientKeepGear = keepGear;
                _clientKeepSkills = keepSkills;
                _clientRulesSet = true;
                if (changed && Player.m_localPlayer != null && (keepGear || keepSkills))
                {
                    var what = keepGear && keepSkills ? "gear and skills are kept on death"
                             : keepGear ? "gear is kept on death" : "skills are kept on death";
                    try { Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Server death rules: " + what + "."); }
                    catch (Exception) { }
                }
                if (mode < 0 || lives < 0) return;   // values are informational on the client; nothing else to do
            }
            catch (Exception) { /* malformed executor payload: keep whatever rules we had */ }
        }

        private static void ClearClientRules()
        {
            _clientRulesSet = false;
            _clientKeepGear = false;
            _clientKeepSkills = false;
        }

        // Player.CreateTombStone() is public, parameterless, and the ONLY thing between "died carrying items"
        // and "inventory moved to a grave" (Player.cs, verified by decompiling). Returning false keeps the
        // inventory on the Player object, which Game._RequestRespawn then saves and reloads.
        [HarmonyPatch(typeof(Player), "CreateTombStone")]
        internal static class Wave8SystemsTombstonePatch
        {
            private static bool Prefix(Player __instance)
            {
                try
                {
                    if (!_clientRulesSet || !_clientKeepGear) return true;
                    if (__instance == null || __instance != Player.m_localPlayer) return true;
                    if (ZNet.instance == null) return true;   // rules are only meaningful while connected
                    try { __instance.Message(MessageHud.MessageType.TopLeft, "Death rules: your gear stays with you."); }
                    catch (Exception) { }
                    return false;
                }
                catch (Exception) { return true; }
            }
        }

        // Skills.OnDeath() is called from Player.OnDeath only on a hard death (owner side); skipping it is
        // exactly the vanilla "no skill drain" status effect, applied server-wide.
        [HarmonyPatch(typeof(Skills), "OnDeath")]
        internal static class Wave8SystemsSkillsDeathPatch
        {
            private static bool Prefix()
            {
                try
                {
                    if (!_clientRulesSet || !_clientKeepSkills) return true;
                    if (ZNet.instance == null) return true;
                    return false;
                }
                catch (Exception) { return true; }
            }
        }

        // ==================== admin RPCs ====================

        // Read poll (not audited). A moderator who may act on players may also read the table, otherwise
        // taking this read out of the audited set would demote it to owner-only whenever roles are on.
        internal static void OnRulesReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDeathRulesReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvDeathRulesPlayer")) return;
            SendData(sender);
        }

        // ZPackage: int ver, int mode, int lives, bool keepGear, bool keepSkills, bool announce.
        internal static void OnRulesSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDeathRulesSet")) return;
            int ver, mode, lives; bool keepGear, keepSkills, announce;
            try
            {
                ver = pkg.ReadInt();
                mode = pkg.ReadInt();
                lives = pkg.ReadInt();
                keepGear = pkg.ReadBool();
                keepSkills = pkg.ReadBool();
                announce = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvDeathRulesSet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            mode = Mathf.Clamp(mode, 0, 2);
            lives = Mathf.Clamp(lives, 1, 20);
            try
            {
                // ConfigEntry setters persist through BepInEx's config file, so the rules survive a restart.
                if (_modeCfg != null) _modeCfg.Value = mode;
                if (_livesCfg != null) _livesCfg.Value = lives;
                if (_keepGearCfg != null) _keepGearCfg.Value = keepGear;
                if (_keepSkillsCfg != null) _keepSkillsCfg.Value = keepSkills;
                if (_announceCfg != null) _announceCfg.Value = announce;
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Death rules: config write failed: {e.Message}"); }

            CompanionPlugin.SrvAudit(sender, "DEATHRULES-SET",
                $"mode={mode} lives={lives} keepGear={keepGear} keepSkills={keepSkills} announce={announce}");
            CompanionPlugin.FeatureLog($"Death rules set by {Wave1AuditRpc.AdminLabel(sender)}: mode={mode} lives={lives} keepGear={keepGear} keepSkills={keepSkills}");
            CompanionPlugin.NotifySender(sender,
                mode == 0 ? "Death rules are OFF." : $"Death rules: {(mode == 2 ? "permadeath" : "lives")} mode, {lives} lives per player.");
            _lastKeepGear = keepGear; _lastKeepSkills = keepSkills; _lastMode = mode; _lastLives = lives;
            BroadcastRules();
            if (mode != 0) Wave8Systems.AnnounceAll($"Death rules are now active: {(mode == 2 ? "PERMADEATH" : "LIVES")} mode, {lives} lives per player.");
            SendData(sender);
        }

        // ZPackage: int ver, string id, int op (0 = reset lives + unlock, 1 = revive = unlock only).
        internal static void OnPlayerAction(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDeathRulesPlayer")) return;
            int ver, op; string id;
            try
            {
                ver = pkg.ReadInt();
                id = pkg.ReadString();
                op = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvDeathRulesPlayer: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            id = Wave8Systems.CleanId(id, MaxIdLen);
            if (id.Length == 0) { CompanionPlugin.NotifySender(sender, "Death rules: invalid player id."); return; }
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Death rules: the store is not ready."); return; }

            int remaining; long last; bool locked; string name;
            ReadRow(id, out remaining, out last, out locked, out name);
            var uid = Wave8Systems.UidOfId(id);
            var label = name.Length > 0 ? $"{name} ({id})" : id;
            if (op == 0)
            {
                WriteRow(id, Lives, last, false, name);
                CompanionPlugin.SrvAudit(sender, "DEATHRULES-RESET", $"id={id} lives={Lives} wasLocked={locked}");
                CompanionPlugin.NotifySender(sender, $"Lives reset to {Lives} for {label}.");
                if (uid != 0L) Wave8Systems.Tell(uid, $"An admin reset your lives: {Lives} of {Lives}.");
            }
            else
            {
                WriteRow(id, remaining, last, false, name);
                CompanionPlugin.SrvAudit(sender, "DEATHRULES-REVIVE", $"id={id} remaining={remaining} wasLocked={locked}");
                CompanionPlugin.NotifySender(sender, locked
                    ? $"{label} revived - they can join again ({remaining} lives left; the next death locks them out again unless you reset their lives)."
                    : $"{label} was not locked out; nothing to revive.");
                if (uid != 0L) Wave8Systems.Tell(uid, "An admin revived you.");
            }
            Wave1AuditRpc.PostModLog($"DEATHRULES {Wave1AuditRpc.AdminLabel(sender)} {(op == 0 ? "reset the lives of" : "revived")} {label}");
            SendData(sender);
        }

        // Reply shape (v1): int ver, int mode, int lives, bool keepGear, bool keepSkills, bool announce,
        // bool storeReady, int rows(<=100) x (string id, string name, int remaining, long lastDeathTicks, bool locked).
        // Rows: most recent death first.
        private static void SendData(long sender)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(Mode);
            pkg.Write(Lives);
            pkg.Write(KeepGear);
            pkg.Write(KeepSkills);
            pkg.Write(AnnounceOnJoin);
            var ready = false;
            try { ready = FeatureStore.Ready; } catch (Exception) { }
            pkg.Write(ready);

            var rows = new List<KeyValuePair<string, string>>();
            if (ready)
            {
                try { foreach (var kv in FeatureStore.Table(TblLives)) rows.Add(kv); }
                catch (Exception) { }
            }
            rows.Sort((a, b) =>
            {
                int ra; long la; bool ka; string na;
                int rb; long lb; bool kb; string nb;
                ParseRow(a.Value, out ra, out la, out ka, out na);
                ParseRow(b.Value, out rb, out lb, out kb, out nb);
                return lb.CompareTo(la);
            });
            var n = rows.Count < RowCap ? rows.Count : RowCap;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                int remaining; long last; bool locked; string name;
                ParseRow(rows[i].Value, out remaining, out last, out locked, out name);
                pkg.Write(Wave8Systems.Clamp(rows[i].Key, MaxIdLen));
                pkg.Write(Wave8Systems.Clamp(name, 60));
                pkg.Write(remaining);
                pkg.Write(last);
                pkg.Write(locked);
            }
            CompanionPlugin.ReplyTo(sender, "AP_DeathRulesData", pkg);
        }
    }
}

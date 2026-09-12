using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 1 - Moderation section (Extras tab) ====================
    // Client half of the moderation suite. Everything here is UI + request plumbing: the server companion
    // owns the truth (temp bans, mutes, watchlist, warnings, frozen players, lockdown) and ships it back in
    // one AP_ModState reply that this file parses defensively and draws from a per-frame Layout snapshot.
    //
    // Member prefix: "Mod". Locale prefix: "mod.".
    //
    // IMGUI law observed throughout: the reply handler writes _modState at ANY time (it runs in ZNet.Update),
    // the draw method reads ONLY _modStateLayout, pinned on the Layout pass. Every list length, every
    // empty-hint branch and the host/connected gates come from that snapshot, so Repaint can never see a
    // different control count than Layout reserved. Text fields are edited live on purpose - a TextField is
    // one control regardless of its contents.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _modSectionCfg;
        private ConfigEntry<int> _modPollSecondsCfg;
        private bool _modInited;

        // ---- server truth (written by OnModStateData, read only through _modStateLayout) ----
        private sealed class ModStateData
        {
            public bool Lockdown;
            public readonly List<ModTempBan> TempBans = new List<ModTempBan>();
            public readonly List<ModMute> Mutes = new List<ModMute>();
            public readonly List<string> Watchlist = new List<string>();
            public readonly List<ModFrozen> Frozen = new List<ModFrozen>();
            public readonly List<ModWarning> Warnings = new List<ModWarning>();
            public float ReceivedAt;   // Time.time when the reply landed, for the "updated Ns ago" line
        }

        private struct ModTempBan { public string Id; public long Expiry; public string Reason; }
        private struct ModMute { public string Id; public long Expiry; }
        private struct ModFrozen { public long Uid; public string Name; }
        private struct ModWarning { public string Id; public int Count; public string Reason; }

        private ModStateData _modState;         // live - RPC handler writes this
        private ModStateData _modStateLayout;   // per-frame snapshot - the ONLY thing draw code reads
        private bool _modConnectedLayout;
        private bool _modIsHostLayout;
        // Pinned on Layout from the roster DrawWindow already built this frame. Text-only: it never changes how
        // many controls this section emits, it only picks which sentence a label carries.
        private bool _modSoloLayout;

        private float _modNextStateReq;         // request throttle (Time.time based)
        private Vector2 _modScroll;

        // Action-row inputs. Separate fields per card so a half-typed temp ban can't leak into a mute.
        private string _modBanId = "";
        private string _modBanMinutes = "60";
        private string _modBanReason = "";
        private string _modMuteId = "";
        private string _modMuteMinutes = "15";
        private string _modWarnId = "";
        private string _modWarnReason = "";
        private string _modWatchId = "";
        private long _modFreezeTargetId;        // stable peer id, 0 = nobody (never a roster index)

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies ModRpcRegistration.
        internal void ModInit()
        {
            if (_modInited) return;
            _modInited = true;
            _modSectionCfg = Config.Bind("Features", "ShowModerationSection", true,
                "Show the Moderation section in the Extras tab (temp bans, mutes, watchlist, freeze, warnings, lockdown). Client-side UI only - it changes nothing on its own.");
            _modPollSecondsCfg = Config.Bind("Features", "ModStatePollSeconds", 15,
                new ConfigDescription("How often the panel asks the server companion for moderation state, in seconds.",
                    new AcceptableValueRange<int>(5, 120)));
        }

        internal bool ModSectionEnabled() => _modSectionCfg == null || _modSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared here or server A's temp-ban list renders
        // with live Unban buttons on server B (the hazard called out for the 2.4.0 server-truth block).
        internal void ModReset()
        {
            _modState = null;
            _modStateLayout = null;
            _modConnectedLayout = false;
            _modIsHostLayout = false;
            _modSoloLayout = false;
            _modNextStateReq = 0f;
            _modScroll = Vector2.zero;
            _modBanId = "";
            _modBanMinutes = "60";
            _modBanReason = "";
            _modMuteId = "";
            _modMuteMinutes = "15";
            _modWarnId = "";
            _modWarnReason = "";
            _modWatchId = "";
            _modFreezeTargetId = 0L;
        }

        // ==================== reply plumbing ====================

        // Own registration class so this file needs no edit to RpcRegistration in the main file.
        [HarmonyPatch]
        internal static class ModRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Both halves side by side: the network handler (gate + parse) and the parse half the
                // in-process bridge calls on a listen-server host, where the companion runs in this very
                // process and there is no server peer for the gate to authenticate against.
                ZRoutedRpc.instance.Register<ZPackage>("AP_ModState", OnModStateData);
                AdminPanelLocalBridge.Register("AP_ModState", ParseModState);
            }
        }

        // AP_ModState wire format (v1):
        //   int ver=1, bool lockdown,
        //   int tb(<=50)  x (string id, long expiryTicksUtc, string reason)
        //   int mu(<=50)  x (string id, long expiryTicksUtc)
        //   int wa(<=50)  x (string id)
        //   int fr(<=50)  x (long uid, string name)
        //   int wn(<=50)  x (string id, int count, string lastReason)
        // Server-authoritative payload, so it must actually come from the server: without the gate a hostile
        // client could InvokeRoutedRPC(adminUid, "AP_ModState", forged) and paint fake bans/warnings into an
        // admin's panel. Unknown version or any out-of-range count discards the WHOLE reply (keep what we had).
        private static void OnModStateData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseModState(self, pkg);
        }

        // Parse half, shared verbatim with the in-process bridge so the local and network paths can never
        // interpret the same payload differently.
        internal static void ParseModState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var st = new ModStateData { Lockdown = pkg.ReadBool(), ReceivedAt = Time.time };

                var n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                    st.TempBans.Add(new ModTempBan { Id = pkg.ReadString(), Expiry = pkg.ReadLong(), Reason = pkg.ReadString() });

                n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                    st.Mutes.Add(new ModMute { Id = pkg.ReadString(), Expiry = pkg.ReadLong() });

                n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                    st.Watchlist.Add(pkg.ReadString());

                n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                    st.Frozen.Add(new ModFrozen { Uid = pkg.ReadLong(), Name = pkg.ReadString() });

                n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                    st.Warnings.Add(new ModWarning { Id = pkg.ReadString(), Count = pkg.ReadInt(), Reason = pkg.ReadString() });

                self._modState = st;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // Periodic poll. Copies RequestServerTruth exactly: Layout pass first (it both sends an RPC and
        // mutates the throttle, and must do so once per frame not once per pass), reachable-companion second,
        // throttle-FIRST third so a companion with no AP_SrvModStateReq handler can never cause a loop.
        //
        // "Reachable" covers BOTH shapes: a remote server peer (ServerUid() != 0) and a listen-server host,
        // where the companion runs in this very process and answers the request locally. The old guard
        // skipped hosts outright, which is why every table above stayed empty in single-player.
        private void ModRequestState()
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            if (ZNet.instance == null) return;                                        // not in a world at all
            if (ServerUid() == 0L && !ZNet.instance.IsServer()) return;               // connecting, no peer yet
            if (Time.time < _modNextStateReq) return;
            var every = _modPollSecondsCfg != null ? Mathf.Clamp(_modPollSecondsCfg.Value, 5, 120) : 15;
            _modNextStateReq = Time.time + every;
            SrvRpc("AP_SrvModStateReq");
        }

        // ==================== drawing ====================

        internal void DrawModerationSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _modStateLayout = _modState;
                _modConnectedLayout = ZNet.instance != null;
                _modIsHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                // Read the roster DrawWindow pinned this frame - never rebuild it here.
                _modSoloLayout = _othersSnapshot == null || _othersSnapshot.Count == 0;
            }

            if (!_modConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            ModRequestState();

            _modScroll = GUILayout.BeginScrollView(_modScroll, GUILayout.Height(ListView(150f)));

            // Said once, at the top, so the individual cards below do not each have to explain themselves.
            // One label either way - only the KEY swaps, so the control count is identical on both passes.
            GUILayout.Label(Loc.T(_modSoloLayout ? "mod.solo_hint" : "mod.multi_hint"), _hintStyle);

            ModDrawLockdownCard();
            ModDrawStatusCard();
            ModDrawTempBansCard();
            ModDrawMutesCard();
            ModDrawWatchlistCard();
            ModDrawFrozenCard();
            ModDrawWarningsCard();
            ModDrawTempBanAction();
            ModDrawMuteAction();
            ModDrawWarnAction();
            ModDrawWatchAction();
            ModDrawFreezeAction();

            GUILayout.EndScrollView();
        }

        private void ModDrawLockdownCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.lockdown_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.lockdown_status"), _labelStyle, GUILayout.MinWidth(80));
            GUILayout.Label(st == null ? Loc.T("mod.lockdown_unknown")
                                       : Loc.T(st.Lockdown ? "mod.lockdown_on" : "mod.lockdown_off"), _headerStyle);
            GUILayout.FlexibleSpace();
            // One control either way - the label swaps, the button count does not.
            var turnOn = st == null || !st.Lockdown;
            if (ConfirmButton("mod:lockdown", Loc.T(turnOn ? "mod.lockdown_enable" : "mod.lockdown_disable"), GUILayout.MinWidth(130)))
            {
                SrvRpc("AP_SrvLockdown", turnOn);
                Message(Loc.T(turnOn ? "mod.msg_lockdown_on" : "mod.msg_lockdown_off"));
                _modNextStateReq = 0f;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("mod.lockdown_hint"), _hintStyle);
            EndCard();
        }

        private void ModDrawStatusCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.state_section"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("mod.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _modNextStateReq = 0f;   // force the next Layout pass to send; never send inline from the event pass
            GUILayout.Space(8);
            GUILayout.Label(st == null
                    ? Loc.T("mod.no_data")
                    : Loc.T("mod.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - st.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // One label either way - only the KEY swaps, so Layout and Repaint always see the same control
            // count. Both strings take the same {0} (poll seconds), so the argument list is shared.
            GUILayout.Label(Loc.T(_modIsHostLayout ? "mod.host_local_hint" : "mod.state_hint",
                    _modPollSecondsCfg != null ? _modPollSecondsCfg.Value : 15),
                _hintStyle);
            EndCard();
        }

        private void ModDrawTempBansCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.tb_section"));
            // Quiet per-card wording: the one real "the companion has not answered" diagnostic lives in the
            // state card above, so five cards do not repeat a failure line while the first reply is in flight.
            if (st == null) GUILayout.Label(Loc.T("mod.card_waiting"), _hintStyle);
            else if (st.TempBans.Count == 0) GUILayout.Label(Loc.T("mod.tb_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < st.TempBans.Count; i++)
                {
                    var e = st.TempBans[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(e.Id, _cellStyle, GUILayout.Width(190));
                    GUILayout.Label(ModRemaining(e.Expiry), _cellStyle, GUILayout.Width(70));
                    GUILayout.Label(string.IsNullOrEmpty(e.Reason) ? Loc.T("mod.reason_none") : e.Reason,
                        _dimCellStyle, GUILayout.Width(200));
                    GUILayout.FlexibleSpace();
                    // Per-row id so arming Unban on one row cannot arm it on another.
                    if (GUILayout.Button(Loc.T("mod.unban"), _buttonStyle, GUILayout.MinWidth(70)))
                    {
                        SrvRpc("AP_SrvUnTempBan", e.Id);
                        Message(Loc.T("mod.msg_untempban", e.Id));
                        _modNextStateReq = 0f;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        private void ModDrawMutesCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.mute_section"));
            // Quiet per-card wording: the one real "the companion has not answered" diagnostic lives in the
            // state card above, so five cards do not repeat a failure line while the first reply is in flight.
            if (st == null) GUILayout.Label(Loc.T("mod.card_waiting"), _hintStyle);
            else if (st.Mutes.Count == 0) GUILayout.Label(Loc.T("mod.mute_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < st.Mutes.Count; i++)
                {
                    var e = st.Mutes[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(e.Id, _cellStyle, GUILayout.Width(190));
                    GUILayout.Label(ModRemaining(e.Expiry), _cellStyle, GUILayout.Width(70));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("mod.unmute"), _buttonStyle, GUILayout.MinWidth(80)))
                    {
                        SrvRpc("AP_SrvUnmute", e.Id);
                        Message(Loc.T("mod.msg_unmute", e.Id));
                        _modNextStateReq = 0f;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        private void ModDrawWatchlistCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.watch_section"));
            // Quiet per-card wording: the one real "the companion has not answered" diagnostic lives in the
            // state card above, so five cards do not repeat a failure line while the first reply is in flight.
            if (st == null) GUILayout.Label(Loc.T("mod.card_waiting"), _hintStyle);
            else if (st.Watchlist.Count == 0) GUILayout.Label(Loc.T("mod.watch_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < st.Watchlist.Count; i++)
                {
                    var id = st.Watchlist[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(id, _cellStyle, GUILayout.Width(260));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("mod.remove"), _buttonStyle, GUILayout.MinWidth(80)))
                    {
                        ModSendWatch(id, false);
                        _modNextStateReq = 0f;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        private void ModDrawFrozenCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.frozen_section"));
            // Quiet per-card wording: the one real "the companion has not answered" diagnostic lives in the
            // state card above, so five cards do not repeat a failure line while the first reply is in flight.
            if (st == null) GUILayout.Label(Loc.T("mod.card_waiting"), _hintStyle);
            // Freezing only ever applies to another online player, so solo this is state-by-design, not a fault.
            else if (st.Frozen.Count == 0)
                GUILayout.Label(Loc.T(_modSoloLayout ? "mod.frozen_empty_solo" : "mod.frozen_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < st.Frozen.Count; i++)
                {
                    var e = st.Frozen[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(e.Name) ? e.Uid.ToString() : e.Name, _cellStyle, GUILayout.Width(260));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("mod.unfreeze"), _buttonStyle, GUILayout.MinWidth(90)))
                    {
                        ModSendFreeze(e.Uid, false, string.IsNullOrEmpty(e.Name) ? e.Uid.ToString() : e.Name);
                        _modNextStateReq = 0f;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        private void ModDrawWarningsCard()
        {
            var st = _modStateLayout;
            BeginCard(Loc.T("mod.warn_section"));
            // Quiet per-card wording: the one real "the companion has not answered" diagnostic lives in the
            // state card above, so five cards do not repeat a failure line while the first reply is in flight.
            if (st == null) GUILayout.Label(Loc.T("mod.card_waiting"), _hintStyle);
            else if (st.Warnings.Count == 0) GUILayout.Label(Loc.T("mod.warn_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < st.Warnings.Count; i++)
                {
                    var e = st.Warnings[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(e.Id, _cellStyle, GUILayout.Width(190));
                    GUILayout.Label(Loc.T("mod.warn_count", e.Count), _cellStyle, GUILayout.Width(90));
                    GUILayout.Label(string.IsNullOrEmpty(e.Reason) ? Loc.T("mod.reason_none") : e.Reason,
                        _dimCellStyle, GUILayout.Width(240));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        // ---- action rows ----

        private void ModDrawTempBanAction()
        {
            BeginCard(Loc.T("mod.tempban_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _modBanId = GUILayout.TextField(_modBanId ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("mod.minutes_label"), _labelStyle, GUILayout.MinWidth(60));
            _modBanMinutes = GUILayout.TextField(_modBanMinutes ?? "", _textFieldStyle, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.reason_label"), _labelStyle, GUILayout.MinWidth(70));
            _modBanReason = GUILayout.TextField(_modBanReason ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (ConfirmButton("mod:tempban", Loc.T("mod.tempban_apply"), GUILayout.MinWidth(110)))
            {
                var id = ModTrim(_modBanId);
                if (id == null) Message(Loc.T("mod.msg_need_id"));
                else
                {
                    var mins = ModMinutes(_modBanMinutes);
                    if (mins <= 0) Message(Loc.T("mod.msg_need_minutes"));
                    else
                    {
                        var pkg = new ZPackage();
                        pkg.Write(id);
                        pkg.Write(mins);
                        pkg.Write(ModTrim(_modBanReason) ?? "");
                        SrvRpc("AP_SrvTempBan", pkg);
                        Message(Loc.T("mod.msg_tempban", id, mins));
                        _modNextStateReq = 0f;
                    }
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("mod.id_hint"), _hintStyle);
            EndCard();
        }

        private void ModDrawMuteAction()
        {
            BeginCard(Loc.T("mod.mute_action_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _modMuteId = GUILayout.TextField(_modMuteId ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("mod.minutes_label"), _labelStyle, GUILayout.MinWidth(60));
            _modMuteMinutes = GUILayout.TextField(_modMuteMinutes ?? "", _textFieldStyle, GUILayout.Width(60));
            if (ConfirmButton("mod:mute", Loc.T("mod.mute_apply"), GUILayout.MinWidth(90)))
            {
                var id = ModTrim(_modMuteId);
                if (id == null) Message(Loc.T("mod.msg_need_id"));
                else
                {
                    var mins = ModMinutes(_modMuteMinutes);
                    if (mins <= 0) Message(Loc.T("mod.msg_need_minutes"));
                    else
                    {
                        var pkg = new ZPackage();
                        pkg.Write(id);
                        pkg.Write(mins);
                        SrvRpc("AP_SrvMute", pkg);
                        Message(Loc.T("mod.msg_mute", id, mins));
                        _modNextStateReq = 0f;
                    }
                }
            }
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void ModDrawWarnAction()
        {
            BeginCard(Loc.T("mod.warn_action_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _modWarnId = GUILayout.TextField(_modWarnId ?? "", _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.Label(Loc.T("mod.reason_label"), _labelStyle, GUILayout.MinWidth(70));
            _modWarnReason = GUILayout.TextField(_modWarnReason ?? "", _textFieldStyle, GUILayout.MinWidth(150));
            // A warning is a permanent record entry, so it takes the two-click treatment too.
            if (ConfirmButton("mod:warn", Loc.T("mod.warn_apply"), GUILayout.MinWidth(90)))
            {
                var id = ModTrim(_modWarnId);
                if (id == null) Message(Loc.T("mod.msg_need_id"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(id);
                    pkg.Write(ModTrim(_modWarnReason) ?? "");
                    SrvRpc("AP_SrvWarn", pkg);
                    Message(Loc.T("mod.msg_warn", id));
                    _modNextStateReq = 0f;
                }
            }
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void ModDrawWatchAction()
        {
            BeginCard(Loc.T("mod.watch_action_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _modWatchId = GUILayout.TextField(_modWatchId ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.FlexibleSpace();
            // Watching is report-only (it just flags the id for extra logging), so no confirm step.
            if (GUILayout.Button(Loc.T("mod.watch_apply"), _buttonStyle, GUILayout.MinWidth(130)))
            {
                var id = ModTrim(_modWatchId);
                if (id == null) Message(Loc.T("mod.msg_need_id"));
                else { ModSendWatch(id, true); _modNextStateReq = 0f; }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("mod.watch_hint"), _hintStyle);
            EndCard();
        }

        // Freeze is uid-based, not platform-id based: ZNet.PlayerInfo exposes no platform id client-side, so
        // the roster picker cycles by NAME and sends the peer uid. The id-based actions above cannot use it.
        private void ModDrawFreezeAction()
        {
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            BeginCard(Loc.T("mod.freeze_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mod.target"), _labelStyle, GUILayout.MinWidth(70));
            if (GUILayout.Button(ModFreezeTargetName(others), _buttonStyle, GUILayout.MinWidth(150)))
                ModCycleFreezeTarget(others);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("mod.freeze"), _buttonStyle, GUILayout.MinWidth(90)))
                ModFreezeSelected(true);
            if (GUILayout.Button(Loc.T("mod.unfreeze"), _buttonStyle, GUILayout.MinWidth(90)))
                ModFreezeSelected(false);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T(_modSoloLayout ? "mod.freeze_hint_solo" : "mod.freeze_hint"), _hintStyle);
            EndCard();
        }

        // ==================== send helpers ====================

        private void ModSendWatch(string id, bool on)
        {
            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(on);
            SrvRpc("AP_SrvWatch", pkg);
            Message(Loc.T(on ? "mod.msg_watch_on" : "mod.msg_watch_off", id));
        }

        private void ModSendFreeze(long uid, bool on, string label)
        {
            var pkg = new ZPackage();
            pkg.Write(uid);
            pkg.Write(on);
            SrvRpc("AP_SrvFreeze", pkg);
            Message(Loc.T(on ? "mod.msg_freeze" : "mod.msg_unfreeze", label));
        }

        private void ModFreezeSelected(bool on)
        {
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            if (_modFreezeTargetId == 0L) { Message(Loc.T("mod.msg_no_target")); return; }
            ModSendFreeze(_modFreezeTargetId, on, ModFreezeTargetName(others));
            _modNextStateReq = 0f;
        }

        // ==================== small helpers ====================

        private string ModFreezeTargetName(List<ZNet.PlayerInfo> others)
        {
            if (_modFreezeTargetId == 0L) return Loc.T("common.nobody");
            if (others != null)
                foreach (var p in others)
                    if (PeerIdOf(p) == _modFreezeTargetId) return p.m_name;
            return Loc.T("common.nobody");   // target left the game; the send helper still rejects on id 0
        }

        private void ModCycleFreezeTarget(List<ZNet.PlayerInfo> others)
        {
            if (others == null || others.Count == 0) { _modFreezeTargetId = 0L; return; }
            var idx = -1;
            for (var i = 0; i < others.Count; i++)
                if (PeerIdOf(others[i]) == _modFreezeTargetId) { idx = i; break; }
            idx = (idx + 1) % others.Count;   // -1 (nobody) advances to the first entry
            _modFreezeTargetId = PeerIdOf(others[idx]);
        }

        private static string ModTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static int ModMinutes(string s)
        {
            if (!int.TryParse(ModTrim(s) ?? "", out var m)) return 0;
            return m <= 0 ? 0 : Mathf.Min(m, 525600);   // one year is plenty; keeps a fat-fingered value sane
        }

        // Remaining time from a server-stamped DateTime.UtcNow.Ticks expiry. Client and server clocks can
        // drift a little, so this is a display approximation - the server is what actually expires the entry.
        private static string ModRemaining(long expiryTicksUtc)
        {
            var delta = expiryTicksUtc - DateTime.UtcNow.Ticks;
            if (delta <= 0L) return Loc.T("mod.expired");
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T("mod.mins", Math.Max(1L, mins));
            if (mins < 1440L) return Loc.T("mod.hours", mins / 60L);
            return Loc.T("mod.days", mins / 1440L);
        }
    }
}

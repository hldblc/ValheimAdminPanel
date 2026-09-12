using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 7 — Guard status UI (client) ====================
    // Read-out for the server-side guard: the anti-cheat flag list and the client-mod report. Everything
    // shown here is the companion's truth, shipped in one AP_GuardState reply and parsed defensively; the
    // three row actions (clear flags / kick / allowlist) go back as AP_SrvGuardAction.
    //
    // Member prefix: "Grd". Locale prefix: "grd.".
    //
    // WHAT THIS SCREEN IS NOT: proof. Detection can only ever see what a client chooses to sync to the
    // server, and "Unknown" means the player is not running the companion mod - it is NOT a clean bill of
    // health. Both statements are on screen (grd.detect_hint / grd.unknown_hint), deliberately, so nobody
    // bans on the strength of a row in this table. Enforcement is server-side configuration; this panel
    // never decides it.
    //
    // IMGUI law: OnGuardState writes _grdState from ZNet.Update at any time; the draw path reads ONLY
    // _grdStateLayout, pinned on the Layout pass, so every row count is frozen for the frame.
    //
    // Dry-run coupling: every action here goes through SdkGuardDestructive (Wave7_Sdk.cs). That is the
    // opt-in half of the global dry-run contract - this file is the reference example of a feature that
    // honors it. If Wave7_Sdk.cs is dropped from the build, this file must drop the guard call with it.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _grdSectionCfg;
        private ConfigEntry<int> _grdPollSecondsCfg;
        private bool _grdInited;

        private const int GrdCap = 40;   // per-list cap in the wire contract; anything else is malformed

        // ---- server truth ----
        private sealed class GrdStateData
        {
            public bool AcOn;
            public bool EnforceOn;
            public readonly List<GrdFlag> Flags = new List<GrdFlag>();
            public readonly List<GrdMod> Mods = new List<GrdMod>();
            public float ReceivedAt;
        }

        private struct GrdFlag
        {
            public string PlayerName;
            public string Id;
            public string Rule;
            public int Hits;
            public long LastTicksUtc;
        }

        private struct GrdMod
        {
            public string PlayerName;
            public string Id;
            public string Status;
            public string Detail;
        }

        private GrdStateData _grdState;         // live - RPC handler writes this
        private GrdStateData _grdStateLayout;   // per-frame snapshot - the ONLY thing draw code reads
        private bool _grdConnectedLayout;
        private bool _grdIsHostLayout;
        private float _grdNextStateReq;
        private Vector2 _grdScroll;

        // ==================== lifecycle ====================

        internal void GrdInit()
        {
            if (_grdInited) return;
            _grdInited = true;
            _grdSectionCfg = Config.Bind("Features", "ShowGuardSection", true,
                "Show the Guard section in the Extras tab (anti-cheat flags and client-mod status reported by the server companion). Client-side UI only - it changes nothing on its own.");
            _grdPollSecondsCfg = Config.Bind("Features", "GuardStatePollSeconds", 20,
                new ConfigDescription("How often the panel asks the server companion for guard state, in seconds.",
                    new AcceptableValueRange<int>(5, 120)));
        }

        internal bool GrdSectionEnabled() => _grdSectionCfg == null || _grdSectionCfg.Value;

        // Per-world: flags and mod reports belong to the server just left.
        internal void GrdReset()
        {
            _grdState = null;
            _grdStateLayout = null;
            _grdConnectedLayout = false;
            _grdIsHostLayout = false;
            _grdNextStateReq = 0f;
            _grdScroll = Vector2.zero;
        }

        // ==================== reply plumbing ====================

        // Own registration class so this file needs no edit to RpcRegistration in the main file.
        [HarmonyPatch]
        internal static class GrdRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_GuardState", OnGuardState);
                AdminPanelLocalBridge.Register("AP_GuardState", ParseGuardState);
            }
        }

        // AP_GuardState wire format (v1):
        //   int ver=1, bool acOn, bool enforceOn,
        //   int flags(<=40) x (string playerName, string id, string rule, int hits, long lastTicksUtc)
        //   int mods(<=40)  x (string playerName, string id, string status, string detail)
        // Server-authoritative, so it must actually come from the server: without the gate a hostile client
        // could paint fake "forbidden mod" rows into an admin's panel and get someone banned. Unknown
        // version or an out-of-range count discards the WHOLE reply and keeps what we had.
        private static void OnGuardState(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseGuardState(self, pkg);
        }

        internal static void ParseGuardState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var st = new GrdStateData
                {
                    AcOn = pkg.ReadBool(),
                    EnforceOn = pkg.ReadBool(),
                    ReceivedAt = Time.time,
                };

                var n = pkg.ReadInt();
                if (n < 0 || n > GrdCap) return;
                for (var i = 0; i < n; i++)
                    st.Flags.Add(new GrdFlag
                    {
                        PlayerName = pkg.ReadString(),
                        Id = pkg.ReadString(),
                        Rule = pkg.ReadString(),
                        Hits = pkg.ReadInt(),
                        LastTicksUtc = pkg.ReadLong(),
                    });

                n = pkg.ReadInt();
                if (n < 0 || n > GrdCap) return;
                for (var i = 0; i < n; i++)
                    st.Mods.Add(new GrdMod
                    {
                        PlayerName = pkg.ReadString(),
                        Id = pkg.ReadString(),
                        Status = pkg.ReadString(),
                        Detail = pkg.ReadString(),
                    });

                self._grdState = st;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // Layout-gated + throttle-FIRST, so a companion with no AP_SrvGuardStateReq handler can never turn
        // this into a request loop. A host polls too: the companion runs in this process, so the request is
        // delivered locally and its reply carries our own session id (accepted by SenderIsServerReply for
        // the host case). The throttle is unchanged.
        private void GrdRequestState()
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            if (ZNet.instance == null) return;
            if (!_grdIsHostLayout && ServerUid() == 0L) return;
            if (Time.time < _grdNextStateReq) return;
            var every = _grdPollSecondsCfg != null ? Mathf.Clamp(_grdPollSecondsCfg.Value, 5, 120) : 20;
            _grdNextStateReq = Time.time + every;
            SrvRpc("AP_SrvGuardStateReq");
        }

        // ==================== drawing ====================

        internal void DrawGuardSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _grdStateLayout = _grdState;
                _grdConnectedLayout = ZNet.instance != null;
                _grdIsHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
            }

            if (!_grdConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            GrdRequestState();

            _grdScroll = GUILayout.BeginScrollView(_grdScroll, GUILayout.Height(ListView(150f)));

            GrdDrawStatusCard();
            GrdDrawFlagsCard();
            GrdDrawModsCard();

            GUILayout.EndScrollView();
        }

        // Anti-cheat and client-mod checks watch OTHER players, so an empty table is the CORRECT result when
        // nobody else is connected - not a fault, and not a failure to reach the companion. _othersSnapshot
        // is the roster of other players, pinned once per frame in DrawWindow's Layout block: read it, never
        // rebuild it. Only label TEXT is chosen from it, never a control count. Null (not pinned yet) counts
        // as "not alone" so a populated server is never described as empty.
        private bool GrdSolo() => _othersSnapshot != null && _othersSnapshot.Count == 0;

        private void GrdDrawStatusCard()
        {
            var st = _grdStateLayout;
            BeginCard(Loc.T("grd.state_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("grd.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _grdNextStateReq = 0f;   // force the NEXT Layout pass to send; never send from the event pass
            GUILayout.Space(8);
            GUILayout.Label(st == null
                    ? Loc.T("grd.no_data")
                    : Loc.T("grd.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - st.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("grd.ac_label"), _labelStyle, GUILayout.MinWidth(110));
            GrdDrawOnOff(st != null && st.AcOn, st == null);
            GUILayout.Space(16);
            GUILayout.Label(Loc.T("grd.enforce_label"), _labelStyle, GUILayout.MinWidth(110));
            GrdDrawOnOff(st != null && st.EnforceOn, st == null);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // One label either way - only the TEXT swaps, and the branch reads the Layout snapshot - so the
            // control count is identical on both passes of a frame. Hosting alone gets its own wording:
            // there is nobody to watch, so an empty table below is expected rather than a missing reply.
            var grdEvery = _grdPollSecondsCfg != null ? _grdPollSecondsCfg.Value : 20;
            GUILayout.Label(_grdIsHostLayout
                    ? Loc.T(GrdSolo() ? "grd.host_solo_note" : "grd.host_note", grdEvery)
                    : Loc.T("grd.state_hint", grdEvery),
                _hintStyle);
            GUILayout.Label(Loc.T("grd.detect_hint"), _proseStyle);
            // Says what the two switches above default to and how far they reach. One more fixed label.
            GUILayout.Label(Loc.T("grd.default_hint"), _proseStyle);
            EndCard();
        }

        // One label either way: the text and the colour swap, the control count never does. GUI.contentColor
        // is restored on every path (an early return here would tint the rest of the panel).
        private void GrdDrawOnOff(bool on, bool unknown)
        {
            var prev = GUI.contentColor;
            if (!unknown) GUI.contentColor = on ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.72f, 0.68f, 0.60f);
            GUILayout.Label(Loc.T(unknown ? "grd.unknown_state" : on ? "grd.on" : "grd.off"), _headerStyle, GUILayout.MinWidth(60));
            GUI.contentColor = prev;
        }

        private void GrdDrawFlagsCard()
        {
            var st = _grdStateLayout;
            BeginCard(Loc.T("grd.flags_section"));
            if (st == null) GUILayout.Label(Loc.T("grd.no_data"), _hintStyle);
            // An empty flag list is a healthy answer, not a missing one. Alone it is also the only possible
            // answer, and the text says so instead of implying the server went quiet.
            else if (st.Flags.Count == 0)
                GUILayout.Label(Loc.T(GrdSolo() ? "grd.flags_empty_solo" : "grd.flags_empty"), _hintStyle);
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("grd.col_player"), _cellStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("grd.col_id"), _cellStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("grd.col_rule"), _cellStyle, GUILayout.Width(140));
                GUILayout.Label(Loc.T("grd.col_hits"), _cellStyle, GUILayout.Width(50));
                GUILayout.Label(Loc.T("grd.col_last"), _cellStyle, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                for (var i = 0; i < st.Flags.Count; i++)
                {
                    var e = st.Flags[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(e.PlayerName) ? Loc.T("grd.unknown_player") : e.PlayerName,
                        _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(e.Id ?? "", _dimCellStyle, GUILayout.Width(150));
                    GUILayout.Label(e.Rule ?? "", _cellStyle, GUILayout.Width(140));
                    GUILayout.Label(e.Hits.ToString(), _cellStyle, GUILayout.Width(50));
                    GUILayout.Label(GrdAgo(e.LastTicksUtc), _dimCellStyle, GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                    // Per-row confirm ids are suffixed with the stable platform id, never the row index.
                    if (GUILayout.Button(Loc.T("grd.clear"), _buttonStyle, GUILayout.MinWidth(70)))
                        GrdSendAction(e.Id, 0, Loc.T("grd.act_clear", GrdLabel(e.PlayerName, e.Id)));
                    if (ConfirmButton("grd:kick:" + (e.Id ?? "?"), Loc.T("grd.kick"), GUILayout.MinWidth(70)))
                        GrdSendAction(e.Id, 1, Loc.T("grd.act_kick", GrdLabel(e.PlayerName, e.Id)));
                    if (ConfirmButton("grd:allow:" + (e.Id ?? "?"), Loc.T("grd.allow"), GUILayout.MinWidth(90)))
                        GrdSendAction(e.Id, 2, Loc.T("grd.act_allow", GrdLabel(e.PlayerName, e.Id)));
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label(Loc.T("grd.flags_hint"), _hintStyle);
            }
            EndCard();
        }

        private void GrdDrawModsCard()
        {
            var st = _grdStateLayout;
            BeginCard(Loc.T("grd.mods_section"));
            if (st == null) GUILayout.Label(Loc.T("grd.no_data"), _hintStyle);
            else if (st.Mods.Count == 0)
                GUILayout.Label(Loc.T(GrdSolo() ? "grd.mods_empty_solo" : "grd.mods_empty"), _hintStyle);
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("grd.col_player"), _cellStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("grd.col_id"), _cellStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("grd.col_status"), _cellStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("grd.col_detail"), _cellStyle, GUILayout.Width(200));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                for (var i = 0; i < st.Mods.Count; i++)
                {
                    var e = st.Mods[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(e.PlayerName) ? Loc.T("grd.unknown_player") : e.PlayerName,
                        _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(e.Id ?? "", _dimCellStyle, GUILayout.Width(150));
                    var prev = GUI.contentColor;
                    GUI.contentColor = GrdStatusColor(e.Status, prev);
                    GUILayout.Label(GrdStatusText(e.Status), _cellStyle, GUILayout.Width(130));
                    GUI.contentColor = prev;
                    GUILayout.Label(string.IsNullOrEmpty(e.Detail) ? "-" : e.Detail, _dimCellStyle, GUILayout.Width(200));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label(Loc.T("grd.unknown_hint"), _proseStyle);
            EndCard();
        }

        // ==================== send helper ====================

        // action: 0 clear flags, 1 kick, 2 add to allowlist (AP_SrvGuardAction wire contract).
        // actionName is a short lower-case phrase ("kick Bjorn") that reads correctly both in the sent
        // message and inside the dry-run "was not sent" sentence.
        // Routed through the dry-run guard: with DryRunMode on, nothing is sent and the admin is told the
        // action was simulated. This is the opt-in the dry-run contract is built on.
        private void GrdSendAction(string id, int action, string actionName)
        {
            if (string.IsNullOrEmpty(id)) { Message(Loc.T("grd.msg_no_id")); return; }
            if (!SdkGuardDestructive(actionName)) return;
            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(action);
            SrvRpc("AP_SrvGuardAction", pkg);
            Message(Loc.T("grd.msg_sent", actionName));
            _grdNextStateReq = 0f;   // pull fresh truth on the next Layout pass
        }

        // ==================== small helpers ====================

        private static string GrdLabel(string name, string id) =>
            string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(id) ? "?" : id) : name;

        // The server ships a short status token. Known tokens render as plain words; anything else is
        // passed through verbatim so a companion that grows a new status is readable instead of blank.
        private static string GrdStatusText(string status)
        {
            switch (GrdStatusKind(status))
            {
                case 1: return Loc.T("grd.status_ok");
                case 2: return Loc.T("grd.status_missing");
                case 3: return Loc.T("grd.status_forbidden");
                case 0: return Loc.T("grd.status_unknown");
                default: return status;
            }
        }

        private static Color GrdStatusColor(string status, Color fallback)
        {
            switch (GrdStatusKind(status))
            {
                case 1: return new Color(0.55f, 0.85f, 0.55f);
                case 2: return new Color(0.95f, 0.72f, 0.35f);
                case 3: return new Color(0.95f, 0.45f, 0.40f);
                case 0: return new Color(0.72f, 0.68f, 0.60f);
                default: return fallback;
            }
        }

        // 0 unknown, 1 ok, 2 missing required, 3 forbidden, -1 unrecognized token.
        private static int GrdStatusKind(string status)
        {
            if (string.IsNullOrEmpty(status)) return 0;
            var s = status.Trim().ToLowerInvariant();
            if (s == "ok" || s == "clean" || s == "match") return 1;
            // "no-mod" is the companion's first-class "this client never answered" status. It is shown as
            // Unknown on purpose: it is NOT a pass, and the prose below the table says so.
            if (s == "unknown" || s == "?" || s == "none" || s == "no-mod" || s == "nomod") return 0;
            if (s == "missing" || s == "missing_required" || s == "missingrequired" || s == "required") return 2;
            if (s == "forbidden" || s == "banned" || s == "blocked" || s == "denylist") return 3;
            return -1;
        }

        // Server-stamped DateTime.UtcNow.Ticks. Clocks drift a little between machines, so this is a
        // display approximation - the server's own record is what counts.
        private static string GrdAgo(long ticksUtc)
        {
            if (ticksUtc <= 0L) return Loc.T("grd.never");
            var delta = DateTime.UtcNow.Ticks - ticksUtc;
            if (delta < TimeSpan.TicksPerMinute) return Loc.T("grd.now");
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T("grd.ago_min", mins);
            if (mins < 1440L) return Loc.T("grd.ago_hour", mins / 60L);
            return Loc.T("grd.ago_day", mins / 1440L);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 1 — Audit trail + chat history viewer (client side) ====================
    // Read-only window onto two server-side append-only logs (audit.log, chat.log). The panel never keeps
    // its own copy of either: it asks the companion for the newest N lines, renders them, and forgets them
    // on logout. Everything here is passive — no button in this section changes server state.
    //
    // IMGUI discipline (see Features\FeaturesCore.cs and the Player-tab chip pattern): the RPC replies land
    // during ZNet.Update and the filter text changes during the event pass, so the row list that the draw
    // code walks is rebuilt ONLY on the Layout pass and every gate below reads a *Layout snapshot.
    public partial class AdminPanelPlugin
    {
        // ---- Wave 1 reply registration (one patch class for the whole wave's client replies) ----
        // Registered on ZNet.Awake, i.e. once per world join, exactly like the main file's RpcRegistration.
        // The glue file applies it with Harmony.CreateAndPatchAll(typeof(Wave1RpcRegistration)).
        [HarmonyPatch]
        private static class Wave1RpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Each reply registers BOTH halves side by side: the network handler (gate + parse) and the
                // parse half the in-process bridge calls on a listen-server host, where the companion lives
                // in this process and there is no server peer for the gate to authenticate against.
                ZRoutedRpc.instance.Register<ZPackage>("AP_AuditData", AudOnAuditData);
                AdminPanelLocalBridge.Register("AP_AuditData", ParseAuditData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ChatLog", AudOnChatLog);
                AdminPanelLocalBridge.Register("AP_ChatLog", ParseChatLog);
                ZRoutedRpc.instance.Register<ZPackage>("AP_RolesData", RoleOnRolesData);
                AdminPanelLocalBridge.Register("AP_RolesData", ParseRolesData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_RapSheet", RapOnRapSheet);
                AdminPanelLocalBridge.Register("AP_RapSheet", ParseRapSheet);
            }
        }

        // ---- config ----
        private ConfigEntry<bool> _audSectionCfg;

        // ---- live payloads (written by the RPC handlers, any time) ----
        private List<string> _audLines;        // audit.log tail, newest LAST
        private List<string> _audChatLines;    // chat.log tail, newest LAST
        private int _audTotal, _audChatTotal;  // server-side line totals (>= shipped count)

        // ---- UI state ----
        private int _audView;                  // 0 = admin actions, 1 = chat history
        private string _audFilter = "";
        private Vector2 _audScroll;
        private float _audNextReq, _audNextChatReq;

        // ---- Layout snapshots (the ONLY things the draw code is allowed to read) ----
        private int _audViewLayout;
        private bool _audHostLayout;
        private List<AudRow> _audRowsLayout;   // null = no reply yet
        private int _audLoadedLayout, _audTotalLayout;
        // Was a filter actually applied when the rows above were built? Pinned here because _audFilter is edited
        // during the event pass; it only picks which empty-state sentence a label carries, never a control count.
        private bool _audFilteredLayout;

        // One parsed log line. Built on the Layout pass only.
        private sealed class AudRow
        {
            public string Time;
            public string Name;
            public string Action;
            public string Detail;
        }

        // ---- lifecycle ----

        internal void AudInit()
        {
            _audSectionCfg = Config.Bind("Features", "ShowAuditSection", true,
                "Show the Audit / Chat history viewer in the Extras tab. Passive: it only reads the server's audit.log and chat.log, it never changes anything.");
        }

        internal bool AudSectionEnabled() => _audSectionCfg == null || _audSectionCfg.Value;

        // Called on logout. Every field above must be cleared here or server A's audit lines would still be
        // on screen after joining server B (the known cross-world leak hazard).
        internal void AudReset()
        {
            _audLines = null;
            _audChatLines = null;
            _audTotal = 0;
            _audChatTotal = 0;
            _audView = 0;
            _audFilter = "";
            _audScroll = Vector2.zero;
            _audNextReq = 0f;
            _audNextChatReq = 0f;
            _audViewLayout = 0;
            _audHostLayout = false;
            _audRowsLayout = null;
            _audLoadedLayout = 0;
            _audTotalLayout = 0;
            _audFilteredLayout = false;
        }

        // ---- replies ----

        private static void AudOnAuditData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAuditData(self, pkg);
        }

        internal static void ParseAuditData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;          // payload version gate
                var total = pkg.ReadInt();
                var shipped = pkg.ReadInt();
                if (shipped < 0 || shipped > 100) return;
                var list = new List<string>(shipped);
                for (var i = 0; i < shipped; i++) list.Add(pkg.ReadString());
                self._audTotal = total;
                self._audLines = list;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        private static void AudOnChatLog(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseChatLog(self, pkg);
        }

        internal static void ParseChatLog(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var total = pkg.ReadInt();
                var shipped = pkg.ReadInt();
                if (shipped < 0 || shipped > 100) return;
                var list = new List<string>(shipped);
                for (var i = 0; i < shipped; i++) list.Add(pkg.ReadString());
                self._audChatTotal = total;
                self._audChatLines = list;
            }
            catch (Exception) { }
        }

        // ---- polling (throttle-first, Layout-gated; a missing server handler can never cause a loop) ----

        private void AudPoll()
        {
            // Two reachable shapes, both polled: a remote server peer (ServerUid() != 0) and a listen-server
            // host, where the companion runs in THIS process, handles the request locally and routes the reply
            // back to our own session id. Only "no ZNet at all" and "connecting, no peer yet" skip the send.
            // _audHostLayout is the Layout-pass snapshot and AudPoll only ever runs on that pass.
            if (ZNet.instance == null) return;
            if (ServerUid() == 0L && !_audHostLayout) return;
            var now = Time.time;
            if (_audViewLayout == 1)
            {
                if (now < _audNextChatReq) return;
                _audNextChatReq = now + 20f;              // set BEFORE sending
                SrvRpc("AP_SrvChatLogReq", 100);
            }
            else
            {
                if (now < _audNextReq) return;
                _audNextReq = now + 20f;
                SrvRpc("AP_SrvAuditReq", 100);
            }
        }

        // ---- parsing ----

        // Log lines are pipe-joined and their field count varies by writer (the chokepoint emits
        // time|uid|id|name|action|verdict, wave features may emit fewer). Rather than trusting one shape we
        // degrade column by column, and the trailing field always absorbs any extra pipes so a chat message
        // containing '|' is never truncated.
        private static AudRow AudParse(string line)
        {
            var r = new AudRow { Time = "", Name = "", Action = "", Detail = "" };
            if (string.IsNullOrEmpty(line)) return r;
            var p = line.Split('|');
            if (p.Length >= 6)
            {
                r.Time = AudShortTime(p[0]);
                r.Name = p[3];
                r.Action = p[4];
                r.Detail = string.Join("|", p, 5, p.Length - 5);
            }
            else if (p.Length == 5)
            {
                r.Time = AudShortTime(p[0]);
                r.Name = p[3];
                r.Detail = p[4];
            }
            else if (p.Length == 4)
            {
                r.Time = AudShortTime(p[0]);
                r.Name = p[1];
                r.Action = p[2];
                r.Detail = p[3];
            }
            else if (p.Length == 3)
            {
                r.Time = AudShortTime(p[0]);
                r.Name = p[1];
                r.Detail = p[2];
            }
            else if (p.Length == 2)
            {
                r.Time = AudShortTime(p[0]);
                r.Detail = p[1];
            }
            else
            {
                r.Detail = line;
            }
            return r;
        }

        // ISO-8601 UTC ("2026-07-31T02:28:11Z") -> local "07-31 02:28:11". Unparseable stamps fall back to
        // the raw prefix so a format change on the server degrades to ugly, never to blank.
        private static string AudShortTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            DateTime dt;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return iso.Length > 19 ? iso.Substring(0, 19) : iso;
        }

        // Rebuilt on the Layout pass only: the filter text changes during the event pass, and a live rebuild
        // would hand Repaint a different row count than Layout reserved controls for.
        private void AudRebuildRows()
        {
            var src = _audViewLayout == 1 ? _audChatLines : _audLines;
            _audTotalLayout = _audViewLayout == 1 ? _audChatTotal : _audTotal;
            if (src == null)
            {
                _audRowsLayout = null;
                _audLoadedLayout = 0;
                _audFilteredLayout = false;
                return;
            }
            _audLoadedLayout = src.Count;
            var f = (_audFilter ?? "").Trim();
            _audFilteredLayout = f.Length > 0;
            var rows = new List<AudRow>(src.Count);
            for (var i = 0; i < src.Count; i++)
            {
                var line = src[i];
                if (line == null) continue;
                if (f.Length > 0 && line.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(AudParse(line));
            }
            _audRowsLayout = rows;
        }

        // ---- draw ----

        internal void DrawAuditSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _audHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _audViewLayout = _audView;
                AudRebuildRows();
                AudPoll();
            }

            BeginCard(Loc.T("audit.section"));

            // Sub-view chips + refresh. The chips WRITE the live field; everything below gates on the
            // snapshot taken above, so a click cannot change this frame's control count.
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_audView == 0, Loc.T("audit.view_audit"), _chipStyleOrButton(), GUILayout.MinWidth(120))
                && _audView != 0) { _audView = 0; _audScroll = Vector2.zero; }
            if (GUILayout.Toggle(_audView == 1, Loc.T("audit.view_chat"), _chipStyleOrButton(), GUILayout.MinWidth(120))
                && _audView != 1) { _audView = 1; _audScroll = Vector2.zero; }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("audit.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                // Zero the throttles instead of sending inline: the next Layout pass does the send, which
                // keeps every request on the one code path.
                _audNextReq = 0f;
                _audNextChatReq = 0f;
            }
            GUILayout.EndHorizontal();

            // One label either way - only the KEY swaps (both take the same {0} config-name argument), so the
            // control count is identical on a host and on a remote client.
            GUILayout.Label(Loc.T(_audHostLayout ? "audit.host_local_hint" : "audit.section_hint",
                "Features.EnableAuditLog"), _hintStyle);

            // Filter row (client side; 100 rows makes debouncing pointless).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("audit.filter"), _labelStyle, GUILayout.MinWidth(60));
            _audFilter = GUILayout.TextField(_audFilter ?? "", _textFieldStyle, GUILayout.MinWidth(200));
            if (GUILayout.Button(Loc.T("audit.clear"), _buttonStyle, GUILayout.MinWidth(70))) _audFilter = "";
            GUILayout.FlexibleSpace();
            GUILayout.Label(Loc.T("audit.count", _audRowsLayout != null ? _audRowsLayout.Count : 0, _audLoadedLayout),
                _dimLabelStyle);
            GUILayout.EndHorizontal();

            // No host special-case here any more: the companion in this process answers the same request and
            // the reply lands in _audLines exactly like a remote one, so a host walks the normal
            // pending / empty / table ladder below.
            var rows = _audRowsLayout;
            if (rows == null)
            {
                GUILayout.Label(Loc.T("audit.pending"), _hintStyle);
            }
            else if (rows.Count == 0)
            {
                // Three genuinely different reasons for "no rows", and the old single line claimed the wrong one
                // whenever the log itself was empty. Still ONE label - only the key swaps, and all three take the
                // same single {0} (the companion config switch), so the argument list is shared.
                var emptyKey = _audFilteredLayout && _audLoadedLayout > 0 ? "audit.empty"
                             : _audViewLayout == 1 ? "audit.empty_chat"
                             : "audit.empty_audit";
                GUILayout.Label(Loc.T(emptyKey, "Features.EnableAuditLog"), _hintStyle);
            }
            else
            {
                var chat = _audViewLayout == 1;
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("audit.col_time"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("audit.col_who"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(chat ? Loc.T("audit.col_channel") : Loc.T("audit.col_action"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(chat ? Loc.T("audit.col_msg") : Loc.T("audit.col_result"), _headerStyle, GUILayout.MinWidth(120));
                GUILayout.EndHorizontal();

                _audScroll = GUILayout.BeginScrollView(_audScroll, GUILayout.Height(ListView(420f)));
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Time, _dimCellStyle, GUILayout.Width(130));
                    GUILayout.Label(r.Name, _cellStyle, GUILayout.Width(130));
                    GUILayout.Label(r.Action, _cellStyle, GUILayout.Width(150));
                    // A denied action is the one thing an admin scans for, so it gets the bright cell style.
                    GUILayout.Label(r.Detail,
                        r.Detail.StartsWith("DENIED", StringComparison.Ordinal) ? _cellStyle : _dimCellStyle,
                        GUILayout.MinWidth(120));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();

                if (_audTotalLayout > _audLoadedLayout)
                    GUILayout.Label(Loc.T("audit.truncated", _audTotalLayout - _audLoadedLayout), _dimLabelStyle);
            }

            EndCard();
        }
    }
}

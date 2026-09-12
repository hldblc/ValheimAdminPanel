using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #9 staff chat (client side) ====================
    // Staff-to-staff text: what the panel sends through AP_SrvStaffChat and what anyone typed as "!a"
    // in-game comes back as AP_StaffChat, only ever to admins. The panel never keeps its own copy beyond
    // the 100 lines the server pushes: the card asks for the history once when it opens (backfill), then
    // appends every live line. A line that lands while the panel is CLOSED is also shown as a top-left
    // toast (config StaffChatToast) so a staff call for help is not missed behind a closed window.
    //
    // IMGUI: the line list is REPLACED (never mutated) by the reply handler and pinned per frame, so the
    // row count can only change between frames, never between the Layout and Repaint passes of one.
    public partial class AdminPanelPlugin
    {
        private const int AchatCap = 100;                       // wire contract: n <= 100
        private const string AchatInputName = "apAchatInput";   // IMGUI control name for Enter-to-send

        private ConfigEntry<bool> _achatSectionCfg;
        private ConfigEntry<bool> _achatToastCfg;

        private sealed class AchatLine
        {
            public long Ticks;
            public string Id = "";
            public string Name = "";
            public string Text = "";
            public string When = "";   // local HH:mm, formatted once on arrival
        }

        // ---- live state (written by the RPC handler and by clicks, any time) ----
        private List<AchatLine> _achatLines;   // null = no reply yet
        private bool _achatPending;            // history request out, no answer yet
        private bool _achatHistoryAsked;       // one backfill per section open / refresh
        private string _achatInput = "";
        private Vector2 _achatScroll;
        private bool _achatScrollToEnd;        // set by the handler; consumed on the next Layout pass
        private float _achatNextReq, _achatNextSend;
        private float _achatAskedAt;           // when the backfill went out (no-answer detection)

        // ---- Layout snapshots (the ONLY things the draw code reads) ----
        private List<AchatLine> _achatLinesLayout;
        private bool _achatPendingLayout;
        private bool _achatNoAnswerLayout;     // asked > 10 s ago, nothing came back (older companion?)
        private bool _achatReachableLayout;

        private const float AchatNoAnswerSeconds = 10f;

        // ---- lifecycle ----

        internal void AchatInit()
        {
            _achatSectionCfg = Config.Bind("Features", "ShowStaffChatSection", true,
                "Show the Staff Chat section in the Tools tab (admin-only text channel relayed by the companion; also reachable in-game with '!a <text>').");
            _achatToastCfg = Config.Bind("Features", "StaffChatToast", true,
                "Show a staff chat line as a top-left toast when it arrives while the panel is closed.");
        }

        internal bool AchatSectionEnabled() => _achatSectionCfg == null || _achatSectionCfg.Value;

        // Called on logout: every line belongs to the server just left.
        internal void AchatReset()
        {
            _achatLines = null;
            _achatPending = false;
            _achatHistoryAsked = false;
            _achatInput = "";
            _achatScroll = Vector2.zero;
            _achatScrollToEnd = false;
            _achatNextReq = 0f;
            _achatNextSend = 0f;
            _achatAskedAt = 0f;
            _achatLinesLayout = null;
            _achatPendingLayout = false;
            _achatNoAnswerLayout = false;
            _achatReachableLayout = false;
        }

        // ---- reply ----

        private static void AchatOnStaffChat(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseStaffChat(self, pkg);
        }

        // Wire: int ver=1 | bool replace | int n (<=100) | n x { long ticks, string id, string name, string text }
        internal static void ParseStaffChat(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;              // payload version gate
                var replace = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > AchatCap) return;
                var incoming = new List<AchatLine>(n);
                for (var i = 0; i < n; i++)
                {
                    var l = new AchatLine
                    {
                        Ticks = pkg.ReadLong(),
                        Id = pkg.ReadString() ?? "",
                        Name = pkg.ReadString() ?? "",
                        Text = pkg.ReadString() ?? "",
                    };
                    l.When = TkClock(l.Ticks);
                    incoming.Add(l);
                }

                List<AchatLine> next;
                if (replace || self._achatLines == null) next = incoming;
                else
                {
                    next = new List<AchatLine>(self._achatLines.Count + incoming.Count);
                    next.AddRange(self._achatLines);
                    next.AddRange(incoming);
                    while (next.Count > AchatCap) next.RemoveAt(0);
                }
                self._achatLines = next;
                self._achatPending = false;
                self._achatScrollToEnd = true;

                // Closed panel: the line would sit unread in a window nobody is looking at. The toast is
                // the panel's own top-left message (same slot as Message()), so it needs no server help
                // and honours the client config even on a listen-server host.
                if (!replace && self._achatToastCfg != null && self._achatToastCfg.Value && !self._visible && LocalPlayer != null)
                    foreach (var l in incoming)
                        LocalPlayer.Message(MessageHud.MessageType.TopLeft, Loc.T("achat.toast", l.Name, l.Text));
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        // One-shot history backfill; runs on the Layout pass only (it sends and mutates throttles).
        private void AchatBackfill()
        {
            if (_achatHistoryAsked || !_achatReachableLayout) return;
            if (Time.time < _achatNextReq) return;      // throttle-first: a missing server handler cannot loop
            _achatNextReq = Time.time + 5f;
            _achatHistoryAsked = true;
            _achatPending = true;
            _achatAskedAt = Time.time;
            SrvRpc("AP_SrvStaffChatReq");
        }

        private void AchatSend()
        {
            var text = (_achatInput ?? "").Trim();
            if (text.Length == 0) { Message(Loc.T("achat.empty_input")); return; }
            if (!TkRequireReachable()) return;
            if (Time.time < _achatNextSend) return;
            _achatNextSend = Time.time + 0.5f;
            SrvRpc("AP_SrvStaffChat", text);
            _achatInput = "";
        }

        // Enter in the input field sends. Read at the top of the draw and consumed, so the key never
        // reaches the text field (single-line anyway) or the game (Wave5_Palette.PalHandleKeys shape).
        private void AchatHandleKeys()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            if (GUI.GetNameOfFocusedControl() != AchatInputName) return;
            AchatSend();
            e.Use();
        }

        // ---- draw ----

        internal void DrawStaffChatSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _achatReachableLayout = TkReachable();
                AchatBackfill();
                _achatLinesLayout = _achatLines;
                _achatPendingLayout = _achatPending;
                _achatNoAnswerLayout = _achatPending && Time.time - _achatAskedAt > AchatNoAnswerSeconds;
                if (_achatScrollToEnd)
                {
                    // Newest last: jump to the bottom when lines arrive. BeginScrollView clamps the value.
                    _achatScrollToEnd = false;
                    _achatScroll.y = 1e6f;
                }
            }

            AchatHandleKeys();

            BeginCard(Loc.T("achat.section"));
            GUILayout.Label(Loc.T("achat.hint"), _hintStyle);

            // Four states, one control each (label or scroll view); which one is emitted depends only on
            // the snapshots above, so both passes of a frame agree.
            var lines = _achatLinesLayout;
            if (!_achatReachableLayout)
            {
                GUILayout.Label(Loc.T("achat.not_connected"), _hintStyle);
            }
            else if (lines == null)
            {
                // Still one label: only the key swaps. "No answer" is a real state (a companion older than
                // this panel has no AP_SrvStaffChatReq handler) and must not masquerade as "loading".
                var idleKey = _achatNoAnswerLayout ? "achat.no_answer"
                            : _achatPendingLayout ? "achat.loading"
                            : "achat.idle";
                GUILayout.Label(Loc.T(idleKey), _hintStyle);
            }
            else if (lines.Count == 0)
            {
                GUILayout.Label(Loc.T("achat.empty"), _hintStyle);
            }
            else
            {
                _achatScroll = GUILayout.BeginScrollView(_achatScroll,
                    GUILayout.Height(Mathf.Min(ListView(360f), lines.Count * 26f + 16f)));
                for (var i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(l.When, _dimCellStyle, GUILayout.Width(50));
                    GUILayout.Label(l.Name, _cellStyle, GUILayout.Width(130));
                    GUILayout.Label(l.Text, _proseStyle);   // the only wrapping bright style
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(AchatInputName);
            _achatInput = GUILayout.TextField(_achatInput ?? "", 200, _textFieldStyle);
            if (GUILayout.Button(Loc.T("achat.send"), _buttonStyle, GUILayout.MinWidth(90))) AchatSend();
            if (GUILayout.Button(Loc.T("achat.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                // Zero the throttle and re-arm the backfill; the next Layout pass does the send, which
                // keeps every request on the one code path.
                _achatHistoryAsked = false;
                _achatNextReq = 0f;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("achat.cmd_hint"), _hintStyle);

            EndCard();
        }
    }
}

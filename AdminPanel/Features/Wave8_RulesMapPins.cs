using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — Server-wide map pins (client side, #14) ====================
    // Admin-defined named pins the companion pushes to every modded client's map (spawn town, arena, shop).
    // The table lives on the server ("mappins"); this card adds / deletes rows and draws the list the
    // server sends back. Vanilla shared pins need a cartography table and are per player; these are neither
    // — and a player WITHOUT the companion sees nothing at all, which the hint states plainly.
    //
    // IMGUI discipline: AP_MapPinsData lands during ZNet.Update, so the list the draw code walks is the
    // Layout snapshot, never the live field. Adding/deleting only sends; the reply repaints the list.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _mpinSectionCfg;

        private sealed class MpinRow
        {
            public int Id;
            public string Name = "";
            public Vector3 Pos;
            public int Icon;
            public string Category = "";
        }

        // ---- live payload (written by the RPC handler, any time) ----
        private List<MpinRow> _mpinRows;   // null = no reply yet
        private bool _mpinEnabled;
        private bool _mpinPending;
        private bool _mpinRequested;       // one request per section open; Refresh re-arms it
        private float _mpinNextReq;

        // ---- UI state ----
        private string _mpinName = "";
        private string _mpinCategory = "";
        private string _mpinX = "", _mpinY = "", _mpinZ = "";
        private int _mpinIconIdx;
        private bool _mpinAtMe = true;
        private Vector2 _mpinScroll;

        // ---- Layout snapshots (the ONLY things the draw code reads) ----
        private List<MpinRow> _mpinRowsLayout;
        private bool _mpinEnabledLayout;
        private bool _mpinPendingLayout;
        private bool _mpinHaveLayout;

        // Minimap.PinType values the companion accepts (Wave8Rules.AllowedIcons): Icon0 house, Icon1 fire,
        // Icon2 mine, Icon3 dot, Icon4 (=6) portal, Boss (=9), Bed (=5). Same order as the label keys.
        private static readonly int[] MpinIcons = { 0, 1, 2, 3, 6, 9, 5 };
        private static readonly string[] MpinIconKeys =
        {
            "mpin.icon_house", "mpin.icon_fire", "mpin.icon_mine", "mpin.icon_dot",
            "mpin.icon_portal", "mpin.icon_boss", "mpin.icon_bed",
        };

        private const int MpinCap = 200;   // wire contract with the companion

        // ---- lifecycle ----

        internal void MpinInit()
        {
            _mpinSectionCfg = Config.Bind("Features", "ShowMapPinsSection", true,
                "Show the Map Pins section in the Tools tab (server-wide pins pushed to modded clients).");
        }

        internal bool MpinSectionEnabled() => _mpinSectionCfg == null || _mpinSectionCfg.Value;

        internal void MpinReset()
        {
            _mpinRows = null;
            _mpinEnabled = false;
            _mpinPending = false;
            _mpinRequested = false;
            _mpinNextReq = 0f;
            _mpinName = "";
            _mpinCategory = "";
            _mpinX = _mpinY = _mpinZ = "";
            _mpinIconIdx = 0;
            _mpinAtMe = true;
            _mpinScroll = Vector2.zero;
            _mpinRowsLayout = null;
            _mpinEnabledLayout = false;
            _mpinPendingLayout = false;
            _mpinHaveLayout = false;
        }

        // ---- reply ----

        private static void MpinOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseMapPinsData(self, pkg);
        }

        // AP_MapPinsData: {int ver, bool enabled, int n(<=200), n x (int id, string name, float x, float y,
        //                  float z, int icon, string category)}
        internal static void ParseMapPinsData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != RulesVer) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > MpinCap) return;
                var rows = new List<MpinRow>(n);
                for (var i = 0; i < n; i++)
                {
                    var r = new MpinRow { Id = pkg.ReadInt(), Name = pkg.ReadString() ?? "" };
                    var x = pkg.ReadSingle(); var y = pkg.ReadSingle(); var z = pkg.ReadSingle();
                    r.Pos = new Vector3(x, y, z);
                    r.Icon = pkg.ReadInt();
                    r.Category = pkg.ReadString() ?? "";
                    rows.Add(r);
                }
                self._mpinRows = rows;
                self._mpinEnabled = enabled;
                self._mpinPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        private void MpinRequest()
        {
            if (!RulesReachable(false)) return;
            if (Time.time < _mpinNextReq) return;   // throttle-first, so a held button cannot spam
            _mpinNextReq = Time.time + 2f;
            _mpinPending = true;
            SrvRpc("AP_SrvMapPinsReq");
        }

        private void MpinAdd()
        {
            if (!RulesReachable(true)) return;
            var name = (_mpinName ?? "").Trim();
            if (name.Length == 0) { Message(Loc.T("mpin.msg_need_name")); return; }

            Vector3 pos;
            if (_mpinAtMe)
            {
                var p = LocalPlayer;
                if (p == null) { Message(Loc.T("mpin.msg_no_player")); return; }
                pos = p.transform.position;
            }
            else
            {
                float x, z, y;
                if (!RulesParseFloat(_mpinX, out x) || !RulesParseFloat(_mpinZ, out z))
                { Message(Loc.T("mpin.msg_bad_coords")); return; }
                if (!RulesParseFloat(_mpinY, out y))
                {
                    // Y is cosmetic for a map pin; the ground height is a nicer default than 0 when known.
                    y = 0f;
                    try { if (ZoneSystem.instance != null) y = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z)); }
                    catch (Exception) { y = 0f; }
                }
                pos = new Vector3(x, y, z);
            }

            var icon = MpinIcons[Mathf.Clamp(_mpinIconIdx, 0, MpinIcons.Length - 1)];
            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(0);                       // id 0 = new pin; the server allocates the id
            pkg.Write(name);
            pkg.Write(pos.x); pkg.Write(pos.y); pkg.Write(pos.z);
            pkg.Write(icon);
            pkg.Write((_mpinCategory ?? "").Trim());
            SrvRpc("AP_SrvMapPinSet", pkg);
            _mpinPending = true;
            _mpinName = "";
            Message(Loc.T("mpin.msg_sent", name));
        }

        private void MpinDelete(MpinRow row)
        {
            if (row == null || !RulesReachable(true)) return;
            SrvRpc("AP_SrvMapPinDel", row.Id);
            _mpinPending = true;
            Message(Loc.T("mpin.msg_deleted", row.Name));
        }

        private static string MpinIconLabel(int icon)
        {
            for (var i = 0; i < MpinIcons.Length; i++)
                if (MpinIcons[i] == icon) return Loc.T(MpinIconKeys[i]);
            return Loc.T("mpin.icon_dot");
        }

        // ---- draw ----

        internal void DrawMapPinsSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _mpinRowsLayout = _mpinRows;
                _mpinEnabledLayout = _mpinEnabled;
                _mpinPendingLayout = _mpinPending;
                _mpinHaveLayout = _mpinRows != null;
                // First look at the section = one request. Refresh zeroes the throttle and re-arms this.
                // Consume the one-shot only once a request can actually go out: F7 works during the connect
                // screen, where ZNet exists but there is no server peer yet, and burning the flag there would
                // leave the card on "press Refresh" for the whole session.
                if (!_mpinRequested && RulesReachable(false)) { _mpinRequested = true; MpinRequest(); }
            }

            BeginCard(Loc.T("mpin.section"));
            RulesDrawStatusRow(_mpinHaveLayout, _mpinEnabledLayout, RulesChPins,
                "mpin.status_unknown", "mpin.status_on", "mpin.status_off", "mpin.turn_on", "mpin.turn_off");

            // ---- add form (constant control count; the coordinate fields are simply ignored while "at my
            //      position" is on, so no control appears or vanishes with the toggle) ----
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mpin.name"), _labelStyle, GUILayout.MinWidth(70));
            _mpinName = GUILayout.TextField(_mpinName ?? "", 48, _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.Label(Loc.T("mpin.category"), _labelStyle, GUILayout.MinWidth(70));
            _mpinCategory = GUILayout.TextField(_mpinCategory ?? "", 24, _textFieldStyle, GUILayout.MinWidth(100));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mpin.icon"), _labelStyle, GUILayout.MinWidth(70));
            // A cycle button instead of a dropdown: no Layout/Repaint bookkeeping for an expanding list.
            if (GUILayout.Button(Loc.T(MpinIconKeys[Mathf.Clamp(_mpinIconIdx, 0, MpinIconKeys.Length - 1)]), _buttonStyle, GUILayout.MinWidth(90)))
                _mpinIconIdx = (_mpinIconIdx + 1) % MpinIcons.Length;
            _mpinAtMe = GUILayout.Toggle(_mpinAtMe, " " + Loc.T("mpin.at_me"), _toggleStyle);
            GUILayout.Label(Loc.T("mpin.x"), _labelStyle, GUILayout.MinWidth(20));
            _mpinX = GUILayout.TextField(_mpinX ?? "", 10, _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.Label(Loc.T("mpin.z"), _labelStyle, GUILayout.MinWidth(20));
            _mpinZ = GUILayout.TextField(_mpinZ ?? "", 10, _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.Label(Loc.T("mpin.y"), _labelStyle, GUILayout.MinWidth(20));
            _mpinY = GUILayout.TextField(_mpinY ?? "", 10, _textFieldStyle, GUILayout.MinWidth(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("mpin.add"), _buttonStyle, GUILayout.MinWidth(110))) MpinAdd();
            GUILayout.FlexibleSpace();
            GUILayout.Label(Loc.T("mpin.count", _mpinRowsLayout != null ? _mpinRowsLayout.Count : 0, MpinCap), _dimLabelStyle);
            if (GUILayout.Button(Loc.T("mpin.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                _mpinNextReq = 0f;
                _mpinRequested = false;   // the next Layout pass sends, on the one code path
            }
            GUILayout.EndHorizontal();

            // ---- list: idle / pending / empty / rows, every branch gated on the snapshot ----
            var rows = _mpinRowsLayout;
            if (rows == null)
            {
                GUILayout.Label(Loc.T(_mpinPendingLayout ? "mpin.pending" : "mpin.idle"), _hintStyle);
            }
            else if (rows.Count == 0)
            {
                GUILayout.Label(Loc.T("mpin.empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("mpin.col_name"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("mpin.col_category"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("mpin.col_icon"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("mpin.col_pos"), _headerStyle, GUILayout.MinWidth(120));
                GUILayout.EndHorizontal();

                _mpinScroll = GUILayout.BeginScrollView(_mpinScroll,
                    GUILayout.Height(Mathf.Min(ListView(360f), rows.Count * 28f + 16f)));
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Name, _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.Category.Length > 0 ? r.Category : "-", _dimCellStyle, GUILayout.Width(90));
                    GUILayout.Label(MpinIconLabel(r.Icon), _dimCellStyle, GUILayout.Width(80));
                    GUILayout.Label(RulesF(r.Pos.x) + " / " + RulesF(r.Pos.z), _dimCellStyle, GUILayout.MinWidth(120));
                    GUILayout.FlexibleSpace();
                    // Per-id confirm ids: arming Delete on one pin must never arm it on another.
                    if (ConfirmButton("mpin:del:" + r.Id, Loc.T("mpin.delete"), GUILayout.MinWidth(80)))
                        MpinDelete(r);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Label(Loc.T("mpin.hint", "Features.MapPinsHiddenCategories"), _hintStyle);
            EndCard();
        }
    }
}

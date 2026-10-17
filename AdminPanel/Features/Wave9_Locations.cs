using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #5 dungeon and location regeneration (World tab → Locations card) ====================
    // Find location instances with their loot state (untouched / partly looted / looted, and since when), then reset
    // one, or every instance of a type looted more than N days ago: the server deletes the location's objects and
    // re-spawns it through the engine's own location spawner, so chests refill and spawners reset. Refused while a
    // player is there; player builds, graves, tames, portals and ground items are never touched (Wave9SrvLocations.cs).
    public partial class AdminPanelPlugin
    {
        private const int LocrRowCap = 120;
        private static readonly string[] LocrStatusKeys = { "locr.s_none", "locr.s_untouched", "locr.s_partial", "locr.s_looted" };

        private sealed class LocrRow
        {
            public string Name = "";
            public Vector3 Pos;
            public int Zx, Zy, Status, Containers, Looted;
            public long LootedSince;
            public bool PlayerNear;
        }

        private ConfigEntry<bool> _locrCardCfg;
        private string _locrFilter = "Crypt";
        private string _locrBulkType = "";
        private string _locrBulkDays = "7";
        private List<LocrRow> _locrRows;
        private int _locrTotal;
        private long _locrMs;
        private bool _locrPending;
        private float _locrAskedAt;
        private Vector2 _locrScroll;

        private List<LocrRow> _locrRowsLayout;
        private string _locrWaitLayout = "locr.idle";

        internal void LocrInit()
        {
            _locrCardCfg = Config.Bind("Features", "ShowLocationsCard", true,
                "Show the Locations card (loot state of dungeons and locations, reset / regenerate) in the World tab.");
        }

        internal void LocrReset()
        {
            _locrRows = null;
            _locrPending = false;
            _locrRowsLayout = null;
        }

        internal static void LocrRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_LocrData", ParseLocrData);

        private static void ParseLocrData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var ms = pkg.ReadLong();
            var total = pkg.ReadInt();
            var n = pkg.ReadInt();
            if (n < 0 || n > LocrRowCap) return;
            var rows = new List<LocrRow>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new LocrRow
                {
                    Name = pkg.ReadString() ?? "", Pos = pkg.ReadVector3(), Zx = pkg.ReadInt(), Zy = pkg.ReadInt(), Status = pkg.ReadInt(),
                    Containers = pkg.ReadInt(), Looted = pkg.ReadInt(), LootedSince = pkg.ReadLong(), PlayerNear = pkg.ReadBool(),
                });
            self._locrRows = rows;
            self._locrTotal = total;
            self._locrMs = ms;
            self._locrPending = false;
        }

        private void LocrScan()
        {
            var pkg = W9Pkg();
            pkg.Write((_locrFilter ?? "").Trim());
            if (!W9Send("AP_SrvLocrScan", pkg)) return;
            _locrPending = true;
            _locrAskedAt = Time.time;
        }

        private void LocrReset(int mode, int zx, int zy, string type, int days)
        {
            var pkg = W9Pkg();
            pkg.Write(mode);
            pkg.Write(zx);
            pkg.Write(zy);
            pkg.Write(type ?? "");
            pkg.Write(days);
            if (W9Send("AP_SrvLocrReset", pkg)) Message(Loc.T("locr.msg_reset_sent"));
        }

        internal void DrawLocrCard()
        {
            if (_locrCardCfg != null && !_locrCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                _locrRowsLayout = _locrRows;
                _locrWaitLayout = !_locrPending ? "locr.idle" : (Time.time - _locrAskedAt > 180f ? "w9.no_answer" : "locr.scanning");
            }

            BeginCard(Loc.T("locr.section"));
            GUILayout.Label(Loc.T("locr.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("locr.filter"), _labelStyle, GUILayout.MinWidth(60));
            _locrFilter = GUILayout.TextField(_locrFilter ?? "", 80, _textFieldStyle, GUILayout.Width(180));
            if (GUILayout.Button(Loc.T("locr.scan"), _buttonStyle, GUILayout.MinWidth(90))) LocrScan();
            GUILayout.Label(Loc.T(_locrWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var rows = _locrRowsLayout;
            if (rows != null)
            {
                GUILayout.Label(Loc.T("locr.summary", rows.Count, _locrTotal, _locrMs), _labelStyle);
                if (rows.Count == 0) GUILayout.Label(Loc.T("locr.none"), _hintStyle);
                else
                {
                    _locrScroll = GUILayout.BeginScrollView(_locrScroll, GUILayout.Height(Mathf.Min(280f, rows.Count * 30f + 12f)));
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label(W9Short(r.Name, 22), _cellStyle, GUILayout.Width(150));
                        GUILayout.Label(W9Pos(r.Pos), _dimCellStyle, GUILayout.Width(90));
                        var status = Loc.T(r.Status >= 0 && r.Status < LocrStatusKeys.Length ? LocrStatusKeys[r.Status] : "locr.s_none");
                        if (r.Containers > 0) status += " " + r.Looted + "/" + r.Containers;
                        if (r.Status == 3 && r.LootedSince > 0) status += " (" + W9Ago(r.LootedSince) + ")";
                        GUILayout.Label(status, r.Status == 3 ? _headerStyle : _cellStyle, GUILayout.Width(190));
                        if (GUILayout.Button(Loc.T("locr.tp"), _buttonStyle, GUILayout.MinWidth(40))) TeleportToWorld(r.Pos, r.Name);
                        if (r.PlayerNear) GUILayout.Label(Loc.T("locr.player_near"), _dimCellStyle);
                        else if (ConfirmButton("locr:" + r.Zx + "," + r.Zy, Loc.T("locr.reset"), GUILayout.MinWidth(70))) LocrReset(0, r.Zx, r.Zy, "", 0);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }

            DrawSection(Loc.T("locr.bulk_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("locr.bulk_type"), _labelStyle, GUILayout.MinWidth(60));
            _locrBulkType = GUILayout.TextField(_locrBulkType ?? "", 64, _textFieldStyle, GUILayout.Width(150));
            GUILayout.Label(Loc.T("locr.bulk_days"), _labelStyle, GUILayout.MinWidth(80));
            _locrBulkDays = GUILayout.TextField(_locrBulkDays ?? "", 4, _textFieldStyle, GUILayout.Width(45));
            if (ConfirmButton("locr:bulk", Loc.T("locr.bulk_reset"), GUILayout.MinWidth(130)))
            {
                if ((_locrBulkType ?? "").Trim().Length == 0) Message(Loc.T("locr.msg_type_needed"));
                else LocrReset(2, 0, 0, _locrBulkType.Trim(), Mathf.Clamp(TkInt(_locrBulkDays, 7), 0, 3650));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("locr.bulk_hint"), _hintStyle);
            EndCard();
        }
    }
}

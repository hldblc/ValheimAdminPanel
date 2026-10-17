using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #9 progression timeline + anomaly flags (Players tab → Progression card) ====================
    // How far along each player is, from what the server can see without trusting the client: first entry into each
    // biome, first time seen equipped with gear of each material tier, and the boss kills they were present for
    // (Wave9SrvProgression.cs). Impossible jumps are flagged into the anti-cheat monitor's flag table (rule
    // "progression", alerts only, never a kick) and shown here first.
    public partial class AdminPanelPlugin
    {
        private const int ProgListCap = 200;

        private static readonly string[] ProgTierKeys = { "", "", "prog.t_bronze", "prog.t_iron", "prog.t_silver", "prog.t_blackmetal", "prog.t_mistlands", "prog.t_ashlands" };

        private sealed class ProgRow
        {
            public string Id = "", Name = "";
            public long First;
            public int MaxTier, Bosses, Flags;
            public bool Online;
        }

        private sealed class ProgDetail
        {
            public string Id = "", Name = "";
            public long First;
            public List<(long T, string Text, bool Flag)> Lines = new List<(long, string, bool)>();
        }

        private ConfigEntry<bool> _progCardCfg;
        private bool _progOpen;
        private List<ProgRow> _progRows;
        private int _progTotal;
        private ProgDetail _progDetail;
        private bool _progListPending, _progDetailPending;
        private float _progListAskedAt, _progDetailAskedAt, _progNextReq;
        private Vector2 _progListScroll, _progDetailScroll;

        private bool _progOpenLayout;
        private List<ProgRow> _progRowsLayout;
        private ProgDetail _progDetailLayout;
        private string _progListWaitLayout = "prog.idle", _progDetailWaitLayout = "prog.pick";

        internal void ProgInit()
        {
            _progCardCfg = Config.Bind("Features", "ShowProgressionCard", true,
                "Show the Progression card (biome / gear tier / boss timeline and anomaly flags) in the Players tab.");
        }

        internal void ProgReset()
        {
            _progRows = null;
            _progDetail = null;
            _progListPending = _progDetailPending = false;
            _progRowsLayout = null;
            _progDetailLayout = null;
        }

        internal static void ProgRegisterReplies(ZRoutedRpc r)
        {
            W9Reply(r, "AP_ProgList", ParseProgList);
            W9Reply(r, "AP_ProgData", ParseProgData);
        }

        private static void ParseProgList(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var total = pkg.ReadInt();
            var n = pkg.ReadInt();
            if (n < 0 || n > ProgListCap) return;
            var rows = new List<ProgRow>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new ProgRow
                {
                    Id = pkg.ReadString() ?? "", Name = pkg.ReadString() ?? "", First = pkg.ReadLong(), MaxTier = pkg.ReadInt(),
                    Bosses = pkg.ReadInt(), Flags = pkg.ReadInt(), Online = pkg.ReadBool(),
                });
            self._progRows = rows;
            self._progTotal = total;
            self._progListPending = false;
        }

        private static void ParseProgData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var d = new ProgDetail { Id = pkg.ReadString() ?? "", Name = pkg.ReadString() ?? "", First = pkg.ReadLong() };
            d.Lines.Add((d.First, Loc.T("prog.first_join"), false));
            var nb = pkg.ReadInt();
            if (nb < 0 || nb > 32) return;
            for (var i = 0; i < nb; i++)
            {
                var biome = pkg.ReadInt();
                var t = pkg.ReadLong();
                d.Lines.Add((t, Loc.T("prog.biome", ProgBiomeName(biome)), false));
            }
            var nt = pkg.ReadInt();
            if (nt < 0 || nt > 16) return;
            for (var i = 0; i < nt; i++)
            {
                var tier = pkg.ReadInt();
                var t = pkg.ReadLong();
                var item = pkg.ReadString() ?? "";
                d.Lines.Add((t, Loc.T("prog.tier", tier >= 0 && tier < ProgTierKeys.Length && ProgTierKeys[tier].Length > 0 ? Loc.T(ProgTierKeys[tier]) : tier.ToString(), item), false));
            }
            var nk = pkg.ReadInt();
            if (nk < 0 || nk > 32) return;
            for (var i = 0; i < nk; i++)
            {
                var boss = pkg.ReadString() ?? "";
                var t = pkg.ReadLong();
                d.Lines.Add((t, Loc.T("prog.boss", boss), false));
            }
            var nf = pkg.ReadInt();
            if (nf < 0 || nf > 64) return;
            for (var i = 0; i < nf; i++)
            {
                var t = pkg.ReadLong();
                d.Lines.Add((t, Loc.T("prog.flag", pkg.ReadString() ?? ""), true));
            }
            d.Lines.Sort((a, b) => a.T.CompareTo(b.T));
            self._progDetail = d;
            self._progDetailPending = false;
            self._progDetailScroll = Vector2.zero;
        }

        private static string ProgBiomeName(int biome)
        {
            switch (biome)
            {
                case 1: return Loc.T("prog.b_meadows");
                case 2: return Loc.T("prog.b_swamp");
                case 4: return Loc.T("prog.b_mountain");
                case 8: return Loc.T("prog.b_blackforest");
                case 16: return Loc.T("prog.b_plains");
                case 32: return Loc.T("prog.b_ashlands");
                case 64: return Loc.T("prog.b_deepnorth");
                case 256: return Loc.T("prog.b_ocean");
                case 512: return Loc.T("prog.b_mistlands");
                default: return "#" + biome;
            }
        }

        private void ProgRequestList()
        {
            if (Time.time < _progNextReq) return;
            _progNextReq = Time.time + 1f;
            if (!W9Send("AP_SrvProgList")) return;
            _progListPending = true;
            _progListAskedAt = Time.time;
        }

        private void ProgRequestDetail(string id)
        {
            if (!TkReachable()) { Message(Loc.T("common.not_connected_srv")); return; }
            SrvRpc("AP_SrvProgReq", id);
            _progDetailPending = true;
            _progDetailAskedAt = Time.time;
        }

        internal void DrawProgCard()
        {
            if (_progCardCfg != null && !_progCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                _progOpenLayout = _progOpen;
                _progRowsLayout = _progRows;
                _progDetailLayout = _progDetail;
                _progListWaitLayout = W9WaitKey(_progListPending, _progListAskedAt, "prog.idle");
                _progDetailWaitLayout = W9WaitKey(_progDetailPending, _progDetailAskedAt, "prog.pick");
            }

            BeginCard(Loc.T("prog.section"));
            var open = GUILayout.Toggle(_progOpenLayout, " " + Loc.T("prog.show"), _toggleStyle);
            if (open != _progOpenLayout)
            {
                _progOpen = open;
                if (open && _progRows == null) ProgRequestList();
            }
            if (!_progOpenLayout) { EndCard(); return; }

            GUILayout.Label(Loc.T("prog.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("prog.refresh"), _buttonStyle, GUILayout.MinWidth(100))) ProgRequestList();
            GUILayout.Label(_progRowsLayout != null ? Loc.T("prog.count", _progRowsLayout.Count, _progTotal) : Loc.T(_progListWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var rows = _progRowsLayout;
            if (rows != null && rows.Count > 0)
            {
                _progListScroll = GUILayout.BeginScrollView(_progListScroll, GUILayout.Height(Mathf.Min(170f, rows.Count * 28f + 10f)));
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(W9Short(r.Name, 20) + (r.Online ? "" : " " + Loc.T("prog.offline")), _cellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.MaxTier >= 2 && r.MaxTier < ProgTierKeys.Length ? Loc.T(ProgTierKeys[r.MaxTier]) : "-", _dimCellStyle, GUILayout.Width(90));
                    GUILayout.Label(Loc.T("prog.bosses_n", r.Bosses), _dimCellStyle, GUILayout.Width(80));
                    GUILayout.Label(r.Flags > 0 ? Loc.T("prog.flags_n", r.Flags) : "", r.Flags > 0 ? _headerStyle : _dimCellStyle, GUILayout.Width(80));
                    if (GUILayout.Button(Loc.T("prog.view"), _buttonStyle, GUILayout.MinWidth(70))) ProgRequestDetail(r.Id);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            var d = _progDetailLayout;
            DrawSection(d != null ? Loc.T("prog.timeline_for", d.Name) : Loc.T("prog.timeline"));
            if (d == null || _progDetailWaitLayout != "prog.pick") GUILayout.Label(Loc.T(_progDetailWaitLayout), _hintStyle);
            if (d != null)
            {
                _progDetailScroll = GUILayout.BeginScrollView(_progDetailScroll, GUILayout.Height(Mathf.Min(220f, d.Lines.Count * 24f + 10f)));
                foreach (var l in d.Lines)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(W9Date(l.T), _dimCellStyle, GUILayout.Width(120));
                    GUILayout.Label(l.Text, l.Flag ? _headerStyle : _cellStyle);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            EndCard();
        }
    }
}

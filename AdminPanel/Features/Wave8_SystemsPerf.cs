using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #25 Client performance census (client card) ====================
    // Roster of what every MODDED client reported to the server: FPS average / minimum over the report
    // window, ping, companion version and plugin list. Read-only. Rows are highlighted by label style when
    // the average FPS is below 30 or the reported companion version differs from the server's.
    //
    // IMGUI: the expand toggles write a live HashSet on the event pass; the rows are drawn from a copy pinned
    // on the Layout pass, so a click can never add the mod-list rows mid-frame.
    //
    // Member prefix: "Pcensus". Locale prefix: "pcensus.".
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _pcensusSectionCfg;

        private const int PcensusRowCap = 100;   // wire contract: rows <= 100
        private const int PcensusModCap = 60;    // wire contract: mods per row <= 60
        private const int PcensusLowFps = 30;

        private sealed class PerfData
        {
            public string ServerVersion = "";
            public int ReportSeconds = 60;
            public readonly List<PerfRow> Rows = new List<PerfRow>();
            public float ReceivedAt;
        }

        private sealed class PerfRow
        {
            public long Uid;
            public string Name;
            public string Id;
            public int AvgFps;
            public int MinFps;
            public int Ping;
            public string Version;
            public int AgeSeconds;
            public readonly List<string> Mods = new List<string>();
        }

        // ---- live payload ----
        private PerfData _pcensusData;
        private bool _pcensusPending;

        // ---- UI state ----
        private float _pcensusNextReq;
        private readonly HashSet<long> _pcensusExpanded = new HashSet<long>();
        private Vector2 _pcensusScroll;

        // ---- Layout snapshots ----
        private PerfData _pcensusDataLayout;
        private HashSet<long> _pcensusExpandedLayout;
        private bool _pcensusHostLayout;
        private bool _pcensusPendingLayout;
        private bool _pcensusSoloLayout;

        // ==================== lifecycle ====================

        internal void PcensusInit()
        {
            _pcensusSectionCfg = Config.Bind("Features", "ShowClientPerfSection", true,
                "Show the Client performance census card in the Tools tab (FPS, ping and plugin list reported by clients that run the companion). Read-only.");
        }

        internal bool PcensusSectionEnabled() => _pcensusSectionCfg == null || _pcensusSectionCfg.Value;

        internal void PcensusReset()
        {
            _pcensusData = null;
            _pcensusPending = false;
            _pcensusNextReq = 0f;
            _pcensusExpanded.Clear();
            _pcensusScroll = Vector2.zero;
            _pcensusDataLayout = null;
            _pcensusExpandedLayout = null;
            _pcensusHostLayout = false;
            _pcensusPendingLayout = false;
            _pcensusSoloLayout = false;
        }

        // ==================== reply ====================

        private static void PcensusOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseClientPerf(self, pkg);
        }

        // AP_ClientPerf (v1): int ver, string serverVersion, int reportSeconds, int rows(<=100) x
        // (long uid, string name, string id, int avgFps, int minFps, int ping, string version, int ageSeconds,
        //  int mods(<=60) x string).
        internal static void ParseClientPerf(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PerfData
                {
                    ServerVersion = pkg.ReadString() ?? "",
                    ReportSeconds = pkg.ReadInt(),
                    ReceivedAt = Time.time,
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > PcensusRowCap) return;
                for (var i = 0; i < n; i++)
                {
                    var r = new PerfRow
                    {
                        Uid = pkg.ReadLong(),
                        Name = pkg.ReadString() ?? "",
                        Id = pkg.ReadString() ?? "",
                        AvgFps = pkg.ReadInt(),
                        MinFps = pkg.ReadInt(),
                        Ping = pkg.ReadInt(),
                        Version = pkg.ReadString() ?? "",
                        AgeSeconds = pkg.ReadInt(),
                    };
                    var m = pkg.ReadInt();
                    if (m < 0 || m > PcensusModCap) return;
                    for (var j = 0; j < m; j++) r.Mods.Add(pkg.ReadString() ?? "");
                    d.Rows.Add(r);
                }
                self._pcensusData = d;
                self._pcensusPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ==================== requests ====================

        private void PcensusPoll()
        {
            if (!SysPollDue(ref _pcensusNextReq, 30f, _pcensusHostLayout)) return;
            _pcensusPending = _pcensusData == null;
            SrvRpc("AP_SrvClientPerfReq");
        }

        // ==================== draw ====================

        internal void DrawClientPerfSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _pcensusHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _pcensusDataLayout = _pcensusData;
                _pcensusPendingLayout = _pcensusPending;
                _pcensusExpandedLayout = new HashSet<long>(_pcensusExpanded);
                // Read the roster DrawWindow pinned this frame - never rebuild it. Text only.
                _pcensusSoloLayout = _othersSnapshot != null && _othersSnapshot.Count == 0;
                PcensusPoll();
            }

            var d = _pcensusDataLayout;
            var expanded = _pcensusExpandedLayout ?? new HashSet<long>();
            BeginCard(Loc.T("pcensus.section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("pcensus.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _pcensusNextReq = 0f;   // the next Layout pass sends; never send from the event pass
            GUILayout.Space(8);
            GUILayout.Label(d == null
                    ? Loc.T(_pcensusPendingLayout ? "pcensus.pending" : "pcensus.no_reply")
                    : Loc.T("pcensus.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("pcensus.hint", d != null ? d.ReportSeconds : 60), _hintStyle);

            if (d != null && d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T(_pcensusSoloLayout ? "pcensus.empty_solo" : "pcensus.empty"), _hintStyle);
            }
            else if (d != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pcensus.col_player"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("pcensus.col_fps"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("pcensus.col_ping"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("pcensus.col_mods"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("pcensus.col_version"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("pcensus.col_age"), _headerStyle, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                _pcensusScroll = GUILayout.BeginScrollView(_pcensusScroll, GUILayout.Height(ListView(330f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    var lowFps = r.AvgFps > 0 && r.AvgFps < PcensusLowFps;
                    var oldVersion = d.ServerVersion.Length > 0 && !string.Equals(r.Version, d.ServerVersion, StringComparison.Ordinal);
                    var open = expanded.Contains(r.Uid);

                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    // The toggle writes the LIVE set; the expanded rows below follow the Layout copy.
                    var on = GUILayout.Toggle(open, string.IsNullOrEmpty(r.Name) ? r.Id : r.Name, _toggleStyle, GUILayout.Width(150));
                    if (on != open) { if (on) _pcensusExpanded.Add(r.Uid); else _pcensusExpanded.Remove(r.Uid); }
                    GUILayout.Label(Loc.T("pcensus.fps_cell", r.AvgFps, r.MinFps), lowFps ? _cellStyle : _dimCellStyle, GUILayout.Width(90));
                    GUILayout.Label(r.Ping < 0 ? Loc.T("pcensus.ping_unknown") : Loc.T("pcensus.ping_cell", r.Ping), _dimCellStyle, GUILayout.Width(70));
                    GUILayout.Label(Loc.T("pcensus.mods_cell", r.Mods.Count), _dimCellStyle, GUILayout.Width(70));
                    GUILayout.Label(r.Version.Length > 0 ? r.Version : "?", oldVersion ? _cellStyle : _dimCellStyle, GUILayout.Width(80));
                    GUILayout.Label(Loc.T("pcensus.age_cell", r.AgeSeconds), _dimCellStyle, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    if (!open) continue;
                    if (r.Mods.Count == 0)
                    {
                        GUILayout.Label(Loc.T("pcensus.no_mods"), _hintStyle);
                        continue;
                    }
                    for (var j = 0; j < r.Mods.Count; j++)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(24);
                        GUILayout.Label(r.Mods[j] ?? "", _dimCellStyle, GUILayout.MinWidth(300));
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.Label(Loc.T("pcensus.legend", PcensusLowFps, d.ServerVersion.Length > 0 ? d.ServerVersion : "?"), _hintStyle);
            }

            EndCard();
        }
    }
}

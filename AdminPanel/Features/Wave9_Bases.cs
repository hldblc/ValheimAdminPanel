using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #8 abandoned-base finder + cleanup (Tools › World › Bases) ====================
    // Player-built pieces grouped into bases, joined with when their builders were last seen, listed when everyone
    // who built them has been gone N days (or was never seen since round 3 started tracking builder ids). Each base
    // shows pieces, ZDO cost and builders; actions: teleport, claim (become its builder), turn into ruins, or delete
    // after a snapshot that can be restored. Ships and carts left far from any base are listed too.
    // Server side: Wave9SrvBases.cs.
    public partial class AdminPanelPlugin
    {
        private const int BasesRowCap = 100;

        private sealed class BaseRow
        {
            public int Id;
            public Vector3 Center;
            public int Pieces, Zdos, Containers;
            public long LastSeen;
            public bool Unknown;
            public string Builders = "";
        }

        private sealed class BaseSnap { public string Name = ""; public long Ticks; public int Bytes; }

        private ConfigEntry<bool> _basesSectionCfg;
        private string _basesDays = "30";
        private string _basesRuinPct = "50";
        private List<BaseRow> _basesRows;
        private List<(string Name, Vector3 Pos)> _basesVehicles;
        private List<BaseSnap> _basesSnaps;
        private int _basesTotal, _basesDaysUsed;
        private int _basesGen;   // the scan generation the rows came from; actions echo it so a rescan elsewhere refuses them
        private long _basesMs;
        private bool _basesPending;
        private float _basesAskedAt;
        private Vector2 _basesScroll, _basesVehScroll;

        private List<BaseRow> _basesRowsLayout;
        private List<(string Name, Vector3 Pos)> _basesVehLayout;
        private List<BaseSnap> _basesSnapsLayout;
        private string _basesWaitLayout = "bases.idle";

        internal void BasesInit()
        {
            _basesSectionCfg = Config.Bind("Features", "ShowBasesSection", true,
                "Show the Bases section (abandoned-base finder, claim / ruin / delete with snapshot) in Tools > World.");
        }

        internal bool BasesSectionEnabled() => _basesSectionCfg == null || _basesSectionCfg.Value;

        internal void BasesReset()
        {
            _basesRows = null;
            _basesVehicles = null;
            _basesSnaps = null;
            _basesPending = false;
            _basesRowsLayout = null;
            _basesVehLayout = null;
            _basesSnapsLayout = null;
        }

        internal static void BasesRegisterReplies(ZRoutedRpc r)
        {
            W9Reply(r, "AP_BasesData", ParseBasesData);
            W9Reply(r, "AP_BasesSnaps", ParseBasesSnaps);
        }

        private static void ParseBasesData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var gen = pkg.ReadInt();
            var ms = pkg.ReadLong();
            var total = pkg.ReadInt();
            var days = pkg.ReadInt();
            var n = pkg.ReadInt();
            if (n < 0 || n > BasesRowCap) return;
            var rows = new List<BaseRow>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new BaseRow
                {
                    Id = pkg.ReadInt(), Center = pkg.ReadVector3(), Pieces = pkg.ReadInt(), Zdos = pkg.ReadInt(), Containers = pkg.ReadInt(),
                    LastSeen = pkg.ReadLong(), Unknown = pkg.ReadBool(), Builders = pkg.ReadString() ?? "",
                });
            var nv = pkg.ReadInt();
            if (nv < 0 || nv > 50) return;
            var veh = new List<(string, Vector3)>(nv);
            for (var i = 0; i < nv; i++) veh.Add((pkg.ReadString() ?? "", pkg.ReadVector3()));
            self._basesGen = gen;
            self._basesRows = rows;
            self._basesVehicles = veh;
            self._basesTotal = total;
            self._basesDaysUsed = days;
            self._basesMs = ms;
            self._basesPending = false;
        }

        private static void ParseBasesSnaps(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > 40) return;
            var list = new List<BaseSnap>(n);
            for (var i = 0; i < n; i++) list.Add(new BaseSnap { Name = pkg.ReadString() ?? "", Ticks = pkg.ReadLong(), Bytes = pkg.ReadInt() });
            self._basesSnaps = list;
        }

        private void BasesScan()
        {
            var pkg = W9Pkg();
            pkg.Write(Mathf.Clamp(TkInt(_basesDays, 30), 0, 3650));
            if (!W9Send("AP_SrvBasesScan", pkg)) return;
            _basesPending = true;
            _basesAskedAt = Time.time;
        }

        private void BasesAct(BaseRow b, int action, int param)
        {
            var pkg = W9Pkg();
            pkg.Write(_basesGen);
            pkg.Write(b.Id);
            pkg.Write(action);
            pkg.Write(param);
            W9Send("AP_SrvBasesAct", pkg);
        }

        internal void DrawBasesSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _basesRowsLayout = _basesRows;
                _basesVehLayout = _basesVehicles;
                _basesSnapsLayout = _basesSnaps;
                _basesWaitLayout = !_basesPending ? "bases.idle" : (Time.time - _basesAskedAt > 180f ? "w9.no_answer" : "bases.scanning");
            }

            BeginCard(Loc.T("bases.section"));
            GUILayout.Label(Loc.T("bases.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bases.days"), _labelStyle, GUILayout.MinWidth(150));
            _basesDays = GUILayout.TextField(_basesDays ?? "", 4, _textFieldStyle, GUILayout.Width(50));
            if (GUILayout.Button(Loc.T("bases.scan"), _buttonStyle, GUILayout.MinWidth(110))) BasesScan();
            GUILayout.Label(Loc.T(_basesWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bases.ruin_pct"), _labelStyle, GUILayout.MinWidth(150));
            _basesRuinPct = GUILayout.TextField(_basesRuinPct ?? "", 2, _textFieldStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var rows = _basesRowsLayout;
            if (rows != null)
            {
                GUILayout.Label(Loc.T("bases.summary", rows.Count, _basesTotal, _basesDaysUsed, _basesMs), _labelStyle);
                if (rows.Count == 0) GUILayout.Label(Loc.T("bases.none"), _hintStyle);
                else
                {
                    _basesScroll = GUILayout.BeginScrollView(_basesScroll, GUILayout.Height(Mathf.Min(FeatureListView(420f, 150f), rows.Count * 58f + 12f)));
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var b = rows[i];
                        GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("#" + b.Id + "  " + W9Pos(b.Center), _cellStyle, GUILayout.Width(130));
                        GUILayout.Label(Loc.T("bases.counts", b.Pieces, b.Zdos, b.Containers), _cellStyle, GUILayout.Width(210));
                        GUILayout.Label(b.Unknown ? Loc.T("bases.seen_unknown") : Loc.T("bases.seen", W9Ago(b.LastSeen)), _dimCellStyle);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(16);
                        GUILayout.Label(W9Short(b.Builders, 40), _dimCellStyle, GUILayout.Width(220));
                        if (GUILayout.Button(Loc.T("bases.tp"), _buttonStyle, GUILayout.MinWidth(40))) TeleportToWorld(b.Center, "#" + b.Id);
                        if (ConfirmButton("bases:claim:" + b.Id, Loc.T("bases.claim"), GUILayout.MinWidth(60))) BasesAct(b, 1, 0);
                        if (ConfirmButton("bases:ruin:" + b.Id, Loc.T("bases.ruin"), GUILayout.MinWidth(60))) BasesAct(b, 2, Mathf.Clamp(TkInt(_basesRuinPct, 50), 10, 90));
                        if (ConfirmButton("bases:del:" + b.Id, Loc.T("bases.delete"), GUILayout.MinWidth(70))) BasesAct(b, 3, 0);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.EndVertical();
                    }
                    GUILayout.EndScrollView();
                }

                var veh = _basesVehLayout;
                DrawSection(Loc.T("bases.vehicles", veh != null ? veh.Count : 0));
                if (veh == null || veh.Count == 0) GUILayout.Label(Loc.T("bases.vehicles_none"), _hintStyle);
                else
                {
                    _basesVehScroll = GUILayout.BeginScrollView(_basesVehScroll, GUILayout.Height(Mathf.Min(140f, veh.Count * 26f + 10f)));
                    foreach (var v in veh)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(v.Name, _cellStyle, GUILayout.Width(160));
                        GUILayout.Label(W9Pos(v.Pos), _dimCellStyle, GUILayout.Width(110));
                        if (GUILayout.Button(Loc.T("bases.tp"), _buttonStyle, GUILayout.MinWidth(40))) TeleportToWorld(v.Pos, v.Name);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }

            DrawSection(Loc.T("bases.snaps"));
            if (GUILayout.Button(Loc.T("bases.snaps_load"), _buttonStyle, GUILayout.MinWidth(130))) W9Send("AP_SrvBasesSnaps");
            var snaps = _basesSnapsLayout;
            if (snaps != null)
            {
                if (snaps.Count == 0) GUILayout.Label(Loc.T("bases.snaps_none"), _hintStyle);
                foreach (var sn in snaps)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(sn.Name, _cellStyle, GUILayout.Width(260));
                    GUILayout.Label(W9Date(sn.Ticks), _dimCellStyle, GUILayout.Width(120));
                    if (ConfirmButton("bases:restore:" + sn.Name, Loc.T("bases.restore"), GUILayout.MinWidth(80)))
                    {
                        if (TkReachable()) SrvRpc("AP_SrvBasesRestore", sn.Name);
                        else Message(Loc.T("common.not_connected_srv"));
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }
    }
}

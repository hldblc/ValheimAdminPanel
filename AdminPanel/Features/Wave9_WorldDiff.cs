using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #11 world diff between backups (Server tab → Backup compare card) ====================
    // Turns the backups from all-or-nothing rollbacks into a forensic tool: compare the live world (or backup A) with
    // backup B, per 64 m zone - player-built objects added and removed (with their builders) and containers whose
    // contents changed. Each row teleports there, or restores just that zone: recreate the pieces that were removed,
    // put the backup's contents back into the changed chests, or remove what was built since (Wave9SrvWorldDiff.cs).
    public partial class AdminPanelPlugin
    {
        private const int WdiffSetCap = 60;
        private const int WdiffRowCap = 80;

        private sealed class WdiffSet { public string Name = ""; public long Ticks; public long Bytes; }

        private sealed class WdiffRow
        {
            public Vector3 Zone;
            public int Added, Removed, Changed;
            public string Builders = "";
            public string AddedTop = "", RemovedTop = "";   // "prefab xN, ..." - what a restore button would touch
        }

        private ConfigEntry<bool> _wdiffCardCfg;
        private List<WdiffSet> _wdiffSets;
        private int _wdiffA = -1;            // -1 = live world
        private int _wdiffB;
        private string _wdiffRadius = "0";
        private bool _wdiffAroundMe;
        private List<WdiffRow> _wdiffRows;
        private string _wdiffLabelA = "", _wdiffLabelB = "", _wdiffError = "";
        private int _wdiffAdded, _wdiffRemoved, _wdiffChanged;
        private long _wdiffMs;
        private int _wdiffRun;   // the server's result id; a restore names it so a newer comparison refuses it
        private bool _wdiffPending, _wdiffSetsPending;
        private float _wdiffAskedAt, _wdiffSetsAskedAt;
        private Vector2 _wdiffScroll;

        private List<WdiffSet> _wdiffSetsLayout;
        private List<WdiffRow> _wdiffRowsLayout;
        private bool _wdiffPendingLayout;
        private bool _wdiffErrorLayout, _wdiffLiveLayout;   // branch drivers pinned with the rows (a host's reply lands mid-pass)
        private string _wdiffSetsWaitLayout = "wdiff.sets_idle";

        internal void WdiffInit()
        {
            _wdiffCardCfg = Config.Bind("Features", "ShowBackupCompareCard", true,
                "Show the Backup compare card (world diff between backups, per-zone restore) in the Server tab.");
        }

        internal void WdiffReset()
        {
            _wdiffSets = null;
            _wdiffRows = null;
            _wdiffA = -1;
            _wdiffB = 0;
            _wdiffPending = _wdiffSetsPending = false;
            _wdiffSetsLayout = null;
            _wdiffRowsLayout = null;
        }

        internal static void WdiffRegisterReplies(ZRoutedRpc r)
        {
            W9Reply(r, "AP_WdiffSets", ParseWdiffSets);
            W9Reply(r, "AP_WdiffData", ParseWdiffData);
        }

        private static void ParseWdiffSets(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > WdiffSetCap) return;
            var list = new List<WdiffSet>(n);
            for (var i = 0; i < n; i++) list.Add(new WdiffSet { Name = pkg.ReadString() ?? "", Ticks = pkg.ReadLong(), Bytes = pkg.ReadLong() });
            self._wdiffSets = list;
            self._wdiffSetsPending = false;
            if (self._wdiffB >= list.Count) self._wdiffB = 0;
            if (self._wdiffA >= list.Count) self._wdiffA = -1;
        }

        private static void ParseWdiffData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var run = pkg.ReadInt();
            self._wdiffLabelA = pkg.ReadString() ?? "";
            self._wdiffLabelB = pkg.ReadString() ?? "";
            self._wdiffMs = pkg.ReadLong();
            self._wdiffAdded = pkg.ReadInt();
            self._wdiffRemoved = pkg.ReadInt();
            self._wdiffChanged = pkg.ReadInt();
            self._wdiffError = pkg.ReadString() ?? "";
            var n = pkg.ReadInt();
            if (n < 0 || n > WdiffRowCap) return;
            var rows = new List<WdiffRow>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new WdiffRow
                {
                    Zone = pkg.ReadVector3(), Added = pkg.ReadInt(), Removed = pkg.ReadInt(), Changed = pkg.ReadInt(), Builders = pkg.ReadString() ?? "",
                    AddedTop = pkg.ReadString() ?? "", RemovedTop = pkg.ReadString() ?? "",
                });
            self._wdiffRun = run;
            self._wdiffRows = rows;
            self._wdiffPending = false;
            self._wdiffScroll = Vector2.zero;
        }

        private void WdiffLoadSets()
        {
            if (!W9Send("AP_SrvWdiffSets")) return;
            _wdiffSetsPending = true;
            _wdiffSetsAskedAt = Time.time;
        }

        private string WdiffSetLabel(int idx)
        {
            if (idx < 0) return Loc.T("wdiff.live");
            var sets = _wdiffSetsLayout;
            return sets != null && idx < sets.Count ? sets[idx].Name : "?";
        }

        private void WdiffRun()
        {
            var sets = _wdiffSets;
            if (sets == null || sets.Count == 0) { Message(Loc.T("wdiff.msg_no_sets")); return; }
            if (_wdiffA == _wdiffB) { Message(Loc.T("wdiff.msg_same")); return; }
            var radius = Mathf.Max(0f, TkFloat(_wdiffRadius, 0f));
            var pkg = W9Pkg();
            pkg.Write(_wdiffA < 0 ? "live" : sets[_wdiffA].Name);
            pkg.Write(sets[_wdiffB].Name);
            pkg.Write(_wdiffAroundMe && LocalPlayer != null ? LocalPlayer.transform.position : Vector3.zero);
            pkg.Write(_wdiffAroundMe ? radius : 0f);
            if (!W9Send("AP_SrvWdiffRun", pkg)) return;
            _wdiffPending = true;
            _wdiffAskedAt = Time.time;
        }

        private void WdiffRestore(WdiffRow r, int what)
        {
            var pkg = W9Pkg();
            pkg.Write(_wdiffRun);
            pkg.Write(r.Zone);
            pkg.Write(what);
            W9Send("AP_SrvWdiffRestore", pkg);
        }

        internal void DrawWdiffCard()
        {
            if (_wdiffCardCfg != null && !_wdiffCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                _wdiffSetsLayout = _wdiffSets;
                _wdiffRowsLayout = _wdiffRows;
                _wdiffErrorLayout = !string.IsNullOrEmpty(_wdiffError);
                _wdiffLiveLayout = _wdiffLabelA == "live";
                _wdiffPendingLayout = _wdiffPending && Time.time - _wdiffAskedAt < 600f;
                _wdiffSetsWaitLayout = W9WaitKey(_wdiffSetsPending, _wdiffSetsAskedAt, "wdiff.sets_idle");
            }

            BeginCard(Loc.T("wdiff.section"));
            GUILayout.Label(Loc.T("wdiff.hint"), _hintStyle);
            var sets = _wdiffSetsLayout;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("wdiff.load_sets"), _buttonStyle, GUILayout.MinWidth(130))) WdiffLoadSets();
            GUILayout.Label(sets != null ? Loc.T("wdiff.sets_count", sets.Count) : Loc.T(_wdiffSetsWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (sets != null && sets.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("wdiff.side_a"), _labelStyle, GUILayout.MinWidth(30));
                if (GUILayout.Button(WdiffSetLabel(_wdiffA), _buttonStyle, GUILayout.MinWidth(170)))
                    _wdiffA = _wdiffA + 1 >= sets.Count ? -1 : _wdiffA + 1;
                GUILayout.Label(Loc.T("wdiff.side_b"), _labelStyle, GUILayout.MinWidth(30));
                if (GUILayout.Button(WdiffSetLabel(_wdiffB), _buttonStyle, GUILayout.MinWidth(170)))
                    _wdiffB = (_wdiffB + 1) % sets.Count;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                _wdiffAroundMe = GUILayout.Toggle(_wdiffAroundMe, " " + Loc.T("wdiff.around_me"), _toggleStyle);
                _wdiffRadius = GUILayout.TextField(_wdiffRadius ?? "", 5, _textFieldStyle, GUILayout.Width(55));
                GUILayout.Label(Loc.T("wdiff.meters"), _labelStyle);
                if (GUILayout.Button(Loc.T("wdiff.compare"), _buttonStyle, GUILayout.MinWidth(110))) WdiffRun();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (_wdiffPendingLayout) GUILayout.Label(Loc.T("wdiff.running"), _hintStyle);

            var rows = _wdiffRowsLayout;
            if (rows != null)
            {
                if (_wdiffErrorLayout) GUILayout.Label(Loc.T("wdiff.error", _wdiffError ?? ""), _proseStyle);
                else
                {
                    GUILayout.Label(Loc.T("wdiff.totals", _wdiffLabelA, _wdiffLabelB, _wdiffAdded, _wdiffRemoved, _wdiffChanged, _wdiffMs), _labelStyle);
                    var live = _wdiffLiveLayout;
                    if (rows.Count == 0) GUILayout.Label(Loc.T("wdiff.no_changes"), _hintStyle);
                    else
                    {
                        _wdiffScroll = GUILayout.BeginScrollView(_wdiffScroll, GUILayout.Height(Mathf.Min(320f, rows.Count * 78f + 12f)));
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var r = rows[i];
                            GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(W9Pos(r.Zone), _cellStyle, GUILayout.Width(95));
                            GUILayout.Label(Loc.T("wdiff.row_counts", r.Added, r.Removed, r.Changed), _cellStyle, GUILayout.Width(190));
                            GUILayout.Label(W9Short(r.Builders, 40), _dimCellStyle);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(Loc.T("wdiff.tp"), _buttonStyle, GUILayout.MinWidth(40))) TeleportToWorld(r.Zone, W9Pos(r.Zone));
                            GUILayout.EndHorizontal();
                            if (live)
                            {
                                GUILayout.BeginHorizontal();
                                GUILayout.Space(16);
                                var key = r.Zone.x.ToString("0", CultureInfo.InvariantCulture) + "," + r.Zone.z.ToString("0", CultureInfo.InvariantCulture);
                                if (r.Removed > 0 && ConfirmButton("wdiff:re:" + key, Loc.T("wdiff.recreate", r.Removed), GUILayout.MinWidth(150))) WdiffRestore(r, 1);
                                if (r.Changed > 0 && ConfirmButton("wdiff:ch:" + key, Loc.T("wdiff.restore_chests", r.Changed), GUILayout.MinWidth(150))) WdiffRestore(r, 2);
                                if (r.Added > 0 && ConfirmButton("wdiff:ad:" + key, Loc.T("wdiff.remove_added", r.Added), GUILayout.MinWidth(150))) WdiffRestore(r, 3);
                                GUILayout.FlexibleSpace();
                                GUILayout.EndHorizontal();
                                // What the buttons would touch, by prefab (+ added, - removed since the backup).
                                GUILayout.Label("    + " + W9Short(r.AddedTop, 70) + "     - " + W9Short(r.RemovedTop, 70), _dimCellStyle);
                            }
                            GUILayout.EndVertical();
                        }
                        GUILayout.EndScrollView();
                    }
                    if (!live) GUILayout.Label(Loc.T("wdiff.restore_needs_live"), _hintStyle);
                }
            }
            EndCard();
        }
    }
}

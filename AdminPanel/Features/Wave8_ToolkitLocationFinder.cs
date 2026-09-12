using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #13 location finder (client side) ====================
    // "Where is the nearest crypt / trader / runestone?" answered from the server's generated location
    // table, which is complete for the whole world (see Wave8SrvToolkitLoc.cs). Two requests:
    //   AP_SrvLocTypesReq -> AP_LocTypes   every location prefab with counts (data-driven type list)
    //   AP_SrvLocFindReq  -> AP_LocFind    nearest N matching a filter, from the admin's position
    // The preset chips (crypts, caves, traders, ...) are nothing but filter strings; the type list is
    // the truth about which names exist on THIS server, so a modded world's extra locations are one
    // click away without a code change. Teleport is client-side (the admin moves themself).
    //
    // The World tab already teleports to the boss altars through the game's own location icons; the
    // "Boss altars" chip here is just the same names as a filter, not a second implementation.
    public partial class AdminPanelPlugin
    {
        private const int LocfTypeCap = 150;   // wire contract
        private const int LocfRowCap = 50;     // wire contract

        private ConfigEntry<bool> _locfSectionCfg;

        private sealed class LocfType
        {
            public string Name = "";
            public int Count;
            public int Placed;
        }

        private sealed class LocfRow
        {
            public string Name = "";
            public Vector3 Pos;
            public float Dist;      // server-side distance at request time (sort order)
            public bool Placed;
        }

        // Preset filters: key of the chip label + the comma-separated substrings sent to the server.
        // Names follow the vanilla location prefabs (Crypt2/3/4 + SunkenCrypt4, TrollCave02 +
        // MountainCave02, Vendor_BlackForest / Hildir_camp / BogWitch_Camp, the boss altars the World tab
        // lists, the Mistlands mines). Wrong on a modded server? The type list shows the real names and
        // the filter field is editable.
        private static readonly (string Key, string Filter)[] LocfPresets =
        {
            ("locf.preset_all", ""),
            ("locf.preset_crypts", "Crypt"),
            ("locf.preset_caves", "Cave"),
            ("locf.preset_traders", "Vendor,Hildir,BogWitch"),
            ("locf.preset_bosses", "Eikthyrnir,GDKing,Bonemass,Dragonqueen,GoblinKing,DvergrBoss,FaderLocation"),
            ("locf.preset_dungeons", "Crypt,Cave,DvergrTown"),
            ("locf.preset_runestones", "Runestone"),
        };

        // ---- live state ----
        private List<LocfType> _locfTypes;        // null = no reply yet
        private int _locfTypesTotal;
        private List<LocfRow> _locfRows;          // null = no find yet
        private int _locfMatchesTotal;
        private string _locfRowsFilter = "";      // the filter the rows answer (echoed by the server)
        private bool _locfTypesPending, _locfFindPending;
        private bool _locfTypesAsked;
        private string _locfFilter = "";
        private string _locfMaxText = "20";
        private Vector2 _locfTypesScroll, _locfRowsScroll;
        private float _locfNextTypesReq, _locfNextFindReq;
        private float _locfTypesAskedAt, _locfFindAskedAt;   // no-answer detection (older companion?)

        private const float LocfNoAnswerSeconds = 10f;

        // ---- Layout snapshots ----
        private List<LocfType> _locfTypesLayout;
        private List<LocfType> _locfVisibleTypesLayout;   // types matching the filter text, pinned
        private List<LocfRow> _locfRowsLayout;
        private bool _locfTypesPendingLayout, _locfFindPendingLayout, _locfReachableLayout;
        private bool _locfTypesNoAnswerLayout, _locfFindNoAnswerLayout;
        private string _locfRowsFilterLayout = "";
        private int _locfMatchesTotalLayout, _locfTypesTotalLayout;
        private Vector3 _locfOriginLayout;        // the admin's position this frame (distance/direction text)
        private bool _locfHasOriginLayout;

        // ---- lifecycle ----

        internal void LocfInit()
        {
            _locfSectionCfg = Config.Bind("Features", "ShowLocationFinderSection", true,
                "Show the Location Finder section in the Tools tab (nearest crypts, caves, traders, altars, runestones... from the server's location table, with teleport).");
        }

        internal bool LocfSectionEnabled() => _locfSectionCfg == null || _locfSectionCfg.Value;

        internal void LocfReset()
        {
            _locfTypes = null;
            _locfTypesTotal = 0;
            _locfRows = null;
            _locfMatchesTotal = 0;
            _locfRowsFilter = "";
            _locfTypesPending = false;
            _locfFindPending = false;
            _locfTypesAsked = false;
            _locfFilter = "";
            _locfMaxText = "20";
            _locfTypesScroll = Vector2.zero;
            _locfRowsScroll = Vector2.zero;
            _locfNextTypesReq = 0f;
            _locfNextFindReq = 0f;
            _locfTypesAskedAt = 0f;
            _locfFindAskedAt = 0f;
            _locfTypesLayout = null;
            _locfVisibleTypesLayout = null;
            _locfRowsLayout = null;
            _locfTypesPendingLayout = false;
            _locfFindPendingLayout = false;
            _locfTypesNoAnswerLayout = false;
            _locfFindNoAnswerLayout = false;
            _locfReachableLayout = false;
            _locfRowsFilterLayout = "";
            _locfMatchesTotalLayout = 0;
            _locfTypesTotalLayout = 0;
            _locfHasOriginLayout = false;
        }

        // ---- replies ----

        private static void LocfOnTypes(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseLocTypes(self, pkg);
        }

        // Wire: int ver=1 | int total | int n (<=150) | n x { string name, int count, int placed }
        internal static void ParseLocTypes(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var total = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > LocfTypeCap) return;
                var list = new List<LocfType>(n);
                for (var i = 0; i < n; i++)
                    list.Add(new LocfType { Name = pkg.ReadString() ?? "", Count = pkg.ReadInt(), Placed = pkg.ReadInt() });
                self._locfTypes = list;
                self._locfTypesTotal = total;
                self._locfTypesPending = false;
            }
            catch (Exception) { }
        }

        private static void LocfOnFind(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseLocFind(self, pkg);
        }

        // Wire: int ver=1 | string filter | int matchesTotal | int n (<=50) | n x { string name, Vector3 pos, float dist, bool placed }
        internal static void ParseLocFind(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var filter = pkg.ReadString() ?? "";
                var total = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > LocfRowCap) return;
                var rows = new List<LocfRow>(n);
                for (var i = 0; i < n; i++)
                    rows.Add(new LocfRow { Name = pkg.ReadString() ?? "", Pos = pkg.ReadVector3(), Dist = pkg.ReadSingle(), Placed = pkg.ReadBool() });
                self._locfRows = rows;
                self._locfMatchesTotal = total;
                self._locfRowsFilter = filter;
                self._locfFindPending = false;
                self._locfRowsScroll = Vector2.zero;
            }
            catch (Exception) { }
        }

        // ---- requests ----

        // Type list: once per section open (and on Refresh). Layout pass only.
        private void LocfPollTypes()
        {
            if (_locfTypesAsked || !_locfReachableLayout) return;
            if (Time.time < _locfNextTypesReq) return;
            _locfNextTypesReq = Time.time + 5f;
            _locfTypesAsked = true;
            _locfTypesPending = true;
            _locfTypesAskedAt = Time.time;
            SrvRpc("AP_SrvLocTypesReq");
        }

        private void LocfFind()
        {
            if (!TkRequireReachable()) return;
            var lp = LocalPlayer;
            if (lp == null) { Message(Loc.T("locf.msg_no_player")); return; }
            if (Time.time < _locfNextFindReq) return;
            _locfNextFindReq = Time.time + 1f;
            var max = Mathf.Clamp(TkInt(_locfMaxText, 20), 1, LocfRowCap);
            _locfMaxText = TkI(max);
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write((_locfFilter ?? "").Trim());
            pkg.Write(lp.transform.position);
            pkg.Write(max);
            _locfFindPending = true;
            _locfFindAskedAt = Time.time;
            SrvRpc("AP_SrvLocFindReq", pkg);
        }

        // Same any-substring rule the server applies, so the type list previews what Find will match.
        private static List<LocfType> LocfFilterTypes(List<LocfType> types, string filter)
        {
            if (types == null) return null;
            var f = (filter ?? "").Trim();
            if (f.Length == 0) return types;
            var terms = new List<string>();
            foreach (var part in f.Split(',', '|', ';'))
            {
                var t = part.Trim();
                if (t.Length > 0) terms.Add(t);
            }
            if (terms.Count == 0) return types;
            var res = new List<LocfType>();
            foreach (var ty in types)
                foreach (var t in terms)
                    if (ty.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) { res.Add(ty); break; }
            return res;
        }

        // ---- draw ----

        internal void DrawLocationFinderSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _locfReachableLayout = TkReachable();
                LocfPollTypes();
                _locfTypesLayout = _locfTypes;
                _locfTypesTotalLayout = _locfTypesTotal;
                _locfVisibleTypesLayout = LocfFilterTypes(_locfTypes, _locfFilter);
                _locfRowsLayout = _locfRows;
                _locfMatchesTotalLayout = _locfMatchesTotal;
                _locfRowsFilterLayout = _locfRowsFilter;
                _locfTypesPendingLayout = _locfTypesPending;
                _locfFindPendingLayout = _locfFindPending;
                _locfTypesNoAnswerLayout = _locfTypesPending && Time.time - _locfTypesAskedAt > LocfNoAnswerSeconds;
                _locfFindNoAnswerLayout = _locfFindPending && Time.time - _locfFindAskedAt > LocfNoAnswerSeconds;
                var lp = LocalPlayer;
                _locfHasOriginLayout = lp != null;
                _locfOriginLayout = lp != null ? lp.transform.position : Vector3.zero;
            }

            BeginCard(Loc.T("locf.section"));
            GUILayout.Label(Loc.T("locf.hint"), _hintStyle);

            // Preset chips: a click sets the filter text AND fires the lookup (both live-field writes).
            GUILayout.BeginHorizontal();
            for (var i = 0; i < LocfPresets.Length; i++)
            {
                if (GUILayout.Button(Loc.T(LocfPresets[i].Key), _buttonStyle, GUILayout.MinWidth(70)))
                {
                    _locfFilter = LocfPresets[i].Filter;
                    LocfFind();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("locf.filter"), _labelStyle, GUILayout.MinWidth(60));
            _locfFilter = GUILayout.TextField(_locfFilter ?? "", 120, _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("locf.max"), _labelStyle, GUILayout.MinWidth(40));
            _locfMaxText = GUILayout.TextField(_locfMaxText ?? "", 3, _textFieldStyle, GUILayout.Width(40));
            if (GUILayout.Button(Loc.T("locf.find"), _buttonStyle, GUILayout.MinWidth(80))) LocfFind();
            if (GUILayout.Button(Loc.T("locf.refresh_types"), _buttonStyle, GUILayout.MinWidth(110)))
            {
                _locfTypesAsked = false;
                _locfNextTypesReq = 0f;
            }
            GUILayout.EndHorizontal();

            // ---- type list ----
            DrawSection(Loc.T("locf.types_title"));
            var types = _locfTypesLayout;
            if (!_locfReachableLayout)
            {
                GUILayout.Label(Loc.T("locf.not_connected"), _hintStyle);
            }
            else if (types == null)
            {
                // One label, key swap only. A companion older than this panel never answers; say so
                // instead of showing "loading" for the rest of the session.
                var idleKey = _locfTypesNoAnswerLayout ? "locf.types_no_answer"
                            : _locfTypesPendingLayout ? "locf.types_loading"
                            : "locf.types_idle";
                GUILayout.Label(Loc.T(idleKey), _hintStyle);
            }
            else if (types.Count == 0)
            {
                GUILayout.Label(Loc.T("locf.types_none"), _hintStyle);
            }
            else
            {
                var vis = _locfVisibleTypesLayout ?? types;
                GUILayout.Label(Loc.T("locf.types_count", vis.Count, _locfTypesTotalLayout), _dimLabelStyle);
                if (vis.Count == 0)
                {
                    GUILayout.Label(Loc.T("locf.types_nomatch"), _hintStyle);
                }
                else
                {
                    _locfTypesScroll = GUILayout.BeginScrollView(_locfTypesScroll,
                        GUILayout.Height(Mathf.Min(160f, vis.Count * 26f + 16f)));
                    for (var i = 0; i < vis.Count; i++)
                    {
                        var ty = vis[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        // Clicking a type name is the exact-name lookup; the filter field shows what was sent.
                        if (GUILayout.Button(ty.Name, _buttonStyle, GUILayout.Width(260)))
                        {
                            _locfFilter = ty.Name;
                            LocfFind();
                        }
                        GUILayout.Label(Loc.T("locf.type_counts", ty.Count, ty.Placed), _dimCellStyle, GUILayout.MinWidth(120));
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }

            // ---- results ----
            DrawSection(Loc.T("locf.results_title"));
            var rows = _locfRowsLayout;
            if (rows == null)
            {
                var idleKey = _locfFindNoAnswerLayout ? "locf.find_no_answer"
                            : _locfFindPendingLayout ? "locf.find_pending"
                            : "locf.find_idle";
                GUILayout.Label(Loc.T(idleKey), _hintStyle);
            }
            else if (rows.Count == 0)
            {
                GUILayout.Label(Loc.T("locf.find_none", _locfRowsFilterLayout.Length > 0 ? _locfRowsFilterLayout : "*"), _hintStyle);
            }
            else
            {
                GUILayout.Label(Loc.T("locf.find_count", rows.Count, _locfMatchesTotalLayout,
                    _locfRowsFilterLayout.Length > 0 ? _locfRowsFilterLayout : "*"), _dimLabelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("locf.col_name"), _headerStyle, GUILayout.Width(220));
                GUILayout.Label(Loc.T("locf.col_dist"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("locf.col_dir"), _headerStyle, GUILayout.Width(50));
                GUILayout.Label(Loc.T("locf.col_state"), _headerStyle, GUILayout.Width(110));
                GUILayout.EndHorizontal();

                _locfRowsScroll = GUILayout.BeginScrollView(_locfRowsScroll,
                    GUILayout.Height(Mathf.Min(ListView(560f), rows.Count * 28f + 16f)));
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    // Distance/direction are relative to where the admin stands NOW (pinned this frame), so
                    // the numbers stay honest after a teleport; the server's distance only ordered the list.
                    var dist = _locfHasOriginLayout ? TkMapDist(_locfOriginLayout, r.Pos) : r.Dist;
                    var dir = _locfHasOriginLayout ? TkCompass(_locfOriginLayout, r.Pos) : "?";
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Name, _cellStyle, GUILayout.Width(220));
                    GUILayout.Label(Loc.T("locf.meters", TkF(dist)), _cellStyle, GUILayout.Width(80));
                    GUILayout.Label(dir, _cellStyle, GUILayout.Width(50));
                    GUILayout.Label(Loc.T(r.Placed ? "locf.placed" : "locf.unplaced"), _dimCellStyle, GUILayout.Width(110));
                    if (GUILayout.Button(Loc.T("locf.teleport"), _buttonStyle, GUILayout.MinWidth(90)))
                        TkTeleport(r.Pos, 2f, r.Name);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            EndCard();
        }
    }
}

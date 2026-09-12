using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #16 chest viewer and container search (client side) ====================
    // Two cards. (a) Aimed chest: aim the camera at a container, send its ZDOID, the companion decodes the
    // container's item blob on the server and answers with the stacks; remove a stack, set its count, or
    // add an item — every edit refused while a player has the chest open. (b) World search: a prefab or
    // item-name substring, swept over every container in the world (frame-spread; progress polled every
    // 2 s while it runs), the 50 nearest hits with count, container prefab, nearest player and teleport.
    //
    // The ZDOID sent for the aimed container is the one the game itself uses for its items: carts and
    // ships keep their Container on a child whose m_rootObjectOverride points at the root ZNetView.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _chestSectionCfg;
        private bool _chestInited;

        private sealed class ChestRow
        {
            public string Item = "";     // localized
            public string Prefab = "";
            public string Stack = "";
            public string Quality = "";
            public string Crafter = "";
            public int StackN;
        }

        private sealed class ChestData
        {
            public ZDOID Id;
            public bool Found;
            public string Prefab = "";
            public string Name = "";     // localized container name
            public int W, H;
            public bool InUse;
            public int Version, Declared, Reason;
            public List<ChestRow> Rows = new List<ChestRow>();
        }

        private sealed class ChestHit
        {
            public ZDOID Id;
            public Vector3 Pos;
            public string Prefab = "";
            public string Item = "";
            public string Count = "";
            public string Nearest = "";
            public string Dist = "";
        }

        private sealed class ChestSearchData
        {
            public bool Running;
            public int Scanned, Total, Containers, Matched;
            public string Query = "";
            public long Millis;
            public List<ChestHit> Rows = new List<ChestHit>();
        }

        // ---- live state: aimed chest ----
        private bool _chestTargeting;
        private Container _chestTarget;
        private ZDOID _chestTargetId;
        private string _chestTargetLabel = "";
        private ChestData _chestData;
        private bool _chestPending;
        private float _chestNextReq;
        private int _chestSelected = -1;

        // ---- live state: search ----
        private ChestSearchData _chestSearch;
        private bool _chestSearchPending;
        private float _chestSearchNextReq;

        // ---- UI text ----
        private Vector2 _chestScroll, _chestSearchScroll;
        private string _chestCountText = "1";
        private string _chestAddPrefab = "";
        private string _chestAddCount = "1";
        private string _chestAddQuality = "1";
        private string _chestQuery = "";

        // ---- Layout snapshots ----
        private bool _chestTargetingLayout;
        private bool _chestHasTargetLayout;
        private string _chestTargetLabelLayout = "";
        private ChestData _chestDataLayout;
        private bool _chestPendingLayout;
        private int _chestSelectedLayout = -1;
        private ChestSearchData _chestSearchLayout;
        private bool _chestSearchPendingLayout;

        // ---- lifecycle ----

        internal void ChestInit()
        {
            _chestSectionCfg = Config.Bind("Features", "ShowContainersSection", true,
                "Show the Containers section in the Tools tab (view and edit the aimed chest's contents; search every container in the world for an item).");
            _chestInited = true;
        }

        internal bool ChestSectionEnabled() => _chestSectionCfg == null || _chestSectionCfg.Value;

        internal void ChestReset()
        {
            _chestTargeting = false;
            _chestTarget = null;
            _chestTargetId = ZDOID.None;
            _chestTargetLabel = "";
            _chestData = null;
            _chestPending = false;
            _chestNextReq = 0f;
            _chestSelected = -1;
            _chestSearch = null;
            _chestSearchPending = false;
            _chestSearchNextReq = 0f;
            _chestScroll = _chestSearchScroll = Vector2.zero;
            _chestCountText = "1";
            _chestAddPrefab = "";
            _chestAddCount = "1";
            _chestAddQuality = "1";
            _chestQuery = "";
            _chestTargetingLayout = false;
            _chestHasTargetLayout = false;
            _chestTargetLabelLayout = "";
            _chestDataLayout = null;
            _chestPendingLayout = false;
            _chestSelectedLayout = -1;
            _chestSearchLayout = null;
            _chestSearchPendingLayout = false;
        }

        // ---- replies ----

        private static void ChestOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseChestData(self, pkg);
        }

        // AP_ChestData v1: ZDOID id, bool found, prefab, nameToken, int w, int h, bool inUse, int version,
        // int declared, int reason, int shipped(<=100) x (itemToken, itemPrefab, int stack, int quality,
        // int variant, float durability, crafter).
        internal static void ParseChestData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ChestData();
                d.Id = pkg.ReadZDOID();
                d.Found = pkg.ReadBool();
                d.Prefab = pkg.ReadString() ?? "";
                var token = pkg.ReadString() ?? "";
                d.W = pkg.ReadInt();
                d.H = pkg.ReadInt();
                d.InUse = pkg.ReadBool();
                d.Version = pkg.ReadInt();
                d.Declared = pkg.ReadInt();
                d.Reason = pkg.ReadInt();
                d.Name = token.Length > 0 ? LocalizeSafe(token, d.Prefab) : d.Prefab;
                var n = pkg.ReadInt();
                if (n < 0 || n > 100) return;
                for (var i = 0; i < n; i++)
                {
                    var r = new ChestRow();
                    var itemToken = pkg.ReadString() ?? "";
                    r.Prefab = pkg.ReadString() ?? "";
                    r.StackN = pkg.ReadInt();
                    var quality = pkg.ReadInt();
                    pkg.ReadInt();       // variant: not shown
                    pkg.ReadSingle();    // durability: not shown
                    r.Crafter = pkg.ReadString() ?? "";
                    r.Item = itemToken.Length > 0 ? LocalizeSafe(itemToken, r.Prefab) : r.Prefab;
                    r.Stack = r.StackN.ToString(CultureInfo.InvariantCulture);
                    r.Quality = quality.ToString(CultureInfo.InvariantCulture);
                    if (r.Crafter.Length == 0) r.Crafter = "-";
                    d.Rows.Add(r);
                }
                self._chestData = d;
                self._chestPending = false;
                if (self._chestSelected >= d.Rows.Count) self._chestSelected = -1;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        private static void ChestOnSearch(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseChestSearch(self, pkg);
        }

        // AP_ChestSearch v1: bool running, int scanned, int total, int containers, int matched, query,
        // int shipped(<=50) x (ZDOID, prefab, Vector3 pos, int count, sampleToken, nearest, float dist), long millis.
        internal static void ParseChestSearch(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ChestSearchData
                {
                    Running = pkg.ReadBool(),
                    Scanned = pkg.ReadInt(),
                    Total = pkg.ReadInt(),
                    Containers = pkg.ReadInt(),
                    Matched = pkg.ReadInt(),
                    Query = pkg.ReadString() ?? "",
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 50) return;
                for (var i = 0; i < n; i++)
                {
                    var r = new ChestHit();
                    r.Id = pkg.ReadZDOID();
                    r.Prefab = pkg.ReadString() ?? "";
                    r.Pos = pkg.ReadVector3();
                    var count = pkg.ReadInt();
                    var sample = pkg.ReadString() ?? "";
                    r.Nearest = pkg.ReadString() ?? "";
                    var dist = pkg.ReadSingle();
                    r.Item = sample.StartsWith("$", StringComparison.Ordinal) ? LocalizeSafe(sample, sample) : sample;
                    r.Count = count.ToString(CultureInfo.InvariantCulture);
                    r.Dist = ObjDist(dist);
                    if (r.Nearest.Length == 0) r.Nearest = "-";
                    d.Rows.Add(r);
                }
                d.Millis = pkg.ReadLong();
                self._chestSearch = d;
                self._chestSearchPending = d.Running;
            }
            catch (Exception) { }
        }

        // ---- tick (LateUpdate): aim ----

        internal void ChestTick()
        {
            if (!_chestInited) return;
            if (!FeaturesEnabled() || !ChestSectionEnabled()) { _chestTarget = null; return; }
            try
            {
                if (LocalPlayer == null || !_chestTargeting) { _chestTarget = null; _chestTargetId = ZDOID.None; return; }
                var c = ObjAim<Container>(null);
                _chestTarget = c;
                var view = ChestViewOf(c);
                if (view == null)
                {
                    _chestTargetId = ZDOID.None;
                    _chestTargetLabel = "";
                    return;
                }
                _chestTargetId = view.GetZDO().m_uid;
                _chestTargetLabel = Loc.T("chest.target_line", LocalizeSafe(c.m_name ?? "", ObjPrefabName(view.gameObject)), ObjPrefabName(view.gameObject));
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Container viewer tick failed (feature degraded, panel unaffected): {e.Message}");
            }
        }

        // The view that carries the container's ZDO: the root override when the container is a child
        // (carts, ships), else the container's own view.
        private static ZNetView ChestViewOf(Container c)
        {
            if (c == null) return null;
            var view = c.m_rootObjectOverride != null ? c.m_rootObjectOverride : c.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view : null;
        }

        // ---- requests ----

        private void ChestRead()
        {
            if (_chestTargetId.IsNone()) { Message(Loc.T("chest.msg_no_target")); return; }
            if (!ObjReachable()) return;
            if (Time.time < _chestNextReq) return;
            _chestNextReq = Time.time + 1f;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(_chestTargetId);
            SrvRpc("AP_SrvChestReadReq", pkg);
            _chestPending = true;
            _chestSelected = -1;
        }

        // op: 0 remove index, 1 set count, 2 add item. The server answers with a refreshed AP_ChestData
        // (and a toast when it refused).
        // expectPrefab/expectStack describe the row the admin SAW at `index`. The server refuses the edit
        // when that slot now holds something else (a player took a stack, an "Add item" landed in an earlier
        // free slot), because InUse only guards a chest that is open right now, not one that changed since
        // the last read. "" / -1 = no row involved (add item).
        private void ChestEdit(ChestData d, int op, int index, int count, string prefab, int quality,
            string expectPrefab = "", int expectStack = -1)
        {
            if (d == null || !d.Found) { Message(Loc.T("chest.msg_read_first")); return; }
            if (d.Reason != 0) { Message(Loc.T(ChestReasonKey(d.Reason))); return; }
            if (!ObjReachable()) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(d.Id);
            pkg.Write(op);
            pkg.Write(index);
            pkg.Write(count);
            pkg.Write(prefab ?? "");
            pkg.Write(quality);
            pkg.Write(expectPrefab ?? "");
            pkg.Write(expectStack);
            SrvRpc("AP_SrvChestEdit", pkg);
            _chestPending = true;
        }

        private static string ChestReasonKey(int reason)
        {
            switch (reason)
            {
                case 0: return "chest.reason_ok";
                case 1: return "chest.reason_inuse";
                case 2: return "chest.reason_disabled";
                case 3: return "chest.reason_unreadable";
                case 4: return "chest.reason_newer";
                default: return "chest.reason_missing";
            }
        }

        private void ChestSearchPoll()
        {
            var d = _chestSearchLayout;
            if (d == null || !d.Running || !ObjCanPoll()) return;
            if (Time.time < _chestSearchNextReq) return;
            _chestSearchNextReq = Time.time + 2f;
            ChestSearchSend(false);
        }

        private void ChestSearchSend(bool startNew)
        {
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write((_chestQuery ?? "").Trim());
            pkg.Write(LocalPlayer != null ? LocalPlayer.transform.position : Vector3.zero);
            pkg.Write(startNew);
            SrvRpc("AP_SrvChestSearchReq", pkg);
        }

        private void ChestSearchClick()
        {
            var q = (_chestQuery ?? "").Trim();
            if (q.Length < 2) { Message(Loc.T("chest.msg_query_short")); return; }
            if (!ObjReachable()) return;
            if (Time.time < _chestSearchNextReq) return;
            _chestSearchNextReq = Time.time + 2f;
            _chestSearchPending = true;
            ChestSearchSend(true);
            Message(Loc.T("chest.msg_search", q));
        }

        private static ChestRow ChestRowAt(ChestData d, int index) =>
            d != null && index >= 0 && index < d.Rows.Count ? d.Rows[index] : null;

        // ---- draw ----

        internal void DrawContainersSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _chestTargetingLayout = _chestTargeting;
                _chestHasTargetLayout = _chestTargeting && !_chestTargetId.IsNone();
                _chestTargetLabelLayout = _chestTargetLabel ?? "";
                _chestDataLayout = _chestData;
                _chestPendingLayout = _chestPending;
                _chestSelectedLayout = _chestSelected;
                _chestSearchLayout = _chestSearch;
                _chestSearchPendingLayout = _chestSearchPending;
                ChestSearchPoll();
            }

            ChestDrawAimedCard();
            ChestDrawSearchCard();
        }

        private void ChestDrawAimedCard()
        {
            var d = _chestDataLayout;
            BeginCard(Loc.T("chest.aim_section"));

            GUILayout.BeginHorizontal();
            var targeting = GUILayout.Toggle(_chestTargetingLayout, " " + Loc.T("chest.targeting"), _toggleStyle);
            if (targeting != _chestTargetingLayout) _chestTargeting = targeting;
            GUILayout.Space(12);
            GUILayout.Label(_chestHasTargetLayout ? _chestTargetLabelLayout : Loc.T(_chestTargetingLayout ? "chest.target_none" : "chest.target_off"),
                _cellStyle, GUILayout.MinWidth(220));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("chest.read"), _buttonStyle, GUILayout.MinWidth(90))) ChestRead();
            GUILayout.EndHorizontal();

            // One status label on every path: nothing read yet / pending / not a container / the chest header.
            string status;
            if (d == null) status = Loc.T(_chestPendingLayout ? "chest.status_pending" : "chest.status_idle");
            else if (!d.Found) status = Loc.T("chest.status_missing");
            else status = Loc.T("chest.status_line", d.Name, d.Prefab, d.Rows.Count, d.W * d.H, Loc.T(ChestReasonKey(d.Reason)));
            GUILayout.Label(status, d != null && d.Found && d.Reason == 0 ? _labelStyle : _dimLabelStyle);

            if (d == null || !d.Found || d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T(d == null ? "chest.empty_idle" : !d.Found ? "chest.empty_missing" : d.Reason == 3 || d.Reason == 4 ? "chest.empty_unreadable" : "chest.empty_none"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("", _headerStyle, GUILayout.Width(22));
                GUILayout.Label(Loc.T("chest.col_item"), _headerStyle, GUILayout.Width(200));
                GUILayout.Label(Loc.T("chest.col_prefab"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("chest.col_stack"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("chest.col_quality"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("chest.col_crafter"), _headerStyle, GUILayout.MinWidth(100));
                GUILayout.EndHorizontal();

                _chestScroll = GUILayout.BeginScrollView(_chestScroll,
                    GUILayout.Height(Mathf.Min(ListView(560f), d.Rows.Count * 26f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    var wasOn = _chestSelectedLayout == i;
                    var on = GUILayout.Toggle(wasOn, "", _toggleStyle, GUILayout.Width(22));
                    if (on && !wasOn) { _chestSelected = i; _chestCountText = r.Stack; }
                    GUILayout.Label(r.Item, wasOn ? _cellStyle : _dimCellStyle, GUILayout.Width(200));
                    GUILayout.Label(r.Prefab, _dimCellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.Stack, _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(r.Quality, _dimCellStyle, GUILayout.Width(60));
                    GUILayout.Label(r.Crafter, _dimCellStyle, GUILayout.MinWidth(100));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            DrawSection(Loc.T("chest.edit_title"));
            var sel = ChestRowAt(d, _chestSelectedLayout);
            GUILayout.BeginHorizontal();
            GUILayout.Label(sel != null ? Loc.T("chest.selected", sel.Item, sel.Stack) : Loc.T("chest.selected_none"), _labelStyle, GUILayout.MinWidth(200));
            GUILayout.Label(Loc.T("chest.count"), _labelStyle, GUILayout.MinWidth(50));
            _chestCountText = GUILayout.TextField(_chestCountText ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            if (GUILayout.Button(Loc.T("chest.set_count"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                if (sel == null) Message(Loc.T("chest.msg_select_first"));
                else ChestEdit(d, 1, _chestSelectedLayout, Mathf.Clamp(ObjParseInt(_chestCountText, sel.StackN), 1, 100000), "", 1, sel.Prefab, sel.StackN);
            }
            if (ConfirmButton("ChestRemove", Loc.T("chest.remove_stack"), GUILayout.MinWidth(110)))
            {
                if (sel == null) Message(Loc.T("chest.msg_select_first"));
                else ChestEdit(d, 0, _chestSelectedLayout, 0, "", 1, sel.Prefab, sel.StackN);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("chest.add_prefab"), _labelStyle, GUILayout.MinWidth(90));
            _chestAddPrefab = GUILayout.TextField(_chestAddPrefab ?? "", 64, _textFieldStyle, GUILayout.MinWidth(160));
            GUILayout.Label(Loc.T("chest.count"), _labelStyle, GUILayout.MinWidth(50));
            _chestAddCount = GUILayout.TextField(_chestAddCount ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("chest.quality"), _labelStyle, GUILayout.MinWidth(50));
            _chestAddQuality = GUILayout.TextField(_chestAddQuality ?? "", 2, _textFieldStyle, GUILayout.Width(30));
            if (GUILayout.Button(Loc.T("chest.add"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                var prefab = (_chestAddPrefab ?? "").Trim();
                if (prefab.Length == 0) Message(Loc.T("chest.msg_add_no_prefab"));
                else ChestEdit(d, 2, -1, Mathf.Clamp(ObjParseInt(_chestAddCount, 1), 1, 100000), prefab, Mathf.Clamp(ObjParseInt(_chestAddQuality, 1), 1, 10));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("chest.edit_hint"), _hintStyle);

            EndCard();
        }

        private void ChestDrawSearchCard()
        {
            var d = _chestSearchLayout;
            BeginCard(Loc.T("chest.search_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("chest.query"), _labelStyle, GUILayout.MinWidth(60));
            _chestQuery = GUILayout.TextField(_chestQuery ?? "", 64, _textFieldStyle, GUILayout.MinWidth(200));
            if (GUILayout.Button(Loc.T("chest.search"), _buttonStyle, GUILayout.MinWidth(90))) ChestSearchClick();
            GUILayout.FlexibleSpace();
            string status;
            if (d == null) status = Loc.T(_chestSearchPendingLayout ? "chest.search_pending" : "chest.search_idle");
            else if (d.Running) status = Loc.T("chest.search_running", d.Scanned, d.Total, d.Containers);
            else status = Loc.T("chest.search_done", d.Matched, d.Containers, d.Millis);
            GUILayout.Label(status, _dimLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("chest.search_hint"), _hintStyle);

            if (d == null || d.Rows.Count == 0)
            {
                var key = d == null
                    ? (_chestSearchPendingLayout ? "chest.search_empty_pending" : "chest.search_empty_idle")
                    : (d.Running ? "chest.search_empty_running" : "chest.search_empty_none");
                GUILayout.Label(Loc.T(key), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("chest.col_item"), _headerStyle, GUILayout.Width(180));
                GUILayout.Label(Loc.T("chest.col_count"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("chest.col_container"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("chest.col_dist"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("chest.col_nearest"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label("", _headerStyle, GUILayout.MinWidth(44));
                GUILayout.EndHorizontal();

                _chestSearchScroll = GUILayout.BeginScrollView(_chestSearchScroll,
                    GUILayout.Height(Mathf.Min(ListView(560f), d.Rows.Count * 26f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Item, _cellStyle, GUILayout.Width(180));
                    GUILayout.Label(r.Count, _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(r.Prefab, _dimCellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.Dist, _dimCellStyle, GUILayout.Width(70));
                    GUILayout.Label(r.Nearest, _dimCellStyle, GUILayout.Width(120));
                    if (GUILayout.Button(Loc.T("chest.tp"), _buttonStyle, GUILayout.MinWidth(44))) ObjTeleport(r.Pos, r.Prefab);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            EndCard();
        }
    }
}

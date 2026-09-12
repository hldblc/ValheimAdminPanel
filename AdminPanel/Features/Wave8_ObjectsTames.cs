using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #10 tame roster and pet manager (client side) ====================
    // Scan button -> the companion sweeps the world for tamed creatures (frame-spread, so the panel polls
    // progress every 2 s while it runs) -> a table sorted by distance to where the admin stood, with
    // teleport / heal / rename / un-tame / cull per row and a "cull this species within a radius" tool for
    // breeding pens. Every action is re-validated on the server against the live ZDO; the row list here is
    // only a snapshot of what the last scan saw.
    //
    // IMGUI discipline: replies land during ZNet.Update and clicks during the event pass, so the draw code
    // reads ONLY the *Layout snapshots pinned at the top of DrawTameRosterSection. Rows removed by an
    // action are dropped by REPLACING the payload object (never by mutating the list a snapshot may hold).
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _tameSectionCfg;

        private sealed class TameRow
        {
            public ZDOID Id;
            public Vector3 Pos;
            public string Prefab = "";
            public string Species = "";   // localized creature name
            public string Pet = "";       // TamedName as stored (may be empty)
            public string Display = "";   // pet name, else the species
            public string Stars = "";
            public string Health = "";
            public string Dist = "";
            public string Owner = "";
            // ConfirmButton id keyed on the ZDOID, never on the row index: the roster is re-sorted and
            // regrown by every progress reply while a scan runs, so an armed "Confirm?" keyed on the index
            // would migrate to whatever animal now sits in that row. Cached so the draw allocates nothing.
            private string _cullId;
            public string CullId => _cullId ?? (_cullId = "TameCull#" + Id);
        }

        private sealed class TameData
        {
            public bool Running;
            public int Scanned, Total, Found;
            public long Millis;
            public List<TameRow> Rows = new List<TameRow>();
        }

        // ---- live state (RPC handler + clicks write; the draw never reads these directly) ----
        private TameData _tameData;
        private bool _tamePending;        // Scan pressed, no reply yet
        private float _tameNextReq;
        private int _tameSelected = -1;   // index into _tameData.Rows

        // ---- UI text state (edited during the event pass; text only, never a control count) ----
        private Vector2 _tameScroll;
        private string _tameRename = "";
        private string _tameCullPrefab = "";
        private string _tameCullRadius = "32";

        // ---- Layout snapshots ----
        private TameData _tameDataLayout;
        private bool _tamePendingLayout;
        private int _tameSelectedLayout = -1;

        // ---- lifecycle ----

        internal void TameInit()
        {
            _tameSectionCfg = Config.Bind("Features", "ShowTameRosterSection", true,
                "Show the Tame Roster section in the Tools tab (world-wide list of tamed creatures with teleport, heal, rename, un-tame and cull).");
        }

        internal bool TameSectionEnabled() => _tameSectionCfg == null || _tameSectionCfg.Value;

        internal void TameReset()
        {
            _tameData = null;
            _tamePending = false;
            _tameNextReq = 0f;
            _tameSelected = -1;
            _tameScroll = Vector2.zero;
            _tameRename = "";
            _tameCullPrefab = "";
            _tameCullRadius = "32";
            _tameDataLayout = null;
            _tamePendingLayout = false;
            _tameSelectedLayout = -1;
        }

        // ---- reply ----

        private static void TameOnRoster(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseTameRoster(self, pkg);
        }

        // AP_TameRoster v1: bool running, int scanned, int total, int found, int shipped(<=100) x
        // (ZDOID, prefab, nameToken, petName, owner, int level, float health, float max, Vector3 pos, float dist),
        // long millis. Row strings are formatted here, once, so the draw loop allocates nothing.
        internal static void ParseTameRoster(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new TameData
                {
                    Running = pkg.ReadBool(),
                    Scanned = pkg.ReadInt(),
                    Total = pkg.ReadInt(),
                    Found = pkg.ReadInt(),
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 100) return;
                for (var i = 0; i < n; i++)
                {
                    var r = new TameRow();
                    r.Id = pkg.ReadZDOID();
                    r.Prefab = pkg.ReadString() ?? "";
                    var token = pkg.ReadString() ?? "";
                    r.Pet = pkg.ReadString() ?? "";
                    r.Owner = pkg.ReadString() ?? "";
                    var level = pkg.ReadInt();
                    var health = pkg.ReadSingle();
                    var max = pkg.ReadSingle();
                    r.Pos = pkg.ReadVector3();
                    var dist = pkg.ReadSingle();
                    r.Species = token.Length > 0 ? LocalizeSafe(token, r.Prefab) : r.Prefab;
                    r.Display = r.Pet.Length > 0 ? r.Pet : r.Species;
                    r.Stars = ObjStars(level);
                    r.Health = ObjHealth(health, max);
                    r.Dist = ObjDist(dist);
                    if (r.Owner.Length == 0) r.Owner = "?";
                    d.Rows.Add(r);
                }
                d.Millis = pkg.ReadLong();
                self._tameData = d;
                self._tamePending = d.Running;
                if (self._tameSelected >= d.Rows.Count) self._tameSelected = -1;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        // Layout-gated progress poll: only while a scan is actually running, throttle-first, silent.
        private void TamePoll()
        {
            var d = _tameDataLayout;
            if (d == null || !d.Running || !ObjCanPoll()) return;
            if (Time.time < _tameNextReq) return;
            _tameNextReq = Time.time + 2f;
            TameSend(false);
        }

        private void TameSend(bool startNew)
        {
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(startNew);
            pkg.Write(LocalPlayer != null ? LocalPlayer.transform.position : Vector3.zero);
            pkg.Write(100);
            SrvRpc("AP_SrvTameScanReq", pkg);
        }

        private void TameScanClick()
        {
            if (!ObjReachable()) return;
            if (Time.time < _tameNextReq) return;            // throttle-first, so a held button cannot spam
            _tameNextReq = Time.time + 2f;
            _tamePending = true;
            TameSend(true);
            Message(Loc.T("tame.msg_scan"));
        }

        // action: 0 heal, 1 rename, 2 untame, 3 cull. Un-tamed and culled rows leave the table at once;
        // the server's own answer arrives as a toast.
        private void TameAction(int action, TameRow r, string text)
        {
            if (r == null) { Message(Loc.T("tame.msg_select_first")); return; }
            if (!ObjReachable()) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(action);
            pkg.Write(r.Id);
            pkg.Write(text ?? "");
            SrvRpc("AP_SrvTameAction", pkg);
            if (action == 2 || action == 3) TameDropRow(r);
            else if (action == 1) Message(Loc.T("tame.msg_rename_sent", r.Display, text ?? ""));
        }

        private void TameCullArea()
        {
            var prefab = (_tameCullPrefab ?? "").Trim();
            if (prefab.Length == 0) { Message(Loc.T("tame.msg_cull_no_species")); return; }
            if (!ObjReachable()) return;
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("tame.msg_not_in_world")); return; }
            var radius = Mathf.Clamp(ObjParseFloat(_tameCullRadius, 32f), 1f, 128f);
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(prefab);
            pkg.Write(player.transform.position);
            pkg.Write(radius);
            SrvRpc("AP_SrvTameCullArea", pkg);
            Message(Loc.T("tame.msg_cull_sent", prefab, ObjDist(radius)));
            // The rows of that species near us are about to go; a fresh scan is the honest picture.
            _tameData = null;
            _tameSelected = -1;
        }

        // Replace the payload rather than editing the list a Layout snapshot may still reference.
        private void TameDropRow(TameRow r)
        {
            var d = _tameData;
            if (d == null || r == null) return;
            var nd = new TameData { Running = d.Running, Scanned = d.Scanned, Total = d.Total, Found = Math.Max(0, d.Found - 1), Millis = d.Millis };
            var removedIndex = -1;
            for (var i = 0; i < d.Rows.Count; i++)
            {
                if (ReferenceEquals(d.Rows[i], r)) { removedIndex = i; continue; }
                nd.Rows.Add(d.Rows[i]);
            }
            _tameData = nd;
            if (_tameSelected == removedIndex) _tameSelected = -1;
            else if (removedIndex >= 0 && _tameSelected > removedIndex) _tameSelected--;
        }

        private void TameSelect(int index, TameRow r)
        {
            _tameSelected = index;
            _tameRename = r.Pet;
            _tameCullPrefab = r.Prefab;
        }

        private static TameRow TameRowAt(TameData d, int index) =>
            d != null && index >= 0 && index < d.Rows.Count ? d.Rows[index] : null;

        // ---- draw ----

        internal void DrawTameRosterSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _tameDataLayout = _tameData;
                _tamePendingLayout = _tamePending;
                _tameSelectedLayout = _tameSelected;
                TamePoll();
            }
            var d = _tameDataLayout;

            BeginCard(Loc.T("tame.section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("tame.scan"), _buttonStyle, GUILayout.MinWidth(100))) TameScanClick();
            GUILayout.FlexibleSpace();
            // Exactly one status label on every path - the text changes, the control count does not.
            string status;
            if (d == null) status = Loc.T(_tamePendingLayout ? "tame.status_pending" : "tame.status_idle");
            else if (d.Running) status = Loc.T("tame.status_running", d.Scanned, d.Total);
            else status = Loc.T("tame.status_done", d.Found, d.Rows.Count, d.Millis);
            GUILayout.Label(status, _dimLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("tame.hint"), _hintStyle);

            if (d == null || d.Rows.Count == 0)
            {
                // Three honest empty states, one label: nothing asked yet / scan in flight / world has none.
                var key = d == null
                    ? (_tamePendingLayout ? "tame.empty_pending" : "tame.empty_idle")
                    : (d.Running ? "tame.empty_running" : "tame.empty_none");
                GUILayout.Label(Loc.T(key), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("", _headerStyle, GUILayout.Width(22));
                GUILayout.Label(Loc.T("tame.col_name"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("tame.col_species"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("tame.col_stars"), _headerStyle, GUILayout.Width(45));
                GUILayout.Label(Loc.T("tame.col_health"), _headerStyle, GUILayout.Width(95));
                GUILayout.Label(Loc.T("tame.col_dist"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("tame.col_owner"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("tame.col_actions"), _headerStyle, GUILayout.MinWidth(120));
                GUILayout.EndHorizontal();

                _tameScroll = GUILayout.BeginScrollView(_tameScroll,
                    GUILayout.Height(Mathf.Min(ListView(520f), d.Rows.Count * 28f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    // Selection toggle: act only on an off->on FLIP (the selected row reports true on every pass).
                    var wasOn = _tameSelectedLayout == i;
                    var on = GUILayout.Toggle(wasOn, "", _toggleStyle, GUILayout.Width(22));
                    if (on && !wasOn) TameSelect(i, r);
                    GUILayout.Label(r.Display, wasOn ? _cellStyle : _dimCellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.Species, _dimCellStyle, GUILayout.Width(110));
                    GUILayout.Label(r.Stars, _cellStyle, GUILayout.Width(45));
                    GUILayout.Label(r.Health, _dimCellStyle, GUILayout.Width(95));
                    GUILayout.Label(r.Dist, _dimCellStyle, GUILayout.Width(70));
                    GUILayout.Label(r.Owner, _dimCellStyle, GUILayout.Width(110));
                    if (GUILayout.Button(Loc.T("tame.tp"), _buttonStyle, GUILayout.MinWidth(44))) ObjTeleport(r.Pos, r.Display);
                    if (GUILayout.Button(Loc.T("tame.heal"), _buttonStyle, GUILayout.MinWidth(52))) TameAction(0, r, "");
                    if (GUILayout.Button(Loc.T("tame.untame"), _buttonStyle, GUILayout.MinWidth(70))) TameAction(2, r, "");
                    if (ConfirmButton(r.CullId, Loc.T("tame.cull"), GUILayout.MinWidth(70))) TameAction(3, r, "");
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            // Selected-row tools: always drawn, only the text swaps with the selection.
            DrawSection(Loc.T("tame.selected_title"));
            var sel = TameRowAt(d, _tameSelectedLayout);
            GUILayout.Label(sel != null ? Loc.T("tame.selected", sel.Display, sel.Species, ObjPos(sel.Pos)) : Loc.T("tame.selected_none"),
                _labelStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tame.rename_label"), _labelStyle, GUILayout.MinWidth(90));
            _tameRename = GUILayout.TextField(_tameRename ?? "", 40, _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("tame.rename"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var name = (_tameRename ?? "").Trim();
                if (name.Length == 0) Message(Loc.T("tame.msg_rename_empty"));
                else TameAction(1, sel, name);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("tame.cull_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tame.cull_species"), _labelStyle, GUILayout.MinWidth(90));
            _tameCullPrefab = GUILayout.TextField(_tameCullPrefab ?? "", 64, _textFieldStyle, GUILayout.MinWidth(140));
            GUILayout.Label(Loc.T("tame.cull_radius"), _labelStyle, GUILayout.MinWidth(60));
            _tameCullRadius = GUILayout.TextField(_tameCullRadius ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            if (ConfirmButton("TameCullArea", Loc.T("tame.cull_area"), GUILayout.MinWidth(150))) TameCullArea();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("tame.cull_hint"), _hintStyle);

            EndCard();
        }
    }
}

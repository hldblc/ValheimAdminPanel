using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #12 spawner and nest manager (client side) ====================
    // Ask the companion for every spawner (SpawnArea nest or CreatureSpawner) in a 1 / 2 / 4-zone block
    // around the admin or around typed coordinates; rows show what it spawns, how far it is and (for
    // creature spawners) when its spawn was last seen alive. Teleport to it, remove one, or remove
    // everything listed (capped at 200, spread over frames on the server). The server re-checks every id
    // against the component test before deleting, so a stale row can never remove something else.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _nestSectionCfg;

        private sealed class NestRow
        {
            public ZDOID Id;
            public Vector3 Pos;
            public string Prefab = "";
            public string Kind = "";
            public string Spawns = "";
            public string Dist = "";
            public string Alive = "";
            // ConfirmButton id keyed on the ZDOID (see TameRow.CullId): a new scan replaces the list, and an
            // armed "Confirm?" keyed on the index would fire on a different spawner.
            private string _removeId;
            public string RemoveId => _removeId ?? (_removeId = "NestRemove#" + Id);
        }

        private sealed class NestData
        {
            public int Found;
            public List<NestRow> Rows = new List<NestRow>();
        }

        // ---- live state ----
        private NestData _nestData;
        private bool _nestPending;
        private float _nestNextReq;
        private int _nestZones = 1;

        // ---- UI text ----
        private Vector2 _nestScroll;
        private string _nestX = "0";
        private string _nestZ = "0";

        // ---- Layout snapshots ----
        private NestData _nestDataLayout;
        private bool _nestPendingLayout;
        private int _nestZonesLayout = 1;

        // ---- lifecycle ----

        internal void NestInit()
        {
            _nestSectionCfg = Config.Bind("Features", "ShowSpawnersSection", true,
                "Show the Spawners section in the Tools tab (list creature spawners and nests around a point; teleport to or remove them).");
        }

        internal bool NestSectionEnabled() => _nestSectionCfg == null || _nestSectionCfg.Value;

        internal void NestReset()
        {
            _nestData = null;
            _nestPending = false;
            _nestNextReq = 0f;
            _nestZones = 1;
            _nestScroll = Vector2.zero;
            _nestX = "0";
            _nestZ = "0";
            _nestDataLayout = null;
            _nestPendingLayout = false;
            _nestZonesLayout = 1;
        }

        // ---- reply ----

        private static void NestOnList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseSpawnerList(self, pkg);
        }

        // AP_SpawnerList v1: int found, int shipped(<=100) x (ZDOID, prefab, int kind, spawns, Vector3 pos,
        // float dist, long aliveTicks, float respawnMinutes, bool spawnedAlive).
        internal static void ParseSpawnerList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new NestData { Found = pkg.ReadInt() };
                var n = pkg.ReadInt();
                if (n < 0 || n > 100) return;
                for (var i = 0; i < n; i++)
                {
                    var r = new NestRow();
                    r.Id = pkg.ReadZDOID();
                    r.Prefab = pkg.ReadString() ?? "";
                    var kind = pkg.ReadInt();
                    r.Spawns = pkg.ReadString() ?? "";
                    r.Pos = pkg.ReadVector3();
                    var dist = pkg.ReadSingle();
                    var aliveTicks = pkg.ReadLong();
                    var respawn = pkg.ReadSingle();
                    var alive = pkg.ReadBool();
                    r.Kind = Loc.T(kind == 1 ? "nest.kind_spawner" : "nest.kind_nest");
                    r.Dist = ObjDist(dist);
                    if (r.Spawns.Length == 0) r.Spawns = "-";
                    if (kind == 1)
                    {
                        // A one-shot spawner (respawn 0) that already spawned is "spent" until its creature dies.
                        r.Alive = alive ? Loc.T("nest.alive_yes")
                            : aliveTicks > 0L ? Loc.T(respawn > 0f ? "nest.alive_respawns" : "nest.alive_spent", respawn.ToString("0", CultureInfo.InvariantCulture))
                            : Loc.T("nest.alive_never");
                    }
                    else r.Alive = Loc.T("nest.alive_nest");
                    d.Rows.Add(r);
                }
                self._nestData = d;
                self._nestPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        private void NestScan(Vector3 center)
        {
            if (!ObjReachable()) return;
            if (Time.time < _nestNextReq) return;            // throttle-first, so a held button cannot spam
            _nestNextReq = Time.time + 2f;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(center);
            pkg.Write(_nestZones);
            SrvRpc("AP_SrvSpawnerScanReq", pkg);
            _nestPending = true;
            Message(Loc.T("nest.msg_scan", ObjPos(center)));
        }

        private void NestScanHere()
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("tame.msg_not_in_world")); return; }
            NestScan(player.transform.position);
        }

        // Typed X/Z; Y is irrelevant for a zone block (ZoneSystem.GetZone ignores it) and is set to the
        // admin's own height only so the distance column stays sensible.
        private void NestScanAt()
        {
            float x, z;
            if (!float.TryParse((_nestX ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse((_nestZ ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            { Message(Loc.T("nest.msg_bad_coords")); return; }
            var y = LocalPlayer != null ? LocalPlayer.transform.position.y : 30f;
            NestScan(new Vector3(x, y, z));
        }

        private void NestRemove(List<NestRow> rows)
        {
            if (rows == null || rows.Count == 0) { Message(Loc.T("nest.msg_nothing")); return; }
            if (!ObjReachable()) return;
            var n = Math.Min(rows.Count, 200);
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(n);
            for (var i = 0; i < n; i++) pkg.Write(rows[i].Id);
            SrvRpc("AP_SrvSpawnerRemove", pkg);
            Message(Loc.T("nest.msg_remove_sent", n));
            // Drop the removed rows by REPLACING the payload (a Layout snapshot may still hold the old list).
            var d = _nestData;
            if (d == null) return;
            var nd = new NestData { Found = Math.Max(0, d.Found - n) };
            var gone = new HashSet<ZDOID>();
            for (var i = 0; i < n; i++) gone.Add(rows[i].Id);
            for (var i = 0; i < d.Rows.Count; i++) if (!gone.Contains(d.Rows[i].Id)) nd.Rows.Add(d.Rows[i]);
            _nestData = nd;
        }

        private static readonly List<NestRow> NestOne = new List<NestRow>(1);
        private static readonly int[] NestZoneChoices = { 1, 2, 4 };   // no per-pass array allocation in the draw

        private void NestRemoveOne(NestRow r)
        {
            NestOne.Clear();
            NestOne.Add(r);
            NestRemove(NestOne);
            NestOne.Clear();
        }

        // ---- draw ----

        internal void DrawSpawnersSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _nestDataLayout = _nestData;
                _nestPendingLayout = _nestPending;
                _nestZonesLayout = _nestZones;
            }
            var d = _nestDataLayout;

            BeginCard(Loc.T("nest.section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("nest.zones"), _labelStyle, GUILayout.MinWidth(90));
            foreach (var z in NestZoneChoices)
            {
                // Chips: act only on an off->on FLIP.
                var wasOn = _nestZonesLayout == z;
                var on = GUILayout.Toggle(wasOn, Loc.T("nest.zone_chip", z, z * 64 + 32), _chipStyleOrButton(), GUILayout.MinWidth(70));
                if (on && !wasOn) _nestZones = z;
            }
            GUILayout.Space(12);
            if (GUILayout.Button(Loc.T("nest.scan_here"), _buttonStyle, GUILayout.MinWidth(120))) NestScanHere();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("nest.axis_x"), _labelStyle, GUILayout.Width(20));
            _nestX = GUILayout.TextField(_nestX ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T("nest.axis_z"), _labelStyle, GUILayout.Width(20));
            _nestZ = GUILayout.TextField(_nestZ ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            if (GUILayout.Button(Loc.T("nest.scan_at"), _buttonStyle, GUILayout.MinWidth(150))) NestScanAt();
            GUILayout.FlexibleSpace();
            // One status label on every path.
            GUILayout.Label(d == null
                    ? Loc.T(_nestPendingLayout ? "nest.status_pending" : "nest.status_idle")
                    : Loc.T("nest.status_done", d.Found, d.Rows.Count),
                _dimLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("nest.hint"), _hintStyle);

            if (d == null || d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T(d == null ? (_nestPendingLayout ? "nest.empty_pending" : "nest.empty_idle") : "nest.empty_none"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("nest.col_prefab"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("nest.col_kind"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("nest.col_spawns"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("nest.col_dist"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("nest.col_alive"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("nest.col_actions"), _headerStyle, GUILayout.MinWidth(100));
                GUILayout.EndHorizontal();

                _nestScroll = GUILayout.BeginScrollView(_nestScroll,
                    GUILayout.Height(Mathf.Min(ListView(430f), d.Rows.Count * 28f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Prefab, _cellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.Kind, _dimCellStyle, GUILayout.Width(80));
                    GUILayout.Label(r.Spawns, _dimCellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.Dist, _dimCellStyle, GUILayout.Width(70));
                    GUILayout.Label(r.Alive, _dimCellStyle, GUILayout.Width(130));
                    if (GUILayout.Button(Loc.T("nest.tp"), _buttonStyle, GUILayout.MinWidth(44))) ObjTeleport(r.Pos, r.Prefab);
                    if (ConfirmButton(r.RemoveId, Loc.T("nest.remove"), GUILayout.MinWidth(80))) NestRemoveOne(r);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.BeginHorizontal();
            if (ConfirmButton("NestRemoveAll", Loc.T("nest.remove_all"), GUILayout.MinWidth(170)))
            {
                if (d == null || d.Rows.Count == 0) Message(Loc.T("nest.msg_nothing"));
                else NestRemove(d.Rows);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("nest.remove_hint"), _hintStyle);

            EndCard();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #6 admin map overlays (server snapshot) ====================
    // One request, one frame-spread world walk, one reply carrying every requested layer as plain points. The
    // panel turns them into unsaved Minimap pins (save=false), so nothing here ever reaches a player's map file.
    //
    //   AP_SrvOvlReq(ZPackage {ver, int mask}) -> AP_OvlData
    //   AP_OvlData: ver | int mask | long ms | int n | n x { byte layer, Vector3 pos, float radius, string label }
    //
    // Layers (bit = 1 << layer): 0 wards (radius from the prefab's PrivateArea.m_radius, on/off + owner name),
    // 1 portals (tag and the linked portal's tag via the ZDO Portal connection), 2 vehicles (Ship / Vagon), 3 tames,
    // 4 recent deaths (wave 3/4 "deaths" table, last 7 days), 5 density (busiest zones by ZDO count).
    internal static class Wave9Ovl
    {
        private const string Label = "overlays";
        private const int PerLayerCap = 150;
        private const int DensityTop = 25;
        private const int DeathCap = 40;
        private const int LabelMax = 48;

        internal const int LWards = 0, LPortals = 1, LVehicles = 2, LTames = 3, LDeaths = 4, LDensity = 5;

        private sealed class Pt
        {
            public byte Layer;
            public Vector3 Pos;
            public float Radius;
            public string Label;
        }

        private static readonly int KeyTamedName = "TamedName".GetStableHashCode();
        private static readonly Dictionary<int, float> WardRadius = new Dictionary<int, float>();

        private static int _mask;
        private static List<Pt> _pts;
        private static int[] _perLayer;
        private static Dictionary<Vector2s, int> _density;
        private static readonly List<long> Waiting = new List<long>();

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvOvlReq", "moderator");
        }

        internal static void Register(ZRoutedRpc r) => r.Register<ZPackage>("AP_SrvOvlReq", OnReq);

        private static void OnReq(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvOvlReq")) return;
            int mask;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                mask = pkg.ReadInt() & 0x3F;
            }
            catch (Exception) { return; }
            if (mask == 0) return;
            if (!Waiting.Contains(sender)) Waiting.Add(sender);
            // A running walk answers everyone waiting with the layers it was started for (widening it mid-walk would
            // ship a silently partial layer); the reply echoes its mask and the panel asks again for anything missing.
            if (Wave9Scan.Busy(Label)) return;

            _mask = mask;
            _pts = new List<Pt>();
            _perLayer = new int[6];
            _density = new Dictionary<Vector2s, int>();
            // Deaths come from the store, not the world: no walk needed for that layer alone.
            if ((mask & ~(1 << LDeaths)) == 0) { Finish(0L); return; }
            if (!Wave9Scan.Start(Label, sender, Visit, job => Finish(job.Aborted ? -1L : job.Millis)))
                Waiting.Remove(sender);
        }

        private static void Add(byte layer, Vector3 pos, float radius, string label)
        {
            if (_perLayer[layer] >= PerLayerCap) return;
            _perLayer[layer]++;
            _pts.Add(new Pt { Layer = layer, Pos = pos, Radius = radius, Label = Wave9.Clean(label, LabelMax) });
        }

        private static bool On(int layer) => (_mask & (1 << layer)) != 0;

        private static void Visit(ZDO zdo)
        {
            if (On(LDensity))
            {
                var z = ZoneSystem.GetZone(zdo.GetPosition());
                int c;
                _density.TryGetValue(z, out c);
                _density[z] = c + 1;
            }
            var hash = zdo.GetPrefab();
            var kind = Wave9.KindOf(hash);
            if (kind == 0) return;
            if ((kind & Wave9.KWard) != 0 && On(LWards))
            {
                var on = zdo.GetBool(Wave9.KeyEnabled, false);
                var owner = zdo.GetString(Wave9.KeyCreatorName, "");
                Add(LWards, zdo.GetPosition(), WardRadiusOf(hash), (on ? "" : "(off) ") + (owner.Length > 0 ? owner : "?"));
            }
            else if ((kind & Wave9.KPortal) != 0 && On(LPortals))
            {
                var tag = zdo.GetString(Wave9.KeyTag, "");
                var linked = "";
                try
                {
                    var target = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                    if (!target.IsNone())
                    {
                        var tz = ZDOMan.instance.GetZDO(target);
                        if (tz != null) linked = " > " + Wave9.F(tz.GetPosition().x) + "," + Wave9.F(tz.GetPosition().z);
                    }
                }
                catch (Exception) { }
                Add(LPortals, zdo.GetPosition(), 0f, (tag.Length > 0 ? tag : "(no tag)") + (linked.Length > 0 ? linked : " (unlinked)"));
            }
            else if ((kind & (Wave9.KShip | Wave9.KCart)) != 0 && On(LVehicles))
            {
                Add(LVehicles, zdo.GetPosition(), 0f, Wave9.PrefabName(hash));
            }
            else if ((kind & Wave9.KCharacter) != 0 && (kind & Wave9.KPlayer) == 0 && On(LTames) && zdo.GetBool(Wave9.KeyTamed, false))
            {
                var name = zdo.GetString(KeyTamedName, "");
                Add(LTames, zdo.GetPosition(), 0f, Wave9.PrefabName(hash) + (name.Length > 0 ? " \"" + name + "\"" : ""));
            }
        }

        private static float WardRadiusOf(int hash)
        {
            float r;
            if (WardRadius.TryGetValue(hash, out r)) return r;
            r = 32f;
            try
            {
                var go = Wave9.PrefabGo(hash);
                var pa = go != null ? go.GetComponent<PrivateArea>() : null;
                if (pa != null) r = pa.m_radius;
            }
            catch (Exception) { }
            WardRadius[hash] = r;
            return r;
        }

        private static void Finish(long ms)
        {
            var waiting = new List<long>(Waiting);
            Waiting.Clear();
            if (ms < 0 || _pts == null)
            {
                foreach (var w in waiting) Wave9.Tell(w, "Map overlay scan aborted.");
                _pts = null;
                return;
            }
            if (On(LDensity))
            {
                var zones = new List<KeyValuePair<Vector2s, int>>(_density);
                zones.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (var i = 0; i < zones.Count && i < DensityTop; i++)
                    Add(LDensity, ZoneSystem.GetZonePos(zones[i].Key), 32f, zones[i].Value.ToString("N0", CultureInfo.InvariantCulture) + " objects");
            }
            if (On(LDeaths)) AddDeaths();

            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(_mask);
            pkg.Write(Math.Max(0L, ms));
            pkg.Write(_pts.Count);
            foreach (var p in _pts)
            {
                pkg.Write(p.Layer);
                pkg.Write(p.Pos);
                pkg.Write(p.Radius);
                pkg.Write(p.Label ?? "");
            }
            foreach (var w in waiting) Wave9.Reply(w, "AP_OvlData", pkg);
            _pts = null;
            _density = null;
        }

        // "deaths": ticksUtc -> "id|name|x|y|z|hasTomb" (Wave34SrvCore.cs). Newest first, last 7 days.
        private static void AddDeaths()
        {
            Dictionary<string, string> table;
            try { table = FeatureStore.Table("deaths"); }
            catch (Exception) { return; }
            var cutoff = DateTime.UtcNow.AddDays(-7).Ticks;
            var rows = new List<KeyValuePair<long, string>>();
            foreach (var kv in table)
            {
                long t;
                if (!long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out t) || t < cutoff) continue;
                rows.Add(new KeyValuePair<long, string>(t, kv.Value));
            }
            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (var i = 0; i < rows.Count && i < DeathCap; i++)
            {
                var p = Wave9.Unpack(rows[i].Value, 6);
                var pos = new Vector3(Wave9.ParseFloat(p[2], 0f), Wave9.ParseFloat(p[3], 0f), Wave9.ParseFloat(p[4], 0f));
                var when = new DateTime(rows[i].Key, DateTimeKind.Utc);
                Add(LDeaths, pos, 0f, p[1] + " " + when.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + (p[5] == "1" ? "" : " (no grave)"));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #5 dungeon / location regeneration (server) ====================
    // Long-running servers run out of dungeons. This resets a looted crypt, burial chamber or any location instance:
    // its objects are deleted and the location is spawned again by the engine's own ZoneSystem.SpawnLocation in
    // Ghost mode (the path SpawnZone uses on a dedicated server, ZoneSystem.cs:1316-1350 / 2395-2575), so chests
    // refill, spawners reset and pickables regrow exactly as on first generation.
    //
    // Engine facts (decompile-1.0.20):
    //  * one location instance per zone (ZoneSystem.m_locationInstances keyed by zone); the LocationProxy object it
    //    created keeps the location hash ("location") and the SEED ("seed") in its ZDO, at the placed position and
    //    rotation - re-spawning with those reproduces the same RandomSpawn / RandomObject choices and orientation;
    //  * a dungeon's interior is generated in the SAME zone high above it (m_interiorTransform, y ~ 5000), so every
    //    container of the instance - exterior or interior - is a creator-0 container in that zone;
    //  * a dungeon chest gets its loot on first open ("addedDefaultItems"); opened and empty = looted.
    // What a reset deletes in the instance's zone: outside, only objects standing where one of the location prefab's
    // own networked children spawns (same prefab, within 1.5 m of root position + rotation * child offset - so a
    // natural tree or rock of the same kind next to it is never touched), plus everything in the interior band
    // (y > 1000) - EXCEPT players, anything a player built (creator != 0), tombstones, tamed creatures, portals and
    // items lying on the ground. A reset is refused while any player is inside or near the location, and while ANY
    // of its containers still holds items (unlooted loot, or a player's stash in a dungeon chest: empty it first).
    // A bulk reset re-checks at reset time that each instance is still looted (a chest opened, none holding items).
    //
    //   AP_SrvLocrScan(ZPackage {ver, string filter})   -> AP_LocrData (frame-spread walk for containers + proxies)
    //   AP_SrvLocrReset(ZPackage {ver, int mode, int zx, int zy, string type, int days})
    //        mode 0 = the one instance in zone (zx, zy); mode 2 = every instance of `type` looted >= `days` days ago
    //   AP_LocrData { ver, long ms, int total, n x { string name, Vector3 pos, int zx, int zy, int status,
    //                 int containers, int looted, long lootedSince, bool playerNear } }
    //   status: 0 no containers, 1 untouched, 2 partly looted, 3 looted
    internal static class Wave9Locr
    {
        private const string Label = "locations";
        private const string Tbl = "w9_locr";        // "zx,zy" -> first seen looted (UTC ticks)
        private const int RowCap = 120;
        private const int BulkCap = 20;
        private const float InteriorY = 1000f;

        private static readonly int KeyAddedDefault = "addedDefaultItems".GetStableHashCode();
        private static readonly int KeySeed = "seed".GetStableHashCode();
        private static readonly int KeyLocation = "location".GetStableHashCode();

        private sealed class ZoneInfo
        {
            public int Containers, Looted, Untouched;
            public ZDOID Proxy = ZDOID.None;
        }

        private sealed class Scan
        {
            public long Requester;
            public string Filter;
            public int ProxyHash;
            public readonly Dictionary<Vector2s, ZoneInfo> Zones = new Dictionary<Vector2s, ZoneInfo>();
        }

        private static Scan _scan;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLocrScan", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLocrReset");   // owner-only: destroys or rewrites world objects (review 2026-10-15)
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register<ZPackage>("AP_SrvLocrScan", OnScan);
            r.Register<ZPackage>("AP_SrvLocrReset", OnReset);
        }

        private static int ProxyHash()
        {
            try
            {
                var go = ZoneSystem.instance != null ? ZoneSystem.instance.m_locationProxyPrefab : null;
                return go != null ? go.name.GetStableHashCode() : "LocationProxy".GetStableHashCode();
            }
            catch (Exception) { return "LocationProxy".GetStableHashCode(); }
        }

        private static bool Matches(string name, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            foreach (var t in filter.Split(',', '|', ';'))
            {
                var term = t.Trim();
                if (term.Length > 0 && name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ==================== scan ====================

        private static void OnScan(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvLocrScan")) return;
            string filter;
            try { if (pkg.ReadInt() != Wave9.Ver) return; filter = Wave9.Clean(pkg.ReadString(), 120); }
            catch (Exception) { return; }
            if (_scan != null) { Wave9.Tell(sender, "A location scan is already running."); return; }
            var s = new Scan { Requester = sender, Filter = filter, ProxyHash = ProxyHash() };
            _scan = s;
            if (!Wave9Scan.Start(Label, sender, z => Visit(s, z), job => Done(s, job))) _scan = null;
        }

        private static void Visit(Scan s, ZDO zdo)
        {
            var hash = zdo.GetPrefab();
            if (hash == s.ProxyHash)
            {
                Info(s, ZoneSystem.GetZone(zdo.GetPosition())).Proxy = zdo.m_uid;
                return;
            }
            if ((Wave9.KindOf(hash) & Wave9.KContainer) == 0 || zdo.GetLong(Wave9.KeyCreator, 0L) != 0L) return;
            var info = Info(s, ZoneSystem.GetZone(zdo.GetPosition()));
            info.Containers++;
            if (!zdo.GetBool(KeyAddedDefault, false)) { info.Untouched++; return; }
            if (IsEmpty(InvCodec.ReadBlob(zdo))) info.Looted++;
        }

        private static ZoneInfo Info(Scan s, Vector2s z)
        {
            ZoneInfo i;
            if (!s.Zones.TryGetValue(z, out i)) s.Zones[z] = i = new ZoneInfo();
            return i;
        }

        private static readonly List<InvCodec.Rec> Scratch = new List<InvCodec.Rec>();

        private static bool IsEmpty(byte[] blob)
        {
            if (blob == null || blob.Length == 0) return true;
            int v, d;
            var ok = InvCodec.Parse(blob, out v, out d, Scratch);
            var empty = ok && Scratch.Count == 0;
            Scratch.Clear();
            return empty;
        }

        private static void Done(Scan s, Wave9Scan.Job job)
        {
            _scan = null;
            if (job.Aborted) { Wave9.Tell(s.Requester, "Location scan aborted: " + job.AbortReason); return; }
            var zs = ZoneSystem.instance;
            if (zs == null) return;
            var table = FeatureStore.Table(Tbl);
            var now = DateTime.UtcNow.Ticks;
            var rows = new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>();
            var dirty = false;
            foreach (var kv in zs.m_locationInstances)
            {
                var li = kv.Value;
                if (!li.m_placed || li.m_location == null) continue;
                var name = li.m_location.m_prefabName ?? "";
                ZoneInfo info;
                s.Zones.TryGetValue(kv.Key, out info);
                var key = kv.Key.x + "," + kv.Key.y;
                var looted = info != null && info.Containers > 0 && info.Looted == info.Containers;
                if (looted && !table.ContainsKey(key)) { table[key] = Wave9.I(now); dirty = true; }
                else if (!looted && table.Remove(key)) dirty = true;
                if (Matches(name, s.Filter)) rows.Add(kv);
            }
            if (dirty) FeatureStore.SaveTable(Tbl);

            // Looted first, then most recently looted; untouched ones last.
            rows.Sort((a, b) => Status(s, b.Key).CompareTo(Status(s, a.Key)));
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(job.Millis);
            pkg.Write(rows.Count);
            var n = Math.Min(rows.Count, RowCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var z = rows[i].Key;
                var li = rows[i].Value;
                ZoneInfo info;
                s.Zones.TryGetValue(z, out info);
                string since;
                table.TryGetValue(z.x + "," + z.y, out since);
                pkg.Write(li.m_location.m_prefabName ?? "?");
                pkg.Write(li.m_position);
                pkg.Write((int)z.x);
                pkg.Write((int)z.y);
                pkg.Write(Status(s, z));
                pkg.Write(info != null ? info.Containers : 0);
                pkg.Write(info != null ? info.Looted : 0);
                pkg.Write(Wave9.ParseLong(since, 0L));
                pkg.Write(PlayerNear(li));
            }
            Wave9.Reply(s.Requester, "AP_LocrData", pkg);
        }

        private static int Status(Scan s, Vector2s z)
        {
            ZoneInfo i;
            if (!s.Zones.TryGetValue(z, out i) || i.Containers == 0) return 0;
            if (i.Looted == i.Containers) return 3;
            return i.Looted > 0 ? 2 : 1;
        }

        private static float Radius(ZoneSystem.LocationInstance li)
        {
            var r = 16f;
            try { r = Mathf.Max(li.m_location.m_exteriorRadius, li.m_location.m_interiorRadius); } catch (Exception) { }
            return Mathf.Clamp(r, 8f, 48f);
        }

        private static bool PlayerNear(ZoneSystem.LocationInstance li)
        {
            var r = Radius(li) + 16f;
            foreach (var p in Wave9.ReadyPeers())
                if (Wave9.MapDist(Wave9.PeerPos(p), li.m_position) < r) return true;
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated() &&
                Wave9.MapDist(Player.m_localPlayer.transform.position, li.m_position) < r) return true;
            return false;
        }

        // ==================== reset ====================

        private static void OnReset(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvLocrReset")) return;
            int mode, zx, zy, days; string type;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                mode = pkg.ReadInt();
                zx = pkg.ReadInt();
                zy = pkg.ReadInt();
                type = Wave9.CleanName(pkg.ReadString(), 64);
                days = Mathf.Clamp(pkg.ReadInt(), 0, 3650);
            }
            catch (Exception) { return; }
            var zs = ZoneSystem.instance;
            if (zs == null || ZDOMan.instance == null) return;

            var targets = new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>();
            if (mode == 0)
            {
                var z = new Vector2s(zx, zy);
                ZoneSystem.LocationInstance li;
                if (zs.m_locationInstances.TryGetValue(z, out li) && li.m_placed) targets.Add(new KeyValuePair<Vector2s, ZoneSystem.LocationInstance>(z, li));
            }
            else if (mode == 2 && type.Length > 0)
            {
                var table = FeatureStore.Table(Tbl);
                var cutoff = DateTime.UtcNow.AddDays(-days).Ticks;
                foreach (var kv in zs.m_locationInstances)
                {
                    if (!kv.Value.m_placed || kv.Value.m_location == null) continue;
                    if (!string.Equals(kv.Value.m_location.m_prefabName, type, StringComparison.OrdinalIgnoreCase)) continue;
                    string since;
                    if (!table.TryGetValue(kv.Key.x + "," + kv.Key.y, out since)) continue;
                    if (Wave9.ParseLong(since, long.MaxValue) > cutoff) continue;
                    targets.Add(kv);
                    if (targets.Count >= BulkCap) break;
                }
            }
            if (targets.Count == 0) { Wave9.Tell(sender, "No matching location to reset (a type reset needs a scan that saw them looted)."); return; }

            int ok = 0, busy = 0, failed = 0, deleted = 0, refused = 0;
            var firstRefusal = "";
            foreach (var t in targets)
            {
                if (PlayerNear(t.Value)) { busy++; continue; }
                int d; string why;
                if (ResetOne(t.Key, t.Value, mode == 2, out d, out why)) { ok++; deleted += d; FeatureStore.Table(Tbl).Remove(t.Key.x + "," + t.Key.y); }
                else if (why.Length > 0) { refused++; if (firstRefusal.Length == 0) firstRefusal = why; }
                else failed++;
            }
            FeatureStore.SaveTable(Tbl);
            CompanionPlugin.SrvAudit(sender, "LocationReset", $"mode {mode} {type} zone {zx},{zy}: reset {ok}, skipped {busy} (player near), refused {refused}, failed {failed}, {deleted} object(s) replaced");
            Wave9.Tell(sender, $"Locations reset: {ok}" + (busy > 0 ? $", {busy} skipped (a player is there)" : "")
                               + (refused > 0 ? $", {refused} refused ({firstRefusal})" : "")
                               + (failed > 0 ? $", {failed} failed (see the server log)" : "") + ".");
        }

        private static readonly List<ZDO> ZoneScratch = new List<ZDO>();
        private static MethodInfo _spawnLocation;
        private static bool _spawnProbed;

        private const float ChildTolerance = 1.5f;

        private static bool ResetOne(Vector2s zone, ZoneSystem.LocationInstance li, bool requireLooted, out int deleted, out string refusal)
        {
            deleted = 0;
            refusal = "";
            var zs = ZoneSystem.instance;
            var man = ZDOMan.instance;
            try
            {
                if (!_spawnProbed)
                {
                    _spawnProbed = true;
                    _spawnLocation = AccessTools.Method(typeof(ZoneSystem), "SpawnLocation");
                    if (_spawnLocation == null) CompanionPlugin.FeatureLog("Location reset unavailable: ZoneSystem.SpawnLocation was not found on this game build.");
                }
                if (_spawnLocation == null) return false;

                ZoneScratch.Clear();
                ZoneCompat.FindSectorObjects(man, zone, 0, ZoneScratch);
                var proxyHash = ProxyHash();
                var locHash = (li.m_location.m_prefabName ?? "").GetStableHashCode();
                ZDO proxy = null;
                foreach (var z in ZoneScratch)
                    if (z != null && z.IsValid() && z.GetPrefab() == proxyHash && z.GetInt(KeyLocation, locHash) == locHash) { proxy = z; break; }

                var pos = proxy != null ? proxy.GetPosition() : li.m_position;
                var rot = proxy != null ? proxy.GetRotation() : Quaternion.identity;
                var seed = proxy != null ? proxy.GetInt(KeySeed, 0) : 0;
                if (proxy == null || seed == 0)
                    seed = WorldGenerator.instance.GetSeed() + zone.x * 4271 + zone.y * 9187;   // PlaceLocations' own formula

                var own = LocationChildren(li.m_location);
                if (own == null || own.Count == 0)
                {
                    // Without the prefab's own object list the old exterior could not be told from the new one and
                    // the reset would stack a second copy on top. Refuse instead.
                    CompanionPlugin.FeatureLog($"Location reset of {li.m_location.m_prefabName} refused: its prefab contents could not be read on this build.");
                    return false;
                }
                // Where each of the location's own children spawns: root position + rotation * child offset.
                var expected = new Dictionary<int, List<Vector3>>();
                foreach (var c in own)
                {
                    List<Vector3> l;
                    if (!expected.TryGetValue(c.Key, out l)) expected[c.Key] = l = new List<Vector3>();
                    l.Add(pos + rot * c.Value);
                }
                var victims = new List<ZDO>();
                var opened = 0;
                var holding = 0;
                foreach (var z in ZoneScratch)
                {
                    if (z == null || !z.IsValid() || z == proxy) continue;
                    var p = z.GetPosition();
                    var interior = p.y > InteriorY;
                    var hash = z.GetPrefab();
                    if (!interior && !AtChildSpot(expected, hash, p)) continue;
                    if (Protected(z, hash)) continue;
                    if ((Wave9.KindOf(hash) & Wave9.KContainer) != 0)
                    {
                        if (z.GetBool(KeyAddedDefault, false)) opened++;
                        var blob = InvCodec.ReadBlob(z);
                        ResetRecs.Clear();
                        if (blob != null && InvCodec.Parse(blob, out _, out _, ResetRecs) && ResetRecs.Count > 0) holding++;
                        ResetRecs.Clear();
                    }
                    victims.Add(z);
                }
                if (holding > 0)
                {
                    refusal = $"{li.m_location.m_prefabName} at zone {zone.x},{zone.y}: {holding} chest(s) still hold items - empty them first";
                    return false;
                }
                if (requireLooted && opened == 0)
                {
                    refusal = $"{li.m_location.m_prefabName} at zone {zone.x},{zone.y} is no longer looted";
                    return false;
                }
                foreach (var z in victims) if (Wave9.DestroyZdo(z)) deleted++;
                if (proxy != null) Wave9.DestroyZdo(proxy);

                var ghosts = new List<GameObject>();
                _spawnLocation.Invoke(zs, new object[] { li.m_location, seed, pos, rot, ZoneSystem.SpawnMode.Ghost, ghosts, false });
                foreach (var g in ghosts) if (g != null) UnityEngine.Object.Destroy(g);
                CompanionPlugin.FeatureLog($"Location reset: {li.m_location.m_prefabName} in zone {zone.x},{zone.y} - {deleted} old object(s) removed, {ghosts.Count} re-spawned.");
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Location reset of zone {zone.x},{zone.y} failed: {(e.InnerException ?? e).Message}");
                return false;
            }
            finally { ZoneScratch.Clear(); }
        }

        private static readonly List<InvCodec.Rec> ResetRecs = new List<InvCodec.Rec>();

        private static bool AtChildSpot(Dictionary<int, List<Vector3>> expected, int hash, Vector3 p)
        {
            List<Vector3> spots;
            if (!expected.TryGetValue(hash, out spots)) return false;
            foreach (var s in spots) if ((s - p).sqrMagnitude <= ChildTolerance * ChildTolerance) return true;
            return false;
        }

        private static bool Protected(ZDO z, int hash)
        {
            var kind = Wave9.KindOf(hash);
            if ((kind & (Wave9.KPlayer | Wave9.KTombstone | Wave9.KPortal | Wave9.KItemDrop)) != 0) return true;
            if (z.GetLong(Wave9.KeyCreator, 0L) != 0L) return true;
            if ((kind & Wave9.KCharacter) != 0 && z.GetBool(Wave9.KeyTamed, false)) return true;
            return false;
        }

        // The location prefab's own networked children (ZNetView): prefab hash + offset from the prefab root, which is
        // where SpawnLocation places each one (it positions the root, then spawns every net view at its world spot).
        // ZoneLocation.m_prefab is a SoftReference<GameObject> from an assembly this project does not reference, so it
        // is driven by reflection.
        private static readonly Dictionary<string, List<KeyValuePair<int, Vector3>>> OwnCache =
            new Dictionary<string, List<KeyValuePair<int, Vector3>>>(StringComparer.Ordinal);

        private static List<KeyValuePair<int, Vector3>> LocationChildren(ZoneSystem.ZoneLocation loc)
        {
            var key = loc.m_prefabName ?? "";
            List<KeyValuePair<int, Vector3>> set;
            if (OwnCache.TryGetValue(key, out set)) return set;
            set = null;
            try
            {
                var sr = AccessTools.Field(typeof(ZoneSystem.ZoneLocation), "m_prefab")?.GetValue(loc);
                if (sr != null)
                {
                    var t = sr.GetType();
                    t.GetMethod("Load", Type.EmptyTypes)?.Invoke(sr, null);
                    try
                    {
                        var asset = t.GetProperty("Asset")?.GetValue(sr, null) as GameObject;
                        if (asset != null)
                        {
                            set = new List<KeyValuePair<int, Vector3>>();
                            var root = asset.transform;
                            foreach (var nv in asset.GetComponentsInChildren<ZNetView>(true))
                                if (nv != null && nv.gameObject != asset)
                                    set.Add(new KeyValuePair<int, Vector3>(Utils.GetPrefabName(nv.gameObject).GetStableHashCode(),
                                        Quaternion.Inverse(root.rotation) * (nv.transform.position - root.position)));
                        }
                    }
                    finally { t.GetMethod("Release", Type.EmptyTypes)?.Invoke(sr, null); }
                }
            }
            catch (Exception e) { Wave9.ThrottledLog("locr-prefab", $"Location prefab read failed ({key}): {e.Message}"); set = null; }
            OwnCache[key] = set;
            return set;
        }
    }
}

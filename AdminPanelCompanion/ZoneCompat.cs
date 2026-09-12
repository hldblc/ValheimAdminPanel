using System;
using System.Collections.Generic;
using System.Reflection;

namespace AdminPanelCompanion
{
    // ==================== Sector / zone / ZDO-container shim ====================
    // Valheim 1.0.12 (the 2026-09-11 update) reshaped the sector API: zones are Vector2s (shorts, not ints),
    // ZDOMan.FindSectorObjects takes a SimulationDistance instead of (area, distantArea), and portals are
    // grouped per ZoneSystem.SectorIndex instead of one flat list. 2.5.0 was compiled against the old shape
    // and every feature that touched these threw MissingMethodException at JIT time (caught, so they just
    // "did nothing"). Everything in the companion now goes through here, so the next engine change is a
    // one-file fix. The projects compile against the LIVE game assembly (see the csproj comment).
    //
    // ENGINE FACTS (decompiled 1.0.12 assembly_valheim; line numbers refer to that decompile)
    //   * ZDOMan.m_objectsByID : Dictionary<ZDOID, ZDO> (ZDOMan.cs:91) holds EVERY ZDO, portals included;
    //     NrOfObjects() is its Count (ZDOMan.cs:1717-1720).
    //   * ZDOMan.m_objectsBySector : List<ZDO>[512*512] indexed by SectorIndex.Sector (ZDOMan.cs:99, sized
    //     in ResetSectorArray 205-208) - one bucket per zone for every NON-portal ZDO. Sector 0 doubles as
    //     the OutsideZones bucket: a ZDO whose sector was invalidated (ZDO.InvalidateSector -> SectorZero,
    //     ZDO.cs:493-496) and any zone with |x| or |y| >= 256 (ZoneSystem.SectorToIndex, ZoneSystem.cs:2990-
    //     3003) both land there. The pre-1.0.12 m_objectsByOutsideSector dictionary no longer exists.
    //   * ZDOMan.m_portalObjects : Dictionary<ZoneSystem.SectorIndex, List<ZDO>> (ZDOMan.cs:81), handed back
    //     as-is by GetPortals() (ZDOMan.cs:1702-1705). Portals live ONLY here: a loaded portal never enters
    //     the sector array (LoadChunks ZDOMan.cs:534-537, legacy Load 635-638 / 654-657), a runtime-created
    //     or received one is pulled back out of it by AddIfPortal (ZDOMan.cs:1791-1809, called from
    //     CreateNewZDO 748 and RPC_ZDOData 1191), and ZDO.SetSector early-returns for portal prefabs
    //     (ZDO.cs:498-503). Game.instance.PortalPrefabHash (Game.cs:234, filled in Awake 241-243) is the
    //     membership test the engine itself uses.
    //   * FindSectorObjects(Vector2s, SimulationDistance, List<ZDO>, List<ZDO> = null) (ZDOMan.cs:1201)
    //     reads BOTH containers for every sector it visits (FindObjects, ZDOMan.cs:1426-1441), so the zone-
    //     block queries see portals; the world-wide scanners walk the array and add the portal table
    //     themselves (PortalsSnapshot).
    internal static class ZoneCompat
    {
        // Zones with |x| or |y| >= 256 alias Sector 0 (see above). The playable world ends at
        // WorldGenerator.waterEdge = 10500 m (WorldGenerator.cs:167) = zone +-164, so nothing real is
        // beyond zone +-255 and clipping to the grid loses no object.
        private const int MaxZoneAbs = 255;

        // ---- ZDOMan container access: ONE cached, silent probe ----
        // Type.GetField returns null for a renamed field without the HarmonyX warning AccessTools.Field logs;
        // the callers report "unavailable" themselves when both containers are gone.
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo _fSectors, _fById;
        private static bool _probed;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _fSectors = typeof(ZDOMan).GetField("m_objectsBySector", AnyInstance);
                _fById = typeof(ZDOMan).GetField("m_objectsByID", AnyInstance);
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"ZDOMan container reflection failed: {e.Message}");
            }
        }

        /// <summary>
        /// The live per-zone bucket array (non-portal ZDOs only; portals are in <see cref="PortalsSnapshot"/>),
        /// or null when it cannot be read. Read it fresh every frame and compare its Length: a world reload
        /// replaces the array (ZDOMan.ResetSectorArray, ZDOMan.cs:205-208).
        /// </summary>
        internal static List<ZDO>[] SectorArray(ZDOMan man)
        {
            Probe();
            if (_fSectors == null || man == null) return null;
            try { return _fSectors.GetValue(man) as List<ZDO>[]; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The live id -> ZDO map (every ZDO, portals included), or null when it cannot be read. Never
        /// enumerate it across frames; take <see cref="FlatSnapshot"/> for that.
        /// </summary>
        internal static Dictionary<ZDOID, ZDO> ById(ZDOMan man)
        {
            Probe();
            if (_fById == null || man == null) return null;
            try { return _fById.GetValue(man) as Dictionary<ZDOID, ZDO>; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// ONE snapshot of every ZDO (portals included) for a frame-spread walk when the sector array cannot
        /// be read. Costs an array of N references (a few MB on a huge world) but is immune to the
        /// InvalidOperationException a live dictionary walk would throw. Null when unavailable.
        /// </summary>
        internal static ZDO[] FlatSnapshot(ZDOMan man)
        {
            var dict = ById(man);
            if (dict == null) return null;
            try
            {
                var arr = new ZDO[dict.Count];
                dict.Values.CopyTo(arr, 0);
                return arr;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// True when <paramref name="zdo"/> sits in the sector-array bucket of its own position. A portal
        /// never does (see the header), so the scanners use this only as a belt-and-braces guard against
        /// counting one object in both the array pass and the portal pass.
        /// </summary>
        internal static bool InSectorBucket(List<ZDO>[] sectors, ZDO zdo)
        {
            if (sectors == null || zdo == null) return false;
            try
            {
                var index = zdo.GetSectorIndex().Sector;
                if (index >= (uint)sectors.Length) return false;
                var bucket = sectors[index];
                return bucket != null && bucket.Contains(zdo);
            }
            catch (Exception) { return false; }
        }

        // Square block of (2*area+1)^2 zones around `sector` - exactly what the pre-1.0.12
        // FindSectorObjects(sector, area, distantArea, ...) overload produced with distantArea 0, which is
        // what every caller in the companion wants (a near block, no distant pass). classic:true bypasses the
        // new circular ZonesWithinRadius check, so the walk stays a square and callers' radius math still
        // holds; near=area, far=0 starts the distant loop past its end (ZDOMan.cs:1234-1240), so nothing else
        // is appended. APPENDS to `objects`, like the engine.
        //
        // Grid clipping (hardening): a zone with |x| or |y| >= 256 aliases Sector 0, the bucket that also
        // holds every sector-invalidated ZDO in the world, so a query there would return unrelated objects
        // from anywhere. A centre outside the grid is answered with nothing, and `area` is shrunk so the
        // block never touches the aliasing cells. The playable world ends 90+ zones before the grid does, so
        // no real object is ever lost to the clip.
        internal static void FindSectorObjects(ZDOMan man, Vector2s sector, int area, List<ZDO> objects)
        {
            if (man == null || objects == null) return;
            int ax = Math.Abs((int)sector.x), ay = Math.Abs((int)sector.y);
            if (ax > MaxZoneAbs || ay > MaxZoneAbs) return;
            area = Math.Max(0, Math.Min(area, MaxZoneAbs - Math.Max(ax, ay)));
            man.FindSectorObjects(sector, new SimulationDistance(area, 0, classic: true), objects);
        }

        // Deduplicated, validated COPY of the live portal table (GetPortals, ZDOMan.cs:1702-1705). Why each
        // filter exists:
        //   * dedupe by m_uid - m_portalObjects is keyed by the sector the portal had when it was inserted
        //     and is never re-keyed: AddIfPortal adds to the bucket of the CURRENT sector and never removes
        //     the entry in the old one (ZDOMan.cs:1791-1809), so a portal carried across a zone edge (its
        //     ZDO arrives again through RPC_ZDOData, ZDOMan.cs:1187-1191) sits in two buckets at once and the
        //     engine's own GetPortalList (ZDOMan.cs:1707-1715) would report it twice.
        //   * IsValid() - HandleDestroyedZDO removes the ZDO only from the bucket of its current sector
        //     (ZDOMan.cs:1039-1044) and then releases it to ZDOPool (1048), so a moved-then-destroyed portal
        //     leaves a reference to a pooled object in its old bucket. ZDOPool.Release -> ZDO.Reset sets
        //     Valid = false, i.e. m_prefab = -1 (ZDO.cs:282-296, 214-223), which is exactly what IsValid()
        //     tests (ZDO.cs:263-266).
        //   * PortalPrefabHash - once the pool hands that object out again (ZDOPool.Get -> ZDO.Init, Valid =
        //     true) the stale reference is a live, unrelated ZDO; the prefab test drops it unless it became a
        //     portal again, in which case it also sits in its own bucket and the m_uid dedupe drops the copy.
        // The engine hands back its own dictionary - never mutate it, and never enumerate it while a ZDO could
        // be created (the copy makes both moot; nothing here calls engine code while walking it).
        internal static List<ZDO> PortalsSnapshot(ZDOMan man)
        {
            var result = new List<ZDO>();
            if (man == null) return result;
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> table;
            try { table = man.GetPortals(); }
            catch (Exception) { return result; }
            if (table == null) return result;

            var game = Game.instance;
            var portalHashes = game != null ? game.PortalPrefabHash : null;
            var seen = new HashSet<ZDOID>();
            foreach (var kv in table)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var zdo = list[i];
                    if (zdo == null || !zdo.IsValid()) continue;
                    if (portalHashes != null && !portalHashes.Contains(zdo.GetPrefab())) continue;
                    if (!seen.Add(zdo.m_uid)) continue;
                    result.Add(zdo);
                }
            }
            return result;
        }
    }
}

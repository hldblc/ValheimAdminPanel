using System.Collections.Generic;

namespace AdminPanelCompanion
{
    // ==================== Sector / zone API shim ====================
    // Valheim 1.0.12 (the 2026-09-11 update) reshaped the sector API: zones are Vector2s (shorts, not ints),
    // ZDOMan.FindSectorObjects takes a SimulationDistance instead of (area, distantArea), and portals are
    // grouped per ZoneSystem.SectorIndex instead of one flat list. 2.5.0 was compiled against the old shape
    // and every feature that touched these threw MissingMethodException at JIT time (caught, so they just
    // "did nothing"). Everything in the companion now goes through here, so the next engine change is a
    // one-file fix. The projects compile against the LIVE game assembly (see the csproj comment).
    internal static class ZoneCompat
    {
        // Square block of (2*area+1)^2 zones around `sector` - exactly what the pre-1.0.12
        // FindSectorObjects(sector, area, distantArea, ...) overload produced. classic:true bypasses the new
        // circular ZonesWithinRadius check, so the walk stays a square and callers' radius math still holds.
        internal static void FindSectorObjects(ZDOMan man, Vector2s sector, int area, int distantArea,
            List<ZDO> objects, List<ZDO> distant = null)
        {
            man.FindSectorObjects(sector, new SimulationDistance(area, distantArea, classic: true), objects, distant);
        }

        // Flattened COPY of the live portal table. The engine hands back its own dictionary - never mutate
        // it, and never enumerate it while a ZDO could be created (the copy makes both moot).
        internal static List<ZDO> PortalsSnapshot(ZDOMan man)
        {
            var result = new List<ZDO>();
            var table = man != null ? man.GetPortals() : null;
            if (table == null) return result;
            foreach (var kv in table)
                if (kv.Value != null) result.AddRange(kv.Value);
            return result;
        }
    }
}

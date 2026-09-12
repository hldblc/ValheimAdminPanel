using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #13 location finder (server side) ====================
    // Two read-only lookups over the world's generated location table:
    //
    //   AP_SrvLocTypesReq()          -> AP_LocTypes   every distinct location prefab with its counts
    //   AP_SrvLocFindReq(ZPackage)   -> AP_LocFind    the nearest N (<= 50) matching a name filter
    //
    // Source: ZoneSystem.m_locationInstances (verified public in this build: Dictionary<Vector2i,
    // LocationInstance>, LocationInstance { ZoneLocation m_location; Vector3 m_position; bool m_placed }).
    // The table is filled by the world generator at load (ZoneSystem.GenerateLocations) and is complete
    // for the whole world regardless of what any player has explored, which is what makes a "nearest
    // crypt" answer possible without a ZDO sweep. m_placed only says whether the zone has been generated
    // yet (a player has been near) — an unplaced location is still exactly where the table says.
    //
    // Both replies are data-driven — a modded world's extra locations show up with no code change. The
    // handlers are click-driven and chokepoint-audited (like AP_SrvRapSheetReq); a full pass over the
    // table is a few thousand struct reads, well under a millisecond, so no frame-spreading is needed.
    internal static class Wave8ToolkitLoc
    {
        private const int Ver = 1;
        private const int TypeCap = 150;     // wire contract: types <= 150
        private const int FindCap = 50;      // wire contract: rows <= 50
        private const int MaxFilterLen = 120;
        private const int MaxTerms = 8;
        private const float TypesCacheSeconds = 30f;

        private sealed class TypeRow
        {
            public string Name;
            public int Count;
            public int Placed;
        }

        private struct Hit
        {
            public string Name;
            public Vector3 Pos;
            public float Dist;
            public bool Placed;
        }

        // The type list changes only when the world is (re)generated; cache it briefly so a held Refresh
        // button cannot turn into a table walk per click.
        private static List<TypeRow> _typesCache;
        private static float _typesCacheAt = -1000f;
        private static bool _readWarned;

        internal static void Init() { /* nothing to bind: passive reads, always available */ }

        // ==================== AP_SrvLocTypesReq -> AP_LocTypes ====================

        internal static void OnTypesReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLocTypesReq")) return;
            try
            {
                var types = Types();
                var pkg = new ZPackage();
                pkg.Write(Ver);
                pkg.Write(types.Count);                       // distinct prefabs in the world
                var n = Math.Min(types.Count, TypeCap);
                pkg.Write(n);
                for (var i = 0; i < n; i++)
                {
                    pkg.Write(types[i].Name);
                    pkg.Write(types[i].Count);
                    pkg.Write(types[i].Placed);
                }
                CompanionPlugin.ReplyTo(sender, "AP_LocTypes", pkg);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvLocTypesReq failed: {e.Message}"); }
        }

        private static List<TypeRow> Types()
        {
            var now = Time.unscaledTime;
            if (_typesCache != null && now - _typesCacheAt < TypesCacheSeconds) return _typesCache;

            var map = new Dictionary<string, TypeRow>(StringComparer.Ordinal);
            var all = Instances();
            if (all != null)
            {
                foreach (var li in all)
                {
                    var name = li.m_location != null ? li.m_location.m_prefabName : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    TypeRow row;
                    if (!map.TryGetValue(name, out row)) map[name] = row = new TypeRow { Name = name };
                    row.Count++;
                    if (li.m_placed) row.Placed++;
                }
            }
            var list = new List<TypeRow>(map.Values);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _typesCache = list;
            _typesCacheAt = now;
            return list;
        }

        // ==================== AP_SrvLocFindReq -> AP_LocFind ====================

        // Request: int ver=1 | string filter | Vector3 origin | int max.
        // Reply:   int ver=1 | string filterEcho | int matchesTotal | int n (<=50) |
        //          n x { string name, Vector3 pos, float distXZ, bool placed }
        internal static void OnFindReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLocFindReq")) return;

            int ver, max; string filter; Vector3 origin;
            try
            {
                ver = pkg.ReadInt();
                filter = pkg.ReadString();
                origin = pkg.ReadVector3();
                max = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvLocFindReq: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            filter = Wave8Toolkit.CleanText(filter, MaxFilterLen);
            max = Mathf.Clamp(max, 1, FindCap);
            // A client-supplied origin that is not finite would poison every distance below.
            if (float.IsNaN(origin.x) || float.IsInfinity(origin.x) || float.IsNaN(origin.z) || float.IsInfinity(origin.z))
                origin = Wave8Toolkit.SenderPosition(sender);

            try
            {
                var terms = Terms(filter);
                var hits = new List<Hit>();
                var all = Instances();
                if (all != null)
                {
                    foreach (var li in all)
                    {
                        var name = li.m_location != null ? li.m_location.m_prefabName : null;
                        if (string.IsNullOrEmpty(name) || !Matches(name, terms)) continue;
                        var dx = li.m_position.x - origin.x;
                        var dz = li.m_position.z - origin.z;
                        hits.Add(new Hit
                        {
                            Name = name,
                            Pos = li.m_position,
                            Dist = Mathf.Sqrt(dx * dx + dz * dz),   // map distance: what an admin walks or flies
                            Placed = li.m_placed,
                        });
                    }
                }
                hits.Sort((a, b) => a.Dist.CompareTo(b.Dist));

                var reply = new ZPackage();
                reply.Write(Ver);
                reply.Write(filter);
                reply.Write(hits.Count);
                var n = Math.Min(hits.Count, max);
                reply.Write(n);
                for (var i = 0; i < n; i++)
                {
                    reply.Write(hits[i].Name);
                    reply.Write(hits[i].Pos);
                    reply.Write(hits[i].Dist);
                    reply.Write(hits[i].Placed);
                }
                CompanionPlugin.ReplyTo(sender, "AP_LocFind", reply);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvLocFindReq failed: {e.Message}"); }
        }

        // ==================== helpers ====================

        // The public field is read through one small method so a future rename fails HERE (a missing
        // member surfaces when this method is JIT-compiled, i.e. at the call site's try/catch) and the
        // reply degrades to "no locations" with one log line, instead of taking the handler down.
        private static Dictionary<Vector2i, ZoneSystem.LocationInstance>.ValueCollection Instances()
        {
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null) return null;
                return zs.GetLocationList();
            }
            catch (Exception e)
            {
                if (!_readWarned)
                {
                    _readWarned = true;
                    CompanionPlugin.FeatureLog($"Wave8: the location table could not be read on this game build ({e.Message}); the location finder reports nothing.");
                }
                return null;
            }
        }

        // "Crypt,Cave|Vendor" -> up to 8 case-insensitive substrings; any one matching selects the row.
        // An empty filter matches everything (the panel caps the rows anyway).
        private static List<string> Terms(string filter)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(filter)) return res;
            foreach (var part in filter.Split(',', '|', ';'))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                res.Add(t);
                if (res.Count >= MaxTerms) break;
            }
            return res;
        }

        private static bool Matches(string name, List<string> terms)
        {
            if (terms.Count == 0) return true;
            for (var i = 0; i < terms.Count; i++)
                if (name.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}

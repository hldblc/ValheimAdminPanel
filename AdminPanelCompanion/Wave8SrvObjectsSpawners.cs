using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #12 spawner and nest manager (server side) ====================
    // Lists every ZDO in a zone block around a point whose PREFAB ASSET carries a SpawnArea (greydwarf
    // nest, draugr pile, surtling geyser ...) or a CreatureSpawner (dungeon / location spawners). The
    // component test is cached per prefab hash; nothing here trusts the "Spawner_" name prefix. Removal
    // re-validates each id against that same test, so the RPC can never delete an arbitrary object.
    //
    //   AP_SrvSpawnerScanReq {int ver, Vector3 center, int zones(1|2|4)}
    //   AP_SpawnerList       {int ver, int found, int shipped(<=100),
    //                         shipped x (ZDOID id, string prefab, int kind(0 nest, 1 spawner), string spawns,
    //                                    Vector3 pos, float dist, long aliveTicks, float respawnMinutes,
    //                                    bool spawnedAlive)}
    //   AP_SrvSpawnerRemove  {int ver, int n(<=200), n x ZDOID}
    internal static partial class Wave8Objects
    {
        private sealed class SpawnerRow
        {
            public ZDOID Id;
            public PrefabInfo Info;
            public Vector3 Pos;
            public float Dist;
            public long AliveTicks;
            public bool SpawnedAlive;
        }

        private static readonly List<SpawnerRow> SpawnerScratch = new List<SpawnerRow>();

        // ==================== AP_SrvSpawnerScanReq ====================

        private static void OnSpawnerScanReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSpawnerScanReq")) return;

            int ver, zones; Vector3 center;
            try
            {
                ver = pkg.ReadInt();
                center = pkg.ReadVector3();
                zones = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvSpawnerScanReq: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (!CenterOk(center))   // NaN/Infinity, or beyond the world edge (see CenterOk)
            {
                CompanionPlugin.NotifySender(sender, "Spawner scan rejected: centre position out of range.");
                SendSpawnerList(sender, null, 0);
                return;
            }
            zones = zones >= 4 ? 4 : zones >= 2 ? 2 : 1;

            if (!CollectBlock(center, zones, Scratch))
            {
                CompanionPlugin.NotifySender(sender, "Spawner scan unavailable: the server could not read the world object store.");
                SendSpawnerList(sender, null, 0);
                return;
            }

            var man = ZDOMan.instance;
            SpawnerScratch.Clear();
            var found = 0;
            for (var i = 0; i < Scratch.Count; i++)
            {
                var zdo = Scratch[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    var info = InfoOf(zdo.GetPrefab());
                    if (info == null || info.SpawnerKind < 0) continue;
                    found++;
                    if (SpawnerScratch.Count >= CandidateCap) continue;
                    var pos = zdo.GetPosition();
                    var row = new SpawnerRow { Id = zdo.m_uid, Info = info, Pos = pos, Dist = Vector3.Distance(pos, center) };
                    if (info.SpawnerKind == 1)
                    {
                        // alive_time is stamped by the owning client each time it sees its spawn alive
                        // (CreatureSpawner.SpawnedCreatureStillExists / Spawn); the Spawned connection points at
                        // the creature it made, so "still alive" is a plain ZDO existence check.
                        row.AliveTicks = zdo.GetLong(KeyAliveTime, 0L);
                        try
                        {
                            if (zdo.GetConnectionType() == ZDOExtraData.ConnectionType.Spawned)
                            {
                                var spawned = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
                                row.SpawnedAlive = !spawned.IsNone() && man != null && man.GetZDO(spawned) != null;
                            }
                        }
                        catch (Exception) { }
                    }
                    SpawnerScratch.Add(row);
                }
                catch (Exception) { }
            }
            Scratch.Clear();

            SpawnerScratch.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            if (SpawnerScratch.Count > SpawnerRowCap) SpawnerScratch.RemoveRange(SpawnerRowCap, SpawnerScratch.Count - SpawnerRowCap);
            SendSpawnerList(sender, SpawnerScratch, found);
            SpawnerScratch.Clear();
        }

        private static void SendSpawnerList(long uid, List<SpawnerRow> rows, int found)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(found);
            var n = rows != null ? Math.Min(rows.Count, SpawnerRowCap) : 0;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i];
                pkg.Write(r.Id);
                pkg.Write(r.Info.Name ?? "");
                pkg.Write(r.Info.SpawnerKind);
                pkg.Write(r.Info.Spawns ?? "");
                pkg.Write(r.Pos);
                pkg.Write(r.Dist);
                pkg.Write(r.AliveTicks);
                pkg.Write(r.Info.RespawnMinutes);
                pkg.Write(r.SpawnedAlive);
            }
            Reply(uid, "AP_SpawnerList", pkg);
        }

        // ==================== AP_SrvSpawnerRemove ====================

        private static void OnSpawnerRemove(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSpawnerRemove")) return;

            int ver, n;
            var ids = new List<ZDOID>();
            try
            {
                ver = pkg.ReadInt();
                n = pkg.ReadInt();
                if (n < 0 || n > SpawnerRemoveCap) return;
                for (var i = 0; i < n; i++) ids.Add(pkg.ReadZDOID());
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvSpawnerRemove: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            if (!SpawnerRemoveEnabled)
            {
                CompanionPlugin.NotifySender(sender, "Spawner removal is disabled in the server config (EnableSpawnerRemove=false).");
                return;
            }
            var man = ZDOMan.instance;
            if (man == null) return;

            var queued = 0;
            var gone = 0;
            var refused = 0;
            var names = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                try
                {
                    var zdo = man.GetZDO(ids[i]);
                    if (zdo == null || !zdo.IsValid()) { gone++; continue; }
                    // The id came from the panel; only something that IS a spawner may be removed through here.
                    var info = InfoOf(zdo.GetPrefab());
                    if (info == null || info.SpawnerKind < 0) { refused++; continue; }
                    if (!QueueDelete(zdo.m_uid)) break;   // queue full: the rest waits for the next request
                    queued++;
                    if (names.Count < 5 && !names.Contains(info.Name)) names.Add(info.Name);
                }
                catch (Exception) { }
            }

            var admin = Wave1AuditRpc.AdminLabel(sender);
            var detail = $"requested={ids.Count} queued={queued} gone={gone} notSpawner={refused} prefabs={string.Join(",", names.ToArray())}";
            CompanionPlugin.SrvAudit(sender, "SPAWNER_REMOVE", detail);
            CompanionPlugin.FeatureLog($"Spawner removal by {admin}: {detail}");
            if (queued > 0) Wave1AuditRpc.PostModLog($"SPAWNER REMOVE {admin} removed {queued} spawner(s) ({string.Join(", ", names.ToArray())})");

            if (queued == 0 && refused > 0)
                CompanionPlugin.NotifySender(sender, "Nothing removed: none of those objects is a spawner on this server.");
            else if (queued == 0)
                CompanionPlugin.NotifySender(sender, "Nothing removed: those spawners no longer exist - scan again.");
            else
                CompanionPlugin.NotifySender(sender, $"Removing {queued} spawner(s)" +
                    (gone > 0 ? $", {gone} already gone" : "") + (refused > 0 ? $", {refused} refused (not spawners)" : "") + ".");
        }
    }
}

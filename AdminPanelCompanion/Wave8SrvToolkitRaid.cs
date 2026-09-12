using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #22 custom raid composer (server side) ====================
    // "20 two-star draugr at the arena in three waves." The panel composes the raid; the server owns
    // every timer and every spawned object:
    //
    //   AP_SrvRaidStart(ZPackage)  start (builder grant, one raid at a time)
    //   AP_SrvRaidStop()           remove what is still alive, forget the raid (builder grant)
    //   AP_SrvRaidStateReq()       -> AP_RaidState  (also pushed to the starter on every change)
    //
    // Spawning is CompanionPlugin.OnServerSpawn's creature branch (ZNetScene prefab, Instantiate, SetLevel,
    // ZDOID remembered) with a random point inside the radius instead of a +/-1.5 m scatter, and the
    // removal is OnServerUndo's owner-fix (claim, then DestroyZDO — a silent no-op for a ZDO the caller
    // does not own). Ownership of a fresh spawn: the server creates the ZDO as its owner, and
    // ZDOMan.ReleaseNearbyZDOS hands any server-owned ZDO inside a peer's active area to that peer on the
    // next release pass (the server's session is never "in a peer active area"), so the nearest client
    // simulates the creature exactly as it does for AP_SrvSpawn. Nothing here holds ownership afterwards.
    //
    // Start wire: int ver=1 | Vector3 origin | bool hasY | int intervalSec | float radius | int durationSec |
    //             string banner | int nWaves | nWaves x { string prefab, int count, int level }
    // State wire: int ver=1 | bool active | bool spawning | int wavesDone | int waveCount | int alive |
    //             int spawnedTotal | float secondsToNextWave (-1 none) | int secondsLeft (-1 keep) |
    //             string banner | string starterName | string note
    internal static class Wave8ToolkitRaid
    {
        private const int Ver = 1;
        private const int MaxWaves = 5;
        private const int MaxPerWave = 30;
        private const int MaxTotal = 100;
        private const int MinInterval = 5, MaxInterval = 120;
        private const float MinRadius = 5f, MaxRadius = 60f;
        private const int MaxDuration = 3600;
        private const int MaxBannerLen = 60;
        private const int MaxPrefabLen = 64;
        private const int MaxNoteLen = 200;
        private const float TickSeconds = 0.5f;

        private static ConfigEntry<int> _maxLevelCfg;
        private static bool _inited;
        private static float _nextTick;
        private static bool _groundWarned;

        // Levels above 3 are allowed only through config, the way BossRushLevel gates the boss rush.
        private static int MaxLevel => _maxLevelCfg != null ? Mathf.Clamp(_maxLevelCfg.Value, 1, 10) : 3;

        private sealed class WaveSpec
        {
            public string Prefab;
            public GameObject Go;
            public int Count;
            public int Level;
        }

        private sealed class Raid
        {
            public long Starter;
            public string StarterName = "";
            public Vector3 Origin;
            public int Interval;
            public float Radius;
            public int Duration;              // seconds; 0 = leftovers are kept
            public string Banner = "";
            public readonly List<WaveSpec> Waves = new List<WaveSpec>();
            public int NextWave;              // index of the next wave to spawn
            public float NextSpawnAt;
            public float StartedAt;
            public float EndsAt;              // 0 = never
            public bool Done;                 // every wave spawned (leftovers may still be alive)
            public readonly List<ZDOID> Spawned = new List<ZDOID>();
            public int SpawnedTotal;
            public string Note = "";          // last thing worth telling the admin (skipped prefab, ...)
        }

        // The current raid, kept after its last wave so "Stop" can still remove the leftovers. Starting
        // a new raid drops the old bookkeeping (its creatures stay in the world; the log says so).
        private static Raid _raid;

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _maxLevelCfg = cfg.Bind("Features", "RaidMaxLevel", 3,
                    "Highest creature level the raid composer may spawn (1 = no stars, 3 = two stars, max 10). The panel offers 1-3; anything above needs this raised, like BossRushLevel does for the boss rush.");
        }

        // ==================== lifecycle ====================

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                // A listen-server host back at the main menu: the ZDOIDs we remembered belong to a world
                // that is gone. Drop the bookkeeping so a raid can never be "stopped" into the next world.
                _raid = null;
                return;
            }
            var now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + TickSeconds;
            var raid = _raid;
            if (raid == null) return;
            try { Step(raid, now); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid tick failed: {e.Message}"); }
        }

        private static void Step(Raid raid, float now)
        {
            if (!raid.Done && now >= raid.NextSpawnAt)
            {
                SpawnWave(raid);
                raid.NextSpawnAt = now + raid.Interval;
                if (raid.NextWave >= raid.Waves.Count)
                {
                    raid.Done = true;
                    if (raid.Duration > 0) raid.EndsAt = now + raid.Duration;
                    CompanionPlugin.FeatureLog($"Wave8 raid: all {raid.Waves.Count} wave(s) spawned ({raid.SpawnedTotal} creatures)");
                }
                PushState(raid.Starter);
            }
            if (raid.Done && raid.EndsAt > 0f && now >= raid.EndsAt)
            {
                var removed = Cleanup(raid);
                _raid = null;
                Wave8Toolkit.AnnounceAll("The raid is over.");
                CompanionPlugin.FeatureLog($"Wave8 raid: duration elapsed, removed {removed} leftover creature(s)");
                PushState(raid.Starter);
            }
        }

        // ==================== AP_SrvRaidStart ====================

        internal static void OnRaidStart(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRaidStart")) return;

            int ver, interval, duration, nWaves; float radius; bool hasY; string banner; Vector3 origin;
            var specs = new List<WaveSpec>();
            try
            {
                ver = pkg.ReadInt();
                origin = pkg.ReadVector3();
                hasY = pkg.ReadBool();
                interval = pkg.ReadInt();
                radius = pkg.ReadSingle();
                duration = pkg.ReadInt();
                banner = pkg.ReadString();
                nWaves = pkg.ReadInt();
                if (nWaves < 0 || nWaves > MaxWaves) return;
                for (var i = 0; i < nWaves; i++)
                {
                    var spec = new WaveSpec
                    {
                        Prefab = Wave8Toolkit.CleanName(pkg.ReadString(), MaxPrefabLen),
                        Count = pkg.ReadInt(),
                        Level = pkg.ReadInt(),
                    };
                    specs.Add(spec);
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRaidStart: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            if (_raid != null && !_raid.Done)
            {
                CompanionPlugin.NotifySender(sender, "A raid is already running - stop it first.");
                return;
            }
            if (ZNetScene.instance == null)
            {
                CompanionPlugin.NotifySender(sender, "Raid not started: the world is not loaded yet.");
                return;
            }

            // Caps are enforced HERE, never trusted from the panel.
            interval = Mathf.Clamp(interval, MinInterval, MaxInterval);
            radius = Mathf.Clamp(float.IsNaN(radius) ? MinRadius : radius, MinRadius, MaxRadius);
            duration = Mathf.Clamp(duration, 0, MaxDuration);
            banner = Wave8Toolkit.CleanText(banner, MaxBannerLen);
            if (float.IsNaN(origin.x) || float.IsInfinity(origin.x) || float.IsNaN(origin.z) || float.IsInfinity(origin.z) ||
                float.IsNaN(origin.y) || float.IsInfinity(origin.y))
                origin = Wave8Toolkit.SenderPosition(sender);
            if (!hasY) origin.y = GroundY(origin.x, origin.z, origin.y);

            var raid = new Raid
            {
                Starter = sender,
                StarterName = CompanionPlugin.SenderDisplayName(sender) ?? "",
                Origin = origin,
                Interval = interval,
                Radius = radius,
                Duration = duration,
                Banner = banner,
            };
            var total = 0;
            var skipped = new List<string>();
            foreach (var spec in specs)
            {
                if (spec.Prefab.Length == 0) continue;
                var go = ResolveCreature(spec.Prefab);
                if (go == null) { skipped.Add(spec.Prefab); continue; }
                spec.Go = go;
                spec.Count = Mathf.Clamp(spec.Count, 1, MaxPerWave);
                spec.Level = Mathf.Clamp(spec.Level, 1, MaxLevel);
                if (total + spec.Count > MaxTotal) spec.Count = MaxTotal - total;   // the last wave absorbs the cap
                if (spec.Count <= 0) break;
                total += spec.Count;
                raid.Waves.Add(spec);
            }
            if (skipped.Count > 0)
                raid.Note = "Skipped (not a creature prefab here): " + Wave8Toolkit.CleanText(string.Join(", ", skipped.ToArray()), MaxNoteLen - 40);
            if (raid.Waves.Count == 0)
            {
                CompanionPlugin.NotifySender(sender, "Raid not started: no wave names a creature prefab that exists on this server.");
                return;
            }

            if (_raid != null)
                CompanionPlugin.FeatureLog($"Wave8 raid: previous raid's {Alive(_raid)} leftover creature(s) are no longer tracked (new raid started)");
            raid.StartedAt = Time.unscaledTime;
            raid.NextSpawnAt = raid.StartedAt;   // first wave on the very next tick
            _raid = raid;

            var text = banner.Length > 0 ? banner
                : $"A raid begins near {Wave8Toolkit.F(origin.x)}, {Wave8Toolkit.F(origin.z)}!";
            Wave8Toolkit.AnnounceAll(text);

            CompanionPlugin.SrvAudit(sender, "RAID-START",
                $"waves={raid.Waves.Count} total={total} interval={interval}s radius={Wave8Toolkit.F(radius)} duration={duration}s at={Wave8Toolkit.F(origin.x)},{Wave8Toolkit.F(origin.z)} banner={banner}");
            CompanionPlugin.FeatureLog($"Wave8 raid started by {raid.StarterName}: {raid.Waves.Count} wave(s), {total} creatures, every {interval}s, r={Wave8Toolkit.F(radius)} at {Wave8Toolkit.F(origin.x)},{Wave8Toolkit.F(origin.z)}");
            try { Wave1AuditRpc.PostModLog($"RAID START {raid.Waves.Count} wave(s), {total} creatures (by {raid.StarterName})"); }
            catch (Exception) { }
            CompanionPlugin.NotifySender(sender, raid.Note.Length > 0
                ? $"Raid started ({raid.Waves.Count} wave(s), {total} creatures). {raid.Note}"
                : $"Raid started: {raid.Waves.Count} wave(s), {total} creatures.");
            PushState(sender);
        }

        // ==================== AP_SrvRaidStop ====================

        internal static void OnRaidStop(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRaidStop")) return;
            var raid = _raid;
            if (raid == null)
            {
                CompanionPlugin.NotifySender(sender, "No raid is running (nothing left to remove).");
                PushState(sender);
                return;
            }
            int removed;
            try { removed = Cleanup(raid); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid cleanup failed: {e.Message}"); removed = 0; }
            _raid = null;
            var wasSpawning = !raid.Done;
            Wave8Toolkit.AnnounceAll("The raid has been called off.");
            CompanionPlugin.SrvAudit(sender, "RAID-STOP", $"removed={removed} wavesDone={raid.NextWave}/{raid.Waves.Count} spawned={raid.SpawnedTotal}");
            CompanionPlugin.FeatureLog($"Wave8 raid stopped by {CompanionPlugin.SenderDisplayName(sender)}: removed {removed} creature(s){(wasSpawning ? ", remaining waves cancelled" : "")}");
            try { Wave1AuditRpc.PostModLog($"RAID STOP removed {removed} (by {CompanionPlugin.SenderDisplayName(sender)})"); }
            catch (Exception) { }
            CompanionPlugin.NotifySender(sender, $"Raid stopped; removed {removed} creature(s).");
            PushState(sender);
            if (raid.Starter != sender) PushState(raid.Starter);
        }

        // ==================== AP_SrvRaidStateReq -> AP_RaidState ====================

        // Polled by the panel while the card is open, hence not chokepoint-audited; a moderator-tier
        // admin without the builder grant can still watch (start grant OR the read's own name).
        internal static void OnRaidStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRaidStateReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvRaidStart")) return;
            PushState(sender);
        }

        private static void PushState(long to)
        {
            if (to == 0L) return;
            try { CompanionPlugin.ReplyTo(to, "AP_RaidState", BuildState()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_RaidState reply failed: {e.Message}"); }
        }

        private static ZPackage BuildState()
        {
            var raid = _raid;
            var now = Time.unscaledTime;
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(raid != null);
            pkg.Write(raid != null && !raid.Done);
            pkg.Write(raid != null ? raid.NextWave : 0);
            pkg.Write(raid != null ? raid.Waves.Count : 0);
            pkg.Write(raid != null ? Alive(raid) : 0);
            pkg.Write(raid != null ? raid.SpawnedTotal : 0);
            pkg.Write(raid != null && !raid.Done ? Mathf.Max(0f, raid.NextSpawnAt - now) : -1f);
            pkg.Write(raid != null && raid.Done && raid.EndsAt > 0f ? Mathf.Max(0, Mathf.CeilToInt(raid.EndsAt - now)) : -1);
            pkg.Write(raid != null ? raid.Banner : "");
            pkg.Write(raid != null ? raid.StarterName : "");
            pkg.Write(raid != null ? raid.Note : "");
            return pkg;
        }

        // ==================== spawning / removal ====================

        private static void SpawnWave(Raid raid)
        {
            if (raid.NextWave >= raid.Waves.Count) return;
            var wave = raid.Waves[raid.NextWave++];
            var spawned = 0;
            for (var i = 0; i < wave.Count; i++)
            {
                try
                {
                    var off = UnityEngine.Random.insideUnitCircle * raid.Radius;
                    var pos = new Vector3(raid.Origin.x + off.x, raid.Origin.y, raid.Origin.z + off.y);
                    pos.y = GroundY(pos.x, pos.z, raid.Origin.y) + 0.5f;
                    var go = UnityEngine.Object.Instantiate(wave.Go, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                    var nview = go.GetComponent<ZNetView>();
                    var zdo = nview != null ? nview.GetZDO() : null;
                    if (zdo != null) raid.Spawned.Add(zdo.m_uid);
                    var character = go.GetComponent<Character>();
                    if (character != null && wave.Level > 1) character.SetLevel(wave.Level);
                    spawned++;
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid: spawn of '{wave.Prefab}' failed: {e.Message}"); }
            }
            raid.SpawnedTotal += spawned;
            var stars = wave.Level > 1 ? $" ({wave.Level - 1}-star)" : "";
            Wave8Toolkit.AnnounceAll($"Raid wave {raid.NextWave}/{raid.Waves.Count}: {spawned}x {wave.Prefab}{stars}!");
            CompanionPlugin.FeatureLog($"Wave8 raid wave {raid.NextWave}/{raid.Waves.Count}: {spawned}x {wave.Prefab} level {wave.Level}");
        }

        // CompanionPlugin.OnServerUndo's removal, verbatim in spirit: an instance (listen server) is
        // destroyed through its ZNetView; a bare ZDO (dedicated server, where the nearest client owns the
        // creature) is claimed first, because DestroyZDO only queues ZDOs the caller owns.
        private static int Cleanup(Raid raid)
        {
            var removed = 0;
            var ids = new List<ZDOID>(raid.Spawned);
            raid.Spawned.Clear();
            foreach (var id in ids)
            {
                try
                {
                    if (ZDOMan.instance == null) break;
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null) continue;   // already dead or despawned
                    var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                    var nview = go != null ? go.GetComponent<ZNetView>() : null;
                    if (nview != null) { nview.ClaimOwnership(); nview.Destroy(); removed++; }
                    else
                    {
                        zdo.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.DestroyZDO(zdo);
                        removed++;
                    }
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid: removal of a spawned creature failed: {e.Message}"); }
            }
            return removed;
        }

        // "Alive" = the ZDO still exists. A killed creature's owner destroys its ZDO once the ragdoll is
        // spawned, so this lags a death by a second or two — fine for a status line.
        private static int Alive(Raid raid)
        {
            if (raid == null || ZDOMan.instance == null) return 0;
            var n = 0;
            for (var i = 0; i < raid.Spawned.Count; i++)
            {
                try { if (ZDOMan.instance.GetZDO(raid.Spawned[i]) != null) n++; }
                catch (Exception) { }
            }
            return n;
        }

        // Creatures only: a Character that is not a Player (a raid of "Beech1" would be a very slow raid).
        private static GameObject ResolveCreature(string name)
        {
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (go == null) return null;
                var ch = go.GetComponent<Character>();
                if (ch == null || go.GetComponent<Player>() != null) return null;
                return go;
            }
            catch (Exception) { return null; }
        }

        // Ground height ladder. ZoneSystem.GetGroundHeight is a terrain raycast: it answers on a listen
        // server (physics loaded around the host) and fails on a dedicated server, which has no colliders;
        // there WorldGenerator's generated height is the next best (it ignores terrain edits, and the
        // creature simply falls the last metre). When both are unavailable the caller's own Y is kept.
        private static float GroundY(float x, float z, float fallback)
        {
            try
            {
                var zs = ZoneSystem.instance;
                float h;
                if (zs != null && zs.GetGroundHeight(new Vector3(x, 0f, z), out h)) return h;
            }
            catch (Exception) { }
            try
            {
                var wg = WorldGenerator.instance;
                if (wg != null)
                {
                    var h = wg.GetHeight(x, z);
                    if (!float.IsNaN(h) && !float.IsInfinity(h)) return h;
                }
            }
            catch (Exception e)
            {
                if (!_groundWarned)
                {
                    _groundWarned = true;
                    CompanionPlugin.FeatureLog($"Wave8 raid: ground height unavailable ({e.Message}); spawns use the origin's height.");
                }
            }
            return fallback;
        }
    }
}

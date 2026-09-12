using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 2 — server world scanning (census / hotspots / cleanup) ====================
    // Three admin tools that all need the same thing: a walk over EVERY ZDO in the world. On a populated
    // server that is 100k-500k objects, so the walk is a single shared, cancellable, TIME-BOXED scanner
    // driven from Tick — never from an RPC handler. A synchronous sweep would freeze the simulation for
    // seconds (ZDOMan.GetAllZDOsWithPrefabIterative is deliberately incremental for exactly this reason,
    // ZDOMan.cs:1126-1168, ~400 sectors per call).
    //
    // Enumeration source (decompiled ZDOMan, assembly_valheim):
    //   * m_objectsBySector : List<ZDO>[512*512]  (ZDOMan.cs:88, sized from ZNet.m_zdoSectorsWidth=512)
    //   * m_objectsByOutsideSector : Dictionary<Vector2i, List<ZDO>>  (ZDOMan.cs:76) — objects whose sector
    //     falls outside the array (including sector-invalidated ZDOs).
    //   Every ZDO lives in exactly one of those two containers (AddToSector, ZDOMan.cs:405-431), so walking
    //   them covers the world without the 3-6 MB key snapshot that iterating m_objectsByID would need. The
    //   array slot is read fresh every frame and the array LENGTH is re-checked, so a world reload aborts the
    //   scan instead of walking a stale container. Within one frame nothing mutates the lists (we call no
    //   engine code while iterating and Unity is single-threaded here), so index iteration cannot throw
    //   InvalidOperationException. Between frames a ZDO may move sectors, so a moving object can be missed
    //   or counted twice — the census is a best-effort snapshot, never an accounting ledger.
    //   If m_objectsBySector cannot be resolved (game update renamed it) we fall back to a one-shot snapshot
    //   of m_objectsByID.Values and walk that array instead; if neither resolves, the feature reports itself
    //   unavailable and nothing crashes.
    //
    // Deletion follows the claim-then-destroy rule the undo path documents (CompanionPlugin.cs:340-349):
    // ZDOMan.DestroyZDO is a SILENT no-op unless the caller owns the ZDO (ZDOMan.cs:630-635), so we
    // SetOwner(ZDOMan.GetSessionID()) first.
    internal static class Wave2World
    {
        private const int Ver = 1;                  // wire version — bump, never reorder

        private const int CensusCap = 40;           // AP_CensusData shipped cap
        private const int HotspotCap = 25;          // AP_HotspotData shipped cap
        private const int CleanupBreakdownCap = 20; // AP_CleanupResult shipped cap
        private const int HardDeleteCap = 20000;    // hard ceiling on deletions per run (contract)
        private const int DeletesPerFrame = 500;    // keeps ZDOMan's destroy broadcast batches sane
        private const int WorkUnitsPerClockRead = 512;
        private const int MaxTrackedZones = 50000;  // 50k zones = 200 km^2 of occupied world; a safety valve
        private const int MaxMinutes = 525600;      // one year
        private const int MinCleanupMinutes = 1;    // never touch something that dropped seconds ago

        // "spawntime" == ZDOVars.s_spawnTime (ZDOVars.cs:333). Written as the literal so a renamed ZDOVars
        // field cannot break us: the KEY is what is persisted in the save, and it never changes.
        private static readonly int SpawnTimeHash = "spawntime".GetStableHashCode();

        // ---- config ----
        private static ConfigEntry<int> _budgetMs;
        private static ConfigEntry<int> _maxDeletions;
        private static ConfigEntry<bool> _enableCleanup;

        private static int BudgetMs => _budgetMs != null ? Mathf.Clamp(_budgetMs.Value, 1, 16) : 8;
        private static int MaxDeletions => _maxDeletions != null ? Mathf.Clamp(_maxDeletions.Value, 1, HardDeleteCap) : HardDeleteCap;
        private static bool CleanupEnabled => _enableCleanup == null || _enableCleanup.Value;

        // ---- prefab hash -> readable name, built ONCE per ZNetScene ----
        private static Dictionary<int, string> _nameByHash;
        private static HashSet<int> _itemFamily;
        private static object _mapScene;

        // ---- reflection handles (resolved once, re-checked when null) ----
        private static FieldInfo _fSectors, _fOutside, _fById;
        private static bool _fieldsProbed;

        // ---- the one and only scan ----
        private static ScanJob _job;
        private static readonly HashSet<long> LivePeers = new HashSet<long>();

        /// <summary>True while a world scan occupies the scanner (sibling wave-2 modules may want this).</summary>
        internal static bool ScanInProgress => _job != null;

        private const int KindCensus = 0;
        private const int KindHotspot = 1;
        private const int KindCleanup = 2;

        private const int PhaseSectors = 0;
        private const int PhaseOutside = 1;
        private const int PhaseFlat = 2;
        private const int PhaseDelete = 3;
        private const int PhaseDone = 4;

        private sealed class ZoneBucket
        {
            public int Count;
            public readonly Dictionary<int, int> ByPrefab = new Dictionary<int, int>();
        }

        private sealed class ScanJob
        {
            public int Kind;
            public long Requester;
            public int TopN;

            // cleanup parameters
            public int Mode;
            public int OlderThanMinutes;
            public bool DryRun;

            // progress
            public int Phase;
            public int SectorIndex;
            public int SectorLen;
            public int OutsideIndex;
            public List<List<ZDO>> Outside;
            public ZDO[] Flat;
            public int FlatIndex;

            // results
            public int Scanned;
            public int DroppedItems;
            public readonly Dictionary<int, int> Counts = new Dictionary<int, int>();
            public Dictionary<long, ZoneBucket> Zones;
            public int Matched;
            public int Removed;
            public int SkippedNoStamp;
            public Dictionary<int, int> MatchedByPrefab;
            public List<ZDOID> Doomed;
            public int DeleteIndex;

            public readonly Stopwatch Watch = new Stopwatch();
        }

        // ==================== lifecycle ====================

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _budgetMs = cfg.Bind("Features", "WorldScanBudgetMs", 8,
                    "Milliseconds per frame the world scanner (census / hotspots / cleanup) may spend. Lower = slower scans but zero impact on a busy server. Clamped to 1-16.");
                _maxDeletions = cfg.Bind("Features", "WorldCleanupMaxPerRun", 20000,
                    "Hard ceiling on how many ZDOs one cleanup run may delete. Clamped to 1-20000.");
                _enableCleanup = cfg.Bind("Features", "EnableWorldCleanup", true,
                    "Allow admins to run world cleanup (deleting old dropped items). Read-only census and hotspot scans are unaffected by this switch. Cleanup is owner-only when tiered roles are on and always supports a dry run.");
            }

            // Census/hotspots are moderator-grade READS; cleanup destroys world data, so it stays owner-only
            // (null) when roles are enforced. These three are user-initiated (button press), never polled, so
            // the audit line the chokepoint writes per call is signal rather than noise.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvCensusReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvHotspotReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvCleanupReq", null);

            try { Harmony.CreateAndPatchAll(typeof(RpcRegisterPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave2World RpcRegisterPatch failed (census/hotspot/cleanup unavailable): {e.Message}"); }
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (_job == null) return;   // self-throttling: the scanner only costs anything while a job runs
            try { StepScan(); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"World scan step failed: {e.Message}");
                AbortJob("internal error");
            }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    ZRoutedRpc.instance.Register<int>("AP_SrvCensusReq", OnCensusReq);
                    ZRoutedRpc.instance.Register("AP_SrvHotspotReq", new Action<long>(OnHotspotReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvCleanupReq", OnCleanupReq);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"World-scan RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== request handlers (queue only — Tick does the work) ====================

        private static void OnCensusReq(long sender, int topN)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvCensusReq")) return;

            topN = Mathf.Clamp(topN, 1, CensusCap);
            if (_job != null)
            {
                // Contract: a second request while a scan runs answers running=true plus current progress.
                SendCensus(_job, sender, topN, running: true);
                return;
            }
            var job = NewJob(KindCensus, sender);
            if (job == null) { SendCensus(null, sender, topN, running: false); return; }
            job.TopN = topN;
            _job = job;
        }

        private static void OnHotspotReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvHotspotReq")) return;

            if (_job != null)
            {
                // AP_HotspotData carries no "running" flag, so the honest answer is an empty result plus a
                // human-readable reason — never a silent drop, or the panel would spin forever.
                CompanionPlugin.NotifySender(sender, "A world scan is already running - try again in a moment.");
                SendHotspots(null, sender);
                return;
            }
            var job = NewJob(KindHotspot, sender);
            if (job == null) { SendHotspots(null, sender); return; }
            job.Zones = new Dictionary<long, ZoneBucket>();
            _job = job;
        }

        // ZPackage: int mode (0 dropped items, 1 orphans, 2 both), int olderThanMinutes, bool dryRun.
        private static void OnCleanupReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvCleanupReq")) return;

            int mode, minutes; bool dryRun;
            try { mode = pkg.ReadInt(); minutes = pkg.ReadInt(); dryRun = pkg.ReadBool(); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvCleanupReq: malformed packet dropped ({e.Message})");
                return;
            }
            mode = Mathf.Clamp(mode, 0, 2);
            minutes = Mathf.Clamp(minutes, MinCleanupMinutes, MaxMinutes);

            if (!CleanupEnabled && !dryRun)
            {
                CompanionPlugin.NotifySender(sender, "World cleanup is disabled in the server config (EnableWorldCleanup=false).");
                SendCleanup(null, sender, dryRun);
                return;
            }
            if (_job != null)
            {
                CompanionPlugin.NotifySender(sender, "A world scan is already running - try again in a moment.");
                SendCleanup(null, sender, dryRun);
                return;
            }
            if (!EnsureNameMap() || _itemFamily == null || _itemFamily.Count == 0)
            {
                // Without the prefab registry we cannot tell a dropped item from a longhouse. Refusing is the
                // only safe answer: a cleanup that guesses would delete builds.
                CompanionPlugin.NotifySender(sender, "Cleanup unavailable: the server could not read the prefab registry, so dropped items cannot be identified.");
                SendCleanup(null, sender, dryRun);
                return;
            }

            var job = NewJob(KindCleanup, sender);
            if (job == null) { SendCleanup(null, sender, dryRun); return; }
            job.Mode = mode;
            job.OlderThanMinutes = minutes;
            job.DryRun = dryRun;
            job.MatchedByPrefab = new Dictionary<int, int>();
            if (!dryRun) job.Doomed = new List<ZDOID>();
            _job = job;

            CompanionPlugin.NotifySender(sender,
                $"World cleanup started (mode {mode}, older than {minutes} min{(dryRun ? ", DRY RUN" : "")}). Scanning...");
        }

        // ==================== job setup ====================

        private static ScanJob NewJob(int kind, long requester)
        {
            if (ZDOMan.instance == null) return null;
            EnsureNameMap();   // best effort — unknown hashes simply render as "#<hash>"
            EnsureFields();

            var job = new ScanJob { Kind = kind, Requester = requester };
            var sectors = SectorArray();
            if (sectors != null)
            {
                job.Phase = PhaseSectors;
                job.SectorLen = sectors.Length;
            }
            else
            {
                var flat = FlatSnapshot();
                if (flat == null)
                {
                    CompanionPlugin.FeatureLog("World scan unavailable: neither ZDOMan.m_objectsBySector nor m_objectsByID could be read on this game build.");
                    CompanionPlugin.NotifySender(requester, "World scanning is unavailable on this server build (the game's object store could not be read).");
                    return null;
                }
                job.Phase = PhaseFlat;
                job.Flat = flat;
            }
            job.Watch.Start();
            return job;
        }

        private static void AbortJob(string reason)
        {
            var job = _job;
            _job = null;
            if (job == null) return;
            CompanionPlugin.FeatureLog($"World scan aborted ({reason}) after {job.Scanned} ZDOs.");
            CompanionPlugin.NotifySender(job.Requester, $"World scan aborted: {reason}.");
            // Answer the pending request anyway so the panel never waits forever.
            switch (job.Kind)
            {
                case KindCensus: SendCensus(null, job.Requester, job.TopN, running: false); break;
                case KindHotspot: SendHotspots(null, job.Requester); break;
                case KindCleanup: SendCleanup(null, job.Requester, job.DryRun); break;
            }
        }

        // ==================== the frame-spread scanner ====================

        private static void StepScan()
        {
            var job = _job;
            if (job == null) return;
            if (ZDOMan.instance == null) { AbortJob("world unloaded"); return; }

            RefreshLivePeers();
            var sw = Stopwatch.StartNew();
            var budget = BudgetMs;
            var deletedThisFrame = 0;

            while (job.Phase != PhaseDone && sw.ElapsedMilliseconds < budget)
            {
                switch (job.Phase)
                {
                    case PhaseSectors: RunSectors(job, sw, budget); break;
                    case PhaseOutside: RunOutside(job, sw, budget); break;
                    case PhaseFlat: RunFlat(job, sw, budget); break;
                    case PhaseDelete:
                        RunDelete(job, sw, budget, ref deletedThisFrame);
                        if (deletedThisFrame >= DeletesPerFrame) return;   // resume next frame
                        break;
                    default: job.Phase = PhaseDone; break;
                }
            }

            if (job.Phase == PhaseDone) FinishJob(job);
        }

        private static void RunSectors(ScanJob job, Stopwatch sw, int budget)
        {
            var arr = SectorArray();
            if (arr == null || arr.Length != job.SectorLen)
            {
                // World reloaded under us: stop walking a container that no longer describes this world.
                job.Phase = PhaseOutside;
                return;
            }
            var since = 0;
            while (job.SectorIndex < arr.Length)
            {
                var list = arr[job.SectorIndex++];
                if (list != null && list.Count > 0)
                {
                    for (var i = 0; i < list.Count; i++) ProcessZdo(job, list[i]);
                    since += list.Count;
                }
                else since++;

                if (since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Phase = PhaseOutside;
        }

        private static void RunOutside(ScanJob job, Stopwatch sw, int budget)
        {
            if (job.Outside == null)
            {
                // Snapshot the (small) outside-sector lists once: this dictionary is enumerated across
                // frames, and a live Dictionary enumeration would throw the moment anything moved.
                job.Outside = OutsideLists() ?? new List<List<ZDO>>();
                job.OutsideIndex = 0;
            }
            var since = 0;
            while (job.OutsideIndex < job.Outside.Count)
            {
                var list = job.Outside[job.OutsideIndex++];
                if (list != null && list.Count > 0)
                {
                    for (var i = 0; i < list.Count; i++) ProcessZdo(job, list[i]);
                    since += list.Count;
                }
                else since++;

                if (since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Phase = job.Doomed != null && job.Doomed.Count > 0 ? PhaseDelete : PhaseDone;
        }

        private static void RunFlat(ScanJob job, Stopwatch sw, int budget)
        {
            if (job.Flat == null) { job.Phase = PhaseDone; return; }
            var since = 0;
            while (job.FlatIndex < job.Flat.Length)
            {
                ProcessZdo(job, job.Flat[job.FlatIndex++]);
                if (++since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Flat = null;
            job.Phase = job.Doomed != null && job.Doomed.Count > 0 ? PhaseDelete : PhaseDone;
        }

        private static void ProcessZdo(ScanJob job, ZDO zdo)
        {
            if (zdo == null) return;
            try
            {
                if (!zdo.IsValid()) return;
                job.Scanned++;
                var hash = zdo.GetPrefab();

                int c;
                job.Counts[hash] = job.Counts.TryGetValue(hash, out c) ? c + 1 : 1;
                var isItem = _itemFamily != null && _itemFamily.Contains(hash);
                if (isItem) job.DroppedItems++;

                if (job.Kind == KindHotspot && job.Zones != null) BucketZone(job, zdo, hash);
                if (job.Kind == KindCleanup) MatchForCleanup(job, zdo, hash, isItem);
            }
            catch (Exception)
            {
                // A single malformed ZDO must never take down a world-wide sweep.
            }
        }

        private static void BucketZone(ScanJob job, ZDO zdo, int hash)
        {
            Vector2s zone;   // shorts since 1.0.12
            try { zone = ZoneSystem.GetZone(zdo.GetPosition()); }
            catch (Exception) { return; }   // engine math moved: hotspots degrade, the scan continues

            int zx = zone.x, zy = zone.y;   // widen the 1.0.12 shorts first: same key layout as before, no CS0675
            var key = ((long)zx << 32) | (uint)zy;
            ZoneBucket bucket;
            if (!job.Zones.TryGetValue(key, out bucket))
            {
                if (job.Zones.Count >= MaxTrackedZones) return;   // safety valve, never hit by a real world
                bucket = new ZoneBucket();
                job.Zones[key] = bucket;
            }
            bucket.Count++;
            int c;
            bucket.ByPrefab[hash] = bucket.ByPrefab.TryGetValue(hash, out c) ? c + 1 : 1;
        }

        // ---- cleanup matching ----
        //
        // ORPHAN / MODE DEFINITIONS (deliberately narrow — see the header of this file and the limitations):
        //   mode 0 "dropped items": the prefab asset carries an ItemDrop component AND the ZDO has a
        //        "spawntime" stamp AND (now - spawntime) >= olderThanMinutes. Owner is irrelevant.
        //   mode 1 "orphan / unowned": everything mode 0 requires PLUS an absent owner — no owner recorded,
        //        or an owner id that is neither this server's ZDOMan session nor any connected peer. This is
        //        the variant that is safe to run while players are online: an item somebody is standing next
        //        to is owned by that player's client and is therefore never touched.
        //   mode 2 "both": the union, which by construction equals mode 0. It exists so the wire contract
        //        stays stable and so a future, wider orphan rule needs no protocol change.
        // Every OTHER family (builds, creatures, terrain, containers, ships, tombstones, ...) is excluded
        // outright and never matched by any mode, because a modern save records no creation time for them:
        // ZDOExtraData's TimeCreated map is populated only while loading a pre-v24 world (ZDO.LoadOldFormat
        // -> ZDOExtraData.SetTimeCreated) and is empty otherwise, so there is no way to age-filter them
        // safely. ItemDrop-family ZDOs that are missing their "spawntime" stamp are likewise never deleted;
        // they are counted into SkippedNoStamp and reported as a labelled row so the admin can see that
        // "matched" is not the whole picture.
        private static void MatchForCleanup(ScanJob job, ZDO zdo, int hash, bool isItem)
        {
            if (!isItem) return;

            bool hasStamp;
            var oldEnough = OldEnough(zdo, job.OlderThanMinutes, out hasStamp);
            if (!hasStamp) { job.SkippedNoStamp++; return; }
            if (!oldEnough) return;
            if (job.Mode == 1 && !OwnerAbsent(zdo)) return;

            job.Matched++;
            int c;
            job.MatchedByPrefab[hash] = job.MatchedByPrefab.TryGetValue(hash, out c) ? c + 1 : 1;

            if (job.Doomed == null) return;                       // dry run: count only
            if (job.Doomed.Count >= MaxDeletions) return;         // cap reached; matched keeps counting
            job.Doomed.Add(zdo.m_uid);
        }

        private static void RunDelete(ScanJob job, Stopwatch sw, int budget, ref int deletedThisFrame)
        {
            if (job.Doomed == null) { job.Phase = PhaseDone; return; }
            var man = ZDOMan.instance;
            if (man == null) { job.Phase = PhaseDone; return; }

            while (job.DeleteIndex < job.Doomed.Count)
            {
                var id = job.Doomed[job.DeleteIndex++];
                try
                {
                    // Re-resolve by ZDOID, never by a cached ZDO reference: ZDOPool recycles ZDO objects
                    // (ZDOMan.HandleDestroyedZDO -> ZDOPool.Release), so a stale reference can silently become
                    // a different, live object. ZDOIDs are globally unique, so this lookup is exact.
                    var zdo = man.GetZDO(id);
                    if (zdo == null || !zdo.IsValid()) continue;
                    zdo.SetOwner(ZDOMan.GetSessionID());   // DestroyZDO is a no-op without ownership
                    man.DestroyZDO(zdo);
                    job.Removed++;
                }
                catch (Exception) { }

                if (++deletedThisFrame >= DeletesPerFrame) return;
                if ((deletedThisFrame & 63) == 0 && sw.ElapsedMilliseconds >= budget) return;
            }
            job.Phase = PhaseDone;
        }

        private static void FinishJob(ScanJob job)
        {
            _job = null;
            job.Watch.Stop();
            switch (job.Kind)
            {
                case KindCensus:
                    SendCensus(job, job.Requester, job.TopN, running: false);
                    break;
                case KindHotspot:
                    SendHotspots(job, job.Requester);
                    break;
                case KindCleanup:
                    SendCleanup(job, job.Requester, job.DryRun);
                    ReportCleanup(job);
                    break;
            }
        }

        private static void ReportCleanup(ScanJob job)
        {
            var admin = CompanionPlugin.SenderDisplayName(job.Requester);
            var detail = $"mode={job.Mode} olderThanMin={job.OlderThanMinutes} dryRun={job.DryRun} " +
                         $"scanned={job.Scanned} matched={job.Matched} removed={job.Removed} " +
                         $"noTimestampSkipped={job.SkippedNoStamp} ms={job.Watch.ElapsedMilliseconds}";
            CompanionPlugin.FeatureLog($"World cleanup by {admin}: {detail}");
            CompanionPlugin.NotifySender(job.Requester,
                job.DryRun
                    ? $"Cleanup dry run: {job.Matched} object(s) would be removed (scanned {job.Scanned} in {job.Watch.ElapsedMilliseconds} ms)."
                    : $"Cleanup done: removed {job.Removed} of {job.Matched} matched object(s) (scanned {job.Scanned} in {job.Watch.ElapsedMilliseconds} ms).");

            if (job.DryRun) return;   // a dry run changed nothing; the chokepoint already logged the request
            CompanionPlugin.SrvAudit(job.Requester, "WORLDCLEANUP", detail);
            Wave1AuditRpc.PostModLog($"WORLD CLEANUP removed {job.Removed} object(s) (mode {job.Mode}, older than {job.OlderThanMinutes} min) by {admin}");
            Wave1Moderation.NotifyOnlineAdmins($"World cleanup: {job.Removed} object(s) removed by {admin}");
        }

        // ==================== replies ====================

        // AP_CensusData: {int ver, bool running, int scanned, int totalZdos, int shipped(<=40),
        //                 shipped x (string prefabName, int count), int droppedItems, long scanMillis}
        private static void SendCensus(ScanJob job, long uid, int topN, bool running)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(running);
            pkg.Write(job != null ? job.Scanned : 0);
            pkg.Write(TotalZdos());

            var top = running || job == null ? null : TopCounts(job.Counts, Mathf.Clamp(topN, 1, CensusCap));
            var n = top != null ? top.Count : 0;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                pkg.Write(PrefabName(top[i].Key));
                pkg.Write(top[i].Value);
            }

            pkg.Write(job != null ? job.DroppedItems : 0);
            pkg.Write(job != null ? job.Watch.ElapsedMilliseconds : 0L);
            Reply(uid, "AP_CensusData", pkg);
        }

        // AP_HotspotData: {int ver, int shipped(<=25),
        //                  shipped x (int zoneX, int zoneY, int zdoCount, string topPrefab), int totalZones}
        private static void SendHotspots(ScanJob job, long uid)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);

            var zones = job != null ? job.Zones : null;
            var list = new List<KeyValuePair<long, ZoneBucket>>();
            if (zones != null)
            {
                foreach (var kv in zones) list.Add(kv);
                list.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
            }
            var n = Math.Min(list.Count, HotspotCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var key = list[i].Key;
                pkg.Write((int)(key >> 32));
                pkg.Write((int)(uint)key);
                pkg.Write(list[i].Value.Count);
                pkg.Write(PrefabName(TopKey(list[i].Value.ByPrefab)));
            }
            pkg.Write(zones != null ? zones.Count : 0);
            Reply(uid, "AP_HotspotData", pkg);
        }

        // AP_CleanupResult: {int ver, bool dryRun, int matched, int removed, int shipped(<=20),
        //                    shipped x (string prefabName, int count)}
        private static void SendCleanup(ScanJob job, long uid, bool dryRun)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(dryRun);
            pkg.Write(job != null ? job.Matched : 0);
            pkg.Write(job != null ? job.Removed : 0);

            var rows = new List<KeyValuePair<string, int>>();
            if (job != null && job.MatchedByPrefab != null)
            {
                var top = TopCounts(job.MatchedByPrefab, CleanupBreakdownCap);
                foreach (var kv in top) rows.Add(new KeyValuePair<string, int>(PrefabName(kv.Key), kv.Value));
            }
            // Synthetic, clearly-labelled trailing row: objects the age filter could not judge because their
            // family stores no timestamp. They were NOT deleted; showing the number is how the admin learns
            // that "matched" is not the whole world.
            if (job != null && job.SkippedNoStamp > 0 && rows.Count < CleanupBreakdownCap)
                rows.Add(new KeyValuePair<string, int>("(skipped: no timestamp)", job.SkippedNoStamp));

            var n = Math.Min(rows.Count, CleanupBreakdownCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++) { pkg.Write(rows[i].Key); pkg.Write(rows[i].Value); }
            Reply(uid, "AP_CleanupResult", pkg);
        }

        // Every census/hotspot/cleanup answer funnels through here, whether it comes from a handler that
        // refused the request or from FinishJob at the end of the frame-spread scan. ReplyTo, not
        // InvokeRoutedRPC: on a listen-server host the requesting admin IS this process, and a routed packet
        // would be discarded by the panel's anti-spoof gate (there is no server peer to authenticate it
        // against). Remote admins still get the ordinary routed reply.
        private static void Reply(long uid, string rpc, ZPackage pkg)
        {
            try { CompanionPlugin.ReplyTo(uid, rpc, pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"{rpc} reply failed: {e.Message}"); }
        }

        // ==================== helpers ====================

        private static List<KeyValuePair<int, int>> TopCounts(Dictionary<int, int> counts, int take)
        {
            var list = new List<KeyValuePair<int, int>>(counts.Count);
            foreach (var kv in counts) list.Add(kv);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > take) list.RemoveRange(take, list.Count - take);
            return list;
        }

        private static int TopKey(Dictionary<int, int> counts)
        {
            var best = 0; var bestCount = -1;
            foreach (var kv in counts)
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            return best;
        }

        private static string PrefabName(int hash)
        {
            string name;
            if (_nameByHash != null && _nameByHash.TryGetValue(hash, out name) && !string.IsNullOrEmpty(name))
                return name;
            return "#" + hash;
        }

        private static int TotalZdos()
        {
            try { return ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0; }
            catch (Exception) { return 0; }
        }

        private static void RefreshLivePeers()
        {
            LivePeers.Clear();
            try
            {
                LivePeers.Add(ZDOMan.GetSessionID());
                var peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
                if (peers == null) return;
                foreach (var p in peers) if (p != null) LivePeers.Add(p.m_uid);
            }
            catch (Exception) { }
        }

        private static bool OwnerAbsent(ZDO zdo)
        {
            try
            {
                if (!zdo.HasOwner()) return true;
                var owner = zdo.GetOwner();
                return owner == 0L || !LivePeers.Contains(owner);
            }
            catch (Exception) { return false; }   // unknown = not an orphan (conservative)
        }

        /// <summary>
        /// Age from the ZDO's own "spawntime" long, which ItemDrop.Awake stamps with ZNet.GetTime().Ticks
        /// (ItemDrop.cs:1103-1106) and compares the same way in GetTimeSinceSpawned (ItemDrop.cs:1150-1154).
        /// Both sides use the WORLD clock (ZNet.m_netTime), not wall-clock UTC, so the comparison is
        /// self-consistent across restarts. hasStamp=false means the ZDO carries no usable timestamp and must
        /// be excluded from every age-filtered operation.
        /// </summary>
        private static bool OldEnough(ZDO zdo, int minutes, out bool hasStamp)
        {
            hasStamp = false;
            long ticks;
            try { ticks = zdo.GetLong(SpawnTimeHash, 0L); }
            catch (Exception) { return false; }
            if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks) return false;
            hasStamp = true;
            try
            {
                var age = (ZNet.instance.GetTime() - new DateTime(ticks)).TotalMinutes;
                return age >= minutes;   // a negative age (clock rolled back) never qualifies
            }
            catch (Exception) { return false; }
        }

        // ---- prefab registry ----

        private static bool EnsureNameMap()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return false;
            if (_nameByHash != null && ReferenceEquals(_mapScene, scene)) return true;

            var names = new Dictionary<int, string>();
            var items = new HashSet<int>();
            try
            {
                // m_namedPrefabs is already hash -> GameObject (ZNetScene.cs:17, filled in Awake) and covers
                // the non-netview prefabs too, so it is the cheapest and most complete source. The public
                // m_prefabs list is the fallback if that private field is ever renamed.
                var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")?.GetValue(scene)
                    as Dictionary<int, GameObject>;
                if (named != null)
                {
                    foreach (var kv in named) RegisterPrefab(names, items, kv.Key, kv.Value);
                }
                else if (scene.m_prefabs != null)
                {
                    foreach (var go in scene.m_prefabs)
                    {
                        if (go == null) continue;
                        RegisterPrefab(names, items, go.name.GetStableHashCode(), go);
                    }
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Prefab name map unavailable ({e.Message}); census will show raw hashes and cleanup will refuse to run.");
                return false;
            }
            if (names.Count == 0) return false;

            _nameByHash = names;
            _itemFamily = items;
            _mapScene = scene;
            CompanionPlugin.FeatureLog($"World scanner: prefab map built ({names.Count} prefabs, {items.Count} in the ItemDrop family).");
            return true;
        }

        // The dropped-item heuristic, stated plainly: a prefab belongs to the ItemDrop family iff its PREFAB
        // ASSET carries an ItemDrop component. Prefab assets (unlike scene instances) do exist on a dedicated
        // server — the shipping give/spawn code already reads them this way (CompanionPlugin.cs:232) — and the
        // component test is exact, unlike any name pattern. If the component type is ever gone the family set
        // simply stays empty and cleanup refuses to run rather than guessing.
        private static void RegisterPrefab(Dictionary<int, string> names, HashSet<int> items, int hash, GameObject go)
        {
            if (go == null) return;
            names[hash] = go.name;
            try { if (go.GetComponent<ItemDrop>() != null) items.Add(hash); }
            catch (Exception) { }
        }

        // ---- ZDOMan container access ----

        private static void EnsureFields()
        {
            if (_fieldsProbed) return;
            _fieldsProbed = true;
            try
            {
                _fSectors = AccessTools.Field(typeof(ZDOMan), "m_objectsBySector");
                // Gone in 1.0.12 (the grid covers everything now). Type.GetField stays silent where
                // AccessTools.Field would log a HarmonyX warning; a null here simply means "no outside bucket".
                _fOutside = typeof(ZDOMan).GetField("m_objectsByOutsideSector", AccessTools.all);
                _fById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"ZDOMan container reflection failed: {e.Message}");
            }
        }

        private static List<ZDO>[] SectorArray()
        {
            EnsureFields();
            if (_fSectors == null || ZDOMan.instance == null) return null;
            try { return _fSectors.GetValue(ZDOMan.instance) as List<ZDO>[]; }
            catch (Exception) { return null; }
        }

        private static List<List<ZDO>> OutsideLists()
        {
            EnsureFields();
            if (_fOutside == null || ZDOMan.instance == null) return null;
            try
            {
                var dict = _fOutside.GetValue(ZDOMan.instance) as Dictionary<Vector2i, List<ZDO>>;
                if (dict == null) return null;
                var res = new List<List<ZDO>>(dict.Count);
                foreach (var kv in dict) res.Add(kv.Value);
                return res;
            }
            catch (Exception) { return null; }
        }

        // Fallback enumeration when the sector array cannot be read: ONE snapshot of the value collection at
        // job start. Costs an array of N references (a few MB on a huge world) but is immune to the
        // InvalidOperationException a live dictionary walk would throw.
        private static ZDO[] FlatSnapshot()
        {
            EnsureFields();
            if (_fById == null || ZDOMan.instance == null) return null;
            try
            {
                var dict = _fById.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (dict == null) return null;
                var arr = new ZDO[dict.Count];
                dict.Values.CopyTo(arr, 0);
                return arr;
            }
            catch (Exception) { return null; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — world objects (server side): tames, spawners, containers ====================
    // Four admin tools that all work on the ZDO layer of a dedicated server, where creatures, spawners and
    // chests exist only as ZDOs (the nearest client simulates them and owns their ZDO):
    //
    //   #10 Tame roster       AP_SrvTameScanReq -> AP_TameRoster, AP_SrvTameAction, AP_SrvTameCullArea
    //   #11 Creature editor   AP_SrvCreatureEditAudit (audit line only — the edit itself is client-side)
    //   #12 Spawner manager   AP_SrvSpawnerScanReq -> AP_SpawnerList, AP_SrvSpawnerRemove
    //   #16 Chest viewer      AP_SrvChestReadReq -> AP_ChestData, AP_SrvChestEdit, AP_SrvChestSearchReq -> AP_ChestSearch
    //
    // This file holds what the three feature files share: config, RPC registration, the per-prefab
    // component cache, the frame-spread world scanner (tame roster + container search), the frame-spread
    // deletion queue, and the ownership helpers. Feature handlers live in Wave8SrvObjectsTames.cs,
    // Wave8SrvObjectsSpawners.cs and Wave8SrvObjectsChests.cs (same static partial class).
    //
    // ENGINE FACTS (verified by decompiling assembly_valheim on 2026-09-10; line numbers from that decompile)
    // ---------------------------------------------------------------------------------------------------
    // * ZNetScene.instance.GetPrefab(hash) resolves prefab ASSETS on a dedicated server (ZNetScene.cs:136),
    //   so component checks (Character / Tameable / Container / SpawnArea / CreatureSpawner) are exact and
    //   cached per prefab hash. Nothing here matches on a prefab NAME pattern.
    // * ZDOMan.DestroyZDO only queues the id when zdo.IsOwner() (ZDOMan.cs:630-636), so every removal
    //   claims first: zdo.SetOwner(ZDOMan.GetSessionID()). Bulk removals are spread over frames.
    // * OWNERSHIP HANDOFF. ZDOMan.ReleaseZDOS runs every 2 s ON THE SERVER ONLY (ZDOMan.cs:515-519,
    //   574-587) and ReleaseNearbyZDOS (603-628) hands an object to the nearest peer when it has no owner
    //   or its owner's active area no longer covers the sector. For an owner equal to the server's own
    //   session id, IsInPeerActiveArea compares against ZNet.GetReferencePosition() (ZDOMan.cs:589-594),
    //   which a dedicated server never updates — an object the server keeps owning could therefore stay
    //   server-owned for ever, and a server-owned creature is a frozen creature (the server runs no
    //   Character simulation). Rule applied by every write below: claim, write, then hand the object
    //   back to its previous owner when that peer is still connected, else SetOwner(0) so the 2 s release
    //   pass gives it to whoever is nearest. The server never remains the owner of something it touched.
    // * Clients apply an incoming ZDO only when its DataRevision is higher than their copy
    //   (ZDOMan.RPC_ZDOData, ZDOMan.cs:802-827); the claim bumps OwnerRevision, Set(...) bumps
    //   DataRevision, and ForceSendZDO (ZDOMan.cs:1251) makes every peer receive the new state. For
    //   CREATURES that have a connected owner the raw write is avoided altogether: the game's own
    //   ZDO-targeted RPCs (Character.RPC_Heal / RPC_SetTamed, Tameable "SetName" — Character.cs:538-546,
    //   Tameable.cs Awake) are invoked on the owning client exactly as a remote player would, so the
    //   simulating client performs the change and no revision race can revert it. Raw writes are used only
    //   for objects nobody owns (nobody near) — or on a listen-server host with a live instance, where the
    //   component setters run in-process after ClaimOwnership like the panel's piece editor.
    // * ZDO keys are the persisted STRING hashes (ZDOVars.cs): "tamed", "TamedName", "TamedNameAuthor",
    //   "health", "max_health", "level", "items", "InUse", "alive_time". The key text is what lives in the
    //   save file; the C# field name could be renamed by a game update.
    // * ZoneSystem.GetZone(Vector3) is a static 64 m grid (ZoneSystem.cs:2458); ZDOMan.FindSectorObjects
    //   (ZDOMan.cs:841) is public and appends the (2*area+1)^2 sector block — that is the "small zone
    //   block" local query. World-wide walks reuse Wave2SrvWorld's container reflection
    //   (m_objectsBySector / m_objectsByOutsideSector, flat m_objectsByID fallback) with a per-frame
    //   time budget.
    //
    // House rules: one Harmony class per target method, applied in Init() in its own try/catch and reported
    // through Wave2Ops.ReportPatch; every RPC parse in try/catch; every list bounded; reply payloads
    // <= 100 rows; action RPCs registered with the audit chokepoint, periodic reads are not.
    internal static partial class Wave8Objects
    {
        internal const int Ver = 1;   // wire version of every payload in this group — bump, never reorder

        // ---- ZDO keys: persisted string hashes (see header) ----
        private static readonly int KeyTamed = "tamed".GetStableHashCode();
        private static readonly int KeyTamedName = "TamedName".GetStableHashCode();
        private static readonly int KeyTamedNameAuthor = "TamedNameAuthor".GetStableHashCode();
        private static readonly int KeyHealth = "health".GetStableHashCode();
        private static readonly int KeyMaxHealth = "max_health".GetStableHashCode();
        private static readonly int KeyLevel = "level".GetStableHashCode();
        private static readonly int KeyItems = "items".GetStableHashCode();
        private static readonly int KeyInUse = "InUse".GetStableHashCode();
        private static readonly int KeyAliveTime = "alive_time".GetStableHashCode();

        // ---- caps (wire contract; the panel bounds its reads with the same numbers) ----
        private const int TameRowCap = 100;
        private const int SpawnerRowCap = 100;
        private const int ChestRowCap = 100;
        private const int SearchRowCap = 50;
        private const int SpawnerRemoveCap = 200;
        private const int CullAreaCap = 500;         // tames one cull-by-species request may queue
        private const int DeletesPerFrame = 50;      // keeps ZDOMan's destroy broadcast batches sane
        private const int DoomQueueCap = 5000;
        private const int CandidateCap = 5000;       // matches kept for sorting before the cap is applied
        private const int WorkUnitsPerClockRead = 256;
        private const int MaxPetNameLen = 40;
        private const int MaxQueryLen = 64;
        private const int MaxAuditLen = 200;
        private const float MaxCullRadius = 128f;
        private const int MaxZoneBlock = 4;          // 4 zones = 9x9 sectors = 576 m square

        // ---- config (read live; admin tools default ON like EnableWorldCleanup / EnableMassPieceRemove) ----
        private static ConfigEntry<int> _budgetMs;
        private static ConfigEntry<bool> _enableTameActions;
        private static ConfigEntry<bool> _enableSpawnerRemove;
        private static ConfigEntry<bool> _enableChestEdit;

        private static int BudgetMs => _budgetMs != null ? Mathf.Clamp(_budgetMs.Value, 1, 16) : 8;
        private static bool TameActionsEnabled => _enableTameActions == null || _enableTameActions.Value;
        private static bool SpawnerRemoveEnabled => _enableSpawnerRemove == null || _enableSpawnerRemove.Value;
        private static bool ChestEditEnabled => _enableChestEdit == null || _enableChestEdit.Value;

        private static bool _inited;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _budgetMs = cfg.Bind("Features", "ObjectScanBudgetMs", 8,
                    "Milliseconds per frame the world-object scanner (tame roster, container search) may spend. Lower = slower scans but zero impact on a busy server. Clamped to 1-16.");
                _enableTameActions = cfg.Bind("Features", "EnableTameManager", true,
                    "Allow admins to heal, rename, un-tame and cull tamed creatures from the panel. The read-only tame roster scan is unaffected by this switch. Culling by species in a radius is owner-only when tiered roles are on.");
                _enableSpawnerRemove = cfg.Bind("Features", "EnableSpawnerRemove", true,
                    "Allow admins to remove creature spawners / nests from the panel. The spawner list itself is unaffected by this switch. Removal is builder-grade when tiered roles are on.");
                _enableChestEdit = cfg.Bind("Features", "EnableChestEdit", true,
                    "Allow admins to remove, resize and add stacks inside a container from the panel. Viewing and the world-wide container search are unaffected by this switch. Edits are refused while a player has the container open.");
            }

            // Actions go through the chokepoint (audit line + role enforcement before the handler runs).
            // The scan / read requests are polled while a scan runs and are deliberately NOT registered —
            // they are gated with SenderCanFeature in their handlers instead.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTameAction", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTameCullArea", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvCreatureEditAudit", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSpawnerRemove", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvChestEdit", "builder");

            ApplyPatch("Wave8ObjectsRpcRegistration", typeof(Wave8ObjectsRpcRegistration),
                "tame roster / spawner / container RPCs unavailable");
        }

        private static void ApplyPatch(string name, Type patchClass, string degradation)
        {
            var ok = true;
            try { Harmony.CreateAndPatchAll(patchClass); }
            catch (Exception e)
            {
                ok = false;
                CompanionPlugin.FeatureLog($"{name} failed ({degradation}): {e.Message}");
            }
            try { Wave2Ops.ReportPatch(name, ok); }
            catch (Exception) { /* the self-test module is optional */ }
        }

        // Called every frame from the companion's Update. Self-throttling: nothing here costs anything
        // unless a scan job or a deletion queue is live.
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (_job != null)
            {
                try { StepScan(); }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"World-object scan step failed: {e.Message}");
                    AbortJob("internal error");
                }
            }
            if (Doom.Count > 0)
            {
                try { StepDeletes(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"World-object delete step failed: {e.Message}"); }
            }
        }

        // ==================== RPC registration (own ZNet.Awake postfix; stacking is sanctioned) ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave8ObjectsRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTameScanReq", OnTameScanReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTameAction", OnTameAction);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTameCullArea", OnTameCullArea);
                    ZRoutedRpc.instance.Register<string>("AP_SrvCreatureEditAudit", OnCreatureEditAudit);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSpawnerScanReq", OnSpawnerScanReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSpawnerRemove", OnSpawnerRemove);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvChestReadReq", OnChestReadReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvChestEdit", OnChestEdit);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvChestSearchReq", OnChestSearchReq);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"World-object RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== #11 creature editor: audit line only ====================

        // The edit itself happens on the admin's client (ClaimOwnership + Character setters, like the piece
        // editor). The server only records that it happened, so the audit trail shows creature edits next
        // to every other admin action. Nothing else is done with the text.
        private static void OnCreatureEditAudit(long sender, string text)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvCreatureEditAudit")) return;
            var line = Clean(text, MaxAuditLen);
            if (line.Length == 0) return;
            CompanionPlugin.SrvAudit(sender, "CREATURE_EDIT", line);
            CompanionPlugin.FeatureLog($"Creature edit by {Wave1AuditRpc.AdminLabel(sender)}: {line}");
        }

        // ==================== per-prefab component cache ====================

        // Built lazily, one entry per prefab hash the scans meet, rebuilt when ZNetScene is replaced
        // (world reload). A prefab that ZNetScene does not know is cached as "nothing" so the lookup
        // never repeats.
        private sealed class PrefabInfo
        {
            public string Name = "";
            public bool IsCharacter;        // asset carries a Character (and is not the Player)
            public string NameToken = "";   // Character.m_name ("$enemy_boar") — the panel localizes it
            public float BaseHealth;        // Character.m_health, the max-health default for level 1
            public bool HasContainer;       // asset (or a child, e.g. carts/ships) carries a Container
            public string ContainerName = "";
            public int ContainerW, ContainerH;
            public int SpawnerKind = -1;    // -1 none, 0 SpawnArea (nest), 1 CreatureSpawner
            public string Spawns = "";      // what it spawns, for the row
            public float RespawnMinutes;    // CreatureSpawner.m_respawnTimeMinuts
        }

        private static readonly Dictionary<int, PrefabInfo> InfoByHash = new Dictionary<int, PrefabInfo>();
        private static object _infoScene;

        private static PrefabInfo InfoOf(int hash)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return null;
            if (!ReferenceEquals(_infoScene, scene)) { InfoByHash.Clear(); _infoScene = scene; }
            PrefabInfo info;
            if (InfoByHash.TryGetValue(hash, out info)) return info;
            info = BuildInfo(scene, hash);
            InfoByHash[hash] = info;
            return info;
        }

        private static PrefabInfo BuildInfo(ZNetScene scene, int hash)
        {
            var info = new PrefabInfo { Name = "#" + hash };
            GameObject go = null;
            try { go = scene.GetPrefab(hash); }
            catch (Exception) { }
            if (go == null) return info;
            info.Name = go.name ?? info.Name;
            try
            {
                var ch = go.GetComponent<Character>();
                if (ch != null && go.GetComponent<Player>() == null)
                {
                    info.IsCharacter = true;
                    info.NameToken = ch.m_name ?? "";
                    info.BaseHealth = ch.m_health > 0f ? ch.m_health : 10f;
                }
            }
            catch (Exception) { }
            try
            {
                // Carts and ships keep their Container on a child with m_rootObjectOverride pointing at
                // the root view, so the root prefab's ZDO is the one carrying "items".
                var c = go.GetComponentInChildren<Container>(true);
                if (c != null)
                {
                    info.HasContainer = true;
                    info.ContainerName = c.m_name ?? "";
                    info.ContainerW = Mathf.Clamp(c.m_width, 1, 64);
                    info.ContainerH = Mathf.Clamp(c.m_height, 1, 64);
                }
            }
            catch (Exception) { }
            try
            {
                var cs = go.GetComponent<CreatureSpawner>();
                if (cs != null)
                {
                    info.SpawnerKind = 1;
                    info.Spawns = cs.m_creaturePrefab != null ? cs.m_creaturePrefab.name : "";
                    info.RespawnMinutes = cs.m_respawnTimeMinuts;
                }
                else
                {
                    var sa = go.GetComponent<SpawnArea>();
                    if (sa != null)
                    {
                        info.SpawnerKind = 0;
                        var names = new List<string>();
                        if (sa.m_prefabs != null)
                            foreach (var sd in sa.m_prefabs)
                            {
                                if (sd == null || sd.m_prefab == null) continue;
                                if (!names.Contains(sd.m_prefab.name)) names.Add(sd.m_prefab.name);
                                if (names.Count >= 3) break;
                            }
                        info.Spawns = string.Join(", ", names.ToArray());
                    }
                }
            }
            catch (Exception) { }
            return info;
        }

        // ==================== frame-spread world scanner (shared by tames + container search) ====================

        private const int KindTames = 0;
        private const int KindSearch = 1;

        private const int PhaseSectors = 0;
        private const int PhaseOutside = 1;
        private const int PhaseFlat = 2;
        private const int PhaseDone = 3;

        private sealed class ScanJob
        {
            public int Kind;
            public long Requester;
            public Vector3 Origin;     // the admin's position at request time; rows are sorted by distance to it
            public int TopN;
            public string Query = "";  // container search only (already trimmed, lower-cased copy in QueryLower)
            public string QueryLower = "";

            public int Phase;
            public int SectorIndex, SectorLen;
            public int OutsideIndex;
            public List<List<ZDO>> Outside;
            public ZDO[] Flat;
            public int FlatIndex;

            public int Scanned;
            public int Total;
            public int Matched;          // tames found / containers matched
            public int Containers;       // containers decoded (search)
            public List<TameRow> Tames;
            public List<SearchRow> Hits;
            public readonly Stopwatch Watch = new Stopwatch();
        }

        private static ScanJob _job;
        private static ScanJob _lastTame;    // finished jobs, answered to progress polls that arrive after completion
        private static ScanJob _lastSearch;

        private static FieldInfo _fSectors, _fOutside, _fById;
        private static bool _fieldsProbed;

        private static ScanJob NewJob(int kind, long requester)
        {
            if (ZDOMan.instance == null) return null;
            EnsureFields();
            var job = new ScanJob { Kind = kind, Requester = requester, Total = TotalZdos() };
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
                    CompanionPlugin.FeatureLog("World-object scan unavailable: neither ZDOMan.m_objectsBySector nor m_objectsByID could be read on this game build.");
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
            CompanionPlugin.FeatureLog($"World-object scan aborted ({reason}) after {job.Scanned} ZDOs.");
            CompanionPlugin.NotifySender(job.Requester, $"Scan aborted: {reason}.");
            // Answer the pending request anyway so the panel never waits forever.
            if (job.Kind == KindTames) SendTameRoster(null, job.Requester, false);
            else SendSearch(null, job.Requester, false);
        }

        private static void StepScan()
        {
            var job = _job;
            if (job == null) return;
            if (ZDOMan.instance == null) { AbortJob("world unloaded"); return; }

            var sw = Stopwatch.StartNew();
            var budget = BudgetMs;
            while (job.Phase != PhaseDone && sw.ElapsedMilliseconds < budget)
            {
                switch (job.Phase)
                {
                    case PhaseSectors: RunSectors(job, sw, budget); break;
                    case PhaseOutside: RunOutside(job, sw, budget); break;
                    case PhaseFlat: RunFlat(job, sw, budget); break;
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
                // Snapshot the (small) outside-sector lists once: a live Dictionary enumeration across
                // frames would throw the moment anything moved.
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
            job.Phase = PhaseDone;
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
            job.Phase = PhaseDone;
        }

        private static void ProcessZdo(ScanJob job, ZDO zdo)
        {
            if (zdo == null) return;
            try
            {
                if (!zdo.IsValid()) return;
                job.Scanned++;
                var hash = zdo.GetPrefab();
                if (job.Kind == KindTames) TameProcess(job, zdo, hash);
                else SearchProcess(job, zdo, hash);
            }
            catch (Exception)
            {
                // A single malformed ZDO must never take down a world-wide sweep.
            }
        }

        private static void FinishJob(ScanJob job)
        {
            _job = null;
            job.Watch.Stop();
            if (job.Kind == KindTames) TameFinish(job);
            else SearchFinish(job);
        }

        // ---- ZDOMan container access (same reflection as Wave2SrvWorld; own handles) ----

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

        private static int TotalZdos()
        {
            try { return ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0; }
            catch (Exception) { return 0; }
        }

        // ==================== zone-block query (synchronous, bounded) ====================

        private static readonly List<ZDO> Scratch = new List<ZDO>();

        // Every ZDO in the (2*zones+1)^2 sector block around `center`. Returns false when the object store
        // is unavailable. Synchronous on purpose: at most 81 sectors, exactly the "small zone block" the
        // area tools already walk.
        private static bool CollectBlock(Vector3 center, int zones, List<ZDO> result)
        {
            result.Clear();
            var man = ZDOMan.instance;
            if (man == null) return false;
            zones = Mathf.Clamp(zones, 1, MaxZoneBlock);
            try { ZoneCompat.FindSectorObjects(man, ZoneSystem.GetZone(center), zones, 0, result); }
            catch (Exception e)
            {
                result.Clear();
                CompanionPlugin.FeatureLog($"FindSectorObjects failed: {e.Message}");
                return false;
            }
            return true;
        }

        // ==================== frame-spread deletion queue ====================

        private static readonly Queue<ZDOID> Doom = new Queue<ZDOID>();

        private static bool QueueDelete(ZDOID id)
        {
            if (Doom.Count >= DoomQueueCap) return false;
            Doom.Enqueue(id);
            return true;
        }

        private static void StepDeletes()
        {
            var man = ZDOMan.instance;
            if (man == null) { Doom.Clear(); return; }
            var n = 0;
            while (Doom.Count > 0 && n < DeletesPerFrame)
            {
                var id = Doom.Dequeue();
                n++;
                try
                {
                    // Re-resolve by ZDOID, never by a cached reference: ZDOPool recycles ZDO objects.
                    var zdo = man.GetZDO(id);
                    if (zdo == null || !zdo.IsValid()) continue;
                    DestroyNow(zdo);
                }
                catch (Exception) { }
            }
        }

        // Claim-then-destroy (CompanionPlugin.OnServerUndo shape). On a listen-server host with a live
        // instance the view path also removes the scene object; on a dedicated server there is never an
        // instance and the raw ZDO path is the normal one.
        private static void DestroyNow(ZDO zdo)
        {
            try
            {
                var view = LocalView(zdo);
                if (view != null) { view.ClaimOwnership(); view.Destroy(); return; }
            }
            catch (Exception) { }
            zdo.SetOwner(SessionId());
            ZDOMan.instance.DestroyZDO(zdo);
        }

        // ==================== ownership helpers (see header: the server never keeps what it claims) ====================

        private static long SessionId()
        {
            try { return ZDOMan.GetSessionID(); }
            catch (Exception) { return 0L; }
        }

        private static ZNetView LocalView(ZDO zdo)
        {
            try
            {
                var scene = ZNetScene.instance;
                return scene != null ? scene.FindInstance(zdo) : null;
            }
            catch (Exception) { return null; }
        }

        private static ZNetPeer FindPeer(long uid)
        {
            if (uid == 0L || ZNet.instance == null) return null;
            try
            {
                var peers = ZNet.instance.GetPeers();
                if (peers == null) return null;
                foreach (var p in peers) if (p != null && p.m_uid == uid) return p;
            }
            catch (Exception) { }
            return null;
        }

        // The owner peer of a ZDO when that peer is connected and is not this server; 0 otherwise. The
        // callers use it to route the game's own ZDO RPCs to the simulating client.
        private static long ConnectedOwner(ZDO zdo)
        {
            long owner;
            try { owner = zdo.HasOwner() ? zdo.GetOwner() : 0L; }
            catch (Exception) { return 0L; }
            if (owner == 0L || owner == SessionId()) return 0L;
            return FindPeer(owner) != null ? owner : 0L;
        }

        // Claim the ZDO for a raw write; returns the previous owner for HandBack.
        private static long ClaimForWrite(ZDO zdo)
        {
            var prev = zdo.HasOwner() ? zdo.GetOwner() : 0L;
            var session = SessionId();
            if (prev != session) zdo.SetOwner(session);
            return prev;
        }

        // Restore the previous owner when that peer is still connected, else release (SetOwner(0)) so the
        // 2 s ReleaseZDOS pass hands the object to whoever is nearest; then force the new state out to
        // every peer. prev == session means the host's own client (listen server) or vanilla server
        // ownership already held it, which is left alone.
        private static void HandBack(ZDO zdo, long prev)
        {
            var session = SessionId();
            if (prev != session)
                zdo.SetOwner(prev != 0L && FindPeer(prev) != null ? prev : 0L);
            try { ZDOMan.instance?.ForceSendZDO(zdo.m_uid); }
            catch (Exception) { }
        }

        // ==================== small helpers ====================

        // Player-controlled text that ends up in an audit row / a ZDO: no pipes (audit column delimiter),
        // no newlines (a whole forged row), bounded length.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > max) s = s.Substring(0, max);
            return s;
        }

        private static bool PosOk(Vector3 p) =>
            !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
              float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));

        private static float DistXZ(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Name of the connected player nearest to `pos` ("" when nobody is online). Peer reference positions
        // are what the server streams ZDOs against, so they are always current; the host's own player is
        // added on a listen server.
        private static string NearestPlayerName(Vector3 pos)
        {
            string best = "";
            var bestD = float.MaxValue;
            try
            {
                var peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
                if (peers != null)
                    foreach (var p in peers)
                    {
                        if (p == null || string.IsNullOrEmpty(p.m_playerName)) continue;
                        var d = Vector3.Distance(p.m_refPos, pos);
                        if (d < bestD) { bestD = d; best = p.m_playerName; }
                    }
                var me = Player.m_localPlayer;
                if (me != null)
                {
                    var d = Vector3.Distance(me.transform.position, pos);
                    if (d < bestD) { bestD = d; best = me.GetPlayerName(); }
                }
            }
            catch (Exception) { }
            return best ?? "";
        }

        // The tame namer, as the game stores it: a platform user id ("Steam_765..."), "host", or nothing.
        // Resolved to a player name when that player is online, otherwise shown as the bare id; "?" when
        // the game stored nothing (a pet named before the field existed, or never named).
        private static string AuthorLabel(string author)
        {
            if (string.IsNullOrEmpty(author)) return "?";
            try
            {
                if (author == "host")
                {
                    var me = Player.m_localPlayer;
                    return me != null ? me.GetPlayerName() : "host";
                }
                var peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
                if (peers != null)
                    foreach (var p in peers)
                    {
                        if (p == null || p.m_socket == null) continue;
                        var host = p.m_socket.GetHostName();
                        if (string.IsNullOrEmpty(host)) continue;
                        if (host == author || Wave1AuditRpc.SameId(host, author))
                            return string.IsNullOrEmpty(p.m_playerName) ? author : p.m_playerName;
                    }
                var bare = CompanionPlugin.FeatureBareId(author);
                return string.IsNullOrEmpty(bare) ? author : bare;
            }
            catch (Exception) { return author; }
        }

        private static void Reply(long uid, string rpc, ZPackage pkg)
        {
            try { CompanionPlugin.ReplyTo(uid, rpc, pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"{rpc} reply failed: {e.Message}"); }
        }
    }
}

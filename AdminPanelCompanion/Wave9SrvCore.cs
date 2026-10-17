using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 (wave 9) — server facade + shared plumbing ====================
    // Fifteen features (FEATURE_BACKLOG_ROUND3.md) share one lifecycle so the companion core needs exactly two
    // hooks (FeaturesWave9Glue.cs). Each feature is a static class in its own Wave9Srv*.cs file exposing
    // Init / Register(ZRoutedRpc) / Tick; this file owns what several of them need:
    //
    //   * ONE ZNet.Awake registration patch that calls every feature's Register in its own try, so a failure
    //     costs that feature, never the group.
    //   * A frame-spread WORLD SCANNER (Wave9Scan) over every ZDO — the same m_objectsBySector + portal pass
    //     (flat m_objectsByID fallback) the Wave 8 tame roster uses, but visitor-driven, so the ownership
    //     census, the map overlays, the provenance scan, the base finder and the location sweep all reuse it.
    //     One job runs at a time; the rest queue (a request that finds the queue full is refused, not stacked).
    //   * The PLAYER-ID LEDGER (Wave9Pids): Piece.m_creator / ZDO "creator" holds the builder's PLAYER id (the
    //     character's profile id), not the platform id the presence table and the adminlist use. The server
    //     learns the pair only while that player is online (ZNetPeer.m_playerID, set by ZNet.RPC_CharacterID),
    //     so it is recorded every 30 s for every connected peer and persisted ("w9_pids": playerId ->
    //     platformId|name|lastSeenTicks). Bases built before this ran show their creator as "unknown".
    //   * Small shared helpers (sanitisers, prefab kinds, bounded radius queries, vanilla-safe messages).
    //
    // Same DLL on the server and on every modded client: server handlers refuse to run off-server, client
    // executors refuse anything not sent by the server (Wave8Toolkit.SenderIsTrustedServer).
    internal static class Wave9
    {
        internal const int Ver = 1;
        private static bool _inited;

        private static ConfigEntry<int> _scanBudgetMs;
        internal static int ScanBudgetMs => _scanBudgetMs != null ? Mathf.Clamp(_scanBudgetMs.Value, 1, 16) : 6;

        private sealed class Feature
        {
            public string Name;
            public Action Init;
            public Action<ZRoutedRpc> Register;
            public Action Tick;
        }

        private static readonly List<Feature> Features = new List<Feature>();

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _scanBudgetMs = cfg.Bind("Features", "Round3ScanBudgetMs", 6,
                    "Milliseconds per frame the round-3 world scanner (ownership census, map overlays, provenance, base finder, location sweep) may spend. Lower = slower scans, smaller frame cost. Clamped to 1-16.");

            Add("zown", Wave9Zown.Init, Wave9Zown.Register, null);
            Add("overlays", Wave9Ovl.Init, Wave9Ovl.Register, null);
            Add("provenance", Wave9Prov.Init, Wave9Prov.Register, Wave9Prov.Tick);
            Add("progression", Wave9Prog.Init, Wave9Prog.Register, Wave9Prog.Tick);
            Add("worlddiff", Wave9Wdiff.Init, Wave9Wdiff.Register, Wave9Wdiff.Tick);
            Add("bases", Wave9Bases.Init, Wave9Bases.Register, null);
            Add("locations", Wave9Locr.Init, Wave9Locr.Register, null);
            Add("rulezones", Wave9Rzone.Init, Wave9Rzone.Register, Wave9Rzone.Tick);
            Add("triggers", Wave9Trig.Init, Wave9Trig.Register, Wave9Trig.Tick);
            Add("undo", Wave9Undo.Init, Wave9Undo.Register, null);
            Add("seforge", Wave9Sef.Init, Wave9Sef.Register, null);
            Add("encounters", Wave9Enc.Init, Wave9Enc.Register, Wave9Enc.Tick);
            Add("ccmd", Wave9Ccmd.Init, Wave9Ccmd.Register, null);

            foreach (var f in Features)
            {
                if (f.Init == null) continue;
                try { f.Init(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Round 3 {f.Name} init failed (that feature is unavailable): {e.Message}"); }
            }

            ApplyPatch("Wave9RpcRegistration", typeof(Wave9RpcRegistration), "round-3 RPCs unavailable");
        }

        private static void Add(string name, Action init, Action<ZRoutedRpc> register, Action tick) =>
            Features.Add(new Feature { Name = name, Init = init, Register = register, Tick = tick });

        // Called every frame from Update. Each tick guards on its own world state and self-throttles.
        internal static void Tick()
        {
            if (!_inited) return;
            try { Wave9Scan.Step(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Round 3 scanner step failed: {e.Message}"); Wave9Scan.Abort("internal error"); }
            try { Wave9Pids.Tick(); } catch (Exception) { }
            try { Wave9Reassert.Tick(); } catch (Exception) { }
            foreach (var f in Features)
            {
                if (f.Tick == null) continue;
                try { f.Tick(); }
                catch (Exception e) { ThrottledLog(f.Name, $"Round 3 {f.Name} tick failed: {e.Message}"); }
            }
        }

        private static readonly Dictionary<string, float> LogThrottle = new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void ThrottledLog(string key, string msg)
        {
            float next;
            var now = Time.unscaledTime;
            if (LogThrottle.TryGetValue(key, out next) && now < next) return;
            LogThrottle[key] = now + 60f;
            CompanionPlugin.FeatureLog(msg);
        }

        internal static void ApplyPatch(string name, Type patchClass, string degradation)
        {
            var ok = true;
            try { Harmony.CreateAndPatchAll(patchClass); }
            catch (Exception e)
            {
                ok = false;
                CompanionPlugin.FeatureLog($"{name} failed ({degradation}): {e.Message}");
            }
            try { Wave2Ops.ReportPatch(name, ok); } catch (Exception) { }
        }

        // Every name registers on BOTH sides (the companion is the same file on the server and on clients);
        // each handler guards its own side.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave9RpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                var r = ZRoutedRpc.instance;
                if (r == null) return;
                foreach (var f in Features)
                {
                    if (f.Register == null) continue;
                    try { f.Register(r); }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Round 3 {f.Name} RPC registration failed: {e.Message}"); }
                }
                Wave9Scan.ResetForWorld();
                Wave9Reassert.Clear();
            }
        }

        // ==================== gates ====================

        internal static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>Server-side admin/role gate for a round-3 action (adminlist, then tiered roles when enabled).</summary>
        internal static bool Can(long sender, string action) => IsServer && CompanionPlugin.SenderCanFeature(sender, action);

        internal static bool FromServer(long sender) => Wave8Toolkit.SenderIsTrustedServer(sender);

        internal static void Reply(long sender, string name, ZPackage pkg) => CompanionPlugin.ReplyTo(sender, name, pkg);

        internal static void Tell(long sender, string text)
        {
            if (sender == 0L || string.IsNullOrEmpty(text)) return;
            CompanionPlugin.NotifySender(sender, text);
        }

        internal static Vector3 SenderPosition(long sender) => Wave8Toolkit.SenderPosition(sender);

        internal static string Clean(string s, int max) => Wave8Toolkit.CleanText(s, max);

        internal static string CleanName(string s, int max) => Wave8Toolkit.CleanName(s, max);

        internal static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);

        internal static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        internal static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

        internal static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) ||
              float.IsNaN(v.z) || float.IsInfinity(v.z));

        internal static float MapDist(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ==================== peers ====================

        internal static List<ZNetPeer> ReadyPeers()
        {
            var res = new List<ZNetPeer>();
            if (ZNet.instance == null) return res;
            foreach (var p in ZNet.instance.GetPeers())
                if (p != null && p.IsReady() && p.m_uid != 0L) res.Add(p);
            return res;
        }

        internal static string PeerId(ZNetPeer p) =>
            p != null && p.m_socket != null ? (p.m_socket.GetHostName() ?? "?") : "?";

        internal static int PeerPing(ZNetPeer p)
        {
            var ping = -1;
            try { if (p != null && p.m_socket != null) p.m_socket.GetConnectionQuality(out _, out _, out ping, out _, out _); }
            catch (Exception) { ping = -1; }
            return ping;
        }

        /// <summary>The peer's character position: the character ZDO when known, else its reported reference position.</summary>
        internal static Vector3 PeerPos(ZNetPeer p)
        {
            if (p == null) return Vector3.zero;
            try
            {
                if (!p.m_characterID.IsNone() && ZDOMan.instance != null)
                {
                    var zdo = ZDOMan.instance.GetZDO(p.m_characterID);
                    if (zdo != null) return zdo.GetPosition();
                }
            }
            catch (Exception) { }
            return p.m_refPos;
        }

        internal static long PeerPlayerId(ZNetPeer p)
        {
            if (p == null) return 0L;
            if (p.m_playerID != 0L) return p.m_playerID;
            try
            {
                if (!p.m_characterID.IsNone() && ZDOMan.instance != null)
                {
                    var zdo = ZDOMan.instance.GetZDO(p.m_characterID);
                    if (zdo != null) return zdo.GetLong(KeyPlayerId, 0L);
                }
            }
            catch (Exception) { }
            return 0L;
        }

        internal static ZNetPeer PeerByUid(long uid) => uid == 0L || ZNet.instance == null ? null : ZNet.instance.GetPeer(uid);

        /// <summary>A listen-server host is not in m_peers; its session id stands in for it.</summary>
        internal static bool IsHostUid(long uid) =>
            uid != 0L && ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.instance.IsDedicated() && uid == ZDOMan.GetSessionID();

        // ==================== ZDO keys (literal strings; the string is what the save file holds) ====================

        internal static readonly int KeyCreator = "creator".GetStableHashCode();
        internal static readonly int KeyCreatorName = "creatorName".GetStableHashCode();
        internal static readonly int KeyPlayerId = "playerID".GetStableHashCode();
        internal static readonly int KeyPlayerName = "playerName".GetStableHashCode();
        internal static readonly int KeyItems = "items".GetStableHashCode();
        internal static readonly int KeyTamed = "tamed".GetStableHashCode();
        internal static readonly int KeyHealth = "health".GetStableHashCode();
        internal static readonly int KeyMaxHealth = "max_health".GetStableHashCode();
        internal static readonly int KeyLevel = "level".GetStableHashCode();
        internal static readonly int KeyEnabled = "enabled".GetStableHashCode();
        internal static readonly int KeyTag = "tag".GetStableHashCode();
        internal static readonly int KeyItemData = "itemData".GetStableHashCode();

        // ==================== prefab names / kinds ====================

        private static Dictionary<int, string> _names;
        private static object _namesScene;

        internal static string PrefabName(int hash)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return "#" + hash;
            if (!ReferenceEquals(scene, _namesScene) || _names == null)
            {
                _namesScene = scene;
                _names = new Dictionary<int, string>();
            }
            string n;
            if (_names.TryGetValue(hash, out n)) return n;
            try
            {
                var go = scene.GetPrefab(hash);
                n = go != null ? go.name : "#" + hash;
            }
            catch (Exception) { n = "#" + hash; }
            _names[hash] = n;
            return n;
        }

        internal static GameObject PrefabGo(int hash)
        {
            try { return ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null; }
            catch (Exception) { return null; }
        }

        // Per-prefab component probes, cached (a world scan asks the same question for thousands of ZDOs).
        private static readonly Dictionary<int, int> KindCache = new Dictionary<int, int>();
        private static object _kindScene;

        internal const int KPiece = 1, KWard = 2, KPortal = 4, KShip = 8, KCart = 16, KCharacter = 32, KContainer = 64,
                           KItemDrop = 128, KTombstone = 256, KPlayer = 512, KBed = 1024;

        internal static int KindOf(int hash)
        {
            var scene = ZNetScene.instance;
            if (!ReferenceEquals(scene, _kindScene)) { KindCache.Clear(); _kindScene = scene; }
            int k;
            if (KindCache.TryGetValue(hash, out k)) return k;
            k = 0;
            try
            {
                var go = PrefabGo(hash);
                if (go != null)
                {
                    if (go.GetComponent<Piece>() != null) k |= KPiece;
                    if (go.GetComponent<PrivateArea>() != null) k |= KWard;
                    if (go.GetComponent<TeleportWorld>() != null) k |= KPortal;
                    if (go.GetComponent<Ship>() != null) k |= KShip;
                    if (go.GetComponent<Vagon>() != null) k |= KCart;
                    if (go.GetComponent<Character>() != null) k |= KCharacter;
                    if (go.GetComponent<Player>() != null) k |= KPlayer;
                    if (go.GetComponent<Container>() != null) k |= KContainer;
                    if (go.GetComponent<ItemDrop>() != null) k |= KItemDrop;
                    if (go.GetComponent<TombStone>() != null) k |= KTombstone;
                    if (go.GetComponent<Bed>() != null) k |= KBed;
                }
            }
            catch (Exception) { }
            KindCache[hash] = k;
            return k;
        }

        // ==================== text packing ('|' fields, sanitised) ====================

        internal static string Pack(params string[] parts)
        {
            for (var i = 0; i < parts.Length; i++) parts[i] = (parts[i] ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
            return string.Join("|", parts);
        }

        internal static string[] Unpack(string s, int min)
        {
            var parts = (s ?? "").Split('|');
            if (parts.Length >= min) return parts;
            var res = new string[min];
            for (var i = 0; i < min; i++) res[i] = i < parts.Length ? parts[i] : "";
            return res;
        }

        internal static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        internal static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        internal static float ParseFloat(string s, float fallback)
        {
            float v;
            return float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v) ? v : fallback;
        }

        internal static string Vec(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##};{1:0.##};{2:0.##}", v.x, v.y, v.z);

        internal static Vector3 ParseVec(string s)
        {
            var p = (s ?? "").Split(';');
            if (p.Length != 3) return Vector3.zero;
            return new Vector3(ParseFloat(p[0], 0f), ParseFloat(p[1], 0f), ParseFloat(p[2], 0f));
        }

        // ==================== zone-block query (bounded, synchronous) ====================

        private static readonly List<ZDO> BlockScratch = new List<ZDO>();

        /// <summary>
        /// Every ZDO whose map distance to <paramref name="center"/> is within <paramref name="radius"/> (any height),
        /// using the classic square zone block. Radius is clamped to 192 m (area 3 = 49 zones).
        /// </summary>
        internal static List<ZDO> ZdosInRadius(Vector3 center, float radius, List<ZDO> into)
        {
            into.Clear();
            var man = ZDOMan.instance;
            if (man == null) return into;
            radius = Mathf.Clamp(radius, 1f, 192f);
            var area = Mathf.Clamp(Mathf.CeilToInt(radius / 64f), 1, 3);
            BlockScratch.Clear();
            try { ZoneCompat.FindSectorObjects(man, ZoneSystem.GetZone(center), area, BlockScratch); }
            catch (Exception) { return into; }
            var seen = new HashSet<ZDOID>();
            for (var i = 0; i < BlockScratch.Count; i++)
            {
                var z = BlockScratch[i];
                if (z == null || !z.IsValid() || !seen.Add(z.m_uid)) continue;
                if (MapDist(z.GetPosition(), center) <= radius) into.Add(z);
            }
            BlockScratch.Clear();
            return into;
        }

        /// <summary>Destroy a ZDO from the server: claim it first, or DestroyZDO is a silent no-op (CompanionPlugin.OnServerUndo).</summary>
        internal static bool DestroyZdo(ZDO zdo)
        {
            if (zdo == null || ZDOMan.instance == null) return false;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                var nview = go != null ? go.GetComponent<ZNetView>() : null;
                if (nview != null && nview.GetZDO() == zdo) { nview.ClaimOwnership(); nview.Destroy(); return true; }
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ==================== vanilla-safe messages / teleport ====================

        internal static void Announce(string text) => Wave8Toolkit.AnnounceAll(text);

        /// <summary>Center-screen text to one player through the vanilla ShowMessage RPC (reaches unmodded clients).</summary>
        internal static void Center(long uid, string text)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            if (IsHostUid(uid))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
                return;
            }
            Wave1Moderation.SendPlayerText(uid, text);
        }

        internal static void TopLeft(long uid, string text) => Wave8Toolkit.SendTopLeft(uid, text);

        /// <summary>Vanilla teleport of any player (works for unmodded clients): Player's RPC_TeleportPlayer on their character.</summary>
        internal static bool TeleportPeer(long uid, Vector3 pos)
        {
            try
            {
                if (IsHostUid(uid) && Player.m_localPlayer != null)
                {
                    Player.m_localPlayer.TeleportTo(pos, Player.m_localPlayer.transform.rotation, true);
                    return true;
                }
                var peer = PeerByUid(uid);
                if (peer == null || peer.m_characterID.IsNone()) return false;
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, peer.m_characterID, "RPC_TeleportPlayer", pos, Quaternion.identity, true);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    // ==================== the shared frame-spread world scanner ====================
    internal static class Wave9Scan
    {
        private const int PhaseSectors = 0, PhasePortals = 1, PhaseFlat = 2, PhaseDone = 3;
        private const int WorkUnitsPerClockRead = 256;
        private const int QueueCap = 4;

        internal sealed class Job
        {
            public string Label;
            public long Requester;
            public Action<ZDO> Visit;
            public Action<Job> Done;       // called once, with Aborted set when it did not finish
            public bool Aborted;
            public string AbortReason = "";

            public int Phase;
            public int SectorIndex, SectorLen;
            public int ListIndex;          // position inside the current sector's list: one huge sector spans frames
            public List<ZDO> Portals;
            public int PortalIndex;
            public ZDO[] Flat;
            public int FlatIndex;

            public int Scanned;
            public int Total;
            public readonly Stopwatch Watch = new Stopwatch();
            public long Millis => Watch.ElapsedMilliseconds;
        }

        private static Job _job;
        private static readonly List<Job> Queue = new List<Job>();

        internal static bool Busy(string label)
        {
            if (_job != null && _job.Label == label) return true;
            foreach (var j in Queue) if (j.Label == label) return true;
            return false;
        }

        /// <summary>Progress of the running or queued job with this label.</summary>
        internal static void Progress(string label, out bool running, out int scanned, out int total)
        {
            running = false; scanned = 0; total = 0;
            if (_job != null && _job.Label == label) { running = true; scanned = _job.Scanned; total = _job.Total; return; }
            foreach (var j in Queue) if (j.Label == label) { running = true; total = j.Total; return; }
        }

        /// <summary>Queue a whole-world walk. Returns false (and tells the requester why) when it cannot start.</summary>
        internal static bool Start(string label, long requester, Action<ZDO> visit, Action<Job> done)
        {
            if (ZDOMan.instance == null || visit == null) return false;
            if (Busy(label)) { Wave9.Tell(requester, "That scan is already running - its result will arrive when it finishes."); return false; }
            if (Queue.Count >= QueueCap) { Wave9.Tell(requester, "The server is busy with other world scans - try again in a moment."); return false; }
            var job = new Job { Label = label, Requester = requester, Visit = visit, Done = done };
            try { job.Total = ZDOMan.instance.NrOfObjects(); } catch (Exception) { job.Total = 0; }
            if (_job == null) return Begin(job);
            Queue.Add(job);
            return true;
        }

        private static bool Begin(Job job)
        {
            var sectors = ZoneCompat.SectorArray(ZDOMan.instance);
            if (sectors != null) { job.Phase = PhaseSectors; job.SectorLen = sectors.Length; }
            else
            {
                var flat = ZoneCompat.FlatSnapshot(ZDOMan.instance);
                if (flat == null)
                {
                    CompanionPlugin.FeatureLog("Round 3 world scan unavailable: neither ZDOMan.m_objectsBySector nor m_objectsByID could be read on this game build.");
                    Wave9.Tell(job.Requester, "World scanning is unavailable on this server build.");
                    return false;
                }
                job.Phase = PhaseFlat;
                job.Flat = flat;
            }
            job.Watch.Start();
            _job = job;
            return true;
        }

        internal static void ResetForWorld()
        {
            // Empty the queue FIRST: Abort() promotes the next queued job, which would then walk the new world for a
            // feature whose state was just reset.
            var queued = Queue.ToArray();
            Queue.Clear();
            if (_job != null) Abort("world reloaded");
            foreach (var j in queued) { j.Aborted = true; j.AbortReason = "world reloaded"; SafeDone(j); }
        }

        internal static void Abort(string reason)
        {
            var job = _job;
            _job = null;
            if (job == null) return;
            job.Aborted = true;
            job.AbortReason = reason;
            job.Watch.Stop();
            CompanionPlugin.FeatureLog($"Round 3 scan '{job.Label}' aborted ({reason}) after {job.Scanned} ZDOs.");
            SafeDone(job);
            Next();
        }

        private static void SafeDone(Job job)
        {
            try { job.Done?.Invoke(job); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Round 3 scan '{job.Label}' completion failed: {e.Message}"); }
        }

        private static void Next()
        {
            while (_job == null && Queue.Count > 0)
            {
                var j = Queue[0];
                Queue.RemoveAt(0);
                if (!Begin(j)) { j.Aborted = true; j.AbortReason = "scanner unavailable"; SafeDone(j); }
            }
        }

        internal static void Step()
        {
            var job = _job;
            if (job == null) return;
            if (ZDOMan.instance == null) { Abort("world unloaded"); return; }
            var sw = Stopwatch.StartNew();
            var budget = Wave9.ScanBudgetMs;
            while (job.Phase != PhaseDone && sw.ElapsedMilliseconds < budget)
            {
                switch (job.Phase)
                {
                    case PhaseSectors: RunSectors(job, sw, budget); break;
                    case PhasePortals: RunPortals(job, sw, budget); break;
                    case PhaseFlat: RunFlat(job, sw, budget); break;
                    default: job.Phase = PhaseDone; break;
                }
            }
            if (job.Phase != PhaseDone) return;
            _job = null;
            job.Watch.Stop();
            SafeDone(job);
            Next();
        }

        private static void RunSectors(Job job, Stopwatch sw, int budget)
        {
            var arr = ZoneCompat.SectorArray(ZDOMan.instance);
            if (arr == null || arr.Length != job.SectorLen) { job.Phase = PhaseDone; job.Aborted = true; job.AbortReason = "world reloaded"; return; }
            var since = 0;
            while (job.SectorIndex < arr.Length)
            {
                var list = arr[job.SectorIndex];
                if (list != null)
                {
                    // The clock is read inside the list too: a base sector can hold tens of thousands of ZDOs. A list
                    // that changes between frames costs at most a few skipped or repeated objects in an estimate.
                    while (job.ListIndex < list.Count)
                    {
                        Visit(job, list[job.ListIndex++]);
                        if (++since < WorkUnitsPerClockRead) continue;
                        since = 0;
                        if (sw.ElapsedMilliseconds >= budget) return;
                    }
                }
                job.SectorIndex++;
                job.ListIndex = 0;
                if (++since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Phase = PhasePortals;
        }

        private static void RunPortals(Job job, Stopwatch sw, int budget)
        {
            if (job.Portals == null) { job.Portals = ZoneCompat.PortalsSnapshot(ZDOMan.instance) ?? new List<ZDO>(); job.PortalIndex = 0; }
            var arr = ZoneCompat.SectorArray(ZDOMan.instance);
            var since = 0;
            while (job.PortalIndex < job.Portals.Count)
            {
                var zdo = job.Portals[job.PortalIndex++];
                if (!ZoneCompat.InSectorBucket(arr, zdo)) Visit(job, zdo);
                if (++since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Portals = null;
            job.Phase = PhaseDone;
        }

        private static void RunFlat(Job job, Stopwatch sw, int budget)
        {
            if (job.Flat == null) { job.Phase = PhaseDone; return; }
            var since = 0;
            while (job.FlatIndex < job.Flat.Length)
            {
                Visit(job, job.Flat[job.FlatIndex++]);
                if (++since < WorkUnitsPerClockRead) continue;
                since = 0;
                if (sw.ElapsedMilliseconds >= budget) return;
            }
            job.Flat = null;
            job.Phase = PhaseDone;
        }

        private static void Visit(Job job, ZDO zdo)
        {
            if (zdo == null) return;
            try
            {
                if (!zdo.IsValid()) return;
                job.Scanned++;
                job.Visit(zdo);
            }
            catch (Exception) { /* one malformed ZDO must never take down a world-wide sweep */ }
        }
    }

    // ==================== player-id ledger ====================
    // ==================== re-asserting writes to ZDOs a client owns ====================
    // The server may write fields on a creature some client simulates, but ZDOMan.RPC_ZDOData drops an incoming copy
    // whose data revision is not newer than the local one: when the owner changes the same ZDO before the server's
    // copy arrives, both sides end at the same revision with different content and the server's field is silently
    // lost once the owner's next update lands. So such writes are re-applied (with a forced send) each second until
    // the owner's copy agrees, for a few seconds - one extra revision wins every race.
    internal static class Wave9Reassert
    {
        private sealed class Item
        {
            public ZDOID Id;
            public Func<ZDO, bool> Holds;
            public Action<ZDO> Apply;
            public float Until;
        }

        private const int Cap = 2000;
        private static readonly List<Item> Items = new List<Item>();
        private static float _next;

        internal static void Add(ZDOID id, Func<ZDO, bool> holds, Action<ZDO> apply, float seconds = 10f)
        {
            if (id.IsNone() || holds == null || apply == null) return;
            Items.RemoveAll(i => i.Id == id);   // the latest order for an object replaces the previous one
            if (Items.Count >= Cap) Items.RemoveAt(0);
            Items.Add(new Item { Id = id, Holds = holds, Apply = apply, Until = Time.unscaledTime + seconds });
        }

        internal static void Clear() => Items.Clear();

        internal static void Tick()
        {
            if (Items.Count == 0 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            var man = ZDOMan.instance;
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                var it = Items[i];
                var zdo = man != null ? man.GetZDO(it.Id) : null;
                if (zdo == null || Time.unscaledTime > it.Until) { Items.RemoveAt(i); continue; }
                try
                {
                    if (it.Holds(zdo)) continue;
                    it.Apply(zdo);
                    man.ForceSendZDO(it.Id);
                }
                catch (Exception) { Items.RemoveAt(i); }
            }
        }
    }

    internal static class Wave9Pids
    {
        private const string Tbl = "w9_pids";   // playerId -> platformId|name|lastSeenTicks (UTC ticks)
        private const string SinceKey = "_since";   // when this ledger started: nobody absent from it was online after that
        private const float Period = 30f;
        private const float SaveEvery = 300f;   // last-seen moves every tick; the file only needs 5-minute precision
        private static float _next, _nextSave;

        /// <summary>
        /// When the ledger began (UTC ticks). A creator the ledger has never recorded has not been online since then,
        /// which makes this a safe "last seen no later than" for unknown builders.
        /// </summary>
        internal static long Since()
        {
            string row;
            if (FeatureStore.Ready && FeatureStore.Table(Tbl).TryGetValue(SinceKey, out row))
            {
                var t = Wave9.ParseLong(row, 0L);
                if (t > 0L) return t;
            }
            return DateTime.UtcNow.Ticks;   // not started yet: unknown builders count as seen just now
        }

        internal static void Tick()
        {
            if (!Wave9.IsServer || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Period;
            if (!FeatureStore.Ready) return;
            var table = FeatureStore.Table(Tbl);
            var now = Wave9.I(DateTime.UtcNow.Ticks);
            var dirty = false;    // something only last-seen: saved every SaveEvery
            var changed = false;  // a new player, platform id or name: saved now
            if (!table.ContainsKey(SinceKey)) { table[SinceKey] = now; changed = true; }
            foreach (var p in Wave9.ReadyPeers())
            {
                var pid = Wave9.PeerPlayerId(p);
                if (pid == 0L) continue;
                changed |= Put(table, pid, Wave9.PeerId(p), p.m_playerName ?? "?", now);
                dirty = true;
            }
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated())
            {
                var pid = Player.m_localPlayer.GetPlayerID();
                if (pid != 0L)
                {
                    changed |= Put(table, pid, "HOST", Player.m_localPlayer.GetPlayerName(), now);
                    dirty = true;
                }
            }
            if (changed || (dirty && Time.unscaledTime >= _nextSave))
            {
                FeatureStore.SaveTable(Tbl);
                _nextSave = Time.unscaledTime + SaveEvery;
            }
        }

        private static bool Put(Dictionary<string, string> table, long pid, string platformId, string name, string now)
        {
            var key = Wave9.I(pid);
            string old;
            var same = table.TryGetValue(key, out old) && old.StartsWith(Wave9.Pack(platformId, name, "") , StringComparison.Ordinal);
            table[key] = Wave9.Pack(platformId, name, now);
            return !same;
        }

        internal sealed class Who
        {
            public long PlayerId;
            public string PlatformId = "";
            public string Name = "";
            public long LastSeenTicks;   // 0 = never recorded
            public bool Online;
        }

        /// <summary>What the server knows about a creator id: online players answer live, others from the ledger + presence.</summary>
        internal static Who Lookup(long playerId, Dictionary<long, ZNetPeer> onlineByPid)
        {
            var w = new Who { PlayerId = playerId };
            ZNetPeer peer;
            if (onlineByPid != null && onlineByPid.TryGetValue(playerId, out peer))
            {
                w.Online = true;
                w.PlatformId = Wave9.PeerId(peer);
                w.Name = peer.m_playerName ?? "?";
                w.LastSeenTicks = DateTime.UtcNow.Ticks;
                return w;
            }
            string row;
            if (FeatureStore.Table(Tbl).TryGetValue(Wave9.I(playerId), out row))
            {
                var p = Wave9.Unpack(row, 3);
                w.PlatformId = p[0];
                w.Name = p[1];
                w.LastSeenTicks = Wave9.ParseLong(p[2], 0L);
                var pres = PresenceLastSeen(w.PlatformId);   // the real session end, when wave 1 recorded it
                if (pres > w.LastSeenTicks) w.LastSeenTicks = pres;
            }
            return w;
        }

        internal static Dictionary<long, ZNetPeer> OnlineByPid()
        {
            var d = new Dictionary<long, ZNetPeer>();
            foreach (var p in Wave9.ReadyPeers())
            {
                var pid = Wave9.PeerPlayerId(p);
                if (pid != 0L) d[pid] = p;
            }
            return d;
        }

        // Wave 1 "presence": platformId -> first|last|sessions|totalSeconds|lastName, first/last in UTC ticks.
        internal static long PresenceLastSeen(string platformId)
        {
            if (string.IsNullOrEmpty(platformId) || platformId == "?" || platformId == "HOST") return 0L;
            try
            {
                var pres = FeatureStore.Table("presence");
                string row;
                if (!pres.TryGetValue(platformId, out row) && !pres.TryGetValue(CompanionPlugin.FeatureBareId(platformId), out row)) return 0L;
                var parts = (row ?? "").Split('|');
                if (parts.Length < 2) return 0L;
                long last;
                return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out last) && last > 0 ? last : 0L;
            }
            catch (Exception) { return 0L; }
        }
    }
}

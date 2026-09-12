using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Waves 3+4 — shared server core ====================
    // The foundation five other wave-3/4 modules build on. Three independent capabilities live here because
    // they all need the same scarce things: a per-peer identity map, a cheap polling budget on the server's
    // Update, and exactly one HTTP sender.
    //
    //   1. CAPABILITY PROBE  — "does this player's client run AdminPanelCompanion.dll?"  Everything that
    //      touches CHARACTER DATA (inventory, skills, status effects, snapshots) is impossible without it,
    //      because in Valheim character data lives in the player's local .fch file and is only reachable by
    //      asking that player's own client. Admins have the DLL; ordinary players usually do not. Features
    //      must degrade on HasMod(uid) != true and TELL THE ADMIN WHY (CapReason).
    //   2. DEATH / BOSS / RAID DETECTION — all TIER-VANILLA: derived from ZDO state and engine singletons the
    //      server owns, so it works for completely unmodded clients. Nothing here writes to the world.
    //   3. DISCORD EVENT FEED — one bounded queue, one in-flight POST, exponential backoff. OFF by default
    //      (it ships player names off the machine, which is a behaviour change no server owner should get
    //      by surprise).
    //
    // House rules obeyed (see Wave1SrvModeration.cs / Wave2SrvWorld.cs for the same shapes):
    //  * ONE Harmony class per target method, each applied in its own try/catch, each reported to the
    //    self-test through Wave2Ops.ReportPatch.
    //  * Ticks on the wire and in the store are DateTime.UtcNow.Ticks; in-frame scheduling is
    //    Time.unscaledTime.
    //  * The server has no GameObjects for players: no Character.GetAllCharacters, no Physics. Death
    //    detection reads the ZDO layer only.
    //  * Nothing sweeps the whole ZDO set. The death poll walks ZNet.GetAllCharacterZDOS() (one entry per
    //    connected player) and the tombstone check walks a 3x3 zone block ONCE per death.
    //  * The HTTP POST is fire-and-forget on Task.Run and touches no Unity/ZNet API off the main thread —
    //    exactly the Wave1SrvAuditRpc.PostModLog contract, plus a result handshake so the queue can retry.
    internal static class Wave34Core
    {
        // ---- wire versions (first field of every payload; discard the whole reply on a mismatch) ----
        private const int CapVer = 1;

        // ---- ZDO keys, written as the persisted STRING hash rather than the ZDOVars field ----
        // ZDOVars.s_dead == "dead" (ZDOVars.cs:67), s_playerName == "playerName" (ZDOVars.cs:211),
        // s_ownerName == "ownerName" (ZDOVars.cs:189). The key string is what lives in the save file and can
        // never change; the C# field name could be renamed by a game update, so we hash the literal ourselves.
        private static readonly int DeadHash = "dead".GetStableHashCode();
        private static readonly int PlayerNameHash = "playerName".GetStableHashCode();
        private static readonly int OwnerNameHash = "ownerName".GetStableHashCode();

        // ---- store tables / logs (shared contract with the sibling wave files) ----
        private const string TblDeaths = "deaths";     // ticksUtc -> "id|name|x|y|z|hasTomb"
        private const string LogDeaths = "deathlog";
        private const int DeathRowCap = 2000;          // newest N kept; pruned on every write

        // ---- polling cadences ----
        private const float FastTickSeconds = 0.5f;    // probe queue + pending deaths + feed drain
        private const float DeathPollSeconds = 2f;
        private const float KeyPollSeconds = 5f;
        private const float TombstoneGraceSeconds = 3f; // the corpse's tombstone ZDO has to reach the server

        // ---- capability probe schedule ----
        private const float FirstProbeDelay = 10f;     // a just-joined client has no Player yet (same reason
                                                       // the MOTD waits; Wave2SrvOps.cs:99)
        private const int MaxProbeAttempts = 3;
        private const float SecondProbeDelay = 20f;
        private const float ThirdProbeDelay = 60f;
        private const int MaxModVersionLen = 32;

        // ---- discord feed ----
        private const int FeedQueueCap = 200;
        private const float MinPostIntervalSeconds = 2f;   // Discord rate-limits hard; one POST / 2 s
        private const float MaxBackoffSeconds = 300f;
        private const int MaxItemFailures = 5;             // then the item is dropped so the queue can move
        private const int TitleCap = 240;                  // Discord: 256
        private const int DescCap = 1500;                  // Discord: 4096; ours stays small on purpose
        private const int MaxErrorLen = 200;

        /// <summary>Embed colours siblings may reuse so the feed stays visually consistent.</summary>
        internal const int ColorJoin = 0x3BA55D;
        internal const int ColorLeave = 0x747F8D;
        internal const int ColorDeath = 0xED4245;
        internal const int ColorBoss = 0xFAA61A;
        internal const int ColorRaid = 0xE67E22;
        internal const int ColorInfo = 0x5865F2;

        // ==================== public data ====================

        /// <summary>
        /// One death, as observed from the server. TIER-VANILLA: every field is derived from the character
        /// ZDO and the peer list, so unmodded players produce these too. PlatformId / PlayerName fall back to
        /// "?" when the owning peer left between the death and the 2 s poll that noticed it.
        /// </summary>
        internal readonly struct DeathInfo
        {
            internal readonly long TicksUtc;
            internal readonly string PlayerName;
            internal readonly string PlatformId;
            internal readonly Vector3 Pos;
            internal readonly bool HasTombstone;

            internal DeathInfo(long ticksUtc, string playerName, string platformId, Vector3 pos, bool hasTombstone)
            {
                TicksUtc = ticksUtc;
                PlayerName = playerName ?? "?";
                PlatformId = platformId ?? "?";
                Pos = pos;
                HasTombstone = hasTombstone;
            }
        }

        /// <summary>Fires exactly once per death, ~3 s after it happened (see TombstoneGraceSeconds).</summary>
        internal static event Action<DeathInfo> OnDeath;

        /// <summary>
        /// Fires when a ZoneSystem global key APPEARS. Boss kills are observable this way
        /// ("defeated_eikthyr", "defeated_bonemass", ...) — but so are server-option keys, so consumers must
        /// filter. The raw key line is passed through unchanged (it may carry a value: "playerdamage 0.5").
        /// </summary>
        internal static event Action<string> OnGlobalKeyAdded;

        /// <summary>Fires when RandEventSystem's current random event changes to a non-null one (raid start).</summary>
        internal static event Action<string, Vector3> OnRaidStarted;

        // ==================== capability state ====================

        private sealed class PeerCap
        {
            public bool? Has;          // null = unknown / no answer yet
            public string Version = "";
            public int Attempts;
            public float NextProbeAt;  // Time.unscaledTime
        }

        private static readonly Dictionary<long, PeerCap> Caps = new Dictionary<long, PeerCap>();

        // ==================== detection state ====================

        private static readonly Dictionary<ZDOID, bool> PrevDead = new Dictionary<ZDOID, bool>();
        private static readonly HashSet<ZDOID> SeenThisPoll = new HashSet<ZDOID>();
        private static readonly List<ZDOID> DeadKeyScratch = new List<ZDOID>();

        private sealed class PendingDeath
        {
            public long TicksUtc;
            public string Name;
            public string Id;
            public Vector3 Pos;
            public float DueAt;
        }

        private static readonly List<PendingDeath> PendingDeaths = new List<PendingDeath>();

        private static HashSet<string> _globalKeys;      // null until the baseline poll seeds it
        private static string _activeRaid = "";
        private static bool _raidUnavailableLogged;

        private static Dictionary<int, string> _prefabNames;
        private static HashSet<int> _tombstoneFamily;
        private static object _prefabScene;
        private static readonly List<ZDO> SectorScratch = new List<ZDO>();

        // Peers we announced a join for, so the double ZNet.Disconnect a kick fires can only produce one
        // leave post (the same discipline Wave1's presence tracker uses).
        private static readonly Dictionary<long, string> AnnouncedPeers = new Dictionary<long, string>();

        // ==================== discord feed state ====================

        private sealed class FeedItem
        {
            public string Title;
            public string Desc;
            public int Color;
            public long TicksUtc;
            public int Failures;
        }

        // One gate for everything the worker task touches: the queue, the result handshake and the counters.
        private static readonly object FeedGate = new object();
        private static readonly List<FeedItem> FeedQueue = new List<FeedItem>();
        private static FeedItem _postItem;      // the item the in-flight POST is carrying (removed by reference)
        private static bool _postInFlight;
        private static bool _postDone;
        private static bool _postOk;
        private static string _postError;
        private static int _postRetryHintMs;
        private static int _sentTotal;
        private static int _failedTotal;
        private static int _droppedTotal;
        private static string _lastError = "";
        private static long _lastSentTicks;

        private static float _nextPostAt;             // main thread only
        private static float _backoffSeconds;         // main thread only
        private static int _tlsPrepared;

        // ==================== config ====================

        private static ConfigEntry<bool> _enableFeed;
        private static ConfigEntry<string> _webhookUrl;
        private static ConfigEntry<bool> _feedJoins;
        private static ConfigEntry<bool> _feedLeaves;
        private static ConfigEntry<bool> _feedDeaths;
        private static ConfigEntry<bool> _feedBossKills;
        private static ConfigEntry<bool> _feedRaids;

        private static bool FeedOn => _enableFeed != null && _enableFeed.Value && WebhookConfigured();

        private static bool _inited;
        private static float _nextFastTick;
        private static float _nextDeathTick;
        private static float _nextKeyTick;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableFeed = cfg.Bind("Features", "EnableDiscordFeed", false,
                    "Post server events (joins, leaves, deaths, boss kills, raids) to DiscordWebhookUrl as rich embeds. OFF by default: this sends player names to a third-party service, which no server owner should get by surprise. Needs a webhook URL to do anything.");
                _webhookUrl = cfg.Bind("Features", "DiscordWebhookUrl", "",
                    "Discord webhook URL the event feed posts to. Empty = feed disabled regardless of EnableDiscordFeed. This is a separate URL from ModLogWebhookUrl so moderation and player-facing events can go to different channels.");
                _feedJoins = cfg.Bind("Features", "FeedJoins", true,
                    "Include player joins in the Discord event feed (only meaningful when EnableDiscordFeed is on).");
                _feedLeaves = cfg.Bind("Features", "FeedLeaves", true,
                    "Include player disconnects in the Discord event feed (only meaningful when EnableDiscordFeed is on).");
                _feedDeaths = cfg.Bind("Features", "FeedDeaths", true,
                    "Include player deaths in the Discord event feed (only meaningful when EnableDiscordFeed is on).");
                _feedBossKills = cfg.Bind("Features", "FeedBossKills", true,
                    "Include boss kills (new 'defeated_*' global keys) in the Discord event feed (only meaningful when EnableDiscordFeed is on).");
                _feedRaids = cfg.Bind("Features", "FeedRaids", true,
                    "Include raid / random-event starts in the Discord event feed (only meaningful when EnableDiscordFeed is on).");
            }

            // AP_CapProbe / AP_CapReply are server<->client plumbing, not admin actions: deliberately NOT
            // registered with the audit chokepoint (they would write a line per peer per join for no signal).
            ApplyPatch("Wave34CoreRpcRegistration", typeof(Wave34RpcRegisterPatch),
                "capability probe unavailable - modded-client features will report 'unknown'");
            ApplyPatch("Wave34JoinPatch", typeof(Wave34JoinPatch),
                "capability probe on join + join feed unavailable");
            ApplyPatch("Wave34LeavePatch", typeof(Wave34LeavePatch),
                "capability cleanup on disconnect + leave feed unavailable");

            // The feed consumes the same public events siblings do — one code path, proven by use.
            OnDeath += FeedDeath;
            OnGlobalKeyAdded += FeedGlobalKey;
            OnRaidStarted += FeedRaid;
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

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;

            if (now >= _nextFastTick)
            {
                _nextFastTick = now + FastTickSeconds;
                try { StepProbes(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Capability probe tick failed: {e.Message}"); }
                try { StepPendingDeaths(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Death resolve tick failed: {e.Message}"); }
                try { StepFeed(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Discord feed tick failed: {e.Message}"); }
            }

            if (now >= _nextDeathTick)
            {
                _nextDeathTick = now + DeathPollSeconds;
                try { PollDeaths(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Death poll failed: {e.Message}"); }
            }

            if (now >= _nextKeyTick)
            {
                _nextKeyTick = now + KeyPollSeconds;
                try { PollGlobalKeys(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Global-key poll failed: {e.Message}"); }
                try { PollRaid(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Raid poll failed: {e.Message}"); }
            }
        }

        // ==================== RPC registration ====================

        // Both names are registered on BOTH sides on purpose: the companion DLL is the same file on the
        // server and on an admin's client, and each handler guards its own side (OnCapProbe refuses anything
        // that is not the server and needs a local Player; OnCapReply refuses to run off-server).
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave34RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_CapProbe", new Action<long>(OnCapProbe));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_CapReply", OnCapReply);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Capability-probe RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. capability probe ====================

        /// <summary>
        /// Does this peer's client run AdminPanelCompanion.dll? true = yes (it answered), false = it did not
        /// answer within three probes over ~90 s (almost always a vanilla client), null = still unknown
        /// (never probed, or a probe is in flight). Callers MUST degrade on anything but true and explain
        /// why — CapReason(uid) produces the sentence.
        /// </summary>
        internal static bool? HasMod(long uid)
        {
            PeerCap c;
            return Caps.TryGetValue(uid, out c) ? c.Has : (bool?)null;
        }

        /// <summary>The companion version the peer's client reported, or "" when it never answered.</summary>
        internal static string ModVersion(long uid)
        {
            PeerCap c;
            return Caps.TryGetValue(uid, out c) && c.Version != null ? c.Version : "";
        }

        /// <summary>
        /// Send a probe to this peer right now (and start tracking it if it was unknown). Cheap; use it when
        /// an admin action needs a fresh answer instead of waiting for the join schedule.
        /// </summary>
        internal static void ProbePeer(long uid)
        {
            if (uid == 0L || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            PeerCap c;
            if (!Caps.TryGetValue(uid, out c))
            {
                c = new PeerCap();
                Caps[uid] = c;
            }
            if (c.Has == true) return;   // already answered; nothing to learn
            SendProbe(uid, c);
        }

        /// <summary>Plain-English reason a modded-client feature is unavailable for this peer ("" when it is available).</summary>
        internal static string CapReason(long uid)
        {
            var has = HasMod(uid);
            if (has == true) return "";
            if (has == null)
                return "still checking whether that player's client runs AdminPanelCompanion.dll - try again in a few seconds";
            return "that player's client does not run AdminPanelCompanion.dll, so it cannot answer requests about their character. " +
                   "Inventory, skills and status effects live in the player's own save file, not on the server. " +
                   "Teleport, kick/ban, chat and messages still work for them.";
        }

        private static void SendProbe(long uid, PeerCap c)
        {
            c.Attempts++;
            var delay = c.Attempts <= 1 ? SecondProbeDelay : ThirdProbeDelay;
            c.NextProbeAt = Time.unscaledTime + delay;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "AP_CapProbe"); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_CapProbe to {uid} failed: {e.Message}"); }
        }

        // Values are mutated in place (never added/removed here), so iterating the dictionary is safe.
        private static void StepProbes(float now)
        {
            if (Caps.Count == 0) return;
            foreach (var kv in Caps)
            {
                var c = kv.Value;
                if (c.Has == true) continue;
                if (c.Attempts >= MaxProbeAttempts)
                {
                    // Out of attempts: only NOW may we call it a vanilla client, and only after the last
                    // probe's window elapsed (a slow client still loading its world can answer late, and a
                    // late answer always wins — OnCapReply sets Has=true whenever it arrives).
                    if (c.Has == null && now >= c.NextProbeAt) c.Has = false;
                    continue;
                }
                if (now < c.NextProbeAt) continue;
                if (!PeerConnected(kv.Key)) continue;   // left during the delay; the leave patch drops it
                SendProbe(kv.Key, c);
            }
        }

        // ---- CLIENT side: answer the server's probe ----
        // Trust template copied from CompanionPlugin.OnHealSelf (CompanionPlugin.cs:910-917): a client-side
        // executor runs only for packets the SERVER sent, and only once a local Player exists. A dedicated
        // server has no local Player, so it never answers its own probe.
        private static void OnCapProbe(long sender)
        {
            try
            {
                if (!SenderIsTrustedServer(sender)) return;
                if (Player.m_localPlayer == null) return;   // not in-world yet; the server probes again later
                var pkg = new ZPackage();
                pkg.Write(CapVer);
                pkg.Write(CompanionPlugin.PluginVersion ?? "");
                ZRoutedRpc.instance?.InvokeRoutedRPC(sender, "AP_CapReply", pkg);
            }
            catch (Exception) { /* never let a probe answer break a client */ }
        }

        // ---- SERVER side: record the answer ----
        // The sender is trustworthy: RouteRpcSanitizer re-stamps every socket-delivered packet with the real
        // peer uid (CompanionPlugin.cs:103-129), so a client can only ever claim capability for ITSELF.
        private static void OnCapReply(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int ver;
            string modVersion;
            try
            {
                ver = pkg.ReadInt();
                modVersion = pkg.ReadString() ?? "";
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_CapReply: malformed packet dropped ({e.Message})"); return; }

            if (ver != CapVer)
            {
                // Unknown payload version: discard the WHOLE reply rather than guessing at its fields.
                CompanionPlugin.FeatureLog($"AP_CapReply from {sender}: unsupported payload version {ver} (expected {CapVer}) - discarded");
                return;
            }

            modVersion = modVersion.Trim();
            if (modVersion.Length > MaxModVersionLen) modVersion = modVersion.Substring(0, MaxModVersionLen);

            PeerCap c;
            if (!Caps.TryGetValue(sender, out c))
            {
                c = new PeerCap();
                Caps[sender] = c;
            }
            var first = c.Has != true;
            c.Has = true;
            c.Version = modVersion;
            if (first)
                CompanionPlugin.FeatureLog($"Capability: {CompanionPlugin.SenderDisplayName(sender)} runs AdminPanelCompanion {(modVersion.Length == 0 ? "?" : modVersion)}");
        }

        // CompanionPlugin.SenderIsServer is private, and Wave*Srv files are plain static classes. Bind it
        // reflectively (it lives in OUR assembly, so this is stable) and keep an equivalent fallback so a
        // future rename degrades to "the probe stops answering", never to "anyone can impersonate the server".
        private static MethodInfo _senderIsServerMi;
        private static bool _senderIsServerProbed;

        private static bool SenderIsTrustedServer(long sender)
        {
            if (!_senderIsServerProbed)
            {
                _senderIsServerProbed = true;
                try { _senderIsServerMi = AccessTools.Method(typeof(CompanionPlugin), "SenderIsServer", new[] { typeof(long) }); }
                catch (Exception) { }
            }
            if (_senderIsServerMi != null)
            {
                try { return (bool)_senderIsServerMi.Invoke(null, new object[] { sender }); }
                catch (Exception) { }
            }
            return FallbackSenderIsServer(sender);
        }

        private static bool FallbackSenderIsServer(long sender)
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return false;
                var serverPeer = znet.GetServerPeer();                       // non-null only on a client
                if (serverPeer != null && serverPeer.m_uid != 0L && sender == serverPeer.m_uid) return true;
                if (!znet.IsServer() || ZDOMan.instance == null) return false;
                if (sender != ZDOMan.GetSessionID()) return false;
                // Host case: the host IS the server, so a locally dispatched packet carries its session id.
                // Trust it ONLY while the sender sanitizer is live — otherwise the id is attacker-chosen.
                var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                return f != null && (bool)f.GetValue(null);
            }
            catch (Exception) { return false; }
        }

        // ==================== 2. death detection (TIER-VANILLA) ====================

        // Player.OnDeath sets the character ZDO's "dead" bool to true (Player.cs:3048) and OnRespawn sets it
        // back to false (Player.cs:3146). Both writes are made by the OWNING client and replicate to the
        // server as ordinary ZDO data, so this observes unmodded players exactly as well as modded ones.
        // ZNet.GetAllCharacterZDOS() (ZNet.cs:1839) is one lookup per ready peer — not a world sweep.
        private static void PollDeaths()
        {
            var znet = ZNet.instance;
            if (znet == null) return;
            List<ZDO> chars;
            try { chars = znet.GetAllCharacterZDOS(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"GetAllCharacterZDOS failed: {e.Message}"); return; }
            if (chars == null) return;

            SeenThisPoll.Clear();
            for (var i = 0; i < chars.Count; i++)
            {
                var zdo = chars[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    var id = zdo.m_uid;
                    SeenThisPoll.Add(id);
                    var dead = zdo.GetBool(DeadHash, false);

                    bool prev;
                    if (!PrevDead.TryGetValue(id, out prev))
                    {
                        // First sight: seed only. A corpse observed after a server restart (or a character
                        // that reconnects still dead) must not be reported as a fresh death.
                        PrevDead[id] = dead;
                        continue;
                    }
                    if (dead == prev) continue;
                    PrevDead[id] = dead;
                    if (!dead) continue;    // respawn: true -> false
                    QueueDeath(zdo);
                }
                catch (Exception) { /* one malformed character ZDO must not stop the poll */ }
            }

            // Drop characters that are no longer connected. Always (not only when the counts differ): one
            // player leaving while another joins in the same 2 s window keeps the counts equal.
            DeadKeyScratch.Clear();
            foreach (var kv in PrevDead)
                if (!SeenThisPoll.Contains(kv.Key)) DeadKeyScratch.Add(kv.Key);
            for (var i = 0; i < DeadKeyScratch.Count; i++) PrevDead.Remove(DeadKeyScratch[i]);
        }

        // The tombstone is spawned by the dying client and its ZDO needs a moment to reach us, so the death
        // is parked for TombstoneGraceSeconds and only then recorded + announced. That delay is also what
        // guarantees "fire exactly once": nothing is emitted until the pending entry is consumed.
        private static void QueueDeath(ZDO zdo)
        {
            var pos = Vector3.zero;
            var name = "";
            try { pos = zdo.GetPosition(); } catch (Exception) { }
            try { name = zdo.GetString(PlayerNameHash, ""); } catch (Exception) { }

            var id = "?";
            try
            {
                var peer = PeerOfCharacter(zdo.m_uid);
                if (peer != null)
                {
                    if (peer.m_socket != null)
                    {
                        var host = peer.m_socket.GetHostName();
                        if (!string.IsNullOrEmpty(host)) id = host;
                    }
                    if (string.IsNullOrEmpty(name)) name = peer.m_playerName ?? "";
                }
            }
            catch (Exception) { }

            PendingDeaths.Add(new PendingDeath
            {
                TicksUtc = DateTime.UtcNow.Ticks,
                Name = CleanText(string.IsNullOrEmpty(name) ? "?" : name, 60),
                Id = CleanText(id, 64),
                Pos = pos,
                DueAt = Time.unscaledTime + TombstoneGraceSeconds,
            });
        }

        private static void StepPendingDeaths(float now)
        {
            if (PendingDeaths.Count == 0) return;
            for (var i = PendingDeaths.Count - 1; i >= 0; i--)
            {
                var p = PendingDeaths[i];
                if (now < p.DueAt) continue;
                PendingDeaths.RemoveAt(i);

                var hasTomb = false;
                try { hasTomb = TombstoneNear(p.Pos, p.Name); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Tombstone probe failed: {e.Message}"); }

                var info = new DeathInfo(p.TicksUtc, p.Name, p.Id, p.Pos, hasTomb);
                try { RecordDeath(info); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Death record failed: {e.Message}"); }
                FireDeath(info);
            }
        }

        private static void RecordDeath(DeathInfo d)
        {
            FeatureStore.Append(LogDeaths,
                $"{new DateTime(d.TicksUtc, DateTimeKind.Utc):yyyy-MM-dd'T'HH:mm:ss'Z'}|{d.PlatformId}|{d.PlayerName}|" +
                $"{F(d.Pos.x)}|{F(d.Pos.y)}|{F(d.Pos.z)}|{(d.HasTombstone ? 1 : 0)}");

            if (!FeatureStore.Ready) return;
            var t = FeatureStore.Table(TblDeaths);
            var key = d.TicksUtc;
            while (t.ContainsKey(key.ToString(CultureInfo.InvariantCulture))) key++;   // ticks collision (never seen, cheap)
            t[key.ToString(CultureInfo.InvariantCulture)] =
                $"{d.PlatformId}|{d.PlayerName}|{F(d.Pos.x)}|{F(d.Pos.y)}|{F(d.Pos.z)}|{(d.HasTombstone ? 1 : 0)}";
            PruneDeaths(t);
            FeatureStore.SaveTable(TblDeaths);

            CompanionPlugin.FeatureLog(
                $"Death: {d.PlayerName} ({d.PlatformId}) at {F(d.Pos.x)},{F(d.Pos.y)},{F(d.Pos.z)}{(d.HasTombstone ? " (tombstone)" : " (no tombstone - empty inventory)")}");
        }

        private static void PruneDeaths(Dictionary<string, string> t)
        {
            if (t.Count <= DeathRowCap) return;
            var keys = new List<long>(t.Count);
            var unparsable = new List<string>();
            foreach (var kv in t)
            {
                long v;
                if (long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) keys.Add(v);
                else unparsable.Add(kv.Key);
            }
            foreach (var k in unparsable) t.Remove(k);   // hand-edited junk never survives a prune
            if (keys.Count <= DeathRowCap) return;
            keys.Sort();
            var drop = keys.Count - DeathRowCap;
            for (var i = 0; i < drop; i++) t.Remove(keys[i].ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Newest death rows from the "deaths" table, oldest first / newest LAST (the ordering every other
        /// log payload in this mod uses). Pass an empty id for "everyone". Siblings should read deaths through
        /// this instead of re-parsing the table, so the row format stays owned by one file.
        /// </summary>
        internal static List<DeathInfo> RecentDeaths(string idOrEmpty, int max)
        {
            var res = new List<DeathInfo>();
            if (max < 1) return res;
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(TblDeaths); }
            catch (Exception) { return res; }
            if (t == null || t.Count == 0) return res;

            var rows = new List<KeyValuePair<long, string>>(t.Count);
            foreach (var kv in t)
            {
                long ticks;
                if (!long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                rows.Add(new KeyValuePair<long, string>(ticks, kv.Value));
            }
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));   // oldest first

            var wantId = string.IsNullOrEmpty(idOrEmpty) ? null : idOrEmpty.Trim();
            for (var i = rows.Count - 1; i >= 0 && res.Count < max; i--)
            {
                var parts = (rows[i].Value ?? "").Split('|');
                if (parts.Length < 6) continue;
                if (wantId != null && !Wave1AuditRpc.SameId(parts[0], wantId)) continue;
                res.Add(new DeathInfo(rows[i].Key, parts[1], parts[0],
                    new Vector3(P(parts[2]), P(parts[3]), P(parts[4])), parts[5] == "1"));
            }
            res.Reverse();   // newest LAST
            return res;
        }

        // ---- tombstone lookup ----
        //
        // TIER-VANILLA and deliberately best-effort. A tombstone only spawns when the player died carrying
        // something (TombStone/Container), so "no tombstone" legitimately means "died empty-handed" as often
        // as it means "we missed it". The tombstone ZDO's s_owner is a PROFILE id (Game.instance's player id),
        // NOT a peer uid and NOT a platform id, so it is never used for matching here — we match on position
        // and, when both sides have one, on the "ownerName" string the tombstone carries.
        private static bool TombstoneNear(Vector3 pos, string ownerName)
        {
            var man = ZDOMan.instance;
            if (man == null || !EnsurePrefabMap()) return false;
            if (_tombstoneFamily == null || _tombstoneFamily.Count == 0) return false;

            Vector2s zone;   // shorts since 1.0.12
            try { zone = ZoneSystem.GetZone(pos); }
            catch (Exception) { return false; }

            SectorScratch.Clear();
            try { ZoneCompat.FindSectorObjects(man, zone, 1, 0, SectorScratch, null); }   // 3x3 zones around the death
            catch (Exception) { SectorScratch.Clear(); return false; }

            var wantName = string.IsNullOrEmpty(ownerName) || ownerName == "?" ? null : ownerName;
            var found = false;
            for (var i = 0; i < SectorScratch.Count; i++)
            {
                var zdo = SectorScratch[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    if (!_tombstoneFamily.Contains(zdo.GetPrefab())) continue;
                    if (Vector3.Distance(zdo.GetPosition(), pos) > 25f) continue;
                    if (wantName != null)
                    {
                        var owner = zdo.GetString(OwnerNameHash, "");
                        if (owner.Length > 0 && !string.Equals(owner, wantName, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    found = true;
                    break;
                }
                catch (Exception) { }
            }
            SectorScratch.Clear();   // never hold references to pooled ZDOs across frames
            return found;
        }

        // Same approach Wave2World uses for the ItemDrop family: a prefab belongs to the tombstone family iff
        // its PREFAB ASSET carries a TombStone component. Prefab assets do exist on a dedicated server.
        private static bool EnsurePrefabMap()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return false;
            if (_prefabNames != null && ReferenceEquals(_prefabScene, scene)) return true;

            var names = new Dictionary<int, string>();
            var tombs = new HashSet<int>();
            try
            {
                var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")?.GetValue(scene)
                    as Dictionary<int, GameObject>;
                if (named != null)
                {
                    foreach (var kv in named) RegisterPrefab(names, tombs, kv.Key, kv.Value);
                }
                else if (scene.m_prefabs != null)
                {
                    foreach (var go in scene.m_prefabs)
                    {
                        if (go == null) continue;
                        RegisterPrefab(names, tombs, go.name.GetStableHashCode(), go);
                    }
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave34 prefab map unavailable ({e.Message}); deaths will always report 'no tombstone'.");
                return false;
            }
            if (names.Count == 0) return false;

            _prefabNames = names;
            _tombstoneFamily = tombs;
            _prefabScene = scene;
            return true;
        }

        private static void RegisterPrefab(Dictionary<int, string> names, HashSet<int> tombs, int hash, GameObject go)
        {
            if (go == null) return;
            names[hash] = go.name;
            // Isolated so a game update that DELETES the TombStone type throws only here (caught) instead of
            // failing the JIT of a bigger method. The name fallback keeps vanilla graves detectable anyway.
            try { if (go.GetComponent<TombStone>() != null) { tombs.Add(hash); return; } }
            catch (Exception) { }
            if (go.name != null && go.name.IndexOf("tombstone", StringComparison.OrdinalIgnoreCase) >= 0)
                tombs.Add(hash);
        }

        // ==================== 3. boss kills (global keys) + raids ====================

        // Boss kills are not directly observable on a server (no Character GameObjects), but every boss death
        // writes a global key — "defeated_eikthyr", "defeated_gdking", ... (GlobalKeys enum). ZoneSystem
        // owns the set on the server and GetGlobalKeys() is public (ZoneSystem.cs:2678). Server-option keys
        // ("playerdamage 0.5") live in the same set, so consumers filter; we pass the raw line through.
        private static void PollGlobalKeys()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return;
            List<string> keys;
            try { keys = zs.GetGlobalKeys(); }
            catch (Exception) { return; }
            if (keys == null) return;

            var cur = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            if (_globalKeys == null)
            {
                _globalKeys = cur;   // baseline: the world's existing keys are not "new"
                return;
            }
            List<string> added = null;
            foreach (var k in cur)
                if (!_globalKeys.Contains(k)) (added ?? (added = new List<string>())).Add(k);
            _globalKeys = cur;
            if (added == null) return;
            foreach (var k in added)
            {
                CompanionPlugin.FeatureLog($"Global key added: {k}");
                FireKey(k);
            }
        }

        /// <summary>True for the global keys that represent a boss/creature kill rather than a server option.</summary>
        internal static bool IsKillKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("killed_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("killed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Name of the raid / random event currently running, or "" (also "" when unobservable).</summary>
        internal static string ActiveRaid() => _activeRaid ?? "";

        // RandEventSystem drives random events ON THE SERVER (RandEventSystem.FixedUpdate branches on
        // ZNet.instance.IsServer()) and GetCurrentRandomEvent() is public, returning a RandomEvent whose
        // m_name / m_pos are public fields. That makes raid starts genuinely observable — no guessing.
        // The read is isolated in its own method so a removed type/method degrades to "raids unavailable".
        private static void PollRaid()
        {
            string name;
            Vector3 pos;
            if (!TryReadActiveRaid(out name, out pos))
            {
                if (!_raidUnavailableLogged)
                {
                    _raidUnavailableLogged = true;
                    CompanionPlugin.FeatureLog("Raid detection unavailable on this game build (RandEventSystem could not be read); every other event still works.");
                }
                return;
            }
            if (string.Equals(name, _activeRaid, StringComparison.Ordinal)) return;
            _activeRaid = name;
            if (name.Length == 0) return;   // event ended
            CompanionPlugin.FeatureLog($"Raid started: {name} at {F(pos.x)},{F(pos.z)}");
            FireRaid(name, pos);
        }

        private static bool TryReadActiveRaid(out string name, out Vector3 pos)
        {
            name = "";
            pos = Vector3.zero;
            try
            {
                var res = RandEventSystem.instance;
                if (res == null) return false;
                var ev = res.GetCurrentRandomEvent();
                if (ev == null) return true;         // readable, simply nothing running
                name = ev.m_name ?? "";
                pos = ev.m_pos;
                return true;
            }
            catch (Exception) { return false; }
        }

        // ==================== event fan-out ====================
        // Each subscriber is invoked separately: one throwing sibling must not stop the others.

        private static void FireDeath(DeathInfo d)
        {
            var handler = OnDeath;
            if (handler == null) return;
            foreach (var del in handler.GetInvocationList())
            {
                try { ((Action<DeathInfo>)del)(d); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"OnDeath subscriber failed: {e.Message}"); }
            }
        }

        private static void FireKey(string key)
        {
            var handler = OnGlobalKeyAdded;
            if (handler == null) return;
            foreach (var del in handler.GetInvocationList())
            {
                try { ((Action<string>)del)(key); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"OnGlobalKeyAdded subscriber failed: {e.Message}"); }
            }
        }

        private static void FireRaid(string name, Vector3 pos)
        {
            var handler = OnRaidStarted;
            if (handler == null) return;
            foreach (var del in handler.GetInvocationList())
            {
                try { ((Action<string, Vector3>)del)(name, pos); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"OnRaidStarted subscriber failed: {e.Message}"); }
            }
        }

        // ==================== 4. discord event feed ====================

        /// <summary>True when a webhook URL is configured (independent of the on/off switch).</summary>
        internal static bool WebhookConfigured()
        {
            var url = _webhookUrl != null ? (_webhookUrl.Value ?? "").Trim() : "";
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Queue one embed for the Discord feed. No-op when the feed is off or unconfigured, so callers never
        /// need to check first. Bounded at 200 items: on overflow the OLDEST is dropped (a stale join notice
        /// matters less than a fresh raid alert) and the drop is counted. Never blocks, never throws.
        /// </summary>
        internal static void Enqueue(string title, string description, int color)
        {
            if (!FeedOn) return;
            EnqueueInternal(title, description, color);
        }

        /// <summary>
        /// Queue an embed that ignores the EnableDiscordFeed switch (it still needs a webhook URL). This is
        /// what an explicit "send a test message" button uses. Returns false when no URL is configured.
        /// </summary>
        internal static bool EnqueueTest(string title, string description, int color)
        {
            if (!WebhookConfigured()) return false;
            EnqueueInternal(title, description, color);
            return true;
        }

        private static void EnqueueInternal(string title, string description, int color)
        {
            var item = new FeedItem
            {
                Title = Clamp(title, TitleCap),
                Desc = Clamp(description, DescCap),
                Color = color & 0xFFFFFF,
                TicksUtc = DateTime.UtcNow.Ticks,
            };
            lock (FeedGate)
            {
                if (FeedQueue.Count >= FeedQueueCap)
                {
                    FeedQueue.RemoveAt(0);
                    _droppedTotal++;
                }
                FeedQueue.Add(item);
            }
        }

        /// <summary>
        /// Live feed status for the panel (AP_SrvDiscordState). queued = items still waiting, sent/failed are
        /// cumulative POST outcomes since server start, lastTicks = DateTime.UtcNow.Ticks of the last success.
        /// </summary>
        internal static (bool feedOn, int queued, int sent, int failed, string lastError, long lastTicks) FeedState()
        {
            lock (FeedGate)
            {
                return (FeedOn, FeedQueue.Count, _sentTotal, _failedTotal, _lastError ?? "", _lastSentTicks);
            }
        }

        /// <summary>Cumulative count of embeds discarded because the queue was full.</summary>
        internal static int FeedDropped()
        {
            lock (FeedGate) return _droppedTotal;
        }

        // ---- built-in feed sources ----

        private static void FeedDeath(DeathInfo d)
        {
            if (_feedDeaths != null && !_feedDeaths.Value) return;
            Enqueue("Death",
                $"**{d.PlayerName}** died at {F(d.Pos.x)}, {F(d.Pos.y)}, {F(d.Pos.z)}" +
                (d.HasTombstone ? "\nA tombstone was left behind." : "\nNo tombstone (empty inventory)."),
                ColorDeath);
        }

        private static void FeedGlobalKey(string key)
        {
            if (!IsKillKey(key)) return;
            if (_feedBossKills != null && !_feedBossKills.Value) return;
            Enqueue("Boss defeated", $"Global key **{key}** was set - a boss has been defeated.", ColorBoss);
        }

        private static void FeedRaid(string name, Vector3 pos)
        {
            if (_feedRaids != null && !_feedRaids.Value) return;
            Enqueue("Raid started", $"Event **{name}** started near {F(pos.x)}, {F(pos.z)}.", ColorRaid);
        }

        // ---- the drain ----

        private static void StepFeed(float now)
        {
            FeedItem head = null;
            lock (FeedGate)
            {
                // 1) consume the previous POST's result (produced on a worker thread).
                if (_postDone)
                {
                    _postDone = false;
                    var sent = _postItem;
                    _postItem = null;
                    if (_postOk)
                    {
                        // Remove BY REFERENCE, never by index: the queue can be trimmed (overflow) or cleared
                        // (webhook unset) while a POST is in flight, and index 0 could be a different item.
                        if (sent != null) FeedQueue.Remove(sent);
                        _sentTotal++;
                        _lastSentTicks = DateTime.UtcNow.Ticks;
                        _lastError = "";
                        _backoffSeconds = 0f;
                        _nextPostAt = now + MinPostIntervalSeconds;
                    }
                    else
                    {
                        _failedTotal++;
                        _lastError = Clamp(_postError ?? "unknown error", MaxErrorLen);
                        if (sent != null)
                        {
                            sent.Failures++;
                            if (sent.Failures >= MaxItemFailures)
                            {
                                FeedQueue.Remove(sent);
                                _droppedTotal++;
                                CompanionPlugin.FeatureLog($"Discord feed: dropping '{sent.Title}' after {MaxItemFailures} failed attempts ({_lastError})");
                            }
                        }
                        _backoffSeconds = _backoffSeconds <= 0f
                            ? MinPostIntervalSeconds
                            : Mathf.Min(_backoffSeconds * 2f, MaxBackoffSeconds);
                        var hint = _postRetryHintMs > 0 ? _postRetryHintMs / 1000f : 0f;
                        _nextPostAt = now + Mathf.Max(_backoffSeconds, hint);
                        _postRetryHintMs = 0;
                    }
                }

                // 2) start the next POST when the queue, the rate limit and the backoff all allow it.
                if (_postInFlight || FeedQueue.Count == 0 || now < _nextPostAt) return;
                if (!WebhookConfigured())
                {
                    // The URL was cleared while items were queued: discard rather than spin forever.
                    FeedQueue.Clear();
                    return;
                }
                head = FeedQueue[0];
                _postItem = head;
                _postInFlight = true;
            }
            StartPost(head);
        }

        // Copied from Wave1SrvAuditRpc.PostModLog: HttpWebRequest on Task.Run, TLS 1.2 OR'd in once, 10 s
        // timeouts, every exception swallowed. The ONLY addition is the result handshake — plain fields under
        // FeedGate, no Unity/ZNet call anywhere inside the task.
        private static void StartPost(FeedItem item)
        {
            byte[] body;
            string url;
            try
            {
                url = (_webhookUrl.Value ?? "").Trim();
                body = Encoding.UTF8.GetBytes(BuildEmbedJson(item));
            }
            catch (Exception e)
            {
                lock (FeedGate) { _postInFlight = false; _postDone = true; _postOk = false; _postError = e.Message; }
                return;
            }

            PrepareTls();
            try
            {
                Task.Run(() =>
                {
                    var ok = false;
                    string err = null;
                    var retryMs = 0;
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Method = "POST";
                        req.ContentType = "application/json";
                        req.Timeout = 10000;
                        req.ReadWriteTimeout = 10000;
                        req.UserAgent = "AdminPanelCompanion/" + CompanionPlugin.PluginVersion;
                        req.ContentLength = body.Length;
                        try { req.ServicePoint.Expect100Continue = false; } catch (Exception) { }
                        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                        using (var resp = req.GetResponse()) { }   // drain + dispose
                        ok = true;
                    }
                    catch (WebException we)
                    {
                        err = we.Message;
                        try
                        {
                            var resp = we.Response as HttpWebResponse;
                            if (resp != null)
                            {
                                err = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                                if ((int)resp.StatusCode == 429)
                                {
                                    retryMs = 10000;   // rate limited: back off well past the usual window
                                    var ra = resp.Headers != null ? resp.Headers["Retry-After"] : null;
                                    double secs;
                                    if (!string.IsNullOrEmpty(ra) &&
                                        double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out secs) &&
                                        secs > 0d && secs < 600d)
                                        retryMs = (int)(secs * 1000d);
                                }
                                resp.Close();
                            }
                        }
                        catch (Exception) { }
                    }
                    catch (Exception e) { err = e.Message; }

                    lock (FeedGate)
                    {
                        _postOk = ok;
                        _postError = err;
                        _postRetryHintMs = retryMs;
                        _postInFlight = false;
                        _postDone = true;
                    }
                });
            }
            catch (Exception e)
            {
                lock (FeedGate) { _postInFlight = false; _postDone = true; _postOk = false; _postError = e.Message; }
            }
        }

        // net48 ships no JSON writer and the mod ships no dependencies, so the embed is built by hand and
        // every string is escaped by hand — exactly like Wave1SrvAuditRpc.
        private static string BuildEmbedJson(FeedItem item)
        {
            var sb = new StringBuilder(item.Title.Length + item.Desc.Length + 160);
            sb.Append("{\"embeds\":[{\"title\":\"").Append(JsonEscape(item.Title))
              .Append("\",\"description\":\"").Append(JsonEscape(item.Desc))
              .Append("\",\"color\":").Append(item.Color.ToString(CultureInfo.InvariantCulture))
              .Append(",\"timestamp\":\"")
              .Append(new DateTime(item.TicksUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
              .Append("\"}]}");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // SecurityProtocol is process-global: OR TLS 1.2 in exactly once, and only when a webhook is used.
        private static void PrepareTls()
        {
            if (Interlocked.Exchange(ref _tlsPrepared, 1) != 0) return;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
        }

        // ==================== join / leave hooks ====================

        // POSTFIX on RPC_PeerInfo: m_uid / m_playerName exist by now (the vanilla reject ladder runs first,
        // ZNet.cs:931-933), which is why every join hook in this mod is a postfix. Stacked alongside
        // CompanionPlugin.PeerJoinLogPatch and the wave-1/2 postfixes — Harmony allows several classes per
        // target; the house rule is one TARGET per class.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave34JoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!_inited || __instance == null || !__instance.IsServer() || rpc == null) return;
                try
                {
                    ZNetPeer peer = null;
                    foreach (var p in __instance.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null || peer.m_uid == 0L) return;
                    if (string.IsNullOrEmpty(peer.m_playerName)) return;   // rejected connection

                    // Schedule the capability probe. A client that just finished the handshake has no Player
                    // yet, so an instant probe is silently ignored — same reason the MOTD waits 10 s.
                    PeerCap c;
                    if (!Caps.TryGetValue(peer.m_uid, out c))
                    {
                        c = new PeerCap();
                        Caps[peer.m_uid] = c;
                    }
                    if (c.Attempts == 0 && c.NextProbeAt <= 0f) c.NextProbeAt = Time.unscaledTime + FirstProbeDelay;

                    var name = CleanText(peer.m_playerName, 60);
                    var host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                    if (AnnouncedPeers.ContainsKey(peer.m_uid)) return;    // double RPC_PeerInfo is possible
                    AnnouncedPeers[peer.m_uid] = name;

                    if (_feedJoins == null || _feedJoins.Value)
                        Enqueue("Player joined",
                            $"**{name}** joined the server.\nOnline: {OnlineCount()}" +
                            (string.IsNullOrEmpty(host) ? "" : $"\nId: `{CleanText(host, 64)}`"),
                            ColorJoin);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave34 join hook failed: {e.Message}"); }
            }
        }

        // PREFIX on Disconnect — the peer is still readable here (the original disposes it).
        [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
        internal static class Wave34LeavePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (!_inited || __instance == null || !__instance.IsServer() || peer == null) return;
                try
                {
                    Caps.Remove(peer.m_uid);
                    if (!peer.m_characterID.IsNone()) PrevDead.Remove(peer.m_characterID);

                    string name;
                    if (!AnnouncedPeers.TryGetValue(peer.m_uid, out name)) return;   // never announced / already left
                    AnnouncedPeers.Remove(peer.m_uid);
                    if (_feedLeaves != null && !_feedLeaves.Value) return;
                    // OnlineCount() still includes this peer at prefix time.
                    Enqueue("Player left", $"**{name}** left the server.\nOnline: {Math.Max(0, OnlineCount() - 1)}", ColorLeave);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave34 leave hook failed: {e.Message}"); }
            }
        }

        // ==================== shared helpers ====================

        private static ZNetPeer PeerOfCharacter(ZDOID characterId)
        {
            if (ZNet.instance == null || characterId.IsNone()) return null;
            foreach (var p in ZNet.instance.GetPeers())
                if (p != null && p.m_characterID == characterId) return p;
            return null;
        }

        private static bool PeerConnected(long uid)
        {
            try { return ZNet.instance != null && ZNet.instance.GetPeer(uid) != null; }
            catch (Exception) { return false; }
        }

        private static int OnlineCount()
        {
            try
            {
                var peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
                if (peers == null) return 0;
                var n = 0;
                foreach (var p in peers) if (p != null && p.IsReady()) n++;
                return n;
            }
            catch (Exception) { return 0; }
        }

        // Invariant culture everywhere: a server running under a comma-decimal locale must still write rows
        // the panel (and this file's own parser) can read back.
        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        private static float P(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        // '|' is the field separator inside stored rows, so it can never survive in free text.
        private static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static string Clamp(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }
}

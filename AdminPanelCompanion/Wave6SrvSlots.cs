using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 6 — reserved slots, config packs, metrics export ====================
    // Three server-owner tools that share nothing but a file: they are grouped because each is small and
    // each is opt-in.
    //
    //  1. RESERVED SLOTS + JOIN QUEUE  (EnableReservedSlots, default FALSE)
    //     A second, independent PREFIX on ZNet.RPC_PeerInfo that refuses NON-ADMIN joins once the server is
    //     inside its reserved band, so an owner/moderator can always get in. Rejected ids go into an
    //     in-memory queue; when a slot frees the front of the queue is "held" for 60 s.
    //
    //  2. CONFIG PACK EXPORT / IMPORT  (no config flag — the RPCs are owner-gated instead)
    //     Serialises the companion's [Features] config section to "Features.<Key>=<value>" lines so an owner
    //     can copy a tuned setup between servers. Webhook/URL/token/password/secret values are REDACTED on
    //     export and REFUSED on import, so a pack is safe to paste into a Discord thread.
    //
    //  3. METRICS + CSV EXPORT  (EnableMetricsExport, default FALSE)
    //     Writes "metrics.prom" (Prometheus text exposition) and "export_<table>.csv" (RFC 4180) into the
    //     FeatureStore data dir on a timer, for an EXTERNAL scraper/agent to read off disk.
    //
    // Design rules obeyed (spec-companion.md / spec-valheim-api.md):
    //  * Every behaviour-changing feature here defaults OFF. Upgrading the DLL changes nothing until an
    //    operator flips a flag.
    //  * The join prefix FAILS OPEN: any exception, any unreadable engine field, and the connection is
    //    allowed. A bug in a convenience feature must never make a server unjoinable.
    //  * ONE Harmony class per target method, applied in its own try/catch, reported to Wave2Ops.ReportPatch.
    //  * Nothing here calls into another wave's sampler/scheduler. Frame times are sampled by THIS file into
    //    its own ring buffer (wave 2 has its own, deliberately not shared).
    //  * Tables owned by other waves (eco/presence/deaths/guard_flags) are read straight from FeatureStore,
    //    never through a cross-module call — same discipline as Wave3Discord's death feed.
    internal static class Wave6Slots
    {
        // ---- tables read/written here ----
        private const string TblSlots = "slots";        // "reserved" -> N   (last effective value, for the panel/operator)

        // ---- tables exported to CSV (owned by other waves; layouts below ARE the shared contract) ----
        private const string TblEco = "eco";            // id    -> "balance|lifetime|name"                  (wave 6 economy)
        private const string TblPresence = "presence";  // id    -> "first|last|sessions|totalSeconds|name"  (wave 1)
        private const string TblDeaths = "deaths";      // ticks -> "id|name|x|y|z|hasTomb"                  (wave 3/4)
        private const string TblGuardFlags = "guard_flags"; // id -> "rule|hits|lastTicks"                   (wave 7)

        // ZNet.ConnectionStatus.ErrorFull == 9 (ZNet.cs:23-38). Written as a plain int for the same reason
        // wave 1 does: a reordered/renamed enum in a future build must not throw inside a join handler.
        private const int ErrFull = 9;

        private const int SlotsCap = 30;            // AP_SlotsData ships at most this many queue entries
        private const int PackLineCap = 200;        // AP_ConfigPack ships at most this many lines (wire contract)
        private const int PackReadCap = 500;        // hard bound on an INBOUND pack before we call it malformed
        private const int PackValueLen = 300;       // clamp on a single exported/imported value
        private const int CsvRowCap = 10000;        // per-file row cap; a runaway table must not fill a disk
        private const int FrameSampleCount = 1024;  // ~17 s of frames on a 60 Hz headless server
        private const double QueueTtlMinutes = 10d; // queue entries older than this are dropped
        private const double HoldSeconds = 60d;     // how long a freed slot is reserved for the front of the queue
        private const int MaxSummaryLen = 800;      // clamp on any AP_Msg text we generate

        // Keys whose VALUE never leaves this server. Substring match, case-insensitive.
        private static readonly string[] SecretKeyParts = { "Webhook", "Url", "Token", "Password", "Secret" };
        private const string Redacted = "<redacted>";

        // ---- config ----
        private static ConfigEntry<bool> _enableReservedSlots;
        private static ConfigEntry<int> _reservedSlots;
        private static ConfigEntry<int> _maxPlayersOverride;
        private static ConfigEntry<bool> _enableMetricsExport;
        private static ConfigEntry<int> _metricsIntervalMinutes;

        private static bool ReserveOn => _enableReservedSlots != null && _enableReservedSlots.Value;
        private static bool MetricsOn => _enableMetricsExport != null && _enableMetricsExport.Value;
        private static int MetricsIntervalMinutes =>
            _metricsIntervalMinutes != null ? Mathf.Clamp(_metricsIntervalMinutes.Value, 1, 1440) : 15;

        // ---- join queue (in-memory by nature: a queue that survives a restart is a lie, because every
        // client reconnects from scratch after one anyway) ----
        private sealed class QueueEntry
        {
            public string Id;
            public long FirstTicks;   // first rejection, for logging
            public long LastTicks;    // last rejection, drives the 10-minute prune
        }

        private static readonly List<QueueEntry> Queue = new List<QueueEntry>();
        private static string _holdId;
        private static long _holdExpiresTicks;

        // ---- metrics sampling ----
        private static readonly float[] Frames = new float[FrameSampleCount];
        private static int _frameIdx;
        private static int _frameFilled;
        private static readonly DateTime StartedUtc = DateTime.UtcNow;

        private static readonly object MetricsGate = new object();
        private static bool _exporting;
        private static long _lastWriteTicks;      // UTC ticks of the last COMPLETED write (0 = never)
        private static string _lastWriteResult = "never written";

        private static float _nextQueueTick;
        private static float _nextMetricsTick;
        private static bool _metricsPrimed;
        private static int _znetLimit = int.MinValue;   // sentinel: engine limit not probed yet
        private static bool _inited;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableReservedSlots = cfg.Bind("Features", "EnableReservedSlots", false,
                    "Keep the last few player slots free for admins. OFF by default: with it off the server fills exactly as vanilla does. Requires the server's player cap to be readable (see MaxPlayersOverride).");
                _reservedSlots = cfg.Bind("Features", "ReservedSlots", 2,
                    "How many of the server's slots are admin-only when EnableReservedSlots is on. Clamped to at most (player cap - 1) so a mis-set value can never lock every non-admin out.");
                _maxPlayersOverride = cfg.Bind("Features", "MaxPlayersOverride", 0,
                    "0 = read the player cap from the game (ZNet). Set this only if another mod raised the cap above the vanilla 10 — reserved slots are computed from this number and would otherwise be wrong.");
                _enableMetricsExport = cfg.Bind("Features", "EnableMetricsExport", false,
                    "Periodically write metrics.prom (Prometheus text format) and export_<table>.csv into the companion's data folder for an EXTERNAL scraper to read. OFF by default. The companion never opens a network port.");
                _metricsIntervalMinutes = cfg.Bind("Features", "MetricsIntervalMinutes", 15,
                    "How often the metrics/CSV files are rewritten, in minutes (1-1440). Files are written on a background thread; only the snapshot is taken on the main thread.");
            }

            // Slots state is a read-only report; the config pack rewrites server settings and the metrics
            // summary leaks file paths, so both of those stay owner-only (null grant = no role gets them).
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSlotsReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvConfigPackReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvConfigPackApply", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMetricsReq", null);

            try
            {
                Harmony.CreateAndPatchAll(typeof(RpcRegisterPatch));
                Wave2Ops.ReportPatch("Wave6Slots.RpcRegisterPatch", true);
            }
            catch (Exception e)
            {
                Wave2Ops.ReportPatch("Wave6Slots.RpcRegisterPatch", false);
                CompanionPlugin.FeatureLog($"Wave6 RpcRegisterPatch failed (slots/config-pack/metrics RPCs unavailable): {e.Message}");
            }

            try
            {
                Harmony.CreateAndPatchAll(typeof(ReservedSlotsGatePatch));
                Wave2Ops.ReportPatch("Wave6Slots.ReservedSlotsGatePatch", true);
            }
            catch (Exception e)
            {
                Wave2Ops.ReportPatch("Wave6Slots.ReservedSlotsGatePatch", false);
                CompanionPlugin.FeatureLog($"Wave6 ReservedSlotsGatePatch failed (reserved slots / join queue unavailable): {e.Message}");
            }
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;

            // Frame sampling is our own, per-frame, and only while the exporter is enabled: with metrics off
            // this file costs one boolean test per frame.
            if (MetricsOn) SampleFrame(Time.unscaledDeltaTime);

            if (now >= _nextQueueTick)
            {
                _nextQueueTick = now + 1f;
                try { QueueTick(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Join-queue tick failed: {e.Message}"); }
            }

            if (!MetricsOn) { _metricsPrimed = false; return; }
            if (!_metricsPrimed)
            {
                // Give the world (and therefore FeatureStore.DataDir) time to resolve before the first write.
                _metricsPrimed = true;
                _nextMetricsTick = now + 30f;
                return;
            }
            if (now < _nextMetricsTick) return;
            _nextMetricsTick = now + MetricsIntervalMinutes * 60f;
            try { ExportMetrics(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Metrics export failed: {e.Message}"); }
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
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvSlotsReq", new Action<long>(OnSlotsReq));
                    ZRoutedRpc.instance.Register("AP_SrvConfigPackReq", new Action<long>(OnConfigPackReq));
                    ZRoutedRpc.instance.Register("AP_SrvMetricsReq", new Action<long>(OnMetricsReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvConfigPackApply", OnConfigPackApply);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave6 slots/config/metrics RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. reserved slots + join queue ====================

        /// <summary>
        /// Server player cap. 0 means UNKNOWN — the reserved-slots feature disables itself in that case
        /// rather than guessing, because a wrong cap either reserves nothing or locks everyone out.
        ///
        /// Vanilla's own full check is a hardcoded literal (`GetNrOfPlayers() >= 10`, ZNet.cs:901) and the
        /// only named constant is `public const int ServerPlayerLimit = 10` (ZNet.cs:119). A const compiled
        /// into our DLL would freeze at 10 forever, so it is read REFLECTIVELY from the loaded assembly —
        /// that way a game update that changes the constant is picked up, and one that renames it degrades
        /// to "unknown" instead of throwing.
        /// </summary>
        internal static int MaxPlayers()
        {
            var over = _maxPlayersOverride != null ? _maxPlayersOverride.Value : 0;
            if (over > 0) return Mathf.Min(over, 1024);
            if (_znetLimit != int.MinValue) return _znetLimit;

            _znetLimit = 0;
            try
            {
                var f = AccessTools.Field(typeof(ZNet), "ServerPlayerLimit")
                        ?? AccessTools.Field(typeof(ZNet), "m_serverPlayerLimit")
                        ?? AccessTools.Field(typeof(ZNet), "m_serverPlayerLimitOverride");
                if (f != null)
                {
                    var raw = f.IsStatic || f.IsLiteral ? f.GetValue(null) : (ZNet.instance != null ? f.GetValue(ZNet.instance) : null);
                    if (raw is int fi && fi > 0) _znetLimit = fi;
                }
                if (_znetLimit == 0)
                {
                    var p = AccessTools.Property(typeof(ZNet), "ServerPlayerLimit");
                    var raw = p != null ? p.GetValue(p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic ? null : ZNet.instance, null) : null;
                    if (raw is int pi && pi > 0) _znetLimit = pi;
                }
            }
            catch (Exception e)
            {
                _znetLimit = 0;
                CompanionPlugin.FeatureLog($"Player cap unreadable, reserved slots disabled ({e.Message}). Set MaxPlayersOverride to enable them anyway.");
            }
            if (_znetLimit == 0)
                CompanionPlugin.FeatureLog("Player cap unreadable (ZNet.ServerPlayerLimit missing): reserved slots stay disabled. Set MaxPlayersOverride to override.");
            return _znetLimit;
        }

        /// <summary>Configured reserve, clamped so at least one non-admin slot always survives.</summary>
        private static int ReservedCount(int max)
        {
            var want = _reservedSlots != null ? _reservedSlots.Value : 0;
            if (want <= 0 || max <= 1) return 0;
            return Mathf.Clamp(want, 0, max - 1);
        }

        /// <summary>
        /// Players currently occupying a slot. `GetNrOfPlayers()` (ZNet.cs:2252) is what vanilla compares
        /// against its own cap, but it reads `m_players`, a list only refreshed by the player-list timer —
        /// so the ready-peer count is taken as well and the LARGER of the two wins. Over-counting closes the
        /// reserve a moment early; under-counting would hand a reserved slot away, which is the failure that
        /// matters.
        /// </summary>
        internal static int OnlinePlayers()
        {
            var znet = ZNet.instance;
            if (znet == null) return 0;
            var listed = 0;
            try { listed = znet.GetNrOfPlayers(); }
            catch (Exception) { }
            var named = 0;
            try
            {
                foreach (var p in znet.GetPeers())
                    if (p != null && !string.IsNullOrEmpty(p.m_playerName)) named++;
            }
            catch (Exception) { }
            return Math.Max(listed, named);
        }

        /// <summary>
        /// Independent join gate. Wave 1 already prefixes ZNet.RPC_PeerInfo (temp-ban / lockdown); Harmony
        /// runs prefixes in priority order and STOPS at the first one that returns false, so this one is
        /// deliberately Priority.Low: a ban must be reported as a ban, not as "server full". The two patches
        /// share no state and either can fail to apply without affecting the other.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPriority(Priority.Low)]
        internal static class ReservedSlotsGatePatch
        {
            private static bool Prefix(ZNet __instance, ZRpc rpc)
            {
                try
                {
                    if (!ReserveOn) return true;
                    if (__instance == null || !__instance.IsServer() || rpc == null) return true;

                    var max = MaxPlayers();
                    if (max <= 0) return true;                 // cap unknown -> feature disabled, fail open
                    var reserved = ReservedCount(max);
                    if (reserved <= 0) return true;

                    ZNetPeer peer = null;
                    foreach (var p in __instance.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null || peer.m_socket == null) return true;

                    // Identity at prefix time is the socket host name ONLY: m_uid/m_playerName are assigned
                    // further down RPC_PeerInfo (ZNet.cs:931-933), so nothing else is readable yet.
                    var host = peer.m_socket.GetHostName();
                    if (string.IsNullOrEmpty(host)) return true;
                    if (CompanionPlugin.FeatureIsAdminId(host)) return true;   // the reserve exists for them

                    var online = OnlinePlayers();
                    if (online < max - reserved)
                    {
                        // Free capacity outside the reserved band: normal join, and they leave the queue.
                        if (HoldMatches(host)) ClearHold();
                        RemoveFromQueue(host);
                        return true;
                    }

                    if (HoldMatches(host))
                    {
                        CompanionPlugin.FeatureLog($"Reserved slots: admitting held queue entry {host}");
                        Wave1Moderation.NotifyOnlineAdmins($"Join queue: {host} took the slot held for them");
                        ClearHold();
                        RemoveFromQueue(host);
                        return true;
                    }

                    var pos = EnqueueAndPosition(host);
                    CompanionPlugin.FeatureLog($"Reserved slots: refused {host} ({online}/{max} online, {reserved} reserved) — queue position {pos}");
                    Wave1Moderation.NotifyOnlineAdmins($"Join queue: {host} was refused (server at {online}/{max}, {reserved} admin slots reserved) — position {pos}");
                    Reject(rpc, peer);
                    return false;
                }
                catch (Exception e)
                {
                    // Fail OPEN, always: reserved slots are a convenience, joinability is not.
                    CompanionPlugin.FeatureLog($"Reserved-slots gate error (connection allowed): {e.Message}");
                    return true;
                }
            }

            // Vanilla's own "server full" path (ZNet.cs:901-905) is Error 9 with no teardown; the explicit
            // Disconnect stops the socket lingering until the connect timeout. Reached reflectively so a
            // signature change degrades to "peer times out" instead of throwing inside the join handler.
            // The socket flushes its send queue on close, so the Error still reaches the client.
            private static void Reject(ZRpc rpc, ZNetPeer peer)
            {
                try { rpc.Invoke("Error", ErrFull); }
                catch (Exception) { }
                try
                {
                    var m = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
                    if (m != null) m.Invoke(ZNet.instance, new object[] { peer });
                }
                catch (Exception) { }
            }
        }

        // ---- queue bookkeeping ----

        private static int EnqueueAndPosition(string host)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            for (var i = 0; i < Queue.Count; i++)
            {
                if (!Wave1Moderation.IdMatches(Queue[i].Id, host)) continue;
                Queue[i].LastTicks = nowTicks;   // still waiting; keeps their place, refreshes the TTL
                return i + 1;
            }
            Queue.Add(new QueueEntry { Id = host, FirstTicks = nowTicks, LastTicks = nowTicks });
            return Queue.Count;
        }

        private static void RemoveFromQueue(string host)
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
                if (Wave1Moderation.IdMatches(Queue[i].Id, host)) Queue.RemoveAt(i);
        }

        private static bool HoldMatches(string host) =>
            _holdId != null && DateTime.UtcNow.Ticks < _holdExpiresTicks && Wave1Moderation.IdMatches(_holdId, host);

        private static void ClearHold()
        {
            _holdId = null;
            _holdExpiresTicks = 0;
        }

        // 1 Hz: prune stale entries, expire a hold nobody claimed, and hand the next free slot to the front
        // of the queue. Polling instead of a second Harmony patch on Disconnect — one less patched method.
        private static void QueueTick()
        {
            if (!ReserveOn)
            {
                if (Queue.Count > 0) Queue.Clear();
                ClearHold();
                return;
            }

            var nowTicks = DateTime.UtcNow.Ticks;
            var cutoff = DateTime.UtcNow.AddMinutes(-QueueTtlMinutes).Ticks;
            for (var i = Queue.Count - 1; i >= 0; i--)
            {
                if (Queue[i].LastTicks >= cutoff) continue;
                if (_holdId != null && Wave1Moderation.IdMatches(Queue[i].Id, _holdId)) ClearHold();
                Queue.RemoveAt(i);
            }

            if (_holdId != null && nowTicks >= _holdExpiresTicks)
            {
                var lost = _holdId;
                RemoveFromQueue(lost);
                ClearHold();
                CompanionPlugin.FeatureLog($"Join queue: hold expired for {lost}, slot released to the next in line");
            }

            var max = MaxPlayers();
            if (max <= 0) return;
            var reserved = ReservedCount(max);
            if (reserved <= 0) return;

            // Persist the effective reserve so the panel and an operator reading the data dir agree on it.
            var slots = FeatureStore.Table(TblSlots);
            var want = reserved.ToString(CultureInfo.InvariantCulture);
            if (!slots.TryGetValue("reserved", out var cur) || cur != want)
            {
                slots["reserved"] = want;
                FeatureStore.SaveTable(TblSlots);
            }

            if (_holdId != null || Queue.Count == 0) return;
            if (OnlinePlayers() >= max - reserved) return;   // still inside the reserved band, nothing freed

            _holdId = Queue[0].Id;
            _holdExpiresTicks = nowTicks + (long)(HoldSeconds * TimeSpan.TicksPerSecond);
            var waited = Math.Max(0, (int)((nowTicks - Queue[0].FirstTicks) / TimeSpan.TicksPerSecond));
            CompanionPlugin.FeatureLog($"Join queue: slot held {HoldSeconds:0} s for {_holdId} (waited {waited}s, queue length {Queue.Count})");
            Wave1Moderation.NotifyOnlineAdmins($"Join queue: a slot is held for {_holdId} for the next {HoldSeconds:0} seconds");
        }

        // ---- AP_SrvSlotsReq -> AP_SlotsData ----

        private static void OnSlotsReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSlotsReq")) return;

            var max = MaxPlayers();
            var reserved = max > 0 ? ReservedCount(max) : Mathf.Max(0, _reservedSlots != null ? _reservedSlots.Value : 0);

            var pkg = new ZPackage();
            pkg.Write(1);                                // payload version — bump, never reorder
            pkg.Write(ReserveOn && max > 0);             // false when the cap is unreadable: the feature is inert
            pkg.Write(reserved);
            pkg.Write(max);                              // 0 == unknown
            pkg.Write(OnlinePlayers());
            pkg.Write(Queue.Count);
            var shipped = Math.Min(Queue.Count, SlotsCap);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++)
            {
                pkg.Write(Queue[i].Id ?? "");
                pkg.Write(i + 1);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_SlotsData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SlotsData reply failed: {e.Message}"); }
        }

        // ==================== 2. config pack export / import ====================

        /// <summary>
        /// Snapshot of the live [Features] entries. Uses the lock-protected IDictionary.Values copy rather
        /// than enumerating the ConfigFile directly (another plugin binding a setting mid-enumeration would
        /// otherwise throw), and degrades to an empty list if this BepInEx build differs.
        /// </summary>
        private static List<ConfigEntryBase> FeatureEntries()
        {
            var res = new List<ConfigEntryBase>();
            try
            {
                var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
                if (cfg == null) return res;
                ICollection<ConfigEntryBase> all;
                try
                {
                    all = ((IDictionary<ConfigDefinition, ConfigEntryBase>)cfg).Values;
                }
                catch (Exception)
                {
                    // Older/newer ConfigFile shape: fall back to plain enumeration.
                    all = new List<ConfigEntryBase>();
                    foreach (var kv in cfg) all.Add(kv.Value);
                }
                foreach (var e in all)
                {
                    if (e == null || e.Definition == null) continue;
                    if (!string.Equals(e.Definition.Section, "Features", StringComparison.Ordinal)) continue;
                    res.Add(e);
                }
                res.Sort((a, b) => string.Compare(a.Definition.Key, b.Definition.Key, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Config pack: cannot enumerate config entries ({e.Message})");
            }
            return res;
        }

        private static bool IsSecretKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var part in SecretKeyParts)
                if (key.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string SerializeEntry(ConfigEntryBase e)
        {
            try
            {
                var v = e.GetSerializedValue() ?? "";
                v = v.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return v.Length > PackValueLen ? v.Substring(0, PackValueLen) : v;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Exported lines, "Features.&lt;Key&gt;=&lt;value&gt;", secrets redacted. Public so the RPC handler and
        /// any future console command share one implementation.
        /// </summary>
        internal static List<string> BuildConfigPack()
        {
            var lines = new List<string>();
            foreach (var e in FeatureEntries())
            {
                var key = e.Definition.Key;
                var val = IsSecretKey(key) ? Redacted : SerializeEntry(e);
                lines.Add("Features." + key + "=" + val);
                if (lines.Count >= PackLineCap) break;
            }
            return lines;
        }

        private static void OnConfigPackReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvConfigPackReq")) return;

            var lines = BuildConfigPack();
            var total = FeatureEntries().Count;
            if (total > lines.Count)
                CompanionPlugin.FeatureLog($"Config pack export truncated: {total} [Features] entries, {lines.Count} shipped (wire cap {PackLineCap}).");

            var pkg = new ZPackage();
            pkg.Write(1);                 // payload version
            pkg.Write(lines.Count);
            foreach (var l in lines) pkg.Write(l);

            CompanionPlugin.SrvAudit(sender, "CONFIGPACK-EXPORT", $"lines={lines.Count} total={total}");
            try { CompanionPlugin.ReplyTo(sender, "AP_ConfigPack", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_ConfigPack reply failed: {e.Message}"); }
        }

        private sealed class PackChange
        {
            public string Key;
            public string Before;
            public string After;
            public string Status;   // changed | would-change | unchanged | rejected:<why>
        }

        // ZPackage: int count, count x string line, bool dryRun.
        private static void OnConfigPackApply(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvConfigPackApply")) return;

            var lines = new List<string>();
            bool dryRun;
            try
            {
                var count = pkg.ReadInt();
                if (count < 0 || count > PackReadCap)
                {
                    CompanionPlugin.FeatureLog($"AP_SrvConfigPackApply: implausible line count {count} dropped");
                    CompanionPlugin.NotifySender(sender, "Config pack rejected: implausible line count.");
                    return;
                }
                for (var i = 0; i < count; i++) lines.Add(pkg.ReadString());
                dryRun = pkg.ReadBool();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvConfigPackApply: malformed packet dropped ({e.Message})");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var entries = FeatureEntries();
            var byKey = new Dictionary<string, ConfigEntryBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) byKey[e.Definition.Key] = e;

            var plan = new List<PackChange>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                if (plan.Count >= PackLineCap) break;
                var change = PlanLine(raw, byKey, seen);
                if (change != null) plan.Add(change);
            }

            // The audit trail is written BEFORE anything is applied on purpose: a pack is allowed to contain
            // EnableAuditLog=false, and the record of who turned it off must survive that.
            var planText = new StringBuilder();
            foreach (var c in plan)
            {
                if (planText.Length > 0) planText.Append("; ");
                planText.Append(c.Key).Append(':').Append(c.Before ?? "?").Append("->").Append(c.After ?? "?")
                        .Append('(').Append(c.Status).Append(')');
            }
            CompanionPlugin.SrvAudit(sender, dryRun ? "CONFIGPACK-DRYRUN" : "CONFIGPACK-APPLY",
                $"lines={lines.Count} planned={plan.Count} detail={planText}");
            Wave1AuditRpc.PostModLog($"CONFIGPACK {(dryRun ? "DRY RUN" : "APPLY")} by {admin}: {planText}");

            var changed = 0; var unchanged = 0; var rejected = 0;
            if (!dryRun)
            {
                // One .cfg write for the whole batch instead of one per setting (ConfigFile.SaveOnConfigSet
                // defaults to true and Save()s on every BoxedValue assignment).
                var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
                var restore = true;
                try
                {
                    if (cfg != null) { restore = cfg.SaveOnConfigSet; cfg.SaveOnConfigSet = false; }
                    foreach (var c in plan)
                    {
                        if (c.Status != "would-change") continue;
                        ConfigEntryBase entry;
                        if (!byKey.TryGetValue(c.Key, out entry)) { c.Status = "rejected:vanished"; continue; }
                        try
                        {
                            entry.BoxedValue = BepInEx.Configuration.TomlTypeConverter.ConvertToValue(c.After, entry.SettingType);
                            c.Status = "changed";
                        }
                        catch (Exception e)
                        {
                            // Per-entry failure never aborts the batch.
                            c.Status = "rejected:set-failed";
                            CompanionPlugin.FeatureLog($"Config pack: {c.Key} could not be set to '{c.After}' ({e.Message})");
                        }
                    }
                }
                finally
                {
                    if (cfg != null)
                    {
                        cfg.SaveOnConfigSet = restore;
                        try { cfg.Save(); }
                        catch (Exception e) { CompanionPlugin.FeatureLog($"Config pack: saving the config file failed ({e.Message})"); }
                    }
                }
            }

            foreach (var c in plan)
            {
                if (c.Status == "changed") changed++;
                else if (c.Status == "unchanged") unchanged++;
                else if (c.Status == "would-change") changed++;      // dry run: counted as "would change"
                else rejected++;
            }

            var sb = new StringBuilder();
            sb.Append(dryRun ? "Config pack DRY RUN: " : "Config pack applied: ")
              .Append(plan.Count).Append(" lines, ")
              .Append(changed).Append(dryRun ? " would change, " : " changed, ")
              .Append(unchanged).Append(" unchanged, ")
              .Append(rejected).Append(" rejected.");
            foreach (var c in plan)
            {
                if (c.Status == "unchanged" || sb.Length > MaxSummaryLen - 80) continue;
                sb.Append(' ').Append(c.Key).Append('=').Append(c.After).Append(" [").Append(c.Status).Append(']');
            }
            var summary = sb.ToString();
            if (summary.Length > MaxSummaryLen) summary = summary.Substring(0, MaxSummaryLen);

            CompanionPlugin.FeatureLog(summary + $" (by {admin})");
            if (!dryRun && changed > 0)
                Wave1Moderation.NotifyOnlineAdmins($"{admin} applied a config pack: {changed} setting(s) changed");
            CompanionPlugin.NotifySender(sender, summary);
        }

        /// <summary>
        /// Validates one "Features.Key=value" line against the live config. Returns null for blank/comment
        /// lines. Never mutates anything — the caller applies the plan.
        /// </summary>
        private static PackChange PlanLine(string raw, Dictionary<string, ConfigEntryBase> byKey, HashSet<string> seen)
        {
            if (raw == null) return null;
            var line = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (line.Length == 0 || line[0] == '#') return null;
            if (line.Length > PackValueLen + 128) line = line.Substring(0, PackValueLen + 128);

            var eq = line.IndexOf('=');
            if (eq <= 0) return new PackChange { Key = line, Before = "", After = "", Status = "rejected:no-value" };

            var left = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();

            // Section gate: anything not addressed to [Features] is refused outright. A config pack must
            // never be able to reach BepInEx core settings or another plugin's config.
            var dot = left.IndexOf('.');
            if (dot <= 0) return new PackChange { Key = left, Before = "", After = value, Status = "rejected:no-section" };
            var section = left.Substring(0, dot);
            var key = left.Substring(dot + 1).Trim();
            if (!string.Equals(section, "Features", StringComparison.OrdinalIgnoreCase))
                return new PackChange { Key = left, Before = "", After = value, Status = "rejected:not-features" };
            if (key.Length == 0)
                return new PackChange { Key = left, Before = "", After = value, Status = "rejected:no-key" };
            if (!seen.Add(key))
                return new PackChange { Key = key, Before = "", After = value, Status = "rejected:duplicate" };
            if (value.Length > PackValueLen) value = value.Substring(0, PackValueLen);

            // A redacted export must not be able to blank a webhook by round-tripping.
            if (string.Equals(value, Redacted, StringComparison.OrdinalIgnoreCase))
                return new PackChange { Key = key, Before = "", After = value, Status = "rejected:redacted" };

            ConfigEntryBase entry;
            if (!byKey.TryGetValue(key, out entry))
                return new PackChange { Key = key, Before = "", After = value, Status = "rejected:unknown-key" };

            var before = SerializeEntry(entry);
            if (string.Equals(before, value, StringComparison.Ordinal))
                return new PackChange { Key = key, Before = before, After = value, Status = "unchanged" };

            // Parse-check now so a dry run reports type errors instead of discovering them at apply time.
            try { BepInEx.Configuration.TomlTypeConverter.ConvertToValue(value, entry.SettingType); }
            catch (Exception)
            {
                return new PackChange { Key = key, Before = before, After = value, Status = "rejected:bad-value" };
            }
            return new PackChange { Key = key, Before = before, After = value, Status = "would-change" };
        }

        // ==================== 3. metrics + CSV export ====================

        private static void SampleFrame(float dt)
        {
            if (dt <= 0f || dt > 10f) return;   // editor pauses / load stalls are not frame times
            Frames[_frameIdx] = dt;
            _frameIdx = (_frameIdx + 1) % FrameSampleCount;
            if (_frameFilled < FrameSampleCount) _frameFilled++;
        }

        // Everything the writer thread needs, sampled on the main thread. No Unity API is touched off-thread.
        private sealed class Snapshot
        {
            public string Dir;
            public int Online;
            public int PeerConnections;
            public int Zdos;
            public double UptimeSeconds;
            public int Queued;
            public int Reserved;
            public int MaxPlayers;
            public float[] Frames;
            public List<KeyValuePair<string, List<KeyValuePair<string, string>>>> Tables;
        }

        /// <summary>
        /// Snapshot on the main thread, format + write on a worker (disk I/O must not stall the simulation).
        /// Returns a human-readable status for the RPC summary.
        /// </summary>
        internal static string ExportMetrics()
        {
            if (!FeatureStore.Ready) return "no world loaded yet: nothing written";
            lock (MetricsGate)
            {
                if (_exporting) return "a write is already in progress";
                _exporting = true;
            }

            Snapshot snap;
            try
            {
                snap = new Snapshot
                {
                    Dir = FeatureStore.DataDir,
                    Online = OnlinePlayers(),
                    Queued = Queue.Count,
                    MaxPlayers = MaxPlayers(),
                    UptimeSeconds = (DateTime.UtcNow - StartedUtc).TotalSeconds,
                    Frames = SnapshotFrames(),
                    Tables = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>(),
                };
                snap.Reserved = snap.MaxPlayers > 0 ? ReservedCount(snap.MaxPlayers) : 0;
                try { snap.PeerConnections = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0; }
                catch (Exception) { snap.PeerConnections = -1; }
                try { snap.Zdos = ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0; }
                catch (Exception) { snap.Zdos = -1; }

                foreach (var name in new[] { TblEco, TblPresence, TblDeaths, TblGuardFlags })
                {
                    List<KeyValuePair<string, string>> rows = null;
                    try
                    {
                        var t = FeatureStore.Table(name);
                        if (t.Count == 0) continue;
                        rows = new List<KeyValuePair<string, string>>(Math.Min(t.Count, CsvRowCap));
                        foreach (var kv in t)
                        {
                            rows.Add(kv);
                            if (rows.Count >= CsvRowCap) break;
                        }
                    }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Metrics: table '{name}' unreadable ({e.Message})"); }
                    if (rows != null && rows.Count > 0)
                        snap.Tables.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(name, rows));
                }
            }
            catch (Exception)
            {
                lock (MetricsGate) _exporting = false;
                throw;
            }

            if (string.IsNullOrEmpty(snap.Dir))
            {
                lock (MetricsGate) _exporting = false;
                return "data folder unavailable: nothing written";
            }

            // Same off-thread discipline as the Discord webhook: exceptions are swallowed into a status
            // string, never surfaced as an unhandled task exception.
            Task.Run(() =>
            {
                var status = "ok";
                try { status = WriteAll(snap); }
                catch (Exception e) { status = "failed: " + e.Message; }
                lock (MetricsGate)
                {
                    _lastWriteResult = status;
                    _lastWriteTicks = DateTime.UtcNow.Ticks;
                    _exporting = false;
                }
            });
            return "write started";
        }

        private static float[] SnapshotFrames()
        {
            var n = _frameFilled;
            var copy = new float[n];
            Array.Copy(Frames, copy, n);
            return copy;
        }

        private static string WriteAll(Snapshot s)
        {
            var files = 0;
            WriteFileAtomic(Path.Combine(s.Dir, "metrics.prom"), BuildProm(s));
            files++;
            foreach (var t in s.Tables)
            {
                WriteFileAtomic(Path.Combine(s.Dir, "export_" + t.Key + ".csv"), BuildCsv(t.Key, t.Value));
                files++;
            }
            return $"ok ({files} file(s))";
        }

        // Prometheus text exposition format. Every number is formatted with InvariantCulture on purpose: a
        // server running under a comma-decimal locale would otherwise emit "0,0166" and break every scraper.
        private static string BuildProm(Snapshot s)
        {
            float p50, p95, p99;
            Percentiles(s.Frames, out p50, out p95, out p99);
            var sb = new StringBuilder(2048);
            Gauge(sb, "valheim_admin_panel_up", "1 when the Advanced Admin Panel companion metrics exporter is running.", 1);
            Gauge(sb, "valheim_online_players", "Players currently occupying a slot on this server.", s.Online);
            Gauge(sb, "valheim_peer_connections", "Ready peer connections (-1 when unreadable).", s.PeerConnections);
            Gauge(sb, "valheim_max_players", "Server player cap (0 when the cap could not be read).", s.MaxPlayers);
            Gauge(sb, "valheim_reserved_slots", "Slots kept free for admins (0 when the feature is off).", s.Reserved);
            Gauge(sb, "valheim_join_queue_length", "Players refused by reserved slots and still waiting.", s.Queued);
            Gauge(sb, "valheim_zdo_count", "Networked objects in the world (-1 when unreadable).", s.Zdos);
            Gauge(sb, "valheim_uptime_seconds", "Seconds since the companion plugin started.", s.UptimeSeconds);

            sb.Append("# HELP valheim_frame_time_seconds Server frame time sampled by the companion.\n");
            sb.Append("# TYPE valheim_frame_time_seconds summary\n");
            sb.Append("valheim_frame_time_seconds{quantile=\"0.5\"} ").Append(Num(p50)).Append('\n');
            sb.Append("valheim_frame_time_seconds{quantile=\"0.95\"} ").Append(Num(p95)).Append('\n');
            sb.Append("valheim_frame_time_seconds{quantile=\"0.99\"} ").Append(Num(p99)).Append('\n');
            sb.Append("valheim_frame_time_seconds_count ").Append(Num(s.Frames.Length)).Append('\n');

            Gauge(sb, "valheim_metrics_written_timestamp_seconds", "Unix time this file was written.",
                (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            return sb.ToString();
        }

        private static void Gauge(StringBuilder sb, string name, string help, double value)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name).Append(' ').Append(Num(value)).Append('\n');
        }

        private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static void Percentiles(float[] samples, out float p50, out float p95, out float p99)
        {
            p50 = 0f; p95 = 0f; p99 = 0f;
            if (samples == null || samples.Length == 0) return;
            var sorted = (float[])samples.Clone();
            Array.Sort(sorted);
            p50 = Pick(sorted, 0.50d);
            p95 = Pick(sorted, 0.95d);
            p99 = Pick(sorted, 0.99d);
        }

        private static float Pick(float[] sorted, double q)
        {
            var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        // ---- CSV (RFC 4180: CRLF rows, quotes doubled inside quoted fields) ----

        private static string BuildCsv(string table, List<KeyValuePair<string, string>> rows)
        {
            var sb = new StringBuilder(rows.Count * 64 + 128);
            string[] header;
            int fields;
            if (table == TblEco) { header = new[] { "id", "balance", "lifetime_earned", "name" }; fields = 3; }
            else if (table == TblPresence) { header = new[] { "id", "first_seen_utc", "last_seen_utc", "sessions", "total_seconds", "last_name" }; fields = 5; }
            else if (table == TblDeaths) { header = new[] { "ticks_utc", "time_utc", "id", "name", "x", "y", "z", "has_tomb" }; fields = 6; }
            else if (table == TblGuardFlags) { header = new[] { "id", "rule", "hits", "last_utc" }; fields = 3; }
            else { header = new[] { "key", "value" }; fields = 1; }

            for (var i = 0; i < header.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Csv(header[i]));
            }
            sb.Append("\r\n");

            foreach (var kv in rows)
            {
                var parts = (kv.Value ?? "").Split(new[] { '|' }, fields);
                if (table == TblPresence)
                {
                    // id | first(ticks) | last(ticks) | sessions | totalSeconds | lastName
                    sb.Append(Csv(kv.Key)).Append(',').Append(Csv(Iso(Part(parts, 0)))).Append(',')
                      .Append(Csv(Iso(Part(parts, 1)))).Append(',').Append(Csv(Part(parts, 2))).Append(',')
                      .Append(Csv(Part(parts, 3))).Append(',').Append(Csv(Part(parts, 4)));
                }
                else if (table == TblDeaths)
                {
                    // key is the death time in ticks; value is id|name|x|y|z|hasTomb
                    sb.Append(Csv(kv.Key)).Append(',').Append(Csv(Iso(kv.Key))).Append(',')
                      .Append(Csv(Part(parts, 0))).Append(',').Append(Csv(Part(parts, 1))).Append(',')
                      .Append(Csv(Part(parts, 2))).Append(',').Append(Csv(Part(parts, 3))).Append(',')
                      .Append(Csv(Part(parts, 4))).Append(',').Append(Csv(Part(parts, 5)));
                }
                else if (table == TblGuardFlags)
                {
                    sb.Append(Csv(kv.Key)).Append(',').Append(Csv(Part(parts, 0))).Append(',')
                      .Append(Csv(Part(parts, 1))).Append(',').Append(Csv(Iso(Part(parts, 2))));
                }
                else
                {
                    sb.Append(Csv(kv.Key));
                    for (var i = 0; i < fields; i++) sb.Append(',').Append(Csv(Part(parts, i)));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private static string Part(string[] parts, int i) => parts != null && i < parts.Length ? parts[i] ?? "" : "";

        private static string Iso(string ticksRaw)
        {
            long ticks;
            if (!long.TryParse(ticksRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return ticksRaw ?? "";
            try { return new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }
            catch (Exception) { return ticksRaw; }
        }

        private static string Csv(string f)
        {
            if (string.IsNullOrEmpty(f)) return "";
            var needsQuotes = f.IndexOf(',') >= 0 || f.IndexOf('"') >= 0 || f.IndexOf('\n') >= 0 || f.IndexOf('\r') >= 0;
            if (!needsQuotes) return f;
            return "\"" + f.Replace("\"", "\"\"") + "\"";
        }

        // Same atomic discipline as FeatureStore.SaveTable: a scraper must never read a half-written file.
        private static void WriteFileAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }

        // ---- AP_SrvMetricsReq -> AP_Msg ----

        private static void OnMetricsReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMetricsReq")) return;

            var text = MetricsSummary();
            CompanionPlugin.SrvAudit(sender, "METRICS-REQ", text.Replace('|', '/'));
            CompanionPlugin.NotifySender(sender, text);
        }

        /// <summary>
        /// Human summary of the exporter: where the files are and when they were last written. Triggers a
        /// fresh write when the feature is on, so an admin pressing the button gets current files.
        /// </summary>
        internal static string MetricsSummary()
        {
            var dir = FeatureStore.DataDir ?? "(no world loaded)";
            if (!MetricsOn)
                return "Metrics export is OFF. Enable Features.EnableMetricsExport in the companion config; files are then written to " + dir;

            string kick;
            try { kick = ExportMetrics(); }
            catch (Exception e) { kick = "failed: " + e.Message; }

            long ticks; string result;
            lock (MetricsGate) { ticks = _lastWriteTicks; result = _lastWriteResult; }
            var last = ticks == 0
                ? "never"
                : new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

            var files = new StringBuilder("metrics.prom");
            foreach (var name in new[] { TblEco, TblPresence, TblDeaths, TblGuardFlags })
            {
                try { if (FeatureStore.Table(name).Count > 0) files.Append(", export_").Append(name).Append(".csv"); }
                catch (Exception) { }
            }

            var text = $"Metrics ON (every {MetricsIntervalMinutes} min). Files: {files} in {dir}. Last write: {last} ({result}). This request: {kick}. Files are for an external scraper — the companion opens no network port.";
            return text.Length > MaxSummaryLen ? text.Substring(0, MaxSummaryLen) : text;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 2 — server operations (scheduler / MOTD / logs / perf / self-test) ====================
    // Six capabilities that a server owner needs and that the panel had no server-side counterpart for:
    //
    //   1. Announcement scheduler  — up to 20 recurring "[Server] ..." lines, persisted in the "schedule" table.
    //   2. MOTD                    — one line pushed to every joining peer, 10 s AFTER the join handshake.
    //   3. Log capture             — bounded in-memory ring fed by BOTH Unity's log callback and a BepInEx listener.
    //   4. Perf sampling           — 300-frame frame-time ring, p50/p95/p99 + counters.
    //   5. Self-test               — a read-only checklist an owner can run from the panel.
    //   6. Health alerts           — passive alerts (frame time / disk / ZDO count) to online admins.
    //   7. World modifiers         — read/write of the world's global keys (the panel's "World Modifiers" view).
    //
    // Design rules obeyed (spec-companion.md §1-§9, spec-valheim-api.md §9):
    //  * A dedicated server has no GameObjects: no MessageHud, no Chat.instance, no Player.m_localPlayer.
    //    Everything player-facing goes out through Wave1Moderation.SendPlayerText ("ShowMessage"), which
    //    lands on UNMODDED clients too.
    //  * ONE Harmony class per target method; each applied in its own try/catch with a named warning so a
    //    game update degrades one capability instead of taking the server down.
    //  * Nothing the specs did not verify is touched directly: ZDOMan counters, ZNet.World, Game's autosave
    //    interval and CompanionPlugin's own private state are all reached through AccessTools with a
    //    documented fallback. A game update must degrade a check, never crash the server.
    //  * Behaviour-changing background work defaults OFF (an empty schedule / empty MOTD IS off).
    //    Health alerts default ON because they only *report* — they never change server behaviour.
    //
    // CROSS-MODULE COUPLING (deliberate, documented):
    //  * AP_SrvSchedReq must report restart + autosave state that is OWNED by the sibling backup/restart
    //    module (Wave2Backup). Rather than call into that class (load-order and build-order coupling), this
    //    file READS the shared "srvcfg" table keys directly: "restart_at", "restart_reason", "autosave_on",
    //    "autosave_min". Those keys are the contract; the sibling owns the writes, this file never touches
    //    them. Only "motd" is written here.
    //  * The self-test calls Wave2Backup.BackupHealth() / SaveHealth() REFLECTIVELY (AccessTools.TypeByName)
    //    for the same reason: if that module is absent or renames a method, the check degrades to "unknown"
    //    instead of failing the build or throwing inside an RPC handler. Expected shape:
    //        internal static string BackupHealth();   internal static string SaveHealth();
    //
    // WIRE NOTE — AP_SchedData ships EXACTLY 20 announcement entries, index == slot number (empty slots are
    // shipped as text="" everyMinutes=0 nextTicksUtc=0). The wire contract carries no slot id, so the only
    // unambiguous way for the panel to address a slot in AP_SrvAnnSet is positional. Do not "compact" the
    // list: deleting slot 1 would silently renumber every slot after it in the panel's next write.
    internal static class Wave2Ops
    {
        // ---- store tables (shared contract with the panel + sibling wave files) ----
        private const string TblSchedule = "schedule";   // slot -> "text|everyMinutes|lastFiredTicks"
        private const string TblSrvCfg = "srvcfg";       // "motd" (ours) + restart/autosave keys (sibling's)

        private const string KeyMotd = "motd";
        private const string KeyRestartAt = "restart_at";        // sibling-owned, read-only here
        private const string KeyRestartReason = "restart_reason"; // sibling-owned, read-only here
        private const string KeyAutosaveOn = "autosave_on";       // sibling-owned, read-only here
        private const string KeyAutosaveMin = "autosave_min";     // sibling-owned, read-only here

        // ---- caps (server-side and mandatory: an over-long reply is discarded WHOLE by the panel) ----
        private const int MaxSlots = 20;          // announcement slots 0..19
        private const int MaxAnnLen = 200;
        private const int MaxMotdLen = 200;
        private const int MaxAnnMinutes = 10080;  // one week; longer is indistinguishable from "off"
        private const int LogShipCap = 120;
        private const int LogLineCap = 400;
        private const int PerfRing = 300;
        private const int PerfShipCap = 60;
        private const int SelfTestCap = 30;
        private const int WorldModShipCap = 40;   // the panel REJECTS a longer AP_WorldModData outright
        private const int MaxWorldKeyLen = 96;

        // ---- health-alert thresholds ----
        private const float P95AlertMs = 200f;         // sustained frame time that means "the server is hurting"
        private const float P95SustainSeconds = 60f;
        private const long LowDiskBytes = 1L * 1024L * 1024L * 1024L;   // 1 GB
        private const long WarnDiskBytes = 5L * 1024L * 1024L * 1024L;  // 5 GB (self-test warn level only)
        private const float AlertThrottleSeconds = 900f;                // 15 min per condition

        // ---- config ----
        private static ConfigEntry<int> _logBufferLines;
        private static ConfigEntry<int> _motdDelaySeconds;
        private static ConfigEntry<bool> _healthAlerts;
        private static ConfigEntry<int> _healthZdoWarn;

        private static int LogBufferLines => _logBufferLines != null ? Mathf.Clamp(_logBufferLines.Value, 200, 2000) : 500;
        private static float MotdDelay => _motdDelaySeconds != null ? Mathf.Clamp(_motdDelaySeconds.Value, 2, 60) : 10f;
        private static bool HealthAlertsOn => _healthAlerts == null || _healthAlerts.Value;
        private static int HealthZdoWarn => _healthZdoWarn != null ? Mathf.Max(1000, _healthZdoWarn.Value) : 300000;

        private static bool _inited;

        // ---- tick throttles ----
        private static float _nextMotdTick;
        private static float _nextSlowTick;

        // ---- MOTD delivery queue: (uid, dueUnscaledTime) ----
        // A just-joined client has no MessageHud yet (wave-1 lesson: anything sent inside the RPC_PeerInfo
        // postfix is dropped on the floor), so delivery is deferred by MotdDelay seconds and driven from Tick.
        private static readonly List<KeyValuePair<long, float>> MotdQueue = new List<KeyValuePair<long, float>>();

        // ---- perf ring (main thread only: written from Tick, read from RPC handlers) ----
        private static readonly float[] PerfSamples = new float[PerfRing];
        private static int _perfHead;
        private static int _perfCount;

        // ---- log ring (written from ANY thread; every access takes LogGate) ----
        private static readonly object LogGate = new object();
        private static string[] _logRing;
        private static int _logHead;
        private static int _logCount;
        private static bool _unityHookOn;
        private static bool _bepHookOn;
        private static BepInExTap _bepTap;

        // Re-entrancy guard: our own FeatureLog calls travel back through the BepInEx listener. Appending
        // never logs, so this only matters if the append itself throws — but a logging loop on a live server
        // is unrecoverable, so the guard is cheap insurance.
        [ThreadStatic] private static bool _inCapture;

        // ---- patch health (any module may report; see ReportPatch) ----
        private static readonly Dictionary<string, bool> PatchReports = new Dictionary<string, bool>(StringComparer.Ordinal);

        // ---- health-alert state ----
        private static float _p95HighSince;
        private static readonly Dictionary<string, float> AlertLastSent = new Dictionary<string, float>(StringComparer.Ordinal);

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _logBufferLines = cfg.Bind("Features", "LogBufferLines", 500,
                    "How many server log lines to keep in memory for the panel's log tail (200-2000). Passive: the buffer is read-only and never written to disk by this feature.");
                _motdDelaySeconds = cfg.Bind("Features", "MotdDelaySeconds", 10,
                    "Seconds to wait after a player joins before sending the MOTD (2-60). A client has no message HUD for the first few seconds of a join, so an instant MOTD is silently lost.");
                _healthAlerts = cfg.Bind("Features", "EnableHealthAlerts", true,
                    "Alert online admins when the server is unhealthy (sustained frame time, low disk space, huge ZDO count). Alerts only: this changes no server behaviour.");
                _healthZdoWarn = cfg.Bind("Features", "HealthZdoWarn", 300000,
                    "ZDO count above which a health alert is raised. A large but healthy world can legitimately sit near this number; raise it rather than ignoring the alert.");
            }

            // Log capture is hooked before anything else so a failure inside the rest of Init is captured.
            try { HookLogSources(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Log capture hook failed (log tail unavailable): {e.Message}"); }

            // Audit-chokepoint registration. Deliberate split (wave-1 lesson: an audited RPC writes a line on
            // EVERY call, so putting a polled read behind it drowns audit.log):
            //   * AP_SrvAnnSet / AP_SrvMotdSet are state-changing server configuration -> audited, owner-only
            //     (null grant) when tiered roles are enforced, same tier as lockdown.
            //   * AP_SrvLogTailReq is an on-demand fetch a moderator legitimately needs -> "moderator" grant,
            //     and the per-fetch audit line is accepted (log lines carry ids and paths: reading them IS an
            //     admin action worth recording).
            //   * AP_SrvWorldModSet changes the world itself and, for a server option, does so PERMANENTLY
            //     (the key is mirrored into the .fwl) -> audited, owner-only.
            //   * AP_SrvSchedReq / AP_SrvPerfReq / AP_SrvSelfTestReq / AP_SrvWorldModReq are reads the panel
            //     refreshes on a timer. They are NOT registered: no audit spam, and with roles enforced they
            //     fall back to owner-only (SenderCanFeature denies un-granted actions for assigned roles).
            CompanionPlugin.RegisterAuditedRpc("AP_SrvAnnSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMotdSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWorldModSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLogTailReq", "moderator");

            ApplyPatch("Wave2OpsRpcRegistration", typeof(Wave2OpsRpcRegistration),
                "scheduler/MOTD/log/perf/self-test RPCs unavailable");
            ApplyPatch("Wave2MotdJoinPatch", typeof(Wave2MotdJoinPatch),
                "MOTD on join unavailable");
        }

        private static void ApplyPatch(string name, Type patchClass, string degradation)
        {
            try
            {
                Harmony.CreateAndPatchAll(patchClass);
                ReportPatch(name, true);
            }
            catch (Exception e)
            {
                ReportPatch(name, false);
                CompanionPlugin.FeatureLog($"{name} failed ({degradation}): {e.Message}");
            }
        }

        internal static void Tick()
        {
            if (!_inited) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Frame-time sampling must happen EVERY frame — it is the whole point of the metric.
            SamplePerf();

            var now = Time.unscaledTime;

            if (now >= _nextMotdTick)
            {
                _nextMotdTick = now + 1f;
                try { DeliverMotd(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"MOTD delivery failed: {e.Message}"); }
            }

            if (now >= _nextSlowTick)
            {
                _nextSlowTick = now + 5f;
                try { FireDueAnnouncement(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Announcement scheduler failed: {e.Message}"); }
                try { HealthSweep(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Health sweep failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// Patch-health reporting for the self-test. Any wave module MAY call this right after its
        /// Harmony.CreateAndPatchAll; a patch that never reports shows up as "not reported", never as broken.
        /// </summary>
        internal static void ReportPatch(string name, bool ok)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (PatchReports) PatchReports[name] = ok;
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave2OpsRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvAnnSet", OnAnnSet);
                    ZRoutedRpc.instance.Register<string>("AP_SrvMotdSet", OnMotdSet);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvLogTailReq", OnLogTailReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvWorldModSet", OnWorldModSet);
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvSchedReq", new Action<long>(OnSchedReq));
                    ZRoutedRpc.instance.Register("AP_SrvPerfReq", new Action<long>(OnPerfReq));
                    ZRoutedRpc.instance.Register("AP_SrvSelfTestReq", new Action<long>(OnSelfTestReq));
                    ZRoutedRpc.instance.Register("AP_SrvWorldModReq", new Action<long>(OnWorldModReq));
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave2 ops RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. announcement scheduler ====================

        // ZPackage: int slot, string text, int everyMinutes. everyMinutes <= 0 deletes the slot.
        private static void OnAnnSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvAnnSet")) return;

            int slot, every;
            string text;
            try
            {
                slot = pkg.ReadInt();
                text = CleanText(pkg.ReadString(), MaxAnnLen);
                every = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvAnnSet: malformed packet dropped ({e.Message})"); return; }

            if (slot < 0 || slot >= MaxSlots)
            {
                CompanionPlugin.NotifySender(sender, $"Announcement slot must be 0-{MaxSlots - 1}");
                return;
            }

            var table = FeatureStore.Table(TblSchedule);
            var key = slot.ToString();

            if (every <= 0 || text.Length == 0)
            {
                var had = table.Remove(key);
                FeatureStore.SaveTable(TblSchedule);
                CompanionPlugin.SrvAudit(sender, "ANN-DELETE", $"slot={slot} existed={had}");
                CompanionPlugin.FeatureLog($"Announcement slot {slot} cleared by {CompanionPlugin.SenderDisplayName(sender)}");
                CompanionPlugin.NotifySender(sender, $"Announcement slot {slot} cleared");
                return;
            }

            // Minimum interval is one minute: anything faster is indistinguishable from chat spam and would
            // hammer every connected client from a Tick that only runs every 5 s anyway.
            every = Mathf.Clamp(every, 1, MaxAnnMinutes);

            // Preserve lastFired when only the text changes, so editing a slot does not restart its cycle;
            // a brand-new slot starts its cycle NOW rather than firing on the next tick.
            long lastFired;
            string oldText;
            int oldEvery;
            if (!ParseSlot(table.TryGetValue(key, out var raw) ? raw : null, out oldText, out oldEvery, out lastFired) || lastFired <= 0)
                lastFired = DateTime.UtcNow.Ticks;

            table[key] = text + "|" + every + "|" + lastFired;
            FeatureStore.SaveTable(TblSchedule);

            CompanionPlugin.SrvAudit(sender, "ANN-SET", $"slot={slot} every={every}m text={text}");
            CompanionPlugin.FeatureLog($"Announcement slot {slot} set by {CompanionPlugin.SenderDisplayName(sender)}: every {every}m \"{text}\"");
            CompanionPlugin.NotifySender(sender, $"Announcement slot {slot} set (every {every} min)");
        }

        // Fires AT MOST ONE due slot per pass (the most overdue one). With the 5 s tick cadence that
        // naturally staggers a batch of simultaneously-due announcements instead of dumping five lines on
        // every player's screen in the same frame.
        private static void FireDueAnnouncement()
        {
            var table = FeatureStore.Table(TblSchedule);
            if (table.Count == 0) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            string dueKey = null, dueText = null;
            var dueEvery = 0;
            var worstOverdue = 0L;

            foreach (var kv in table)
            {
                string text;
                int every;
                long lastFired;
                if (!ParseSlot(kv.Value, out text, out every, out lastFired)) continue;
                if (every <= 0 || text.Length == 0) continue;

                var dueAt = lastFired + TimeSpan.FromMinutes(every).Ticks;
                if (nowTicks < dueAt) continue;
                var overdue = nowTicks - dueAt;
                if (dueKey != null && overdue <= worstOverdue) continue;
                worstOverdue = overdue;
                dueKey = kv.Key;
                dueText = text;
                dueEvery = every;
            }

            if (dueKey == null) return;

            // lastFired is advanced (and PERSISTED) whether or not anyone was online. Without this a server
            // that restarts after an idle night would find every slot overdue and fire the whole schedule at
            // the first player who walks in.
            table[dueKey] = dueText + "|" + dueEvery + "|" + nowTicks;
            FeatureStore.SaveTable(TblSchedule);

            var peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return;   // nobody to tell; the cycle still advanced

            var line = "[Server] " + dueText;
            foreach (var peer in peers)
            {
                if (peer == null || peer.m_uid == 0L) continue;
                Wave1Moderation.SendPlayerText(peer.m_uid, line);
            }
            CompanionPlugin.FeatureLog($"Announcement slot {dueKey} fired to {peers.Count} peer(s): {dueText}");
        }

        // ==================== 2. MOTD ====================

        private static void OnMotdSet(long sender, string rawText)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMotdSet")) return;

            var text = CleanText(rawText, MaxMotdLen);
            var cfgTable = FeatureStore.Table(TblSrvCfg);
            if (text.Length == 0) cfgTable.Remove(KeyMotd);
            else cfgTable[KeyMotd] = text;
            FeatureStore.SaveTable(TblSrvCfg);

            CompanionPlugin.SrvAudit(sender, "MOTD-SET", text.Length == 0 ? "cleared" : $"text={text}");
            CompanionPlugin.FeatureLog($"MOTD {(text.Length == 0 ? "cleared" : "set")} by {CompanionPlugin.SenderDisplayName(sender)}");
            CompanionPlugin.NotifySender(sender, text.Length == 0 ? "MOTD cleared (feature off)" : "MOTD updated");
        }

        private static string CurrentMotd()
        {
            var cfgTable = FeatureStore.Table(TblSrvCfg);
            return cfgTable.TryGetValue(KeyMotd, out var v) && !string.IsNullOrEmpty(v) ? v : "";
        }

        // POSTFIX on RPC_PeerInfo — by now m_uid/m_playerName exist (the vanilla reject ladder runs first).
        // Stacked alongside CompanionPlugin.PeerJoinLogPatch and Wave1's own postfixes: Harmony allows
        // several classes per target, the house rule is one TARGET per class.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave2MotdJoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!_inited || __instance == null || !__instance.IsServer() || rpc == null) return;
                try
                {
                    if (CurrentMotd().Length == 0) return;   // empty MOTD = feature off, queue nothing
                    foreach (var peer in __instance.GetPeers())
                    {
                        if (peer == null || peer.m_rpc != rpc) continue;
                        if (peer.m_uid == 0L) return;        // rejected connection: never got an identity
                        QueueMotd(peer.m_uid);
                        return;
                    }
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"MOTD join hook failed: {e.Message}"); }
            }
        }

        private static void QueueMotd(long uid)
        {
            for (var i = 0; i < MotdQueue.Count; i++)
                if (MotdQueue[i].Key == uid) return;   // already queued (double RPC_PeerInfo is possible)
            MotdQueue.Add(new KeyValuePair<long, float>(uid, Time.unscaledTime + MotdDelay));
        }

        private static void DeliverMotd(float now)
        {
            if (MotdQueue.Count == 0) return;
            for (var i = MotdQueue.Count - 1; i >= 0; i--)
            {
                var entry = MotdQueue[i];
                if (now < entry.Value) continue;
                MotdQueue.RemoveAt(i);

                // Read the MOTD at DELIVERY time: an admin who clears it during the 10 s window means it.
                var text = CurrentMotd();
                if (text.Length == 0) continue;
                if (!PeerConnected(entry.Key)) continue;   // left during the delay
                Wave1Moderation.SendPlayerText(entry.Key, text);
            }
        }

        // ==================== 3. log capture ====================

        private static void HookLogSources()
        {
            lock (LogGate)
            {
                if (_logRing == null) { _logRing = new string[LogBufferLines]; _logHead = 0; _logCount = 0; }
            }

            // Source A — Unity/ZLog. The handler may be invoked off the main thread, so it may only touch the
            // lock-protected ring (spec-valheim-api.md §9.2). Subtract-then-add makes a plugin reload
            // idempotent: we can never end up subscribed twice, and there is no OnDestroy to unhook from
            // (this file may not edit CompanionPlugin).
            try
            {
                Application.logMessageReceived -= OnUnityLog;
                Application.logMessageReceived += OnUnityLog;
                _unityHookOn = true;
            }
            catch (Exception e)
            {
                _unityHookOn = false;
                CompanionPlugin.FeatureLog($"Unity log hook unavailable: {e.Message}");
            }

            // Source B — BepInEx 5.4: BepInEx.Logging.Logger.Listeners is ICollection<ILogListener>, and
            // ILogListener is { void LogEvent(object, LogEventArgs); } : IDisposable (verified against the
            // referenced BepInEx.dll). This is the only source that sees OTHER plugins' Logger output.
            try
            {
                var listeners = BepInEx.Logging.Logger.Listeners;
                if (listeners == null) throw new InvalidOperationException("Logger.Listeners is null");

                // Idempotent on reload: a stale listener from a previously loaded copy of this assembly is a
                // DIFFERENT Type object with the SAME full name, so match on the name and drop it.
                var mine = typeof(BepInExTap).FullName;
                List<ILogListener> stale = null;
                foreach (var l in listeners)
                {
                    if (l == null || l.GetType().FullName != mine) continue;
                    (stale ?? (stale = new List<ILogListener>())).Add(l);
                }
                if (stale != null)
                    foreach (var l in stale)
                    {
                        try { listeners.Remove(l); } catch (Exception) { }
                        try { l.Dispose(); } catch (Exception) { }
                    }

                _bepTap = new BepInExTap();
                listeners.Add(_bepTap);
                _bepHookOn = true;
            }
            catch (Exception e)
            {
                _bepHookOn = false;
                CompanionPlugin.FeatureLog($"BepInEx log listener unavailable (only engine output is captured): {e.Message}");
            }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            CaptureLine(UnityLevel(type), null, condition);
        }

        private static string UnityLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "Error";
                case LogType.Assert: return "Assert";
                case LogType.Warning: return "Warning";
                case LogType.Exception: return "Exception";
                default: return "Info";
            }
        }

        // The BepInEx tap. Kept harmless on reload: Dispose only flips a flag, and a stale instance left in
        // the Listeners collection by a failed reload writes into ITS OWN dead assembly's ring, not ours.
        private sealed class BepInExTap : ILogListener
        {
            private bool _disposed;

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (_disposed || eventArgs == null) return;
                try
                {
                    var source = eventArgs.Source != null ? eventArgs.Source.SourceName : null;
                    // BepInEx pipes Unity's own log through a "Unity Log" source; capturing it here as well
                    // would duplicate every engine line already taken by Application.logMessageReceived.
                    if (source == "Unity Log") return;
                    CaptureLine(eventArgs.Level.ToString(), source, eventArgs.Data != null ? eventArgs.Data.ToString() : "");
                }
                catch (Exception) { /* a logging tap must never throw into the logger */ }
            }

            public void Dispose() { _disposed = true; }
        }

        private static void CaptureLine(string level, string source, string message)
        {
            if (_inCapture) return;
            _inCapture = true;
            try
            {
                if (message == null) message = "";
                message = message.Replace('\r', ' ').Replace('\n', ' ');
                var line = DateTime.UtcNow.ToString("HH:mm:ss") + " [" + (level ?? "Info") + "] "
                           + (string.IsNullOrEmpty(source) ? "" : "[" + source + "] ") + message;
                if (line.Length > LogLineCap) line = line.Substring(0, LogLineCap);

                lock (LogGate)
                {
                    if (_logRing == null || _logRing.Length == 0) return;
                    _logRing[_logHead] = line;
                    _logHead = (_logHead + 1) % _logRing.Length;
                    if (_logCount < _logRing.Length) _logCount++;
                }
            }
            catch (Exception) { }
            finally { _inCapture = false; }
        }

        // Oldest -> newest.
        private static List<string> LogSnapshot()
        {
            lock (LogGate)
            {
                if (_logRing == null || _logCount == 0) return new List<string>();
                var res = new List<string>(_logCount);
                var start = (_logHead - _logCount + _logRing.Length) % _logRing.Length;
                for (var i = 0; i < _logCount; i++)
                {
                    var s = _logRing[(start + i) % _logRing.Length];
                    if (s != null) res.Add(s);
                }
                return res;
            }
        }

        // ZPackage: int maxLines, string filter.
        // Reply AP_LogTail: int ver=1, int total (matching lines held), int shipped(<=120), shipped x string.
        private static void OnLogTailReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLogTailReq")) return;

            int maxLines;
            string filter;
            try
            {
                maxLines = pkg.ReadInt();
                filter = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvLogTailReq: malformed packet dropped ({e.Message})"); return; }

            maxLines = Mathf.Clamp(maxLines, 1, LogShipCap);
            filter = filter != null ? filter.Trim() : "";
            if (filter.Length > 64) filter = filter.Substring(0, 64);

            var all = LogSnapshot();
            List<string> matched;
            if (filter.Length == 0) matched = all;
            else
            {
                matched = new List<string>();
                foreach (var line in all)
                    if (line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) matched.Add(line);
            }

            var start = Math.Max(0, matched.Count - maxLines);   // newest-N trim, join-log style
            var shipped = matched.Count - start;

            var reply = new ZPackage();
            reply.Write(1);                 // payload version — bump, never reorder
            reply.Write(matched.Count);     // total matching lines held
            reply.Write(shipped);
            for (var i = start; i < matched.Count; i++) reply.Write(matched[i]);

            // ReplyTo, not InvokeRoutedRPC: on a listen-server host the requesting admin is this process and
            // the routed packet would be discarded by the panel's anti-spoof gate (no server peer to verify).
            try { CompanionPlugin.ReplyTo(sender, "AP_LogTail", reply); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_LogTail reply failed: {e.Message}"); }
        }

        // ==================== 4. perf sampling ====================

        private static void SamplePerf()
        {
            var ms = Time.unscaledDeltaTime * 1000f;
            if (ms < 0f || float.IsNaN(ms) || float.IsInfinity(ms)) return;
            PerfSamples[_perfHead] = ms;
            _perfHead = (_perfHead + 1) % PerfRing;
            if (_perfCount < PerfRing) _perfCount++;
        }

        // Sorted copy of the ring (oldest ordering is irrelevant for percentiles).
        private static float[] SortedSamples()
        {
            var n = _perfCount;
            var copy = new float[n];
            var start = (_perfHead - n + PerfRing) % PerfRing;
            for (var i = 0; i < n; i++) copy[i] = PerfSamples[(start + i) % PerfRing];
            Array.Sort(copy);
            return copy;
        }

        private static float Percentile(float[] sorted, float p)
        {
            if (sorted == null || sorted.Length == 0) return 0f;
            var idx = Mathf.Clamp(Mathf.RoundToInt(p * (sorted.Length - 1)), 0, sorted.Length - 1);
            return sorted[idx];
        }

        private static float AvgFps()
        {
            if (_perfCount == 0) return 0f;
            var total = 0f;
            var start = (_perfHead - _perfCount + PerfRing) % PerfRing;
            for (var i = 0; i < _perfCount; i++) total += PerfSamples[(start + i) % PerfRing];
            var avgMs = total / _perfCount;
            return avgMs <= 0.0001f ? 0f : 1000f / avgMs;
        }

        // Reply AP_PerfData per the wire contract.
        private static void OnPerfReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvPerfReq")) return;

            var sorted = SortedSamples();
            var shipped = Math.Min(PerfShipCap, _perfCount);

            var pkg = new ZPackage();
            pkg.Write(1);                       // payload version
            pkg.Write(AvgFps());
            pkg.Write(Percentile(sorted, 0.50f));
            pkg.Write(Percentile(sorted, 0.95f));
            pkg.Write(Percentile(sorted, 0.99f));
            pkg.Write(_perfCount);
            pkg.Write(shipped);
            // Newest `shipped` samples, oldest-first, for the panel's sparkline.
            var start = (_perfHead - shipped + PerfRing) % PerfRing;
            for (var i = 0; i < shipped; i++) pkg.Write(PerfSamples[(start + i) % PerfRing]);

            pkg.Write((long)Mathf.Max(0f, Time.realtimeSinceStartup));   // process uptime, seconds
            pkg.Write(ZdoCount());
            pkg.Write(PeerCount());
            long gc;
            try { gc = GC.GetTotalMemory(false); } catch (Exception) { gc = 0L; }
            pkg.Write(gc);
            pkg.Write(SendQueue());

            try { CompanionPlugin.ReplyTo(sender, "AP_PerfData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_PerfData reply failed: {e.Message}"); }
        }

        // ==================== 5. self-test ====================

        private struct Check
        {
            public string Name;
            public int Status;   // 0 ok, 1 warn, 2 fail
            public string Detail;
        }

        private static void OnSelfTestReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSelfTestReq")) return;

            var checks = new List<Check>();
            // Every check is individually try/catch'd: one broken probe must not blank the whole report.
            Add(checks, "companion version", CheckVersion);
            Add(checks, "sender sanitizer", CheckSanitizer);
            Add(checks, "feature store", CheckStore);
            Add(checks, "adminlist", CheckAdminList);
            Add(checks, "world files", CheckWorldFiles);
            Add(checks, "disk space", CheckDisk);
            Add(checks, "last save", CheckLastSave);
            Add(checks, "backup health", CheckBackupHealth);
            Add(checks, "save health", CheckSaveHealth);
            Add(checks, "rpc registration", CheckRpcs);
            Add(checks, "patch health", CheckPatches);
            Add(checks, "peers", CheckPeers);
            Add(checks, "server frame time", CheckFrameTime);
            Add(checks, "log capture", CheckLogCapture);
            Add(checks, "audit log", CheckAudit);
            Add(checks, "scheduler", CheckScheduler);

            if (checks.Count > SelfTestCap) checks.RemoveRange(SelfTestCap, checks.Count - SelfTestCap);

            var pkg = new ZPackage();
            pkg.Write(1);                  // payload version
            pkg.Write(checks.Count);
            foreach (var c in checks)
            {
                pkg.Write(c.Name ?? "?");
                pkg.Write(c.Status);
                pkg.Write(Clip(c.Detail ?? "", 200));
            }

            CompanionPlugin.SrvAudit(sender, "SELFTEST", $"checks={checks.Count}");
            try { CompanionPlugin.ReplyTo(sender, "AP_SelfTest", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SelfTest reply failed: {e.Message}"); }
        }

        private static void Add(List<Check> list, string name, Func<Check> probe)
        {
            if (list.Count >= SelfTestCap) return;
            try
            {
                var c = probe();
                c.Name = name;
                list.Add(c);
            }
            catch (Exception e)
            {
                list.Add(new Check { Name = name, Status = 1, Detail = "check itself failed: " + e.Message });
            }
        }

        private static Check Ok(string d) => new Check { Status = 0, Detail = d };
        private static Check Warn(string d) => new Check { Status = 1, Detail = d };
        private static Check Fail(string d) => new Check { Status = 2, Detail = d };

        // The companion cannot know the panel's version — it reports its own and the panel compares.
        private static Check CheckVersion() =>
            Ok($"AdminPanelCompanion {CompanionPlugin.PluginVersion} (panel compares against its own)");

        private static Check CheckSanitizer()
        {
            var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
            if (f == null) return Warn("unknown: SenderSanitizerActive not readable on this build");
            var v = f.GetValue(null);
            if (!(v is bool b)) return Warn("unknown: unexpected field type");
            return b
                ? Ok("active: routed-RPC sender ids are re-stamped server-side")
                : Fail("INACTIVE: sender ids are client-supplied — admin gating is forgeable, restart the server and check the log");
        }

        private static Check CheckStore()
        {
            if (!FeatureStore.Ready) return Fail("not ready: no world resolved, nothing persists");
            var dir = FeatureStore.DataDir;
            if (string.IsNullOrEmpty(dir)) return Fail("not ready: data directory unresolved");
            // Real writability probe: create and delete a file. A cached table would hide a read-only dir.
            var probe = Path.Combine(dir, "__selftest.tmp");
            try
            {
                File.WriteAllText(probe, DateTime.UtcNow.Ticks.ToString());
                File.Delete(probe);
                return Ok("writable: " + dir);
            }
            catch (Exception e)
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { }
                return Fail("NOT writable (" + e.Message + "): " + dir);
            }
        }

        private static Check CheckAdminList()
        {
            var count = SyncedListCount("m_adminList");
            if (count < 0) return Warn("unknown: adminlist not readable on this build");
            if (count == 0) return Warn("0 entries: only the host can administer this server");
            return Ok(count + " entr" + (count == 1 ? "y" : "ies"));
        }

        private static Check CheckWorldFiles()
        {
            var db = WorldPath(false);
            var fwl = WorldPath(true);
            var source = WorldFileSource();
            if (string.IsNullOrEmpty(db)) return Warn("unknown: ZNet.World not readable on this build");

            var cloud = !string.IsNullOrEmpty(source) && source.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0;
            var dbOk = false;
            var fwlOk = false;
            try { dbOk = File.Exists(db); fwlOk = !string.IsNullOrEmpty(fwl) && File.Exists(fwl); } catch (Exception) { }

            if (cloud)
                return Warn($"file source {source}: cloud saves cannot be file-copied — backups are unavailable");
            if (!dbOk || !fwlOk)
                return Warn($"file source {source ?? "?"}, .db={(dbOk ? "present" : "missing")} .fwl={(fwlOk ? "present" : "missing")} (a world that has never been saved reports missing)");
            return Ok($"file source {source}, .db + .fwl present ({db})");
        }

        private static Check CheckDisk()
        {
            string label;
            var free = FreeDiskBytes(out label);
            if (free < 0) return Warn("unknown: free space not readable for the world drive");
            var gb = free / 1024d / 1024d / 1024d;
            var detail = $"{gb:0.0} GB free on {label}";
            if (free < LowDiskBytes) return Fail("LOW: " + detail + " — a save can fail and corrupt the world");
            if (free < WarnDiskBytes) return Warn(detail);
            return Ok(detail);
        }

        private static Check CheckLastSave()
        {
            var ticks = LastSaveTicksUtc();
            var intervalMin = AutosaveIntervalMinutes();
            if (ticks <= 0)
                return Warn($"no save observed since the companion loaded (autosave interval {intervalMin} min)");
            var ageMin = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes;
            var detail = $"{ageMin:0} min ago (autosave interval {intervalMin} min)";
            if (intervalMin > 0 && ageMin > intervalMin * 2) return Fail("STALE: " + detail);
            if (intervalMin > 0 && ageMin > intervalMin * 1.25) return Warn(detail);
            return Ok(detail);
        }

        private static Check CheckBackupHealth() => SiblingHealthCheck("BackupHealth", "backup module");

        private static Check CheckSaveHealth() => SiblingHealthCheck("SaveHealth", "save module");

        // Reflective call into the sibling backup module (see the CROSS-MODULE COUPLING note at the top).
        private static Check SiblingHealthCheck(string method, string what)
        {
            bool ok;
            string text;
            if (!SiblingHealth(method, out ok, out text) || string.IsNullOrEmpty(text))
                return Warn($"unknown: {what} did not expose a usable {method}()");
            if (ok) return Ok(text);
            // Not-ok: "an operator must look". Fail only for wording that means broken; a merely disabled or
            // never-yet-run subsystem is a warning, not a failure.
            if (text.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0)
                return Fail(text);
            return Warn(text);
        }

        // Accepts either shape the sibling may expose, so a refactor there degrades this check instead of
        // breaking the build: `string Health()` or `(bool ok, string detail) Health()` (read as Item1/Item2
        // so no ValueTuple type reference is baked in).
        private static bool SiblingHealth(string method, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            try
            {
                var t = AccessTools.TypeByName("AdminPanelCompanion.Wave2Backup");
                if (t == null) return false;
                var m = AccessTools.Method(t, method, new Type[0]);
                if (m == null || !m.IsStatic) return false;
                var result = m.Invoke(null, null);
                if (result == null) return false;

                if (result is string s)
                {
                    detail = s;
                    ok = s.IndexOf("fail", StringComparison.OrdinalIgnoreCase) < 0 &&
                         s.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;
                    return true;
                }

                var f1 = AccessTools.Field(result.GetType(), "Item1");
                var f2 = AccessTools.Field(result.GetType(), "Item2");
                if (f1 != null && f2 != null && f1.GetValue(result) is bool b)
                {
                    ok = b;
                    detail = f2.GetValue(result) as string ?? "";
                    return true;
                }

                detail = result.ToString();
                return true;
            }
            catch (Exception) { return false; }
        }

        // Are our RPC names actually in ZRoutedRpc's dispatch table? This is the check that would have
        // caught every "the button does nothing" report in one click.
        private static Check CheckRpcs()
        {
            var names = new[]
            {
                "AP_SrvAnnSet", "AP_SrvMotdSet", "AP_SrvSchedReq", "AP_SrvLogTailReq",
                "AP_SrvPerfReq", "AP_SrvSelfTestReq", "AP_SrvWorldModReq", "AP_SrvWorldModSet",
                "AP_SrvKick", "AP_SrvBan", "AP_SrvInfoReq", "AP_SrvVersion",
            };
            if (ZRoutedRpc.instance == null) return Warn("unknown: ZRoutedRpc not up yet");
            var f = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
            var dict = f != null ? f.GetValue(ZRoutedRpc.instance) as IDictionary : null;
            if (dict == null) return Warn("unknown: ZRoutedRpc.m_functions not readable on this build");

            List<string> missing = null;
            foreach (var n in names)
            {
                bool present;
                try { present = dict.Contains(n.GetStableHashCode()); }
                catch (Exception) { return Warn("unknown: dispatch table not probeable"); }
                if (!present) (missing ?? (missing = new List<string>())).Add(n);
            }
            if (missing == null) return Ok($"{names.Length}/{names.Length} probed RPCs registered");
            return Fail($"missing: {string.Join(", ", missing.ToArray())}");
        }

        private static Check CheckPatches()
        {
            KeyValuePair<string, bool>[] snapshot;
            lock (PatchReports)
            {
                snapshot = new KeyValuePair<string, bool>[PatchReports.Count];
                var i = 0;
                foreach (var kv in PatchReports) snapshot[i++] = kv;
            }
            if (snapshot.Length == 0) return Warn("not reported: no module called ReportPatch");
            List<string> broken = null;
            foreach (var kv in snapshot)
                if (!kv.Value) (broken ?? (broken = new List<string>())).Add(kv.Key);
            if (broken == null) return Ok($"{snapshot.Length} patch class(es) applied");
            return Fail($"{broken.Count}/{snapshot.Length} failed: {string.Join(", ", broken.ToArray())}");
        }

        private static Check CheckPeers()
        {
            var peers = PeerCount();
            var limit = ServerPlayerLimit();
            if (limit <= 0) return Ok($"{peers} connected (server limit unknown on this build)");
            var detail = $"{peers}/{limit} connected";
            if (peers >= limit) return Warn(detail + " — server is full");
            return Ok(detail);
        }

        private static Check CheckFrameTime()
        {
            if (_perfCount == 0) return Warn("no samples yet");
            var sorted = SortedSamples();
            var p95 = Percentile(sorted, 0.95f);
            var detail = $"avg {AvgFps():0.0} fps, p50 {Percentile(sorted, 0.50f):0.0} ms, p95 {p95:0.0} ms, p99 {Percentile(sorted, 0.99f):0.0} ms";
            if (p95 >= P95AlertMs) return Fail("SLOW: " + detail);
            if (p95 >= P95AlertMs / 2f) return Warn(detail);
            return Ok(detail);
        }

        private static Check CheckLogCapture()
        {
            int held;
            lock (LogGate) held = _logCount;
            var detail = $"{held} line(s) held (buffer {LogBufferLines}); engine={(_unityHookOn ? "on" : "OFF")}, plugins={(_bepHookOn ? "on" : "OFF")}";
            if (!_unityHookOn && !_bepHookOn) return Fail("no log source hooked — " + detail);
            if (!_unityHookOn || !_bepHookOn) return Warn(detail);
            return Ok(detail);
        }

        private static Check CheckAudit()
        {
            var on = CompanionPlugin.AuditEnabledCfg != null && CompanionPlugin.AuditEnabledCfg.Value;
            var roles = CompanionPlugin.RolesEnabledCfg != null && CompanionPlugin.RolesEnabledCfg.Value;
            var detail = $"audit log {(on ? "on" : "OFF")}, tiered roles {(roles ? "enforced" : "off")}";
            return on ? Ok(detail) : Warn(detail + " — admin actions are not being recorded");
        }

        private static Check CheckScheduler()
        {
            var active = 0;
            foreach (var kv in FeatureStore.Table(TblSchedule))
            {
                string text;
                int every;
                long last;
                if (ParseSlot(kv.Value, out text, out every, out last) && every > 0 && text.Length > 0) active++;
            }
            var motd = CurrentMotd();
            return Ok($"{active}/{MaxSlots} announcement slot(s) active, MOTD {(motd.Length == 0 ? "off" : "set")}");
        }

        // ==================== 6. health alerts (report-only) ====================

        private static void HealthSweep(float now)
        {
            if (!HealthAlertsOn) return;

            // (a) sustained frame time. A single slow frame means nothing; P95SustainSeconds of a bad p95 does.
            if (_perfCount >= 30)
            {
                var p95 = Percentile(SortedSamples(), 0.95f);
                if (p95 >= P95AlertMs)
                {
                    if (_p95HighSince <= 0f) _p95HighSince = now;
                    else if (now - _p95HighSince >= P95SustainSeconds)
                    {
                        _p95HighSince = now;   // re-arm the window; the throttle governs repeats
                        RaiseAlert("frametime",
                            $"Server health: frame time p95 is {p95:0} ms (sustained over {P95SustainSeconds:0} s). Players will feel rubber-banding.");
                    }
                }
                else _p95HighSince = 0f;
            }

            // (b) free disk on the world drive.
            string label;
            var free = FreeDiskBytes(out label);
            if (free >= 0 && free < LowDiskBytes)
                RaiseAlert("disk",
                    $"Server health: only {free / 1024d / 1024d / 1024d:0.0} GB free on {label}. A world save can fail at this level.");

            // (c) ZDO count.
            var zdos = ZdoCount();
            if (zdos > HealthZdoWarn)
                RaiseAlert("zdos",
                    $"Server health: {zdos} ZDOs in the world (threshold {HealthZdoWarn}). Consider the world cleanup tools.");
        }

        private static void RaiseAlert(string condition, string text)
        {
            var now = Time.unscaledTime;
            float last;
            if (AlertLastSent.TryGetValue(condition, out last) && now - last < AlertThrottleSeconds) return;
            AlertLastSent[condition] = now;

            CompanionPlugin.FeatureLog(text);
            try { Wave1Moderation.NotifyOnlineAdmins(text); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Health alert notify failed: {e.Message}"); }
            try { Wave1AuditRpc.PostModLog(text); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Health alert mod-log failed: {e.Message}"); }
        }

        // ==================== AP_SrvSchedReq -> AP_SchedData ====================

        private static void OnSchedReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSchedReq")) return;

            var sched = FeatureStore.Table(TblSchedule);
            var cfgTable = FeatureStore.Table(TblSrvCfg);
            var nowTicks = DateTime.UtcNow.Ticks;

            // ---- restart block: OWNED by the sibling backup/restart module, read straight from "srvcfg" ----
            long restartAt = 0;
            if (cfgTable.TryGetValue(KeyRestartAt, out var restartRaw)) long.TryParse(restartRaw, out restartAt);
            var restartPending = restartAt > nowTicks;
            var restartReason = cfgTable.TryGetValue(KeyRestartReason, out var rr) ? rr : "";

            var pkg = new ZPackage();
            pkg.Write(1);                                  // payload version — bump, never reorder
            pkg.Write(restartPending);
            pkg.Write(restartPending ? restartAt : 0L);
            pkg.Write(restartReason ?? "");

            // ---- announcement slots: ALWAYS MaxSlots entries, index == slot (see the WIRE NOTE up top) ----
            pkg.Write(MaxSlots);
            for (var slot = 0; slot < MaxSlots; slot++)
            {
                string text;
                int every;
                long lastFired;
                if (!ParseSlot(sched.TryGetValue(slot.ToString(), out var raw) ? raw : null, out text, out every, out lastFired)
                    || every <= 0 || text.Length == 0)
                {
                    pkg.Write("");
                    pkg.Write(0);
                    pkg.Write(0L);
                    continue;
                }
                pkg.Write(text);
                pkg.Write(every);
                pkg.Write(lastFired + TimeSpan.FromMinutes(every).Ticks);
            }

            pkg.Write(CurrentMotd());

            // ---- autosave block: also sibling-owned in "srvcfg" ----
            var autosaveOn = cfgTable.TryGetValue(KeyAutosaveOn, out var ao) && ao == "1";
            int autosaveMin;
            if (!cfgTable.TryGetValue(KeyAutosaveMin, out var am) || !int.TryParse(am, out autosaveMin) || autosaveMin <= 0)
                autosaveMin = AutosaveIntervalMinutes();   // no override stored: report the live engine value
            pkg.Write(autosaveOn);
            pkg.Write(autosaveMin);

            try { CompanionPlugin.ReplyTo(sender, "AP_SchedData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SchedData reply failed: {e.Message}"); }
        }

        // ==================== 7. world modifiers ====================

        // World modifiers ARE global keys in this build. ZoneSystem stores them as one line per key,
        // "<lowercase name>" or "<lowercase name> <value>" (ZoneSystem.GlobalKeyAdd), and every member of the
        // GlobalKeys enum BEFORE GlobalKeys.NonServerOption is a *server option*: GlobalKeyAdd/GlobalKeyRemove
        // mirror exactly those into ZNet.World.m_startingGlobalKeys, which is what the .fwl persists. That
        // enum position is therefore the only correct source for the panel's permanent-vs-session split, and
        // it is read reflectively so a game update that reorders or drops the enum degrades the split instead
        // of throwing inside an RPC handler.
        private static HashSet<string> _worldOptionNames;      // enum members before NonServerOption
        private static Dictionary<string, string> _worldKeyCase;   // lowercase name -> canonical enum casing
        private static bool _worldKeysProbed;

        private static void ProbeWorldKeys()
        {
            if (_worldKeysProbed) return;
            _worldKeysProbed = true;
            _worldOptionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _worldKeyCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var t = AccessTools.TypeByName("GlobalKeys");
                if (t == null || !t.IsEnum)
                {
                    CompanionPlugin.FeatureLog("Wave2: the GlobalKeys enum could not be read; world modifiers are listed without the permanent/session split.");
                    return;
                }
                var names = Enum.GetNames(t);
                var boundary = Array.IndexOf(names, "NonServerOption");
                for (var i = 0; i < names.Length; i++)
                {
                    _worldKeyCase[names[i]] = names[i];
                    if (boundary > 0 && i < boundary) _worldOptionNames.Add(names[i]);
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave2: GlobalKeys probe failed ({e.Message}); world modifiers degrade to session-only rows.");
            }
        }

        private static bool IsWorldOption(string name) =>
            _worldOptionNames != null && _worldOptionNames.Contains(name);

        private static string CanonicalWorldKey(string name) =>
            _worldKeyCase != null && _worldKeyCase.TryGetValue(name, out var c) ? c : name;

        // Splits a stored key line into name + value. The value half must be preserved verbatim in what we
        // ship: a scalar modifier ("playerdamage 0.5") can only be re-applied by sending the SAME line back,
        // and the panel replays exactly the string it was given.
        private static string WorldKeyName(string line, out string value)
        {
            value = "";
            if (string.IsNullOrEmpty(line)) return "";
            var sp = line.IndexOf(' ');
            if (sp <= 0) return line.Trim();
            value = line.Substring(sp + 1).Trim();
            return line.Substring(0, sp);
        }

        private static List<string> CurrentWorldKeys()
        {
            try
            {
                var zs = ZoneSystem.instance;
                return zs != null ? zs.GetGlobalKeys() : null;
            }
            catch (Exception) { return null; }
        }

        private static bool WorldKeyPresent(string name)
        {
            var keys = CurrentWorldKeys();
            if (keys == null) return false;
            foreach (var line in keys)
            {
                string v;
                if (string.Equals(WorldKeyName(line, out v), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Same mechanism the seasonal toggles use: ZoneSystem.SetGlobalKey/RemoveGlobalKey send the vanilla
        // "SetGlobalKey"/"RemoveGlobalKey" routed RPC, which on the server dispatches locally into
        // RPC_SetGlobalKey — the ONLY path that also mirrors a server option into
        // ZNet.World.m_startingGlobalKeys and pushes the new set to every client. Touching m_globalKeys
        // directly would change the session and lose the change on restart. Duplicated rather than shared
        // because Wave6Events.SetGlobalKey is private to that file.
        private static bool SetWorldKey(string line, bool on)
        {
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || ZRoutedRpc.instance == null) return false;
                if (on) zs.SetGlobalKey(line);
                else zs.RemoveGlobalKey(line);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave2: world modifier '{line}' could not be changed: {e.Message}");
                return false;
            }
        }

        // Reply AP_WorldModData: int ver=1, int shipped(<=40), shipped x (string key, bool isServerOption),
        // string presetName. The panel discards a reply it cannot parse WHOLE, so the cap and the field order
        // are mandatory. A world with no ZoneSystem yet answers with an empty list rather than not at all —
        // silence is exactly what made this view look dead.
        private static void OnWorldModReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWorldModReq")) return;

            ProbeWorldKeys();

            var options = new List<string>();
            var others = new List<string>();
            var preset = "";
            var keys = CurrentWorldKeys();
            if (keys != null)
            {
                foreach (var line in keys)
                {
                    string value;
                    var name = WorldKeyName(line, out value);
                    if (name.Length == 0) continue;
                    // "Preset" is the world's difficulty preset, not a switch: it has its own wire field.
                    if (string.Equals(name, "Preset", StringComparison.OrdinalIgnoreCase))
                    {
                        preset = Clip(value, 64);
                        continue;
                    }
                    var display = Clip(CanonicalWorldKey(name) + (value.Length > 0 ? " " + value : ""), MaxWorldKeyLen);
                    if (IsWorldOption(name)) options.Add(display);
                    else others.Add(display);
                }
            }

            // Server options first: they are the permanent world changes this view exists for, so on a world
            // carrying a long tail of progression keys they must be the rows that survive the cap.
            options.Sort(StringComparer.OrdinalIgnoreCase);
            others.Sort(StringComparer.OrdinalIgnoreCase);

            var pkg = new ZPackage();
            pkg.Write(1);                 // payload version — bump, never reorder
            var shipped = Math.Min(WorldModShipCap, options.Count + others.Count);
            pkg.Write(shipped);
            var written = 0;
            for (var i = 0; i < options.Count && written < shipped; i++, written++)
            {
                pkg.Write(options[i]);
                pkg.Write(true);
            }
            for (var i = 0; i < others.Count && written < shipped; i++, written++)
            {
                pkg.Write(others[i]);
                pkg.Write(false);
            }
            pkg.Write(preset);

            try { CompanionPlugin.ReplyTo(sender, "AP_WorldModData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_WorldModData reply failed: {e.Message}"); }
        }

        // ZPackage: string key, bool on.
        private static void OnWorldModSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWorldModSet")) return;

            string raw;
            bool on;
            try
            {
                raw = CleanText(pkg.ReadString(), MaxWorldKeyLen);
                on = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvWorldModSet: malformed packet dropped ({e.Message})"); return; }

            if (raw.Length == 0) { CompanionPlugin.NotifySender(sender, "No world modifier key given."); return; }

            string value;
            var name = WorldKeyName(raw, out value);
            ProbeWorldKeys();

            // A key that is neither defined by this build nor currently set on the world would be written into
            // the world permanently with no way to discover it again, so it is refused. If the enum probe
            // itself failed we cannot classify anything and must not block the feature on that.
            var defined = _worldKeyCase == null || _worldKeyCase.Count == 0 || _worldKeyCase.ContainsKey(name);
            if (!defined && !WorldKeyPresent(name))
            {
                CompanionPlugin.NotifySender(sender, $"'{name}' is not a world modifier this build defines and is not set on this world.");
                return;
            }

            var permanent = IsWorldOption(name);
            if (!SetWorldKey(raw, on))
            {
                CompanionPlugin.NotifySender(sender, "The global-key system is not available on this server right now.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "WORLDMOD-SET", $"key={raw} state={(on ? "ON" : "OFF")} permanent={permanent}");
            CompanionPlugin.FeatureLog($"World modifier '{raw}' turned {(on ? "ON" : "OFF")} by {admin}{(permanent ? " (permanent: mirrored into the world's .fwl)" : "")}");
            Wave1AuditRpc.PostModLog($"WORLDMOD {raw} {(on ? "ON" : "OFF")} (by {admin})");
            CompanionPlugin.NotifySender(sender, permanent
                ? $"{raw} is now {(on ? "ON" : "OFF")} — a permanent world change, it survives a restart."
                : $"{raw} is now {(on ? "ON" : "OFF")}.");
        }

        // ==================== engine access (all reflective, all degrading) ====================

        // ZDOMan.NrOfObjects() is public and verified (ZDOMan.cs:1203) but reached reflectively so a game
        // update that removes it degrades this metric instead of throwing inside an RPC handler. Fallback is
        // the m_objectsByID count the Server tab already uses.
        private static MethodInfo _miNrOfObjects;
        private static MethodInfo _miClientChangeQueue;
        private static FieldInfo _fiObjectsById;
        private static bool _zdoProbed;

        private static void ProbeZdoMan()
        {
            if (_zdoProbed) return;
            _zdoProbed = true;
            try
            {
                _miNrOfObjects = AccessTools.Method(typeof(ZDOMan), "NrOfObjects", new Type[0]);
                _miClientChangeQueue = AccessTools.Method(typeof(ZDOMan), "GetClientChangeQueue", new Type[0]);
                _fiObjectsById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            }
            catch (Exception) { }
        }

        private static int ZdoCount()
        {
            try
            {
                if (ZDOMan.instance == null) return 0;
                ProbeZdoMan();
                if (_miNrOfObjects != null)
                {
                    var v = _miNrOfObjects.Invoke(ZDOMan.instance, null);
                    if (v is int n) return n;
                }
                if (_fiObjectsById != null && _fiObjectsById.GetValue(ZDOMan.instance) is ICollection c) return c.Count;
            }
            catch (Exception) { }
            return 0;
        }

        // ZDOMan.GetClientChangeQueue() (ZDOMan.cs:1218) is the closest thing to a send-queue depth the
        // engine exposes. Ships 0 when the method is gone rather than failing the whole reply.
        private static float SendQueue()
        {
            try
            {
                if (ZDOMan.instance == null) return 0f;
                ProbeZdoMan();
                if (_miClientChangeQueue == null) return 0f;
                var v = _miClientChangeQueue.Invoke(ZDOMan.instance, null);
                if (v is int n) return n;
            }
            catch (Exception) { }
            return 0f;
        }

        private static int PeerCount()
        {
            try
            {
                var peers = ZNet.instance != null ? ZNet.instance.GetPeers() : null;
                return peers != null ? peers.Count : 0;
            }
            catch (Exception) { return 0; }
        }

        // ZNet.ServerPlayerLimit is a const (ZNet.cs:119) — read as a raw constant so an update that turns it
        // into a field or removes it degrades to "unknown" instead of a type-load error.
        private static int ServerPlayerLimit()
        {
            try
            {
                var f = AccessTools.Field(typeof(ZNet), "ServerPlayerLimit");
                if (f == null) return 0;
                var v = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
                if (v is int n) return n;
            }
            catch (Exception) { }
            return 0;
        }

        private static bool PeerConnected(long uid)
        {
            try
            {
                if (ZNet.instance == null) return false;
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_uid == uid) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static int SyncedListCount(string fieldName)
        {
            try
            {
                var list = AccessTools.Field(typeof(ZNet), fieldName)?.GetValue(ZNet.instance);
                if (list == null) return -1;
                if (AccessTools.Method(list.GetType(), "GetList")?.Invoke(list, null) is ICollection c) return c.Count;
                var countProp = AccessTools.Property(list.GetType(), "Count");
                if (countProp != null && countProp.GetValue(list, null) is int n) return n;
            }
            catch (Exception) { }
            return -1;
        }

        // ZNet.World is a STATIC property (ZNet.cs:257) backed by the static m_world field; both are probed.
        private static object CurrentWorld()
        {
            try
            {
                return AccessTools.Property(typeof(ZNet), "World")?.GetValue(null, null)
                       ?? AccessTools.Field(typeof(ZNet), "m_world")?.GetValue(null);
            }
            catch (Exception) { return null; }
        }

        // GetDBPath()/GetMetaPath() both have a FileSource overload, so the no-arg form is selected explicitly.
        private static string WorldPath(bool meta)
        {
            try
            {
                var w = CurrentWorld();
                if (w == null) return null;
                var m = AccessTools.Method(w.GetType(), meta ? "GetMetaPath" : "GetDBPath", new Type[0]);
                return m?.Invoke(w, null) as string;
            }
            catch (Exception) { return null; }
        }

        private static string WorldFileSource()
        {
            try
            {
                var w = CurrentWorld();
                if (w == null) return null;
                var v = AccessTools.Field(w.GetType(), "m_fileSource")?.GetValue(w);
                return v?.ToString();
            }
            catch (Exception) { return null; }
        }

        private static long FreeDiskBytes(out string label)
        {
            label = "?";
            try
            {
                var path = WorldPath(false);
                if (string.IsNullOrEmpty(path)) path = FeatureStore.DataDir;
                if (string.IsNullOrEmpty(path)) return -1;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;
                label = root;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception) { return -1; }
        }

        // CompanionPlugin._lastSaveTicksUtc is private and written from the save worker thread; read it the
        // same way the owner does (Interlocked) after reflecting the field.
        private static long LastSaveTicksUtc()
        {
            try
            {
                var f = AccessTools.Field(typeof(CompanionPlugin), "_lastSaveTicksUtc");
                if (f == null) return 0;
                var v = f.GetValue(null);
                return v is long l ? l : 0;
            }
            catch (Exception) { return 0; }
        }

        // Game.m_saveInterval is a static float (Game.cs:126, default 1800).
        private static int AutosaveIntervalMinutes()
        {
            try
            {
                var f = AccessTools.Field(typeof(Game), "m_saveInterval");
                if (f?.GetValue(null) is float s && s > 0f) return Mathf.Max(1, Mathf.RoundToInt(s / 60f));
            }
            catch (Exception) { }
            return 0;
        }

        // ==================== shared helpers ====================

        // '|' is the field separator inside stored values, so it can never survive in free text; newlines
        // would corrupt the append-only logs and the key=value store alike.
        private static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static string Clip(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) : s);

        // "text|everyMinutes|lastFiredTicks". Text is sanitized on write, so a 3-way split is unambiguous.
        private static bool ParseSlot(string value, out string text, out int everyMinutes, out long lastFired)
        {
            text = ""; everyMinutes = 0; lastFired = 0;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split(new[] { '|' }, 3);
            text = parts[0];
            if (parts.Length > 1) int.TryParse(parts[1], out everyMinutes);
            if (parts.Length > 2) long.TryParse(parts[2], out lastFired);
            return true;
        }
    }
}

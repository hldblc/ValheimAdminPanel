using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 2 — backups, staged restore, restarts, autosave, save integrity ====
    // Everything in this file touches the one thing a server owner cannot re-create: the world save.
    //
    // THE 1.0.12 SAVE LAYOUT (decompile-1.0.12; FeatureStore's "World save set" section carries the line-level
    // citations and the scanner that turns a directory into a loadable file set):
    //  * A world lives in a CHUNKED DIRECTORY <worlds root>/<name>/ (World.GetSaveDirectory, World.cs:91) holding
    //    ONE save generation N as _main.N.fwl2 + _main.N.db2 + _main.N.chunks + _main.N.ok plus the per-chunk
    //    "<hh>_<ll>__<size>_<version>.chunk" files that the .chunks index names. ZNet.SaveWorldThread
    //    (ZNet.cs:1801-1912) writes generation N+1 as NEW files (FileWriter.cs:64 File.Create, no rename), writes
    //    _main.N+1.ok LAST and only on success (:1877-1879), then deletes generation N's four files and the
    //    superseded chunk versions (:1880-1884). A rewritten chunk always lands under a bumped version, i.e. a
    //    new file name (ChunkSaveMapping.CreateOrUpdate).
    //  * The legacy <worlds root>/<name>.db + <name>.fwl pair is what a pre-chunked world still is until its first
    //    1.0.12 save, which migrates it into the directory and renames the pair to <name>_backup_<stamp>.*
    //    (SaveSystem.CheckMove :623-658 -> MoveToBackup :735). World.IsChunkedSave() reports the layout the world
    //    was LOADED from (World.cs:316/386), so FeatureStore.ResolveSaveSet lets the directory on disk win.
    //  * The engine's own backups and restores copy the WHOLE directory filtered to {.fwl2 .db2 .chunks .ok .chunk}
    //    (SaveSystem.CopyDirectory :371-379, s_saveFileExtensions :70) and a restore renames the current directory
    //    to <name>_backup_restore-<stamp> (SaveSystem.RestoreBackup :587-620). The engine enumerates ONE directory
    //    level under the worlds root (FileHelpers.GetFiles :589-600), so a set two levels down is invisible to it.
    //
    // The rules that shape every line below (spec-valheim-api.md §5, spec-companion.md §5.5/§8):
    //  * NEVER copy while ZNet.IsSaving() — the save thread is rewriting the directory (new generation, then the
    //    deletions of the old one). The only safe window is after a completed save.
    //  * The completion signal is a postfix on ZNet.SaveWorldThread; ZNet.WorldSaveFinished fires from
    //    PrintWorldSaveMessage which dereferences MessageHud.instance FIRST — null on headless, so on the
    //    exact machine this targets it may never fire (ZNet.cs:1784-1798).
    //  * That postfix runs on the SAVE WORKER THREAD: it may only set atomics. Every path resolution
    //    (World.GetSaveDirectory -> SaveSystem.GetWorldsSaveRootPath -> Utils.GetSaveDataPath) is done on the
    //    main thread from Tick through FeatureStore's cached resolver, never in the postfix.
    //  * A BACKUP SET is a loadable snapshot and is listed, pruned, staged and restored only as a whole:
    //      chunked world: the newest complete _main.N quartet plus every .chunk file of the directory (the engine's
    //                     own copy semantics) as <backupRoot>/<world>/<stamp>/ with an apbackup.txt manifest;
    //      legacy world:  the .db + .fwl pair as <backupRoot>/<world>-<stamp>.db/.fwl, exactly as before.
    //    Copies land in a ".tmp" sibling (directory or files) and are moved into place only after the copy has
    //    been verified complete, so a half-written set can never appear in a listing.
    //  * The byte copy runs on a WORKER THREAD (a mature world is hundreds of megabytes; copying it inline froze
    //    the simulation for seconds). Everything Unity/ZNet-backed (the set, the backup directory, the "no save
    //    is in flight" decision) is resolved on the main thread BEFORE the worker starts; the worker does pure
    //    System.IO (FeatureStore.ScanChunkedSet is pure IO by contract) and publishes its result through a
    //    volatile flag that Tick picks up. Retention pruning is file IO too and runs in the same worker.
    //  * A running server cannot swap its own live save, so "restore" is staged (a marker file naming the set)
    //    and applied at the next process start before the world loads: the set is copied to a sibling of the
    //    live directory and verified, the live directory is moved aside as <name>_backup_restore-<stamp> (the
    //    engine's own convention, never deleted), the copy is moved in and verified again before
    //    "RESTORE APPLIED" is declared. A legacy world keeps the file-pair swap. Any definitive failure consumes
    //    the marker and is logged/posted honestly: a restore must never apply unannounced on a later boot.
    //  * Behaviour-changing background work is OFF by default: EnableAutoBackup (writes to disk) and
    //    EnableScheduledRestart (terminates the process) both default false.
    internal static class Wave2Backup
    {
        // ---- store (shared "srvcfg" table; sibling wave files own other keys in it) ----
        private const string TblCfg = "srvcfg";
        private const string KeyAutoBackupOn = "autobackup_on";
        private const string KeyBackupInterval = "backup_interval";
        private const string KeyBackupKeep = "backup_keep";
        private const string KeyRestartAt = "restart_at";
        private const string KeyRestartReason = "restart_reason";
        private const string KeyAutosaveOn = "autosave_on";
        private const string KeyAutosaveMin = "autosave_min";
        private const string KeyRestoreStaged = "restore_staged";   // "<backupName>|<stagedTicksUtc>"

        // ---- wire caps (contract: AP_BackupData ships at most 30 sets, newest first) ----
        private const int BackupShipCap = 30;
        private const int MaxNameLen = 128;
        private const int MaxReasonLen = 200;
        private const int MaxRestartMinutes = 10080;   // one week; longer is a calendar, not a countdown

        // ---- on-disk names ----
        private const string BackupRootName = "adminpanel_backups";
        private const string ManifestName = "apbackup.txt";     // written LAST into a chunked set: "this set is complete"
        private const string TmpSuffix = ".tmp";                // half-copied set (directory or file); never listed
        private const string StampFormat = "yyyyMMdd-HHmmss";   // SaveSystem.s_defaultDateFormat (SaveSystem.cs:68)

        // ---- tuning ----
        private const double FreeSpaceFactor = 3d;      // refuse a backup under 3x the set size free
        private const float SaveWaitSeconds = 300f;     // give up waiting for a save to finish
        private const long SlowSaveMs = 20000;          // integrity warning threshold
        private const double ShrinkRatio = 0.75d;       // set smaller than 75% of the previous one
        private const double WarnThrottleMinutes = 10d;
        private const int AutosaveMinMinutes = 5;
        private const int AutosaveMaxMinutes = 120;
        private static readonly int[] RestartWarnMinutes = { 30, 15, 10, 5, 2, 1 };

        // ---- config ----
        private static ConfigEntry<bool> _enableAutoBackup;
        private static ConfigEntry<int> _backupIntervalMinutes;
        private static ConfigEntry<int> _backupKeep;
        private static ConfigEntry<string> _backupDir;
        private static ConfigEntry<bool> _enableScheduledRestart;

        internal static bool AutoBackupOn => _enableAutoBackup != null && _enableAutoBackup.Value;
        private static int BackupIntervalMinutes =>
            _backupIntervalMinutes != null ? Mathf.Max(10, _backupIntervalMinutes.Value) : 60;
        private static int BackupKeep => _backupKeep != null ? Mathf.Max(1, _backupKeep.Value) : 10;
        private static bool ScheduledRestartOn => _enableScheduledRestart != null && _enableScheduledRestart.Value;

        // ---- backup state machine (main thread only) ----
        private enum Phase { Idle, WaitingForSave, Copying }

        private static Phase _phase;
        private static long _backupRequester;      // uid to push AP_BackupData to when the copy lands (0 = auto)
        private static float _phaseDeadline;
        private static long _lastBackupTicksUtc;   // completed backup, UTC ticks (0 = none this session)
        private static string _lastError = "";
        private static bool _backupSeeded;         // last-backup time recovered from disk once per world

        // ---- copy worker handoff. _copyDone is the ONLY thing the worker publishes with; every other field
        // below is written before it and read only after it, so the volatile write orders them. ----
        private static volatile bool _copyBusy;    // a worker owns the backup directory: never start a second one
        private static volatile bool _copyDone;    // worker -> main thread: the result fields are readable
        private static bool _copyOk;
        private static string _copyError = "";
        private static string _copyModLog = "";
        private static List<string> _copyLog;      // what the worker would have logged; emitted from Tick

        // Built on the main thread, then owned by the worker alone: plain strings, ints and a list of paths.
        private sealed class CopyJob
        {
            public bool Chunked;
            public string WorldName;
            public int Generation;        // chunked: the _main.N generation the set was resolved to
            public List<string> Files;    // absolute source paths (chunked: quartet then chunks; legacy: db, fwl)
            public string SetParent;      // chunked: <backupRoot>/<world>; legacy: <backupRoot>
            public string Name;           // chunked: the stamp directory; legacy: the "<world>-<stamp>" file stem
            public string StagedName;     // the set staged for restore is never pruned ("" = none)
            public int Keep;
        }

        // ---- save observation. The two volatiles below are written on the SAVE WORKER THREAD. ----
        private static long _saveThreadStartTicks;       // Interlocked
        private static long _saveCompletions;            // Interlocked counter
        private static long _lastSaveDurationMs;         // Interlocked
        private static long _processedCompletions;       // main-thread cursor into _saveCompletions
        private static volatile bool _saveSignal;        // "a save finished since we started waiting"

        private static long _lastSaveEndTicksUtc;
        private static long _lastSetBytes;               // size of the complete save set after the last save
        private static long _prevSetBytes;
        private static int _lastSetGeneration = -1;      // chunked: _main.N after the last save (-1 legacy/unknown)
        private static string _saveWarning = "";
        private static long _saveWarningTicks;            // warnings expire so health recovers on its own
        private const double WarnExpiryMinutes = 30d;
        private static readonly Dictionary<string, long> WarnThrottle = new Dictionary<string, long>(StringComparer.Ordinal);

        // ---- restart state ----
        private enum RestartPhase { None, Counting, Saving, Quitting }

        private static RestartPhase _restartPhase;
        private static long _armedRestartTicks;          // schedule the fired-set was built for
        private static readonly HashSet<int> FiredWarnings = new HashSet<int>();
        private static float _saveWaitUntil;
        private static float _quitAt;

        // ---- autosave override ----
        private static float _defaultSaveIntervalSeconds = -1f;
        private static bool _autosaveApplied;

        // ---- staged restore ----
        private static string _restoreApplied;           // set by Init on success, reported once the store is readable
        private static string _restoreFailed;            // set by Init on a definitive failure, reported the same way
        private static bool _restoreReported;

        private static float _nextTick;
        private static bool _cfgMirrored;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            // FIRST, before anything else touches the world: a restore staged by the previous run must be
            // applied while the world files are still closed. This is deliberately file-driven (a marker
            // file next to the config dir, not the store) because FeatureStore keys off the LOADED world's
            // name, and at plugin-Awake time no world exists yet.
            try { ApplyPendingRestore(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Staged restore check failed (world untouched): {e.Message}"); }

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableAutoBackup = cfg.Bind("Features", "EnableAutoBackup", false,
                    "Copy the world save set (the chunked save directory's current generation, or a legacy world's .db + .fwl pair) into BackupDir on a timer. OFF by default: it writes to disk on a schedule. Manual backups from the panel work regardless of this setting.");
                _backupIntervalMinutes = cfg.Bind("Features", "BackupIntervalMinutes", 60,
                    "Minutes between automatic backups (minimum 10). Each backup forces a world save first and copies only after it completes.");
                _backupKeep = cfg.Bind("Features", "BackupKeep", 10,
                    "How many complete backup sets to keep per world. Older sets are pruned oldest-first (minimum 1); a set staged for restore is never pruned.");
                _backupDir = cfg.Bind("Features", "BackupDir", "",
                    "Absolute directory for backups. Empty = <worlds root>/adminpanel_backups. A chunked world's sets land in <BackupDir>/<world>/<stamp>/, a legacy world's pairs directly in <BackupDir>.");
                _enableScheduledRestart = cfg.Bind("Features", "EnableScheduledRestart", false,
                    "Allow a scheduled restart to actually terminate the server process. OFF by default: the countdown still announces, then logs 'restart suppressed' instead of quitting, so announcements can be tested safely.");
            }

            // Reads are NOT registered with the audit chokepoint (every call writes an audit line — wave-1
            // lesson); AP_SrvBackupReq is a poll. The three that change something are owner-only (null).
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBackupNow", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBackupStage", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRestartReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvAutosaveSet", null);

            try { Harmony.CreateAndPatchAll(typeof(RpcRegisterPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave2 RpcRegisterPatch failed (backup/restart RPCs unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(SaveWatchPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave2 SaveWatchPatch failed (backups and save-integrity warnings unavailable): {e.Message}"); }
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + 1f;   // countdown granularity; every step below is cheap

            try { MirrorConfigToStore(); } catch (Exception) { }
            try { ReportRestoreOnce(); } catch (Exception) { }
            try { ApplyAutosaveOverride(); } catch (Exception e) { CompanionPlugin.FeatureLog($"Autosave override failed: {e.Message}"); }
            try { ObserveSaves(); } catch (Exception e) { CompanionPlugin.FeatureLog($"Save observation failed: {e.Message}"); }
            try { TickBackup(now); } catch (Exception e) { CompanionPlugin.FeatureLog($"Backup tick failed: {e.Message}"); }
            try { TickRestart(now); } catch (Exception e) { CompanionPlugin.FeatureLog($"Restart tick failed: {e.Message}"); }
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
                    ZRoutedRpc.instance.Register("AP_SrvBackupReq", new Action<long>(OnBackupReq));
                    ZRoutedRpc.instance.Register("AP_SrvBackupNow", new Action<long>(OnBackupNow));
                    ZRoutedRpc.instance.Register<string>("AP_SrvBackupStage", OnBackupStage);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvRestartReq", OnRestartReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvAutosaveSet", OnAutosaveSet);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave2 backup RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== save observation (integrity + backup trigger) ====================

        // Stacked alongside CompanionPlugin.SaveTimestampPatch on the same method — sanctioned by
        // spec-companion.md §8.3. BOTH halves run on the save worker thread: they may only touch atomics.
        // No ZNet/Unity/World access here (World.GetSaveDirectory reaches Utils.GetSaveDataPath).
        [HarmonyPatch(typeof(ZNet), "SaveWorldThread")]
        internal static class SaveWatchPatch
        {
            [HarmonyPrefix]
            private static void Prefix() => Interlocked.Exchange(ref _saveThreadStartTicks, DateTime.UtcNow.Ticks);

            [HarmonyPostfix]
            private static void Postfix()
            {
                var start = Interlocked.Read(ref _saveThreadStartTicks);
                var ms = start == 0 ? 0L : (long)(DateTime.UtcNow - new DateTime(start, DateTimeKind.Utc)).TotalMilliseconds;
                Interlocked.Exchange(ref _lastSaveDurationMs, ms < 0 ? 0 : ms);
                Interlocked.Increment(ref _saveCompletions);
                _saveSignal = true;   // volatile: consumed by Tick on the main thread
            }
        }

        // Main thread: pick up whatever the worker recorded, measure the resulting save set, raise warnings.
        // The set is measured once per completed save (a stat per file of the chunked directory): the save
        // thread has already deleted the previous generation by the time the postfix fires, so the directory
        // is consistent even though IsSaving() may still be true until UpdateSave joins the thread.
        private static void ObserveSaves()
        {
            var done = Interlocked.Read(ref _saveCompletions);
            if (done != _processedCompletions)
            {
                _processedCompletions = done;
                _lastSaveEndTicksUtc = DateTime.UtcNow.Ticks;
                // Cheap on purpose: this runs on the main thread after EVERY save. Only the main data file is
                // measured (_main.N.db2, or the legacy .db): a handful of stats, never a per-chunk sweep of the
                // save directory. The full set is scanned only when a backup is actually taken.
                var layout = FeatureStore.ResolveSaveLayout();
                long bytes; int generation;
                if (layout != null && !layout.IsCloud && TryMainDataSize(layout, out bytes, out generation))
                {
                    _prevSetBytes = _lastSetBytes;
                    _lastSetBytes = bytes;
                    _lastSetGeneration = generation;
                }

                var ms = Interlocked.Read(ref _lastSaveDurationMs);
                if (ms > SlowSaveMs)
                    RaiseSaveWarning("slow", $"World save took {ms / 1000.0:0.0}s (over {SlowSaveMs / 1000}s). Disk or storage backend may be struggling.");
                if (_prevSetBytes > 0 && _lastSetBytes > 0 && _lastSetBytes < (long)(_prevSetBytes * ShrinkRatio))
                    RaiseSaveWarning("shrink", $"World data file shrank from {Mb(_prevSetBytes)} to {Mb(_lastSetBytes)} in one save. Check for mass object loss BEFORE the next save overwrites it.");
            }

            // Stale-save watchdog: nothing has completed for 3x the autosave interval.
            var interval = AutosaveSeconds();
            if (interval > 0f)
            {
                var sinceSeconds = _lastSaveEndTicksUtc == 0
                    ? Time.realtimeSinceStartup
                    : (float)(DateTime.UtcNow - new DateTime(_lastSaveEndTicksUtc, DateTimeKind.Utc)).TotalSeconds;
                if (sinceSeconds > interval * 3f)
                    RaiseSaveWarning("stale", $"No world save has completed for {(int)(sinceSeconds / 60)} min (autosave interval is {(int)(interval / 60)} min). Saving may be broken.");
            }
        }

        // Size of the world's main data file (chunked: _main.N.db2 of the newest generation that has its .ok
        // marker; legacy: the .db). Directory.GetFiles with a narrow pattern plus one FileInfo - no chunk sweep.
        private static bool TryMainDataSize(FeatureStore.WorldSaveSet layout, out long bytes, out int generation)
        {
            bytes = 0; generation = -1;
            try
            {
                if (!layout.Chunked)
                {
                    if (string.IsNullOrEmpty(layout.LegacyDb) || !File.Exists(layout.LegacyDb)) return false;
                    bytes = new FileInfo(layout.LegacyDb).Length;
                    return true;
                }
                if (string.IsNullOrEmpty(layout.SaveDirectory) || !Directory.Exists(layout.SaveDirectory)) return false;
                foreach (var ok in Directory.GetFiles(layout.SaveDirectory, "_main.*.ok"))
                {
                    var stem = Path.GetFileNameWithoutExtension(ok);   // "_main.N"
                    int n;
                    if (stem.Length > 6 && int.TryParse(stem.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > generation)
                        generation = n;
                }
                if (generation < 0) return false;
                var db2 = Path.Combine(layout.SaveDirectory, "_main." + generation + ".db2");
                if (!File.Exists(db2)) return false;
                bytes = new FileInfo(db2).Length;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Where a set that failed to verify after a restore is parked: next to the backup sets when that
        // parent is reachable (the engine never looks there), else a dot-named sibling of the live folder.
        private static string FailedRestoreParkingPath(string source, string liveDir, string stamp)
        {
            try
            {
                var parent = !string.IsNullOrEmpty(source) ? Path.GetDirectoryName(source) : null;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    return Path.Combine(parent, "failedrestore-" + stamp);
            }
            catch (Exception) { }
            return liveDir + ".failedrestore-" + stamp;
        }

        private static void RaiseSaveWarning(string key, string text)
        {
            _saveWarning = text;
            _saveWarningTicks = DateTime.UtcNow.Ticks;
            var nowTicks = DateTime.UtcNow.Ticks;
            if (WarnThrottle.TryGetValue(key, out var last) &&
                (nowTicks - last) < TimeSpan.FromMinutes(WarnThrottleMinutes).Ticks) return;
            WarnThrottle[key] = nowTicks;
            CompanionPlugin.FeatureLog("SAVE WARNING: " + text);
            try { Wave1Moderation.NotifyOnlineAdmins("Save warning: " + text); } catch (Exception) { }
            try { Wave1AuditRpc.PostModLog("SAVE-WARNING " + text); } catch (Exception) { }
        }

        // ==================== backups ====================

        private static void OnBackupReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBackupReq")) return;
            SendBackupData(sender);
        }

        // AP_BackupData v1 (unchanged wire format): int ver=1, int shipped(<=30) x (string name, long sizeBytes,
        // long ticksUtc) newest first, bool autoOn, int intervalMinutes, string lastError. "name" is what the
        // panel sends back in AP_SrvBackupStage: a chunked set's stamp directory, a legacy pair's file stem.
        private static void SendBackupData(long sender)
        {
            var pkg = new ZPackage();
            pkg.Write(1);   // payload version — bump, never reorder

            var set = FeatureStore.ResolveSaveLayout();   // layout only: this is polled every 20 s
            var list = ListBackups(BackupSetParent(set), set != null && set.Chunked);
            var shipped = Math.Min(list.Count, BackupShipCap);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++)
            {
                pkg.Write(list[i].Name);
                pkg.Write(list[i].Bytes);
                pkg.Write(list[i].Ticks);
            }
            pkg.Write(AutoBackupOn);
            pkg.Write(BackupIntervalMinutes);
            pkg.Write(_lastError ?? "");

            // ReplyTo, not InvokeRoutedRPC: on a listen-server host the requesting admin is this process and
            // the routed packet would be discarded by the panel's anti-spoof gate (no server peer to verify).
            try { CompanionPlugin.ReplyTo(sender, "AP_BackupData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_BackupData reply failed: {e.Message}"); }
        }

        private static void OnBackupNow(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBackupNow")) return;

            CompanionPlugin.SrvAudit(sender, "BACKUP-NOW", $"dir={BackupSetParent(FeatureStore.ResolveSaveLayout()) ?? "?"}");
            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.FeatureLog($"Manual backup requested by {admin}");
            StartBackup(sender);
        }

        // Shared by the manual RPC and the auto-backup timer. Never copies here: it forces a save (unless
        // one is already running) and hands off to TickBackup, which copies only once the save is done.
        private static void StartBackup(long requester)
        {
            if (_phase != Phase.Idle)
            {
                if (requester != 0) CompanionPlugin.NotifySender(requester, "A backup is already in progress");
                return;
            }

            string why;
            if (!PreflightBackup(out why))
            {
                _lastError = why;
                CompanionPlugin.FeatureLog("Backup refused: " + why);
                if (requester != 0)
                {
                    CompanionPlugin.NotifySender(requester, "Backup refused: " + why);
                    SendBackupData(requester);
                }
                else
                {
                    // The timer counts from the refusal, not from the last success: otherwise a world that
                    // cannot be backed up right now (never saved yet, low disk) is re-refused - and re-logged -
                    // on every one-second tick until it can.
                    _lastBackupTicksUtc = DateTime.UtcNow.Ticks;
                }
                return;
            }

            _backupRequester = requester;
            _saveSignal = false;
            _phase = Phase.WaitingForSave;
            _phaseDeadline = Time.unscaledTime + SaveWaitSeconds;

            // Refuse to STACK a save: SaveWorld does a blocking Join on a live save thread and this runs on
            // the main thread (spec-companion.md §5.5). If one is already running we simply wait for it —
            // its files are just as good a snapshot.
            try
            {
                if (!ZNet.instance.IsSaving() && Game.instance != null)
                    ZNet.instance.Save(false, false, true);   // waitForNextFrame:true, like every vanilla call site
            }
            catch (Exception e)
            {
                _phase = Phase.Idle;
                _lastError = "could not start a world save: " + e.Message;
                CompanionPlugin.FeatureLog("Backup: " + _lastError);
                if (requester != 0) CompanionPlugin.NotifySender(requester, "Backup failed: " + _lastError);
                return;
            }

            if (requester != 0) CompanionPlugin.NotifySender(requester, "Backup started: saving the world first...");
        }

        private static void TickBackup(float now)
        {
            // A copy is in flight on a worker. Nothing here may touch the backup directory until it lands.
            if (_phase == Phase.Copying)
            {
                if (!_copyDone) return;
                _copyDone = false;
                _copyBusy = false;
                _phase = Phase.Idle;
                var requester = _backupRequester;
                _backupRequester = 0;
                _lastError = _copyOk ? "" : _copyError;
                _lastBackupTicksUtc = DateTime.UtcNow.Ticks;   // attempted: do not retry-storm on failure
                var lines = _copyLog;
                _copyLog = null;
                if (lines != null)
                    for (var i = 0; i < lines.Count; i++) CompanionPlugin.FeatureLog(lines[i]);
                if (_copyOk && _copyModLog.Length > 0)
                {
                    try { Wave1AuditRpc.PostModLog(_copyModLog); } catch (Exception) { }
                    _copyModLog = "";
                }
                if (requester != 0)
                {
                    CompanionPlugin.NotifySender(requester, _copyOk ? "Backup complete" : "Backup failed: " + _lastError);
                    SendBackupData(requester);
                }
                return;
            }

            if (_phase == Phase.WaitingForSave)
            {
                // Both conditions matter: the postfix fires on the worker thread the moment the write
                // finishes, but IsSaving() (m_saveThread != null) stays true until UpdateSave clears it on
                // the main thread (ZNet.cs:1766-1780). Copying between those two points is still unsafe.
                if (_saveSignal && !ZNet.instance.IsSaving())
                {
                    _saveSignal = false;
                    var requester = _backupRequester;
                    if (BeginCopy())
                    {
                        _phase = Phase.Copying;   // the requester is answered when the worker lands
                        return;
                    }
                    _phase = Phase.Idle;
                    _backupRequester = 0;
                    _lastBackupTicksUtc = DateTime.UtcNow.Ticks;
                    CompanionPlugin.FeatureLog("Backup " + _lastError);
                    if (requester != 0)
                    {
                        CompanionPlugin.NotifySender(requester, "Backup failed: " + _lastError);
                        SendBackupData(requester);
                    }
                }
                else if (now > _phaseDeadline)
                {
                    _phase = Phase.Idle;
                    var requester = _backupRequester;
                    _backupRequester = 0;
                    _lastError = "timed out waiting for the world save to finish";
                    _lastBackupTicksUtc = DateTime.UtcNow.Ticks;
                    CompanionPlugin.FeatureLog("Backup: " + _lastError);
                    if (requester != 0)
                    {
                        CompanionPlugin.NotifySender(requester, "Backup failed: " + _lastError);
                        SendBackupData(requester);
                    }
                }
                return;
            }

            if (!AutoBackupOn) return;
            if (!SeedLastBackupTime()) return;
            var dueTicks = _lastBackupTicksUtc + TimeSpan.FromMinutes(BackupIntervalMinutes).Ticks;
            if (DateTime.UtcNow.Ticks < dueTicks) return;
            CompanionPlugin.FeatureLog($"Automatic backup due ({BackupIntervalMinutes} min interval)");
            StartBackup(0);
        }

        // Recover "when did we last back up" from the newest set ON DISK, once per world. Without this a
        // server that restarts often would take a fresh backup on every boot.
        private static bool SeedLastBackupTime()
        {
            if (_backupSeeded) return true;
            var set = FeatureStore.ResolveSaveLayout();
            var parent = BackupSetParent(set);
            if (parent == null) return false;   // no world yet — try again next tick
            var list = ListBackups(parent, set.Chunked);
            _lastBackupTicksUtc = list.Count > 0 ? list[0].Ticks : DateTime.UtcNow.Ticks;
            _backupSeeded = true;
            return true;
        }

        private static bool PreflightBackup(out string why)
        {
            why = "";
            var set = FeatureStore.ResolveSaveSet();
            if (set == null) { why = "no world is loaded"; return false; }
            if (set.IsCloud)
            {
                why = $"the world is stored on a cloud save ({set.Source}); a file copy cannot back it up safely";
                return false;
            }
            if (!set.Complete)
            {
                why = DescribeIncomplete(set);
                return false;
            }
            var parent = BackupSetParent(set);
            if (parent == null) { why = "the backup directory could not be resolved"; return false; }
            try { Directory.CreateDirectory(parent); }
            catch (Exception e) { why = "cannot create the backup directory: " + e.Message; return false; }

            // Free-space guard on the set's total size. If the drive cannot be queried (exotic mount,
            // permissions) we proceed rather than block backups forever — the copy itself will fail loudly
            // if the disk is full.
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(parent));
                if (!string.IsNullOrEmpty(root))
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    var need = (long)(set.Bytes * FreeSpaceFactor);
                    if (free < need)
                    {
                        why = $"only {free / 1048576} MB free on {root}, need {need / 1048576} MB ({FreeSpaceFactor}x the save set of {Mb(set.Bytes)})";
                        return false;
                    }
                }
            }
            catch (Exception) { }
            return true;
        }

        private static string DescribeIncomplete(FeatureStore.WorldSaveSet set)
        {
            if (set.Chunked)
            {
                if (set.Generation < 0)
                    return $"the save directory {set.SaveDirectory} has no complete _main.N generation yet (the world has not been saved by this game build)";
                return $"save generation {set.Generation} in {set.SaveDirectory} is missing chunk file(s) named by its index; nothing loadable to copy until the next save";
            }
            if (set.Files.Count == 0)
                return "the world has no save on disk yet: neither a chunked save directory with a finished generation nor a legacy .db + .fwl pair exists (a world that has never been saved reports this)";
            return "the legacy .db + .fwl pair is incomplete on disk (one of the two files is missing)";
        }

        // MAIN THREAD. Resolves everything that needs Unity/ZNet/config — the save set, the backup directory,
        // the unique name and the "no save is in flight" decision — and then hands the byte copy to a worker.
        // Returns false when the copy could not even be started (_lastError says why).
        private static bool BeginCopy()
        {
            var set = FeatureStore.ResolveSaveSet();
            var parent = BackupSetParent(set);
            if (set == null || parent == null) { _lastError = "no world / backup directory at copy time"; return false; }
            if (set.IsCloud) { _lastError = "the world is on a cloud save"; return false; }
            if (ZNet.instance.IsSaving()) { _lastError = "a save started again before the copy could run"; return false; }
            if (_copyBusy) { _lastError = "the previous backup copy is still running"; return false; }
            if (!set.Complete) { _lastError = DescribeIncomplete(set); return false; }

            string name;
            try
            {
                Directory.CreateDirectory(parent);
                name = UniqueSetName(parent, set);
            }
            catch (Exception e) { _lastError = "cannot prepare the backup directory: " + e.Message; return false; }

            var job = new CopyJob
            {
                Chunked = set.Chunked,
                WorldName = set.WorldName,
                Generation = set.Generation,
                Files = new List<string>(set.Files),
                SetParent = parent,
                Name = name,
                StagedName = StagedBackupName(),
                Keep = BackupKeep,   // ConfigEntry/store reads happen here so the worker touches nothing but System.IO
            };
            _copyBusy = true;
            _copyDone = false;
            _copyOk = false;
            _copyError = "";
            _copyModLog = "";
            _copyLog = null;
            try { Task.Run(() => CopyWorker(job)); }
            catch (Exception e)
            {
                _copyBusy = false;
                _lastError = "could not start the copy worker: " + e.Message;
                return false;
            }
            return true;
        }

        // WORKER THREAD. Pure System.IO: no Unity, no ZNet, no store, no logging. A chunked set is copied into
        // "<stamp>.tmp/", verified as a loadable set (FeatureStore.ScanChunkedSet), given its manifest and only
        // then renamed to "<stamp>/"; a legacy pair goes through .tmp files the same way. So a half-copied set is
        // never listed or restored. The outcome is published through _copyDone for TickBackup to report.
        private static void CopyWorker(CopyJob job)
        {
            var log = new List<string>();
            var ok = false;
            var err = "";
            var modLog = "";
            string tmpDir = null, tmpDb = null, tmpFwl = null;
            try
            {
                if (job.Chunked)
                {
                    tmpDir = Path.Combine(job.SetParent, job.Name + TmpSuffix);
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                    Directory.CreateDirectory(tmpDir);
                    var skipped = 0;
                    foreach (var src in job.Files)
                    {
                        try { CopySnapshot(src, Path.Combine(tmpDir, Path.GetFileName(src))); }
                        catch (Exception e) when ((e is FileNotFoundException || e is DirectoryNotFoundException)
                                                  && src.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase))
                        {
                            // A save finished while this copy ran and DeleteOldChunks removed a superseded chunk
                            // version (ZNet.cs:1884). Only the chunks the index names matter, and the verification
                            // below refuses the set if one of THOSE is missing; a stale vintage is not part of it.
                            skipped++;
                        }
                    }

                    var scan = FeatureStore.ScanChunkedSet(tmpDir);
                    if (!scan.Complete || scan.Generation != job.Generation || scan.Files.Count + skipped != job.Files.Count)
                        throw new IOException(
                            $"the copied set is not loadable (generation {scan.Generation} vs {job.Generation}, {scan.Files.Count}/{job.Files.Count} files, {scan.MissingIndexed.Count} indexed chunk(s) missing)");
                    WriteManifest(tmpDir, job.WorldName, scan);
                    var final = Path.Combine(job.SetParent, job.Name);
                    Directory.Move(tmpDir, final);
                    tmpDir = null;
                    log.Add($"Backup written: {job.Name} (generation {scan.Generation}, {scan.Files.Count} files, {Mb(scan.Bytes)}) in {job.SetParent}");
                    modLog = $"BACKUP {job.Name} ({scan.Files.Count} files, {Mb(scan.Bytes)})";
                }
                else
                {
                    var db = Path.Combine(job.SetParent, job.Name + ".db");
                    var fwl = Path.Combine(job.SetParent, job.Name + ".fwl");
                    tmpDb = db + TmpSuffix;
                    tmpFwl = fwl + TmpSuffix;
                    CopySnapshot(job.Files[0], tmpDb);
                    CopySnapshot(job.Files[1], tmpFwl);
                    File.Move(tmpDb, db);
                    tmpDb = null;
                    File.Move(tmpFwl, fwl);
                    tmpFwl = null;
                    var bytes = 0L;
                    try { bytes = new FileInfo(db).Length + new FileInfo(fwl).Length; } catch (Exception) { }
                    log.Add($"Backup written: {job.Name} ({Mb(bytes)}) in {job.SetParent}");
                    modLog = $"BACKUP {job.Name} ({Mb(bytes)})";
                }
                PruneBackups(job.SetParent, job.Chunked, job.Keep, job.StagedName, log);
                ok = true;
            }
            catch (Exception e)
            {
                err = "copy failed: " + e.Message;
                log.Add("Backup " + err);
            }
            finally
            {
                try { if (tmpDir != null && Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch (Exception) { }
                try { if (tmpDb != null && File.Exists(tmpDb)) File.Delete(tmpDb); } catch (Exception) { }
                try { if (tmpFwl != null && File.Exists(tmpFwl)) File.Delete(tmpFwl); } catch (Exception) { }
                _copyOk = ok;
                _copyError = err;
                _copyModLog = modLog;
                _copyLog = log;
                _copyDone = true;   // volatile, written LAST: publishes everything above to the main thread
            }
        }

        // The source is opened with FileShare.ReadWrite | FileShare.Delete deliberately. While this copy runs
        // the game may start and finish a save: SaveWorldThread never rewrites a file in place (every file is
        // File.Create'd under a new generation number or chunk version, FileWriter.cs:64), but on success it
        // DELETES the previous generation's _main files and the superseded chunk versions (ZNet.cs:1880-1884)
        // - the very files this copy holds open. Without FileShare.Delete that delete would fail on the save
        // thread; a backup must never be able to break a save. With it the save proceeds untouched and this
        // handle keeps reading the complete file it opened. A file of the set that was deleted before this
        // copy reached it simply fails the copy (reported honestly; the next interval retries).
        private static void CopySnapshot(string src, string dst)
        {
            const int Buf = 1 << 16;
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Buf))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, Buf))
                input.CopyTo(output, Buf);
        }

        // Chunked: "<stamp>" directory under <backupRoot>/<world>/; legacy: "<world>-<stamp>" file stem, as before.
        private static string UniqueSetName(string parent, FeatureStore.WorldSaveSet set)
        {
            var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
            var stem = set.Chunked ? stamp : set.WorldName + "-" + stamp;
            var name = stem;
            var n = 2;
            while (n < 100 && (set.Chunked
                       ? Directory.Exists(Path.Combine(parent, name)) || Directory.Exists(Path.Combine(parent, name + TmpSuffix))
                       : File.Exists(Path.Combine(parent, name + ".db"))))
                name = stem + "-" + n++;
            return name;
        }

        // ---- manifest: one small file per chunked set so a listing costs two stats per set, not one per chunk ----

        private static void WriteManifest(string setDir, string world, FeatureStore.ChunkedScan scan)
        {
            var text = "world=" + world + "\r\n" +
                       "generation=" + scan.Generation.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       "files=" + scan.Files.Count.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       "bytes=" + scan.Bytes.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       "ticks=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       "companion=" + CompanionPlugin.PluginVersion + "\r\n";
            File.WriteAllText(Path.Combine(setDir, ManifestName), text);
        }

        // Cheap listing of a chunked set: the manifest (written last, after verification) plus one existence
        // check of the generation's .ok and .db2. A set without a manifest (copied by hand, or by a future
        // layout) falls back to the full scan and is listed only when that scan says it is loadable.
        private static bool ReadSetEntry(string setDir, out long bytes, out long ticks)
        {
            bytes = 0; ticks = 0;
            var manifest = Path.Combine(setDir, ManifestName);
            var generation = -1;
            var haveManifest = false;
            try
            {
                if (File.Exists(manifest))
                {
                    foreach (var line in File.ReadAllLines(manifest))
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = line.Substring(0, eq).Trim();
                        var v = line.Substring(eq + 1).Trim();
                        if (k == "generation") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);
                        else if (k == "bytes") long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
                        else if (k == "ticks") long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
                    }
                    haveManifest = generation >= 0 && bytes > 0 && ticks > 0;
                }
            }
            catch (Exception) { haveManifest = false; }

            if (haveManifest)
            {
                return File.Exists(Path.Combine(setDir, "_main." + generation + ".ok")) &&
                       File.Exists(Path.Combine(setDir, "_main." + generation + ".db2"));
            }
            var scan = FeatureStore.ScanChunkedSet(setDir);
            if (!scan.Complete) return false;
            bytes = scan.Bytes;
            ticks = StampTicks(Path.GetFileName(setDir));
            if (ticks == 0)
            {
                try { ticks = File.GetLastWriteTimeUtc(Path.Combine(setDir, "_main." + scan.Generation + ".ok")).Ticks; }
                catch (Exception) { ticks = 0; }
            }
            return true;
        }

        // "yyyyMMdd-HHmmss[-n]" (our own UTC stamps) -> ticks; 0 when the name is not one of ours.
        private static long StampTicks(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < StampFormat.Length) return 0;
            DateTime t;
            return DateTime.TryParseExact(name.Substring(0, StampFormat.Length), StampFormat, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)
                ? t.Ticks
                : 0;
        }

        // Retention counts COMPLETE sets only; a half-copied set is never counted and never deletes a good one.
        // WORKER THREAD (called from CopyWorker): it is file IO like the copy, so `keep` and the staged name
        // arrive pre-read and log lines are collected instead of emitted. Whole sets are pruned, never files.
        private static void PruneBackups(string parent, bool chunked, int keep, string stagedName, List<string> log)
        {
            try
            {
                var list = ListBackups(parent, chunked);
                for (var i = keep; i < list.Count; i++)
                {
                    var name = list[i].Name;
                    if (!string.IsNullOrEmpty(stagedName) && string.Equals(name, stagedName, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add($"Kept old backup {name}: it is staged for restore");
                        continue;
                    }
                    try
                    {
                        if (chunked) Directory.Delete(Path.Combine(parent, name), true);
                        else
                        {
                            File.Delete(Path.Combine(parent, name + ".db"));
                            File.Delete(Path.Combine(parent, name + ".fwl"));
                        }
                        log.Add($"Pruned old backup {name} (keep={keep})");
                    }
                    catch (Exception e) { log.Add($"Could not prune backup {name}: {e.Message}"); }
                }
                // Abandoned half-copies from a crash mid-backup. The set this worker just wrote has already been
                // renamed into place, so any ".tmp" older than an hour belongs to nobody.
                var cutoff = DateTime.UtcNow.AddHours(-1);
                if (chunked)
                {
                    foreach (var tmp in Directory.GetDirectories(parent, "*" + TmpSuffix))
                    {
                        try { if (Directory.GetLastWriteTimeUtc(tmp) < cutoff) Directory.Delete(tmp, true); }
                        catch (Exception) { }
                    }
                }
                else
                {
                    foreach (var tmp in Directory.GetFiles(parent, "*" + TmpSuffix))
                    {
                        try { if (File.GetLastWriteTimeUtc(tmp) < cutoff) File.Delete(tmp); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception e) { log.Add($"Backup prune failed: {e.Message}"); }
        }

        private sealed class BackupEntry
        {
            public string Name;
            public long Bytes;
            public long Ticks;
        }

        // Pure System.IO (main thread for the RPC replies and health, worker thread for pruning). A chunked
        // world lists the set directories under <backupRoot>/<world>/, a legacy world the pairs in <backupRoot>;
        // a layout's sets are only ever restored into the same layout, so the other kind is never listed.
        private static List<BackupEntry> ListBackups(string parent, bool chunked)
        {
            var res = new List<BackupEntry>();
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return res;
            try
            {
                if (chunked)
                {
                    foreach (var dir in Directory.GetDirectories(parent))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                        long bytes, ticks;
                        if (!ReadSetEntry(dir, out bytes, out ticks)) continue;   // incomplete: not restorable, so not listed
                        res.Add(new BackupEntry { Name = name, Bytes = bytes, Ticks = ticks });
                    }
                }
                else
                {
                    foreach (var db in Directory.GetFiles(parent, "*.db"))
                    {
                        var fwl = Path.ChangeExtension(db, ".fwl");
                        if (!File.Exists(fwl)) continue;   // incomplete pair: not restorable, so not listed
                        try
                        {
                            var fi = new FileInfo(db);
                            var fm = new FileInfo(fwl);
                            res.Add(new BackupEntry
                            {
                                Name = Path.GetFileNameWithoutExtension(db),
                                Bytes = fi.Length + fm.Length,
                                Ticks = fi.LastWriteTimeUtc.Ticks,
                            });
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            res.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));   // newest first
            return res;
        }

        // ==================== staged restore ====================

        // The name arrives from the network: it is a set NAME, never a path. Anything with a separator or
        // ".." is rejected outright and the resolved path is re-verified against the backup directory
        // prefix — path traversal here would let an admin-role account overwrite arbitrary files.
        private static void OnBackupStage(long sender, string rawName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBackupStage")) return;

            var name = CleanFileName(rawName);
            if (name.Length == 0)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: invalid backup name");
                CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={Trim(rawName, 64)} result=rejected-name");
                return;
            }

            var set = FeatureStore.ResolveSaveLayout();   // the set itself is scanned once the name is validated
            if (set == null)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: no world is loaded");
                return;
            }
            if (set.IsCloud)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: this world is on a cloud save and cannot be file-restored");
                CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=cloud-save");
                return;
            }
            var parent = BackupSetParent(set);
            if (parent == null)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: the backup directory could not be resolved");
                return;
            }

            string keptAs;
            if (set.Chunked)
            {
                if (set.SaveDirectory == null)
                {
                    CompanionPlugin.NotifySender(sender, "Restore refused: the world's save directory is not readable on this build");
                    return;
                }
                string srcDir;
                if (!ResolveSetDirInside(parent, name, out srcDir))
                {
                    CompanionPlugin.NotifySender(sender, "Restore refused: that backup set does not exist in the backup folder");
                    CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=not-found-or-outside-dir");
                    return;
                }
                var scan = FeatureStore.ScanChunkedSet(srcDir);
                if (!scan.Complete)
                {
                    CompanionPlugin.NotifySender(sender, "Restore refused: that backup set is incomplete and would not load");
                    CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=incomplete-set generation={scan.Generation} missing={scan.MissingIndexed.Count}");
                    return;
                }
                // Nothing is copied now: the set stays where it is (and is exempt from pruning) and is copied
                // in at the next boot, when the copy can take as long as it needs without stalling the server.
                if (!WriteRestoreMarker(set, name, srcDir))
                {
                    CompanionPlugin.NotifySender(sender, "Restore staging failed: the restore marker could not be written (see the log)");
                    return;
                }
                keptAs = $"{set.WorldName}_backup_restore-<stamp>";
            }
            else
            {
                string srcDb, srcFwl;
                if (!ResolveInsideBackupDir(parent, name, out srcDb, out srcFwl))
                {
                    CompanionPlugin.NotifySender(sender, "Restore refused: that backup pair does not exist in the backup folder");
                    CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=not-found-or-outside-dir");
                    return;
                }
                if (set.LegacyDb == null || set.LegacyFwl == null)
                {
                    CompanionPlugin.NotifySender(sender, "Restore refused: the world's file paths are not readable on this build");
                    return;
                }

                var stagedDb = set.LegacyDb + ".aprestore";
                var stagedFwl = set.LegacyFwl + ".aprestore";
                try
                {
                    File.Copy(srcDb, stagedDb + TmpSuffix, true);
                    File.Copy(srcFwl, stagedFwl + TmpSuffix, true);
                    if (File.Exists(stagedDb)) File.Delete(stagedDb);
                    if (File.Exists(stagedFwl)) File.Delete(stagedFwl);
                    File.Move(stagedDb + TmpSuffix, stagedDb);
                    File.Move(stagedFwl + TmpSuffix, stagedFwl);
                }
                catch (Exception e)
                {
                    TryDelete(stagedDb + TmpSuffix);
                    TryDelete(stagedFwl + TmpSuffix);
                    CompanionPlugin.FeatureLog($"Restore staging failed for {name}: {e.Message}");
                    CompanionPlugin.NotifySender(sender, "Restore staging failed: " + e.Message);
                    CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=copy-failed detail={e.Message}");
                    return;
                }
                if (!WriteRestoreMarker(set, name, null))
                {
                    CompanionPlugin.NotifySender(sender, "Restore staging failed: the restore marker could not be written (see the log)");
                    return;
                }
                keptAs = ".prerestore files";
            }

            var t = FeatureStore.Table(TblCfg);
            t[KeyRestoreStaged] = name + "|" + DateTime.UtcNow.Ticks;
            FeatureStore.SaveTable(TblCfg);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} world={set.WorldName} layout={(set.Chunked ? "chunked" : "legacy")} result=staged");
            CompanionPlugin.FeatureLog($"RESTORE STAGED: {name} -> {set.WorldName}. It will be applied on the NEXT server start (staged by {admin}).");
            Wave1AuditRpc.PostModLog($"RESTORE STAGED {name} for world {set.WorldName} (by {admin}) - applies on next server start");
            Wave1Moderation.NotifyOnlineAdmins($"Restore staged: {name}. A server restart is required to apply it.");
            CompanionPlugin.NotifySender(sender,
                $"Backup '{name}' is staged. A running server cannot swap its own world save - RESTART the server to apply it. The current world will be kept as {keptAs}.");
        }

        // Validate + resolve a network-supplied set name strictly inside <backupRoot>/<world>/.
        private static bool ResolveSetDirInside(string parent, string name, out string dir)
        {
            dir = null;
            try
            {
                var root = Path.GetFullPath(parent);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += Path.DirectorySeparatorChar;
                var cand = Path.GetFullPath(Path.Combine(root, name));
                if (!cand.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                if (cand.Length <= root.Length) return false;
                if (!Directory.Exists(cand)) return false;
                dir = cand;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Validate + resolve a network-supplied backup name strictly inside the backup directory (legacy pair).
        private static bool ResolveInsideBackupDir(string dir, string name, out string db, out string fwl)
        {
            db = null; fwl = null;
            try
            {
                var root = Path.GetFullPath(dir);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += Path.DirectorySeparatorChar;
                var candDb = Path.GetFullPath(Path.Combine(root, name + ".db"));
                var candFwl = Path.GetFullPath(Path.Combine(root, name + ".fwl"));
                if (!candDb.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                if (!candFwl.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                if (!File.Exists(candDb) || !File.Exists(candFwl)) return false;
                db = candDb; fwl = candFwl;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string RestoreMarkerPath()
        {
            try { return Path.Combine(Path.Combine(Paths.ConfigPath, "AdminPanelCompanion"), "pending_restore.txt"); }
            catch (Exception) { return null; }
        }

        // The marker is a plain file, not a store table: it must be readable at plugin-Awake time, BEFORE a
        // world exists (FeatureStore keys its directory off the loaded world's name). Keys: layout, world,
        // dir (worlds root), backup (set name); chunked adds savedir (the live directory), source (the set
        // directory to copy from) and tmp (the sibling the copy lands in, fixed so a retry can reuse it).
        private static bool WriteRestoreMarker(FeatureStore.WorldSaveSet set, string backupName, string sourceDir)
        {
            var path = RestoreMarkerPath();
            if (path == null) return false;
            try
            {
                var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
                var text = "layout=" + (set.Chunked ? "chunked" : "legacy") + "\r\n" +
                           "world=" + set.WorldName + "\r\n" +
                           "dir=" + set.WorldsRoot + "\r\n" +
                           "backup=" + backupName + "\r\n" +
                           "ticks=" + DateTime.UtcNow.Ticks + "\r\n";
                if (set.Chunked)
                    text += "savedir=" + set.SaveDirectory + "\r\n" +
                            "source=" + sourceDir + "\r\n" +
                            "tmp=" + Path.Combine(set.WorldsRoot, set.WorldName + "_backup_staging-" + stamp) + "\r\n";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Could not write the restore marker: {e.Message}");
                return false;
            }
        }

        // Called from Init, before the game opens the world. Moves the CURRENT save aside (never deletes it)
        // and swaps the staged set in. Any failure leaves the world exactly as it was. The marker is consumed
        // on success and on every definitive failure (source gone, copy or verification failed): a restore
        // that could not be applied is reported, not silently retried on some later boot. It survives only an
        // interrupted attempt (crash mid-swap), which the next boot resumes safely.
        private static void ApplyPendingRestore()
        {
            var marker = RestoreMarkerPath();
            if (marker == null || !File.Exists(marker)) return;

            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(marker))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string world, dir, backup, layout;
            kv.TryGetValue("world", out world);
            kv.TryGetValue("dir", out dir);
            kv.TryGetValue("backup", out backup);
            kv.TryGetValue("layout", out layout);
            backup = backup ?? "(unnamed)";

            if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(dir))
            {
                CompanionPlugin.FeatureLog("Restore marker is unreadable; ignoring and removing it.");
                TryDelete(marker);
                return;
            }

            if (layout == "chunked")
            {
                string saveDir, source, tmp;
                kv.TryGetValue("savedir", out saveDir);
                kv.TryGetValue("source", out source);
                kv.TryGetValue("tmp", out tmp);
                if (string.IsNullOrEmpty(saveDir)) saveDir = Path.Combine(dir, world);
                if (string.IsNullOrEmpty(tmp)) tmp = Path.Combine(dir, world + "_backup_staging-" + DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture));
                ApplyChunkedRestore(marker, world, saveDir, source, tmp, backup);
            }
            else
            {
                ApplyLegacyRestore(marker, world, dir, backup);
            }
        }

        // Chunked swap, in an order that is safe to interrupt at any point:
        //   1. copy the set into the sibling <world>_backup_staging-<stamp>/ (reused if a previous attempt
        //      already left a complete copy there) and verify it is a loadable set;
        //   2. move the live <world>/ to <world>_backup_restore-<stamp>/ (the engine's own restore convention:
        //      SaveSystem.RestoreBackup :601, classified as a RestoredBackup of this world, never auto-pruned);
        //   3. move the verified copy to <world>/ and verify it again.
        // A crash between 2 and 3 leaves the marker in place and the copy complete, so the next boot skips 1
        // and 2 and finishes 3 instead of letting the game create a fresh world under the name.
        private static void ApplyChunkedRestore(string marker, string world, string liveDir, string source, string tmp, string backup)
        {
            // Staging copies left behind by an earlier, superseded staging of this world (the marker names the
            // current one). They are our own copies of backup sets, never the live world or a backup itself.
            try
            {
                var root = Path.GetDirectoryName(liveDir);
                if (root != null && Directory.Exists(root))
                    foreach (var d in Directory.GetDirectories(root, world + "_backup_staging-*"))
                        if (!string.Equals(Path.GetFullPath(d), Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase))
                            Directory.Delete(d, true);
            }
            catch (Exception) { }

            FeatureStore.ChunkedScan copy = null;
            try
            {
                if (Directory.Exists(tmp))
                {
                    copy = FeatureStore.ScanChunkedSet(tmp);
                    if (!copy.Complete)
                    {
                        Directory.Delete(tmp, true);
                        copy = null;
                    }
                }
                if (copy == null)
                {
                    if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                    {
                        RestoreFailed(marker, backup, $"the staged backup set is no longer at {source ?? "?"}; nothing was changed");
                        return;
                    }
                    var src = FeatureStore.ScanChunkedSet(source);
                    if (!src.Complete)
                    {
                        RestoreFailed(marker, backup, $"the staged backup set at {source} is not loadable (generation {src.Generation}, {src.MissingIndexed.Count} indexed chunk(s) missing); nothing was changed");
                        return;
                    }
                    Directory.CreateDirectory(tmp);
                    foreach (var f in src.Files) CopySnapshot(f, Path.Combine(tmp, Path.GetFileName(f)));
                    copy = FeatureStore.ScanChunkedSet(tmp);
                    if (!copy.Complete || copy.Generation != src.Generation || copy.Files.Count != src.Files.Count)
                    {
                        try { Directory.Delete(tmp, true); } catch (Exception) { }
                        RestoreFailed(marker, backup, $"the copy of the backup set did not verify (generation {copy.Generation} vs {src.Generation}, {copy.Files.Count}/{src.Files.Count} files); the live world was not touched");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
                RestoreFailed(marker, backup, $"copying the backup set failed ({e.Message}); the live world was not touched");
                return;
            }

            var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
            var kept = Path.Combine(Path.GetDirectoryName(liveDir) ?? "", world + "_backup_restore-" + stamp);
            var movedAside = false;
            try
            {
                if (Directory.Exists(liveDir))
                {
                    Directory.Move(liveDir, kept);
                    movedAside = true;
                }
                Directory.Move(tmp, liveDir);
            }
            catch (Exception e)
            {
                // Best-effort rollback: the original world goes back exactly where it was.
                try { if (movedAside && !Directory.Exists(liveDir) && Directory.Exists(kept)) Directory.Move(kept, liveDir); } catch (Exception) { }
                // The staging copy sits one level under the worlds root, where the engine would list it as a backup
                // of this world forever (SaveSystem.GetSaveInfo classifies "_backup_staging-"): remove it.
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
                RestoreFailed(marker, backup, $"swapping the save directory failed ({e.Message}); the original world was left in place");
                return;
            }

            var live = FeatureStore.ScanChunkedSet(liveDir);
            if (!live.Complete || live.Generation != copy.Generation)
            {
                // The verified copy is now the live directory and does not scan as loadable: put the original
                // back and keep the moved-in files aside for inspection instead of guessing.
                // Park the unloadable set OUTSIDE the worlds root (next to the backup sets) so the engine never
                // offers a known-broken folder as a restorable backup; the same-volume fallback uses a dot-name
                // that the engine's "_backup_" classifier does not match.
                var broken = FailedRestoreParkingPath(source, liveDir, stamp);
                try { Directory.Move(liveDir, broken); }
                catch (Exception) { try { Directory.Move(liveDir, liveDir + ".failedrestore-" + stamp); } catch (Exception) { } }
                try { if (movedAside && !Directory.Exists(liveDir) && Directory.Exists(kept)) Directory.Move(kept, liveDir); } catch (Exception) { }
                RestoreFailed(marker, backup, $"the moved-in set did not verify (generation {live.Generation}, {live.MissingIndexed.Count} indexed chunk(s) missing); the original world was put back, the failed set is at {broken}");
                return;
            }

            TryDelete(marker);
            _restoreApplied = backup;
            CompanionPlugin.FeatureLog("==================================================================");
            CompanionPlugin.FeatureLog($"RESTORE APPLIED: world '{world}' was replaced with backup '{backup}' (generation {live.Generation}, {live.Files.Count} files, {Mb(live.Bytes)}).");
            CompanionPlugin.FeatureLog(movedAside
                ? $"The world as it was before this boot is kept at: {kept}"
                : "There was no live save directory to keep (the world had never been saved).");
            CompanionPlugin.FeatureLog("==================================================================");
        }

        // Legacy pair swap, unchanged in shape: the .aprestore files staged next to the live pair are renamed
        // in, the live pair is kept as .prerestore-<stamp>.
        private static void ApplyLegacyRestore(string marker, string world, string dir, string backup)
        {
            var liveDb = Path.Combine(dir, world + ".db");
            var liveFwl = Path.Combine(dir, world + ".fwl");
            var stagedDb = liveDb + ".aprestore";
            var stagedFwl = liveFwl + ".aprestore";

            if (!File.Exists(stagedDb) || !File.Exists(stagedFwl))
            {
                RestoreFailed(marker, backup, "the staged .aprestore files are missing; nothing was changed");
                return;
            }

            var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
            var keptDb = liveDb + ".prerestore-" + stamp;
            var keptFwl = liveFwl + ".prerestore-" + stamp;
            var movedDb = false;
            var movedFwl = false;
            try
            {
                if (File.Exists(liveDb)) { File.Move(liveDb, keptDb); movedDb = true; }
                if (File.Exists(liveFwl)) { File.Move(liveFwl, keptFwl); movedFwl = true; }
                File.Move(stagedDb, liveDb);
                File.Move(stagedFwl, liveFwl);
            }
            catch (Exception e)
            {
                // Best-effort rollback: put the original world back exactly where it was.
                try { if (File.Exists(liveDb) && !File.Exists(stagedDb) && movedDb) File.Move(liveDb, stagedDb); } catch (Exception) { }
                try { if (movedDb && File.Exists(keptDb) && !File.Exists(liveDb)) File.Move(keptDb, liveDb); } catch (Exception) { }
                try { if (movedFwl && File.Exists(keptFwl) && !File.Exists(liveFwl)) File.Move(keptFwl, liveFwl); } catch (Exception) { }
                RestoreFailed(marker, backup, $"{e.Message}; the original world was left in place (the staged .aprestore files are kept: stage the backup again to retry)");
                return;
            }

            TryDelete(marker);
            _restoreApplied = backup;
            CompanionPlugin.FeatureLog("==================================================================");
            CompanionPlugin.FeatureLog($"RESTORE APPLIED: world '{world}' was replaced with backup '{backup}'.");
            CompanionPlugin.FeatureLog($"The world as it was before this boot is kept at: {keptDb} / {keptFwl}");
            CompanionPlugin.FeatureLog("==================================================================");
        }

        private static void RestoreFailed(string marker, string backup, string why)
        {
            TryDelete(marker);
            _restoreFailed = $"'{backup}': {why}";
            CompanionPlugin.FeatureLog("==================================================================");
            CompanionPlugin.FeatureLog($"!!! RESTORE FAILED for {_restoreFailed}. Stage the backup again from the panel to retry.");
            CompanionPlugin.FeatureLog("==================================================================");
        }

        // The store is only readable once a world is loaded, so the mod-log entry and store cleanup for an
        // applied (or failed) restore happen on the first tick that has a store.
        private static void ReportRestoreOnce()
        {
            if (_restoreReported || (_restoreApplied == null && _restoreFailed == null)) return;
            if (!FeatureStore.Ready) return;
            _restoreReported = true;
            var t = FeatureStore.Table(TblCfg);
            if (t.ContainsKey(KeyRestoreStaged))
            {
                t.Remove(KeyRestoreStaged);
                FeatureStore.SaveTable(TblCfg);
            }
            if (_restoreApplied != null)
                Wave1AuditRpc.PostModLog($"RESTORE APPLIED at startup: {_restoreApplied} (previous world kept beside it, never deleted)");
            else
                Wave1AuditRpc.PostModLog($"RESTORE FAILED at startup for {_restoreFailed}");
        }

        // The set name currently staged for restore ("" = none): the worker must never prune it.
        private static string StagedBackupName()
        {
            try
            {
                if (!FeatureStore.Ready) return "";
                string raw;
                if (!FeatureStore.Table(TblCfg).TryGetValue(KeyRestoreStaged, out raw) || string.IsNullOrEmpty(raw)) return "";
                var bar = raw.IndexOf('|');
                return bar > 0 ? raw.Substring(0, bar) : raw;
            }
            catch (Exception) { return ""; }
        }

        // ==================== scheduled restart ====================

        // ZPackage: int minutes, string reason, bool cancel.
        private static void OnRestartReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRestartReq")) return;

            int minutes; string reason; bool cancel;
            try { minutes = pkg.ReadInt(); reason = CleanText(pkg.ReadString()); cancel = pkg.ReadBool(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRestartReq: malformed packet dropped ({e.Message})"); return; }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var t = FeatureStore.Table(TblCfg);

            if (cancel)
            {
                var had = t.ContainsKey(KeyRestartAt);
                ClearRestart();
                CompanionPlugin.SrvAudit(sender, "RESTART-CANCEL", $"wasScheduled={had}");
                CompanionPlugin.FeatureLog($"Scheduled restart cancelled by {admin} (was scheduled: {had})");
                if (had)
                {
                    Wave1AuditRpc.PostModLog($"RESTART CANCELLED (by {admin})");
                    AnnounceAll("The scheduled server restart has been CANCELLED.");
                }
                CompanionPlugin.NotifySender(sender, had ? "Scheduled restart cancelled" : "No restart was scheduled");
                return;
            }

            minutes = Mathf.Clamp(minutes, 1, MaxRestartMinutes);
            if (reason.Length == 0) reason = "maintenance";
            var at = DateTime.UtcNow.AddMinutes(minutes).Ticks;
            t[KeyRestartAt] = at.ToString(CultureInfo.InvariantCulture);
            t[KeyRestartReason] = reason;
            FeatureStore.SaveTable(TblCfg);
            ArmWarnings(at);
            _restartPhase = RestartPhase.Counting;

            CompanionPlugin.SrvAudit(sender, "RESTART-SCHEDULE",
                $"minutes={minutes} atUtc={new DateTime(at, DateTimeKind.Utc):yyyy-MM-dd'T'HH:mm:ss'Z'} reason={reason} enforced={ScheduledRestartOn}");
            CompanionPlugin.FeatureLog($"Server restart scheduled in {minutes} min by {admin}: {reason}" +
                                       (ScheduledRestartOn ? "" : " (EnableScheduledRestart=false: announcements only)"));
            Wave1AuditRpc.PostModLog($"RESTART SCHEDULED in {minutes}m: {reason} (by {admin})");
            AnnounceAll($"Server restart in {minutes} minute{(minutes == 1 ? "" : "s")}: {reason}");
            CompanionPlugin.NotifySender(sender, ScheduledRestartOn
                ? $"Restart scheduled in {minutes} min"
                : $"Restart scheduled in {minutes} min - announcements only (EnableScheduledRestart=false)");
        }

        // Only warnings still in the future are armed; the ones the schedule already passed are marked
        // fired so a 10-minute schedule does not immediately spam T-30 and T-15.
        private static void ArmWarnings(long atTicks)
        {
            FiredWarnings.Clear();
            _armedRestartTicks = atTicks;
            var remaining = (new DateTime(atTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes;
            foreach (var t in RestartWarnMinutes)
                if (t >= remaining) FiredWarnings.Add(t);
        }

        private static void ClearRestart()
        {
            var t = FeatureStore.Table(TblCfg);
            var changed = t.Remove(KeyRestartAt);
            changed |= t.Remove(KeyRestartReason);
            if (changed) FeatureStore.SaveTable(TblCfg);
            FiredWarnings.Clear();
            _armedRestartTicks = 0;
            _restartPhase = RestartPhase.None;
        }

        private static void TickRestart(float now)
        {
            if (_restartPhase == RestartPhase.Quitting)
            {
                // Application.Quit is a request; Unity may sit in shutdown. Hard-exit if it does.
                if (now >= _quitAt)
                {
                    CompanionPlugin.FeatureLog("Restart: Application.Quit did not terminate the process in time; calling Environment.Exit(0).");
                    Environment.Exit(0);
                }
                return;
            }

            if (_restartPhase == RestartPhase.Saving)
            {
                if (ZNet.instance.IsSaving() && now < _saveWaitUntil) return;
                if (!ScheduledRestartOn)
                {
                    CompanionPlugin.FeatureLog("restart suppressed (EnableScheduledRestart=false)");
                    Wave1AuditRpc.PostModLog("RESTART SUPPRESSED (EnableScheduledRestart=false)");
                    _restartPhase = RestartPhase.None;
                    return;
                }
                CompanionPlugin.FeatureLog("Restart: world saved, terminating the server process now (Application.Quit).");
                _restartPhase = RestartPhase.Quitting;
                _quitAt = now + 10f;
                try { Application.Quit(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Application.Quit threw ({e.Message}); Environment.Exit follows."); }
                return;
            }

            if (!FeatureStore.Ready) return;
            var t = FeatureStore.Table(TblCfg);
            string raw;
            long at;
            if (!t.TryGetValue(KeyRestartAt, out raw) || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out at) || at <= 0)
            {
                if (_restartPhase != RestartPhase.None) _restartPhase = RestartPhase.None;
                return;
            }
            var reason = t.TryGetValue(KeyRestartReason, out var r) ? r : "maintenance";
            var remaining = (new DateTime(at, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes;

            // A schedule left behind by a crash (or the restart that already happened) must not fire on
            // boot. Anything more than 5 minutes overdue is stale.
            if (remaining < -5d)
            {
                CompanionPlugin.FeatureLog("Dropping a stale restart schedule (it was already overdue at startup).");
                ClearRestart();
                return;
            }

            if (at != _armedRestartTicks) ArmWarnings(at);   // survives a companion restart mid-countdown
            _restartPhase = RestartPhase.Counting;

            foreach (var step in RestartWarnMinutes)
            {
                if (FiredWarnings.Contains(step) || remaining > step) continue;
                FiredWarnings.Add(step);
                AnnounceAll($"Server restart in {step} minute{(step == 1 ? "" : "s")}: {reason}");
            }

            if (remaining > 0d) return;

            // Due. Clear the schedule BEFORE quitting: a restart loop caused by a persisted past-due
            // schedule would be far worse than a missed restart.
            AnnounceAll("Server is restarting NOW. You will be disconnected - reconnect in a minute.");
            Wave1AuditRpc.PostModLog($"RESTART EXECUTING: {reason}" + (ScheduledRestartOn ? "" : " (suppressed by config)"));
            CompanionPlugin.FeatureLog($"Scheduled restart is due ({reason}). Forcing a world save before shutdown.");
            ClearRestart();
            _restartPhase = RestartPhase.Saving;
            _saveWaitUntil = now + 120f;
            try
            {
                if (!ZNet.instance.IsSaving() && Game.instance != null)
                    ZNet.instance.Save(false, false, true);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Restart: save before shutdown failed ({e.Message}); continuing."); }
        }

        // ==================== autosave override ====================

        // ZPackage: bool on, int minutes.
        private static void OnAutosaveSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvAutosaveSet")) return;

            bool on; int minutes;
            try { on = pkg.ReadBool(); minutes = pkg.ReadInt(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvAutosaveSet: malformed packet dropped ({e.Message})"); return; }
            // Saves can be made rarer or more frequent, never disabled: "off" restores the game default.
            minutes = Mathf.Clamp(minutes, AutosaveMinMinutes, AutosaveMaxMinutes);

            var t = FeatureStore.Table(TblCfg);
            t[KeyAutosaveOn] = on ? "1" : "0";
            t[KeyAutosaveMin] = minutes.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblCfg);
            _autosaveApplied = false;   // re-apply on the next tick

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "AUTOSAVE-SET", $"on={on} minutes={minutes}");
            CompanionPlugin.FeatureLog($"Autosave override {(on ? "ON " + minutes + " min" : "OFF (game default)")} by {admin}");
            Wave1AuditRpc.PostModLog($"AUTOSAVE {(on ? minutes + "m" : "default")} (by {admin})");

            ApplyAutosaveOverride();
            var seconds = AutosaveSeconds();
            CompanionPlugin.NotifySender(sender, seconds > 0f
                ? $"Autosave interval is now {(int)(seconds / 60)} min"
                : "Autosave interval could not be changed on this game build");
        }

        // Game.m_saveInterval is a public static float (Game.cs:127, default 1800 s) consumed by Game.UpdateSaving;
        // it is read directly, the same way CompanionPlugin's next-save countdown reads it.
        private static float AutosaveSeconds() => Game.m_saveInterval;

        private static void ApplyAutosaveOverride()
        {
            if (_autosaveApplied) return;
            if (!FeatureStore.Ready) return;

            try
            {
                if (_defaultSaveIntervalSeconds < 0f) _defaultSaveIntervalSeconds = Game.m_saveInterval;
                var t = FeatureStore.Table(TblCfg);
                var on = t.TryGetValue(KeyAutosaveOn, out var v) && v == "1";
                var minutes = 0;
                if (t.TryGetValue(KeyAutosaveMin, out var m)) int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes);
                minutes = Mathf.Clamp(minutes == 0 ? AutosaveMinMinutes : minutes, AutosaveMinMinutes, AutosaveMaxMinutes);

                var target = on ? minutes * 60f : (_defaultSaveIntervalSeconds > 0f ? _defaultSaveIntervalSeconds : 1800f);
                var current = Game.m_saveInterval;
                if (Mathf.Abs(current - target) > 0.5f)
                {
                    Game.m_saveInterval = target;
                    CompanionPlugin.FeatureLog($"Autosave interval set to {(int)(target / 60)} min" + (on ? " (admin override)" : " (game default)"));
                }
                _autosaveApplied = true;
            }
            catch (Exception e)
            {
                _autosaveApplied = true;   // do not retry-spam a build that will not accept the write
                CompanionPlugin.FeatureLog($"Could not apply the autosave override: {e.Message}");
            }
        }

        // ==================== health (read by the self-test module) ====================

        /// <summary>Backup subsystem health for AP_SrvSelfTestReq. ok=false means an operator must look.</summary>
        internal static (bool ok, string detail) BackupHealth()
        {
            try
            {
                var set = FeatureStore.ResolveSaveLayout();
                var parent = BackupSetParent(set);
                if (parent == null) return (false, "no world loaded, backup directory unresolved");
                var list = ListBackups(parent, set.Chunked);
                var newest = list.Count > 0
                    ? $"newest '{list[0].Name}' {(int)(DateTime.UtcNow - new DateTime(list[0].Ticks, DateTimeKind.Utc)).TotalMinutes} min old"
                    : "no complete sets on disk";
                var head = $"{list.Count} {(set.Chunked ? "chunked" : "legacy")} backup set(s) in {parent}, keep={BackupKeep}, {newest}";

                if (!string.IsNullOrEmpty(_lastError)) return (false, head + "; last error: " + _lastError);
                if (!AutoBackupOn) return (true, "auto-backup OFF (manual only); " + head);
                if (list.Count == 0) return (false, "auto-backup is ON but " + head);
                var ageMin = (DateTime.UtcNow - new DateTime(list[0].Ticks, DateTimeKind.Utc)).TotalMinutes;
                if (ageMin > BackupIntervalMinutes * 2)
                    return (false, $"auto-backup is ON but the newest backup is {(int)ageMin} min old (interval {BackupIntervalMinutes} min); {head}");
                return (true, $"auto-backup every {BackupIntervalMinutes} min; " + head);
            }
            catch (Exception e) { return (false, "backup health check failed: " + e.Message); }
        }

        /// <summary>World-save health for AP_SrvSelfTestReq: cadence, duration and save-set size trend.</summary>
        internal static (bool ok, string detail) SaveHealth()
        {
            try
            {
                var interval = AutosaveSeconds();
                var ms = Interlocked.Read(ref _lastSaveDurationMs);
                var sinceSeconds = _lastSaveEndTicksUtc == 0
                    ? Time.realtimeSinceStartup
                    : (float)(DateTime.UtcNow - new DateTime(_lastSaveEndTicksUtc, DateTimeKind.Utc)).TotalSeconds;
                var head = _lastSaveEndTicksUtc == 0
                    ? $"no save completed yet this session ({(int)(sinceSeconds / 60)} min uptime)"
                    : $"last save {(int)(sinceSeconds / 60)} min ago, took {ms / 1000.0:0.0}s, save set {Mb(_lastSetBytes)}" +
                      (_lastSetGeneration >= 0 ? $" (generation {_lastSetGeneration})" : "");
                head += interval > 0f ? $", autosave every {(int)(interval / 60)} min" : ", autosave interval unknown";

                // A recent warning dominates the verdict; older ones expire so a server that recovered
                // (one slow save during a disk hiccup) stops reporting fail forever.
                var warnAgeMin = _saveWarningTicks == 0
                    ? double.MaxValue
                    : (DateTime.UtcNow - new DateTime(_saveWarningTicks, DateTimeKind.Utc)).TotalMinutes;
                if (!string.IsNullOrEmpty(_saveWarning) && warnAgeMin <= WarnExpiryMinutes)
                    return (false, head + "; " + _saveWarning);
                if (interval > 0f && sinceSeconds > interval * 3f)
                    return (false, head + "; no save has completed for 3x the autosave interval");
                if (ms > SlowSaveMs) return (false, head + "; last save exceeded 20s");
                return (true, head);
            }
            catch (Exception e) { return (false, "save health check failed: " + e.Message); }
        }

        // ==================== shared helpers ====================

        // MAIN THREAD ONLY (FeatureStore's world helpers). The backup ROOT is the configured BackupDir or
        // <worlds root>/adminpanel_backups; a chunked world's sets live one level further down in <root>/<world>/
        // so that the engine's one-level scan of the worlds root (FileHelpers.GetFiles :589-600) never sees a
        // backup's _main.* files as a world of their own. A legacy world's pairs stay directly in the root.
        private static string BackupRoot(FeatureStore.WorldSaveSet set)
        {
            try
            {
                var cfg = _backupDir != null ? (_backupDir.Value ?? "").Trim() : "";
                if (cfg.Length > 0) return Path.GetFullPath(cfg);
                return set == null || set.WorldsRoot == null ? null : Path.Combine(set.WorldsRoot, BackupRootName);
            }
            catch (Exception) { return null; }
        }

        private static string BackupSetParent(FeatureStore.WorldSaveSet set)
        {
            if (set == null) return null;
            var root = BackupRoot(set);
            if (root == null) return null;
            // Every layout is namespaced per world. A flat root for legacy pairs let two legacy worlds sharing one
            // BackupDir prune and stage each other's sets (a set is only ever listed for the world folder it sits in).
            return Path.Combine(root, set.WorldName);
        }

        // Mirror the backup settings into "srvcfg" so the panel and sibling modules can read one place.
        // Config remains the source of truth (no RPC changes these).
        private static void MirrorConfigToStore()
        {
            if (_cfgMirrored || !FeatureStore.Ready) return;
            _cfgMirrored = true;
            var t = FeatureStore.Table(TblCfg);
            t[KeyAutoBackupOn] = AutoBackupOn ? "1" : "0";
            t[KeyBackupInterval] = BackupIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            t[KeyBackupKeep] = BackupKeep.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblCfg);
        }

        /// <summary>Countdown / cancellation text to EVERY connected player, unmodded clients included.</summary>
        private static void AnnounceAll(string text)
        {
            CompanionPlugin.FeatureLog("Announce: " + text);
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null) continue;
                    Wave1Moderation.SendPlayerText(peer.m_uid, text);   // vanilla "ShowMessage"
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Restart announcement failed: {e.Message}"); }
        }

        // A backup NAME, never a path: any separator, drive colon, wildcard or ".." is rejected outright
        // (the caller then re-verifies the resolved path against the backup directory).
        private static string CleanFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length == 0 || s.Length > MaxNameLen) return "";
            if (s.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.LastIndexOf('.'));
            if (s.Length == 0) return "";
            if (s.IndexOf("..", StringComparison.Ordinal) >= 0) return "";
            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0 || s.IndexOf(':') >= 0) return "";
            if (s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0 || s.IndexOf('"') >= 0) return "";
            foreach (var c in Path.GetInvalidFileNameChars())
                if (s.IndexOf(c) >= 0) return "";
            return s;
        }

        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MaxReasonLen ? s.Substring(0, MaxReasonLen) : s;
        }

        private static string Trim(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) : s);

        private static string Mb(long bytes) =>
            (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}

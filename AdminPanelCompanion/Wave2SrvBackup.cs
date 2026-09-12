using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 2 — backups, staged restore, restarts, autosave, save integrity ====
    // Everything in this file touches the one thing a server owner cannot re-create: the world file.
    // The rules that shape every line below (spec-valheim-api.md §5, spec-companion.md §5.5/§8):
    //
    //  * NEVER copy .db/.fwl while ZNet.IsSaving() — SaveWorldThread renames the file underneath us
    //    (ZNet.cs:1658 FileHelpers.ReplaceOldFile). The only safe window is after a completed save.
    //  * The completion signal is a postfix on ZNet.SaveWorldThread; ZNet.WorldSaveFinished fires from
    //    PrintWorldSaveMessage which dereferences MessageHud.instance FIRST — null on headless, so on the
    //    exact machine this targets it may never fire (ZNet.cs:1542-1556).
    //  * That postfix runs on the SAVE WORKER THREAD: it may only set atomics. Every path resolution
    //    (World.GetDBPath() -> Utils.GetSaveDataPath -> Application.persistentDataPath) is a Unity call and
    //    is therefore done on the main thread from Tick, never in the postfix.
    //  * A .db without its .fwl is not a restorable world: both files are copied, and a pair is only ever
    //    listed/pruned/restored as a pair. Copies land as .tmp then File.Move, so a half-written file can
    //    never appear in a listing.
    //  * The byte copy itself runs on a WORKER THREAD. A mature world .db is hundreds of megabytes; copying
    //    it inline froze the simulation for seconds on every interval. Everything Unity/ZNet-backed (the
    //    paths, the backup directory, the "no save is in flight" decision) is resolved on the main thread
    //    BEFORE the worker starts; the worker does pure System.IO and publishes its result through a
    //    volatile flag that Tick picks up. Retention pruning is file IO too and runs in the same worker.
    //  * A running server cannot swap its own live world file, so "restore" is staged and applied at the
    //    next process start, and the current files are moved aside (never deleted).
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

        // ---- wire caps (contract: AP_BackupData ships at most 30 pairs, newest first) ----
        private const int BackupShipCap = 30;
        private const int MaxNameLen = 128;
        private const int MaxReasonLen = 200;
        private const int MaxRestartMinutes = 10080;   // one week; longer is a calendar, not a countdown

        // ---- tuning ----
        private const double FreeSpaceFactor = 3d;      // refuse a backup under 3x the .db size free
        private const float SaveWaitSeconds = 300f;     // give up waiting for a save to finish
        private const long SlowSaveMs = 20000;          // integrity warning threshold
        private const double ShrinkRatio = 0.75d;       // .db smaller than 75% of the previous one
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

        // ---- save observation. The two volatiles below are written on the SAVE WORKER THREAD. ----
        private static long _saveThreadStartTicks;       // Interlocked
        private static long _saveCompletions;            // Interlocked counter
        private static long _lastSaveDurationMs;         // Interlocked
        private static long _processedCompletions;       // main-thread cursor into _saveCompletions
        private static volatile bool _saveSignal;        // "a save finished since we started waiting"

        private static long _lastSaveEndTicksUtc;
        private static long _lastDbBytes;
        private static long _prevDbBytes;
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
        private static FieldInfo _saveIntervalField;
        private static bool _saveIntervalProbed;
        private static float _defaultSaveIntervalSeconds = -1f;
        private static bool _autosaveApplied;

        // ---- staged restore ----
        private static string _restoreApplied;           // set by Init, reported once the store is readable
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
                    "Copy the world .db + .fwl into BackupDir on a timer. OFF by default: it writes to disk on a schedule. Manual backups from the panel work regardless of this setting.");
                _backupIntervalMinutes = cfg.Bind("Features", "BackupIntervalMinutes", 60,
                    "Minutes between automatic backups (minimum 10). Each backup forces a world save first and copies only after it completes.");
                _backupKeep = cfg.Bind("Features", "BackupKeep", 10,
                    "How many complete .db+.fwl backup pairs to keep. Older pairs are pruned oldest-first (minimum 1).");
                _backupDir = cfg.Bind("Features", "BackupDir", "",
                    "Absolute directory for backups. Empty = <world folder>/adminpanel_backups.");
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
        // No ZNet/Unity/World access here (World.GetDBPath() reaches Application.persistentDataPath).
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

        // Main thread: pick up whatever the worker recorded, measure the resulting .db, raise warnings.
        private static void ObserveSaves()
        {
            var done = Interlocked.Read(ref _saveCompletions);
            if (done != _processedCompletions)
            {
                _processedCompletions = done;
                _lastSaveEndTicksUtc = DateTime.UtcNow.Ticks;
                var wp = ResolveWorldPaths();
                if (wp != null)
                {
                    try
                    {
                        if (File.Exists(wp.Db))
                        {
                            var size = new FileInfo(wp.Db).Length;
                            _prevDbBytes = _lastDbBytes;
                            _lastDbBytes = size;
                        }
                    }
                    catch (Exception) { }
                }

                var ms = Interlocked.Read(ref _lastSaveDurationMs);
                if (ms > SlowSaveMs)
                    RaiseSaveWarning("slow", $"World save took {ms / 1000.0:0.0}s (over {SlowSaveMs / 1000}s). Disk or storage backend may be struggling.");
                if (_prevDbBytes > 0 && _lastDbBytes > 0 && _lastDbBytes < (long)(_prevDbBytes * ShrinkRatio))
                    RaiseSaveWarning("shrink", $"World .db shrank from {_prevDbBytes / 1024} KB to {_lastDbBytes / 1024} KB in one save. Check for mass object loss BEFORE the next save overwrites it.");
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

        private static void SendBackupData(long sender)
        {
            var pkg = new ZPackage();
            pkg.Write(1);   // payload version — bump, never reorder

            var list = ListBackups(ResolveBackupDir());
            var shipped = Math.Min(list.Count, BackupShipCap);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++)
            {
                pkg.Write(list[i].Name);     // base name, no extension: the pair is <name>.db + <name>.fwl
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

            CompanionPlugin.SrvAudit(sender, "BACKUP-NOW", $"dir={ResolveBackupDir() ?? "?"}");
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
                // the main thread. Copying between those two points is still unsafe.
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

        // Recover "when did we last back up" from the newest pair ON DISK, once per world. Without this a
        // server that restarts often would take a fresh backup on every boot.
        private static bool SeedLastBackupTime()
        {
            if (_backupSeeded) return true;
            var dir = ResolveBackupDir();
            if (dir == null) return false;   // no world yet — try again next tick
            var list = ListBackups(dir);
            _lastBackupTicksUtc = list.Count > 0 ? list[0].Ticks : DateTime.UtcNow.Ticks;
            _backupSeeded = true;
            return true;
        }

        private static bool PreflightBackup(out string why)
        {
            why = "";
            var wp = ResolveWorldPaths();
            if (wp == null) { why = "no world is loaded"; return false; }
            if (wp.IsCloud)
            {
                why = $"the world is stored on a cloud save ({wp.Source}); a file copy cannot back it up safely";
                return false;
            }
            if (!File.Exists(wp.Db) || !File.Exists(wp.Fwl))
            {
                why = "the world .db/.fwl are not both present on disk yet";
                return false;
            }
            var dir = ResolveBackupDir();
            if (dir == null) { why = "the backup directory could not be resolved"; return false; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { why = "cannot create the backup directory: " + e.Message; return false; }

            long dbSize;
            try { dbSize = new FileInfo(wp.Db).Length; }
            catch (Exception e) { why = "cannot read the world .db: " + e.Message; return false; }

            // Free-space guard. If the drive cannot be queried (exotic mount, permissions) we proceed
            // rather than block backups forever — the copy itself will fail loudly if the disk is full.
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (!string.IsNullOrEmpty(root))
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < (long)(dbSize * FreeSpaceFactor))
                    {
                        why = $"only {free / 1048576} MB free on {root}, need {(long)(dbSize * FreeSpaceFactor) / 1048576} MB ({FreeSpaceFactor}x the world size)";
                        return false;
                    }
                }
            }
            catch (Exception) { }
            return true;
        }

        // MAIN THREAD. Resolves everything that needs Unity/ZNet/config — the world paths, the backup
        // directory, the unique name and the "no save is in flight" decision — and then hands the byte copy
        // to a worker. Returns false when the copy could not even be started (_lastError says why).
        private static bool BeginCopy()
        {
            var wp = ResolveWorldPaths();
            var dir = ResolveBackupDir();
            if (wp == null || dir == null) { _lastError = "no world / backup directory at copy time"; return false; }
            if (ZNet.instance.IsSaving()) { _lastError = "a save started again before the copy could run"; return false; }
            if (_copyBusy) { _lastError = "the previous backup copy is still running"; return false; }

            string baseName;
            try
            {
                Directory.CreateDirectory(dir);
                baseName = UniqueBackupName(dir, wp.FileName);
            }
            catch (Exception e) { _lastError = "cannot prepare the backup directory: " + e.Message; return false; }

            var srcDb = wp.Db;
            var srcFwl = wp.Fwl;
            var keep = BackupKeep;   // a ConfigEntry read: taken here so the worker touches nothing but System.IO
            _copyBusy = true;
            _copyDone = false;
            _copyOk = false;
            _copyError = "";
            _copyModLog = "";
            _copyLog = null;
            try { Task.Run(() => CopyWorker(srcDb, srcFwl, dir, baseName, keep)); }
            catch (Exception e)
            {
                _copyBusy = false;
                _lastError = "could not start the copy worker: " + e.Message;
                return false;
            }
            return true;
        }

        // WORKER THREAD. Pure System.IO: no Unity, no ZNet, no FeatureStore, no logging. Copies to .tmp then
        // File.Move so a half-copied file is never listed or restored, and publishes the outcome through
        // _copyDone for TickBackup to report.
        private static void CopyWorker(string srcDb, string srcFwl, string dir, string baseName, int keep)
        {
            var log = new List<string>();
            var ok = false;
            var err = "";
            var modLog = "";
            string tmpDb = null, tmpFwl = null;
            try
            {
                var db = Path.Combine(dir, baseName + ".db");
                var fwl = Path.Combine(dir, baseName + ".fwl");
                tmpDb = db + ".tmp";
                tmpFwl = fwl + ".tmp";

                CopySnapshot(srcDb, tmpDb);
                CopySnapshot(srcFwl, tmpFwl);
                File.Move(tmpDb, db);
                tmpDb = null;
                File.Move(tmpFwl, fwl);
                tmpFwl = null;

                var bytes = 0L;
                try { bytes = new FileInfo(db).Length + new FileInfo(fwl).Length; } catch (Exception) { }
                log.Add($"Backup written: {baseName} ({bytes / 1024} KB) in {dir}");
                modLog = $"BACKUP {baseName} ({bytes / 1024} KB)";
                PruneBackups(dir, keep, log);
                ok = true;
            }
            catch (Exception e)
            {
                err = "copy failed: " + e.Message;
                log.Add("Backup " + err);
            }
            finally
            {
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
        // the game may finish a save, and SaveWorldThread swaps the live .db in by RENAMING it aside
        // (FileHelpers.ReplaceOldFile). File.Copy holds the source without FileShare.Delete, so that rename
        // would fail on the save thread — a backup must never be able to break a save. With these flags the
        // save proceeds untouched and this handle keeps reading the complete pre-save file it opened, which
        // is exactly the snapshot a backup wants.
        private static void CopySnapshot(string src, string dst)
        {
            const int Buf = 1 << 16;
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Buf))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, Buf))
                input.CopyTo(output, Buf);
        }

        private static string UniqueBackupName(string dir, string worldFile)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = worldFile + "-" + stamp;
            var n = 2;
            while (File.Exists(Path.Combine(dir, name + ".db")) && n < 100)
                name = worldFile + "-" + stamp + "-" + n++;
            return name;
        }

        // Retention counts COMPLETE pairs only; a lone .db is never counted and never deletes a good pair.
        // WORKER THREAD (called from CopyWorker): it is file IO like the copy, so `keep` arrives pre-read and
        // log lines are collected instead of emitted.
        private static void PruneBackups(string dir, int keep, List<string> log)
        {
            try
            {
                var list = ListBackups(dir);
                for (var i = keep; i < list.Count; i++)
                {
                    try
                    {
                        File.Delete(Path.Combine(dir, list[i].Name + ".db"));
                        File.Delete(Path.Combine(dir, list[i].Name + ".fwl"));
                        log.Add($"Pruned old backup {list[i].Name} (keep={keep})");
                    }
                    catch (Exception e) { log.Add($"Could not prune backup {list[i].Name}: {e.Message}"); }
                }
                // Abandoned half-copies from a crash mid-backup.
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var tmp in Directory.GetFiles(dir, "*.tmp"))
                {
                    try { if (File.GetLastWriteTimeUtc(tmp) < cutoff) File.Delete(tmp); }
                    catch (Exception) { }
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

        private static List<BackupEntry> ListBackups(string dir)
        {
            var res = new List<BackupEntry>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return res;
            try
            {
                foreach (var db in Directory.GetFiles(dir, "*.db"))
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
            catch (Exception) { }
            res.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));   // newest first
            return res;
        }

        // ==================== staged restore ====================

        // The name arrives from the network: it is a FILE NAME, never a path. Anything with a separator or
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

            var dir = ResolveBackupDir();
            var wp = ResolveWorldPaths();
            if (dir == null || wp == null)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: no world / backup directory");
                return;
            }
            if (wp.IsCloud)
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: this world is on a cloud save and cannot be file-restored");
                CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=cloud-save");
                return;
            }

            string srcDb, srcFwl;
            if (!ResolveInsideBackupDir(dir, name, out srcDb, out srcFwl))
            {
                CompanionPlugin.NotifySender(sender, "Restore refused: that backup pair does not exist in the backup folder");
                CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=not-found-or-outside-dir");
                return;
            }

            var stagedDb = wp.Db + ".aprestore";
            var stagedFwl = wp.Fwl + ".aprestore";
            try
            {
                File.Copy(srcDb, stagedDb + ".tmp", true);
                File.Copy(srcFwl, stagedFwl + ".tmp", true);
                if (File.Exists(stagedDb)) File.Delete(stagedDb);
                if (File.Exists(stagedFwl)) File.Delete(stagedFwl);
                File.Move(stagedDb + ".tmp", stagedDb);
                File.Move(stagedFwl + ".tmp", stagedFwl);
            }
            catch (Exception e)
            {
                try { if (File.Exists(stagedDb + ".tmp")) File.Delete(stagedDb + ".tmp"); } catch (Exception) { }
                try { if (File.Exists(stagedFwl + ".tmp")) File.Delete(stagedFwl + ".tmp"); } catch (Exception) { }
                CompanionPlugin.FeatureLog($"Restore staging failed for {name}: {e.Message}");
                CompanionPlugin.NotifySender(sender, "Restore staging failed: " + e.Message);
                CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} result=copy-failed detail={e.Message}");
                return;
            }

            WriteRestoreMarker(wp, name);
            var t = FeatureStore.Table(TblCfg);
            t[KeyRestoreStaged] = name + "|" + DateTime.UtcNow.Ticks;
            FeatureStore.SaveTable(TblCfg);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "BACKUP-STAGE", $"name={name} world={wp.FileName} result=staged");
            CompanionPlugin.FeatureLog($"RESTORE STAGED: {name} -> {wp.FileName}. It will be applied on the NEXT server start (staged by {admin}).");
            Wave1AuditRpc.PostModLog($"RESTORE STAGED {name} for world {wp.FileName} (by {admin}) - applies on next server start");
            Wave1Moderation.NotifyOnlineAdmins($"Restore staged: {name}. A server restart is required to apply it.");
            CompanionPlugin.NotifySender(sender,
                $"Backup '{name}' is staged. A running server cannot swap its own world file - RESTART the server to apply it. The current world will be kept as .prerestore files.");
        }

        // Validate + resolve a network-supplied backup name strictly inside the backup directory.
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
        // world exists (FeatureStore keys its directory off the loaded world's name).
        private static void WriteRestoreMarker(WorldPaths wp, string backupName)
        {
            var path = RestoreMarkerPath();
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path,
                    "world=" + wp.FileName + "\r\n" +
                    "dir=" + wp.Dir + "\r\n" +
                    "backup=" + backupName + "\r\n" +
                    "ticks=" + DateTime.UtcNow.Ticks + "\r\n");
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Could not write the restore marker: {e.Message}"); }
        }

        // Called from Init, before the game opens the world. Moves the CURRENT pair aside (never deletes it)
        // and swaps the staged pair in. Any failure leaves the world exactly as it was.
        private static void ApplyPendingRestore()
        {
            var marker = RestoreMarkerPath();
            if (marker == null || !File.Exists(marker)) return;

            string world = null, dir = null, backup = null;
            foreach (var line in File.ReadAllLines(marker))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (k == "world") world = v;
                else if (k == "dir") dir = v;
                else if (k == "backup") backup = v;
            }

            if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(dir))
            {
                CompanionPlugin.FeatureLog("Restore marker is unreadable; ignoring and removing it.");
                TryDelete(marker);
                return;
            }

            var liveDb = Path.Combine(dir, world + ".db");
            var liveFwl = Path.Combine(dir, world + ".fwl");
            var stagedDb = liveDb + ".aprestore";
            var stagedFwl = liveFwl + ".aprestore";

            if (!File.Exists(stagedDb) || !File.Exists(stagedFwl))
            {
                CompanionPlugin.FeatureLog($"Restore marker for '{backup}' found but the staged files are missing; nothing was changed.");
                TryDelete(marker);
                return;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
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
                CompanionPlugin.FeatureLog($"!!! RESTORE FAILED for '{backup}': {e.Message}. The original world was left in place; staged files kept for a retry.");
                return;
            }

            TryDelete(marker);
            _restoreApplied = backup ?? "(unnamed)";
            CompanionPlugin.FeatureLog("==================================================================");
            CompanionPlugin.FeatureLog($"RESTORE APPLIED: world '{world}' was replaced with backup '{_restoreApplied}'.");
            CompanionPlugin.FeatureLog($"The world as it was before this boot is kept at: {keptDb} / {keptFwl}");
            CompanionPlugin.FeatureLog("==================================================================");
        }

        // The store is only readable once a world is loaded, so the mod-log entry and marker cleanup for an
        // applied restore happen on the first tick that has a store.
        private static void ReportRestoreOnce()
        {
            if (_restoreReported || _restoreApplied == null) return;
            if (!FeatureStore.Ready) return;
            _restoreReported = true;
            var t = FeatureStore.Table(TblCfg);
            if (t.ContainsKey(KeyRestoreStaged))
            {
                t.Remove(KeyRestoreStaged);
                FeatureStore.SaveTable(TblCfg);
            }
            Wave1AuditRpc.PostModLog($"RESTORE APPLIED at startup: {_restoreApplied} (previous world kept as .prerestore files)");
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

        // The interval field is NOT on ZNet in the current build — it is the static Game.m_saveInterval
        // (Game.cs:126, default 1800s) consumed by Game.UpdateSaving. Probed reflectively (ZNet first, as
        // the task's decompile hint suggests, then Game) so a move between the two degrades to "unavailable"
        // instead of throwing.
        private static FieldInfo SaveIntervalField()
        {
            if (_saveIntervalProbed) return _saveIntervalField;
            _saveIntervalProbed = true;
            try
            {
                var f = AccessTools.Field(typeof(ZNet), "m_saveInterval") ?? AccessTools.Field(typeof(Game), "m_saveInterval");
                if (f != null && f.FieldType == typeof(float)) _saveIntervalField = f;
                else CompanionPlugin.FeatureLog("Autosave interval field not found on this game build: autosave override disabled (saves keep the game's own cadence).");
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Autosave interval probe failed: {e.Message}"); }
            return _saveIntervalField;
        }

        private static object SaveIntervalTarget(FieldInfo f)
        {
            if (f == null || f.IsStatic) return null;
            if (f.DeclaringType == typeof(ZNet)) return ZNet.instance;
            return Game.instance;
        }

        private static float AutosaveSeconds()
        {
            var f = SaveIntervalField();
            if (f == null) return -1f;
            try
            {
                if (!f.IsStatic && SaveIntervalTarget(f) == null) return -1f;
                return (float)f.GetValue(SaveIntervalTarget(f));
            }
            catch (Exception) { return -1f; }
        }

        private static void ApplyAutosaveOverride()
        {
            if (_autosaveApplied) return;
            if (!FeatureStore.Ready) return;
            var f = SaveIntervalField();
            if (f == null) { _autosaveApplied = true; return; }
            if (!f.IsStatic && SaveIntervalTarget(f) == null) return;   // Game not up yet; retry next tick

            try
            {
                if (_defaultSaveIntervalSeconds < 0f) _defaultSaveIntervalSeconds = (float)f.GetValue(SaveIntervalTarget(f));
                var t = FeatureStore.Table(TblCfg);
                var on = t.TryGetValue(KeyAutosaveOn, out var v) && v == "1";
                var minutes = 0;
                if (t.TryGetValue(KeyAutosaveMin, out var m)) int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes);
                minutes = Mathf.Clamp(minutes == 0 ? AutosaveMinMinutes : minutes, AutosaveMinMinutes, AutosaveMaxMinutes);

                var target = on ? minutes * 60f : (_defaultSaveIntervalSeconds > 0f ? _defaultSaveIntervalSeconds : 1800f);
                var current = (float)f.GetValue(SaveIntervalTarget(f));
                if (Mathf.Abs(current - target) > 0.5f)
                {
                    f.SetValue(SaveIntervalTarget(f), target);
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
                var dir = ResolveBackupDir();
                if (dir == null) return (false, "no world loaded, backup directory unresolved");
                var list = ListBackups(dir);
                var newest = list.Count > 0
                    ? $"newest '{list[0].Name}' {(int)(DateTime.UtcNow - new DateTime(list[0].Ticks, DateTimeKind.Utc)).TotalMinutes} min old"
                    : "no complete pairs on disk";
                var head = $"{list.Count} backup(s) in {dir}, keep={BackupKeep}, {newest}";

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

        /// <summary>World-save health for AP_SrvSelfTestReq: cadence, duration and .db size trend.</summary>
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
                    : $"last save {(int)(sinceSeconds / 60)} min ago, took {ms / 1000.0:0.0}s, db {_lastDbBytes / 1024} KB";
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

        private sealed class WorldPaths
        {
            public string Db;
            public string Fwl;
            public string Dir;
            public string FileName;
            public string Source;
            public bool IsCloud;
        }

        // MAIN THREAD ONLY: World.GetDBPath() reaches Utils.GetSaveDataPath -> Application.persistentDataPath.
        // Reflection throughout (same discipline as FeatureStore) so a renamed member degrades this feature
        // instead of throwing inside an RPC handler. m_fileSource is compared BY NAME: hard-coding the
        // FileHelpers.FileSource enum values would break silently if the game reorders them.
        private static WorldPaths ResolveWorldPaths()
        {
            try
            {
                var w = AccessTools.Property(typeof(ZNet), "World")?.GetValue(null)
                        ?? AccessTools.Field(typeof(ZNet), "m_world")?.GetValue(null);
                if (w == null) return null;
                var db = AccessTools.Method(w.GetType(), "GetDBPath", Type.EmptyTypes)?.Invoke(w, null) as string;
                var fwl = AccessTools.Method(w.GetType(), "GetMetaPath", Type.EmptyTypes)?.Invoke(w, null) as string;
                var name = AccessTools.Field(w.GetType(), "m_fileName")?.GetValue(w) as string;
                if (string.IsNullOrEmpty(db) || string.IsNullOrEmpty(fwl) || string.IsNullOrEmpty(name)) return null;
                var src = AccessTools.Field(w.GetType(), "m_fileSource")?.GetValue(w);
                var srcName = src != null ? src.ToString() : "";
                var full = Path.GetFullPath(db);
                return new WorldPaths
                {
                    Db = full,
                    Fwl = Path.GetFullPath(fwl),
                    Dir = Path.GetDirectoryName(full),
                    FileName = name,
                    Source = srcName,
                    IsCloud = srcName.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0,
                };
            }
            catch (Exception) { return null; }
        }

        private static string ResolveBackupDir()
        {
            try
            {
                var cfg = _backupDir != null ? (_backupDir.Value ?? "").Trim() : "";
                if (cfg.Length > 0) return Path.GetFullPath(cfg);
                var wp = ResolveWorldPaths();
                return wp == null ? null : Path.Combine(wp.Dir, "adminpanel_backups");
            }
            catch (Exception) { return null; }
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}

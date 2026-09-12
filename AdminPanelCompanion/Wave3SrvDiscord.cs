using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    // ==================== Wave 3 — Discord extras (alerts / leaderboards / account linking) ====================
    // Sits ON TOP of the wave-3 Discord core (Wave34Core, sibling file Wave34SrvCore.cs), which owns the feed
    // webhook, its outgoing queue and the counters. This file adds the three things that are NOT a live event
    // feed:
    //
    //   1. ADMIN ALERTS   — operational messages an owner wants pinged about, not buried in a busy feed:
    //                       server started, scheduled restart announced/cancelled, low disk, and an
    //                       unclean-shutdown watchdog. Own webhook URL (so they can go to a private channel)
    //                       with a documented fallback to the feed webhook.
    //   2. LEADERBOARDS   — a periodic playtime/deaths digest built from the wave-1 "presence" table and the
    //                       wave-3/4 "deaths" table, read STRAIGHT FROM FeatureStore (no cross-module calls:
    //                       the table layouts are the contract, exactly like Wave2Ops reads "srvcfg").
    //   3. ACCOUNT LINK   — "!link" mints a short-lived code; a Discord bot later redeems it through
    //                       TryConsumeLinkCode. See the WEBHOOKS ARE ONE-WAY note below — this half of the
    //                       handshake is all a webhook-only server can ever do on its own.
    //
    // WEBHOOKS ARE ONE-WAY (read this before promising anything to a user):
    //   A Discord webhook is an OUTGOING HTTP endpoint. The game server can POST to it; Discord can never
    //   push anything back. There is no inbound channel, no polling endpoint, no gateway connection here —
    //   the mod ships no dependencies and opens no listening socket. Therefore:
    //     * every feature in this file that "talks to Discord" is a one-way announcement, and
    //     * account linking CANNOT be completed by this file alone. "!link" produces a code and parks it in
    //       the "linkcodes" table; something with a real Discord connection (the Ikarus bot) must call
    //       Wave3Discord.TryConsumeLinkCode(code, discordUserId, out platformId) through a future in-process
    //       bridge to finish the link. Until that bridge exists, "links" only ever gets rows via that method
    //       being called by other server-side code — never by anything arriving from Discord.
    //
    // Design rules obeyed (spec-companion.md, spec-valheim-api.md §9):
    //   * A dedicated server has NO GameObjects: nothing here touches MessageHud/Chat/Player. Player-facing
    //     text goes out through Wave1Moderation.SendPlayerText ("ShowMessage"), which lands on UNMODDED
    //     clients too.
    //   * ONE Harmony class per target method, each applied in its own try/catch with a named warning and a
    //     Wave2Ops.ReportPatch line, so a game update degrades one capability instead of killing the server.
    //   * Every behaviour-changing feature here defaults OFF (all three config toggles are false).
    //   * HTTP is the PostModLog recipe verbatim: HttpWebRequest on Task.Run, Tls12 OR'd in once, 10 s
    //     timeouts, every exception swallowed, manual JSON escaping (net48 + no dependencies).
    //
    // DEPENDENCY ON Wave34Core (sibling file, same assembly):
    //   * Enqueue(title, description, color) — direct call, funnelled through PostEmbed() so a signature
    //     change is a one-line fix.
    //   * FeedState() — its RETURN SHAPE is not part of the contract handed to this file, so it is read
    //     REFLECTIVELY (same reasoning Wave2Ops documents for Wave2Backup.BackupHealth). Any of: a bool, a
    //     string, or an object exposing feed/queued/sent/failed/lastError/lastSent members is understood; an
    //     absent or unrecognised shape degrades AP_DiscordState to zeros plus an explanatory lastError, never
    //     to an exception.
    //   * OnDeath / OnGlobalKeyAdded / HasMod are deliberately NOT consumed here — the live feed owns them.
    internal static class Wave3Discord
    {
        // ---- store tables (shared contract; see the wave-3/4 table list) ----
        private const string TblDsc = "dsc";            // our counters/settings: alert_restart_at, lb_last, boot_count
        private const string TblLinks = "links";        // platformId -> "discordId|linkedTicks"
        private const string TblLinkCodes = "linkcodes";// code       -> "platformId|expiryTicks"
        private const string TblSrvCfg = "srvcfg";      // sibling-owned; restart_at/restart_reason read-only here
        private const string TblPresence = "presence";  // wave-1 owned; id -> "first|last|sessions|totalSeconds|lastName"
        private const string TblDeaths = "deaths";      // wave-3/4 owned; ticks -> "id|name|x|y|z|hasTomb"

        private const string KeyRestartAt = "restart_at";           // sibling-owned, read-only
        private const string KeyRestartReason = "restart_reason";   // sibling-owned, read-only
        private const string KeyAlertRestartAt = "alert_restart_at";// ours: last restart_at we announced
        private const string KeyLbLast = "lb_last";                 // ours: ticks of the last leaderboard post

        // ---- watchdog files (plain files, not tables: they must survive a process that never got to save) ----
        private const string HeartbeatFile = "heartbeat.dat";
        private const string ShutdownFile = "shutdown.dat";

        // ---- limits ----
        private const long LowDiskBytes = 1L * 1024L * 1024L * 1024L;   // 1 GB, same threshold Wave2Ops alerts on
        private const float HeartbeatSeconds = 60f;
        private const float SlowTickSeconds = 30f;
        private const float DiskAlertSeconds = 3600f;   // "once per hour" per the feature brief
        private const int LeaderboardRows = 10;
        private const int MaxDescLen = 1800;            // Discord allows 4096 in an embed description; stay well clear
        private const int MaxNameLen = 24;
        private const int DeathScanCap = 50000;         // hard stop so a pathological table cannot stall a frame
        private const int LinkCodeLen = 6;
        private const double LinkCodeMinutes = 15d;
        private const int LinkCodeAttempts = 8;

        // Embed colours (Discord takes a plain 24-bit int).
        private const int ColorInfo = 0x3498DB;   // blue   — informational
        private const int ColorGood = 0x2ECC71;   // green  — server up / test ok
        private const int ColorWarn = 0xE67E22;   // orange — restart announced
        private const int ColorBad = 0xE74C3C;    // red    — low disk / crash watchdog

        // Confusable characters removed on purpose: a code is read off a screen and typed into Discord.
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no I, L, O, 0, 1

        // ---- config ----
        private static ConfigEntry<bool> _alertsCfg;
        private static ConfigEntry<string> _alertUrlCfg;
        private static ConfigEntry<bool> _mentionCfg;
        private static ConfigEntry<bool> _leaderboardCfg;
        private static ConfigEntry<int> _leaderboardHoursCfg;
        private static ConfigEntry<bool> _linkCfg;

        internal static bool AlertsOn => _alertsCfg != null && _alertsCfg.Value;
        internal static bool LeaderboardOn => _leaderboardCfg != null && _leaderboardCfg.Value;
        internal static bool LinkOn => _linkCfg != null && _linkCfg.Value;
        private static bool MentionEveryone => _mentionCfg != null && _mentionCfg.Value;
        private static int LeaderboardHours => _leaderboardHoursCfg != null
            ? Mathf.Clamp(_leaderboardHoursCfg.Value, 1, 720)
            : 24;

        private static string AlertUrl
        {
            get
            {
                var u = _alertUrlCfg != null ? _alertUrlCfg.Value : null;
                if (string.IsNullOrEmpty(u)) return null;
                u = u.Trim();
                return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : null;
            }
        }

        // ---- state ----
        private static bool _inited;
        private static bool _booted;              // boot alerts + watchdog evaluation happen exactly once
        private static bool _cleanMarkerWritten;  // Shutdown postfix / quitting / ProcessExit all race here
        private static float _nextHeartbeat;
        private static float _nextSlowTick;
        private static float _nextDiskAlert;
        private static readonly System.Random Rng = new System.Random();

        // Counters. Alerts posted through our OWN webhook complete on a thread-pool task, so every counter is
        // touched with Interlocked and the error string is a plain reference assignment (atomic on all targets).
        private static int _alertsSent;
        private static int _alertsFailed;
        private static long _lastAlertTicksUtc;
        private static volatile string _lastAlertError = "";

        // Cached reflection handle for the sibling's FeedState(); resolved once, never re-probed.
        private static bool _feedProbed;
        private static MethodInfo _feedStateMi;

        private static int _tlsPrepared;   // Interlocked flag: ServicePointManager.SecurityProtocol is process-global

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _alertsCfg = cfg.Bind("Features", "EnableDiscordAlerts", false,
                    "Post operational alerts (server started, scheduled restart announced/cancelled, low disk, unclean shutdown detected) to Discord. Off by default: it sends data to an external service.");
                _alertUrlCfg = cfg.Bind("Features", "DiscordAlertWebhookUrl", "",
                    "Webhook alerts are POSTed to. Empty = fall back to the Discord feed webhook. Set this to send alerts to a private admin channel instead of the public feed.");
                _mentionCfg = cfg.Bind("Features", "AlertMentionEveryone", false,
                    "Prefix alerts with @everyone. Only actually PINGS when DiscordAlertWebhookUrl is set (the mention has to ride in the message content); with the feed fallback the mention is plain text inside an embed and pings nobody.");
                _leaderboardCfg = cfg.Bind("Features", "EnableDiscordLeaderboard", false,
                    "Periodically post a playtime/deaths leaderboard to the Discord feed. Off by default: it publishes player names to an external service.");
                _leaderboardHoursCfg = cfg.Bind("Features", "LeaderboardHours", 24,
                    "Hours between leaderboard posts (1-720). The schedule is persisted, so restarting the server does not re-post the same leaderboard.");
                _linkCfg = cfg.Bind("Features", "EnableDiscordLink", false,
                    "Enable the '!link' / '!unlink' chat commands that mint a Discord account-link code. Off by default. NOTE: completing a link needs a Discord BOT to call back into the server; a webhook alone cannot receive anything.");
            }

            // AP_SrvDiscordTest posts to an external service on demand -> audited, owner-only (null grant),
            // same tier as every other server-configuration action. AP_SrvDiscordStateReq is a panel poll and
            // is deliberately NOT registered: an audited poll drowns audit.log (wave-2 lesson).
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDiscordTest", null);

            ApplyPatch("Wave3DiscordRpcRegistration", typeof(Wave3DiscordRpcRegistration),
                "Discord state/test RPCs unavailable");
            ApplyPatch("Wave3ShutdownMarkerPatch", typeof(Wave3ShutdownMarkerPatch),
                "clean-shutdown marker unavailable — a normal shutdown may be reported as a crash");

            // Belt and braces for the clean-shutdown marker. ZNet.Shutdown is the primary hook (verified
            // present: public void Shutdown(bool save = true), ZNet.decompiled.cs:541). Application.quitting
            // covers the scheduled-restart path (Wave2Backup calls Application.Quit), and ProcessExit covers
            // its Environment.Exit(0) fallback. IF ALL THREE FAIL TO FIRE — SIGKILL, a hard crash, a host
            // power-cut, or a game update that renames Shutdown while Unity's events are also unavailable —
            // the marker is simply not written and the next boot reports "previous session ended
            // unexpectedly". That is the watchdog behaving correctly for a kill, and a false positive only in
            // the (loud, already-logged) case where the patch failed to apply.
            try { Application.quitting += OnUnityQuitting; }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Application.quitting hook failed (clean-shutdown marker relies on the ZNet.Shutdown patch): {e.Message}"); }
            try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; }
            catch (Exception) { /* hosted/restricted domain — the other two hooks still cover us */ }

            // Chat commands. Registration is unconditional (first registration wins and Init runs once); the
            // handlers themselves check EnableDiscordLink so toggling the config takes effect without a
            // restart and a disabled feature answers with an explanation instead of silence.
            try
            {
                Wave1Chat.RegisterChatCommand("link", OnLinkCommand);
                Wave1Chat.RegisterChatCommand("unlink", OnUnlinkCommand);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Discord link chat commands unavailable: {e.Message}"); }
        }

        private static void ApplyPatch(string name, Type patchClass, string degradation)
        {
            try
            {
                Harmony.CreateAndPatchAll(patchClass);
                Wave2Ops.ReportPatch(name, true);
            }
            catch (Exception e)
            {
                Wave2Ops.ReportPatch(name, false);
                CompanionPlugin.FeatureLog($"{name} failed ({degradation}): {e.Message}");
            }
        }

        internal static void Tick()
        {
            if (!_inited) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var now = Time.unscaledTime;

            // Boot work waits for the store: the watchdog needs the per-world data dir, which only resolves
            // once a world is loaded. Until then this is a couple of null checks per frame.
            if (!_booted && FeatureStore.Ready)
            {
                _booted = true;
                try { RunBootSequence(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Discord boot alerts failed: {e.Message}"); }
            }

            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + HeartbeatSeconds;
                try { WriteHeartbeat(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Discord watchdog heartbeat failed: {e.Message}"); }
            }

            if (now < _nextSlowTick) return;
            _nextSlowTick = now + SlowTickSeconds;

            try { WatchRestartSchedule(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Discord restart watch failed: {e.Message}"); }
            try { WatchDisk(now); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Discord disk watch failed: {e.Message}"); }
            try { TickLeaderboard(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Discord leaderboard failed: {e.Message}"); }
            try { PruneLinkCodes(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Link-code prune failed: {e.Message}"); }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave3DiscordRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvDiscordStateReq", new Action<long>(OnDiscordStateReq));
                    ZRoutedRpc.instance.Register("AP_SrvDiscordTest", new Action<long>(OnDiscordTest));
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Discord RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. admin alerts ====================

        // One-shot boot work: evaluate the watchdog BEFORE refreshing the heartbeat (otherwise this session's
        // own heartbeat would hide the previous session's), then announce that the server is up.
        private static void RunBootSequence()
        {
            var uncleanAt = EvaluateWatchdog();
            ClearCleanMarker();
            WriteHeartbeat();

            var dsc = FeatureStore.Table(TblDsc);
            var boots = 0;
            if (dsc.TryGetValue("boot_count", out var raw)) int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out boots);
            dsc["boot_count"] = (boots + 1).ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblDsc);

            SendAlert("Server started",
                $"World: **{Md(WorldName())}**\nCompanion: {CompanionPlugin.PluginVersion}\nStart #{boots + 1} on this world.",
                ColorGood);

            if (uncleanAt > 0)
            {
                var when = new DateTime(uncleanAt, DateTimeKind.Utc);
                var text = $"Previous session ended unexpectedly at {when:yyyy-MM-dd HH:mm:ss} UTC " +
                           $"({FormatAgo(DateTime.UtcNow - when)} ago). The last heartbeat is newer than the last clean-shutdown marker, " +
                           "so the process did not go through a normal shutdown — check for a crash, an out-of-memory kill or a host reboot. " +
                           "Anything the world had not saved since that moment is lost.";
                CompanionPlugin.FeatureLog(text);
                Wave1AuditRpc.PostModLog("WATCHDOG " + text);
                SendAlert("Unclean shutdown detected", text, ColorBad);
            }
        }

        // Returns the heartbeat ticks of a session that never shut down cleanly, or 0.
        // Rule: heartbeat newer than the clean-shutdown marker (or no marker at all while a heartbeat exists)
        // == the previous process died between two heartbeats without writing its marker.
        private static long EvaluateWatchdog()
        {
            var beat = ReadTicksFile(HeartbeatFile);
            if (beat <= 0) return 0;                 // first ever run on this world — nothing to compare against
            var clean = ReadTicksFile(ShutdownFile);
            return beat > clean ? beat : 0;
        }

        private static void WriteHeartbeat()
        {
            WriteTicksFile(HeartbeatFile, DateTime.UtcNow.Ticks);
        }

        private static void ClearCleanMarker()
        {
            try
            {
                var dir = FeatureStore.DataDir;
                if (dir == null) return;
                var path = Path.Combine(dir, ShutdownFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { /* a stale marker only ever HIDES a crash report; never worth throwing over */ }
        }

        /// <summary>
        /// Record a clean shutdown. Idempotent and exception-free: called from a Harmony postfix on
        /// ZNet.Shutdown, from Application.quitting and from AppDomain.ProcessExit, any of which may fire
        /// first, more than once, or (on a hard kill) not at all.
        /// </summary>
        internal static void MarkCleanShutdown()
        {
            if (_cleanMarkerWritten) return;
            _cleanMarkerWritten = true;
            try { WriteTicksFile(ShutdownFile, DateTime.UtcNow.Ticks); }
            catch (Exception) { }
        }

        private static void OnUnityQuitting() => MarkCleanShutdown();
        private static void OnProcessExit(object sender, EventArgs e) => MarkCleanShutdown();

        // POSTFIX on ZNet.Shutdown (public void Shutdown(bool save = true), ZNet.decompiled.cs:541) — the last
        // point at which ZNet.World is still resolvable, which FeatureStore.DataDir needs to find the per-world
        // folder. A client leaving a world runs this too, hence the IsServer guard.
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class Wave3ShutdownMarkerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                try
                {
                    if (__instance == null || !__instance.IsServer()) return;
                    MarkCleanShutdown();
                }
                catch (Exception) { }
            }
        }

        // Restart announcements. "restart_at" / "restart_reason" are OWNED by the wave-2 backup/restart module;
        // this file only reads the shared "srvcfg" keys (identical arrangement to Wave2Ops's schedule reply —
        // reading the table avoids a load-order dependency on that class). The last announced value lives in
        // "dsc" so a server restart cannot re-announce a schedule that was already posted.
        private static void WatchRestartSchedule()
        {
            if (!AlertsOn || !FeatureStore.Ready) return;

            var cfgTable = FeatureStore.Table(TblSrvCfg);
            long cur = 0;
            if (cfgTable.TryGetValue(KeyRestartAt, out var raw))
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out cur);

            var dsc = FeatureStore.Table(TblDsc);
            long prev = 0;
            if (dsc.TryGetValue(KeyAlertRestartAt, out var praw))
                long.TryParse(praw, NumberStyles.Integer, CultureInfo.InvariantCulture, out prev);

            if (cur == prev) return;   // unchanged, or the pending time simply elapsed (the restart itself fired)

            var nowTicks = DateTime.UtcNow.Ticks;
            var reason = cfgTable.TryGetValue(KeyRestartReason, out var rr) ? rr : "";

            if (cur > nowTicks)
            {
                var when = new DateTime(cur, DateTimeKind.Utc);
                SendAlert("Scheduled restart announced",
                    $"The server restarts at **{when:yyyy-MM-dd HH:mm} UTC** (in {FormatAgo(when - DateTime.UtcNow)})." +
                    (string.IsNullOrEmpty(reason) ? "" : $"\nReason: {Md(reason)}"),
                    ColorWarn);
            }
            else if (prev > nowTicks)
            {
                // The previously announced time was still in the future and has now been cleared or moved into
                // the past: an admin cancelled it.
                SendAlert("Scheduled restart cancelled",
                    "The pending server restart was cancelled.", ColorInfo);
            }

            dsc[KeyAlertRestartAt] = cur.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblDsc);
        }

        // Low disk, at most once per hour. Free space is measured on the drive holding the companion's data
        // directory — the same volume the world .db/.fwl live on for every normal install. If the world is on
        // a different mount than BepInEx/config, this reports the wrong volume; that is a deliberate trade
        // against reflecting into World.GetDBPath (whose signature has moved across game versions).
        private static void WatchDisk(float now)
        {
            if (!AlertsOn) return;
            if (now < _nextDiskAlert) return;

            string label;
            var free = FreeDiskBytes(out label);
            if (free < 0 || free >= LowDiskBytes) return;

            _nextDiskAlert = now + DiskAlertSeconds;
            var gb = free / 1024d / 1024d / 1024d;
            var text = $"Only **{gb:0.0} GB** free on `{label}`. A world save can fail at this level and a failed save can corrupt the world.";
            CompanionPlugin.FeatureLog($"Discord alert: low disk ({gb:0.0} GB free on {label})");
            SendAlert("Low disk space", text, ColorBad);
        }

        private static long FreeDiskBytes(out string label)
        {
            label = "?";
            try
            {
                var path = FeatureStore.DataDir;
                if (string.IsNullOrEmpty(path)) return -1;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;
                label = root;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception) { return -1; }
        }

        // ==================== 2. leaderboards ====================

        private static void TickLeaderboard()
        {
            if (!LeaderboardOn || !FeatureStore.Ready) return;

            var dsc = FeatureStore.Table(TblDsc);
            long last = 0;
            if (dsc.TryGetValue(KeyLbLast, out var raw))
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out last);

            var nowTicks = DateTime.UtcNow.Ticks;
            if (last <= 0 || last > nowTicks)
            {
                // First time the feature is seen (or a clock that went backwards): start the clock instead of
                // firing immediately, so enabling it does not dump a leaderboard the instant the server boots.
                dsc[KeyLbLast] = nowTicks.ToString(CultureInfo.InvariantCulture);
                FeatureStore.SaveTable(TblDsc);
                return;
            }

            var due = new DateTime(last, DateTimeKind.Utc).AddHours(LeaderboardHours).Ticks;
            if (nowTicks < due) return;

            // Persist FIRST. If the post itself fails (dead webhook, no feed configured), the schedule still
            // advances — an offline webhook must not turn into a retry storm every 30 s.
            dsc[KeyLbLast] = nowTicks.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblDsc);

            PostEmbed($"Leaderboard - last {LeaderboardHours}h", BuildLeaderboard(last), ColorInfo);
        }

        // Reads the wave-1 "presence" table and the "deaths" table directly (their layouts ARE the contract;
        // calling into the owning modules would couple load order for no benefit).
        private static string BuildLeaderboard(long periodStartTicks)
        {
            var sb = new StringBuilder();

            // ---- playtime (all-time totals: "presence" accumulates seconds, it keeps no per-period history) ----
            var play = new List<KeyValuePair<string, long>>();
            foreach (var kv in FeatureStore.Table(TblPresence))
            {
                long first, lastSeen, seconds; int sessions; string name;
                ParsePresence(kv.Value, out first, out lastSeen, out sessions, out seconds, out name);
                if (seconds <= 0) continue;
                play.Add(new KeyValuePair<string, long>(
                    string.IsNullOrEmpty(name) ? ShortId(kv.Key) : name, seconds));
            }
            play.Sort((a, b) => b.Value.CompareTo(a.Value));

            sb.Append("**Playtime (all time)**\n");
            if (play.Count == 0) sb.Append("_no recorded sessions yet_\n");
            for (var i = 0; i < play.Count && i < LeaderboardRows; i++)
                sb.Append(i + 1).Append(". ").Append(Md(Clip(play[i].Key, MaxNameLen)))
                  .Append(" - ").Append(FormatHours(play[i].Value)).Append('\n');

            // ---- deaths in the period ----
            var deaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
            foreach (var kv in FeatureStore.Table(TblDeaths))
            {
                if (++scanned > DeathScanCap) break;
                long ticks;
                if (!long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                if (ticks < periodStartTicks) continue;
                // "id|name|x|y|z|hasTomb"
                var parts = (kv.Value ?? "").Split('|');
                var who = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                    ? parts[1]
                    : (parts.Length > 0 ? ShortId(parts[0]) : "?");
                int n;
                deaths[who] = deaths.TryGetValue(who, out n) ? n + 1 : 1;
            }
            var deathList = new List<KeyValuePair<string, int>>(deaths);
            deathList.Sort((a, b) => b.Value.CompareTo(a.Value));

            sb.Append("\n**Deaths (this period)**\n");
            if (deathList.Count == 0) sb.Append("_nobody died. Suspicious._\n");
            for (var i = 0; i < deathList.Count && i < LeaderboardRows; i++)
                sb.Append(i + 1).Append(". ").Append(Md(Clip(deathList[i].Key, MaxNameLen)))
                  .Append(" - ").Append(deathList[i].Value).Append('\n');

            return Clip(sb.ToString(), MaxDescLen);
        }

        // ==================== 3. account linking ====================

        // "!link" — mints a code. Everything a webhook-only server CAN do; the other half needs a bot.
        private static void OnLinkCommand(long sender, string args)
        {
            if (!LinkOn)
            {
                Tell(sender, "Discord account linking is not enabled on this server.");
                return;
            }
            if (!FeatureStore.Ready)
            {
                Tell(sender, "Linking is temporarily unavailable (server storage not ready).");
                return;
            }

            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?")
            {
                Tell(sender, "Linking failed: your platform id could not be read.");
                return;
            }

            var links = FeatureStore.Table(TblLinks);
            var existing = FindKey(links, id);
            if (existing != null)
            {
                var disc = SplitFirst(links[existing]);
                Tell(sender, $"You are already linked to Discord user {disc}. Type !unlink first if you want to change it.");
                return;
            }

            var codes = FeatureStore.Table(TblLinkCodes);

            // One live code per player: drop any earlier code of theirs so an old screenshot cannot be redeemed.
            List<string> mine = null;
            foreach (var kv in codes)
            {
                string owner; long exp;
                if (!ParseLinkCode(kv.Value, out owner, out exp)) continue;
                if (Wave1AuditRpc.SameId(owner, id)) (mine ?? (mine = new List<string>())).Add(kv.Key);
            }
            if (mine != null) foreach (var c in mine) codes.Remove(c);

            string code = null;
            for (var i = 0; i < LinkCodeAttempts && code == null; i++)
            {
                var candidate = NewCode();
                if (!codes.ContainsKey(candidate)) code = candidate;
            }
            if (code == null)
            {
                Tell(sender, "Linking failed: could not generate a code. Try again in a moment.");
                return;
            }

            var expiry = DateTime.UtcNow.AddMinutes(LinkCodeMinutes).Ticks;
            codes[code] = id + "|" + expiry.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblLinkCodes);

            CompanionPlugin.FeatureLog($"Discord link code issued for {id} (expires in {LinkCodeMinutes:0} min)");
            Tell(sender, $"Your Discord link code is {code} - valid for {LinkCodeMinutes:0} minutes. In Discord, run: /link {code}");
        }

        // "!unlink" — clears the player's row in "links". Purely local; Discord is never told.
        private static void OnUnlinkCommand(long sender, string args)
        {
            if (!LinkOn)
            {
                Tell(sender, "Discord account linking is not enabled on this server.");
                return;
            }
            if (!FeatureStore.Ready) return;

            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?") return;

            var links = FeatureStore.Table(TblLinks);
            var key = FindKey(links, id);
            if (key == null)
            {
                Tell(sender, "You are not linked to a Discord account.");
                return;
            }
            links.Remove(key);
            FeatureStore.SaveTable(TblLinks);

            // Any pending code of theirs dies with the link, otherwise it would silently re-link them.
            var codes = FeatureStore.Table(TblLinkCodes);
            List<string> dead = null;
            foreach (var kv in codes)
            {
                string owner; long exp;
                if (ParseLinkCode(kv.Value, out owner, out exp) && Wave1AuditRpc.SameId(owner, id))
                    (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead != null)
            {
                foreach (var c in dead) codes.Remove(c);
                FeatureStore.SaveTable(TblLinkCodes);
            }

            CompanionPlugin.FeatureLog($"Discord link removed for {key}");
            Tell(sender, "Your Discord link has been removed.");
        }

        /// <summary>
        /// Redeem a "!link" code on behalf of a Discord user and write the "links" row.
        ///
        /// THIS IS THE BRIDGE POINT. A Discord webhook is outgoing-only, so nothing in this assembly can ever
        /// learn that someone typed "/link ABC123" in Discord. The Ikarus bot (or any future in-process bridge)
        /// must call this method with the code it received and the Discord user id it came from. Until such a
        /// bridge exists, codes simply expire unused and the "links" table stays empty.
        ///
        /// Call it on the MAIN THREAD: FeatureStore's file I/O is lock-guarded, but the Dictionary it hands
        /// back is not, and every other reader of these tables runs on the main thread.
        /// </summary>
        /// <returns>true when the code was valid, unexpired and consumed; platformId is then the linked player.</returns>
        internal static bool TryConsumeLinkCode(string code, string discordUserId, out string platformId)
        {
            platformId = null;
            try
            {
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(discordUserId)) return false;
                if (!FeatureStore.Ready) return false;

                var clean = code.Trim().ToUpperInvariant();
                if (clean.Length == 0 || clean.Length > 16) return false;

                var codes = FeatureStore.Table(TblLinkCodes);
                string raw;
                if (!codes.TryGetValue(clean, out raw)) return false;

                string owner; long expiry;
                if (!ParseLinkCode(raw, out owner, out expiry) || string.IsNullOrEmpty(owner))
                {
                    codes.Remove(clean);
                    FeatureStore.SaveTable(TblLinkCodes);
                    return false;
                }
                // A single-use code is consumed whether it was fresh or stale — an expired code must never be
                // redeemable on a retry.
                codes.Remove(clean);
                FeatureStore.SaveTable(TblLinkCodes);
                if (expiry <= DateTime.UtcNow.Ticks) return false;

                var discord = discordUserId.Trim();
                if (discord.Length > 40) discord = discord.Substring(0, 40);
                discord = discord.Replace('|', '/');

                var links = FeatureStore.Table(TblLinks);
                // One Discord account maps to one player: drop any other row already claiming this Discord id.
                List<string> stale = null;
                foreach (var kv in links)
                {
                    if (string.Equals(SplitFirst(kv.Value), discord, StringComparison.Ordinal))
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                }
                if (stale != null) foreach (var k in stale) links.Remove(k);

                var key = FindKey(links, owner) ?? owner;
                links[key] = discord + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                FeatureStore.SaveTable(TblLinks);

                platformId = key;
                CompanionPlugin.FeatureLog($"Discord link completed: {key} <-> discord:{discord}");
                Wave1AuditRpc.PostModLog($"DISCORD LINK {key} linked to discord user {discord}");
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"TryConsumeLinkCode failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Discord user id linked to this platform id, or null. Handy for a future bridge.</summary>
        internal static string LinkedDiscordId(string platformId)
        {
            try
            {
                if (string.IsNullOrEmpty(platformId) || !FeatureStore.Ready) return null;
                var v = Wave1AuditRpc.LookupById(FeatureStore.Table(TblLinks), platformId);
                var d = SplitFirst(v);
                return string.IsNullOrEmpty(d) ? null : d;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Number of completed account links (the "links" table size).</summary>
        internal static int LinkedCount()
        {
            try { return FeatureStore.Ready ? FeatureStore.Table(TblLinks).Count : 0; }
            catch (Exception) { return 0; }
        }

        private static void PruneLinkCodes()
        {
            if (!FeatureStore.Ready) return;
            var codes = FeatureStore.Table(TblLinkCodes);
            if (codes.Count == 0) return;
            var nowTicks = DateTime.UtcNow.Ticks;
            List<string> dead = null;
            foreach (var kv in codes)
            {
                string owner; long expiry;
                if (!ParseLinkCode(kv.Value, out owner, out expiry) || expiry <= nowTicks)
                    (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var c in dead) codes.Remove(c);
            FeatureStore.SaveTable(TblLinkCodes);
        }

        private static string NewCode()
        {
            var chars = new char[LinkCodeLen];
            for (var i = 0; i < LinkCodeLen; i++) chars[i] = CodeAlphabet[Rng.Next(CodeAlphabet.Length)];
            return new string(chars);
        }

        private static bool ParseLinkCode(string value, out string owner, out long expiry)
        {
            owner = null; expiry = 0;
            if (string.IsNullOrEmpty(value)) return false;
            var bar = value.LastIndexOf('|');
            if (bar <= 0) return false;
            owner = value.Substring(0, bar);
            return long.TryParse(value.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry);
        }

        // ==================== 4. AP_SrvDiscordStateReq / AP_SrvDiscordTest ====================

        // Wire: {int ver=1, bool feedOn, bool alertsOn, bool linkOn, int queued, int sentTotal, int failedTotal,
        //        string lastError, long lastSentTicksUtc, int linkedCount}
        private static void OnDiscordStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDiscordStateReq")) return;

            var feed = ReadFeedState();

            var sent = feed.Sent + Volatile.Read(ref _alertsSent);
            var failed = feed.Failed + Volatile.Read(ref _alertsFailed);
            var myTicks = Interlocked.Read(ref _lastAlertTicksUtc);
            var lastSent = feed.LastSentTicks > myTicks ? feed.LastSentTicks : myTicks;

            // The feed's own error wins when it has one (it carries the bulk of the traffic); our alert error is
            // the fallback so a misconfigured alert webhook is still visible in the panel.
            var err = !string.IsNullOrEmpty(feed.LastError) ? feed.LastError : (_lastAlertError ?? "");

            var pkg = new ZPackage();
            pkg.Write(1);                       // payload version — bump, never reorder
            pkg.Write(feed.FeedOn);
            pkg.Write(AlertsOn);
            pkg.Write(LinkOn);
            pkg.Write(feed.Queued);
            pkg.Write(sent);
            pkg.Write(failed);
            pkg.Write(Clip(err, 200));
            pkg.Write(lastSent);
            pkg.Write(LinkedCount());

            try { CompanionPlugin.ReplyTo(sender, "AP_DiscordState", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_DiscordState reply failed: {e.Message}"); }
        }

        private static void OnDiscordTest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDiscordTest")) return;

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "DISCORD-TEST", $"world={WorldName()}");

            PostEmbed("Admin panel test",
                $"Test message from **{Md(WorldName())}**, requested by {Md(admin)}.\nCompanion {CompanionPlugin.PluginVersion}.",
                ColorGood);

            // The post is queued/fired asynchronously, so this confirms the ATTEMPT, not delivery — the panel's
            // state poll (sent/failed/lastError) is where the outcome shows up.
            CompanionPlugin.NotifySender(sender,
                "Discord test embed queued. Check the channel; if nothing arrives, refresh the Discord state for the last error.");
        }

        // ==================== delivery ====================

        // Route one alert. With DiscordAlertWebhookUrl set we POST it ourselves (only that path can carry an
        // @everyone ping, which has to live in the message content); otherwise it falls back to the feed queue.
        private static void SendAlert(string title, string description, int color)
        {
            if (!AlertsOn) return;
            var url = AlertUrl;
            if (url == null)
            {
                // Feed fallback: the mention is prepended as TEXT so an owner can see it was requested, but a
                // mention inside an embed never pings anyone. Documented, not a bug.
                PostEmbed(title, (MentionEveryone ? "@everyone\n" : "") + description, color);
                return;
            }
            PostAlertWebhook(url, title, description, color);
        }

        // Single funnel for the sibling's queue: if Wave34Core.Enqueue ever changes shape, this is the one line
        // that needs touching.
        private static void PostEmbed(string title, string description, int color)
        {
            try { Wave34Core.Enqueue(title ?? "", description ?? "", color); }
            catch (Exception e)
            {
                Interlocked.Increment(ref _alertsFailed);
                _lastAlertError = "feed enqueue failed: " + e.Message;
                CompanionPlugin.FeatureLog($"Discord enqueue failed: {e.Message}");
            }
        }

        // PostModLog's recipe, verbatim (Wave1SrvAuditRpc.cs): HttpWebRequest on a thread-pool task, Tls12 OR'd
        // in once, 10 s timeouts, every exception swallowed, manual JSON escaping. Nothing inside the task
        // touches Unity or ZNet — only a byte[] and two ints crossing the thread boundary.
        private static void PostAlertWebhook(string url, string title, string description, int color)
        {
            string json;
            try
            {
                var sb = new StringBuilder(512);
                sb.Append('{');
                if (MentionEveryone)
                    sb.Append("\"content\":\"@everyone\",\"allowed_mentions\":{\"parse\":[\"everyone\"]},");
                sb.Append("\"embeds\":[{\"title\":\"").Append(JsonEscape(Clip(title, 250)))
                  .Append("\",\"description\":\"").Append(JsonEscape(Clip(description, MaxDescLen)))
                  .Append("\",\"color\":").Append(color.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"footer\":{\"text\":\"").Append(JsonEscape(Clip(WorldName(), 80)))
                  .Append("\"},\"timestamp\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                  .Append("\"}]}");
                json = sb.ToString();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _alertsFailed);
                _lastAlertError = "alert build failed: " + e.Message;
                return;
            }

            var body = Encoding.UTF8.GetBytes(json);
            PrepareTls();
            try
            {
                Task.Run(() =>
                {
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
                        using (var resp = req.GetResponse()) { }   // drain + dispose; status is ignored
                        Interlocked.Increment(ref _alertsSent);
                        Interlocked.Exchange(ref _lastAlertTicksUtc, DateTime.UtcNow.Ticks);
                        _lastAlertError = "";
                    }
                    catch (Exception e)
                    {
                        // Rate limit, DNS, TLS, 404 — an alert webhook is never load-bearing. Record it so the
                        // panel can show WHY nothing is arriving.
                        Interlocked.Increment(ref _alertsFailed);
                        _lastAlertError = Clip("alert webhook: " + e.Message, 200);
                    }
                });
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _alertsFailed);
                _lastAlertError = "alert dispatch failed: " + e.Message;
            }
        }

        private static void PrepareTls()
        {
            if (Interlocked.Exchange(ref _tlsPrepared, 1) != 0) return;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { /* older/patched runtime without Tls12 in the enum — leave the default */ }
        }

        // ==================== Wave34Core.FeedState() — reflective read ====================

        private struct FeedSnapshot
        {
            public bool FeedOn;
            public int Queued;
            public int Sent;
            public int Failed;
            public string LastError;
            public long LastSentTicks;
        }

        // The sibling owns the feed; its state object's SHAPE is not part of the contract handed to this file,
        // so it is read by name with a documented fallback (identical reasoning to Wave2Ops reading
        // Wave2Backup.BackupHealth reflectively). Understood returns: bool, string, or any object exposing
        // members whose names contain the words below.
        private static FeedSnapshot ReadFeedState()
        {
            var snap = new FeedSnapshot { LastError = "" };
            try
            {
                if (!_feedProbed)
                {
                    _feedProbed = true;
                    var t = AccessTools.TypeByName("AdminPanelCompanion.Wave34Core");
                    if (t != null) _feedStateMi = AccessTools.Method(t, "FeedState", new Type[0]);
                }
                if (_feedStateMi == null)
                {
                    snap.LastError = "feed module not available";
                    return snap;
                }

                var res = _feedStateMi.Invoke(null, null);
                if (res == null) { snap.LastError = "feed state unavailable"; return snap; }

                if (res is bool b) { snap.FeedOn = b; return snap; }
                if (res is string s) { snap.FeedOn = true; snap.LastError = Clip(s, 200); return snap; }

                snap.FeedOn = true;
                foreach (var m in res.GetType().GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                           BindingFlags.Instance | BindingFlags.Static))
                {
                    object val;
                    if (m is FieldInfo f) { try { val = f.GetValue(res); } catch (Exception) { continue; } }
                    else if (m is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0)
                    { try { val = p.GetValue(res, null); } catch (Exception) { continue; } }
                    else continue;
                    if (val == null) continue;

                    var n = m.Name.ToLowerInvariant();
                    if (val is bool vb && (n.Contains("on") || n.Contains("enable"))) snap.FeedOn = vb;
                    else if (val is int vi)
                    {
                        if (n.Contains("queue")) snap.Queued = vi;
                        else if (n.Contains("fail") || n.Contains("error")) snap.Failed = vi;
                        else if (n.Contains("sent") || n.Contains("posted")) snap.Sent = vi;
                    }
                    else if (val is long vl)
                    {
                        if (n.Contains("sent") || n.Contains("last")) snap.LastSentTicks = vl;
                    }
                    else if (val is string vs && n.Contains("error")) snap.LastError = Clip(vs, 200);
                }
            }
            catch (Exception e)
            {
                snap.LastError = Clip("feed state read failed: " + e.Message, 200);
            }
            return snap;
        }

        // ==================== shared helpers ====================

        // Reaches UNMODDED clients (vanilla "ShowMessage"); the AP_Msg copy is for admins running the panel,
        // whose chat HUD is not necessarily where they are looking.
        private static void Tell(long uid, string text)
        {
            try { Wave1Moderation.SendPlayerText(uid, text); } catch (Exception) { }
            try { CompanionPlugin.NotifySender(uid, text); } catch (Exception) { }
        }

        private static string WorldName()
        {
            try
            {
                object w = AccessTools.Property(typeof(ZNet), "World")?.GetValue(null)
                           ?? AccessTools.Field(typeof(ZNet), "m_world")?.GetValue(null);
                if (w == null) return "?";
                var n = FeatureStore.WorldDisplayName(w);   // display name, else on-disk name (silent reflection)
                return string.IsNullOrEmpty(n) ? "?" : n;
            }
            catch (Exception) { return "?"; }
        }

        private static long ReadTicksFile(string file)
        {
            try
            {
                var dir = FeatureStore.DataDir;
                if (dir == null) return 0;
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) return 0;
                long ticks;
                return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out ticks) ? ticks : 0;
            }
            catch (Exception) { return 0; }
        }

        private static void WriteTicksFile(string file, long ticks)
        {
            try
            {
                var dir = FeatureStore.DataDir;
                if (dir == null) return;
                File.WriteAllText(Path.Combine(dir, file), ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception) { /* full disk / locked file must never take down a tick */ }
        }

        private static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (Wave1AuditRpc.SameId(kv.Key, id)) return kv.Key;
            return null;
        }

        private static string SplitFirst(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var bar = value.IndexOf('|');
            return bar < 0 ? value : value.Substring(0, bar);
        }

        private static void ParsePresence(string value, out long first, out long last, out int sessions,
            out long seconds, out string lastName)
        {
            first = 0; last = 0; sessions = 0; seconds = 0; lastName = "";
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 5);
            if (parts.Length > 0) long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out first);
            if (parts.Length > 1) long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out last);
            if (parts.Length > 2) int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sessions);
            if (parts.Length > 3) long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
            if (parts.Length > 4) lastName = parts[4];
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            var bare = CompanionPlugin.FeatureBareId(id);
            if (string.IsNullOrEmpty(bare)) bare = id;
            return bare.Length > 10 ? "..." + bare.Substring(bare.Length - 6) : bare;
        }

        private static string FormatHours(long seconds)
        {
            var h = seconds / 3600;
            var m = (seconds % 3600) / 60;
            return h > 0 ? $"{h}h {m}m" : $"{m}m";
        }

        private static string FormatAgo(TimeSpan span)
        {
            if (span.Ticks < 0) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{Math.Max(1, (int)span.TotalMinutes)}m";
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // Player names are arbitrary text on their way into a markdown renderer: neutralise the formatting
        // characters so a name like "**x**" cannot mangle the embed (and cannot fake a heading).
        private static string Md(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '*' || c == '_' || c == '`' || c == '~' || c == '>' || c == '|' || c == '@' || c == '#') continue;
                if (c == '\r' || c == '\n') { sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Minimal, dependency-free JSON string escaping (net48 ships no JSON writer and the mod ships no deps).
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
    }
}

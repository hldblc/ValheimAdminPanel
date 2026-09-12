using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 6 — server events, warps, voting, seasonal toggles ====================
    //
    // Four opt-in server-owner features behind ONE master switch (Features.EnableEvents, default FALSE).
    // A server that merely upgrades the DLL sees nothing from this file: no chat command is registered (so
    // chat behaves byte-for-byte as before), no timer does work, and every RPC answers "events are disabled".
    //
    // Design rules this file obeys (identical discipline to waves 1-4, see spec-companion.md):
    //  * Durable state lives in FeatureStore tables ("warps", "events", "votes"); ticks are DateTime.UtcNow.Ticks
    //    on the wire and in the store; floats are written with InvariantCulture.
    //  * Every admin RPC is gated by CompanionPlugin.SenderCanFeature and audited via CompanionPlugin.SrvAudit
    //    + Wave1AuditRpc.PostModLog + Wave34Core.Enqueue (Discord).
    //  * Nothing here invents a game mechanism. Creature spawning follows CompanionPlugin.OnServerSpawn
    //    (CompanionPlugin.cs:203-275) exactly; raids reuse CompanionPlugin.OnServerEvent's mechanism
    //    (RandEventSystem.SetRandomEventByName, CompanionPlugin.cs:654-660); night-skip reuses
    //    CompanionPlugin.OnServerSkipNight (EnvMan.SkipToMorning, CompanionPlugin.cs:793-799); teleports reuse
    //    the vanilla "RPC_TeleportPlayer" Wave1Moderation's freeze already relies on; seasonal toggles reuse
    //    ZoneSystem.SetGlobalKey / RemoveGlobalKey, the same path the panel's global-keys UI drives.
    //  * TIER-VANILLA where it matters: warps, treasure arrival detection, tournament teleports and every
    //    announcement work for players with NO client mod. Nothing in this file needs the companion on a client.
    //  * No world sweeps. Every loop is bounded by the peer list, a <=20 spot list or a <=32 bracket; the only
    //    ZDO read is ZNet.GetAllCharacterZDOS() (one lookup per ready peer — the same call Wave34Core's death
    //    poll uses, NOT a sector scan) on a 5 s cadence.
    internal static class Wave6Events
    {
        // ---- store tables (shared contract with the panel and sibling wave files) ----
        private const string TblWarps = "warps";     // name -> "x|y|z|adminOnly"
        private const string TblEvents = "events";   // "active" -> "kind|endsTicks|param"; "seasonal:<name>" -> "0|1"
        private const string TblVotes = "votes";     // "current" -> "topic|endsTicks|action|yesCsv|noCsv"

        // COUPLING (documented on purpose): the treasure hunt pays out in the wave-6 ECONOMY module's currency
        // — the "eco" table, id -> "balance|lifetime|name". It is written through Wave6Economy.Grant, never by
        // hand, so clamping / lifetime bookkeeping / auditing stay owned by one file. With the economy module
        // disabled a find is announced and nothing is paid.

        private const string KeyActive = "active";
        private const string KeyVote = "current";
        private const string SeasonPrefix = "seasonal:";

        // ---- caps (never trust a client string or number) ----
        private const int MaxNameLen = 32;
        private const int MaxTextLen = 200;
        private const int MaxIdLen = 64;
        private const int MaxWarps = 200;
        private const int WarpShipCap = 40;      // AP_EventState wire cap
        private const int SeasonShipCap = 20;    // AP_EventState wire cap
        private const int MaxEventMinutes = 1440;
        private const int MaxVoteMinutes = 60;
        private const int MaxTreasureSpots = 20;
        private const int MaxBossesInSequence = 12;
        private const int MaxParticipants = 32;
        private const int VoteEventMinutes = 15;   // duration used when a vote passes "event:<kind>"
        private const float ArenaSpread = 6f;      // metres between the two duellists of a pair

        // ---- config ----
        private static ConfigEntry<bool> _enableEvents;
        private static ConfigEntry<int> _warpCooldown;
        private static ConfigEntry<string> _bossRushSequence;
        private static ConfigEntry<int> _bossRushInterval;
        private static ConfigEntry<int> _bossRushLevel;
        private static ConfigEntry<int> _treasureSpots;
        private static ConfigEntry<float> _treasureRadius;
        private static ConfigEntry<float> _treasureFindRadius;
        private static ConfigEntry<int> _treasureReward;
        private static ConfigEntry<string> _treasureMarkerPrefab;
        private static ConfigEntry<string> _tournamentArena;
        private static ConfigEntry<int> _tournamentSignupSeconds;
        private static ConfigEntry<int> _tournamentRoundSeconds;
        private static ConfigEntry<int> _votePassPercent;
        private static ConfigEntry<int> _voteMinVoters;
        private static ConfigEntry<bool> _enablePlayerVoteStart;
        private static ConfigEntry<int> _playerVoteCooldown;
        private static ConfigEntry<bool> _seasonAllowAnyKey;

        /// <summary>Master switch. Everything in this file is inert while it is false.</summary>
        internal static bool EventsOn => _enableEvents != null && _enableEvents.Value;

        private static int WarpCooldownSeconds => _warpCooldown != null ? Mathf.Clamp(_warpCooldown.Value, 0, 86400) : 60;
        private static string BossRushSequence => _bossRushSequence != null ? (_bossRushSequence.Value ?? "") : "";
        private static int BossRushInterval => _bossRushInterval != null ? Mathf.Clamp(_bossRushInterval.Value, 10, 3600) : 120;
        private static int BossRushLevel => _bossRushLevel != null ? Mathf.Clamp(_bossRushLevel.Value, 1, 10) : 1;
        private static int TreasureSpotCount => _treasureSpots != null ? Mathf.Clamp(_treasureSpots.Value, 1, MaxTreasureSpots) : 5;
        private static float TreasureRadius => _treasureRadius != null ? Mathf.Clamp(_treasureRadius.Value, 20f, 5000f) : 300f;
        private static float TreasureFindRadius => _treasureFindRadius != null ? Mathf.Clamp(_treasureFindRadius.Value, 3f, 100f) : 12f;
        private static int TreasureReward => _treasureReward != null ? Mathf.Clamp(_treasureReward.Value, 0, 1000000) : 100;
        private static string TreasureMarkerPrefab => _treasureMarkerPrefab != null ? (_treasureMarkerPrefab.Value ?? "").Trim() : "";
        private static string TournamentArena => _tournamentArena != null ? (_tournamentArena.Value ?? "").Trim() : "";
        private static int TournamentSignupSeconds => _tournamentSignupSeconds != null ? Mathf.Clamp(_tournamentSignupSeconds.Value, 15, 3600) : 120;
        private static int TournamentRoundSeconds => _tournamentRoundSeconds != null ? Mathf.Clamp(_tournamentRoundSeconds.Value, 30, 3600) : 300;
        private static int VotePassPercent => _votePassPercent != null ? Mathf.Clamp(_votePassPercent.Value, 1, 100) : 60;
        private static int VoteMinVoters => _voteMinVoters != null ? Mathf.Clamp(_voteMinVoters.Value, 1, 64) : 2;
        private static bool PlayerVoteStartOn => _enablePlayerVoteStart != null && _enablePlayerVoteStart.Value;
        private static int PlayerVoteCooldownSeconds => _playerVoteCooldown != null ? Mathf.Clamp(_playerVoteCooldown.Value, 0, 86400) : 600;
        private static bool SeasonAllowAnyKey => _seasonAllowAnyKey != null && _seasonAllowAnyKey.Value;

        // ==================== runtime state ====================
        // Session-scoped by nature: an event's timers, its spawned-object list and its bracket cannot be
        // reconstructed after a restart, so only the "there is an event" marker row and the vote are persisted.

        private sealed class TreasureSpot
        {
            public Vector3 Pos;
            public bool Found;
            public string FinderName = "";
            public ZDOID Marker = ZDOID.None;
        }

        private sealed class ActiveEvent
        {
            public string Kind = "";
            public string Param = "";
            public long EndsTicksUtc;
            public long Starter;             // peer uid of the admin who started it (0 when vote-started)
            public string StarterName = "";
            public Vector3 Centre;

            // bossrush
            public readonly List<string> Bosses = new List<string>();
            public int NextBoss;
            public float NextSpawnAt;

            // everything this event added to the world, for the restore-on-stop path
            public readonly List<ZDOID> Spawned = new List<ZDOID>();

            // invasion
            public bool RaidStarted;

            // treasure
            public readonly List<TreasureSpot> Spots = new List<TreasureSpot>();

            // tournament
            public readonly List<string> Signups = new List<string>();                 // platform ids
            public readonly Dictionary<string, string> Names =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);      // platform id -> last seen name
            public bool SignupOpen = true;
            public float SignupClosesAt;
            public int Entrants;             // frozen when signups close (Signups shrinks each round)
            public int Round;
            public float RoundEndsAt;
            public readonly List<string> RoundPairs = new List<string>();               // flat: [0] vs [1], [2] vs [3], ...
            public readonly List<string> Winners = new List<string>();
        }

        private sealed class Vote
        {
            public string Topic = "";
            public string Action = "none";
            public long EndsTicksUtc;
            public long Starter;
            public readonly HashSet<string> Yes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> No = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static ActiveEvent _event;
        private static Vote _vote;

        private static readonly Dictionary<string, DateTime> WarpCooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> VoteStartCooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static bool _inited;
        private static bool _staleActiveCleared;
        private static bool _worldGenBroken;
        private static float _nextTick;
        private static float _nextTreasureTick;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableEvents = cfg.Bind("Features", "EnableEvents", false,
                    "Master switch for the events suite (warp hubs, admin-run events, player voting, seasonal global-key toggles). OFF by default: no chat command is claimed and no RPC does anything until an owner turns it on. Takes effect on server restart.");
                _warpCooldown = cfg.Bind("Features", "WarpCooldownSeconds", 60,
                    "Per-player cooldown between '!warp' uses, in seconds. 0 = none (the game's own 2 s teleport cooldown still applies).");
                _bossRushSequence = cfg.Bind("Features", "BossRushSequence", "Eikthyr,gd_king,Bonemass,Dragon,GoblinKing",
                    "Comma-separated creature prefab names the 'bossrush' event spawns in order. Names are resolved against the server's prefab list; unknown names are skipped with a log line.");
                _bossRushInterval = cfg.Bind("Features", "BossRushIntervalSeconds", 120,
                    "Seconds between spawns in the 'bossrush' event.");
                _bossRushLevel = cfg.Bind("Features", "BossRushLevel", 1,
                    "Star level applied to bossrush spawns (1 = no stars, max 10).");
                _treasureSpots = cfg.Bind("Features", "TreasureSpots", 5,
                    "Number of treasure points the 'treasure' event places (max 20).");
                _treasureRadius = cfg.Bind("Features", "TreasureRadius", 300f,
                    "Radius in metres around the starting admin within which treasure points are scattered.");
                _treasureFindRadius = cfg.Bind("Features", "TreasureFindRadius", 12f,
                    "How close (horizontal metres) a player must get to claim a treasure point. Arrival is detected by polling character positions every 5 s, so this should not be tiny.");
                _treasureReward = cfg.Bind("Features", "TreasureReward", 100,
                    "Currency paid to the first player at each treasure point. Requires the economy module to already hold balances; with no economy in use the find is announced and nothing is paid.");
                _treasureMarkerPrefab = cfg.Bind("Features", "TreasureMarkerPrefab", "",
                    "Optional prefab spawned as a visual marker at each treasure point (e.g. a chest). EMPTY BY DEFAULT: the hunt is coordinate-based and needs no world objects. Markers are best-effort, spawned EMPTY (a dedicated server cannot fill a container reliably) and removed when the event ends.");
                _tournamentArena = cfg.Bind("Features", "TournamentArenaWarp", "",
                    "Name of a warp (see the warps table) each tournament pair is teleported to. Empty = no teleports; the tournament is then announcements and bracket bookkeeping only.");
                _tournamentSignupSeconds = cfg.Bind("Features", "TournamentSignupSeconds", 120,
                    "How long '!join' stays open after a tournament starts.");
                _tournamentRoundSeconds = cfg.Bind("Features", "TournamentRoundSeconds", 300,
                    "Maximum length of a tournament round. If nobody dies in time the round is announced as 'no result' and BOTH players advance - the server cannot referee a PvP fight.");
                _votePassPercent = cfg.Bind("Features", "VotePassPercent", 60,
                    "Percentage of cast votes (yes / (yes+no)) required for a vote to pass.");
                _voteMinVoters = cfg.Bind("Features", "VoteMinVoters", 2,
                    "Minimum number of players who must vote for the result to count. Below this the vote fails.");
                _enablePlayerVoteStart = cfg.Bind("Features", "EnablePlayerVoteStart", false,
                    "Let ordinary players start a night-skip vote with '!vote skipnight'. Off = only admins can start votes.");
                _playerVoteCooldown = cfg.Bind("Features", "PlayerVoteCooldownSeconds", 600,
                    "Per-player cooldown between player-started votes (only meaningful with EnablePlayerVoteStart).");
                _seasonAllowAnyKey = cfg.Bind("Features", "SeasonAllowAnyGlobalKey", false,
                    "Let AP_SrvSeasonSet set ANY global key name, not only the ones this game build actually defines. Off = unknown names are refused with an explanation.");
            }

            // Every new admin RPC must be known to the audit chokepoint. Grants: running/stopping an event and
            // starting a vote are moderator work; defining warps and flipping world-wide global keys change the
            // server's rules, so they stay owner-only (null) once tiered roles are enforced.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEventStateReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEventStart", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEventStop", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvVoteStart", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWarpSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSeasonSet", null);

            ApplyPatch("Wave6EventRpcRegistration", typeof(Wave6RpcRegisterPatch),
                "event / warp / vote / season RPCs unavailable");

            // Tournament results come from Wave34Core's death observer (TIER-VANILLA: it watches the character
            // ZDO's "dead" flag, so unmodded duellists are scored too). Subscribed unconditionally and cheap —
            // the handler returns immediately unless a tournament round is actually running.
            try { Wave34Core.OnDeath += OnDeathObserved; }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: death subscription failed (tournament results unavailable): {e.Message}"); }

            // Chat commands are registered ONLY when the suite is enabled. With the master switch off the '!'
            // registry never claims these words and chat passes through exactly as before the upgrade.
            if (!EventsOn) return;
            try
            {
                Wave1Chat.RegisterChatCommand("warps", CmdWarps);
                Wave1Chat.RegisterChatCommand("warp", CmdWarp);
                Wave1Chat.RegisterChatCommand("join", CmdJoin);
                Wave1Chat.RegisterChatCommand("yes", CmdYes);
                Wave1Chat.RegisterChatCommand("no", CmdNo);
                Wave1Chat.RegisterChatCommand("votestatus", CmdVoteStatus);
                Wave1Chat.RegisterChatCommand("vote", CmdVote);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: chat command registration failed: {e.Message}"); }
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
            if (!EventsOn) return;
            var now = Time.unscaledTime;

            if (now >= _nextTick)
            {
                _nextTick = now + 1f;
                try { ClearStaleRows(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: stale-state cleanup failed: {e.Message}"); }
                try { StepEvent(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: event tick failed: {e.Message}"); }
                try { StepVote(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: vote tick failed: {e.Message}"); }
                try { PruneCooldowns(); }
                catch (Exception) { }
            }

            if (now >= _nextTreasureTick)
            {
                _nextTreasureTick = now + 5f;
                try { PollTreasure(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: treasure poll failed: {e.Message}"); }
            }
        }

        // Neither an event nor a vote can survive a restart: their timers are session state, participants have
        // relogged, and the ZDOIDs of what an event spawned are gone from our bookkeeping. Rather than pretend
        // to resume, the stale rows are dropped once the store becomes readable and the consequence is logged.
        // Runs once per process; the persisted rows exist so the panel and an operator can SEE what was running
        // when the server went down, not so it can be replayed.
        private static void ClearStaleRows()
        {
            if (_staleActiveCleared || _event != null || _vote != null) return;
            if (!FeatureStore.Ready) return;
            _staleActiveCleared = true;

            var t = FeatureStore.Table(TblEvents);
            string raw;
            if (t.TryGetValue(KeyActive, out raw) && !string.IsNullOrEmpty(raw))
            {
                t.Remove(KeyActive);
                FeatureStore.SaveTable(TblEvents);
                CompanionPlugin.FeatureLog(
                    $"Wave6: event '{raw.Split('|')[0]}' did not survive the restart - cleared. Anything it spawned is still in the world and must be removed manually.");
            }

            var v = FeatureStore.Table(TblVotes);
            if (v.ContainsKey(KeyVote))
            {
                v.Remove(KeyVote);
                FeatureStore.SaveTable(TblVotes);
                CompanionPlugin.FeatureLog("Wave6: a vote was open when the server stopped - cleared (tallies are session state and cannot be resumed).");
            }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave6RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvEventStateReq", new Action<long>(OnEventStateReq));
                    ZRoutedRpc.instance.Register("AP_SrvEventStop", new Action<long>(OnEventStopRpc));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvEventStart", OnEventStart);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvWarpSet", OnWarpSet);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvVoteStart", OnVoteStart);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSeasonSet", OnSeasonSet);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave6 RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. warp hubs ====================

        // ZPackage: string name, float x, float y, float z, bool adminOnly, bool remove.
        private static void OnWarpSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWarpSet")) return;
            if (!RequireEnabled(sender)) return;

            string name; float x, y, z; bool adminOnly, remove;
            try
            {
                name = CleanName(pkg.ReadString());
                x = pkg.ReadSingle();
                y = pkg.ReadSingle();
                z = pkg.ReadSingle();
                adminOnly = pkg.ReadBool();
                remove = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvWarpSet: malformed packet dropped ({e.Message})"); return; }
            if (name.Length == 0)
            {
                CompanionPlugin.NotifySender(sender, "Warp name is empty or invalid (letters, digits, - and _ only).");
                return;
            }

            var t = FeatureStore.Table(TblWarps);
            var key = FindWarpKey(t, name);
            var admin = CompanionPlugin.SenderDisplayName(sender);

            if (remove)
            {
                if (key == null)
                {
                    CompanionPlugin.NotifySender(sender, $"No warp named '{name}'.");
                    CompanionPlugin.SrvAudit(sender, "WARP-REMOVE", $"name={name} result=not-found");
                    return;
                }
                t.Remove(key);
                FeatureStore.SaveTable(TblWarps);
                CompanionPlugin.SrvAudit(sender, "WARP-REMOVE", $"name={key}");
                CompanionPlugin.FeatureLog($"Warp '{key}' removed by {admin}");
                Wave1AuditRpc.PostModLog($"WARP REMOVE {key} (by {admin})");
                CompanionPlugin.NotifySender(sender, $"Warp '{key}' removed.");
                return;
            }

            if (key == null && t.Count >= MaxWarps)
            {
                CompanionPlugin.NotifySender(sender, $"Warp limit reached ({MaxWarps}). Remove one first.");
                return;
            }
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                CompanionPlugin.NotifySender(sender, "Warp coordinates are not a finite position.");
                return;
            }

            t[key ?? name] = $"{F(x)}|{F(y)}|{F(z)}|{(adminOnly ? 1 : 0)}";
            FeatureStore.SaveTable(TblWarps);
            CompanionPlugin.SrvAudit(sender, "WARP-SET", $"name={key ?? name} pos={F(x)},{F(y)},{F(z)} adminOnly={adminOnly}");
            CompanionPlugin.FeatureLog($"Warp '{key ?? name}' set to {F(x)},{F(y)},{F(z)} (adminOnly={adminOnly}) by {admin}");
            Wave1AuditRpc.PostModLog($"WARP SET {key ?? name} {F(x)},{F(y)},{F(z)} adminOnly={adminOnly} (by {admin})");
            CompanionPlugin.NotifySender(sender, $"Warp '{key ?? name}' saved.");
        }

        /// <summary>
        /// Look up a warp by name (case-insensitive). False when it does not exist. Exposed so sibling modules
        /// resolve warps through one code path instead of re-parsing the table.
        /// </summary>
        internal static bool TryGetWarp(string name, out Vector3 pos, out bool adminOnly)
        {
            pos = Vector3.zero;
            adminOnly = false;
            var clean = CleanName(name);
            if (clean.Length == 0) return false;
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(TblWarps); }
            catch (Exception) { return false; }
            var key = FindWarpKey(t, clean);
            return key != null && ParseWarp(t[key], out pos, out adminOnly);
        }

        /// <summary>
        /// Teleport a connected peer to a named warp. TIER-VANILLA: "RPC_TeleportPlayer" is registered by
        /// Chat.Awake on EVERY client (Chat.cs:130) and its handler has no sender and no admin check, so this
        /// reaches players with no mod at all. Returns "" on success or a plain-English failure reason.
        /// </summary>
        internal static string WarpPeer(long uid, string warpName, bool ignoreCooldown)
        {
            Vector3 pos; bool adminOnly;
            if (!TryGetWarp(warpName, out pos, out adminOnly))
                return $"There is no warp named '{CleanName(warpName)}'.";

            var host = CompanionPlugin.SenderPlatformId(uid);
            if (adminOnly && !CompanionPlugin.FeatureIsAdminId(host)) return "That warp is admin-only.";

            var track = !ignoreCooldown && WarpCooldownSeconds > 0 && !string.IsNullOrEmpty(host) && host != "?";
            if (track)
            {
                DateTime last;
                if (WarpCooldowns.TryGetValue(host, out last))
                {
                    var left = WarpCooldownSeconds - (int)(DateTime.UtcNow - last).TotalSeconds;
                    if (left > 0) return $"Warp cooldown: {left} second(s) left.";
                }
            }

            // The cooldown is stamped only AFTER the teleport was actually sent — a failed warp must not cost
            // the player a minute of waiting.
            if (!TeleportPeer(uid, pos)) return "Teleport failed (the server could not reach that player).";
            if (track) WarpCooldowns[host] = DateTime.UtcNow;
            return "";
        }

        // The single teleport call in this file. distantTeleport:true runs the client's normal loading screen,
        // which is what a long-distance warp needs. Player.TeleportTo silently refuses inside its own 2 s
        // cooldown (Player.cs:5467-5493) — that is why every repeatable use above is cooldown-gated.
        private static bool TeleportPeer(long uid, Vector3 pos)
        {
            try
            {
                if (ZRoutedRpc.instance == null) return false;
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, "RPC_TeleportPlayer", pos, Quaternion.identity, true);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave6: teleport to {uid} failed: {e.Message}");
                return false;
            }
        }

        private static void CmdWarps(long sender, string args)
        {
            if (!EventsOn) return;
            var isAdmin = CompanionPlugin.FeatureIsAdminId(CompanionPlugin.SenderPlatformId(sender));
            var names = new List<string>();
            foreach (var kv in FeatureStore.Table(TblWarps))
            {
                Vector3 pos; bool adminOnly;
                if (!ParseWarp(kv.Value, out pos, out adminOnly)) continue;
                if (adminOnly && !isAdmin) continue;
                names.Add(adminOnly ? kv.Key + "*" : kv.Key);
                if (names.Count >= WarpShipCap) break;
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            Wave1Moderation.SendPlayerText(sender, names.Count == 0
                ? "No warps are defined."
                : Clamp("Warps: " + string.Join(", ", names.ToArray()), MaxTextLen));
        }

        private static void CmdWarp(long sender, string args)
        {
            if (!EventsOn) return;
            var name = CleanName(args);
            if (name.Length == 0)
            {
                Wave1Moderation.SendPlayerText(sender, "Usage: !warp <name>   (see !warps)");
                return;
            }
            var err = WarpPeer(sender, name, false);
            if (err.Length > 0) { Wave1Moderation.SendPlayerText(sender, err); return; }
            Wave1Moderation.SendPlayerText(sender, $"Warping to {name}...");
            CompanionPlugin.FeatureLog($"Warp: {CompanionPlugin.SenderDisplayName(sender)} -> {name}");
        }

        // ==================== 2. admin events ====================

        // ZPackage: string kind, int minutes, string param.
        private static void OnEventStart(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvEventStart")) return;
            if (!RequireEnabled(sender)) return;

            string kind, param; int minutes;
            try
            {
                kind = CleanName(pkg.ReadString()).ToLowerInvariant();
                minutes = pkg.ReadInt();
                param = CleanText(pkg.ReadString());
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvEventStart: malformed packet dropped ({e.Message})"); return; }

            minutes = Mathf.Clamp(minutes, 1, MaxEventMinutes);
            var err = StartEvent(kind, minutes, param, sender,
                CompanionPlugin.SenderDisplayName(sender), SenderPosition(sender));

            CompanionPlugin.SrvAudit(sender, "EVENT-START",
                $"kind={kind} minutes={minutes} param={param} result={(err.Length == 0 ? "ok" : err)}");
            CompanionPlugin.NotifySender(sender, err.Length == 0
                ? $"Event '{kind}' started for {minutes} min."
                : "Event not started: " + err);
        }

        /// <summary>
        /// Start one of the four event kinds ("bossrush" | "treasure" | "invasion" | "tournament"). Returns ""
        /// on success or a plain-English reason. Only ONE event may run at a time. Also used by the vote
        /// system's "event:&lt;kind&gt;" action, which is why it takes an explicit starter/centre.
        /// </summary>
        internal static string StartEvent(string kind, int minutes, string param, long starter, string starterName, Vector3 centre)
        {
            if (!EventsOn) return "the events suite is disabled (Features.EnableEvents).";
            if (_event != null) return $"'{_event.Kind}' is already running - stop it first.";
            if (string.IsNullOrEmpty(kind)) return "no event kind was given.";
            minutes = Mathf.Clamp(minutes, 1, MaxEventMinutes);

            var ev = new ActiveEvent
            {
                Kind = kind,
                Param = param ?? "",
                EndsTicksUtc = DateTime.UtcNow.AddMinutes(minutes).Ticks,
                Starter = starter,
                StarterName = string.IsNullOrEmpty(starterName) ? "an admin" : starterName,
                Centre = centre,
            };

            string err;
            switch (kind)
            {
                case "bossrush": err = BeginBossRush(ev); break;
                case "invasion": err = BeginInvasion(ev); break;
                case "treasure": err = BeginTreasure(ev); break;
                case "tournament": err = BeginTournament(ev); break;
                default: return $"unknown event kind '{kind}' (bossrush, invasion, treasure, tournament).";
            }
            if (err.Length > 0)
            {
                // Nothing is left half-started: each Begin* undoes its own partial work before returning.
                CleanupWorldObjects(ev);
                return err;
            }

            _event = ev;
            PersistActive(ev);
            CompanionPlugin.FeatureLog($"Event '{kind}' started by {ev.StarterName} for {minutes} min (param='{ev.Param}')");
            Wave1AuditRpc.PostModLog($"EVENT START {kind} {minutes}m param={ev.Param} (by {ev.StarterName})");
            try
            {
                Wave34Core.Enqueue("Event started",
                    $"**{kind}** for {minutes} min, started by {ev.StarterName}.", Wave34Core.ColorBoss);
            }
            catch (Exception) { }
            return "";
        }

        private static void OnEventStopRpc(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvEventStop")) return;
            if (!RequireEnabled(sender)) return;
            if (_event == null)
            {
                CompanionPlugin.NotifySender(sender, "No event is running.");
                return;
            }
            var kind = _event.Kind;
            CompanionPlugin.SrvAudit(sender, "EVENT-STOP", $"kind={kind}");
            StopEvent($"stopped by {CompanionPlugin.SenderDisplayName(sender)}");
            CompanionPlugin.NotifySender(sender, $"Event '{kind}' stopped.");
        }

        /// <summary>
        /// End the running event and restore everything it changed: creatures/markers it spawned are removed,
        /// a raid it started is reset. No-op when nothing is running.
        /// </summary>
        internal static void StopEvent(string reason)
        {
            var ev = _event;
            if (ev == null) return;
            _event = null;

            if (ev.RaidStarted) ResetRaid();
            var removed = CleanupWorldObjects(ev);

            var summary = SummariseResult(ev);
            AnnounceAll($"Event '{ev.Kind}' has ended. {summary}");
            CompanionPlugin.FeatureLog($"Event '{ev.Kind}' ended ({reason}); removed {removed} spawned object(s). {summary}");
            Wave1AuditRpc.PostModLog($"EVENT END {ev.Kind} ({reason}) {summary}");
            try { Wave34Core.Enqueue("Event ended", $"**{ev.Kind}** - {reason}. {summary}", Wave34Core.ColorInfo); }
            catch (Exception) { }

            try
            {
                var t = FeatureStore.Table(TblEvents);
                if (t.Remove(KeyActive)) FeatureStore.SaveTable(TblEvents);
            }
            catch (Exception) { }
        }

        private static void PersistActive(ActiveEvent ev)
        {
            try
            {
                var t = FeatureStore.Table(TblEvents);
                t[KeyActive] = $"{ev.Kind}|{ev.EndsTicksUtc}|{ev.Param}";
                FeatureStore.SaveTable(TblEvents);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: could not persist the active event: {e.Message}"); }
        }

        private static void StepEvent(float now)
        {
            var ev = _event;
            if (ev == null) return;

            if (DateTime.UtcNow.Ticks >= ev.EndsTicksUtc) { StopEvent("time expired"); return; }

            switch (ev.Kind)
            {
                case "bossrush": StepBossRush(ev, now); break;
                case "treasure": break;                     // driven by the 5 s position poll
                case "invasion": break;                     // RandEventSystem drives itself
                case "tournament": StepTournament(ev, now); break;
            }
        }

        // ---------- bossrush ----------

        // Reuses CompanionPlugin.OnServerSpawn's mechanism exactly (see SpawnCreature below): prefab lookup
        // through ZNetScene with an ObjectDB fallback, Instantiate at a small random offset, SetLevel for
        // stars, and the resulting ZDOID remembered so the stop path can remove it.
        private static string BeginBossRush(ActiveEvent ev)
        {
            var raw = ev.Param.Length > 0 ? ev.Param : BossRushSequence;
            foreach (var piece in (raw ?? "").Split(','))
            {
                var n = CleanName(piece);
                if (n.Length == 0) continue;
                if (ResolvePrefab(n) == null)
                {
                    CompanionPlugin.FeatureLog($"Wave6 bossrush: prefab '{n}' not found on this server - skipped.");
                    continue;
                }
                ev.Bosses.Add(n);
                if (ev.Bosses.Count >= MaxBossesInSequence) break;
            }
            if (ev.Bosses.Count == 0)
                return "none of the configured bossrush prefabs exist on this server (check Features.BossRushSequence).";
            if (ZNetScene.instance == null) return "the world is not loaded yet.";

            AnnounceAll($"BOSS RUSH! {ev.Bosses.Count} boss(es) will appear near {F(ev.Centre.x)}, {F(ev.Centre.z)} every {BossRushInterval}s.");
            ev.NextSpawnAt = Time.unscaledTime;   // first boss on the very next tick
            return "";
        }

        private static void StepBossRush(ActiveEvent ev, float now)
        {
            if (ev.NextBoss >= ev.Bosses.Count) return;
            if (now < ev.NextSpawnAt) return;
            ev.NextSpawnAt = now + BossRushInterval;

            var name = ev.Bosses[ev.NextBoss++];
            var spawned = SpawnCreature(name, ev.Centre, BossRushLevel, ev.Spawned);
            if (spawned)
            {
                AnnounceAll($"Boss rush {ev.NextBoss}/{ev.Bosses.Count}: {name} has appeared near {F(ev.Centre.x)}, {F(ev.Centre.z)}!");
                CompanionPlugin.FeatureLog($"Wave6 bossrush: spawned {name} at {F(ev.Centre.x)},{F(ev.Centre.z)}");
                try { Wave34Core.Enqueue("Boss rush", $"{name} spawned near {F(ev.Centre.x)}, {F(ev.Centre.z)}.", Wave34Core.ColorBoss); }
                catch (Exception) { }
            }
            else
            {
                CompanionPlugin.FeatureLog($"Wave6 bossrush: spawn of '{name}' failed - continuing with the sequence.");
            }
        }

        /// <summary>
        /// Spawn ONE creature server-side. This is CompanionPlugin.OnServerSpawn's creature branch
        /// (CompanionPlugin.cs:251-270) with the same prefab resolution, the same +/-1.5 m scatter and the same
        /// SetLevel clamp — deliberately not a new spawn path. The created ZDOID is appended to <paramref
        /// name="batch"/> so StopEvent can remove exactly what this event added.
        /// </summary>
        private static bool SpawnCreature(string prefabName, Vector3 pos, int level, List<ZDOID> batch)
        {
            try
            {
                var prefab = ResolvePrefab(prefabName);
                if (prefab == null) return false;
                var offset = new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0.5f, UnityEngine.Random.Range(-1.5f, 1.5f));
                var go = UnityEngine.Object.Instantiate(prefab, pos + offset, Quaternion.identity);
                var nview = go.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo != null && batch != null) batch.Add(zdo.m_uid);
                var character = go.GetComponent<Character>();
                if (character != null && level > 1) character.SetLevel(Mathf.Clamp(level, 1, 10));
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave6: spawn of '{prefabName}' failed: {e.Message}");
                return false;
            }
        }

        // Same two-step lookup OnServerSpawn uses (CompanionPlugin.cs:223-225).
        private static GameObject ResolvePrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(name);
                return prefab;
            }
            catch (Exception) { return null; }
        }

        // Removal follows CompanionPlugin.OnServerUndo (CompanionPlugin.cs:331-351): claim ownership first,
        // because ZDOMan.DestroyZDO is a silent no-op for a ZDO the caller does not own.
        private static int CleanupWorldObjects(ActiveEvent ev)
        {
            var removed = 0;
            if (ev == null) return 0;

            var ids = new List<ZDOID>(ev.Spawned);
            foreach (var spot in ev.Spots)
                if (!spot.Marker.IsNone()) ids.Add(spot.Marker);
            ev.Spawned.Clear();

            foreach (var id in ids)
            {
                try
                {
                    if (ZDOMan.instance == null) break;
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null) continue;   // already gone (killed, despawned, or a world reload)
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
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: cleanup of a spawned object failed: {e.Message}"); }
            }
            foreach (var spot in ev.Spots) spot.Marker = ZDOID.None;
            return removed;
        }

        // ---------- invasion ----------

        // Reuses CompanionPlugin.OnServerEvent's mechanism (CompanionPlugin.cs:654-660):
        // RandEventSystem.instance.SetRandomEventByName(name, pos). Nothing new is invented; the only addition
        // is validating the name first so a typo produces an explanation instead of a silent no-op.
        private static string BeginInvasion(ActiveEvent ev)
        {
            var res = RandEventSystem.instance;
            if (res == null) return "RandEventSystem is not available on this server yet.";

            var name = CleanName(ev.Param);
            if (name.Length == 0) return "invasion needs the name of a raid event as its parameter, e.g. " + SampleEventNames();
            bool have;
            try { have = res.HaveEvent(name); }
            catch (Exception e) { return "the raid list could not be read on this game build: " + e.Message; }
            if (!have) return $"'{name}' is not an enabled raid on this build. Try one of: {SampleEventNames()}";

            try { res.SetRandomEventByName(name, ev.Centre); }
            catch (Exception e) { return "the raid could not be started: " + e.Message; }

            ev.RaidStarted = true;
            AnnounceAll($"INVASION! '{name}' has been triggered near {F(ev.Centre.x)}, {F(ev.Centre.z)}.");
            return "";
        }

        private static void ResetRaid()
        {
            try
            {
                var res = RandEventSystem.instance;
                if (res != null) res.ResetRandomEvent();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: raid reset failed: {e.Message}"); }
        }

        // Read straight off RandEventSystem.m_events (a public List<RandomEvent>, RandEventSystem.cs:35), but
        // reflectively and inside try/catch so a renamed field degrades to "no examples" instead of throwing.
        private static string SampleEventNames()
        {
            try
            {
                var res = RandEventSystem.instance;
                if (res == null) return "(none available)";
                var list = AccessTools.Field(typeof(RandEventSystem), "m_events")?.GetValue(res) as System.Collections.IList;
                if (list == null) return "(event list unreadable on this build)";
                var names = new List<string>();
                foreach (var ev in list)
                {
                    if (ev == null) continue;
                    var n = AccessTools.Field(ev.GetType(), "m_name")?.GetValue(ev) as string;
                    var enabled = AccessTools.Field(ev.GetType(), "m_enabled")?.GetValue(ev);
                    if (string.IsNullOrEmpty(n)) continue;
                    if (enabled is bool && !(bool)enabled) continue;
                    names.Add(n);
                    if (names.Count >= 6) break;
                }
                return names.Count == 0 ? "(none enabled)" : string.Join(", ", names.ToArray());
            }
            catch (Exception) { return "(event list unreadable)"; }
        }

        // ---------- treasure ----------

        // COORDINATE-BASED by default, and that is the honest design: a dedicated server can instantiate a
        // prefab, but it cannot reliably fill a Container's inventory, so a "treasure chest" would be an empty
        // box. The hunt therefore announces coordinates and detects arrival from character ZDO positions
        // (TIER-VANILLA — unmodded players are detected exactly as well). Features.TreasureMarkerPrefab may
        // opt into a purely cosmetic marker object, spawned empty and removed when the event ends.
        private static string BeginTreasure(ActiveEvent ev)
        {
            var count = TreasureSpotCount;
            int parsed;
            if (ev.Param.Length > 0 && int.TryParse(ev.Param.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                count = Mathf.Clamp(parsed, 1, MaxTreasureSpots);

            var radius = TreasureRadius;
            for (var i = 0; i < count; i++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var dist = Mathf.Sqrt(UnityEngine.Random.Range(0.04f, 1f)) * radius;   // uniform-ish over the disc, never on top of the admin
                var x = ev.Centre.x + Mathf.Cos(ang) * dist;
                var z = ev.Centre.z + Mathf.Sin(ang) * dist;
                var y = GeneratedGroundY(x, z);
                if (float.IsNaN(y)) y = ev.Centre.y;
                ev.Spots.Add(new TreasureSpot { Pos = new Vector3(x, y, z) });
            }
            if (ev.Spots.Count == 0) return "no treasure points could be generated.";

            var marker = TreasureMarkerPrefab;
            if (marker.Length > 0)
            {
                if (ResolvePrefab(marker) == null)
                    CompanionPlugin.FeatureLog($"Wave6 treasure: marker prefab '{marker}' not found - running a coordinate-only hunt.");
                else
                    foreach (var spot in ev.Spots)
                    {
                        var batch = new List<ZDOID>();
                        if (SpawnCreature(marker, spot.Pos, 1, batch) && batch.Count > 0) spot.Marker = batch[0];
                    }
            }

            AnnounceAll($"TREASURE HUNT! {ev.Spots.Count} site(s). First to reach each one wins{(TreasureReward > 0 ? " " + TreasureReward + " coins" : "")}. Coordinates in chat.");
            var sb = new StringBuilder();
            for (var i = 0; i < ev.Spots.Count; i++)
            {
                if (sb.Length > 0) sb.Append("   ");
                sb.Append('#').Append(i + 1).Append(' ').Append(F(ev.Spots[i].Pos.x)).Append(", ").Append(F(ev.Spots[i].Pos.z));
            }
            AnnounceAll(Clamp(sb.ToString(), MaxTextLen));
            return "";
        }

        // One ZNet.GetAllCharacterZDOS() call every 5 s (the same bounded lookup Wave34Core's death poll uses,
        // ZNet.cs:1839) crossed with at most 20 spots. Horizontal distance only: a player on a cliff above the
        // point has still found it, and the server's generated-terrain Y is an approximation anyway.
        private static void PollTreasure()
        {
            var ev = _event;
            if (ev == null || ev.Kind != "treasure" || ev.Spots.Count == 0) return;
            var znet = ZNet.instance;
            if (znet == null) return;

            List<ZDO> chars;
            try { chars = znet.GetAllCharacterZDOS(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: GetAllCharacterZDOS failed: {e.Message}"); return; }
            if (chars == null || chars.Count == 0) return;

            var find = TreasureFindRadius;
            var remaining = 0;
            foreach (var spot in ev.Spots)
            {
                if (spot.Found) continue;
                remaining++;
                for (var i = 0; i < chars.Count; i++)
                {
                    var zdo = chars[i];
                    if (zdo == null) continue;
                    Vector3 p;
                    try { if (!zdo.IsValid()) continue; p = zdo.GetPosition(); }
                    catch (Exception) { continue; }
                    var dx = p.x - spot.Pos.x;
                    var dz = p.z - spot.Pos.z;
                    if (dx * dx + dz * dz > find * find) continue;

                    var peer = PeerOfCharacter(zdo.m_uid);
                    var name = peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : "someone";
                    var host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                    spot.Found = true;
                    spot.FinderName = name;
                    remaining--;
                    AwardTreasure(host, name);
                    break;
                }
            }
            if (remaining == 0) StopEvent("all treasure found");
        }

        // COUPLING with the wave-6 ECONOMY module, stated plainly: the payout is currency in the "eco" table
        // ("id -> balance|lifetime|name"). We do NOT hand-write that row — Wave6Economy.Grant is the single
        // mutation door that owns clamping, lifetime bookkeeping, persistence and the audit line, and it is
        // documented as callable by sibling wave modules for exactly this (event rewards). When the economy is
        // switched off there is no currency to award, so the find is announced and nothing is paid.
        private static void AwardTreasure(string platformId, string playerName)
        {
            var reward = TreasureReward;
            var paid = false;

            if (reward > 0 && !string.IsNullOrEmpty(platformId) && platformId != "?")
            {
                try
                {
                    if (Wave6Economy.EconomyEnabled())
                    {
                        Wave6Economy.Grant(platformId, reward, "treasure hunt", 0L);
                        paid = true;
                    }
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: treasure payout failed for {platformId}: {e.Message}"); }
            }

            AnnounceAll(paid
                ? $"{playerName} found a treasure site and earned {reward} coins!"
                : $"{playerName} found a treasure site!");
            CompanionPlugin.FeatureLog($"Wave6 treasure: {playerName} ({platformId}) claimed a site (paid={paid})");
            Wave1AuditRpc.PostModLog($"TREASURE FOUND {playerName} ({platformId}) reward={(paid ? reward : 0)}");
            if (!paid && reward > 0)
                CompanionPlugin.FeatureLog("Wave6 treasure: no payout - the economy module is disabled, so no currency exists to award.");
        }

        // ---------- tournament ----------

        // A HELPER, not a referee. The server cannot enforce PvP rules: it cannot force PvP flags on, cannot
        // stop a third party interfering, cannot see who dealt the killing blow and cannot tell a duel death
        // from a drowning. All it does is take signups, pair people, teleport them to an arena, announce the
        // rounds, and record who died first. Everything else is the admin's job, and the announcements say so.
        private static string BeginTournament(ActiveEvent ev)
        {
            var arena = ev.Param.Length > 0 ? CleanName(ev.Param) : TournamentArena;
            ev.Param = arena;
            if (arena.Length > 0)
            {
                Vector3 pos; bool adminOnly;
                if (!TryGetWarp(arena, out pos, out adminOnly))
                {
                    CompanionPlugin.FeatureLog($"Wave6 tournament: arena warp '{arena}' does not exist - running without teleports.");
                    ev.Param = "";
                }
            }
            ev.SignupOpen = true;
            ev.SignupClosesAt = Time.unscaledTime + TournamentSignupSeconds;
            AnnounceAll($"TOURNAMENT! Type !join within {TournamentSignupSeconds}s to enter. The server does NOT enforce PvP rules - an admin referees.");
            return "";
        }

        private static void StepTournament(ActiveEvent ev, float now)
        {
            if (ev.SignupOpen)
            {
                if (now < ev.SignupClosesAt) return;
                ev.SignupOpen = false;
                if (ev.Signups.Count < 2)
                {
                    AnnounceAll("Tournament cancelled: fewer than two entrants.");
                    StopEvent("not enough entrants");
                    return;
                }
                ev.Entrants = ev.Signups.Count;
                AnnounceAll($"Signups closed with {ev.Entrants} entrant(s).");
                StartRound(ev, now);
                return;
            }

            if (ev.RoundPairs.Count == 0) return;
            if (now < ev.RoundEndsAt) return;

            // Round timeout with nobody dead: the server has no way to pick a winner, so it says so and lets
            // both through rather than inventing a result.
            var stalled = new List<string>();
            for (var i = 0; i + 1 < ev.RoundPairs.Count; i += 2)
            {
                var a = ev.RoundPairs[i];
                var b = ev.RoundPairs[i + 1];
                if (a.Length == 0 && b.Length == 0) continue;
                if (a.Length > 0) stalled.Add(a);
                if (b.Length > 0) stalled.Add(b);
            }
            if (stalled.Count > 0)
            {
                AnnounceAll($"Round {ev.Round} timed out with no result for {stalled.Count / 2} pair(s) - both advance; an admin decides.");
                foreach (var id in stalled) ev.Winners.Add(id);
            }
            ev.RoundPairs.Clear();
            AdvanceRound(ev, Time.unscaledTime);
        }

        private static void StartRound(ActiveEvent ev, float now)
        {
            ev.Round++;
            ev.RoundPairs.Clear();
            ev.Winners.Clear();

            // Fisher-Yates on a copy: pairings must not be the join order every single round.
            var pool = new List<string>(ev.Signups);
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = UnityEngine.Random.Range(0, i + 1);
                var tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
            }

            var arenaOk = false;
            Vector3 arena = Vector3.zero;
            if (ev.Param.Length > 0)
            {
                bool adminOnly;
                arenaOk = TryGetWarp(ev.Param, out arena, out adminOnly);
            }

            var pairs = 0;
            for (var i = 0; i + 1 < pool.Count; i += 2)
            {
                var a = pool[i];
                var b = pool[i + 1];
                ev.RoundPairs.Add(a);
                ev.RoundPairs.Add(b);
                pairs++;
                AnnounceAll($"Round {ev.Round} - {NameOf(ev, a)} vs {NameOf(ev, b)}");
                if (!arenaOk) continue;
                TeleportIdTo(a, arena + new Vector3(-ArenaSpread, 0f, 0f));
                TeleportIdTo(b, arena + new Vector3(ArenaSpread, 0f, 0f));
            }
            if (pool.Count % 2 == 1)
            {
                var bye = pool[pool.Count - 1];
                ev.Winners.Add(bye);
                AnnounceAll($"Round {ev.Round} - {NameOf(ev, bye)} gets a bye.");
            }

            if (pairs == 0) { FinishTournament(ev); return; }
            ev.RoundEndsAt = now + TournamentRoundSeconds;
            CompanionPlugin.FeatureLog($"Wave6 tournament: round {ev.Round} started with {pairs} pair(s){(arenaOk ? " (teleported to arena)" : " (no arena warp - players travel themselves)")}");
        }

        private static void AdvanceRound(ActiveEvent ev, float now)
        {
            var survivors = new List<string>(ev.Winners);
            ev.Signups.Clear();
            ev.Signups.AddRange(survivors);
            if (ev.Signups.Count <= 1) { FinishTournament(ev); return; }
            StartRound(ev, now);
        }

        private static void FinishTournament(ActiveEvent ev)
        {
            var champion = ev.Signups.Count == 1 ? NameOf(ev, ev.Signups[0])
                : ev.Winners.Count == 1 ? NameOf(ev, ev.Winners[0]) : "";
            AnnounceAll(champion.Length > 0
                ? $"Tournament over - the champion is {champion}!"
                : "Tournament over - no single champion; an admin decides.");
            CompanionPlugin.FeatureLog($"Wave6 tournament finished (champion: {(champion.Length > 0 ? champion : "undecided")})");
            Wave1AuditRpc.PostModLog($"TOURNAMENT END champion={(champion.Length > 0 ? champion : "undecided")}");
            StopEvent("tournament finished");
        }

        // Wave34Core.OnDeath is TIER-VANILLA (character ZDO "dead" flag), so unmodded duellists are scored too.
        // A death only counts while a round is live and only for someone actually in a pair — a duellist
        // drowning between rounds is not a result.
        private static void OnDeathObserved(Wave34Core.DeathInfo info)
        {
            var ev = _event;
            if (ev == null || ev.Kind != "tournament" || ev.RoundPairs.Count == 0) return;
            var id = info.PlatformId;
            if (string.IsNullOrEmpty(id) || id == "?") return;

            for (var i = 0; i + 1 < ev.RoundPairs.Count; i += 2)
            {
                var a = ev.RoundPairs[i];
                var b = ev.RoundPairs[i + 1];
                string loser = null, winner = null;
                if (a.Length > 0 && Wave1AuditRpc.SameId(a, id)) { loser = a; winner = b; }
                else if (b.Length > 0 && Wave1AuditRpc.SameId(b, id)) { loser = b; winner = a; }
                if (loser == null) continue;

                ev.RoundPairs[i] = "";
                ev.RoundPairs[i + 1] = "";
                if (winner.Length > 0) ev.Winners.Add(winner);
                AnnounceAll($"{NameOf(ev, winner)} beats {NameOf(ev, loser)} in round {ev.Round}.");
                CompanionPlugin.FeatureLog($"Wave6 tournament: {NameOf(ev, winner)} beat {NameOf(ev, loser)} (round {ev.Round})");

                var live = 0;
                for (var k = 0; k < ev.RoundPairs.Count; k++) if (ev.RoundPairs[k].Length > 0) live++;
                if (live == 0)
                {
                    ev.RoundPairs.Clear();
                    AdvanceRound(ev, Time.unscaledTime);
                }
                return;
            }
        }

        private static void CmdJoin(long sender, string args)
        {
            if (!EventsOn) return;
            var ev = _event;
            if (ev == null || ev.Kind != "tournament")
            {
                Wave1Moderation.SendPlayerText(sender, "No tournament is running.");
                return;
            }
            if (!ev.SignupOpen)
            {
                Wave1Moderation.SendPlayerText(sender, "Signups are closed.");
                return;
            }
            var host = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(host) || host == "?") return;
            var name = CompanionPlugin.SenderDisplayName(sender);
            ev.Names[host] = name;

            foreach (var id in ev.Signups)
                if (Wave1AuditRpc.SameId(id, host))
                {
                    Wave1Moderation.SendPlayerText(sender, "You are already entered.");
                    return;
                }
            if (ev.Signups.Count >= MaxParticipants)
            {
                Wave1Moderation.SendPlayerText(sender, "The tournament is full.");
                return;
            }
            ev.Signups.Add(host);
            Wave1Moderation.SendPlayerText(sender, $"You are entered ({ev.Signups.Count} so far).");
            AnnounceAll($"{name} joined the tournament ({ev.Signups.Count}).");
        }

        private static string NameOf(ActiveEvent ev, string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return "?";
            string n;
            if (ev != null && ev.Names.TryGetValue(platformId, out n) && !string.IsNullOrEmpty(n)) return n;
            var peer = PeerById(platformId);
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : platformId;
        }

        private static void TeleportIdTo(string platformId, Vector3 pos)
        {
            var peer = PeerById(platformId);
            if (peer == null) return;
            TeleportPeer(peer.m_uid, pos);
        }

        private static string SummariseResult(ActiveEvent ev)
        {
            switch (ev.Kind)
            {
                case "treasure":
                    var found = 0;
                    foreach (var s in ev.Spots) if (s.Found) found++;
                    return $"{found}/{ev.Spots.Count} treasure site(s) claimed.";
                case "bossrush":
                    return $"{ev.NextBoss}/{ev.Bosses.Count} boss(es) spawned.";
                case "tournament":
                    return $"{(ev.Entrants > 0 ? ev.Entrants : ev.Signups.Count)} entrant(s), {ev.Round} round(s).";
                default:
                    return "";
            }
        }

        // ==================== 3. voting ====================

        // ZPackage: string topic, int minutes, string action.
        private static void OnVoteStart(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvVoteStart")) return;
            if (!RequireEnabled(sender)) return;

            string topic, action; int minutes;
            try
            {
                topic = CleanText(pkg.ReadString());
                minutes = pkg.ReadInt();
                action = CleanText(pkg.ReadString()).ToLowerInvariant();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvVoteStart: malformed packet dropped ({e.Message})"); return; }

            var err = StartVote(topic, minutes, action, sender, CompanionPlugin.SenderDisplayName(sender));
            CompanionPlugin.SrvAudit(sender, "VOTE-START",
                $"topic={topic} minutes={minutes} action={action} result={(err.Length == 0 ? "ok" : err)}");
            CompanionPlugin.NotifySender(sender, err.Length == 0 ? "Vote started." : "Vote not started: " + err);
        }

        /// <summary>
        /// Start a vote. action is "skipnight" | "kick:&lt;id&gt;" | "event:&lt;kind&gt;" | "none" (advisory).
        /// Returns "" on success or a plain-English reason.
        /// </summary>
        internal static string StartVote(string topic, int minutes, string action, long starter, string starterName)
        {
            if (!EventsOn) return "the events suite is disabled (Features.EnableEvents).";
            if (_vote != null) return "a vote is already running.";
            minutes = Mathf.Clamp(minutes, 1, MaxVoteMinutes);
            action = string.IsNullOrEmpty(action) ? "none" : action.Trim().ToLowerInvariant();
            if (!ActionSupported(action)) return $"unsupported vote action '{action}' (skipnight, kick:<id>, event:<kind>, none).";
            if (string.IsNullOrEmpty(topic)) topic = DescribeAction(action);

            _vote = new Vote
            {
                Topic = Clamp(topic, MaxTextLen),
                Action = action,
                EndsTicksUtc = DateTime.UtcNow.AddMinutes(minutes).Ticks,
                Starter = starter,
            };
            PersistVote();

            AnnounceAll($"VOTE: {_vote.Topic} - type !yes or !no ({minutes} min, {VotePassPercent}% to pass, min {VoteMinVoters} voters).");
            CompanionPlugin.FeatureLog($"Vote started by {starterName}: '{_vote.Topic}' action={action} for {minutes}m");
            Wave1AuditRpc.PostModLog($"VOTE START '{_vote.Topic}' action={action} {minutes}m (by {starterName})");
            try { Wave34Core.Enqueue("Vote started", $"{_vote.Topic}\naction: `{action}`", Wave34Core.ColorInfo); }
            catch (Exception) { }
            return "";
        }

        private static bool ActionSupported(string action)
        {
            if (action == "none" || action == "skipnight") return true;
            if (action.StartsWith("kick:", StringComparison.Ordinal)) return CleanId(action.Substring(5)).Length > 0;
            if (action.StartsWith("event:", StringComparison.Ordinal))
            {
                var kind = CleanName(action.Substring(6)).ToLowerInvariant();
                return kind == "bossrush" || kind == "invasion" || kind == "treasure" || kind == "tournament";
            }
            return false;
        }

        private static string DescribeAction(string action)
        {
            if (action == "skipnight") return "Skip to morning?";
            if (action.StartsWith("kick:", StringComparison.Ordinal)) return "Kick " + CleanId(action.Substring(5)) + "?";
            if (action.StartsWith("event:", StringComparison.Ordinal)) return "Start the " + CleanName(action.Substring(6)) + " event?";
            return "Vote";
        }

        private static void PersistVote()
        {
            try
            {
                var t = FeatureStore.Table(TblVotes);
                if (_vote == null)
                {
                    if (t.Remove(KeyVote)) FeatureStore.SaveTable(TblVotes);
                    return;
                }
                t[KeyVote] = string.Join("|", new[]
                {
                    _vote.Topic.Replace('|', '/'),
                    _vote.EndsTicksUtc.ToString(CultureInfo.InvariantCulture),
                    _vote.Action,
                    Csv(_vote.Yes),
                    Csv(_vote.No),
                });
                FeatureStore.SaveTable(TblVotes);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: could not persist the vote: {e.Message}"); }
        }

        private static void StepVote()
        {
            var v = _vote;
            if (v == null) return;
            if (DateTime.UtcNow.Ticks < v.EndsTicksUtc) return;

            _vote = null;
            PersistVote();

            var yes = v.Yes.Count;
            var no = v.No.Count;
            var total = yes + no;
            var pct = total > 0 ? (int)Math.Round(yes * 100d / total) : 0;

            if (total < VoteMinVoters)
            {
                AnnounceAll($"Vote failed: only {total} vote(s) cast, {VoteMinVoters} needed. ({v.Topic})");
                CompanionPlugin.FeatureLog($"Vote '{v.Topic}' failed: {total} voters < {VoteMinVoters}");
                Wave1AuditRpc.PostModLog($"VOTE FAIL '{v.Topic}' turnout {total}/{VoteMinVoters}");
                return;
            }
            if (pct < VotePassPercent)
            {
                AnnounceAll($"Vote failed: {yes} yes / {no} no ({pct}%, {VotePassPercent}% needed). ({v.Topic})");
                CompanionPlugin.FeatureLog($"Vote '{v.Topic}' failed: {pct}% < {VotePassPercent}%");
                Wave1AuditRpc.PostModLog($"VOTE FAIL '{v.Topic}' {yes}/{no} ({pct}%)");
                try { Wave34Core.Enqueue("Vote failed", $"{v.Topic}\n{yes} yes / {no} no ({pct}%)", Wave34Core.ColorLeave); }
                catch (Exception) { }
                return;
            }

            AnnounceAll($"Vote PASSED: {yes} yes / {no} no ({pct}%). ({v.Topic})");
            CompanionPlugin.FeatureLog($"Vote '{v.Topic}' passed {yes}/{no} ({pct}%) - executing '{v.Action}'");
            Wave1AuditRpc.PostModLog($"VOTE PASS '{v.Topic}' {yes}/{no} ({pct}%) action={v.Action}");
            try { Wave34Core.Enqueue("Vote passed", $"{v.Topic}\n{yes} yes / {no} no ({pct}%)\naction: `{v.Action}`", Wave34Core.ColorJoin); }
            catch (Exception) { }
            ExecuteVoteAction(v);
        }

        private static void ExecuteVoteAction(Vote v)
        {
            try
            {
                if (v.Action == "none") return;

                if (v.Action == "skipnight")
                {
                    // Exactly CompanionPlugin.OnServerSkipNight's mechanism (CompanionPlugin.cs:793-799):
                    // world time is server-owned, EnvMan.SkipToMorning advances it for everyone.
                    if (EnvMan.instance == null)
                    {
                        AnnounceAll("Night skip failed: the day/night system is not available.");
                        return;
                    }
                    EnvMan.instance.SkipToMorning();
                    AnnounceAll("Skipping to morning.");
                    return;
                }

                if (v.Action.StartsWith("kick:", StringComparison.Ordinal))
                {
                    var id = CleanId(v.Action.Substring(5));
                    var peer = PeerById(id);
                    if (peer == null) { AnnounceAll($"Vote-kick: {id} is not online."); return; }
                    var name = peer.m_playerName;
                    Wave1Moderation.SendPlayerText(peer.m_uid, "You have been kicked by a player vote.");
                    var ok = CompanionPlugin.FeatureKick(peer.m_uid);
                    AnnounceAll($"Vote-kick: {name} was removed.");
                    CompanionPlugin.FeatureLog($"Vote-kick of {name} ({id}): {(ok ? "kicked" : "peer not found")}");
                    Wave1AuditRpc.PostModLog($"VOTEKICK {name} ({id}) ok={ok}");
                    return;
                }

                if (v.Action.StartsWith("event:", StringComparison.Ordinal))
                {
                    var kind = CleanName(v.Action.Substring(6)).ToLowerInvariant();
                    var centre = v.Starter != 0 ? SenderPosition(v.Starter) : Vector3.zero;
                    var err = StartEvent(kind, VoteEventMinutes, "", 0L, "a player vote", centre);
                    if (err.Length > 0) AnnounceAll($"The voted event could not start: {err}");
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave6: vote action '{v.Action}' failed: {e.Message}");
                AnnounceAll("The voted action could not be carried out (see the server log).");
            }
        }

        private static void CmdYes(long sender, string args) => CastVote(sender, true);
        private static void CmdNo(long sender, string args) => CastVote(sender, false);

        // One vote per platform id, changeable until the deadline (the sets are kept mutually exclusive).
        private static void CastVote(long sender, bool yes)
        {
            if (!EventsOn) return;
            var v = _vote;
            if (v == null) { Wave1Moderation.SendPlayerText(sender, "No vote is running."); return; }
            var host = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(host) || host == "?") return;

            if (yes) { v.No.Remove(host); v.Yes.Add(host); }
            else { v.Yes.Remove(host); v.No.Add(host); }
            PersistVote();
            Wave1Moderation.SendPlayerText(sender, $"Your vote was recorded: {(yes ? "yes" : "no")}. ({v.Yes.Count} yes / {v.No.Count} no)");
        }

        private static void CmdVoteStatus(long sender, string args)
        {
            if (!EventsOn) return;
            var v = _vote;
            if (v == null) { Wave1Moderation.SendPlayerText(sender, "No vote is running."); return; }
            var left = Math.Max(0, (int)(new DateTime(v.EndsTicksUtc, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds);
            Wave1Moderation.SendPlayerText(sender, Clamp(
                $"Vote: {v.Topic} - {v.Yes.Count} yes / {v.No.Count} no, {left}s left ({VotePassPercent}% to pass).", MaxTextLen));
        }

        // Player-started votes are OFF by default and limited to the one action a player can reasonably ask
        // for. Anything else must come from an admin through AP_SrvVoteStart.
        private static void CmdVote(long sender, string args)
        {
            if (!EventsOn) return;
            var host = CompanionPlugin.SenderPlatformId(sender);
            var isAdmin = CompanionPlugin.FeatureIsAdminId(host);
            if (!PlayerVoteStartOn && !isAdmin)
            {
                Wave1Moderation.SendPlayerText(sender, "Player-started votes are disabled on this server.");
                return;
            }
            var what = CleanName(args).ToLowerInvariant();
            if (what.Length == 0) { Wave1Moderation.SendPlayerText(sender, "Usage: !vote skipnight"); return; }
            if (what != "skipnight" && !isAdmin)
            {
                Wave1Moderation.SendPlayerText(sender, "Players can only start '!vote skipnight'.");
                return;
            }
            if (!isAdmin && PlayerVoteCooldownSeconds > 0 && !string.IsNullOrEmpty(host) && host != "?")
            {
                DateTime last;
                if (VoteStartCooldowns.TryGetValue(host, out last))
                {
                    var left = PlayerVoteCooldownSeconds - (int)(DateTime.UtcNow - last).TotalSeconds;
                    if (left > 0)
                    {
                        Wave1Moderation.SendPlayerText(sender, $"You can start another vote in {left}s.");
                        return;
                    }
                }
                VoteStartCooldowns[host] = DateTime.UtcNow;
            }

            var action = what == "skipnight" ? "skipnight" : "none";
            var err = StartVote(DescribeAction(action), 2, action, sender, CompanionPlugin.SenderDisplayName(sender));
            if (err.Length > 0) Wave1Moderation.SendPlayerText(sender, "Vote not started: " + err);
        }

        // ==================== 4. seasonal / global-key toggles ====================

        // HONEST SCOPE, read the limitations before believing the name: this game build's GlobalKeys enum
        // (GlobalKeys.decompiled.cs) contains NO holiday/seasonal keys — the seasonal item groups the client
        // shows are date-driven (Player.UpdateCurrentSeason -> SeasonalItemGroup.IsInSeason), not key-gated,
        // and the server cannot influence them. So this RPC is a curated GLOBAL-KEY setter: every name below
        // is a real member of the enum this build defines, and each one genuinely changes play. Unknown names
        // are refused unless Features.SeasonAllowAnyGlobalKey is on.
        private static readonly string[] SeasonCandidates =
        {
            "PlayerEvents", "PassiveMobs", "NoPortals", "NoBossPortals", "TeleportAll", "NoMap",
            "DeathKeepEquip", "DeathDeleteItems", "DeathDeleteUnequipped", "DeathSkillsReset",
            "NoBuildCost", "NoCraftCost", "AllPiecesUnlocked", "AllRecipesUnlocked", "NoWorkbench",
            "DungeonBuild", "Fire",
        };

        private static List<string> _seasonKeys;

        // The candidate list is intersected with the enum THIS BUILD actually defines, so a game update that
        // drops a key silently drops it here too instead of offering an option that does nothing.
        private static List<string> SeasonKeys()
        {
            if (_seasonKeys != null) return _seasonKeys;
            var res = new List<string>();
            try
            {
                var t = AccessTools.TypeByName("GlobalKeys");
                var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (t != null && t.IsEnum)
                    foreach (var n in Enum.GetNames(t)) defined.Add(n);
                foreach (var c in SeasonCandidates)
                    if (defined.Count == 0 || defined.Contains(c)) res.Add(c);
                if (defined.Count == 0)
                    CompanionPlugin.FeatureLog("Wave6: the GlobalKeys enum could not be read; the seasonal key list is unfiltered.");
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave6: seasonal key discovery failed ({e.Message}); using the built-in list.");
                res.AddRange(SeasonCandidates);
            }
            _seasonKeys = res;
            return res;
        }

        // ZPackage: string name, bool on.
        private static void OnSeasonSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSeasonSet")) return;
            if (!RequireEnabled(sender)) return;

            string name; bool on;
            try { name = CleanName(pkg.ReadString()); on = pkg.ReadBool(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvSeasonSet: malformed packet dropped ({e.Message})"); return; }
            if (name.Length == 0) { CompanionPlugin.NotifySender(sender, "No global key name given."); return; }

            var known = false;
            foreach (var k in SeasonKeys())
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { name = k; known = true; break; }
            if (!known && !SeasonAllowAnyKey)
            {
                CompanionPlugin.NotifySender(sender, Clamp(
                    $"'{name}' is not one of this build's toggles. Available: {string.Join(", ", SeasonKeys().ToArray())}. " +
                    "Set Features.SeasonAllowAnyGlobalKey to allow arbitrary global keys.", 480));
                return;
            }

            if (!SetGlobalKey(name, on))
            {
                CompanionPlugin.NotifySender(sender, "The global-key system is not available on this server right now.");
                return;
            }

            try
            {
                var t = FeatureStore.Table(TblEvents);
                t[SeasonPrefix + name] = on ? "1" : "0";
                FeatureStore.SaveTable(TblEvents);
            }
            catch (Exception) { }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "SEASON-SET", $"key={name} state={(on ? "ON" : "OFF")} known={known}");
            CompanionPlugin.FeatureLog($"Global key '{name}' turned {(on ? "ON" : "OFF")} by {admin}");
            Wave1AuditRpc.PostModLog($"GLOBALKEY {name} {(on ? "ON" : "OFF")} (by {admin})");
            AnnounceAll($"Server rule changed: {name} is now {(on ? "ON" : "OFF")}.");
            CompanionPlugin.NotifySender(sender, $"{name} is now {(on ? "ON" : "OFF")}.");
        }

        // ZoneSystem.SetGlobalKey/RemoveGlobalKey send the vanilla "SetGlobalKey"/"RemoveGlobalKey" routed RPC
        // (ZoneSystem.cs:2592-2595, 2665-2668). Called ON THE SERVER the target resolves to our own id, so
        // ZRoutedRpc dispatches locally into RPC_SetGlobalKey, which adds the key and pushes the new set to
        // every client (ZoneSystem.cs:2651-2676) — the same path the panel's global-keys UI ends up in.
        private static bool SetGlobalKey(string name, bool on)
        {
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null || ZRoutedRpc.instance == null) return false;
                if (on) zs.SetGlobalKey(name);
                else zs.RemoveGlobalKey(name);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave6: global key '{name}' could not be changed: {e.Message}");
                return false;
            }
        }

        private static bool GlobalKeyOn(string name)
        {
            try
            {
                var zs = ZoneSystem.instance;
                return zs != null && zs.GetGlobalKey(name);
            }
            catch (Exception) { return false; }
        }

        // ==================== AP_SrvEventStateReq -> AP_EventState ====================

        private static void OnEventStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvEventStateReq")) return;

            var pkg = new ZPackage();
            pkg.Write(1);                          // payload version — bump, never reorder
            pkg.Write(_event != null ? _event.Kind : "");
            pkg.Write(_event != null ? _event.EndsTicksUtc : 0L);

            // Seasonal / global-key toggles: name, kind, enabled.
            var keys = SeasonKeys();
            var n = Math.Min(keys.Count, SeasonShipCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                pkg.Write(keys[i]);
                pkg.Write("globalkey");
                pkg.Write(GlobalKeyOn(keys[i]));
            }

            var v = _vote;
            pkg.Write(v != null);
            pkg.Write(v != null ? v.Topic : "");
            pkg.Write(v != null ? v.Yes.Count : 0);
            pkg.Write(v != null ? v.No.Count : 0);
            pkg.Write(v != null ? v.EndsTicksUtc : 0L);

            var warps = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var kv in FeatureStore.Table(TblWarps))
                {
                    warps.Add(kv);
                    if (warps.Count >= WarpShipCap) break;
                }
            }
            catch (Exception) { }
            pkg.Write(warps.Count);
            foreach (var kv in warps)
            {
                Vector3 pos; bool adminOnly;
                ParseWarp(kv.Value, out pos, out adminOnly);
                pkg.Write(kv.Key);
                pkg.Write(pos.x); pkg.Write(pos.y); pkg.Write(pos.z);
                pkg.Write(adminOnly);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_EventState", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_EventState reply failed: {e.Message}"); }
        }

        // ==================== shared helpers ====================

        /// <summary>Kind of the running event, or "" — for sibling modules and the self-test.</summary>
        internal static string ActiveEventKind() => _event != null ? _event.Kind : "";

        /// <summary>True while a vote is open.</summary>
        internal static bool VoteIsActive() => _vote != null;

        private static bool RequireEnabled(long sender)
        {
            if (EventsOn) return true;
            CompanionPlugin.NotifySender(sender,
                "The events suite is disabled on this server (set Features.EnableEvents = true in the companion config and restart).");
            return false;
        }

        // Vanilla-safe broadcast: "ShowMessage" is registered by MessageHud.Awake on every client
        // (MessageHud.cs:111), which is why Wave1Moderation.SendPlayerText reaches unmodded players. One send
        // per connected peer; the peer list is the only thing iterated.
        private static void AnnounceAll(string text)
        {
            if (string.IsNullOrEmpty(text) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    Wave1Moderation.SendPlayerText(peer.m_uid, text);
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave6: announce failed: {e.Message}"); }
        }

        // The requesting admin's position. m_refPos is the client's own continuously-reported reference
        // position (ZNetPeer.cs:15) — the only position the server has for a player it does not simulate. A
        // listen-server host is not in m_peers at all, so it falls back to its local player.
        private static Vector3 SenderPosition(long sender)
        {
            try
            {
                var peer = ZNet.instance != null ? ZNet.instance.GetPeer(sender) : null;
                if (peer != null) return peer.m_refPos;
                if (Player.m_localPlayer != null) return Player.m_localPlayer.transform.position;
            }
            catch (Exception) { }
            return Vector3.zero;
        }

        private static ZNetPeer PeerById(string platformId)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(platformId)) return null;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    if (Wave1Moderation.IdMatches(platformId, peer.m_socket.GetHostName())) return peer;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static ZNetPeer PeerOfCharacter(ZDOID characterId)
        {
            if (ZNet.instance == null || characterId.IsNone()) return null;
            try
            {
                foreach (var p in ZNet.instance.GetPeers())
                    if (p != null && p.m_characterID == characterId) return p;
            }
            catch (Exception) { }
            return null;
        }

        // WorldGenerator.GetHeight is pure math and is initialised on the server by WorldGenerator.Initialize
        // (ZNet.cs:299) — the same ladder Wave4PlayerData documents. It is the GENERATED height: it knows
        // nothing about player terrain edits or buildings, which is fine for scattering hunt coordinates.
        private static float GeneratedGroundY(float x, float z)
        {
            if (_worldGenBroken) return float.NaN;
            try
            {
                var wg = WorldGenerator.instance;
                if (wg == null) return float.NaN;
                var h = wg.GetHeight(x, z);
                return float.IsNaN(h) || float.IsInfinity(h) ? float.NaN : h;
            }
            catch (Exception e)
            {
                _worldGenBroken = true;
                CompanionPlugin.FeatureLog($"Wave6: WorldGenerator height unavailable ({e.Message}); treasure points use the start position's height.");
                return float.NaN;
            }
        }

        private static void PruneCooldowns()
        {
            PruneMap(WarpCooldowns, Math.Max(WarpCooldownSeconds, 60));
            PruneMap(VoteStartCooldowns, Math.Max(PlayerVoteCooldownSeconds, 60));
        }

        private static void PruneMap(Dictionary<string, DateTime> map, int seconds)
        {
            if (map.Count == 0) return;
            var cutoff = DateTime.UtcNow.AddSeconds(-seconds);
            List<string> dead = null;
            foreach (var kv in map)
                if (kv.Value < cutoff) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead) map.Remove(k);
        }

        private static string FindWarpKey(Dictionary<string, string> t, string name)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            if (t.ContainsKey(name)) return name;
            foreach (var kv in t)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        private static bool ParseWarp(string value, out Vector3 pos, out bool adminOnly)
        {
            pos = Vector3.zero;
            adminOnly = false;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split('|');
            if (parts.Length < 3) return false;
            float x, y, z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return false;
            pos = new Vector3(x, y, z);
            adminOnly = parts.Length > 3 && parts[3] == "1";
            return true;
        }

        private static string Csv(HashSet<string> set)
        {
            if (set == null || set.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var s in set)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(s.Replace(',', ' ').Replace('|', '/'));
            }
            return sb.ToString();
        }

        // Names are the key of a store table and are echoed into chat, so they stay to a conservative charset.
        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxNameLen) s = s.Substring(0, MaxNameLen);
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
                else return "";   // reject outright rather than silently mangling an admin's input
            }
            return sb.ToString();
        }

        // Ids are stored AS ENTERED (trimmed) — the same rule OnServerBanId and Wave1Moderation document:
        // the game compares the full networkUserId, so stripping a platform prefix silently no-ops crossplay ids.
        private static string CleanId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxIdLen) s = s.Substring(0, MaxIdLen);
            return s.IndexOf(' ') >= 0 ? "" : s;
        }

        // '|' is the field separator inside stored values, so it can never survive in free text.
        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MaxTextLen ? s.Substring(0, MaxTextLen) : s;
        }

        private static string Clamp(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max);

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}

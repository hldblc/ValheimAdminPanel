using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 7 — impossible-state monitor + client-mod enforcement ====================
    //
    // READ THIS BEFORE TOUCHING ANYTHING HERE. Both halves of this file are REPORT-ONLY by default and both
    // are fundamentally advisory. The honesty rules below are load-bearing, not decoration:
    //
    //  1. THE IMPOSSIBLE-STATE MONITOR IS NOT AN ANTI-CHEAT. Every value it reads (health, max_health,
    //     position, DebugFly, stamina, eitr, dead) is written to the character ZDO BY THE CLIENT THAT OWNS
    //     THAT CHARACTER (spec-valheim-api.md §10.1; Character.cs:2519-2566, Player.cs:3048/7075). The server
    //     is a mirror, not a judge. A cheat that simply does not sync — or that syncs plausible numbers — is
    //     completely invisible here. What this catches is CARELESS cheating: devcommands fly left on, a
    //     health value nobody bothered to hide, a client that fell through the world. Treat every flag as
    //     "look at this player", never as proof.
    //  2. THE MOD ENFORCEMENT IS A COMPLIANCE AID, NOT A SECURITY CONTROL. The plugin list is self-reported
    //     by the client. A player who wants to lie about their mods can trivially patch the reply. A player
    //     who does not run AdminPanelCompanion.dll cannot answer at all, and "did not answer" is exposed as
    //     its own status ("unknown" / "no-mod") — it is NEVER folded into "ok".
    //  3. Nothing here corrects state. The server cannot write a client-owned ZDO and make it stick
    //     (spec-valheim-api.md §0.2/§10.2 — the owner's next sync overwrites it, and stealing ownership
    //     breaks the client's own character). The only escalations offered are a re-teleport pin (the same
    //     vanilla-safe trick Wave1's freeze uses) and a kick.
    //
    // House rules obeyed (same shapes as Wave1SrvModeration.cs / Wave34SrvCore.cs):
    //  * ONE Harmony class per target method, each applied in its own try/catch, each reported to the
    //    self-test through Wave2Ops.ReportPatch.
    //  * Ticks on the wire and in the store are DateTime.UtcNow.Ticks; in-frame scheduling is
    //    Time.unscaledTime.
    //  * Every admin RPC is gated by CompanionPlugin.SenderCanFeature and audited via CompanionPlugin.SrvAudit.
    //  * No world sweep: sampling walks the connected peer list (<=10 entries), one ZDO lookup each.
    internal static class Wave7Guard
    {
        // ---- wire versions (first field of every payload; discard the whole payload on a mismatch) ----
        private const int GuardStateVer = 1;
        private const int ModListVer = 1;

        // ---- store tables (shared contract with the panel) ----
        // guard_flags: key "<platformId>#<rule>" -> "rule|hits|lastTicksUtc".
        //   The wire row is (id, rule, hits, ticks), so a player who trips two different rules needs two
        //   rows; the table is a flat key=value store, hence the composite key. '#' can appear in neither a
        //   platform id nor a rule name, so the split is unambiguous.
        private const string TblFlags = "guard_flags";
        // guard_mods: platformId -> "status|detail|ticksUtc"   (status: ok|missing|forbidden|unknown|no-mod)
        private const string TblMods = "guard_mods";
        // guard_allow: platformId -> "1"   (exempt from the impossible-state monitor entirely)
        private const string TblAllow = "guard_allow";
        // Durable evidence trail, separate from the admin audit log (these are machine observations, not
        // admin actions, and they would drown audit.log).
        private const string LogGuard = "guard";

        // ---- ZDO keys, written as the persisted STRING hash rather than the ZDOVars field ----
        // The key string is what lives in the save file and can never change; the C# field name could be
        // renamed by a game update, so the literal is hashed here (same discipline as Wave34Core).
        // Verified against the decompiled ZDOVars: s_dead="dead" (:67), s_debugFly="DebugFly" (:69),
        // s_eitr="eitr" (:81), s_health="health" (:111), s_maxHealth="max_health" (:177),
        // s_stamina="stamina" (:271).
        private static readonly int DeadHash = "dead".GetStableHashCode();
        private static readonly int HealthHash = "health".GetStableHashCode();
        private static readonly int MaxHealthHash = "max_health".GetStableHashCode();
        private static readonly int StaminaHash = "stamina".GetStableHashCode();
        private static readonly int EitrHash = "eitr".GetStableHashCode();
        // The written casing is "DebugFly" in the current build, but older notes (and older builds) use
        // "debugFly". Both hashes are probed and OR'd: a wrong guess would silently disable the single
        // highest-signal rule in the file, which is worse than one extra dictionary lookup per sample.
        private static readonly int DebugFlyHashA = "DebugFly".GetStableHashCode();
        private static readonly int DebugFlyHashB = "debugFly".GetStableHashCode();

        // ---- rule identifiers (also the wire strings and the guard_flags key suffix) ----
        private const string RuleFly = "fly";
        private const string RuleSpeed = "speed";
        private const string RuleHealth = "health";
        private const string RuleNoclip = "noclip";

        // ---- fixed policy constants (deliberately not configurable) ----
        private const float SampleSeconds = 2f;          // the task's 2 s cadence
        private const float PinSeconds = 2.6f;           // Player.TeleportTo refuses inside its own 2 s cooldown
        private const int HealthSustain = 3;             // consecutive samples before the health rule counts a hit
        private const int NoclipSustain = 3;             // consecutive samples before the noclip rule counts a hit
        private const double AlertCooldownMinutes = 5d;  // one admin alert per player per rule per 5 min
        private const double EscalateCooldownMinutes = 5d;
        private const float MaxSampleGapSeconds = 12f;   // a longer gap means a server hitch: dt is untrustworthy
        private const int StateCap = 40;                 // per-list cap in AP_GuardState (wire contract)
        private const int MaxIdLen = 64;
        private const int MaxTextLen = 200;
        private const int MaxNameLen = 60;
        private const int MaxGuidLen = 96;
        private const int MaxModVerLen = 24;
        private const int MaxModsAccepted = 80;          // hard cap on a client-supplied list
        private const int NameCacheCap = 500;

        // ---- mod-probe schedule (mirrors the wave-3/4 capability probe: a just-joined client has no
        // Player yet, so the first request waits) ----
        private const float FirstModProbeDelay = 12f;
        private const float SecondModProbeDelay = 30f;
        private const float ThirdModProbeDelay = 90f;
        private const int MaxModProbes = 3;
        // AP_ModList is a CLIENT-authored packet on an open RPC bus: a peer can send it whenever it likes,
        // as often as it likes. Each accepted one rewrites a store table and can alert every admin and the
        // Discord webhook, so replies are only taken when we actually asked, once per probe, and at most
        // MaxModReplies times per connection with ModReplyCooldown seconds between them.
        private const float ModReplyCooldown = 30f;
        private const int MaxModReplies = 3;

        // ==================== config ====================

        private static ConfigEntry<bool> _enableAc;
        private static ConfigEntry<int> _acActionMode;
        private static ConfigEntry<int> _acActionThreshold;
        private static ConfigEntry<int> _acFreezeMinutes;
        private static ConfigEntry<bool> _acRuleFly;
        private static ConfigEntry<bool> _acRuleSpeed;
        private static ConfigEntry<bool> _acRuleHealth;
        private static ConfigEntry<bool> _acRuleNoclip;
        private static ConfigEntry<float> _acSpeedThreshold;
        private static ConfigEntry<float> _acSpeedTeleportCutoff;
        private static ConfigEntry<int> _acSpeedConsecutive;
        private static ConfigEntry<int> _acTeleportGraceSeconds;
        private static ConfigEntry<float> _acHealthCap;
        private static ConfigEntry<float> _acNoclipFloorY;

        private static ConfigEntry<bool> _enableEnforce;
        private static ConfigEntry<int> _enforceMode;
        private static ConfigEntry<string> _requiredMods;
        private static ConfigEntry<string> _forbiddenMods;
        private static ConfigEntry<bool> _kickUnanswered;

        private static bool AcOn => _enableAc != null && _enableAc.Value;
        private static int AcActionMode => _acActionMode != null ? Mathf.Clamp(_acActionMode.Value, 0, 2) : 0;
        private static int AcActionThreshold => _acActionThreshold != null ? Mathf.Max(1, _acActionThreshold.Value) : 5;
        private static int AcFreezeMinutes => _acFreezeMinutes != null ? Mathf.Clamp(_acFreezeMinutes.Value, 1, 60) : 5;
        private static bool RuleFlyOn => _acRuleFly == null || _acRuleFly.Value;
        private static bool RuleSpeedOn => _acRuleSpeed == null || _acRuleSpeed.Value;
        private static bool RuleHealthOn => _acRuleHealth == null || _acRuleHealth.Value;
        private static bool RuleNoclipOn => _acRuleNoclip == null || _acRuleNoclip.Value;
        private static float SpeedThreshold => _acSpeedThreshold != null ? Mathf.Max(5f, _acSpeedThreshold.Value) : 30f;
        private static float SpeedTeleportCutoff => _acSpeedTeleportCutoff != null ? Mathf.Max(50f, _acSpeedTeleportCutoff.Value) : 150f;
        private static int SpeedConsecutive => _acSpeedConsecutive != null ? Mathf.Clamp(_acSpeedConsecutive.Value, 1, 10) : 2;
        private static float TeleportGrace => _acTeleportGraceSeconds != null ? Mathf.Clamp(_acTeleportGraceSeconds.Value, 1, 120) : 10f;
        private static float HealthCap => _acHealthCap != null ? Mathf.Max(25f, _acHealthCap.Value) : 500f;
        private static float NoclipFloor => _acNoclipFloorY != null ? _acNoclipFloorY.Value : -50f;

        private static bool EnforceOn => _enableEnforce != null && _enableEnforce.Value;
        private static int EnforceMode => _enforceMode != null ? Mathf.Clamp(_enforceMode.Value, 0, 1) : 0;
        private static bool KickUnanswered => _kickUnanswered != null && _kickUnanswered.Value;

        // ==================== state (all session-scoped; the durable half lives in FeatureStore) ====================

        private sealed class Sample
        {
            public bool Seeded;              // false until the first position/dead reading exists
            public Vector3 LastPos;
            public Vector3 LastGoodPos;      // newest position that violated nothing (the pin target)
            public float LastMoveAt;         // Time.unscaledTime of the last CHANGED position
            public bool LastDead;
            public int SpeedStreak;
            public int HealthStreak;
            public int NoclipStreak;
            public float NoSpeedUntil;       // Time.unscaledTime; set by NoteTeleport and by our own pins
            public string Id = "";
        }

        private sealed class ModProbe
        {
            public int Attempts;
            public float NextAt;             // Time.unscaledTime
            public bool Answered;
            public bool Spawned;             // the peer has a character: only now can it answer at all
            public int AcceptedAttempt;      // Attempts value the last accepted reply belonged to (0 = none)
            public int Accepted;             // replies accepted on this connection
            public float LastAcceptAt;       // Time.unscaledTime of the last accepted reply
            public string Id = "";
            public string Name = "";
        }

        private sealed class Pin
        {
            public Vector3 Pos;
            public string Name;
            public long UntilTicksUtc;
        }

        private static readonly Dictionary<long, Sample> Samples = new Dictionary<long, Sample>();
        private static readonly Dictionary<long, ModProbe> ModProbes = new Dictionary<long, ModProbe>();
        private static readonly Dictionary<long, Pin> Pins = new Dictionary<long, Pin>();
        private static readonly Dictionary<string, long> AlertAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> EscalatedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> NameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<long> UidScratch = new List<long>();
        private static readonly List<ZNetPeer> PeerScratch = new List<ZNetPeer>();
        private static readonly List<long> ProbeScratch = new List<long>();

        private static bool _inited;
        private static float _nextSampleAt;
        private static float _nextPinAt;
        private static float _nextProbeAt;
        private static bool _zdoManWarned;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableAc = cfg.Bind("Features", "EnableAntiCheat", false,
                    "Impossible-state monitor: sample connected players' character ZDOs every 2 s and flag states a legitimate client should not report (devcommands fly, absurd health, sub-floor position, teleport-speed movement). OFF by default - when off, nothing is sampled at all. THIS IS NOT AN ANTI-CHEAT: every value it reads is written by the player's own client, so it detects careless cheating only and can never prove anything. Flags are advisory.");
                _acActionMode = cfg.Bind("Features", "AcActionMode", 0,
                    "What happens when a player's flag count passes AcActionThreshold. 0 = report only (default: alert admins, log, nothing else). 1 = report + freeze (repeatedly teleport the player back to their last clean position for AcFreezeMinutes; the same vanilla-safe pin the freeze feature uses). 2 = report + kick. Given that flags are heuristic, 1 and 2 will eventually punish an innocent player - use them only after watching mode 0 on your own server.");
                _acActionThreshold = cfg.Bind("Features", "AcActionThreshold", 5,
                    "Total flags (summed across all rules) a player must exceed before AcActionMode does anything. Ignored in mode 0.");
                _acFreezeMinutes = cfg.Bind("Features", "AcFreezeMinutes", 5,
                    "How long an AcActionMode=1 freeze pin holds a player, in minutes.");
                _acRuleFly = cfg.Bind("Features", "AcRuleFly", true,
                    "Rule 'fly': flag a non-admin whose character reports DebugFly (the devcommands flight flag). Highest-signal, lowest-false-positive rule in the file - a legitimate client never sets it. Admins (adminlist members) are exempt. Only active when EnableAntiCheat is on.");
                _acRuleSpeed = cfg.Bind("Features", "AcRuleSpeed", true,
                    "Rule 'speed': flag sustained horizontal movement above AcSpeedThreshold. The noisiest rule - teleports, portals, deaths, boats and lag all imitate it, so it is heavily filtered (see AcSpeedTeleportCutoff / AcSpeedConsecutive / AcTeleportGraceSeconds). Turn this off first if you see false positives. Only active when EnableAntiCheat is on.");
                _acRuleHealth = cfg.Bind("Features", "AcRuleHealth", true,
                    "Rule 'health': flag a character reporting current or maximum health above AcHealthCap for 3 consecutive samples. Only active when EnableAntiCheat is on.");
                _acRuleNoclip = cfg.Bind("Features", "AcRuleNoclip", true,
                    "Rule 'noclip': flag a character whose Y stays below AcNoclipFloorY for 3 consecutive samples (fell through the world, or is under the terrain). Only active when EnableAntiCheat is on.");
                _acSpeedThreshold = cfg.Bind("Features", "AcSpeedThreshold", 30f,
                    "Horizontal metres per second above which the 'speed' rule trips. Default 30 is roughly three times the fastest vanilla ship, so boats and carts stay far below it - the server cannot observe whether a player is on a vehicle, so this margin IS the vehicle exclusion. Lowering it below about 15 will start flagging longships.");
                _acSpeedTeleportCutoff = cfg.Bind("Features", "AcSpeedTeleportCutoff", 150f,
                    "A single-sample position jump larger than this many metres is treated as a teleport (portal, admin teleport, respawn, zone load) and NEVER as a speed violation. Raise it if your players use very short portal hops; lower it and portals start producing flags.");
                _acSpeedConsecutive = cfg.Bind("Features", "AcSpeedConsecutive", 2,
                    "How many consecutive samples must exceed AcSpeedThreshold before the 'speed' rule records a flag. 2 discards single-sample artefacts (a delayed position burst arriving after a stall).");
                _acTeleportGraceSeconds = cfg.Bind("Features", "AcTeleportGraceSeconds", 10,
                    "Seconds after a teleport RPC is sent to a player during which the 'speed' rule ignores them. Covers this mod's own teleports, warps and freeze pins, plus any other server-side plugin that teleports through RPC_TeleportPlayer / RPC_TeleportTo.");
                _acHealthCap = cfg.Bind("Features", "AcHealthCap", 500f,
                    "Health (current or maximum) above which the 'health' rule trips. Vanilla tops out far below this even with the best food, but mods that legitimately raise health (progression/RPG mods) will trip it - raise the cap or disable the rule on such a server.");
                _acNoclipFloorY = cfg.Bind("Features", "AcNoclipFloorY", -50f,
                    "World Y below which the 'noclip' rule trips. Valheim's sea level is y=30 and the ocean floor sits well above -50, so normal play never goes here; a player who legitimately falls out of the world will be flagged too.");

                _enableEnforce = cfg.Bind("Features", "EnableModEnforcement", false,
                    "Ask joining clients for their BepInEx plugin list and compare it against RequiredMods / ForbiddenMods. OFF by default - when off, no request is sent. COMPLIANCE AID, NOT SECURITY: the list is self-reported, a modified client can lie, and a client without AdminPanelCompanion.dll cannot answer at all (reported as status 'no-mod', never as 'ok').");
                _enforceMode = cfg.Bind("Features", "EnforceMode", 0,
                    "0 = report only (default: record and alert admins). 1 = kick players whose reported list violates RequiredMods/ForbiddenMods. Players who never answer are only kicked when KickUnansweredMods is on. Adminlist members are never evaluated or kicked by this feature.");
                _requiredMods = cfg.Bind("Features", "RequiredMods", "",
                    "Comma-separated BepInEx plugin GUIDs every player must report (for example: com.example.mymod,org.bepinex.plugins.jotunn). Empty = no requirement. Matching is case-insensitive on the GUID only, never the version.");
                _forbiddenMods = cfg.Bind("Features", "ForbiddenMods", "",
                    "Comma-separated BepInEx plugin GUIDs no player may report. Empty = no ban list. A cheat client will simply omit its own GUID from the reply, so treat a clean answer as unproven.");
                _kickUnanswered = cfg.Bind("Features", "KickUnansweredMods", false,
                    "Also kick players who never answer the plugin-list request (status 'no-mod'). This effectively makes AdminPanelCompanion.dll mandatory for every player on the server - ordinary vanilla players CANNOT answer and will be kicked (adminlist members excepted). The wait only starts once the player has actually spawned in, because a client at the character-selection screen cannot answer yet. OFF by default and only meaningful when EnforceMode is 1.");
            }

            // The read is a moderator job; the actions (clear/kick/allowlist) stay owner-only under tiered
            // roles, because adding an id to the allowlist permanently blinds the monitor for that player.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvGuardStateReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvGuardAction", null);
            // AP_ModListReq / AP_ModList are server<->client plumbing, not admin actions: deliberately NOT
            // registered with the audit chokepoint (one line per peer per join, for no signal).

            ApplyPatch("Wave7GuardRpcRegistration", typeof(Wave7RpcRegisterPatch),
                "guard state/action RPCs and mod-list handshake unavailable");
            ApplyPatch("Wave7GuardJoinPatch", typeof(Wave7JoinPatch),
                "mod-list request on join unavailable");
            ApplyPatch("Wave7GuardLeavePatch", typeof(Wave7LeavePatch),
                "per-peer sampling state will be cleaned up lazily instead of on disconnect");
            ApplyPatch("Wave7GuardTeleportWatch", typeof(Wave7TeleportWatchPatch),
                "speed rule loses its teleport whitelist - portals/admin teleports may produce flags");
            ApplyPatch("Wave7GuardTeleportZdoWatch", typeof(Wave7TeleportZdoWatchPatch),
                "speed rule loses the ZDO-targeted teleport whitelist");
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

            if (AcOn && now >= _nextSampleAt)
            {
                _nextSampleAt = now + SampleSeconds;
                try { SampleAll(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Guard sampling failed: {e.Message}"); }
            }

            if (Pins.Count > 0 && now >= _nextPinAt)
            {
                _nextPinAt = now + PinSeconds;
                try { EnforcePins(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Guard pin tick failed: {e.Message}"); }
            }

            if (EnforceOn && now >= _nextProbeAt)
            {
                _nextProbeAt = now + 1f;
                try { StepModProbes(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Mod-list probe tick failed: {e.Message}"); }
            }
        }

        // ==================== RPC registration ====================

        // Registered on BOTH sides on purpose: the companion DLL is the same file on the server and on an
        // admin's client. Each handler guards its own side (OnModListReq refuses anything the server did not
        // send and needs a local Player; the server handlers refuse to run off-server).
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave7RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvGuardStateReq", new Action<long>(OnGuardStateReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvGuardAction", OnGuardAction);
                    ZRoutedRpc.instance.Register("AP_ModListReq", new Action<long>(OnModListReq));   // client side
                    ZRoutedRpc.instance.Register<ZPackage>("AP_ModList", OnModList);                 // server side
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Guard RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. impossible-state monitor ====================

        // One pass over the connected peers. NOT ZNet.GetAllCharacterZDOS(): that returns bare ZDOs and
        // throws away the peer association we need for identity, alerts and kicks — and on a listen host it
        // also includes the host's own character, which we deliberately never sample (the host is the
        // operator; flagging them is noise).
        private static void SampleAll(float now)
        {
            var znet = ZNet.instance;
            var man = ZDOMan.instance;
            if (znet == null) return;
            if (man == null)
            {
                if (!_zdoManWarned)
                {
                    _zdoManWarned = true;
                    CompanionPlugin.FeatureLog("Guard: ZDOMan unavailable, impossible-state sampling is idle.");
                }
                return;
            }

            // SNAPSHOT: ZNet.GetPeers() hands back the live m_peers list (ZNet.cs:2356-2359), and an
            // AcActionMode=2 escalation kicks mid-pass, which mutates it. Iterating the original would skip
            // peers at best; the copy costs one small allocation every 2 s.
            PeerScratch.Clear();
            try { PeerScratch.AddRange(znet.GetPeers()); }
            catch (Exception) { PeerScratch.Clear(); return; }

            UidScratch.Clear();
            for (var i = 0; i < PeerScratch.Count; i++)
            {
                var peer = PeerScratch[i];
                if (peer == null) continue;
                try
                {
                    if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
                    var host = peer.m_socket != null ? CleanId(peer.m_socket.GetHostName()) : "";
                    if (host.Length == 0) continue;
                    UidScratch.Add(peer.m_uid);
                    RememberName(host, peer.m_playerName);

                    if (IsAllowlisted(host)) continue;

                    var zdo = man.GetZDO(peer.m_characterID);
                    if (zdo == null || !zdo.IsValid()) continue;

                    SampleOne(now, peer, host, zdo);
                }
                catch (Exception) { /* one malformed peer/ZDO must never stop the pass */ }
            }
            PeerScratch.Clear();   // never hold peer references across frames

            // Drop state for peers that are gone (the leave patch normally does this; this is the safety net
            // for a disconnect path that never reaches our prefix).
            if (Samples.Count > UidScratch.Count) PruneByUid(Samples, UidScratch);
        }

        private static void SampleOne(float now, ZNetPeer peer, string host, ZDO zdo)
        {
            Sample s;
            if (!Samples.TryGetValue(peer.m_uid, out s))
            {
                s = new Sample { Id = host };
                Samples[peer.m_uid] = s;
            }
            s.Id = host;

            var pos = zdo.GetPosition();
            var dead = zdo.GetBool(DeadHash, false);
            // GetFloat's default doubles as "the client never wrote this" — -1 is impossible for both fields,
            // so a value below 0 means "no data" and the health rule simply abstains.
            var hp = zdo.GetFloat(HealthHash, -1f);
            var maxHp = zdo.GetFloat(MaxHealthHash, -1f);
            var fly = zdo.GetBool(DebugFlyHashA, false) || zdo.GetBool(DebugFlyHashB, false);

            var deadChanged = s.Seeded && dead != s.LastDead;

            // ---- rule: fly ----
            // A legitimate client sets DebugFly only through the devcommands console, which a non-admin
            // cannot open on a server that has cheats off. False-positive story: an ADMIN flying legitimately
            // (exempted below by adminlist membership), and any third-party plugin that reuses the flag for
            // its own flight mode (an admin-installed creative/build mod would trip this — disable AcRuleFly
            // on such a server).
            if (RuleFlyOn && fly && !CompanionPlugin.FeatureIsAdminId(host))
                RegisterHit(host, peer.m_uid, RuleFly, "character reports DebugFly=true", s.Seeded ? s.LastGoodPos : pos);

            // ---- rule: health ----
            // False-positive story: any mod that legitimately raises health (RPG/progression mods, some
            // server-side balance mods) reports real values above the cap; so does an admin who healed
            // themselves through a mod that raises max health. Sustained over 3 samples so a single
            // mid-transition read cannot trip it.
            if (RuleHealthOn && (hp > HealthCap || maxHp > HealthCap))
            {
                s.HealthStreak++;
                if (s.HealthStreak == HealthSustain)
                    RegisterHit(host, peer.m_uid, RuleHealth,
                        $"health={F(hp)} max_health={F(maxHp)} cap={F(HealthCap)}", s.Seeded ? s.LastGoodPos : pos);
                else if (s.HealthStreak > HealthSustain) s.HealthStreak = HealthSustain;   // one flag per episode
            }
            else s.HealthStreak = 0;

            // ---- rule: noclip / underworld ----
            // False-positive story: a genuine engine bug (falling through unloaded terrain right after a
            // zone load or a teleport) puts an innocent player below the floor for a few seconds. Requiring
            // 3 consecutive samples means ~6 s below the floor, which normal play never produces.
            if (RuleNoclipOn && pos.y < NoclipFloor)
            {
                s.NoclipStreak++;
                if (s.NoclipStreak == NoclipSustain)
                    RegisterHit(host, peer.m_uid, RuleNoclip,
                        $"y={F(pos.y)} floor={F(NoclipFloor)} for {NoclipSustain} samples", s.Seeded ? s.LastGoodPos : pos);
                else if (s.NoclipStreak > NoclipSustain) s.NoclipStreak = NoclipSustain;
            }
            else s.NoclipStreak = 0;

            // ---- rule: speed ----
            var violated = false;
            if (!s.Seeded)
            {
                // First sample of a session: seed only. A player who joins mid-world has no previous
                // position, and "spawn point -> current position" is not movement.
                s.Seeded = true;
                s.LastPos = pos;
                s.LastGoodPos = pos;
                s.LastMoveAt = now;
                s.LastDead = dead;
                s.SpeedStreak = 0;
                return;
            }

            if (RuleSpeedOn) violated = EvaluateSpeed(now, s, pos, deadChanged, host, peer.m_uid);

            s.LastDead = dead;
            if (pos != s.LastPos)
            {
                s.LastPos = pos;
                s.LastMoveAt = now;
            }
            if (!violated && !fly && s.NoclipStreak == 0) s.LastGoodPos = pos;
        }

        // Returns true when this sample counted as a speed violation (so the caller does not adopt the
        // position as a clean pin target).
        //
        // EXCLUSIONS, all concrete, in the order they apply:
        //   1. dead flag changed since the last sample  -> death or respawn moved the character.
        //   2. inside the teleport grace window         -> we (or another server plugin) sent this peer a
        //                                                  teleport RPC within AcTeleportGraceSeconds.
        //   3. jump larger than AcSpeedTeleportCutoff   -> portal / admin teleport / respawn / zone load.
        //   4. position unchanged                       -> no new data arrived; measuring 0 m/s is pointless
        //                                                  and measuring across the stall would be wrong.
        //   5. elapsed time <= 0 or > MaxSampleGapSeconds -> a server hitch makes dt meaningless.
        //   6. vehicles                                 -> NOT observable server-side (the client's attach
        //                                                  state never reaches the ZDO; only s_inBed does).
        //                                                  The threshold margin is the exclusion: 30 m/s is
        //                                                  ~3x the fastest vanilla ship. Documented, not hidden.
        //   7. AcSpeedConsecutive samples in a row must exceed the threshold before a flag is recorded.
        private static bool EvaluateSpeed(float now, Sample s, Vector3 pos, bool deadChanged, string host, long uid)
        {
            if (deadChanged) { s.SpeedStreak = 0; return false; }
            if (now < s.NoSpeedUntil) { s.SpeedStreak = 0; return false; }
            if (pos == s.LastPos) return false;                       // no fresh data this sample

            var dt = now - s.LastMoveAt;
            if (dt <= 0.05f || dt > MaxSampleGapSeconds) { s.SpeedStreak = 0; return false; }

            // Horizontal only: falling is not a speed cheat and terminal velocity would flag every long drop.
            var dx = pos.x - s.LastPos.x;
            var dz = pos.z - s.LastPos.z;
            var dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > SpeedTeleportCutoff) { s.SpeedStreak = 0; return false; }   // clearly a teleport

            var speed = dist / dt;
            if (speed <= SpeedThreshold) { s.SpeedStreak = 0; return false; }

            s.SpeedStreak++;
            if (s.SpeedStreak < SpeedConsecutive) return true;
            s.SpeedStreak = 0;
            RegisterHit(host, uid, RuleSpeed,
                $"{F(speed)} m/s horizontal over {F(dt)} s ({F(dist)} m), threshold {F(SpeedThreshold)}", s.LastGoodPos);
            return true;
        }

        // ==================== flags, alerts, escalation ====================

        private static void RegisterHit(string id, long uid, string rule, string detail, Vector3 pinAt)
        {
            id = CleanId(id);
            if (id.Length == 0) return;
            detail = CleanText(detail);

            var key = id + "#" + rule;
            var t = FeatureStore.Table(TblFlags);
            int hits; long lastTicks; string storedRule;
            ParseFlag(t.TryGetValue(key, out var raw) ? raw : null, out storedRule, out hits, out lastTicks);
            hits++;
            var nowTicks = DateTime.UtcNow.Ticks;
            t[key] = rule + "|" + hits.ToString(CultureInfo.InvariantCulture) + "|" + nowTicks.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblFlags);

            var name = NameOf(id);
            FeatureStore.Append(LogGuard,
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|FLAG|{id}|{name}|{rule}|{hits}|{detail}");
            CompanionPlugin.FeatureLog($"Guard flag [{rule}] {name} ({id}) hit #{hits}: {detail}");

            // Alert throttle: at most one admin alert per player per rule per 5 minutes. The flag counter
            // still increments on every trip — only the noise is throttled.
            long alertedAt;
            if (!AlertAt.TryGetValue(key, out alertedAt) ||
                new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(alertedAt, DateTimeKind.Utc) >= TimeSpan.FromMinutes(AlertCooldownMinutes))
            {
                AlertAt[key] = nowTicks;
                var line = $"Guard [{rule}] {name} ({id}): {detail} - flag #{hits}. Heuristic only, verify before acting.";
                try { Wave1Moderation.NotifyOnlineAdmins(line); } catch (Exception) { }
                try { Wave1AuditRpc.PostModLog("GUARD " + line); } catch (Exception) { }
                try
                {
                    Wave34Core.Enqueue("Anti-cheat flag",
                        $"**{name}** (`{id}`)\nRule: `{rule}` - flag #{hits}\n{detail}\n_Heuristic: the server can only see what the client chose to sync._",
                        Wave34Core.ColorDeath);
                }
                catch (Exception) { }
            }

            Escalate(id, uid, rule, hits, pinAt, nowTicks);
        }

        private static void Escalate(string id, long uid, string rule, int ruleHits, Vector3 pinAt, long nowTicks)
        {
            var mode = AcActionMode;
            if (mode == 0) return;
            var total = TotalHits(id);
            if (total <= AcActionThreshold) return;

            long last;
            if (EscalatedAt.TryGetValue(id, out last) &&
                new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(last, DateTimeKind.Utc) < TimeSpan.FromMinutes(EscalateCooldownMinutes))
                return;
            EscalatedAt[id] = nowTicks;

            var name = NameOf(id);
            if (mode == 1)
            {
                Pins[uid] = new Pin
                {
                    Pos = pinAt,
                    Name = name,
                    UntilTicksUtc = DateTime.UtcNow.AddMinutes(AcFreezeMinutes).Ticks,
                };
                NoteTeleport(uid);   // our own pin must not feed the speed rule
                try { Wave1Moderation.SendPlayerText(uid, "An automated check has restricted your movement. Contact an admin."); }
                catch (Exception) { }
                var line = $"Guard: {name} ({id}) frozen for {AcFreezeMinutes} min after {total} flags (latest rule: {rule}).";
                CompanionPlugin.FeatureLog(line);
                FeatureStore.Append(LogGuard, $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|FREEZE|{id}|{name}|{rule}|{total}|auto");
                try { Wave1Moderation.NotifyOnlineAdmins(line); } catch (Exception) { }
                try { Wave1AuditRpc.PostModLog("GUARD " + line); } catch (Exception) { }
                return;
            }

            // mode 2 — kick.
            try { Wave1Moderation.SendPlayerText(uid, "An automated check flagged your client. You are being disconnected."); }
            catch (Exception) { }
            var kicked = false;
            try { kicked = CompanionPlugin.FeatureKick(uid); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Guard auto-kick failed for {id}: {e.Message}"); }
            var msg = $"Guard: {name} ({id}) auto-kicked after {total} flags (latest rule: {rule}); kick={kicked}.";
            CompanionPlugin.FeatureLog(msg);
            FeatureStore.Append(LogGuard, $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|KICK|{id}|{name}|{rule}|{total}|auto");
            try { Wave1Moderation.NotifyOnlineAdmins(msg); } catch (Exception) { }
            try { Wave1AuditRpc.PostModLog("GUARD " + msg); } catch (Exception) { }
        }

        // The pin is the wave-1 freeze trick, reused: there is no RPC that roots a player, so anyone who
        // drifts off the pin is put back on it. Paced at 2.6 s because Player.TeleportTo silently refuses
        // inside its own 2 s cooldown.
        private static void EnforcePins(float now)
        {
            if (Pins.Count == 0) return;
            var nowTicks = DateTime.UtcNow.Ticks;
            List<long> gone = null;
            foreach (var kv in Pins)
            {
                if (kv.Value.UntilTicksUtc <= nowTicks) { (gone ?? (gone = new List<long>())).Add(kv.Key); continue; }
                var peer = FindPeerByUid(kv.Key);
                if (peer == null) { (gone ?? (gone = new List<long>())).Add(kv.Key); continue; }
                if (Vector3.Distance(peer.m_refPos, kv.Value.Pos) <= 3f) continue;
                try
                {
                    // Chat.RPC_TeleportPlayer is registered on EVERY client (Chat.cs:130) and has no sender
                    // or admin check — the one teleport that works on unmodded clients.
                    ZRoutedRpc.instance.InvokeRoutedRPC(kv.Key, "RPC_TeleportPlayer", kv.Value.Pos, Quaternion.identity, true);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Guard pin teleport failed for {kv.Value.Name}: {e.Message}"); }
            }
            if (gone == null) return;
            for (var i = 0; i < gone.Count; i++) Pins.Remove(gone[i]);
        }

        /// <summary>
        /// Suppress the 'speed' rule for this peer for AcTeleportGraceSeconds. Called automatically whenever
        /// a teleport RPC is routed to the peer (see the two watch patches); siblings may call it directly
        /// before any movement they cause themselves.
        /// </summary>
        internal static void NoteTeleport(long uid)
        {
            if (uid == 0L) return;
            Sample s;
            if (!Samples.TryGetValue(uid, out s)) return;
            s.NoSpeedUntil = Time.unscaledTime + TeleportGrace;
            s.SpeedStreak = 0;
        }

        // Both overloads of InvokeRoutedRPC that can carry a teleport are watched, one class each (house
        // rule: one target method per patch class). Prefix, so the grace window starts before the client can
        // possibly have moved. Fail-open in every branch: this must never break the RPC bus.
        [HarmonyPatch(typeof(ZRoutedRpc), "InvokeRoutedRPC", new[] { typeof(long), typeof(string), typeof(object[]) })]
        internal static class Wave7TeleportWatchPatch
        {
            [HarmonyPrefix]
            private static void Prefix(long targetPeerID, string methodName)
            {
                if (!_inited || !AcOn) return;
                try
                {
                    if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                    if (IsTeleportRpc(methodName)) NoteTeleport(targetPeerID);
                }
                catch (Exception) { }
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "InvokeRoutedRPC", new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) })]
        internal static class Wave7TeleportZdoWatchPatch
        {
            [HarmonyPrefix]
            private static void Prefix(long targetPeerID, string methodName)
            {
                if (!_inited || !AcOn) return;
                try
                {
                    if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                    if (IsTeleportRpc(methodName)) NoteTeleport(targetPeerID);
                }
                catch (Exception) { }
            }
        }

        private static bool IsTeleportRpc(string methodName) =>
            string.Equals(methodName, "RPC_TeleportPlayer", StringComparison.Ordinal) ||
            string.Equals(methodName, "RPC_TeleportTo", StringComparison.Ordinal);

        // ==================== 2. client-mod enforcement ====================

        // ---- server side: schedule + send the request ----

        private static void StepModProbes(float now)
        {
            if (ModProbes.Count == 0) return;
            // SNAPSHOT the keys: RecordNoAnswer can end in a kick, whose Disconnect runs our own leave
            // prefix, which removes from ModProbes — mutating the dictionary we would otherwise be
            // enumerating.
            ProbeScratch.Clear();
            foreach (var kv in ModProbes) ProbeScratch.Add(kv.Key);

            for (var i = 0; i < ProbeScratch.Count; i++)
            {
                var uid = ProbeScratch[i];
                ModProbe p;
                if (!ModProbes.TryGetValue(uid, out p)) continue;
                if (p.Answered) continue;
                var peer = FindPeerByUid(uid);
                if (peer == null) continue;   // left during the delay; the leave patch drops it

                // The clock only starts once the client is ABLE to answer. OnModListReq drops the request
                // while Player.m_localPlayer is null, so a peer parked on the character-selection screen
                // would otherwise burn every attempt and be recorded as "did not answer" — and kicked, if
                // KickUnansweredMods is on — without ever having been asked a question it could hear.
                if (!p.Spawned)
                {
                    if (!PeerInWorld(peer)) continue;   // re-checked on the next tick; nothing is consumed
                    p.Spawned = true;
                    p.Attempts = 0;
                    p.NextAt = now + FirstModProbeDelay;
                }

                if (p.Attempts >= MaxModProbes)
                {
                    // Out of attempts. Only NOW may the peer be recorded as unable to answer, and only after
                    // the last window elapsed — a slow client can still answer late, and a late answer always
                    // wins (OnModList overwrites whatever we wrote here).
                    if (now >= p.NextAt) { p.Answered = true; RecordNoAnswer(uid, p); }
                    continue;
                }
                if (now < p.NextAt) continue;
                p.Attempts++;
                p.NextAt = now + (p.Attempts <= 1 ? SecondModProbeDelay : ThirdModProbeDelay);
                try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "AP_ModListReq"); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"AP_ModListReq to {uid} failed: {e.Message}"); }
            }
            ProbeScratch.Clear();
        }

        private static void RecordNoAnswer(long uid, ModProbe p)
        {
            // "no-mod" is a FIRST-CLASS status, never folded into "ok". It means exactly one thing: this
            // client did not answer. The overwhelmingly common cause is a player without our companion DLL;
            // a client deliberately suppressing the reply looks identical, and that is a hard limit.
            WriteModRow(p.Id, "no-mod", "client did not answer the plugin-list request (no companion DLL, or the reply was suppressed)");
            CompanionPlugin.FeatureLog($"Mod enforcement: {NameOf(p.Id)} ({p.Id}) did not answer - status 'no-mod' (not 'ok').");
            if (EnforceMode != 1 || !KickUnanswered) return;
            // Admins are exempt from the kick, exactly like the fly rule and the lockdown/reserved-slot
            // gates: the mod policy is written for players, and an owner locked out by it cannot reach the
            // panel that would switch it off. The 'no-mod' row above is still written either way.
            if (CompanionPlugin.FeatureIsAdminId(p.Id))
            {
                CompanionPlugin.FeatureLog($"Mod enforcement: {NameOf(p.Id)} ({p.Id}) is on the adminlist - not kicked for 'no-mod'.");
                return;
            }
            KickForMods(uid, p.Id, "no-mod", "this server requires AdminPanelCompanion.dll so it can verify your mod list");
        }

        // ---- CLIENT side: answer the server's request ----
        // Trust template copied from CompanionPlugin.OnHealSelf / Wave34Core.OnCapProbe: a client-side
        // executor runs only for packets the SERVER sent, and only once a local Player exists. A dedicated
        // server has no local Player, so it never answers its own request.
        private static void OnModListReq(long sender)
        {
            try
            {
                if (!SenderIsTrustedServer(sender)) return;
                if (Player.m_localPlayer == null) return;   // not in-world yet; the server asks again later

                var list = LocalPluginList();
                var pkg = new ZPackage();
                pkg.Write(ModListVer);
                pkg.Write(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    pkg.Write(list[i].Key);
                    pkg.Write(list[i].Value);
                }
                ZRoutedRpc.instance?.InvokeRoutedRPC(sender, "AP_ModList", pkg);
            }
            catch (Exception) { /* never let a plugin-list answer break a client */ }
        }

        // BepInEx 5.4: BepInEx.Bootstrap.Chainloader.PluginInfos is a
        // Dictionary<string, PluginInfo>, PluginInfo.Metadata is a BepInPlugin with GUID / Name / Version
        // (verified by decompiling the referenced BepInEx.dll). Isolated in its own method with a blanket
        // catch so a BepInEx 6 shape change degrades to "reports an empty list" instead of throwing inside
        // an RPC handler; an empty list is then honestly reported as such by the server.
        private static List<KeyValuePair<string, string>> LocalPluginList()
        {
            var res = new List<KeyValuePair<string, string>>();
            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (infos == null) return res;
                foreach (var kv in infos)
                {
                    if (res.Count >= MaxModsAccepted) break;
                    var meta = kv.Value != null ? kv.Value.Metadata : null;
                    var guid = meta != null && !string.IsNullOrEmpty(meta.GUID) ? meta.GUID : kv.Key;
                    if (string.IsNullOrEmpty(guid)) continue;
                    var ver = "";
                    try { ver = meta != null && meta.Version != null ? meta.Version.ToString() : ""; }
                    catch (Exception) { }
                    res.Add(new KeyValuePair<string, string>(Clamp(guid, MaxGuidLen), Clamp(ver, MaxModVerLen)));
                }
            }
            catch (Exception) { res.Clear(); }
            res.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            return res;
        }

        // ---- SERVER side: record + evaluate the answer ----
        // The sender is trustworthy AS AN IDENTITY (RouteRpcSanitizer re-stamps every socket-delivered
        // packet with the real peer uid, so a client can only ever report FOR ITSELF). The CONTENT is not
        // trustworthy at all — see the file header.
        private static void OnModList(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!EnforceOn) return;   // feature off: ignore unsolicited replies entirely

            // ---- accept-or-drop, BEFORE any parsing, storing or alerting ----
            // Everything this handler goes on to do (a full guard_mods rewrite, an alert to every admin, a
            // mod-log line, a Discord post) is expensive and remotely triggerable, so an unsolicited, a
            // repeated or a too-frequent reply is dropped here and costs nothing.
            ModProbe probe;
            if (!ModProbes.TryGetValue(sender, out probe)) return;   // never probed: not a reply to anything
            if (probe.Attempts <= 0) return;                         // no request has gone out to this peer yet
            var nowT = Time.unscaledTime;
            // One answer per probe. A LATE answer is still welcome (it overwrites a 'no-mod' row) — that is
            // why this is not a bare `if (probe.Answered) return;` — but the same probe is answered once.
            if (probe.AcceptedAttempt == probe.Attempts) return;
            if (probe.Accepted >= MaxModReplies) return;
            if (probe.Accepted > 0 && nowT - probe.LastAcceptAt < ModReplyCooldown) return;

            int ver, count;
            var guids = new List<string>();
            var shown = new List<string>();
            try
            {
                ver = pkg.ReadInt();
                if (ver != ModListVer)
                {
                    CompanionPlugin.FeatureLog($"AP_ModList from {sender}: unsupported payload version {ver} (expected {ModListVer}) - discarded");
                    return;
                }
                count = pkg.ReadInt();
                if (count < 0) count = 0;
                if (count > MaxModsAccepted) count = MaxModsAccepted;
                for (var i = 0; i < count; i++)
                {
                    // Client-authored text: length-capped AND stripped of the separators our own rows use,
                    // because the summary below is written straight into a '|'-delimited store row.
                    var g = CleanText(pkg.ReadString(), MaxGuidLen);
                    var v = CleanText(pkg.ReadString(), MaxModVerLen);
                    if (g.Length == 0) continue;
                    guids.Add(g);
                    if (shown.Count < 12) shown.Add(v.Length == 0 ? g : g + " " + v);
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_ModList: malformed packet dropped ({e.Message})");
                return;
            }

            var peer = FindPeerByUid(sender);
            var id = peer != null && peer.m_socket != null ? CleanId(peer.m_socket.GetHostName()) : "";
            if (id.Length == 0) return;
            RememberName(id, peer.m_playerName);

            probe.Answered = true;
            probe.Id = id;
            probe.AcceptedAttempt = probe.Attempts;
            probe.Accepted++;
            probe.LastAcceptAt = nowT;

            string status, detail;
            EvaluateMods(guids, out status, out detail);
            // An admin's toolset is not the players' policy: adminlist members are exempt here for the same
            // reason they are exempt from the fly rule and the lockdown gate, and because a RequiredMods typo
            // must not be able to lock the owner out of the panel that would fix it.
            var exempt = CompanionPlugin.FeatureIsAdminId(id);
            if (exempt && status != "ok")
            {
                detail = "adminlist member - mod policy not applied (" + detail + ")";
                status = "admin";
            }
            var summary = detail + (shown.Count > 0 ? " | reported: " + string.Join(", ", shown.ToArray()) : " | reported: none");
            WriteModRow(id, status, summary);

            CompanionPlugin.FeatureLog($"Mod list from {NameOf(id)} ({id}): {guids.Count} plugin(s), status={status}. {detail}");
            if (status != "ok" && !exempt)
            {
                var line = $"Mod policy: {NameOf(id)} ({id}) is '{status}' - {detail}. Self-reported list; a modified client can lie.";
                try { Wave1Moderation.NotifyOnlineAdmins(line); } catch (Exception) { }
                try { Wave1AuditRpc.PostModLog("MODPOLICY " + line); } catch (Exception) { }
                try { Wave34Core.Enqueue("Mod policy", $"**{NameOf(id)}** (`{id}`)\nStatus: `{status}`\n{detail}\n_Self-reported; not a security control._", Wave34Core.ColorInfo); }
                catch (Exception) { }
                if (EnforceMode == 1) KickForMods(sender, id, status, detail);
            }
        }

        private static void EvaluateMods(List<string> guids, out string status, out string detail)
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < guids.Count; i++) have.Add(guids[i]);

            var forbidden = new List<string>();
            foreach (var f in SplitCsv(_forbiddenMods != null ? _forbiddenMods.Value : ""))
                if (have.Contains(f)) forbidden.Add(f);
            if (forbidden.Count > 0)
            {
                status = "forbidden";
                detail = "forbidden plugin(s) present: " + string.Join(", ", forbidden.ToArray());
                return;
            }

            var missing = new List<string>();
            foreach (var r in SplitCsv(_requiredMods != null ? _requiredMods.Value : ""))
                if (!have.Contains(r)) missing.Add(r);
            if (missing.Count > 0)
            {
                status = "missing";
                detail = "required plugin(s) absent: " + string.Join(", ", missing.ToArray());
                return;
            }

            status = "ok";
            detail = $"{guids.Count} plugin(s) reported, policy satisfied";
        }

        private static void KickForMods(long uid, string id, string status, string detail)
        {
            try { Wave1Moderation.SendPlayerText(uid, "Mod policy: " + Clamp(detail, 120)); }
            catch (Exception) { }
            var kicked = false;
            try { kicked = CompanionPlugin.FeatureKick(uid); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Mod-policy kick failed for {id}: {e.Message}"); }
            var line = $"Mod policy kick: {NameOf(id)} ({id}) status={status} ({detail}); kick={kicked}.";
            CompanionPlugin.FeatureLog(line);
            FeatureStore.Append(LogGuard, $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|MODKICK|{id}|{NameOf(id)}|{status}|0|{CleanText(detail)}");
            try { Wave1Moderation.NotifyOnlineAdmins(line); } catch (Exception) { }
            try { Wave1AuditRpc.PostModLog("MODPOLICY " + line); } catch (Exception) { }
        }

        private static void WriteModRow(string id, string status, string detail)
        {
            id = CleanId(id);
            if (id.Length == 0) return;
            var t = FeatureStore.Table(TblMods);
            var key = FindKey(t, id) ?? id;
            t[key] = status + "|" + CleanText(detail) + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblMods);
        }

        // ==================== 3. AP_SrvGuardStateReq / AP_SrvGuardAction ====================

        private static void OnGuardStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvGuardStateReq")) return;

            var pkg = new ZPackage();
            pkg.Write(GuardStateVer);          // payload version — bump, never reorder
            pkg.Write(AcOn);
            pkg.Write(EnforceOn);

            // flags: (playerName, id, rule, hits, lastTicksUtc), newest first, capped at StateCap.
            var flags = new List<KeyValuePair<long, string[]>>();
            foreach (var kv in FeatureStore.Table(TblFlags))
            {
                int hits; long ticks; string rule;
                ParseFlag(kv.Value, out rule, out hits, out ticks);
                if (hits <= 0) continue;
                var id = kv.Key;
                var hash = id.LastIndexOf('#');
                if (hash > 0)
                {
                    if (rule.Length == 0) rule = id.Substring(hash + 1);
                    id = id.Substring(0, hash);
                }
                flags.Add(new KeyValuePair<long, string[]>(ticks, new[]
                {
                    NameOf(id), id, rule.Length == 0 ? "?" : rule,
                    hits.ToString(CultureInfo.InvariantCulture), ticks.ToString(CultureInfo.InvariantCulture),
                }));
            }
            flags.Sort((a, b) => b.Key.CompareTo(a.Key));
            var flagCount = Math.Min(flags.Count, StateCap);
            pkg.Write(flagCount);
            for (var i = 0; i < flagCount; i++)
            {
                var r = flags[i].Value;
                pkg.Write(r[0]);
                pkg.Write(r[1]);
                pkg.Write(r[2]);
                pkg.Write(int.Parse(r[3], CultureInfo.InvariantCulture));
                pkg.Write(long.Parse(r[4], CultureInfo.InvariantCulture));
            }

            // mods: (playerName, id, status, detail), newest first, capped at StateCap.
            var mods = new List<KeyValuePair<long, string[]>>();
            foreach (var kv in FeatureStore.Table(TblMods))
            {
                string status, detail; long ticks;
                ParseModRow(kv.Value, out status, out detail, out ticks);
                mods.Add(new KeyValuePair<long, string[]>(ticks, new[] { NameOf(kv.Key), kv.Key, status, detail }));
            }
            mods.Sort((a, b) => b.Key.CompareTo(a.Key));
            var modCount = Math.Min(mods.Count, StateCap);
            pkg.Write(modCount);
            for (var i = 0; i < modCount; i++)
            {
                var r = mods[i].Value;
                pkg.Write(r[0]);
                pkg.Write(r[1]);
                pkg.Write(r[2]);
                pkg.Write(r[3]);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_GuardState", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_GuardState reply failed: {e.Message}"); }
        }

        // ZPackage: string id, int action (0 clear flags, 1 kick, 2 add to allowlist).
        private static void OnGuardAction(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvGuardAction")) return;
            string id; int action;
            try { id = CleanId(pkg.ReadString()); action = pkg.ReadInt(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvGuardAction: malformed packet dropped ({e.Message})"); return; }
            if (id.Length == 0) return;

            var admin = CompanionPlugin.SenderDisplayName(sender);
            switch (action)
            {
                case 0:
                {
                    var removed = ClearFlags(id);
                    CompanionPlugin.SrvAudit(sender, "GUARD-CLEAR", $"id={id} rows={removed}");
                    CompanionPlugin.FeatureLog($"Guard flags cleared for {id} by {admin} ({removed} row(s))");
                    try { Wave1AuditRpc.PostModLog($"GUARD CLEAR {id} ({removed} rows, by {admin})"); } catch (Exception) { }
                    CompanionPlugin.NotifySender(sender, $"Cleared {removed} guard flag row(s) for {id}");
                    return;
                }
                case 1:
                {
                    var peer = FindPeerById(id);
                    if (peer == null)
                    {
                        CompanionPlugin.SrvAudit(sender, "GUARD-KICK", $"id={id} result=not-online");
                        CompanionPlugin.NotifySender(sender, $"{id} is not connected");
                        return;
                    }
                    Pins.Remove(peer.m_uid);
                    var ok = false;
                    try { ok = CompanionPlugin.FeatureKick(peer.m_uid); }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Guard kick failed for {id}: {e.Message}"); }
                    CompanionPlugin.SrvAudit(sender, "GUARD-KICK", $"id={id} name={peer.m_playerName} result={ok}");
                    try { Wave1AuditRpc.PostModLog($"GUARD KICK {id} (by {admin})"); } catch (Exception) { }
                    CompanionPlugin.NotifySender(sender, ok ? $"Kicked {id}" : $"Kick failed for {id}");
                    return;
                }
                case 2:
                {
                    var t = FeatureStore.Table(TblAllow);
                    t[FindKey(t, id) ?? id] = "1";
                    FeatureStore.SaveTable(TblAllow);
                    ClearFlags(id);
                    foreach (var uid in UidsOf(id)) Pins.Remove(uid);
                    CompanionPlugin.SrvAudit(sender, "GUARD-ALLOW", $"id={id}");
                    CompanionPlugin.FeatureLog($"Guard allowlist: {id} added by {admin} (monitor is now blind to this player)");
                    try { Wave1AuditRpc.PostModLog($"GUARD ALLOWLIST {id} (by {admin})"); } catch (Exception) { }
                    CompanionPlugin.NotifySender(sender, $"{id} is now exempt from the impossible-state monitor");
                    return;
                }
                default:
                    CompanionPlugin.SrvAudit(sender, "GUARD-ACTION", $"id={id} action={action} result=unknown-action");
                    CompanionPlugin.NotifySender(sender, "Unknown guard action");
                    return;
            }
        }

        private static int ClearFlags(string id)
        {
            var t = FeatureStore.Table(TblFlags);
            if (t.Count == 0) return 0;
            List<string> dead = null;
            foreach (var kv in t)
            {
                var key = kv.Key;
                var hash = key.LastIndexOf('#');
                var rowId = hash > 0 ? key.Substring(0, hash) : key;
                if (!Wave1Moderation.IdMatches(rowId, id)) continue;
                (dead ?? (dead = new List<string>())).Add(key);
            }
            if (dead == null) return 0;
            for (var i = 0; i < dead.Count; i++) { t.Remove(dead[i]); AlertAt.Remove(dead[i]); }
            FeatureStore.SaveTable(TblFlags);
            EscalatedAt.Remove(id);
            return dead.Count;
        }

        private static int TotalHits(string id)
        {
            var total = 0;
            foreach (var kv in FeatureStore.Table(TblFlags))
            {
                var key = kv.Key;
                var hash = key.LastIndexOf('#');
                var rowId = hash > 0 ? key.Substring(0, hash) : key;
                if (!Wave1Moderation.IdMatches(rowId, id)) continue;
                int hits; long ticks; string rule;
                ParseFlag(kv.Value, out rule, out hits, out ticks);
                total += Math.Max(0, hits);
            }
            return total;
        }

        // ==================== join / leave hooks ====================

        // POSTFIX on RPC_PeerInfo: m_uid / m_playerName exist by now (the vanilla reject ladder runs first,
        // ZNet.cs:931-933), which is why every join hook in this mod is a postfix.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave7JoinPatch
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
                    var host = peer.m_socket != null ? CleanId(peer.m_socket.GetHostName()) : "";
                    if (host.Length == 0) return;
                    RememberName(host, peer.m_playerName);

                    // Only schedule the plugin-list request when enforcement is switched on. A server that
                    // upgrades the DLL with the feature off sends nothing at all.
                    if (!EnforceOn) return;
                    ModProbe p2;
                    if (!ModProbes.TryGetValue(peer.m_uid, out p2))
                    {
                        p2 = new ModProbe();
                        ModProbes[peer.m_uid] = p2;
                    }
                    p2.Id = host;
                    p2.Name = CleanText(peer.m_playerName);
                    if (p2.Attempts == 0 && p2.NextAt <= 0f) p2.NextAt = Time.unscaledTime + FirstModProbeDelay;
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Guard join hook failed: {e.Message}"); }
            }
        }

        // PREFIX on Disconnect — the peer is still readable here (the original disposes it).
        [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
        internal static class Wave7LeavePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (!_inited || __instance == null || !__instance.IsServer() || peer == null) return;
                try
                {
                    Samples.Remove(peer.m_uid);
                    ModProbes.Remove(peer.m_uid);
                    Pins.Remove(peer.m_uid);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Guard leave hook failed: {e.Message}"); }
            }
        }

        // ==================== shared helpers ====================

        // CompanionPlugin.SenderIsServer is private and this file is a plain static class. Bound reflectively
        // (it lives in OUR assembly, so this is stable) with an equivalent fallback, so a future rename
        // degrades to "the client stops answering", never to "anyone can impersonate the server".
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
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return false;
                var serverPeer = znet.GetServerPeer();                       // non-null only on a client
                if (serverPeer != null && serverPeer.m_uid != 0L && sender == serverPeer.m_uid) return true;
                if (!znet.IsServer() || ZDOMan.instance == null) return false;
                if (sender != ZDOMan.GetSessionID()) return false;
                var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                return f != null && (bool)f.GetValue(null);
            }
            catch (Exception) { return false; }
        }

        private static bool IsAllowlisted(string id)
        {
            try
            {
                var t = FeatureStore.Table(TblAllow);
                return t.Count != 0 && FindKey(t, id) != null;
            }
            catch (Exception) { return false; }
        }

        private static void RememberName(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return;
            if (NameById.Count > NameCacheCap) NameById.Clear();   // bounded; it is only a display convenience
            NameById[id] = CleanText(name, MaxNameLen);
        }

        private static string NameOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            string n;
            if (NameById.TryGetValue(id, out n) && n.Length > 0) return n;
            var peer = FindPeerById(id);
            if (peer != null && !string.IsNullOrEmpty(peer.m_playerName)) return CleanText(peer.m_playerName, MaxNameLen);
            return "?";
        }

        private static ZNetPeer FindPeerByUid(long uid)
        {
            try
            {
                if (ZNet.instance == null) return null;
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_uid == uid) return peer;
            }
            catch (Exception) { }
            return null;
        }

        // "This peer can answer an RPC that needs Player.m_localPlayer." Same pair the sampler uses: a peer
        // is only ready and carrying a character ZDOID once it has actually spawned into the world.
        private static bool PeerInWorld(ZNetPeer peer)
        {
            try { return peer != null && peer.IsReady() && !peer.m_characterID.IsNone(); }
            catch (Exception) { return false; }
        }

        private static ZNetPeer FindPeerById(string id)
        {
            try
            {
                if (ZNet.instance == null || string.IsNullOrEmpty(id)) return null;
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    if (Wave1Moderation.IdMatches(id, peer.m_socket.GetHostName())) return peer;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static List<long> UidsOf(string id)
        {
            var res = new List<long>();
            try
            {
                if (ZNet.instance == null) return res;
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    if (Wave1Moderation.IdMatches(id, peer.m_socket.GetHostName())) res.Add(peer.m_uid);
                }
            }
            catch (Exception) { }
            return res;
        }

        private static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (Wave1Moderation.IdMatches(kv.Key, id)) return kv.Key;
            return null;
        }

        private static void PruneByUid<T>(Dictionary<long, T> map, List<long> alive)
        {
            List<long> dead = null;
            foreach (var kv in map)
            {
                var found = false;
                for (var i = 0; i < alive.Count; i++) if (alive[i] == kv.Key) { found = true; break; }
                if (!found) (dead ?? (dead = new List<long>())).Add(kv.Key);
            }
            if (dead == null) return;
            for (var i = 0; i < dead.Count; i++) map.Remove(dead[i]);
        }

        private static IEnumerable<string> SplitCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) yield break;
            foreach (var part in csv.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) yield return Clamp(p, MaxGuidLen);
            }
        }

        private static void ParseFlag(string value, out string rule, out int hits, out long ticks)
        {
            rule = ""; hits = 0; ticks = 0;
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 3);
            if (parts.Length > 0) rule = parts[0];
            if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hits);
            if (parts.Length > 2) long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
        }

        private static void ParseModRow(string value, out string status, out string detail, out long ticks)
        {
            status = "unknown"; detail = ""; ticks = 0;
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 3);
            if (parts.Length > 0 && parts[0].Length > 0) status = parts[0];
            if (parts.Length > 1) detail = parts[1];
            if (parts.Length > 2) long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
        }

        // Ids are stored AS ENTERED (trimmed) — the game's own checks compare the full networkUserId, so
        // stripping a platform prefix silently no-ops crossplay ids.
        private static string CleanId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxIdLen) s = s.Substring(0, MaxIdLen);
            if (s.IndexOf(' ') >= 0) return "";
            return s.Replace('#', '_').Replace('|', '_');   // both are separators in our keys/values
        }

        private static string CleanText(string s) => CleanText(s, MaxTextLen);

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

        // Invariant culture everywhere: a server under a comma-decimal locale must still write rows the
        // panel (and this file's own parser) can read back.
        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
    }
}

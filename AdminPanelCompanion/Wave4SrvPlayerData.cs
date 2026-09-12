using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 4 — player-data services (server side) ====================
    // Rescue / un-stuck, death log + grave teleport, playtime ledger, item audit, player reset.
    //
    // CAPABILITY TIERS — every feature in this file is labelled, because half of them physically cannot
    // work on a vanilla client and the UI has to say so:
    //
    //   TIER-VANILLA (works for EVERY player, modded or not):
    //     * rescue / un-stuck (AP_SrvRescueReq)      — vanilla "RPC_TeleportPlayer" (Chat.cs:130)
    //     * grave teleport    (AP_SrvGraveTpReq)     — same RPC
    //     * death log         (AP_SrvDeathLogReq)    — server-side ZDO observation (Wave34Core writes it)
    //     * playtime ledger   (AP_SrvLedgerReq)      — wave-1 "presence" table, peer join/leave only
    //     * the "!unstuck" chat command              — vanilla chat in, vanilla teleport out
    //
    //   TIER-MODDED (needs AdminPanelCompanion.dll on the TARGET's client — admins have it, ordinary
    //   players usually do NOT):
    //     * item audit        (AP_SrvItemAuditReq)   — inventories live in the player's .fch, client-side
    //     * player reset      (AP_SrvPlayerReset)    — same reason; a wipe is executed BY the target client
    //
    //   TIER-IMPOSSIBLE (do not promise it, ever):
    //     * reading or editing an OFFLINE player's character file. The .fch is on the player's own machine;
    //       a dedicated server has never seen it. Everything "offline" here is a QUEUE row in
    //       "offline_<id>" that a sibling (Wave4Vault) delivers on the player's next join.
    //
    // Design rules inherited from waves 1-2 (see spec-companion.md / spec-valheim-api.md):
    //  * Durable state lives in FeatureStore tables; ticks are DateTime.UtcNow.Ticks on the wire and in
    //    the store; floats in stored strings are InvariantCulture.
    //  * Every admin RPC is gated by CompanionPlugin.SenderCanFeature and audited with rich detail.
    //  * ONE Harmony class per target method, applied in its own try/catch, reported to Wave2Ops.ReportPatch.
    //  * The server has no GameObjects: no Heightmap, no Physics, no Character list. ZDO layer + peers only.
    //  * Never sweep the whole ZDO set synchronously — the only ZDO read here is ONE sector (§ SafeSurface).
    //
    // SIBLING COUPLING (read before changing anything below):
    //  * Wave34Core owns death detection, the "deaths" table and the capability probe. Deaths are read
    //    through Wave34Core.RecentDeaths and capability through Wave34Core.HasMod; neither is re-implemented.
    //  * Wave4Vault owns the offline queue: the RPCs (AP_SrvOfflineQueueReq / AP_SrvOfflineAdd /
    //    AP_SrvOfflineClear), the "offline_<id>" table naming, id canonicalisation AND delivery on join.
    //    An offline rescue is therefore written through Wave4Vault.EnqueueOffline(id, "tp", "x|y|z") and
    //    never by composing a table name here — the two halves would otherwise read different files.
    //  * DELIBERATE DIVERGENCE — a queued RESET does NOT use the vault's queue. The vault's "strip" kind
    //    means "remove N of ONE prefab" (ParseStrip -> AP_RemoveItem); a reset is a FULL wipe, which that
    //    vocabulary cannot express, and a row the vault cannot parse would sit undeliverable in its queue
    //    forever. Queued resets therefore live in this file's own "resetq" table and are delivered by this
    //    file's own join hook through the AP_PlayerStrip executor below. Same promise (applies on the next
    //    join, never touches a character file), separate lane.
    //
    // WIRE NOTE — every reply's first field is int ver = 1; the panel discards a whole reply whose version
    // it does not know. Never reorder fields; append only, behind a bumped version.
    internal static class Wave4PlayerData
    {
        // ---- store tables / logs (shared contract with the panel + sibling wave files) ----
        private const string TblPresence = "presence";     // wave 1: id -> "first|last|sessions|totalSeconds|lastName"
        private const string TblResetQ = "resetq";         // ours: id -> "queuedTicks|reason" (pending full wipe)

        // ---- wire caps (server-side and mandatory: an over-long reply is discarded WHOLE by the panel) ----
        private const int DeathShipCap = 60;
        private const int LedgerShipCap = 60;
        private const int AuditShipCap = 60;
        private const int MaxIdLen = 64;
        private const int MaxTextLen = 200;
        private const int MaxPrefabLen = 64;
        private const int ResetQueueCap = 200;             // pending resets across all players

        // ---- world / teleport constants ----
        private const float WaterLevel = 30f;              // ZoneSystem.m_waterLevel default (ZoneSystem.cs:385)
        private const float NudgeMetres = 3f;              // mode 0
        private const float SurfaceClearance = 1.5f;       // how far above the resolved ground we drop the player
        private const float TeleportCooldown = 2.5f;       // Player.TeleportTo refuses inside 2 s (Player.cs:5467)
        private const float WorldEdge = 15000f;            // sanity clamp for admin-supplied coordinates
        private const float SafeRadius = 20f;              // "near the player" for the ZDO surface proxy
        private const int SectorZdoCap = 4000;             // hard bound on the one-sector proxy scan

        // ---- item audit / strip ----
        private const int WireVer = 1;
        private const float StripConfirmSeconds = 12f;     // no AP_StripRep by then => fall back to the queue
        private const float ResetDeliveryDelay = 12f;      // a just-joined client has no player object yet
        private const int MaxOnlineAsk = 128;              // never blast more than this many probes at once

        // ---- config ----
        private static ConfigEntry<bool> _enablePlayerReset;
        private static ConfigEntry<bool> _enableUnstuckCommand;
        private static ConfigEntry<int> _unstuckCooldownSeconds;
        private static ConfigEntry<int> _itemAuditTimeoutSeconds;

        private static bool ResetEnabled => _enablePlayerReset != null && _enablePlayerReset.Value;
        private static bool UnstuckCommandOn => _enableUnstuckCommand != null && _enableUnstuckCommand.Value;
        private static int UnstuckCooldown =>
            _unstuckCooldownSeconds != null ? Mathf.Clamp(_unstuckCooldownSeconds.Value, 10, 86400) : 300;
        private static float ItemAuditTimeout =>
            _itemAuditTimeoutSeconds != null ? Mathf.Clamp(_itemAuditTimeoutSeconds.Value, 5, 60) : 20f;

        private static bool _inited;

        // ---- in-memory, session-scoped state ----

        // Teleport pacing. Player.TeleportTo silently refuses inside its own 2 s cooldown, so a second
        // rescue inside that window would look like it worked and do nothing at all.
        private static readonly Dictionary<long, float> LastTeleport = new Dictionary<long, float>();
        // "!unstuck" self-service cooldown, by platform id (survives a relog within the session; a restart
        // resets it, which is the honest trade for not writing a table on every command).
        private static readonly Dictionary<string, long> LastUnstuck = new Dictionary<string, long>(StringComparer.Ordinal);

        private sealed class AuditJob
        {
            public string Prefab;
            public long Requester;
            public float Deadline;
            public int Scanned;                                   // online players considered (coverage denominator)
            public int Answered;                                  // clients that actually replied (i.e. run the mod)
            public readonly HashSet<long> Asked = new HashSet<long>();
            public readonly List<Entry> Results = new List<Entry>();
            public struct Entry { public string Name; public string Id; public int Count; }
        }

        private static AuditJob _audit;

        private sealed class PendingStrip
        {
            public long Requester;      // 0 = system (queued delivery), nobody to notify
            public string Id;
            public string Reason;
            public float Expiry;
            public bool FromQueue;      // true = came from "resetq"; keep the row until it is confirmed
        }

        // uid -> strip we sent and are waiting on a confirmation for.
        private static readonly Dictionary<long, PendingStrip> PendingStrips = new Dictionary<long, PendingStrip>();
        // uid -> queued reset waiting for the client to finish loading before we send it.
        private static readonly Dictionary<long, PendingStrip> ResetDeliveries = new Dictionary<long, PendingStrip>();

        private static float _nextPruneTick;

        // Mirror of CompanionPlugin.SenderSanitizerActive, read reflectively once (it is private and this
        // file may not touch CompanionPlugin.cs). See SenderIsServerPeer for why it matters.
        private static bool _sanitizerFieldRead;
        private static FieldInfo _sanitizerField;

        // WorldGenerator degrades to "unavailable" after the first failure instead of throwing every call.
        private static bool _worldGenBroken;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enablePlayerReset = cfg.Bind("Features", "EnablePlayerReset", false,
                    "Allow admins to WIPE a player's inventory (immediately if they are online with the companion mod, otherwise queued for their next join). Destructive and irreversible: OFF by default, and owner-only when tiered roles are enforced.");
                _enableUnstuckCommand = cfg.Bind("Features", "EnableUnstuckCommand", false,
                    "Let players type !unstuck in chat to teleport themselves to the generated surface at their own position. Works on UNMODDED clients. OFF by default: it can also be used to escape combat or a fall.");
                _unstuckCooldownSeconds = cfg.Bind("Features", "UnstuckCooldownSeconds", 300,
                    "Seconds a player must wait between !unstuck uses (10-86400). Ignored when EnableUnstuckCommand is off.");
                _itemAuditTimeoutSeconds = cfg.Bind("Features", "ItemAuditTimeoutSeconds", 20,
                    "How long an item audit waits for online clients to answer before replying with whatever arrived (5-60). Players without the companion mod never answer and are reported as unanswered coverage.");
            }

            // Audit-chokepoint registration. Split follows the wave-2 lesson (an audited RPC writes a line on
            // EVERY call, so a polled read behind it drowns audit.log):
            //   * AP_SrvRescueReq / AP_SrvRescueOffline / AP_SrvGraveTpReq change the world -> audited, "moderator".
            //   * AP_SrvDeathLogReq / AP_SrvItemAuditReq are ON-DEMAND fetches (never poll them from the
            //     panel) whose content is sensitive — positions, who owns what — so the per-fetch audit line
            //     is deliberate, same call as wave 2's AP_SrvLogTailReq.
            //   * AP_SrvLedgerReq is a panel-refreshed poll: NOT registered (no audit spam). Consequence,
            //     documented rather than hidden: with tiered roles enforced it falls back to owner-only.
            //   * AP_SrvPlayerReset is destructive -> audited with a null grant = owner-only.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRescueReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRescueOffline", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDeathLogReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvGraveTpReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvItemAuditReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvPlayerReset", null);

            ApplyPatch("Wave4PlayerDataRpcRegistration", typeof(Wave4PlayerDataRpcRegistration),
                "rescue / death log / ledger / item audit / player reset RPCs unavailable");
            ApplyPatch("Wave4ResetJoinPatch", typeof(Wave4ResetJoinPatch),
                "queued player resets will not be delivered on join");

            // Vanilla-client self-service. Registered unconditionally so the command always ANSWERS (with
            // "disabled" when the config is off) instead of looking broken; the config decides what it does.
            try { Wave1Chat.RegisterChatCommand("unstuck", OnUnstuckCommand); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"!unstuck command registration failed: {e.Message}"); }
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

            if (_audit != null && now >= _audit.Deadline)
            {
                try { FinishAudit("timeout"); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Item audit finish failed: {e.Message}"); _audit = null; }
            }

            if (ResetDeliveries.Count > 0)
            {
                try { DeliverQueuedResets(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Queued reset delivery failed: {e.Message}"); ResetDeliveries.Clear(); }
            }

            if (PendingStrips.Count > 0)
            {
                try { ExpireStrips(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Strip confirmation sweep failed: {e.Message}"); }
            }

            if (now >= _nextPruneTick)
            {
                _nextPruneTick = now + 60f;
                try { PruneTeleportCooldowns(now); }
                catch (Exception) { }
            }
        }

        // ==================== RPC registration ====================

        // One postfix registers BOTH halves, exactly like CompanionPlugin.cs:59-92: the server-side admin
        // entry points and the two client-side executors. The DLL ships to both sides; each half only ever
        // fires on the side whose guard passes (IsServer() / SenderIsServerPeer()).
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave4PlayerDataRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // --- server-side (admin -> server) ---
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvRescueReq", OnRescueReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvRescueOffline", OnRescueOffline);
                    ZRoutedRpc.instance.Register<string>("AP_SrvDeathLogReq", OnDeathLogReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvGraveTpReq", OnGraveTpReq);
                    ZRoutedRpc.instance.Register<string>("AP_SrvItemAuditReq", OnItemAuditReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvPlayerReset", OnPlayerReset);
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvLedgerReq", new Action<long>(OnLedgerReq));

                    // --- server-side receivers for our own client executors' answers ---
                    ZRoutedRpc.instance.Register<ZPackage>("AP_ItemScanRep", OnItemScanRep);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_StripRep", OnStripRep);

                    // --- client-side executors (only accepted when sent by the server) ---
                    // DELIBERATELY NOT "AP_InvRequest"/"AP_InvData": those names are owned by
                    // CompanionPlugin (client executor) and by the PANEL (reply handler). On a listen-server
                    // host both plugins live in one process and share ZRoutedRpc's function table, so
                    // re-registering either name would silently steal the panel's inventory viewer.
                    // Distinct names, distinct payloads, zero interference with the vault sibling.
                    ZRoutedRpc.instance.Register<ZPackage>("AP_ItemScanReq", OnItemScanReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_PlayerStrip", OnPlayerStrip);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave4 player-data RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. un-stuck / rescue (TIER-VANILLA) ====================

        // ZPackage: long targetUid, int mode.
        //   mode 0 = nudge +3 m at the same X/Z (snap move, no loading screen)
        //   mode 1 = world spawn (StartTemple location icon; see WorldSpawn for the fallback ladder)
        //   mode 2 = "safe surface point" near the player (see SafeSurface — it is an APPROXIMATION)
        private static void OnRescueReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRescueReq")) return;

            long uid; int mode;
            try { uid = pkg.ReadLong(); mode = pkg.ReadInt(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRescueReq: malformed packet dropped ({e.Message})"); return; }
            mode = Mathf.Clamp(mode, 0, 2);

            var peer = FindPeerByUid(uid);
            if (peer == null)
            {
                // TIER-IMPOSSIBLE reminder for the admin: an offline player's position is not on this server.
                CompanionPlugin.SrvAudit(sender, "RESCUE", $"uid={uid} mode={mode} result=not-connected");
                CompanionPlugin.NotifySender(sender,
                    "Rescue: that player is not connected. Queue an offline rescue instead — it applies on their next join.");
                return;
            }

            var now = Time.unscaledTime;
            if (LastTeleport.TryGetValue(uid, out var last) && now - last < TeleportCooldown)
            {
                CompanionPlugin.NotifySender(sender,
                    $"Rescue: wait {Mathf.CeilToInt(TeleportCooldown - (now - last))}s — the game ignores a second teleport inside its 2s cooldown.");
                return;
            }

            // m_refPos is the client's own continuously-reported reference position (ZNetPeer.cs:15) — the
            // only position the server has for a player whose character ZDO it does not simulate.
            var from = peer.m_refPos;
            string how;
            Vector3 to;
            switch (mode)
            {
                case 1: to = WorldSpawn(out how); break;
                case 2: to = SafeSurface(from, out how); break;
                default: to = from + Vector3.up * NudgeMetres; how = $"nudge +{NudgeMetres:0.#}m"; break;
            }

            // distantTeleport=true shows the client's loading screen and waits for the terrain to build;
            // a 3 m nudge does not need it and looks better as a snap move.
            if (!Teleport(uid, to, mode != 0))
            {
                CompanionPlugin.SrvAudit(sender, "RESCUE", $"uid={uid} mode={mode} result=send-failed");
                CompanionPlugin.NotifySender(sender, "Rescue: the teleport RPC could not be sent.");
                return;
            }
            LastTeleport[uid] = now;

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "RESCUE",
                $"uid={uid} name={peer.m_playerName} mode={mode} how={how} from={Fmt(from)} to={Fmt(to)}");
            CompanionPlugin.FeatureLog($"Rescue {peer.m_playerName} (mode {mode}, {how}) by {admin}");
            Wave1AuditRpc.PostModLog($"RESCUE {peer.m_playerName} mode={mode} ({how}) (by {admin})");
            Wave1Moderation.SendPlayerText(uid, "An admin moved you to safety.");
            CompanionPlugin.NotifySender(sender, $"Rescued {peer.m_playerName} ({how})");
        }

        // ZPackage: string id, int mode, float x, float y, float z.
        //   mode 1 -> world spawn, resolved NOW and stored as coordinates
        //   anything else -> the supplied coordinates (e.g. a death point straight out of the death log)
        // TIER-IMPOSSIBLE boundary: this does NOT touch the offline player's character file. It writes one
        // "tp" row into "offline_<id>"; Wave4Vault delivers it the next time that player joins.
        private static void OnRescueOffline(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRescueOffline")) return;

            string id; int mode; float x, y, z;
            try
            {
                id = CleanId(pkg.ReadString());
                mode = pkg.ReadInt();
                x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRescueOffline: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(id)) return;

            Vector3 pos;
            string how;
            if (mode == 1) pos = WorldSpawn(out how);
            else { pos = new Vector3(x, y, z); how = "supplied coordinates"; }
            if (!Sane(pos))
            {
                CompanionPlugin.NotifySender(sender, "Offline rescue: those coordinates are outside the world.");
                return;
            }

            if (!QueueOfflineTp(id, pos))
            {
                CompanionPlugin.NotifySender(sender, "Offline rescue: the queue could not be written (queue full, or no world loaded).");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "RESCUE-QUEUE", $"id={id} how={how} pos={Fmt(pos)}");
            CompanionPlugin.FeatureLog($"Offline rescue queued for {id} -> {Fmt(pos)} ({how}) by {admin}");
            Wave1AuditRpc.PostModLog($"RESCUE-QUEUE {id} -> {Fmt(pos)} (by {admin})");
            CompanionPlugin.NotifySender(sender, $"Offline rescue queued for {id} — it applies on their next join.");
        }

        // Player-facing self-service (TIER-VANILLA: chat in, "RPC_TeleportPlayer" out — no client mod).
        private static void OnUnstuckCommand(long sender, string args)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!UnstuckCommandOn)
            {
                Wave1Moderation.SendPlayerText(sender, "!unstuck is disabled on this server. Ask an admin.");
                return;
            }

            var peer = FindPeerByUid(sender);
            if (peer == null) return;   // host / unresolvable sender: nothing to move
            var id = CompanionPlugin.SenderPlatformId(sender);
            var nowTicks = DateTime.UtcNow.Ticks;

            if (!string.IsNullOrEmpty(id) && LastUnstuck.TryGetValue(id, out var lastTicks))
            {
                var waited = (int)(new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(lastTicks, DateTimeKind.Utc)).TotalSeconds;
                if (waited < UnstuckCooldown)
                {
                    Wave1Moderation.SendPlayerText(sender, $"!unstuck is on cooldown ({UnstuckCooldown - waited}s left).");
                    return;
                }
            }
            if (LastTeleport.TryGetValue(sender, out var lastTp) && Time.unscaledTime - lastTp < TeleportCooldown)
            {
                Wave1Moderation.SendPlayerText(sender, "Hold on a moment and try !unstuck again.");
                return;
            }

            var to = SafeSurface(peer.m_refPos, out var how);
            if (!Teleport(sender, to, true)) return;
            LastTeleport[sender] = Time.unscaledTime;
            if (!string.IsNullOrEmpty(id)) LastUnstuck[id] = nowTicks;

            Wave1Moderation.SendPlayerText(sender, "Un-stuck: you were moved to the surface.");
            CompanionPlugin.SrvAudit(sender, "UNSTUCK-SELF", $"id={id} name={peer.m_playerName} how={how} to={Fmt(to)}");
            CompanionPlugin.FeatureLog($"!unstuck used by {peer.m_playerName} ({id}) -> {Fmt(to)} ({how})");
        }

        /// <summary>
        /// World spawn point. ZoneSystem.GetLocationIcon (ZoneSystem.cs:2150-2176) has a dedicated
        /// IsServer() branch that walks m_locationInstances, so it is the correct server-side accessor —
        /// it is exactly what Game.FindSpawnPoint uses for a fresh spawn (Game.cs:505-507), including the
        /// +2 m lift. Fallback ladder when locations are not generated yet (or the accessor is renamed by a
        /// game update): the world-generator height at 0,0; then a plain high point over 0,0.
        /// </summary>
        private static Vector3 WorldSpawn(out string how)
        {
            try
            {
                if (ZoneSystem.instance != null)
                {
                    var start = StartLocationName();
                    if (ZoneSystem.instance.GetLocationIcon(start, out var pos))
                    {
                        how = start;
                        return pos + Vector3.up * 2f;
                    }
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"World spawn lookup failed (using fallback): {e.Message}"); }

            var g = GeneratedGroundY(0f, 0f);
            if (!float.IsNaN(g))
            {
                how = "fallback: generated ground at 0,0 (start temple not found)";
                return new Vector3(0f, g + SurfaceClearance, 0f);
            }
            how = "fallback: 0,0 at high altitude (no spawn and no world generator — expect a drop)";
            return new Vector3(0f, WaterLevel + 100f, 0f);
        }

        // Game.m_StartLocation is "StartTemple" (Game.cs:57). Read from the live instance when there is one
        // so a modded start location still resolves; the constant is only the fallback.
        private static string StartLocationName()
        {
            try
            {
                if (Game.instance != null)
                {
                    var f = AccessTools.Field(typeof(Game), "m_StartLocation");
                    if (f != null && f.GetValue(Game.instance) is string s && s.Length > 0) return s;
                }
            }
            catch (Exception) { }
            return "StartTemple";
        }

        /// <summary>
        /// "Safe surface point" near a position — an APPROXIMATION, and the UI must say so.
        /// The server has no Heightmap and no physics (spec §0.1), so ZoneSystem.GetGroundHeight is off the
        /// table. Ladder:
        ///   1. WorldGenerator.instance.GetHeight(x, z) (WorldGenerator.cs:852) — pure math, initialised on
        ///      the server by WorldGenerator.Initialize(m_world) (ZNet.cs:299). This is the GENERATED
        ///      terrain height: it does not know about player terrain edits, so a player standing in a dug
        ///      pit is lifted to the original ground, and one on a built platform is dropped to the ground
        ///      under it. That is the honest limit of a server-side surface query.
        ///   2. The highest ZDO within SafeRadius in the player's OWN sector (one bounded
        ///      ZDOMan.FindSectorObjects call, area 0 — never a world sweep). A rough "there is something
        ///      solid up there" proxy.
        ///   3. Nothing found: fall back to the mode-0 nudge, and say which happened.
        /// The caller always lands SurfaceClearance above the result and never below sea level.
        /// </summary>
        private static Vector3 SafeSurface(Vector3 from, out string how)
        {
            var g = GeneratedGroundY(from.x, from.z);
            if (!float.IsNaN(g))
            {
                how = "generated surface (ignores player terrain edits and buildings)";
                return new Vector3(from.x, Mathf.Max(g, WaterLevel) + SurfaceClearance, from.z);
            }

            if (HighestNearbyZdoY(from, out var y))
            {
                how = $"highest known object within {SafeRadius:0}m (approximation)";
                return new Vector3(from.x, Mathf.Max(y, WaterLevel) + SurfaceClearance, from.z);
            }

            how = "no surface data on the server — nudged up instead";
            return from + Vector3.up * NudgeMetres;
        }

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
                CompanionPlugin.FeatureLog($"WorldGenerator height unavailable (surface rescue degraded): {e.Message}");
                return float.NaN;
            }
        }

        // ONE sector (ZoneCompat.FindSectorObjects with area 0: the engine's FindSectorObjects reads the centre
        // sector first, ZDOMan.cs:1201-1204, and a zero near/far distance runs no ring loop; FindObjects,
        // ZDOMan.cs:1426-1441, also appends that sector's portal bucket), hard-capped iteration. This is the
        // only ZDO read in the file and it is deliberately not frame-spread: a single sector is bounded,
        // unlike a world sweep.
        private static bool HighestNearbyZdoY(Vector3 from, out float y)
        {
            y = 0f;
            if (ZDOMan.instance == null) return false;
            try
            {
                var list = new List<ZDO>();
                ZoneCompat.FindSectorObjects(ZDOMan.instance, ZoneSystem.GetZone(from), 0, list);
                var found = false;
                var n = Mathf.Min(list.Count, SectorZdoCap);
                for (var i = 0; i < n; i++)
                {
                    var zdo = list[i];
                    if (zdo == null) continue;
                    var p = zdo.GetPosition();
                    var dx = p.x - from.x;
                    var dz = p.z - from.z;
                    if (dx * dx + dz * dz > SafeRadius * SafeRadius) continue;
                    if (!found || p.y > y) { y = p.y; found = true; }
                }
                return found;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Nearby-ZDO surface probe failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The one teleport that works on UNMODDED clients: Chat.Awake registers
        /// Register&lt;Vector3, Quaternion, bool&gt;("RPC_TeleportPlayer", ...) on every client (Chat.cs:130)
        /// and the handler has no sender or admin check. Chat.instance is null on a dedicated server, so it
        /// must be invoked through ZRoutedRpc directly — never Chat.instance.TeleportPlayer.
        /// </summary>
        private static bool Teleport(long uid, Vector3 pos, bool distant)
        {
            if (!Sane(pos)) return false;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, "RPC_TeleportPlayer", pos, Quaternion.identity, distant);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Teleport to {uid} failed: {e.Message}");
                return false;
            }
        }

        private static void PruneTeleportCooldowns(float now)
        {
            if (LastTeleport.Count == 0) return;
            List<long> dead = null;
            foreach (var kv in LastTeleport)
                if (now - kv.Value > 300f) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var uid in dead) LastTeleport.Remove(uid);
        }

        // ==================== 2. death log + grave teleport (TIER-VANILLA) ====================

        // Death DETECTION belongs to Wave34Core (it polls character ZDOs for the s_dead flip and writes the
        // "deaths" table). This handler is a pure projection of Wave34Core.RecentDeaths onto the wire, so the
        // row format stays owned by exactly one file.
        // AP_DeathLog: {int ver, int shipped, shipped x (long ticksUtc, string playerName, string id,
        //               float x, float y, float z, bool hasTombstone)}
        // ORDER: oldest first, NEWEST LAST — the ordering every other log payload in this mod uses.
        private static void OnDeathLogReq(long sender, string idOrEmpty)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDeathLogReq")) return;

            var filter = CleanId(idOrEmpty);
            List<Wave34Core.DeathInfo> rows;
            try { rows = Wave34Core.RecentDeaths(filter, DeathShipCap); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Death log read failed: {e.Message}");
                rows = new List<Wave34Core.DeathInfo>();
            }

            var pkg = new ZPackage();
            pkg.Write(WireVer);
            pkg.Write(rows.Count);
            foreach (var d in rows)
            {
                pkg.Write(d.TicksUtc);
                pkg.Write(d.PlayerName ?? "");
                pkg.Write(d.PlatformId ?? "");
                pkg.Write(d.Pos.x); pkg.Write(d.Pos.y); pkg.Write(d.Pos.z);
                pkg.Write(d.HasTombstone);
            }
            Reply(sender, "AP_DeathLog", pkg);

            CompanionPlugin.SrvAudit(sender, "DEATHLOG-READ",
                $"filter={(filter.Length == 0 ? "*" : filter)} shipped={rows.Count}");
        }

        // ZPackage: long targetUid (0 = the requesting admin), float x, float y, float z.
        private static void OnGraveTpReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvGraveTpReq")) return;

            long uid; float x, y, z;
            try { uid = pkg.ReadLong(); x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvGraveTpReq: malformed packet dropped ({e.Message})"); return; }
            if (uid == 0L) uid = sender;

            var pos = new Vector3(x, y, z);
            if (!Sane(pos))
            {
                CompanionPlugin.NotifySender(sender, "Grave teleport: those coordinates are outside the world.");
                return;
            }

            // The host is never in ZNet.m_peers, so a null peer for the requester itself is normal and must
            // not block the teleport — only a *different* target has to be a real connected peer.
            var peer = FindPeerByUid(uid);
            if (peer == null && uid != sender)
            {
                CompanionPlugin.SrvAudit(sender, "GRAVE-TP", $"uid={uid} result=not-connected");
                CompanionPlugin.NotifySender(sender, "Grave teleport: that player is not connected.");
                return;
            }

            var now = Time.unscaledTime;
            if (LastTeleport.TryGetValue(uid, out var last) && now - last < TeleportCooldown)
            {
                CompanionPlugin.NotifySender(sender, "Grave teleport: wait a moment (2s teleport cooldown).");
                return;
            }

            // Land slightly above the recorded death position: the corpse position is the character's feet
            // and the ground may have been edited since.
            if (!Teleport(uid, pos + Vector3.up * 2f, true))
            {
                CompanionPlugin.NotifySender(sender, "Grave teleport: the teleport RPC could not be sent.");
                return;
            }
            LastTeleport[uid] = now;

            var who = peer != null ? peer.m_playerName : CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "GRAVE-TP", $"uid={uid} name={who} pos={Fmt(pos)} self={(uid == sender)}");
            CompanionPlugin.FeatureLog($"Grave teleport: {who} -> {Fmt(pos)} (by {CompanionPlugin.SenderDisplayName(sender)})");
            if (uid != sender) Wave1Moderation.SendPlayerText(uid, "An admin teleported you to a death location.");
            CompanionPlugin.NotifySender(sender, $"Teleported {who} to {Fmt(pos)}");
        }

        // ==================== 3. playtime ledger (TIER-VANILLA, pure read) ====================

        // Wave 1's "presence" table: id -> "first|last|sessions|totalSeconds|lastName". Peer-level truth
        // (join/leave), so it is complete for every player, modded or not.
        // AP_Ledger: {int ver, int shipped, shipped x (string id, string lastName, long firstTicks,
        //             long lastTicks, int sessions, long totalSeconds)}
        private static void OnLedgerReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLedgerReq")) return;

            var rows = new List<KeyValuePair<string, long[]>>();   // id -> [first, last, sessions, seconds]
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var kv in FeatureStore.Table(TblPresence))
                {
                    long first, last, seconds; int sessions; string lastName;
                    ParsePresence(kv.Value, out first, out last, out sessions, out seconds, out lastName);
                    if (first == 0 && last == 0 && sessions == 0) continue;   // unparseable row
                    rows.Add(new KeyValuePair<string, long[]>(kv.Key, new[] { first, last, (long)sessions, seconds }));
                    names[kv.Key] = lastName ?? "";
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Ledger read failed: {e.Message}"); }

            rows.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));   // most recently active first
            if (rows.Count > LedgerShipCap) rows.RemoveRange(LedgerShipCap, rows.Count - LedgerShipCap);

            var pkg = new ZPackage();
            pkg.Write(WireVer);
            pkg.Write(rows.Count);
            foreach (var r in rows)
            {
                pkg.Write(r.Key);
                pkg.Write(names.TryGetValue(r.Key, out var n) ? n : "");
                pkg.Write(r.Value[0]);
                pkg.Write(r.Value[1]);
                pkg.Write((int)r.Value[2]);
                pkg.Write(r.Value[3]);
            }
            Reply(sender, "AP_Ledger", pkg);
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

        // ==================== 4. item audit (TIER-MODDED, inherently partial) ====================

        // "Who has X?" can only ever be answered by the clients that HOLD X: a player's inventory lives in
        // their .fch on their own machine and is never uploaded to a dedicated server. So this asks every
        // online client and reports coverage honestly:
        //   scanned  = online players considered
        //   answered = clients that replied (i.e. run the companion mod)
        // Offline players and players without the mod are simply ABSENT from the results — never zero.
        //
        // AP_ItemAudit: {int ver, string prefab, int scanned, int answered, int shipped,
        //                shipped x (string playerName, string id, int count)}
        private static void OnItemAuditReq(long sender, string prefabName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvItemAuditReq")) return;

            var prefab = CleanToken(prefabName, MaxPrefabLen);
            if (prefab.Length == 0)
            {
                CompanionPlugin.NotifySender(sender, "Item audit: give an item prefab name (e.g. SwordBlackmetal).");
                return;
            }
            if (_audit != null)
            {
                CompanionPlugin.NotifySender(sender, "Item audit: one is already running — try again in a few seconds.");
                return;
            }

            var job = new AuditJob
            {
                Prefab = prefab,
                Requester = sender,
                Deadline = Time.unscaledTime + ItemAuditTimeout,
            };

            // The host's own character (listen server) is counted locally: it has no peer entry, so an RPC
            // round trip would never reach it. On a dedicated server Player.m_localPlayer is always null.
            try
            {
                if (Player.m_localPlayer != null)
                {
                    var localCount = CountInInventory(Player.m_localPlayer, prefab);
                    job.Scanned++;
                    job.Answered++;
                    if (localCount > 0)
                        job.Results.Add(new AuditJob.Entry
                        {
                            Name = Player.m_localPlayer.GetPlayerName(),
                            Id = "HOST",
                            Count = localCount,
                        });
                }
            }
            catch (Exception) { }

            var unmodded = 0;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) continue;
                    job.Scanned++;
                    // HasMod: true = the companion answered the capability probe; false = it answered
                    // nothing in time / is known vanilla; null = not probed yet. We ask on true AND null —
                    // an unknown RPC name is a silent no-op on a vanilla client, so a wasted probe is free,
                    // and probing widens coverage. Known-vanilla peers are skipped and counted as such.
                    if (HasMod(peer.m_uid) == false) { unmodded++; continue; }
                    if (job.Asked.Count >= MaxOnlineAsk) break;
                    var req = new ZPackage();
                    req.Write(WireVer);
                    req.Write(prefab);
                    try
                    {
                        ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_ItemScanReq", req);
                        job.Asked.Add(peer.m_uid);
                    }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Item audit probe to {peer.m_playerName} failed: {e.Message}"); }
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Item audit peer sweep failed: {e.Message}"); }

            _audit = job;
            CompanionPlugin.SrvAudit(sender, "ITEM-AUDIT",
                $"prefab={prefab} online={job.Scanned} asked={job.Asked.Count} knownVanilla={unmodded}");
            CompanionPlugin.NotifySender(sender,
                $"Item audit for {prefab}: asked {job.Asked.Count} of {job.Scanned} online player(s) — waiting up to {ItemAuditTimeout:0}s.");

            if (job.Asked.Count == 0) FinishAudit("nobody to ask");
        }

        // Client answer (server side). Only the peers we actually asked are accepted, so a random client
        // cannot inject rows into another admin's audit.
        private static void OnItemScanRep(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var job = _audit;
            if (job == null || !job.Asked.Remove(sender)) return;

            try
            {
                var ver = pkg.ReadInt();
                if (ver != WireVer) return;             // unknown version: discard the WHOLE reply
                var name = CleanText(pkg.ReadString(), MaxTextLen);
                var count = pkg.ReadInt();
                if (count < 0) count = 0;
                if (count > 0 && job.Results.Count < AuditShipCap * 4)
                    job.Results.Add(new AuditJob.Entry
                    {
                        Name = name,
                        Id = CompanionPlugin.SenderPlatformId(sender),
                        Count = count,
                    });
                job.Answered++;
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_ItemScanRep: malformed packet dropped ({e.Message})"); }

            if (job.Asked.Count == 0) FinishAudit("all answered");
        }

        private static void FinishAudit(string why)
        {
            var job = _audit;
            _audit = null;
            if (job == null) return;

            job.Results.Sort((a, b) => b.Count.CompareTo(a.Count));
            var shipped = Mathf.Min(job.Results.Count, AuditShipCap);

            var pkg = new ZPackage();
            pkg.Write(WireVer);
            pkg.Write(job.Prefab);
            pkg.Write(job.Scanned);
            pkg.Write(job.Answered);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++)
            {
                pkg.Write(job.Results[i].Name ?? "");
                pkg.Write(job.Results[i].Id ?? "");
                pkg.Write(job.Results[i].Count);
            }
            Reply(job.Requester, "AP_ItemAudit", pkg);

            CompanionPlugin.FeatureLog(
                $"Item audit '{job.Prefab}' finished ({why}): {job.Answered}/{job.Scanned} answered, {shipped} holder(s)");
        }

        // ---- client-side executor: count one prefab in the local player's inventory ----
        // Runs on the TARGET player's client. Same trust model as CompanionPlugin.OnHealSelf
        // (CompanionPlugin.cs:910-916): only the server may send it, and only when a local player exists.
        private static void OnItemScanReq(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerPeer(sender)) return;

            string prefab;
            try
            {
                if (pkg.ReadInt() != WireVer) return;
                prefab = pkg.ReadString();
            }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(prefab)) return;

            int count;
            try { count = CountInInventory(player, prefab); }
            catch (Exception) { return; }

            var rep = new ZPackage();
            rep.Write(WireVer);
            rep.Write(player.GetPlayerName() ?? "");
            rep.Write(count);
            // Reply to `sender`, which SenderIsServerPeer just proved is the server.
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_ItemScanRep", rep); }
            catch (Exception) { }
        }

        // Same key the existing inventory viewer serializes (CompanionPlugin.cs:895): the drop prefab name,
        // falling back to the shared (localised token) name — so panel-side prefab strings match. Backpacks
        // and chests are NOT included: only what is in the character's own inventory.
        private static int CountInInventory(Player player, string prefab)
        {
            var inv = player != null ? player.GetInventory() : null;
            if (inv == null) return 0;
            var total = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null) continue;
                var key = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                if (!string.Equals(key, prefab, StringComparison.OrdinalIgnoreCase)) continue;
                total += Mathf.Max(0, item.m_stack);
            }
            return total;
        }

        // ==================== 5. player reset / wipe (TIER-MODDED, destructive) ====================

        // ZPackage: string id, long targetUid (0 = offline / unknown), string reason.
        //
        // NEVER touches a file — a wipe is always executed BY the target's own client (TIER-MODDED).
        // Two delivery paths, and exactly ONE of them runs per request:
        //   * target online and not known-vanilla -> "AP_PlayerStrip" to their client. Done once the client
        //     confirms with AP_StripRep inside StripConfirmSeconds.
        //   * target offline, known-vanilla, or the online attempt goes unconfirmed -> a row in "resetq",
        //     delivered by Wave4ResetJoinPatch on their next join (see the header for why this is NOT the
        //     vault's offline queue).
        // The queue is deliberately NOT written alongside a CONFIRMED immediate strip: that would wipe the
        // loot they gathered between the reset and their next login.
        private static void OnPlayerReset(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvPlayerReset")) return;

            if (!ResetEnabled)
            {
                CompanionPlugin.SrvAudit(sender, "RESET", "result=disabled-by-config");
                CompanionPlugin.NotifySender(sender,
                    "Player reset is disabled. Set EnablePlayerReset = true in the companion config to allow it.");
                return;
            }

            string id, reason; long uid;
            try
            {
                id = CleanId(pkg.ReadString());
                uid = pkg.ReadLong();
                reason = CleanText(pkg.ReadString(), MaxTextLen);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvPlayerReset: malformed packet dropped ({e.Message})"); return; }

            var peer = uid != 0L ? FindPeerByUid(uid) : FindPeerById(id);
            if (peer != null && peer.m_socket != null && string.IsNullOrEmpty(id))
                id = CleanId(peer.m_socket.GetHostName());
            if (string.IsNullOrEmpty(id))
            {
                CompanionPlugin.NotifySender(sender, "Player reset: no platform id for that player.");
                return;
            }
            if (reason.Length == 0) reason = "admin reset";

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var name = peer != null ? peer.m_playerName : id;

            if (peer != null && HasMod(peer.m_uid) != false)
            {
                var req = new ZPackage();
                req.Write(WireVer);
                req.Write(reason);
                var sent = false;
                try { ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_PlayerStrip", req); sent = true; }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Strip RPC to {name} failed: {e.Message}"); }

                if (sent)
                {
                    PendingStrips[peer.m_uid] = new PendingStrip
                    {
                        Requester = sender,
                        Id = id,
                        Reason = reason,
                        Expiry = Time.unscaledTime + StripConfirmSeconds,
                    };
                    CompanionPlugin.SrvAudit(sender, "RESET",
                        $"id={id} name={name} mode=online-strip mod={Cap(HasMod(peer.m_uid))} reason={reason}");
                    CompanionPlugin.FeatureLog($"Player reset requested for {name} ({id}) by {admin}: {reason}");
                    Wave1AuditRpc.PostModLog($"RESET {name} ({id}) online strip requested: {reason} (by {admin})");
                    Wave1Moderation.NotifyOnlineAdmins($"Player reset: {name} — inventory wipe requested by {admin}");
                    CompanionPlugin.NotifySender(sender,
                        $"Reset sent to {name}. If their client does not confirm within {StripConfirmSeconds:0}s it will be queued for their next join instead.");
                    return;
                }
            }

            // Offline, known-vanilla, or the RPC could not be sent: queue it.
            if (!QueueReset(id, reason))
            {
                CompanionPlugin.NotifySender(sender, "Player reset: the offline queue could not be written.");
                return;
            }
            CompanionPlugin.SrvAudit(sender, "RESET",
                $"id={id} name={name} mode=queued online={(peer != null)} mod={Cap(peer != null ? HasMod(peer.m_uid) : (bool?)null)} reason={reason}");
            CompanionPlugin.FeatureLog($"Player reset QUEUED for {id} by {admin}: {reason}");
            Wave1AuditRpc.PostModLog($"RESET {id} queued for next join: {reason} (by {admin})");
            Wave1Moderation.NotifyOnlineAdmins($"Player reset queued for {id} by {admin}");
            CompanionPlugin.NotifySender(sender,
                peer == null
                    ? $"{id} is offline — the reset is queued and applies on their next join (the server cannot edit their character file)."
                    : $"{name} has no companion mod — the reset is queued and applies on their next join.");
        }

        // ---- client-side executor: wipe the local player's inventory ----
        // Runs on the TARGET player's client. Same trust model as CompanionPlugin.OnHealSelf. Unequips
        // first, exactly like CompanionPlugin.OnRemoveItem (CompanionPlugin.cs:768-773): an equipped item
        // removed from the bag otherwise stays ghost-equipped in hand until the player relogs.
        private static void OnPlayerStrip(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerPeer(sender)) return;

            string reason;
            try
            {
                if (pkg.ReadInt() != WireVer) return;
                reason = pkg.ReadString() ?? "";
            }
            catch (Exception) { return; }

            var stacks = 0;
            var items = 0;
            try
            {
                var inv = player.GetInventory();
                foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
                {
                    if (item == null) continue;
                    if (item.m_equipped) player.UnequipItem(item, false);
                    stacks++;
                    items += Mathf.Max(0, item.m_stack);
                    inv.RemoveItem(item, item.m_stack);
                }
            }
            catch (Exception) { /* partial wipe is still reported below */ }

            try
            {
                player.Message(MessageHud.MessageType.Center,
                    reason.Length > 0 ? $"An admin reset your inventory: {reason}" : "An admin reset your inventory");
            }
            catch (Exception) { }

            var rep = new ZPackage();
            rep.Write(WireVer);
            rep.Write(player.GetPlayerName() ?? "");
            rep.Write(stacks);
            rep.Write(items);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_StripRep", rep); }
            catch (Exception) { }
        }

        // Client confirmation (server side).
        private static void OnStripRep(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!PendingStrips.TryGetValue(sender, out var pending)) return;
            PendingStrips.Remove(sender);

            string name; int stacks, items;
            try
            {
                if (pkg.ReadInt() != WireVer) return;
                name = CleanText(pkg.ReadString(), MaxTextLen);
                stacks = pkg.ReadInt();
                items = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_StripRep: malformed packet dropped ({e.Message})"); return; }

            // A queued row is cleared only once a client CONFIRMS a wipe — never on hope. Any confirmed wipe
            // satisfies a pending one, so an immediate reset also clears a stale queued row for that id
            // (otherwise the player would be wiped a second time on their next join).
            ClearReset(pending.Id);

            CompanionPlugin.SrvAudit(pending.Requester, "RESET-CONFIRM",
                $"id={pending.Id} name={name} stacks={stacks} items={items} queued={pending.FromQueue}");
            CompanionPlugin.FeatureLog($"Player reset CONFIRMED for {name} ({pending.Id}): {stacks} stack(s), {items} item(s)");
            Wave1AuditRpc.PostModLog($"RESET CONFIRMED {name} ({pending.Id}): {stacks} stack(s), {items} item(s)");
            if (pending.Requester != 0L)
                CompanionPlugin.NotifySender(pending.Requester, $"Reset applied to {name}: {stacks} stack(s), {items} item(s) removed.");
            else
                Wave1Moderation.NotifyOnlineAdmins($"Queued reset applied to {name}: {stacks} stack(s), {items} item(s) removed.");
        }

        // No confirmation in time => the client is not running the companion (or dropped the packet).
        // Fall back to the queue so the reset is not silently lost.
        private static void ExpireStrips(float now)
        {
            List<long> dead = null;
            foreach (var kv in PendingStrips)
                if (now >= kv.Value.Expiry) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;

            foreach (var uid in dead)
            {
                var p = PendingStrips[uid];
                PendingStrips.Remove(uid);
                if (p.FromQueue)
                {
                    // Already in "resetq" — leave it there and try again on their next join.
                    CompanionPlugin.SrvAudit(0L, "RESET-UNCONFIRMED", $"id={p.Id} uid={uid} source=queue stillQueued=true");
                    CompanionPlugin.FeatureLog($"Queued reset for {p.Id} was not confirmed; it stays queued for the next join");
                    continue;
                }
                var queued = QueueReset(p.Id, p.Reason);
                CompanionPlugin.SrvAudit(p.Requester, "RESET-UNCONFIRMED", $"id={p.Id} uid={uid} queued={queued}");
                CompanionPlugin.FeatureLog($"Player reset for {p.Id} was not confirmed by the client; queued={queued}");
                CompanionPlugin.NotifySender(p.Requester, queued
                    ? $"{p.Id} did not confirm the reset (no companion mod?) — it is queued for their next join."
                    : $"{p.Id} did not confirm the reset and the queue could not be written.");
            }
        }

        // ==================== queues ====================

        /// <summary>
        /// Queue a teleport that applies on the player's next join (offline rescue). The row goes into the
        /// VAULT's queue — Wave4Vault.EnqueueOffline canonicalises the id, owns the table name and delivers
        /// the row with "RPC_TeleportPlayer" (TIER-VANILLA: it works on an unmodded client). Detail shape is
        /// the vault's ParseTp contract: "x|y|z", invariant culture.
        /// Returns false when the store is not ready or the vault refused the row (queue full).
        /// </summary>
        internal static bool QueueOfflineTp(string id, Vector3 pos)
        {
            if (!Sane(pos) || string.IsNullOrEmpty(CleanId(id)) || !FeatureStore.Ready) return false;
            try { return Wave4Vault.EnqueueOffline(CleanId(id), "tp", FmtRaw(pos)); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Offline tp queue write failed for {id}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Queue a FULL inventory wipe for the player's next join. Deliberately NOT the vault's queue: its
        /// "strip" kind is a single-prefab removal and cannot express "everything" (see the header). One
        /// pending reset per player — a second request just refreshes the reason and the timestamp.
        /// </summary>
        internal static bool QueueReset(string id, string reason)
        {
            var clean = CleanId(id);
            if (clean.Length == 0 || !FeatureStore.Ready) return false;
            try
            {
                var t = FeatureStore.Table(TblResetQ);
                var key = FindKey(t, clean) ?? clean;
                if (!t.ContainsKey(key) && t.Count >= ResetQueueCap)
                {
                    CompanionPlugin.FeatureLog($"Reset queue is full ({ResetQueueCap}); refusing to queue {clean}");
                    return false;
                }
                t[key] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + CleanText(reason ?? "", MaxTextLen);
                FeatureStore.SaveTable(TblResetQ);
                return true;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Reset queue write failed for {id}: {e.Message}");
                return false;
            }
        }

        private static void ClearReset(string id)
        {
            try
            {
                var t = FeatureStore.Table(TblResetQ);
                var key = FindKey(t, CleanId(id));
                if (key == null) return;
                t.Remove(key);
                FeatureStore.SaveTable(TblResetQ);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Reset queue clear failed for {id}: {e.Message}"); }
        }

        // ---- delivery of a queued reset on join ----
        //
        // POSTFIX on RPC_PeerInfo: by here m_uid / m_playerName are filled (the same reason wave 1's
        // JoinWatchPatch is a postfix). Nothing is sent from inside the handler — a just-joined client has no
        // player object yet, so the strip is DEFERRED and driven from Tick, exactly like wave 2's MOTD.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave4ResetJoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                try
                {
                    if (__instance == null || !__instance.IsServer() || rpc == null) return;
                    ZNetPeer peer = null;
                    foreach (var p in __instance.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null || peer.m_socket == null || string.IsNullOrEmpty(peer.m_playerName)) return;
                    var host = peer.m_socket.GetHostName();
                    if (string.IsNullOrEmpty(host)) return;

                    var t = FeatureStore.Table(TblResetQ);
                    var key = FindKey(t, host);
                    if (key == null) return;

                    var reason = "";
                    var raw = t[key];
                    var bar = raw != null ? raw.IndexOf('|') : -1;
                    if (bar >= 0) reason = raw.Substring(bar + 1);
                    ResetDeliveries[peer.m_uid] = new PendingStrip
                    {
                        Requester = 0L,                     // system delivery, no admin is waiting
                        Id = key,
                        Reason = reason,
                        Expiry = Time.unscaledTime + ResetDeliveryDelay,
                        FromQueue = true,
                    };
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Queued-reset join hook failed: {e.Message}"); }
            }
        }

        private static void DeliverQueuedResets(float now)
        {
            List<long> due = null;
            foreach (var kv in ResetDeliveries)
                if (now >= kv.Value.Expiry) (due ?? (due = new List<long>())).Add(kv.Key);
            if (due == null) return;

            foreach (var uid in due)
            {
                var p = ResetDeliveries[uid];
                ResetDeliveries.Remove(uid);
                var peer = FindPeerByUid(uid);
                if (peer == null) continue;                       // left again before delivery: stays queued

                var req = new ZPackage();
                req.Write(WireVer);
                req.Write(p.Reason ?? "");
                try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_PlayerStrip", req); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Queued reset delivery to {peer.m_playerName} failed: {e.Message}"); continue; }

                p.Expiry = now + StripConfirmSeconds;
                PendingStrips[uid] = p;
                CompanionPlugin.FeatureLog($"Queued reset delivered to {peer.m_playerName} ({p.Id}); waiting for confirmation");
            }
        }

        // ==================== shared helpers ====================

        /// <summary>
        /// Per-peer capability, owned by Wave34Core's AP_CapProbe / AP_CapReply handshake:
        /// true = companion confirmed, false = known vanilla, null = not answered yet. Every consumer must
        /// degrade on null/false and TELL THE ADMIN WHY — see OnItemAuditReq and OnPlayerReset.
        /// </summary>
        private static bool? HasMod(long uid)
        {
            try { return Wave34Core.HasMod(uid); }
            catch (Exception) { return null; }   // core absent/renamed: treat every peer as "unknown"
        }

        private static string Cap(bool? v) => v == null ? "unknown" : (v.Value ? "yes" : "no");

        /// <summary>
        /// Client-side trust gate, identical in effect to CompanionPlugin.SenderIsServer
        /// (CompanionPlugin.cs:153-158) which is private and lives in a file this wave may not edit.
        /// The host branch is gated on the same SenderSanitizerActive flag, read reflectively: without the
        /// routed-RPC sender sanitizer a remote client could forge sender == our own session id and trigger
        /// a client executor (here: an inventory wipe). If the flag cannot be read we FAIL CLOSED — the
        /// executors stop working on a listen-server host, which is a visible degradation, not a hole.
        /// </summary>
        private static bool SenderIsServerPeer(long sender)
        {
            if (ZNet.instance == null) return false;
            try
            {
                var sp = ZNet.instance.GetServerPeer();
                if (sp != null && sp.m_uid != 0L && sender == sp.m_uid) return true;   // real client
            }
            catch (Exception) { }

            if (!ZNet.instance.IsServer() || ZDOMan.instance == null) return false;
            if (!SanitizerActive()) return false;
            return sender == ZDOMan.GetSessionID();                                     // listen-server host
        }

        private static bool SanitizerActive()
        {
            if (!_sanitizerFieldRead)
            {
                _sanitizerFieldRead = true;
                _sanitizerField = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                if (_sanitizerField == null)
                    CompanionPlugin.FeatureLog("Wave4: sender-sanitizer flag not found; host-side client executors disabled (fail closed).");
            }
            try { return _sanitizerField != null && (bool)_sanitizerField.GetValue(null); }
            catch (Exception) { return false; }
        }

        // Every admin reply in this file (AP_DeathLog / AP_Ledger / AP_ItemAudit) funnels through here, so the
        // listen-server host path is a one-line change: CompanionPlugin.ReplyTo hands the payload straight to
        // the panel in-process when the requester IS this process, and falls back to the routed send otherwise.
        private static void Reply(long uid, string rpc, ZPackage pkg)
        {
            try { CompanionPlugin.ReplyTo(uid, rpc, pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"{rpc} reply failed: {e.Message}"); }
        }

        // Table keys are platform ids, which reach us as either the full "Platform_id" or the bare form
        // (adminlist leniency, see CompanionPlugin.SenderIsAdmin) — both spellings must find the same row.
        private static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (Wave1Moderation.IdMatches(kv.Key, id)) return kv.Key;
            return null;
        }

        private static ZNetPeer FindPeerByUid(long uid)
        {
            if (ZNet.instance == null) return null;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.m_uid == uid) return peer;
            return null;
        }

        private static ZNetPeer FindPeerById(string id)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(id)) return null;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_socket == null) continue;
                if (Wave1Moderation.IdMatches(id, peer.m_socket.GetHostName())) return peer;
            }
            return null;
        }

        // Reject NaN/Infinity and anything far outside the world: a teleport to a broken coordinate can
        // wedge a client in an endless load.
        private static bool Sane(Vector3 p) =>
            !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z) &&
            !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z) &&
            Mathf.Abs(p.x) <= WorldEdge && Mathf.Abs(p.z) <= WorldEdge && Mathf.Abs(p.y) <= WorldEdge;

        // Ids are stored AS ENTERED (trimmed): the game's own checks compare the full networkUserId, so
        // stripping a platform prefix silently no-ops crossplay ids (same rule as wave 1).
        private static string CleanId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxIdLen) s = s.Substring(0, MaxIdLen);
            return s.IndexOf(' ') >= 0 ? "" : s;
        }

        // '|' is the field separator inside stored values, so it can never survive in free text.
        private static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // A prefab name is an identifier: no spaces, no separators, bounded length.
        private static string CleanToken(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > max) s = s.Substring(0, max);
            return s.IndexOf(' ') >= 0 || s.IndexOf('|') >= 0 ? "" : s;
        }

        // Stored form for a queued "tp" row — the vault's ParseTp contract: "x|y|z", invariant culture.
        // Centimetre precision is plenty for a teleport and keeps the stored file readable.
        private static string FmtRaw(Vector3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###}|{1:0.###}|{2:0.###}", p.x, p.y, p.z);

        // Human form for audit lines and admin toasts.
        private static string Fmt(Vector3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0},{2:0}", p.x, p.y, p.z);
    }
}

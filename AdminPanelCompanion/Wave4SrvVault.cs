using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 4 — character vault, inventory backup/restore, offline queue ====================
    //
    // CAPABILITY TIERING (read this before touching anything here — it is the whole design constraint):
    //
    //   Valheim keeps CHARACTER DATA (inventory, skills, food, equipment) CLIENT-SIDE, in the player's .fch
    //   profile on their own machine. A dedicated server never holds it and never sees it. Everything in this
    //   file that touches an inventory is therefore TIER-MODDED: it works only when the TARGET's client is
    //   running AdminPanelCompanion.dll, exactly like the existing inventory viewer
    //   (AP_SrvReqInv -> AP_InvRequest -> AP_InvData, CompanionPlugin.cs:359-363 / 883-900). Admins have the
    //   mod; ordinary players usually do not. Every path below asks Wave34Core.HasMod(uid) FIRST and, when the
    //   answer is false or not-yet-known, refuses with a NotifySender that says WHY instead of failing silently.
    //
    //   Sub-feature tiers, stated once and repeated in the limitations list:
    //     * vault snapshot / restore / auto-death-snapshot ....... TIER-MODDED (target needs the DLL)
    //     * offline queue "give" / "strip" / "kit" ............... TIER-MODDED (applied on the next join)
    //     * offline queue "tp" .................................. TIER-VANILLA (RPC_TeleportPlayer, works on
    //                                                             an unmodded client, so it is delivered even
    //                                                             when the rest of that player's queue is held)
    //     * editing an OFFLINE player's character ............... TIER-IMPOSSIBLE. The .fch lives on their
    //                                                             machine. This module never pretends otherwise:
    //                                                             offline intent is a QUEUE that applies on the
    //                                                             next join, never a file edit.
    //
    // DEATH SNAPSHOTS — the honest version (verified, do not "improve" without re-verifying):
    //   Player.OnDeath (Player.decompiled.cs:3040-3142) sets ZDOVars.s_dead FIRST (line 3048) and calls
    //   CreateTombStone() LATER in the same method (line 3116); CreateTombStone moves the whole inventory into
    //   the grave container (line 3023, MoveInventoryToGrave), which EMPTIES m_inventory. Both happen inside a
    //   single frame ON THE DYING CLIENT. The server only learns about the death when the s_dead ZDO flag
    //   replicates — and Wave34Core.OnDeath deliberately waits a further ~3 s for the tombstone to appear
    //   before it fires — after which our snapshot request still has to make a round trip back to that client.
    //   The grave took the items long before any of that. There is no earlier server-side signal and no way to
    //   snapshot BEFORE the death, so a "pre-death inventory" is not implementable and is deliberately NOT
    //   shipped. What a death snapshot actually records is the POST-death inventory: whatever the death rules
    //   left on the body (nothing at all in default settings; the equipped set when the DeathKeepEquip global
    //   key is on). That is still worth recording next to the tombstone, which is why the feature exists — but
    //   it is OFF by default (EnableDeathSnapshots) and the panel/limitations say plainly what it captures.
    //
    // Design rules obeyed (spec-companion.md §1-§9, spec-valheim-api.md §4/§11/§13):
    //   * Everything durable lives in FeatureStore tables; ticks are DateTime.UtcNow.Ticks on the wire and in
    //     the store. Table names are built from a SANITIZED id (they become file names — see SafeIdKey).
    //   * Every admin RPC is gated by CompanionPlugin.SenderCanFeature and audited with rich detail.
    //   * Every client executor checks a SenderIsServer-equivalent, exactly like OnHealSelf
    //     (CompanionPlugin.cs:910-916), and only runs when Player.m_localPlayer exists.
    //   * Every server-side receiver of a client-authored payload checks that the sender is the peer we
    //     actually asked, and re-validates the payload before storing or replaying it.
    //   * ONE Harmony class per target method, each applied in its own try/catch that names the degradation and
    //     reports through Wave2Ops.ReportPatch so the self-test surfaces it.
    //   * Behaviour-changing background work defaults OFF (death snapshots, starter kit). Offline delivery
    //     follows the Wave-2 MOTD precedent: an EMPTY queue is off, and every row in a non-empty queue was
    //     explicitly created by an admin.
    internal static class Wave4Vault
    {
        private const int Ver = 1;                     // wire + payload version — bump, never reorder

        // ---- store tables (shared contract with the panel + sibling wave files) ----
        private const string TblVaultPrefix = "vault_";     // snapTicks -> "label|source|itemCount|payloadBase64"
        private const string TblOfflinePrefix = "offline_"; // queuedTicks -> "kind|detail"
        private const string TblPresence = "presence";      // wave-1 owned, READ ONLY here (first-join detection)
        private const string TblMeta = "vaultmeta";         // ours: "kit_<id>" -> ticks (starter kit fired once)

        // ---- caps (server-side and mandatory: an over-long reply is discarded WHOLE by the panel) ----
        private const int VaultCap = 30;          // snapshots kept per player
        private const int VaultShipCap = 30;      // AP_VaultList shipped cap (wire contract)
        private const int DeathProtect = 5;       // newest N "death" snapshots are never pruned
        private const int OfflineStoreCap = 60;   // rows kept per player
        private const int OfflineShipCap = 30;    // AP_OfflineQueue shipped cap (wire contract)
        private const int MaxItems = 512;         // items per snapshot payload
        private const int MaxCustomData = 32;     // m_customData entries per item
        private const int MaxPayloadChars = 262144;
        private const int MaxIdLen = 64;
        private const int MaxLabelLen = 48;
        private const int MaxPrefabLen = 64;
        private const int MaxDetailLen = 200;
        private const int MaxKitEntries = 20;
        private const int MaxGiveCount = 10000;

        private const double OfflinePruneDays = 90d;
        private const float SnapTimeout = 30f;    // seconds a pending capture/restore token stays valid
        private const float DeliverDelay = 12f;   // seconds after join before the queue is applied
        private const float DeliverRetry = 8f;    // extra wait when the capability probe has not answered yet
        // The capability probe is only ANSWERED once the target's Player exists (Wave34SrvCore), and its own
        // schedule keeps probing to ~150 s after the join. Concluding "unmodded" before that misreports every
        // correctly-modded client with a slow world load and holds its queue for the whole session, so the
        // wait outlasts the probe schedule instead of counting a fixed number of retries.
        private const float CapWaitSeconds = 160f;
        private const int MaxGrantAttempts = 3;   // sends of one queued grant per server run (see GrantAttempts)

        // MessageHud.MessageType.Center == 2 (MessageHud.cs:8-12) — written as an int so a renamed/reordered
        // enum in a future build cannot throw a type-load error inside an RPC handler.
        private const int MsgCenter = 2;

        // ---- config ----
        private static ConfigEntry<bool> _enableDeathSnapshots;
        private static ConfigEntry<bool> _enableOfflineDelivery;
        private static ConfigEntry<bool> _enableStarterKit;
        private static ConfigEntry<string> _starterKitItems;

        private static bool DeathSnapshotsOn => _enableDeathSnapshots != null && _enableDeathSnapshots.Value;
        private static bool OfflineDeliveryOn => _enableOfflineDelivery == null || _enableOfflineDelivery.Value;
        private static bool StarterKitOn => _enableStarterKit != null && _enableStarterKit.Value;
        private static string StarterKitRaw => _starterKitItems != null ? (_starterKitItems.Value ?? "") : "";

        private static bool _inited;

        // ---- in-memory state (session-scoped by nature; never persisted) ----

        // A capture/restore in flight. The token is the ONLY thing that lets a client-authored reply match a
        // request we made, and the target uid is re-checked on arrival so peer A cannot answer peer B's probe.
        private sealed class Pending
        {
            public long TargetUid;
            public string Id;
            public string Name;
            public string Label;
            public string Source;
            public long AdminUid;
            public long SnapTicks;   // restores only: which snapshot was sent
            public float Expires;
        }

        private static readonly Dictionary<long, Pending> PendingCaptures = new Dictionary<long, Pending>();
        private static readonly Dictionary<long, Pending> PendingRestores = new Dictionary<long, Pending>();
        private static long _tokenSeq;

        // Offline-queue delivery, deferred like the MOTD: a just-joined client has no HUD (and, more to the
        // point, no answered capability probe) for the first few seconds of a join.
        private sealed class Delivery
        {
            public long Uid;
            public float Due;
            public int Attempts;
            public float Deadline;   // Time.unscaledTime after which an unknown capability stops being waited for
            public bool Reprobed;    // one last probe was sent before concluding anything
        }

        private static readonly List<Delivery> DeliveryQueue = new List<Delivery>();

        // A queued grant that has been SENT but not yet confirmed. The store row is still in the queue: it is
        // removed only when the target's client reports what it actually added, because "the RPC did not
        // throw" says nothing about whether that prefab exists on that client.
        private sealed class QueuedGrant
        {
            public long Uid;
            public string Id;
            public string Name;
            public string Table;
            public string Key;
            public string Kind;
            public string Detail;
            public float Expires;
        }

        private static readonly Dictionary<long, QueuedGrant> PendingGrants = new Dictionary<long, QueuedGrant>();

        // Send attempts per queued row, for this server run only. A row naming a prefab the target's client
        // does not have can never succeed; without a ceiling it would be re-sent on every join forever.
        private static readonly Dictionary<string, int> GrantAttempts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Players we already told the admins about ("returned, queue held, no mod"). One alert per session.
        private static readonly HashSet<long> UnmoddedAlerted = new HashSet<long>();

        private static float _nextFastTick;
        private static float _nextPruneTick;

        // Cached reflection (resolved once, all optional — a game update must degrade, never crash).
        private static bool _reflectionResolved;
        private static FieldInfo _fWorldLevel;      // ItemDrop.ItemData.m_worldLevel
        private static FieldInfo _fCustomData;      // ItemDrop.ItemData.m_customData
        private static MethodInfo _mEquipItem;      // Humanoid.EquipItem(ItemData, bool)
        private static FieldInfo _fSanitizerActive; // CompanionPlugin.SenderSanitizerActive
        private static bool _sanitizerFieldTried;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableDeathSnapshots = cfg.Bind("Features", "EnableDeathSnapshots", false,
                    "Automatically vault a player's inventory when they die. OFF by default and honestly labelled: the game moves the inventory into the tombstone inside Player.OnDeath, before the server can even see the death, so this records the POST-death inventory (empty under default rules, the equipped set when DeathKeepEquip is on) — useful next to the tombstone, useless as a pre-death backup. Requires the target to have AdminPanelCompanion.dll.");
                _enableOfflineDelivery = cfg.Bind("Features", "EnableOfflineDelivery", true,
                    "Apply queued offline actions when a player next joins. An EMPTY queue is off, and every queued row was explicitly created by an admin, so this changes nothing on a server that never queues anything. Set to false to freeze all queues without deleting them.");
                _enableStarterKit = cfg.Bind("Features", "EnableStarterKit", false,
                    "Give a starter kit on a player's first-ever join. The kit is QUEUED and delivered through the same offline path, so it needs AdminPanelCompanion.dll on that player's client; without it the kit stays queued and admins are told once.");
                _starterKitItems = cfg.Bind("Features", "StarterKitItems", "",
                    "Starter kit contents, \"prefab:count:quality\" separated by commas, e.g. \"Wood:20:1,Stone:20:1,Flint:5:1\". Empty = no kit even when EnableStarterKit is on. Unknown prefabs are skipped by the receiving client.");
            }

            // Audit-chokepoint registration. Deliberate split (wave-1 lesson: an audited RPC writes a line on
            // EVERY call, so a polled read behind it drowns audit.log):
            //   * every MUTATING vault/offline RPC is audited and owner-only (null grant) when tiered roles are
            //     enforced. Restoring a stored inventory is the single largest item-duplication vector this mod
            //     exposes — it is deliberately NOT delegated to the "builder" or "moderator" tiers.
            //   * AP_SrvVaultListReq / AP_SrvOfflineQueueReq are panel-refreshed reads: not registered, so they
            //     produce no audit spam and fall back to owner-only under role enforcement.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvVaultSnapReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvVaultRestoreReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvVaultDeleteReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvOfflineAdd", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvOfflineClear", null);

            ApplyPatch("Wave4VaultRpcRegistration", typeof(Wave4VaultRpcRegistration),
                "vault / offline-queue RPCs unavailable");
            ApplyPatch("Wave4JoinPatch", typeof(Wave4JoinPatch),
                "offline-queue delivery and first-join starter kit unavailable");

            TrySubscribeDeath();
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

            if (now >= _nextFastTick)
            {
                _nextFastTick = now + 1f;
                try { DeliverQueued(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Offline delivery tick failed: {e.Message}"); }
                try { ExpirePending(now); }
                catch (Exception) { }
            }

            if (now >= _nextPruneTick)
            {
                _nextPruneTick = now + 600f;
                try { PruneOfflineTables(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Offline-queue prune failed: {e.Message}"); }
            }
        }

        // ==================== RPC registration ====================

        // Registration is global (ZNet.Awake fires on servers AND clients), which is what makes one file able to
        // own both ends of a flow: the server-side handlers below no-op on a client (IsServer gate) and the
        // client-side executors no-op on a dedicated server (Player.m_localPlayer is null there).
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave4VaultRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // ---- server-side entry points (admin -> server) ----
                    ZRoutedRpc.instance.Register<string>("AP_SrvVaultListReq", OnVaultListReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvVaultSnapReq", OnVaultSnapReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvVaultRestoreReq", OnVaultRestoreReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvVaultDeleteReq", OnVaultDeleteReq);
                    ZRoutedRpc.instance.Register<string>("AP_SrvOfflineQueueReq", OnOfflineQueueReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvOfflineAdd", OnOfflineAdd);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvOfflineClear", OnOfflineClear);

                    // ---- server-side receivers (target client -> server) ----
                    ZRoutedRpc.instance.Register<ZPackage>("AP_VaultData", OnVaultData);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_VaultAck", OnVaultAck);

                    // ---- client-side executors (only accepted when sent by the server) ----
                    ZRoutedRpc.instance.Register<ZPackage>("AP_VaultCapture", OnVaultCapture);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_VaultApply", OnVaultApply);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Vault RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== capability gate ====================

        /// <summary>
        /// true = the peer answered the AP_CapProbe and has AdminPanelCompanion.dll; false = answered "no" or is
        /// known-unmodded; null = not answered yet (or the probe module is unavailable). Wrapped so a throw in a
        /// sibling module degrades this feature instead of killing an RPC handler.
        /// </summary>
        private static bool? HasMod(long uid)
        {
            try { return Wave34Core.HasMod(uid); }
            catch (Exception) { return null; }
        }

        // One place that turns the tri-state into an admin-readable refusal, so every path explains itself the
        // same way and in the SAME words as every other modded-client feature (Wave34Core.CapReason owns the
        // sentence). Returns true when the action may proceed. On "unknown" a fresh probe is kicked off so the
        // admin's retry a few seconds later has a real answer instead of the same shrug.
        private static bool RequireMod(long targetUid, string targetName, long adminUid, string what)
        {
            var cap = HasMod(targetUid);
            if (cap == true) return true;

            string reason;
            try { reason = Wave34Core.CapReason(targetUid); }
            catch (Exception) { reason = "that player's client capability could not be determined"; }
            if (cap == null)
            {
                try { Wave34Core.ProbePeer(targetUid); }
                catch (Exception) { }
            }

            var who = string.IsNullOrEmpty(targetName) ? targetUid.ToString() : targetName;
            var why = $"Cannot {what} for {who}: {reason}. Inventories live in the player's own character file on their machine — the server never holds them.";
            if (adminUid != 0L) CompanionPlugin.NotifySender(adminUid, why);
            else CompanionPlugin.FeatureLog($"Vault: skipped {what} for {who} — {reason}");
            return false;
        }

        // ==================== 1. snapshot capture ====================

        // ZPackage: long targetUid, string label.
        private static void OnVaultSnapReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvVaultSnapReq")) return;
            long targetUid; string label;
            try { targetUid = pkg.ReadLong(); label = CleanLabel(pkg.ReadString()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvVaultSnapReq: malformed packet dropped ({e.Message})"); return; }

            RequestSnapshot(targetUid, label, "manual", sender);
        }

        /// <summary>
        /// TIER-MODDED. Ask the TARGET's client for its inventory using the same mechanism as the existing
        /// inventory viewer (server -> client executor, client serializes, client replies to the server) and
        /// store the answer in the vault. adminUid 0 = an automatic capture (death snapshot): failures are
        /// logged instead of being pushed at a person.
        /// </summary>
        internal static void RequestSnapshot(long targetUid, string label, string source, long adminUid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var peer = FindPeerByUid(targetUid);
            if (peer == null)
            {
                if (adminUid != 0L)
                    CompanionPlugin.NotifySender(adminUid,
                        "Snapshot: that player is not connected. A character can only be read from a live client — an offline player's inventory is on their own machine, not on the server.");
                return;
            }
            var name = peer.m_playerName ?? "?";
            if (!RequireMod(targetUid, name, adminUid, "read that inventory")) return;

            var token = NextToken();
            PendingCaptures[token] = new Pending
            {
                TargetUid = targetUid,
                Id = CanonicalId(PeerHost(peer)),
                Name = name,
                Label = CleanLabel(label),
                Source = CleanSource(source),
                AdminUid = adminUid,
                Expires = Time.unscaledTime + SnapTimeout,
            };

            var req = new ZPackage();
            req.Write(Ver);
            req.Write(token);
            req.Write(CleanLabel(label));
            req.Write(CleanSource(source));
            try { ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_VaultCapture", req); }
            catch (Exception e)
            {
                PendingCaptures.Remove(token);
                CompanionPlugin.FeatureLog($"Vault capture request to {name} failed: {e.Message}");
                if (adminUid != 0L) CompanionPlugin.NotifySender(adminUid, $"Snapshot request to {name} could not be sent.");
            }
        }

        // ---- client side: serialize this client's inventory and hand it to the server ----
        // Same trust model as OnHealSelf (CompanionPlugin.cs:910-916): only the server may ask, and only when a
        // local player exists. A dedicated server never reaches the body (m_localPlayer is null there).
        private static void OnVaultCapture(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerLike(sender)) return;
            int ver; long token; string label, source;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                label = pkg.ReadString();
                source = pkg.ReadString();
            }
            catch (Exception) { return; }
            if (ver != Ver) return;   // unknown version: discard the WHOLE request

            string payload; int count;
            try { payload = SerializeInventory(player, out count); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Vault capture failed locally: {e.Message}"); return; }
            if (payload == null || payload.Length > MaxPayloadChars) return;

            var reply = new ZPackage();
            reply.Write(Ver);
            reply.Write(token);
            reply.Write(player.GetPlayerName() ?? "");
            reply.Write(count);
            reply.Write(payload);
            reply.Write(label ?? "");
            reply.Write(source ?? "");
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_VaultData", reply); }
            catch (Exception) { }
        }

        // ---- server side: receive, verify, store ----
        private static void OnVaultData(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int ver, count; long token; string name, payload;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                name = pkg.ReadString();
                count = pkg.ReadInt();
                payload = pkg.ReadString();
            }
            catch (Exception) { return; }
            if (ver != Ver) return;

            Pending p;
            if (!PendingCaptures.TryGetValue(token, out p)) return;   // unsolicited / expired: silently dropped
            // Only the peer we actually asked may answer that token. Without this check any client could inject
            // an arbitrary "snapshot" into another player's vault and then have it restored onto them.
            if (p.TargetUid != sender)
            {
                CompanionPlugin.FeatureLog($"AP_VaultData: peer {sender} answered a token issued for {p.TargetUid} — dropped");
                return;
            }
            PendingCaptures.Remove(token);

            if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadChars || count < 0 || count > MaxItems)
            {
                CompanionPlugin.FeatureLog($"AP_VaultData from {p.Name}: implausible payload dropped (count={count})");
                if (p.AdminUid != 0L) CompanionPlugin.NotifySender(p.AdminUid, $"Snapshot from {p.Name} was rejected (implausible payload).");
                return;
            }
            int parsed;
            if (!TryParsePayload(payload, count, out parsed))
            {
                CompanionPlugin.FeatureLog($"AP_VaultData from {p.Name}: payload failed validation (claimed {count}) — dropped");
                if (p.AdminUid != 0L) CompanionPlugin.NotifySender(p.AdminUid, $"Snapshot from {p.Name} was rejected (payload did not parse).");
                return;
            }

            // The identity comes from OUR pending record (resolved from the peer socket at request time), never
            // from the client's reply: a client must not be able to choose which player's vault it writes into.
            // The label/source it echoes back are ignored for the same reason.
            var id = p.Id;
            var table = VaultTable(id);
            if (table == null)
            {
                CompanionPlugin.FeatureLog($"AP_VaultData from {p.Name}: no usable platform id for that peer — snapshot dropped");
                if (p.AdminUid != 0L) CompanionPlugin.NotifySender(p.AdminUid, "Snapshot could not be stored (that peer has no resolvable platform id).");
                return;
            }
            var t = FeatureStore.Table(table);
            var ticks = UniqueKey(t, DateTime.UtcNow.Ticks);
            var label = p.Label.Length > 0 ? p.Label : DefaultLabel(p.Source);
            t[ticks.ToString()] = $"{label}|{p.Source}|{parsed}|{payload}";
            PruneVault(t);
            FeatureStore.SaveTable(table);

            CompanionPlugin.SrvAudit(p.AdminUid, "VAULT-SNAPSHOT",
                $"id={id} name={p.Name} clientReportedName={name} label={label} source={p.Source} items={parsed} snapTicks={ticks}");
            CompanionPlugin.FeatureLog($"Vault: stored {p.Source} snapshot of {p.Name} ({parsed} items)");
            if (p.Source == "death") Wave1AuditRpc.PostModLog($"VAULT death-snapshot {p.Name}: {parsed} item(s) left after death");
            else Wave1AuditRpc.PostModLog($"VAULT snapshot {p.Name}: {parsed} item(s) ({label})");
            if (p.AdminUid != 0L)
                CompanionPlugin.NotifySender(p.AdminUid, $"Vaulted {p.Name}: {parsed} item(s) as \"{label}\"");
        }

        // 30 per player. A "death" snapshot among the NEWEST 5 deaths is never pruned — those are the ones an
        // admin actually needs when a player reports lost loot. The protected set can never exceed 5, so this
        // always has something to drop while the table is over cap.
        private static void PruneVault(Dictionary<string, string> t)
        {
            if (t.Count <= VaultCap) return;
            var keys = SortedKeysDesc(t);

            var protectedKeys = new HashSet<string>(StringComparer.Ordinal);
            var deaths = 0;
            for (var i = 0; i < keys.Count && deaths < DeathProtect; i++)
            {
                string label, source, payload; int count;
                if (!ParseSnap(t[keys[i]], out label, out source, out count, out payload)) continue;
                if (source != "death") continue;
                protectedKeys.Add(keys[i]);
                deaths++;
            }

            for (var i = keys.Count - 1; i >= 0 && t.Count > VaultCap; i--)
            {
                if (protectedKeys.Contains(keys[i])) continue;
                t.Remove(keys[i]);
            }
        }

        // ==================== 2. auto-snapshot on death ====================

        /// <summary>
        /// Entry point for the death detector. Wired to Wave34Core.OnDeath in Init, and also callable directly
        /// (glue file, tests, a future death source). uid 0 or an empty id are both tolerated — whichever
        /// identity is known is used to resolve the peer.
        /// </summary>
        internal static void NoteDeath(long uid, string id, string name)
        {
            if (!_inited || !DeathSnapshotsOn) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                var peer = uid != 0L ? FindPeerByUid(uid) : FindPeerById(id);
                if (peer == null) return;   // died and left, or an offline/stale ZDO: nothing to ask
                RequestSnapshot(peer.m_uid,
                    "death " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"), "death", 0L);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Death snapshot failed: {e.Message}"); }
        }

        // Subscribed once from Init. Wave34Core fans its subscribers out individually inside try/catch, so a
        // throw here cannot break the other consumers of the event — but the subscription itself is still
        // guarded so a missing/renamed event degrades to "no automatic death snapshots" rather than aborting
        // the rest of Init.
        private static void TrySubscribeDeath()
        {
            try { Wave34Core.OnDeath += OnDeathObserved; }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Vault: could not subscribe to the death event ({e.Message}); death snapshots unavailable.");
            }
        }

        // DeathInfo carries no peer uid (it is derived from the character ZDO), so the peer is resolved from the
        // platform id — which is also why a player who disconnected on death is simply skipped.
        private static void OnDeathObserved(Wave34Core.DeathInfo d) => NoteDeath(0L, d.PlatformId, d.PlayerName);

        // ==================== 3. restore ====================

        // ZPackage: string id, long snapTicksUtc, bool wipeFirst.
        private static void OnVaultRestoreReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvVaultRestoreReq")) return;
            string rawId; long snapTicks; bool wipeFirst;
            try { rawId = pkg.ReadString(); snapTicks = pkg.ReadLong(); wipeFirst = pkg.ReadBool(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvVaultRestoreReq: malformed packet dropped ({e.Message})"); return; }

            var id = CanonicalId(rawId);
            var table = VaultTable(id);
            if (table == null) { CompanionPlugin.NotifySender(sender, "Restore: invalid player id."); return; }

            var peer = FindPeerById(id);
            if (peer == null)
            {
                CompanionPlugin.SrvAudit(sender, "VAULT-RESTORE", $"id={id} snapTicks={snapTicks} result=target-offline");
                CompanionPlugin.NotifySender(sender,
                    "Restore: that player is offline. A character file lives on the player's own machine, so the server cannot write it — queue the items instead (offline queue) and they will be delivered on their next join.");
                return;
            }
            var name = peer.m_playerName ?? "?";

            var t = FeatureStore.Table(table);
            string raw;
            if (!t.TryGetValue(snapTicks.ToString(), out raw))
            {
                CompanionPlugin.SrvAudit(sender, "VAULT-RESTORE", $"id={id} snapTicks={snapTicks} result=not-found");
                CompanionPlugin.NotifySender(sender, "Restore: that snapshot no longer exists.");
                return;
            }
            string label, source, payload; int storedCount;
            if (!ParseSnap(raw, out label, out source, out storedCount, out payload))
            {
                CompanionPlugin.SrvAudit(sender, "VAULT-RESTORE", $"id={id} snapTicks={snapTicks} result=unparsable-row");
                CompanionPlugin.NotifySender(sender, "Restore: that snapshot row is corrupt and was not sent.");
                return;
            }
            // Defence in depth: the panel has a confirm button, but the SERVER is the last line. A row whose
            // stored item count disagrees with what actually parses out of the payload is never replayed — that
            // mismatch is exactly what a tampered store file or a truncated write looks like.
            int parsed;
            if (!TryParsePayload(payload, storedCount, out parsed))
            {
                CompanionPlugin.SrvAudit(sender, "VAULT-RESTORE",
                    $"id={id} snapTicks={snapTicks} result=REFUSED-COUNT-MISMATCH stored={storedCount}");
                CompanionPlugin.FeatureLog($"Vault restore REFUSED for {name}: snapshot {snapTicks} claims {storedCount} items but does not parse to that.");
                Wave1AuditRpc.PostModLog($"VAULT RESTORE REFUSED {name}: snapshot {snapTicks} failed integrity check");
                CompanionPlugin.NotifySender(sender, "Restore refused: the stored snapshot failed its integrity check (item count does not match the payload).");
                return;
            }

            if (!RequireMod(peer.m_uid, name, sender, "write that inventory")) return;

            var token = NextToken();
            PendingRestores[token] = new Pending
            {
                TargetUid = peer.m_uid,
                Id = id,
                Name = name,
                Label = label,
                Source = source,
                AdminUid = sender,
                SnapTicks = snapTicks,
                Expires = Time.unscaledTime + SnapTimeout,
            };

            var apply = new ZPackage();
            apply.Write(Ver);
            apply.Write(token);
            apply.Write(wipeFirst);
            apply.Write(parsed);
            apply.Write(payload);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_VaultApply", apply); }
            catch (Exception e)
            {
                PendingRestores.Remove(token);
                CompanionPlugin.FeatureLog($"Vault restore send to {name} failed: {e.Message}");
                CompanionPlugin.NotifySender(sender, $"Restore could not be sent to {name}.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "VAULT-RESTORE",
                $"id={id} name={name} snapTicks={snapTicks} label={label} source={source} items={parsed} wipeFirst={wipeFirst} result=sent");
            CompanionPlugin.FeatureLog($"Vault: restoring {parsed} item(s) to {name} (snapshot {label}, wipeFirst={wipeFirst}) by {admin}");
            Wave1AuditRpc.PostModLog($"VAULT RESTORE {name}: {parsed} item(s) from \"{label}\"{(wipeFirst ? " (inventory wiped first)" : "")} (by {admin})");
            Wave1Moderation.NotifyOnlineAdmins($"Vault restore: {parsed} item(s) sent to {name} by {admin}");
            CompanionPlugin.NotifySender(sender, $"Restore sent to {name} ({parsed} item(s)) — waiting for confirmation.");
        }

        // ---- client side: apply a restore payload ----
        private static void OnVaultApply(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerLike(sender)) return;
            int ver, claimed; long token; bool wipeFirst; string payload;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                wipeFirst = pkg.ReadBool();
                claimed = pkg.ReadInt();
                payload = pkg.ReadString();
            }
            catch (Exception) { return; }
            if (ver != Ver) return;

            var applied = 0;
            var missing = 0;
            try
            {
                int parsed;
                if (!TryParsePayload(payload, claimed, out parsed)) return;   // never touch the bag on a bad payload
                if (wipeFirst) WipeInventory(player);
                ApplyPayload(player, payload, out applied, out missing);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Vault apply failed locally: {e.Message}"); }

            try
            {
                player.Message((MessageHud.MessageType)MsgCenter,
                    $"An admin restored {applied} item(s) to your inventory");
            }
            catch (Exception) { }

            var ack = new ZPackage();
            ack.Write(Ver);
            ack.Write(token);
            ack.Write(applied);
            ack.Write(missing);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_VaultAck", ack); }
            catch (Exception) { }
        }

        // ---- server side: the target confirms what it actually managed to apply ----
        private static void OnVaultAck(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int ver, applied, missing; long token;
            try { ver = pkg.ReadInt(); token = pkg.ReadLong(); applied = pkg.ReadInt(); missing = pkg.ReadInt(); }
            catch (Exception) { return; }
            if (ver != Ver) return;

            Pending p;
            if (!PendingRestores.TryGetValue(token, out p))
            {
                // Offline-queue grants ride the same executor and therefore the same ack.
                SettleGrant(token, sender, applied, missing);
                return;
            }
            if (p.TargetUid != sender) return;
            PendingRestores.Remove(token);

            CompanionPlugin.SrvAudit(p.AdminUid, "VAULT-RESTORE-ACK",
                $"id={p.Id} name={p.Name} snapTicks={p.SnapTicks} applied={applied} missingPrefabs={missing}");
            CompanionPlugin.FeatureLog($"Vault restore confirmed for {p.Name}: {applied} applied, {missing} unknown prefab(s)");
            if (p.AdminUid != 0L)
                CompanionPlugin.NotifySender(p.AdminUid,
                    missing > 0
                        ? $"{p.Name}: restored {applied} item(s); {missing} prefab(s) do not exist on their client and were skipped."
                        : $"{p.Name}: restored {applied} item(s).");
        }

        private static void ExpirePending(float now)
        {
            List<long> dead = null;
            foreach (var kv in PendingCaptures)
                if (now >= kv.Value.Expires) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead)
                {
                    var p = PendingCaptures[k];
                    PendingCaptures.Remove(k);
                    if (p.AdminUid != 0L)
                        CompanionPlugin.NotifySender(p.AdminUid,
                            $"Snapshot of {p.Name} timed out — their client never answered (mod missing, or they left).");
                }

            dead = null;
            foreach (var kv in PendingRestores)
                if (now >= kv.Value.Expires) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead)
                {
                    var p = PendingRestores[k];
                    PendingRestores.Remove(k);
                    if (p.AdminUid != 0L)
                        CompanionPlugin.NotifySender(p.AdminUid,
                            $"Restore to {p.Name} was never confirmed — it may have been applied, verify with a fresh inventory read.");
                }

            dead = null;
            foreach (var kv in PendingGrants)
                if (now >= kv.Value.Expires) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead)
            {
                var d = PendingGrants[k];
                PendingGrants.Remove(k);
                // No confirmation arrived (the player left, or their build does not answer). The row is left
                // in the queue on purpose — the next join tries again, up to MaxGrantAttempts.
                CompanionPlugin.SrvAudit(0L, "OFFLINE-DELIVER-TIMEOUT",
                    $"id={d.Id} name={d.Name} kind={d.Kind} detail={d.Detail} queuedTicks={d.Key} result=kept-in-queue");
                CompanionPlugin.FeatureLog($"Offline queue: {d.Name} never confirmed {d.Kind} {d.Detail} — the row stays queued.");
            }
        }

        // ==================== 4. list / delete ====================

        private static void OnVaultListReq(long sender, string rawId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvVaultListReq")) return;

            var id = CanonicalId(rawId);
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(id);

            var table = VaultTable(id);
            var rows = new List<KeyValuePair<long, string>>();
            if (table != null)
            {
                var t = FeatureStore.Table(table);
                var keys = SortedKeysDesc(t);
                for (var i = 0; i < keys.Count && rows.Count < VaultShipCap; i++)
                {
                    long ticks;
                    if (!long.TryParse(keys[i], out ticks)) continue;
                    rows.Add(new KeyValuePair<long, string>(ticks, t[keys[i]]));
                }
            }
            pkg.Write(rows.Count);
            foreach (var row in rows)
            {
                string label, source, payload; int count;
                if (!ParseSnap(row.Value, out label, out source, out count, out payload))
                { label = "?"; source = "manual"; count = 0; }
                pkg.Write(row.Key);
                pkg.Write(label);
                pkg.Write(count);
                pkg.Write(source);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_VaultList", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_VaultList reply failed: {e.Message}"); }
        }

        // ZPackage: string id, long snapTicksUtc.
        private static void OnVaultDeleteReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvVaultDeleteReq")) return;
            string rawId; long snapTicks;
            try { rawId = pkg.ReadString(); snapTicks = pkg.ReadLong(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvVaultDeleteReq: malformed packet dropped ({e.Message})"); return; }

            var id = CanonicalId(rawId);
            var table = VaultTable(id);
            if (table == null) { CompanionPlugin.NotifySender(sender, "Delete: invalid player id."); return; }
            var t = FeatureStore.Table(table);
            var key = snapTicks.ToString();
            string raw;
            if (!t.TryGetValue(key, out raw))
            {
                CompanionPlugin.SrvAudit(sender, "VAULT-DELETE", $"id={id} snapTicks={snapTicks} result=not-found");
                CompanionPlugin.NotifySender(sender, "Delete: that snapshot no longer exists.");
                return;
            }
            string label, source, payload; int count;
            ParseSnap(raw, out label, out source, out count, out payload);
            t.Remove(key);
            FeatureStore.SaveTable(table);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "VAULT-DELETE",
                $"id={id} snapTicks={snapTicks} label={label} source={source} items={count}");
            CompanionPlugin.FeatureLog($"Vault: {admin} deleted snapshot {snapTicks} of {id} ({label})");
            Wave1AuditRpc.PostModLog($"VAULT DELETE {id}: \"{label}\" ({count} item(s)) (by {admin})");
            CompanionPlugin.NotifySender(sender, $"Deleted snapshot \"{label}\".");
        }

        // ==================== 5. offline queue ====================

        // ZPackage: string id, string kind, string detail.
        private static void OnOfflineAdd(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvOfflineAdd")) return;
            string rawId, kind, detail;
            try { rawId = pkg.ReadString(); kind = pkg.ReadString(); detail = pkg.ReadString(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvOfflineAdd: malformed packet dropped ({e.Message})"); return; }

            var id = CanonicalId(rawId);
            string normalized;
            if (!NormalizeQueueRow(ref kind, detail, out normalized))
            {
                CompanionPlugin.NotifySender(sender, $"Queue: \"{kind}\" is not a valid action, or its parameters are malformed.");
                return;
            }
            long ticks;
            if (!EnqueueOffline(id, kind, normalized, out ticks))
            {
                CompanionPlugin.NotifySender(sender, $"Queue for {id} is full ({OfflineStoreCap} rows) — clear some first.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "OFFLINE-QUEUE-ADD", $"id={id} kind={kind} detail={normalized} queuedTicks={ticks}");
            CompanionPlugin.FeatureLog($"Offline queue: {admin} queued {kind} ({normalized}) for {id}");
            Wave1AuditRpc.PostModLog($"QUEUE {kind} for {id}: {normalized} (by {admin})");

            var peer = FindPeerById(id);
            var note = peer != null
                ? $"Queued {kind} for {id}. They are ONLINE — queued actions are applied on their NEXT join, not now."
                : $"Queued {kind} for {id}. It will be applied when they next join (an offline character file lives on their machine and cannot be edited server-side).";
            if (kind != "tp") note += " Requires AdminPanelCompanion.dll on their client.";
            CompanionPlugin.NotifySender(sender, note);
        }

        /// <summary>
        /// Add a row to a player's offline queue. Usable by sibling wave modules (rewards, shop deliveries) that
        /// need "give this to them next time they log in" semantics. Returns false only when the queue is full.
        /// </summary>
        internal static bool EnqueueOffline(string id, string kind, string detail)
        {
            long ticks;
            return EnqueueOffline(CanonicalId(id), kind, detail, out ticks);
        }

        private static bool EnqueueOffline(string id, string kind, string detail, out long ticks)
        {
            ticks = 0;
            var table = OfflineTable(id);
            if (table == null) return false;
            var t = FeatureStore.Table(table);
            if (t.Count >= OfflineStoreCap) return false;
            ticks = UniqueKey(t, DateTime.UtcNow.Ticks);
            t[ticks.ToString()] = kind + "|" + detail;
            FeatureStore.SaveTable(table);
            return true;
        }

        private static void OnOfflineQueueReq(long sender, string rawId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvOfflineQueueReq")) return;

            var id = CanonicalId(rawId);
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(id);

            var rows = new List<KeyValuePair<long, string>>();
            var table = OfflineTable(id);
            if (table != null)
            {
                var t = FeatureStore.Table(table);
                var keys = SortedKeysAsc(t);
                for (var i = 0; i < keys.Count && rows.Count < OfflineShipCap; i++)
                {
                    long ticks;
                    if (!long.TryParse(keys[i], out ticks)) continue;
                    rows.Add(new KeyValuePair<long, string>(ticks, t[keys[i]]));
                }
            }
            pkg.Write(rows.Count);
            foreach (var row in rows)
            {
                string kind, detail;
                ParseQueueRow(row.Value, out kind, out detail);
                pkg.Write(row.Key);
                pkg.Write(kind);
                pkg.Write(detail);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_OfflineQueue", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_OfflineQueue reply failed: {e.Message}"); }
        }

        // ZPackage: string id, long queuedTicksUtc (0 = clear the whole queue for that id).
        private static void OnOfflineClear(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvOfflineClear")) return;
            string rawId; long ticks;
            try { rawId = pkg.ReadString(); ticks = pkg.ReadLong(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvOfflineClear: malformed packet dropped ({e.Message})"); return; }

            var id = CanonicalId(rawId);
            var table = OfflineTable(id);
            if (table == null) { CompanionPlugin.NotifySender(sender, "Queue: invalid player id."); return; }
            var t = FeatureStore.Table(table);
            var admin = CompanionPlugin.SenderDisplayName(sender);

            if (ticks == 0L)
            {
                var n = t.Count;
                if (n == 0) { CompanionPlugin.NotifySender(sender, $"Queue for {id} is already empty."); return; }
                t.Clear();
                FeatureStore.SaveTable(table);
                CompanionPlugin.SrvAudit(sender, "OFFLINE-QUEUE-CLEAR", $"id={id} removed={n} scope=all");
                CompanionPlugin.FeatureLog($"Offline queue: {admin} cleared all {n} row(s) for {id}");
                Wave1AuditRpc.PostModLog($"QUEUE CLEAR {id}: {n} row(s) (by {admin})");
                CompanionPlugin.NotifySender(sender, $"Cleared {n} queued action(s) for {id}.");
                return;
            }

            var key = ticks.ToString();
            string raw;
            if (!t.TryGetValue(key, out raw))
            {
                CompanionPlugin.SrvAudit(sender, "OFFLINE-QUEUE-CLEAR", $"id={id} queuedTicks={ticks} result=not-found");
                CompanionPlugin.NotifySender(sender, "Queue: that row no longer exists.");
                return;
            }
            t.Remove(key);
            FeatureStore.SaveTable(table);
            CompanionPlugin.SrvAudit(sender, "OFFLINE-QUEUE-CLEAR", $"id={id} queuedTicks={ticks} row={raw}");
            CompanionPlugin.FeatureLog($"Offline queue: {admin} removed row {ticks} ({raw}) for {id}");
            Wave1AuditRpc.PostModLog($"QUEUE CLEAR {id}: {raw} (by {admin})");
            CompanionPlugin.NotifySender(sender, "Queued action removed.");
        }

        /// <summary>Rows currently queued for a player (0 when the store is unavailable).</summary>
        internal static int QueuedCount(string id)
        {
            var table = OfflineTable(CanonicalId(id));
            return table == null ? 0 : FeatureStore.Table(table).Count;
        }

        /// <summary>Snapshots currently vaulted for a player (0 when the store is unavailable).</summary>
        internal static int SnapshotCount(string id)
        {
            var table = VaultTable(CanonicalId(id));
            return table == null ? 0 : FeatureStore.Table(table).Count;
        }

        // ---- delivery on join ----

        // Deferred exactly like the MOTD (Wave2SrvOps): anything sent inside the RPC_PeerInfo postfix is dropped
        // on the floor, and the capability probe itself only answers ~10 s after the join.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave4JoinPatch
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
                    // A rejected connection never gets an identity — same filter the vanilla join log uses.
                    if (peer == null || peer.m_uid == 0L || peer.m_socket == null) return;
                    if (string.IsNullOrEmpty(peer.m_playerName)) return;

                    UnmoddedAlerted.Remove(peer.m_uid);
                    var id = CanonicalId(PeerHost(peer));
                    if (id.Length == 0) return;

                    MaybeQueueStarterKit(id, peer.m_playerName);

                    var table = OfflineTable(id);
                    if (table == null || FeatureStore.Table(table).Count == 0) return;   // empty queue = nothing to do
                    QueueDelivery(peer.m_uid);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Offline-queue join hook failed: {e.Message}"); }
            }
        }

        private static void QueueDelivery(long uid)
        {
            for (var i = 0; i < DeliveryQueue.Count; i++)
                if (DeliveryQueue[i].Uid == uid) return;   // already queued (a double RPC_PeerInfo is possible)
            var now = Time.unscaledTime;
            DeliveryQueue.Add(new Delivery
            {
                Uid = uid,
                Due = now + DeliverDelay,
                Attempts = 0,
                Deadline = now + CapWaitSeconds,
            });
        }

        private static void DeliverQueued(float now)
        {
            if (DeliveryQueue.Count == 0) return;
            for (var i = DeliveryQueue.Count - 1; i >= 0; i--)
            {
                var entry = DeliveryQueue[i];
                if (now < entry.Due) continue;

                var peer = FindPeerByUid(entry.Uid);
                if (peer == null) { DeliveryQueue.RemoveAt(i); continue; }   // left during the delay

                // Read the switch at DELIVERY time: an owner who turns delivery off during the window means it.
                if (!OfflineDeliveryOn) { DeliveryQueue.RemoveAt(i); continue; }

                var cap = HasMod(entry.Uid);
                if (cap == null && now < entry.Deadline)
                {
                    // The probe has not answered yet, and it still might: it cannot be answered before the
                    // client's Player exists, which on a heavy modlist is a minute or more into the join.
                    entry.Attempts++;
                    entry.Due = now + DeliverRetry;
                    continue;
                }
                if (cap == null && !entry.Reprobed)
                {
                    // Last word before anything is concluded: ask once more and wait one window for it.
                    entry.Reprobed = true;
                    entry.Due = now + DeliverRetry;
                    try { Wave34Core.ProbePeer(entry.Uid); }
                    catch (Exception) { }
                    continue;
                }
                DeliveryQueue.RemoveAt(i);
                try { ApplyQueue(peer, cap); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Offline delivery to {peer.m_playerName} failed: {e.Message}"); }
            }
        }

        // cap == true  -> every row is applied.
        // cap == false -> only "tp" is applied (TIER-VANILLA: RPC_TeleportPlayer is registered by the base
        //                 game on every client). The rest STAYS queued and the admins are told once.
        // cap == null  -> same holding behaviour, but the admins must NOT be told the client has no mod: the
        //                 capability simply never resolved this session (Wave34Core.CapReason says which).
        private static void ApplyQueue(ZNetPeer peer, bool? cap)
        {
            var modded = cap == true;
            var id = CanonicalId(PeerHost(peer));
            var table = OfflineTable(id);
            if (table == null) return;
            var t = FeatureStore.Table(table);
            if (t.Count == 0) return;

            var name = peer.m_playerName ?? "?";
            var keys = SortedKeysAsc(t);
            var applied = 0;
            var held = 0;
            var failed = 0;
            var sent = 0;
            var stalled = 0;
            var retired = 0;

            foreach (var key in keys)
            {
                string raw;
                if (!t.TryGetValue(key, out raw)) continue;
                string kind, detail;
                ParseQueueRow(raw, out kind, out detail);

                if (!modded && kind != "tp") { held++; continue; }

                // Item grants go out on the CONFIRMED route and the row stays queued until the client says
                // what it actually added. Deleting it on send destroyed the item whenever the prefab did not
                // resolve on that client, while every log line claimed a delivery.
                if (kind == "give" || kind == "kit")
                {
                    var attemptKey = table + "|" + key;
                    int tries;
                    GrantAttempts.TryGetValue(attemptKey, out tries);
                    if (tries >= MaxGrantAttempts) { stalled++; continue; }

                    GrantStart start;
                    try { start = BeginGrant(peer, id, name, table, key, kind, detail); }
                    catch (Exception e)
                    {
                        start = GrantStart.Failed;
                        CompanionPlugin.FeatureLog($"Offline row {kind} for {name} threw: {e.Message}");
                    }
                    if (start == GrantStart.Sent)
                    {
                        if (GrantAttempts.Count > 500) GrantAttempts.Clear();   // bounded: it is only a retry ceiling
                        GrantAttempts[attemptKey] = tries + 1;
                        sent++;
                        continue;
                    }
                    if (start == GrantStart.Failed) { failed++; continue; }

                    // Nothing to deliver at all (an empty kit spec): retire the row instead of re-sending it
                    // on every join forever. NOT counted as a delivery — nothing was handed over.
                    t.Remove(key);
                    retired++;
                    CompanionPlugin.SrvAudit(0L, "OFFLINE-DELIVER",
                        $"id={id} name={name} kind={kind} detail={detail} queuedTicks={key} result=nothing-to-deliver");
                    continue;
                }

                bool ok;
                try { ok = ApplyRow(peer, kind, detail); }
                catch (Exception e)
                {
                    ok = false;
                    CompanionPlugin.FeatureLog($"Offline row {kind} for {name} threw: {e.Message}");
                }
                if (!ok) { failed++; continue; }

                t.Remove(key);
                applied++;
                CompanionPlugin.SrvAudit(0L, "OFFLINE-DELIVER", $"id={id} name={name} kind={kind} detail={detail} queuedTicks={key}");
                Wave1AuditRpc.PostModLog($"QUEUE DELIVERED to {name}: {kind} {detail}");
            }

            if (applied > 0 || retired > 0) FeatureStore.SaveTable(table);
            if (applied > 0)
            {
                CompanionPlugin.FeatureLog($"Offline queue: delivered {applied} action(s) to {name}");
                Wave1Moderation.NotifyOnlineAdmins($"Offline queue: delivered {applied} queued action(s) to {name}");
                try { Wave1Moderation.SendPlayerText(peer.m_uid, "An admin left something for you — check your inventory."); }
                catch (Exception) { }
            }
            // Deliberately NOT reported as delivered: these are in flight and only SettleGrant (or the
            // timeout in ExpirePending) may say what became of them.
            if (sent > 0)
                CompanionPlugin.FeatureLog($"Offline queue: sent {sent} item grant(s) to {name}, waiting for their client to confirm");
            if (failed > 0)
                CompanionPlugin.FeatureLog($"Offline queue: {failed} row(s) for {name} could not be applied and stay queued");
            if (stalled > 0)
            {
                var text = $"Offline queue: {stalled} row(s) for {name} have failed {MaxGrantAttempts} delivery attempts and are no longer retried this session - the prefab probably does not exist on their client. The rows are KEPT.";
                CompanionPlugin.FeatureLog(text);
                try { Wave1Moderation.NotifyOnlineAdmins(text); } catch (Exception) { }
            }
            if (held > 0 && UnmoddedAlerted.Add(peer.m_uid))
            {
                var why = cap == false
                    ? "their client is not running AdminPanelCompanion.dll"
                    : "their client capability could not be determined this session (" + CapReasonOf(peer.m_uid) + ")";
                var text = $"{name} returned with {held} queued action(s) still pending: {why}, so items and inventory writes cannot be delivered. The queue is KEPT, nothing was lost.";
                CompanionPlugin.FeatureLog(text);
                Wave1Moderation.NotifyOnlineAdmins(text);
                Wave1AuditRpc.PostModLog($"QUEUE HELD for {name}: {held} row(s), {why}");
            }
        }

        private static string CapReasonOf(long uid)
        {
            try { return Wave34Core.CapReason(uid); }
            catch (Exception) { return "capability unknown"; }
        }

        // Result of starting a confirmed grant.
        private enum GrantStart { Sent, Empty, Failed }

        // Ships a queued "give"/"kit" as a one-record-per-entry vault payload so it travels the SAME
        // confirmed route as a vault restore (AP_VaultApply -> AP_VaultAck, both ends in this file). The
        // alternative, AP_GiveItem, has no reply of any kind: its executor returns silently when the prefab
        // is not in the client's ObjectDB, which is exactly the case the queue must not lose.
        private static GrantStart BeginGrant(ZNetPeer peer, string id, string name, string table, string key, string kind, string detail)
        {
            if (ZRoutedRpc.instance == null) return GrantStart.Failed;

            List<KitEntry> entries;
            if (kind == "kit")
                entries = ParseKit(detail.Length > 0 ? detail : StarterKitRaw);
            else
            {
                string prefab; int count, quality;
                if (!ParseGive(detail, out prefab, out count, out quality)) return GrantStart.Failed;
                entries = new List<KitEntry> { new KitEntry { Prefab = prefab, Count = count, Quality = quality } };
            }
            if (entries.Count == 0) return GrantStart.Empty;

            int records;
            var payload = BuildGrantPayload(entries, out records);
            if (records == 0) return GrantStart.Empty;

            var token = NextToken();
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(token);
            pkg.Write(false);      // wipeFirst: a grant adds, it never clears the bag
            pkg.Write(records);
            pkg.Write(payload);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_VaultApply", pkg); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Offline {kind} send to {name} failed: {e.Message}");
                return GrantStart.Failed;
            }

            PendingGrants[token] = new QueuedGrant
            {
                Uid = peer.m_uid,
                Id = id,
                Name = name,
                Table = table,
                Key = key,
                Kind = kind,
                Detail = detail,
                Expires = Time.unscaledTime + SnapTimeout,
            };
            return GrantStart.Sent;
        }

        // Same record layout as WriteRecord (the client reads both with ReadRecord), with every
        // character-specific field neutral: durability 0 = full on apply, and the client picks the slot.
        private static string BuildGrantPayload(List<KitEntry> entries, out int records)
        {
            var body = new ZPackage();
            body.Write(Ver);
            var n = Mathf.Min(entries.Count, MaxItems);
            body.Write(n);
            for (var i = 0; i < n; i++)
            {
                var e = entries[i];
                body.Write(e.Prefab ?? "");
                body.Write(Mathf.Clamp(e.Count, 1, MaxGiveCount));
                body.Write(Mathf.Max(1, e.Quality));
                body.Write(0);      // variant
                body.Write(0f);     // durability
                body.Write(0L);     // crafterID
                body.Write("");     // crafterName
                body.Write(false);  // equipped
                body.Write(0);      // gridX
                body.Write(0);      // gridY
                body.Write(0);      // worldLevel
                body.Write(0);      // custom data entries
            }
            records = n;
            return body.GetBase64();
        }

        // The confirmation the offline queue waits for. Called from OnVaultAck for a token that is not a
        // restore. Only a report of items actually added may retire a queued row.
        private static void SettleGrant(long token, long sender, int applied, int missing)
        {
            QueuedGrant d;
            if (!PendingGrants.TryGetValue(token, out d)) return;
            if (d.Uid != sender) return;   // only the peer we sent it to may confirm it
            PendingGrants.Remove(token);

            if (applied <= 0)
            {
                // Nothing landed — the prefab does not exist on that client, or the payload was rejected.
                // The row STAYS queued and no log line claims otherwise.
                CompanionPlugin.SrvAudit(0L, "OFFLINE-DELIVER-FAILED",
                    $"id={d.Id} name={d.Name} kind={d.Kind} detail={d.Detail} queuedTicks={d.Key} applied=0 unknownPrefabs={missing} result=kept-in-queue");
                var text = $"Offline queue: {d.Name}'s client added nothing for '{d.Kind} {d.Detail}' ({missing} unknown prefab(s)). The row is STILL QUEUED - check the prefab name.";
                CompanionPlugin.FeatureLog(text);
                try { Wave1Moderation.NotifyOnlineAdmins(text); } catch (Exception) { }
                try { Wave1AuditRpc.PostModLog($"QUEUE NOT DELIVERED to {d.Name}: {d.Kind} {d.Detail} (client added nothing; row kept)"); }
                catch (Exception) { }
                return;
            }

            var t = FeatureStore.Table(d.Table);
            if (t.Remove(d.Key)) FeatureStore.SaveTable(d.Table);
            GrantAttempts.Remove(d.Table + "|" + d.Key);

            var partial = missing > 0 ? $", {missing} prefab(s) unknown on their client" : "";
            CompanionPlugin.SrvAudit(0L, "OFFLINE-DELIVER",
                $"id={d.Id} name={d.Name} kind={d.Kind} detail={d.Detail} queuedTicks={d.Key} appliedStacks={applied} unknownPrefabs={missing}");
            CompanionPlugin.FeatureLog($"Offline queue: {d.Name} confirmed {applied} stack(s) from {d.Kind} {d.Detail}{partial}");
            try { Wave1AuditRpc.PostModLog($"QUEUE DELIVERED to {d.Name}: {d.Kind} {d.Detail} ({applied} stack(s) confirmed{partial})"); }
            catch (Exception) { }
            try { Wave1Moderation.NotifyOnlineAdmins($"Offline queue: {d.Name} received {d.Kind} {d.Detail} ({applied} stack(s) confirmed{partial})"); }
            catch (Exception) { }
        }

        // One queued action. Returns false when the row could not be delivered and should stay queued.
        // "give"/"kit" are deliberately absent: they need a delivery confirmation and go through BeginGrant.
        private static bool ApplyRow(ZNetPeer peer, string kind, string detail)
        {
            if (ZRoutedRpc.instance == null) return false;
            switch (kind)
            {
                case "strip":
                {
                    string prefab; int count;
                    if (!ParseStrip(detail, out prefab, out count)) return false;
                    // CompanionPlugin.OnRemoveItem's ZPackage shape: string itemName, int amount, long replyTo.
                    // replyTo is where the target pushes its refreshed inventory; there is no admin waiting on
                    // an automated delivery, so it is addressed to the server's own session id. The server
                    // registers no AP_InvData handler, so the refresh is simply discarded (never target 0 —
                    // ZRoutedRpc treats 0 as "everybody" and that would broadcast an inventory to the world).
                    var relay = new ZPackage();
                    relay.Write(prefab);
                    relay.Write(count);
                    relay.Write(ZDOMan.instance != null ? ZDOMan.GetSessionID() : peer.m_uid);
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_RemoveItem", relay);
                    return true;
                }
                case "tp":
                {
                    Vector3 pos;
                    if (!ParseTp(detail, out pos)) return false;
                    // TIER-VANILLA: Chat.RPC_TeleportPlayer is registered on EVERY client (Chat.cs:130) with no
                    // sender or admin check — the one teleport that works against an unmodded client.
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_TeleportPlayer", pos, Quaternion.identity, true);
                    return true;
                }
                default:
                    return false;
            }
        }

        // Age-out. FeatureStore exposes no table enumeration, so the per-player queue files are discovered on
        // disk and their table names derived from the file name (DataDir is the module's own directory).
        private static void PruneOfflineTables()
        {
            var dir = FeatureStore.DataDir;
            if (string.IsNullOrEmpty(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, TblOfflinePrefix + "*.txt"); }
            catch (Exception) { return; }
            var cutoff = DateTime.UtcNow.AddDays(-OfflinePruneDays).Ticks;

            foreach (var file in files)
            {
                string table;
                try { table = Path.GetFileNameWithoutExtension(file); }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(table)) continue;
                var t = FeatureStore.Table(table);
                if (t.Count == 0) continue;
                List<string> dead = null;
                foreach (var kv in t)
                {
                    long ticks;
                    if (!long.TryParse(kv.Key, out ticks) || ticks < cutoff)
                        (dead ?? (dead = new List<string>())).Add(kv.Key);
                }
                if (dead == null) continue;
                foreach (var k in dead) t.Remove(k);
                FeatureStore.SaveTable(table);
                CompanionPlugin.FeatureLog($"Offline queue: pruned {dead.Count} row(s) older than {OfflinePruneDays:0} days from {table}");
            }
        }

        // ==================== 6. first-join starter kit ====================

        // A kit is ENQUEUED, never granted inline, so it travels the same audited delivery path as every other
        // queued action (and therefore behaves correctly for a player whose client has no companion mod).
        //
        // "First-ever join" = wave 1's presence table has no row for this id, or has a row with sessions <= 1.
        // Both cases are checked because Harmony does not order two postfixes on the same method: wave 1's
        // presence writer may already have run when we get here, in which case the brand-new row reads
        // sessions == 1. A "kit_<id>" marker makes the whole thing idempotent regardless.
        private static void MaybeQueueStarterKit(string id, string playerName)
        {
            if (!StarterKitOn) return;
            var kit = StarterKitRaw.Trim();
            if (kit.Length == 0) return;

            var meta = FeatureStore.Table(TblMeta);
            var markerKey = "kit_" + id;
            if (meta.ContainsKey(markerKey)) return;

            var presence = Wave1AuditRpc.LookupById(FeatureStore.Table(TblPresence), id);
            if (presence != null)
            {
                var parts = presence.Split(new[] { '|' }, 5);
                int sessions;
                if (parts.Length > 2 && int.TryParse(parts[2], out sessions) && sessions > 1)
                {
                    // A returning player. Mark them so this check never runs again for them.
                    meta[markerKey] = DateTime.UtcNow.Ticks.ToString();
                    FeatureStore.SaveTable(TblMeta);
                    return;
                }
            }

            // Enqueue FIRST, mark second: a full queue must leave the player eligible on their next join
            // instead of silently swallowing the kit.
            long ticks;
            if (!EnqueueOffline(id, "kit", CleanDetail(kit), out ticks))
            {
                CompanionPlugin.FeatureLog($"Starter kit for {playerName} ({id}) not queued — their queue is full; will retry on the next join.");
                return;
            }
            meta[markerKey] = DateTime.UtcNow.Ticks.ToString();
            FeatureStore.SaveTable(TblMeta);
            CompanionPlugin.SrvAudit(0L, "STARTER-KIT-QUEUED", $"id={id} name={playerName} kit={kit} queuedTicks={ticks}");
            CompanionPlugin.FeatureLog($"Starter kit queued for first-time player {playerName} ({id})");
        }

        // ==================== payload serialization (client side) ====================

        private static string SerializeInventory(Player player, out int count)
        {
            count = 0;
            var inv = player.GetInventory();
            if (inv == null) return null;
            var items = inv.GetAllItems();
            if (items == null) return null;

            ResolveReflection();
            var body = new ZPackage();
            body.Write(Ver);
            var n = Mathf.Min(items.Count, MaxItems);
            body.Write(n);
            for (var i = 0; i < n; i++)
            {
                var item = items[i];
                if (item == null || item.m_shared == null) { WriteEmptyRecord(body); continue; }
                try { WriteRecord(body, item); }
                catch (Exception) { WriteEmptyRecord(body); }
            }
            count = n;
            return body.GetBase64();
        }

        // Record layout v1 (fixed order; a new field means a new Ver, never an insertion):
        //   string prefab | int stack | int quality | int variant | float durability | long crafterID |
        //   string crafterName | bool equipped | int gridX | int gridY | int worldLevel |
        //   int customCount | customCount x (string key, string value)
        private static void WriteRecord(ZPackage p, ItemDrop.ItemData item)
        {
            p.Write(item.m_dropPrefab != null ? item.m_dropPrefab.name : (item.m_shared.m_name ?? ""));
            p.Write(item.m_stack);
            p.Write(item.m_quality);
            p.Write(item.m_variant);
            p.Write(item.m_durability);
            p.Write(item.m_crafterID);
            p.Write(item.m_crafterName ?? "");
            p.Write(item.m_equipped);
            p.Write(item.m_gridPos.x);
            p.Write(item.m_gridPos.y);
            // m_worldLevel / m_customData are newer fields: reached reflectively so a build without them
            // degrades to "restored without new-game-plus level / custom data" instead of throwing.
            p.Write(_fWorldLevel != null ? Convert.ToInt32(_fWorldLevel.GetValue(item)) : 0);

            var custom = _fCustomData != null ? _fCustomData.GetValue(item) as Dictionary<string, string> : null;
            var cn = custom == null ? 0 : Mathf.Min(custom.Count, MaxCustomData);
            p.Write(cn);
            if (cn <= 0) return;
            var written = 0;
            foreach (var kv in custom)
            {
                if (written >= cn) break;
                p.Write(kv.Key ?? "");
                p.Write(kv.Value ?? "");
                written++;
            }
        }

        private static void WriteEmptyRecord(ZPackage p)
        {
            p.Write("");
            p.Write(0); p.Write(1); p.Write(0);
            p.Write(0f);
            p.Write(0L);
            p.Write("");
            p.Write(false);
            p.Write(0); p.Write(0); p.Write(0);
            p.Write(0);
        }

        private sealed class Record
        {
            public string Prefab;
            public int Stack;
            public int Quality;
            public int Variant;
            public float Durability;
            public long CrafterId;
            public string CrafterName;
            public bool Equipped;
            public int WorldLevel;
            public List<KeyValuePair<string, string>> Custom;
        }

        private static Record ReadRecord(ZPackage p)
        {
            var r = new Record();
            r.Prefab = p.ReadString();
            r.Stack = p.ReadInt();
            r.Quality = p.ReadInt();
            r.Variant = p.ReadInt();
            r.Durability = p.ReadSingle();
            r.CrafterId = p.ReadLong();
            r.CrafterName = p.ReadString();
            r.Equipped = p.ReadBool();
            p.ReadInt();   // gridX — read for layout compatibility; Inventory.AddItem chooses the real slot
            p.ReadInt();   // gridY
            r.WorldLevel = p.ReadInt();
            var cn = p.ReadInt();
            if (cn < 0 || cn > MaxCustomData) throw new InvalidDataException("custom data count out of range");
            if (cn > 0)
            {
                r.Custom = new List<KeyValuePair<string, string>>(cn);
                for (var i = 0; i < cn; i++)
                    r.Custom.Add(new KeyValuePair<string, string>(p.ReadString(), p.ReadString()));
            }
            return r;
        }

        /// <summary>
        /// Full structural validation of a stored payload. expectedCount &lt; 0 skips the count comparison.
        /// Deliberately strict — a payload that does not consume exactly its own bytes is treated as corrupt,
        /// because the writer is ours and always produces an exact encoding.
        /// </summary>
        private static bool TryParsePayload(string b64, int expectedCount, out int parsed)
        {
            parsed = 0;
            if (string.IsNullOrEmpty(b64) || b64.Length > MaxPayloadChars) return false;
            try
            {
                var p = new ZPackage(b64);
                if (p.ReadInt() != Ver) return false;
                var n = p.ReadInt();
                if (n < 0 || n > MaxItems) return false;
                for (var i = 0; i < n; i++) ReadRecord(p);
                if (p.GetPos() != p.Size()) return false;
                parsed = n;
                return expectedCount < 0 || parsed == expectedCount;
            }
            catch (Exception) { return false; }
        }

        // ==================== payload application (client side) ====================

        private static void WipeInventory(Player player)
        {
            var inv = player.GetInventory();
            if (inv == null) return;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (item == null) continue;
                try
                {
                    // An equipped item must be unequipped first or the player keeps a ghost-equipped copy in
                    // hand until they relog (the same lesson CompanionPlugin.OnRemoveItem documents).
                    if (item.m_equipped) player.UnequipItem(item, false);
                    inv.RemoveItem(item, item.m_stack);
                }
                catch (Exception) { }
            }
        }

        private static void ApplyPayload(Player player, string b64, out int applied, out int missing)
        {
            applied = 0;
            missing = 0;
            ResolveReflection();
            var inv = player.GetInventory();
            if (inv == null) return;

            var p = new ZPackage(b64);
            if (p.ReadInt() != Ver) return;
            var n = p.ReadInt();
            for (var i = 0; i < n; i++)
            {
                Record r;
                try { r = ReadRecord(p); }
                catch (Exception) { return; }
                if (r.Prefab == null || r.Prefab.Length == 0 || r.Stack <= 0) continue;

                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(r.Prefab) : null;
                if (prefab == null) { missing++; continue; }
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) { missing++; continue; }
                // Icon-less items (hair, beards, effect holders) corrupt the inventory grid — never add them.
                var icons = drop.m_itemData.m_shared.m_icons;
                if (icons == null || icons.Length == 0) { missing++; continue; }

                var maxStack = drop.m_itemData.m_shared.m_maxStackSize;
                if (maxStack < 1) maxStack = 1;
                var remaining = Mathf.Min(r.Stack, MaxGiveCount);
                while (remaining > 0)
                {
                    var stack = Mathf.Min(remaining, maxStack);
                    remaining -= stack;
                    ItemDrop.ItemData data;
                    try
                    {
                        data = drop.m_itemData.Clone();
                        data.m_dropPrefab = prefab;
                        data.m_stack = stack;
                        data.m_quality = Mathf.Clamp(r.Quality, 1, Mathf.Max(1, data.m_shared.m_maxQuality));
                        data.m_variant = Mathf.Clamp(r.Variant, 0, icons.Length - 1);
                        var maxDur = data.GetMaxDurability();
                        data.m_durability = r.Durability <= 0f ? maxDur : Mathf.Clamp(r.Durability, 0f, maxDur);
                        data.m_crafterID = r.CrafterId;
                        data.m_crafterName = r.CrafterName ?? "";
                        if (_fWorldLevel != null && r.WorldLevel > 0)
                        {
                            try { _fWorldLevel.SetValue(data, r.WorldLevel); }
                            catch (Exception) { }
                        }
                        if (r.Custom != null && _fCustomData != null)
                        {
                            try
                            {
                                var dict = _fCustomData.GetValue(data) as Dictionary<string, string>;
                                if (dict != null)
                                {
                                    dict.Clear();
                                    foreach (var kv in r.Custom) dict[kv.Key] = kv.Value;
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception) { break; }

                    var added = false;
                    try { added = inv.AddItem(data); }
                    catch (Exception) { }
                    if (!added)
                    {
                        // Inventory full: drop at the player's feet, exactly like OnGiveItem's fallback, so a
                        // restore never silently eats items.
                        try
                        {
                            var go = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity);
                            var d = go.GetComponent<ItemDrop>();
                            if (d != null)
                            {
                                d.m_itemData.m_stack = stack;
                                d.m_itemData.m_quality = data.m_quality;
                                d.m_itemData.m_durability = data.m_durability;
                            }
                        }
                        catch (Exception) { }
                    }
                    else if (r.Equipped && _mEquipItem != null)
                    {
                        try { _mEquipItem.Invoke(player, new object[] { data, false }); }
                        catch (Exception) { }
                    }
                    applied++;
                }
            }
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;
            try { _fWorldLevel = AccessTools.Field(typeof(ItemDrop.ItemData), "m_worldLevel"); }
            catch (Exception) { }
            try { _fCustomData = AccessTools.Field(typeof(ItemDrop.ItemData), "m_customData"); }
            catch (Exception) { }
            try { _mEquipItem = AccessTools.Method(typeof(Humanoid), "EquipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) }); }
            catch (Exception) { }
        }

        // ==================== trust helpers ====================

        // Local equivalent of CompanionPlugin.SenderIsServer (private there, and this file must not edit it).
        // Same two cases and the same gate: on a client the server peer's uid, on a host our own session id —
        // and the host case is only honoured while the routed-RPC sender sanitizer is active, because without
        // it an attacker chooses the sender field and could impersonate the server.
        private static bool SenderIsServerLike(long sender)
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return false;
                var server = znet.GetServerPeer();
                if (server != null && server.m_uid != 0L && sender == server.m_uid) return true;
                if (!znet.IsServer() || ZDOMan.instance == null) return false;
                if (sender != ZDOMan.GetSessionID()) return false;
                if (!_sanitizerFieldTried)
                {
                    _sanitizerFieldTried = true;
                    _fSanitizerActive = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                    if (_fSanitizerActive == null)
                        CompanionPlugin.FeatureLog("Vault: SenderSanitizerActive not readable — host-local vault executors are disabled (fail closed).");
                }
                if (_fSanitizerActive == null) return false;
                var v = _fSanitizerActive.GetValue(null);
                return v is bool && (bool)v;
            }
            catch (Exception) { return false; }
        }

        // ==================== parsing / formatting helpers ====================

        private static long NextToken()
        {
            _tokenSeq++;
            // Unique per server run and not guessable in bulk; the token alone grants nothing (the sender is
            // re-checked against the peer we asked), it just correlates a reply with a request.
            return (DateTime.UtcNow.Ticks & 0x00FFFFFFFFFFFFFFL) ^ (_tokenSeq << 56);
        }

        private static long UniqueKey(Dictionary<string, string> t, long ticks)
        {
            while (t.ContainsKey(ticks.ToString())) ticks++;
            return ticks;
        }

        /// <summary>
        /// Table names become FILE names (FeatureStore writes &lt;name&gt;.txt), so an id that arrives over the
        /// wire must never reach one unfiltered — this is the path-traversal gate. Returns "" when nothing
        /// usable survives, and callers then refuse the operation.
        /// </summary>
        private static string SafeIdKey(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var sb = new System.Text.StringBuilder(Math.Min(id.Length, MaxIdLen));
            foreach (var c in id)
            {
                if (sb.Length >= MaxIdLen) break;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
            }
            var s = sb.ToString().Trim('.');
            return s;
        }

        private static string VaultTable(string id)
        {
            var safe = SafeIdKey(id);
            return safe.Length == 0 ? null : TblVaultPrefix + safe;
        }

        private static string OfflineTable(string id)
        {
            var safe = SafeIdKey(id);
            return safe.Length == 0 ? null : TblOfflinePrefix + safe;
        }

        // Ids reach us as either the full "Platform_id" or the bare id (adminlist leniency, see
        // CompanionPlugin.SenderIsAdmin). Both forms must land on the SAME vault, so an incoming id is mapped
        // onto whichever spelling the presence table already uses for that player.
        private static string CanonicalId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var trimmed = id.Trim();
            if (trimmed.Length > MaxIdLen) trimmed = trimmed.Substring(0, MaxIdLen);
            try
            {
                foreach (var kv in FeatureStore.Table(TblPresence))
                    if (Wave1AuditRpc.SameId(kv.Key, trimmed)) return kv.Key;
            }
            catch (Exception) { }
            return trimmed;
        }

        private static string PeerHost(ZNetPeer peer) =>
            peer != null && peer.m_socket != null ? (peer.m_socket.GetHostName() ?? "") : "";

        // '|' is the field separator inside every stored value, so it can never survive in free text.
        private static string CleanLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MaxLabelLen ? s.Substring(0, MaxLabelLen) : s;
        }

        private static string CleanDetail(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MaxDetailLen ? s.Substring(0, MaxDetailLen) : s;
        }

        private static string CleanSource(string s)
        {
            if (s == "auto" || s == "death" || s == "manual") return s;
            return "manual";
        }

        private static string DefaultLabel(string source) =>
            source == "death" ? "death" : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");

        private static string CleanPrefab(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxPrefabLen) s = s.Substring(0, MaxPrefabLen);
            foreach (var c in s)
                if (c == '|' || c == ',' || c == ':' || char.IsWhiteSpace(c)) return "";
            return s;
        }

        // value = "label|source|itemCount|payloadBase64". label and source are '|'-free by construction and the
        // base64 alphabet contains no '|', so a 4-way split is exact.
        private static bool ParseSnap(string value, out string label, out string source, out int count, out string payload)
        {
            label = ""; source = "manual"; count = 0; payload = "";
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split(new[] { '|' }, 4);
            if (parts.Length < 4) return false;
            label = parts[0];
            source = CleanSource(parts[1]);
            if (!int.TryParse(parts[2], out count)) return false;
            payload = parts[3];
            return true;
        }

        // value = "kind|detail"; detail may itself contain '|' (a "give" detail does), hence the bounded split.
        private static void ParseQueueRow(string value, out string kind, out string detail)
        {
            kind = ""; detail = "";
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 2);
            kind = parts[0];
            if (parts.Length > 1) detail = parts[1];
        }

        // Validates and re-emits a queue row so nothing unparsable is ever stored.
        private static bool NormalizeQueueRow(ref string kind, string detail, out string normalized)
        {
            normalized = "";
            kind = (kind ?? "").Trim().ToLowerInvariant();
            detail = CleanDetail(detail);
            switch (kind)
            {
                case "give":
                {
                    string prefab; int count, quality;
                    if (!ParseGive(detail, out prefab, out count, out quality)) return false;
                    normalized = $"{prefab}|{count}|{quality}";
                    return true;
                }
                case "strip":
                {
                    string prefab; int count;
                    if (!ParseStrip(detail, out prefab, out count)) return false;
                    normalized = $"{prefab}|{count}";
                    return true;
                }
                case "tp":
                {
                    Vector3 pos;
                    if (!ParseTp(detail, out pos)) return false;
                    normalized = $"{pos.x:0.##}|{pos.y:0.##}|{pos.z:0.##}";
                    return true;
                }
                case "kit":
                {
                    var spec = detail.Length > 0 ? detail : StarterKitRaw;
                    normalized = CleanDetail(spec);
                    return true;   // an empty kit is legal: it delivers nothing and clears itself
                }
                default:
                    return false;
            }
        }

        // detail = "<prefab>|<count>|<quality>"
        private static bool ParseGive(string detail, out string prefab, out int count, out int quality)
        {
            prefab = ""; count = 0; quality = 1;
            if (string.IsNullOrEmpty(detail)) return false;
            var parts = detail.Split('|');
            if (parts.Length < 2) return false;
            prefab = CleanPrefab(parts[0]);
            if (prefab.Length == 0) return false;
            if (!int.TryParse(parts[1].Trim(), out count)) return false;
            count = Mathf.Clamp(count, 1, MaxGiveCount);
            if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out quality)) quality = Mathf.Clamp(quality, 1, 10);
            else quality = 1;
            return true;
        }

        // detail = "<prefab>|<count>"
        private static bool ParseStrip(string detail, out string prefab, out int count)
        {
            prefab = ""; count = 0;
            if (string.IsNullOrEmpty(detail)) return false;
            var parts = detail.Split('|');
            prefab = CleanPrefab(parts[0]);
            if (prefab.Length == 0) return false;
            if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out count)) count = MaxGiveCount;
            count = Mathf.Clamp(count, 1, MaxGiveCount);
            return true;
        }

        // detail = "<x>|<y>|<z>"
        private static bool ParseTp(string detail, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (string.IsNullOrEmpty(detail)) return false;
            var parts = detail.Split('|');
            if (parts.Length < 3) return false;
            float x, y, z;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z)) return false;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            pos = new Vector3(Mathf.Clamp(x, -50000f, 50000f), Mathf.Clamp(y, -1000f, 10000f), Mathf.Clamp(z, -50000f, 50000f));
            return true;
        }

        private struct KitEntry
        {
            public string Prefab;
            public int Count;
            public int Quality;
        }

        // spec = "prefab:count:quality,prefab:count:quality,..."
        private static List<KitEntry> ParseKit(string spec)
        {
            var res = new List<KitEntry>();
            if (string.IsNullOrEmpty(spec)) return res;
            foreach (var raw in spec.Split(','))
            {
                if (res.Count >= MaxKitEntries) break;
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                var parts = entry.Split(':');
                var prefab = CleanPrefab(parts[0]);
                if (prefab.Length == 0) continue;
                var count = 1;
                var quality = 1;
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out count)) count = Mathf.Clamp(count, 1, MaxGiveCount);
                else count = 1;
                if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out quality)) quality = Mathf.Clamp(quality, 1, 10);
                else quality = 1;
                res.Add(new KitEntry { Prefab = prefab, Count = count, Quality = quality });
            }
            return res;
        }

        private static List<string> SortedKeysDesc(Dictionary<string, string> t)
        {
            var keys = new List<string>(t.Keys);
            keys.Sort(CompareTicksDesc);
            return keys;
        }

        private static List<string> SortedKeysAsc(Dictionary<string, string> t)
        {
            var keys = new List<string>(t.Keys);
            keys.Sort(CompareTicksAsc);
            return keys;
        }

        private static int CompareTicksDesc(string a, string b) => CompareTicksAsc(b, a);

        private static int CompareTicksAsc(string a, string b)
        {
            long x, y;
            var ax = long.TryParse(a, out x);
            var by = long.TryParse(b, out y);
            if (ax && by) return x.CompareTo(y);
            if (ax) return -1;
            if (by) return 1;
            return string.CompareOrdinal(a, b);
        }

        private static ZNetPeer FindPeerByUid(long uid)
        {
            if (ZNet.instance == null || uid == 0L) return null;
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
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 5 — server-side AREA tools ====================
    // Four admin tools that all operate on a SPHERE OF WORLD around a point: ownership transfer, mass
    // repair/remove, protection zones and ward management. Unlike wave 2's census these are TARGETED
    // operations, so they are answered synchronously from the RPC handler instead of frame-spread — but
    // only because the radius is hard-capped at 128 m and the collected set at 20 000 ZDOs. Anything
    // bigger is REFUSED with an explanation, never quietly turned into a world sweep.
    //
    // ---------------------------------------------------------------------------------------------------
    // ENGINE FACTS (all verified by decompiling assembly_valheim; line numbers are from that decompile)
    // ---------------------------------------------------------------------------------------------------
    // * ZDOMan.FindSectorObjects(Vector2s sector, SimulationDistance sd, List<ZDO> sectorObjects,
    //   List<ZDO> distantSectorObjects = null) is PUBLIC (ZDOMan.cs:1201); zones are shorts since 1.0.12.
    //   It APPENDS (FindObjects -> objects.AddRange, ZDOMan.cs:1426-1441: the sector-array bucket plus that
    //   sector's portal bucket) and walks rings around `sector`. ZoneCompat.FindSectorObjects(man, sector,
    //   area, list) wraps it as the classic square (2*area+1)^2 block with no distant pass, and clips the
    //   block to the 512x512 grid (|zone| >= 256 aliases Sector 0, ZoneSystem.cs:2990-3003; the pre-1.0.12
    //   m_objectsByOutsideSector dictionary is gone). A 128 m radius is area = 2 => 25 sectors. That is
    //   the whole reason this module can be synchronous.
    // * ZoneSystem.GetZone(Vector3) is static and uses a hard-coded 64 m grid (ZoneSystem.cs:2971-2976);
    //   ZoneSystem.instance.m_zoneSize is the same 64 (ZoneSystem.cs:371) and is read defensively.
    // * ZDO OWNERSHIP IS SESSION STATE, NOT SAVED STATE. ZDO.Save (ZDO.cs:1001) serialises only the typed
    //   ZDOExtraData maps; the owner lives in ZDOExtraData's owner map keyed by a session id
    //   (ZDO.SetOwnerInternal, ZDO.cs:1519-1535) and is gone after a restart. Ownership transfer is
    //   therefore ALWAYS transient — see the creator note below for what actually persists.
    // * PIECE EDITING IS NOT GATED ON ZDO OWNERSHIP. Player.RemovePiece (Player.cs:2745-2775) gates on
    //   piece.m_canBeRemoved, Location.IsInsideNoBuildLocation and PrivateArea.CheckAccess — nothing else.
    //   PrivateArea.CheckAccess -> HaveLocalAccess -> Piece.IsCreator() compares ZDOVars.s_creator
    //   ("creator", a SAVED long) against the local player's PROFILE id. So the real "the builder quit and
    //   nobody can touch their base" fix is (a) the ward (guard stone) and (b) the creator field — not the
    //   ZDO owner. This module writes BOTH and says exactly which in its reply and its audit line.
    // * Piece.m_creator is cached in Piece.Awake (Piece.cs:196), so a creator rewrite only takes effect on
    //   clients that (re)load the object. The ZDO write itself is immediate and persistent.
    // * WearNTear stores durability in ZDOVars.s_health ("health", float) and full health is the prefab's
    //   WearNTear.m_health scaled by the world level: m_health += worldLevel * m_worldLevelPieceHPMultiplier
    //   * m_health (WearNTear.cs:242-253). WearNTear.Repair writes s_health and then broadcasts
    //   RPC_HealthChanged to everybody (WearNTear.cs:331-332) — the ZDO write alone does NOT refresh the
    //   visual on a client that already has the object loaded, so this module mirrors that broadcast.
    // * PrivateArea (guard stone) ZDO layout: ZDOVars.s_enabled ("enabled", bool), ZDOVars.s_creatorName
    //   ("creatorName", string, written by PrivateArea.Setup), ZDOVars.s_permitted ("permitted", int count)
    //   plus "pu_id<i>" (long playerID) / "pu_name<i>" (string) pairs. The owner check is the Piece
    //   creator, not the ward itself.
    // * The server's OWN deletions loop back through ZRoutedRpc with m_senderPeerID = the server's own uid
    //   (ZRoutedRpc.cs:120-135, ZDOMan.SendDestroyed ZDOMan.cs:638-652), which is never a connected peer.
    //   That is how the destroy guard below tells "a client hammered this" from "the server removed it".
    //
    // ALL ZDO KEYS ARE COMPUTED FROM THEIR LITERAL STRING, never from a ZDOVars field reference: the string
    // is what is persisted in the save file and can never change, while the C# field name can.
    internal static class Wave5Area
    {
        private const int Ver = 1;                    // wire version — bump, never reorder

        // ---- hard limits (contract) ----
        private const float HardMaxRadius = 128f;     // refuse anything larger, do not frame-spread it
        private const int MaxCollect = 20000;         // ZDOs collected by one radius query
        private const int MassAffectCap = 5000;       // refuse a repair/remove that would touch more than this
        private const int HealthRpcCap = 500;         // how many visual-refresh RPCs one repair may broadcast
        private const int PieceBreakdownCap = 20;     // AP_MassPiece shipped cap
        private const int ZoneListCap = 40;           // AP_ZoneList shipped cap
        private const int WardListCap = 40;           // AP_WardList shipped cap
        private const int MaxZones = 64;              // protection zones per world
        private const int MaxBaselinePerZone = 20000; // grandfathered pieces tracked per zone
        private const int WardScanCap = 5000;         // wards collected by one world-wide ward scan
        private const int RestoresPerCycle = 100;     // no-damage health restores per enforcement pass

        private const float ZoneCheckInterval = 2f;   // seconds between protection-zone passes
        private const int ZonesPerCycle = 4;          // zones examined per pass (round-robin)
        private const float WardActionRadius = 8f;    // how close the clicked point must be to the ward

        // ---- ZDO keys (literal strings -> stable hashes; see the header) ----
        private static readonly int KeyHealth = "health".GetStableHashCode();
        private static readonly int KeyCreator = "creator".GetStableHashCode();
        private static readonly int KeyCreatorName = "creatorName".GetStableHashCode();
        private static readonly int KeyEnabled = "enabled".GetStableHashCode();
        private static readonly int KeyPermitted = "permitted".GetStableHashCode();
        private static readonly int KeyPlayerId = "playerID".GetStableHashCode();

        // ---- config ----
        private static ConfigEntry<bool> _enableZones;
        private static ConfigEntry<bool> _blockClientDestroy;
        private static ConfigEntry<bool> _enableMassRemove;
        private static ConfigEntry<float> _maxRadius;
        private static ConfigEntry<int> _budgetMs;

        private static bool ZonesEnabled => _enableZones != null && _enableZones.Value;
        private static bool BlockDestroyEnabled => _blockClientDestroy == null || _blockClientDestroy.Value;
        private static bool MassRemoveEnabled => _enableMassRemove == null || _enableMassRemove.Value;
        private static float MaxRadius => _maxRadius != null ? Mathf.Clamp(_maxRadius.Value, 4f, HardMaxRadius) : HardMaxRadius;
        private static int BudgetMs => _budgetMs != null ? Mathf.Clamp(_budgetMs.Value, 1, 16) : 8;

        // ---- prefab registry (own copy; wave 2 builds its own for its own purposes) ----
        private sealed class PrefabInfo
        {
            public string Name;
            public bool IsPiece;            // asset carries a Piece component -> has a creator field
            public bool HasWear;            // asset carries a WearNTear component -> has s_health
            public bool RandomInitialDamage;// WearNTear.m_randomInitialDamage: repairing to exactly max re-randomises on reload
            public bool IsWard;             // asset carries a player-faction PrivateArea component
            public float MaxHealth;         // WearNTear.m_health scaled by the world level
        }

        private static Dictionary<int, PrefabInfo> _info;
        private static object _mapScene;
        private static List<string> _wardPrefabNames;

        private static Type _tPiece, _tWear, _tPrivateArea;
        private static FieldInfo _fWearHealth, _fWearRandomDamage, _fAreaFaction;
        private static bool _typesProbed;

        // ---- scratch (reused; these handlers never run re-entrantly — Unity is single threaded here) ----
        private static readonly List<ZDO> Scratch = new List<ZDO>();
        private static readonly List<ZDO> Collected = new List<ZDO>();

        // ==================== lifecycle ====================

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableZones = cfg.Bind("Features", "EnableProtectionZones", false,
                    "Enforce admin-defined protection zones (no-build / no-damage spheres). OFF by default because it is a background feature that DESTROYS newly placed pieces inside a zone. Zones can still be defined and listed while this is off; nothing is enforced.");
                _blockClientDestroy = cfg.Bind("Features", "ProtectionZoneBlockClientDestroy", true,
                    "Inside a no-damage protection zone, drop client-sent 'destroy this object' messages for player-built pieces so the piece survives. Only has any effect when EnableProtectionZones is on and a no-damage zone exists. Admin-sent destroys and every server-side removal are always allowed through.");
                _enableMassRemove = cfg.Bind("Features", "EnableMassPieceRemove", true,
                    "Allow admins to mass-REMOVE build pieces in a radius. Mass repair and dry runs are unaffected by this switch. Removal is owner-only when tiered roles are on.");
                _maxRadius = cfg.Bind("Features", "AreaToolsMaxRadius", 128f,
                    "Largest radius (metres) any area tool will accept. Clamped to 4-128; requests above it are refused with an explanation rather than turned into a world sweep.");
                _budgetMs = cfg.Bind("Features", "AreaScanBudgetMs", 8,
                    "Milliseconds per frame the world-wide ward scan may spend. Clamped to 1-16. Radius operations are synchronous and are bounded by AreaToolsMaxRadius instead.");
            }

            // Reads stay at moderator; anything that mutates the world is owner-only (null) under roles.
            // Mass repair is a moderator grant, but the REMOVE action re-checks the pseudo-action
            // "AP_SrvMassPieceRemove" which no built-in role carries, so a moderator can repair and not
            // demolish while a custom roleperms line can still grant it explicitly.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvOwnerXferReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMassPieceReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvZoneListReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvZoneSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWardListReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWardAction", null);

            try
            {
                Harmony.CreateAndPatchAll(typeof(Wave5AreaRpcRegistration));
                Wave2Ops.ReportPatch("Wave5Area.RpcRegistration", true);
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave5Area RPC registration patch failed (area tools unavailable): {e.Message}");
                Wave2Ops.ReportPatch("Wave5Area.RpcRegistration", false);
            }

            try
            {
                Harmony.CreateAndPatchAll(typeof(Wave5ZoneDestroyGuard));
                Wave2Ops.ReportPatch("Wave5Area.ZoneDestroyGuard", true);
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave5Area destroy guard patch failed (no-damage zones degrade to a repair sweep): {e.Message}");
                Wave2Ops.ReportPatch("Wave5Area.ZoneDestroyGuard", false);
            }
        }

        private static float _nextZoneCheck;

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            if (_wardScan != null)
            {
                try { StepWardScan(); }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Ward scan step failed: {e.Message}");
                    var job = _wardScan;
                    _wardScan = null;
                    if (job != null) SendWardList(null, job.Requester);
                }
            }

            if (!ZonesEnabled) return;
            var now = Time.unscaledTime;
            if (now < _nextZoneCheck) return;
            _nextZoneCheck = now + ZoneCheckInterval;
            try { RunZoneEnforcement(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Protection-zone pass failed: {e.Message}"); }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave5AreaRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvOwnerXferReq", OnOwnerXferReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvMassPieceReq", OnMassPieceReq);
                    ZRoutedRpc.instance.Register("AP_SrvZoneListReq", new Action<long>(OnZoneListReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvZoneSet", OnZoneSet);
                    ZRoutedRpc.instance.Register("AP_SrvWardListReq", new Action<long>(OnWardListReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvWardAction", OnWardAction);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave5Area RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. radius enumeration ====================

        /// <summary>
        /// Collect every valid ZDO whose position is within <paramref name="radius"/> HORIZONTAL metres of
        /// <paramref name="center"/> into <paramref name="result"/> (cleared first).
        /// Distance is XZ-only with no vertical limit, deliberately matching how Valheim's own ward radius
        /// works (PrivateArea.IsInside -> Utils.DistanceXZ), so a tall build is covered top to bottom.
        /// Returns the number collected, -1 if the world/object store is unavailable, or -2 if the radius
        /// contains more than 20 000 objects (the caller must refuse, not silently truncate).
        /// </summary>
        internal static int CollectInRadius(Vector3 center, float radius, List<ZDO> result)
        {
            if (result == null) return -1;
            result.Clear();
            var man = ZDOMan.instance;
            if (man == null) return -1;

            radius = Mathf.Clamp(radius, 0.5f, HardMaxRadius);
            var zoneSize = 64f;
            try { if (ZoneSystem.instance != null && ZoneSystem.instance.m_zoneSize > 1f) zoneSize = ZoneSystem.instance.m_zoneSize; }
            catch (Exception) { }
            var area = Mathf.Clamp(Mathf.CeilToInt(radius / zoneSize), 1, 4);

            Scratch.Clear();
            try { ZoneCompat.FindSectorObjects(man, ZoneSystem.GetZone(center), area, Scratch); }
            catch (Exception e)
            {
                Scratch.Clear();
                CompanionPlugin.FeatureLog($"FindSectorObjects failed: {e.Message}");
                return -1;
            }

            var r2 = radius * radius;
            var overflow = false;
            for (var i = 0; i < Scratch.Count; i++)
            {
                var zdo = Scratch[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    var p = zdo.GetPosition();
                    var dx = p.x - center.x;
                    var dz = p.z - center.z;
                    if (dx * dx + dz * dz > r2) continue;
                }
                catch (Exception) { continue; }

                if (result.Count >= MaxCollect) { overflow = true; break; }
                result.Add(zdo);
            }
            Scratch.Clear();
            if (overflow) { result.Clear(); return -2; }
            return result.Count;
        }

        // Shared refusal text so every area tool explains the SAME limit the same way.
        private static bool RadiusOk(long sender, ref float radius)
        {
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                CompanionPlugin.NotifySender(sender, "Area tools: the radius must be a positive number.");
                return false;
            }
            var max = MaxRadius;
            if (radius > max)
            {
                CompanionPlugin.NotifySender(sender,
                    $"Area tools refuse a radius above {max:0} m ({radius:0} m requested). These are targeted operations, not world sweeps - use the world tools for anything larger.");
                return false;
            }
            radius = Mathf.Clamp(radius, 0.5f, max);
            return true;
        }

        private static bool CenterOk(long sender, Vector3 c)
        {
            if (float.IsNaN(c.x) || float.IsNaN(c.y) || float.IsNaN(c.z) ||
                float.IsInfinity(c.x) || float.IsInfinity(c.y) || float.IsInfinity(c.z))
            {
                CompanionPlugin.NotifySender(sender, "Area tools: the supplied position is not a valid point.");
                return false;
            }
            return true;
        }

        // ==================== 2. ownership transfer ====================

        // AP_SrvOwnerXferReq: ZPackage{float x, float y, float z, float radius, long newOwnerUid, bool dryRun}
        //
        // WHAT THIS ACTUALLY CHANGES (see the file header for the decompile evidence):
        //   * ZDOVars.s_creator ("creator") on every Piece-bearing ZDO in the radius -> the target's PLAYER
        //     PROFILE id. This is the field ward access and Piece.IsCreator() read, it is SAVED, and it is
        //     the one that actually fixes "the builder quit and nobody can touch their base".
        //   * The ZDO owner -> the target's peer uid when that player is connected. This is session state
        //     only (never written to the save) and exists so the transfer takes effect immediately for a
        //     player standing there.
        // newOwnerUid is interpreted as a CONNECTED PEER UID first; if no such peer exists it is taken as a
        // raw player profile id, which is how an offline player's base can still be handed over. 0 means
        // "give the ZDOs back to the server": owner -> the server session, creator LEFT ALONE (clearing the
        // creator would make Piece.IsPlacedByPlayer() false, which changes AI targeting and refund amounts).
        private static void OnOwnerXferReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvOwnerXferReq")) return;

            float x, y, z, radius; long newOwnerUid; bool dryRun;
            try
            {
                x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle();
                radius = pkg.ReadSingle(); newOwnerUid = pkg.ReadLong(); dryRun = pkg.ReadBool();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvOwnerXferReq: malformed packet dropped ({e.Message})");
                return;
            }

            var center = new Vector3(x, y, z);
            if (!CenterOk(sender, center) || !RadiusOk(sender, ref radius)) { SendOwnerXfer(sender, dryRun, 0, 0); return; }

            var n = CollectInRadius(center, radius, Collected);
            if (n < 0)
            {
                CompanionPlugin.NotifySender(sender, n == -2
                    ? $"Ownership transfer refused: more than {MaxCollect} objects inside {radius:0} m. Reduce the radius."
                    : "Ownership transfer unavailable: the server could not read the world object store.");
                SendOwnerXfer(sender, dryRun, 0, 0);
                return;
            }
            EnsurePrefabMap();

            // Resolve the destination.
            var session = SessionId();
            long targetPeerUid = 0, targetPlayerId = 0;
            string targetLabel;
            if (newOwnerUid == 0L)
            {
                targetPeerUid = session;
                targetLabel = "the server";
            }
            else
            {
                var peer = FindPeer(newOwnerUid);
                if (peer != null)
                {
                    targetPeerUid = peer.m_uid;
                    targetPlayerId = PlayerIdOfPeer(peer);
                    targetLabel = string.IsNullOrEmpty(peer.m_playerName) ? newOwnerUid.ToString() : peer.m_playerName;
                }
                else
                {
                    // Not connected: treat the value as a player profile id so an offline handover works.
                    targetPlayerId = newOwnerUid;
                    targetLabel = "player id " + newOwnerUid;
                }
            }

            var matched = 0;
            for (var i = 0; i < Collected.Count; i++)
            {
                var info = InfoOf(Collected[i]);
                if (info != null && info.IsPiece) matched++;
            }

            if (dryRun)
            {
                CompanionPlugin.NotifySender(sender,
                    $"Ownership transfer DRY RUN: {matched} build piece(s) within {radius:0} m would be handed to {targetLabel}.");
                SendOwnerXfer(sender, true, matched, 0);
                Collected.Clear();
                return;
            }

            var changed = 0;
            var creatorWrites = 0;
            var man = ZDOMan.instance;
            for (var i = 0; i < Collected.Count; i++)
            {
                var zdo = Collected[i];
                var info = InfoOf(zdo);
                if (info == null || !info.IsPiece) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    // Claim first: a write to a ZDO a client owns loses to that client's next sync.
                    zdo.SetOwner(session);
                    if (targetPlayerId != 0L) { zdo.Set(KeyCreator, targetPlayerId); creatorWrites++; }
                    if (targetPeerUid != 0L && targetPeerUid != session) zdo.SetOwner(targetPeerUid);
                    if (man != null) man.ForceSendZDO(zdo.m_uid);
                    changed++;
                }
                catch (Exception) { }
            }
            Collected.Clear();

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var detail = $"center={x:0.#}/{y:0.#}/{z:0.#} radius={radius:0.#} target={targetLabel} " +
                         $"matched={matched} changed={changed} creatorWrites={creatorWrites} " +
                         $"ownerWrite={(targetPeerUid != 0L ? "yes" : "no")}";
            CompanionPlugin.SrvAudit(sender, "OWNERXFER", detail);
            Wave1AuditRpc.PostModLog($"OWNERSHIP TRANSFER: {changed} piece(s) within {radius:0} m of {x:0}/{z:0} handed to {targetLabel} by {admin}");
            CompanionPlugin.FeatureLog($"Ownership transfer by {admin}: {detail}");
            CompanionPlugin.NotifySender(sender, creatorWrites > 0
                ? $"Transferred {changed} piece(s) to {targetLabel}: builder (creator) field rewritten on {creatorWrites} - this is the saved field ward access uses. Clients already standing there see it after the objects reload."
                : $"Transferred {changed} piece(s) to {targetLabel}: ZDO ownership only (session state, lost on restart); the saved builder field was left untouched.");

            SendOwnerXfer(sender, false, matched, changed);
        }

        // AP_OwnerXfer: {int ver, bool dryRun, int matched, int changed}
        private static void SendOwnerXfer(long uid, bool dryRun, int matched, int changed)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(dryRun);
            pkg.Write(matched);
            pkg.Write(changed);
            Reply(uid, "AP_OwnerXfer", pkg);
        }

        // ==================== 3. mass repair / remove ====================

        // AP_SrvMassPieceReq: ZPackage{float x, float y, float z, float radius, int action (0 repair,
        //                     1 remove), bool dryRun, string prefabFilter}
        private static void OnMassPieceReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMassPieceReq")) return;

            float x, y, z, radius; int action; bool dryRun; string filter;
            try
            {
                x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle();
                radius = pkg.ReadSingle(); action = pkg.ReadInt(); dryRun = pkg.ReadBool();
                filter = pkg.ReadString();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvMassPieceReq: malformed packet dropped ({e.Message})");
                return;
            }

            action = Mathf.Clamp(action, 0, 1);
            filter = string.IsNullOrEmpty(filter) ? null : filter.Trim();
            if (filter != null && filter.Length == 0) filter = null;

            var center = new Vector3(x, y, z);
            if (!CenterOk(sender, center) || !RadiusOk(sender, ref radius)) { SendMassPiece(sender, dryRun, action, 0, 0, null); return; }

            if (action == 1)
            {
                // Removal is destructive: it needs the owner-grade grant AND the server-side kill switch.
                if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMassPieceRemove"))
                {
                    CompanionPlugin.NotifySender(sender, "Mass REMOVE is owner-only; your role may repair but not demolish.");
                    SendMassPiece(sender, dryRun, action, 0, 0, null);
                    return;
                }
                if (!MassRemoveEnabled && !dryRun)
                {
                    CompanionPlugin.NotifySender(sender, "Mass remove is disabled in the server config (EnableMassPieceRemove=false). Dry runs still work.");
                    SendMassPiece(sender, dryRun, action, 0, 0, null);
                    return;
                }
            }

            var n = CollectInRadius(center, radius, Collected);
            if (n < 0)
            {
                CompanionPlugin.NotifySender(sender, n == -2
                    ? $"Mass {(action == 1 ? "remove" : "repair")} refused: more than {MaxCollect} objects inside {radius:0} m. Reduce the radius."
                    : "Mass piece tools unavailable: the server could not read the world object store.");
                SendMassPiece(sender, dryRun, action, 0, 0, null);
                return;
            }
            if (!EnsurePrefabMap())
            {
                // Without the prefab registry we cannot tell a wall from a deer. Refusing is the only safe
                // answer for a tool that repairs or deletes by family.
                CompanionPlugin.NotifySender(sender, "Mass piece tools unavailable: the server could not read the prefab registry, so build pieces cannot be identified.");
                SendMassPiece(sender, dryRun, action, 0, 0, null);
                Collected.Clear();
                return;
            }

            // ---- match pass ----
            var candidates = new List<ZDO>();
            var byPrefab = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < Collected.Count; i++)
            {
                var zdo = Collected[i];
                var info = InfoOf(zdo);
                if (info == null) continue;
                if (action == 0)
                {
                    // Repair targets anything with durability. Prefabs whose WearNTear randomises its
                    // starting damage are skipped: writing exactly max health makes WearNTear.Awake
                    // re-randomise on the next load, so the "repair" would silently undo itself.
                    if (!info.HasWear || info.RandomInitialDamage || info.MaxHealth <= 0f) continue;
                }
                else
                {
                    // Remove targets BUILD PIECES only, so a stray creature, dropped item or terrain
                    // compiler inside the radius is never destroyed by a build cleanup.
                    if (!info.IsPiece) continue;
                }
                if (filter != null && (info.Name == null ||
                    info.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;

                candidates.Add(zdo);
                var key = info.Name ?? "?";
                int c;
                byPrefab[key] = byPrefab.TryGetValue(key, out c) ? c + 1 : 1;
            }
            Collected.Clear();

            var matched = candidates.Count;
            if (dryRun)
            {
                CompanionPlugin.NotifySender(sender,
                    $"Mass {(action == 1 ? "remove" : "repair")} DRY RUN: {matched} object(s) within {radius:0} m{(filter != null ? " matching '" + filter + "'" : "")}.");
                SendMassPiece(sender, true, action, matched, 0, byPrefab);
                return;
            }
            if (matched > MassAffectCap)
            {
                CompanionPlugin.NotifySender(sender,
                    $"Mass {(action == 1 ? "remove" : "repair")} refused: {matched} objects matched, the per-operation cap is {MassAffectCap}. Reduce the radius or add a prefab filter.");
                SendMassPiece(sender, false, action, matched, 0, byPrefab);
                return;
            }

            var affected = action == 0 ? DoRepair(candidates) : DoRemove(candidates);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var verb = action == 1 ? "remove" : "repair";
            var detail = $"action={verb} center={x:0.#}/{y:0.#}/{z:0.#} radius={radius:0.#} " +
                         $"filter={(filter ?? "-")} matched={matched} affected={affected}";
            CompanionPlugin.SrvAudit(sender, "MASSPIECE", detail);
            CompanionPlugin.FeatureLog($"Mass {verb} by {admin}: {detail}");
            if (action == 1)
            {
                Wave1AuditRpc.PostModLog($"MASS REMOVE: {affected} piece(s) within {radius:0} m of {x:0}/{z:0} deleted by {admin}");
                Wave1Moderation.NotifyOnlineAdmins($"Mass remove: {affected} piece(s) deleted by {admin}");
                CompanionPlugin.NotifySender(sender, $"Removed {affected} of {matched} matched piece(s). Terrain edits are NOT undone by this - the raise/level piece is gone but the ground stays shaped.");
            }
            else
            {
                Wave1AuditRpc.PostModLog($"MASS REPAIR: {affected} piece(s) within {radius:0} m of {x:0}/{z:0} restored by {admin}");
                CompanionPlugin.NotifySender(sender, $"Repaired {affected} of {matched} matched object(s). Objects already loaded on a client refresh their damage look immediately for the first {HealthRpcCap}; the rest look repaired once they reload.");
            }

            SendMassPiece(sender, false, action, matched, affected, byPrefab);
        }

        private static int DoRepair(List<ZDO> candidates)
        {
            var session = SessionId();
            var man = ZDOMan.instance;
            var affected = 0;
            var broadcasts = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var zdo = candidates[i];
                var info = InfoOf(zdo);
                if (info == null || info.MaxHealth <= 0f) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    var cur = zdo.GetFloat(KeyHealth, info.MaxHealth);
                    if (cur >= info.MaxHealth) continue;      // already whole: matched, not affected
                    zdo.SetOwner(session);                    // claim, or the owning client overwrites us
                    zdo.Set(KeyHealth, info.MaxHealth);
                    if (man != null) man.ForceSendZDO(zdo.m_uid);
                    // Mirror WearNTear.Repair: the ZDO write alone does not refresh the cracked-wall visual
                    // on a client that already has the object loaded. Capped so a big repair cannot turn
                    // into thousands of routed RPCs in one frame.
                    if (broadcasts < HealthRpcCap)
                    {
                        try
                        {
                            ZRoutedRpc.instance?.InvokeRoutedRPC(0L /* everybody */, zdo.m_uid, "RPC_HealthChanged", info.MaxHealth);
                            broadcasts++;
                        }
                        catch (Exception) { }
                    }
                    affected++;
                }
                catch (Exception) { }
            }
            return affected;
        }

        private static int DoRemove(List<ZDO> candidates)
        {
            var session = SessionId();
            var man = ZDOMan.instance;
            if (man == null) return 0;
            var affected = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var zdo = candidates[i];
                try
                {
                    if (zdo == null || !zdo.IsValid()) continue;
                    zdo.SetOwner(session);   // DestroyZDO is a silent no-op without ownership
                    man.DestroyZDO(zdo);
                    affected++;
                }
                catch (Exception) { }
            }
            return affected;
        }

        // AP_MassPiece: {int ver, bool dryRun, int action, int matched, int affected, int shipped(<=20),
        //                shipped x (string prefabName, int count)}
        private static void SendMassPiece(long uid, bool dryRun, int action, int matched, int affected,
            Dictionary<string, int> byPrefab)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(dryRun);
            pkg.Write(action);
            pkg.Write(matched);
            pkg.Write(affected);

            var rows = new List<KeyValuePair<string, int>>();
            if (byPrefab != null)
            {
                foreach (var kv in byPrefab) rows.Add(kv);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (rows.Count > PieceBreakdownCap) rows.RemoveRange(PieceBreakdownCap, rows.Count - PieceBreakdownCap);
            }
            pkg.Write(rows.Count);
            for (var i = 0; i < rows.Count; i++) { pkg.Write(rows[i].Key); pkg.Write(rows[i].Value); }
            Reply(uid, "AP_MassPiece", pkg);
        }

        // ==================== 4. protection zones ====================
        //
        // HONEST SCOPE. The server sees ZDOs appear and disappear; it never sees "the player pressed place".
        // So:
        //   * no-build (kind 0 / 2) is enforced by NOTICING a new player-built piece inside a zone on the
        //     next pass (at most every 2 s per zone group) and destroying it, then telling the builder why.
        //     The client DID place it and will see it vanish a moment later. Nothing here prevents the
        //     placement itself; a client-side ghost/preview block is not possible from the server.
        //   * no-damage (kind 1 / 2) cannot be prevented either — WearNTear.ApplyDamage runs on the client
        //     that owns the piece and simply writes s_health. It is enforced as (a) a health-restore sweep
        //     that puts partially damaged pieces back to full within a couple of seconds and (b) a guard
        //     that drops CLIENT-sent destroy messages for protected pieces so a piece that was hammered or
        //     burned down survives (it pops back on the attacker's screen).
        //   * Pieces that already existed when a zone was defined (or when the server started) are
        //     GRANDFATHERED: the first pass over a zone records everything present and never touches it.
        //     Only pieces that appear afterwards are removed.
        //   * Admins are exempt: if the ZDO's current owner is a connected adminlist peer, the piece is
        //     adopted into the baseline instead of destroyed.
        private sealed class ProtZone
        {
            public string Name;
            public Vector3 Pos;
            public float Radius;
            public int Kind;                    // 0 no-build, 1 no-damage, 2 both
            public HashSet<ZDOID> Baseline;     // null = not grandfathered yet
            public bool Disabled;               // too many pieces inside; enforcement gave up
        }

        private static readonly List<ProtZone> Zones = new List<ProtZone>();
        private static string _zoneSignature;
        private static int _zoneCursor;

        // Rebuild the in-memory zone list whenever the persisted table changes (a zone was added/edited, or
        // the world changed under us and FeatureStore handed back a different table). Baselines survive for
        // zones whose name AND geometry are unchanged, so an unrelated edit does not re-grandfather
        // everything.
        private static void EnsureZones()
        {
            Dictionary<string, string> table;
            try { table = FeatureStore.Table("zones"); }
            catch (Exception) { return; }
            if (table == null) return;

            var sig = BuildSignature(table);
            if (sig == _zoneSignature) return;
            _zoneSignature = sig;

            var old = new Dictionary<string, ProtZone>(StringComparer.Ordinal);
            foreach (var z in Zones) old[z.Name] = z;

            Zones.Clear();
            foreach (var kv in table)
            {
                if (Zones.Count >= MaxZones) break;
                var z = ParseZone(kv.Key, kv.Value);
                if (z == null) continue;
                ProtZone prev;
                if (old.TryGetValue(z.Name, out prev) && prev.Kind == z.Kind && prev.Radius == z.Radius &&
                    prev.Pos == z.Pos)
                {
                    z.Baseline = prev.Baseline;
                    z.Disabled = prev.Disabled;
                }
                Zones.Add(z);
            }
            if (_zoneCursor >= Zones.Count) _zoneCursor = 0;
        }

        private static string BuildSignature(Dictionary<string, string> table)
        {
            var parts = new List<string>(table.Count);
            foreach (var kv in table) parts.Add(kv.Key + "=" + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts.ToArray());
        }

        private static ProtZone ParseZone(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) return null;
            var bits = value.Split('|');
            if (bits.Length < 5) return null;
            float x, y, z, r; int kind;
            if (!float.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return null;
            if (!float.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return null;
            if (!float.TryParse(bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return null;
            if (!float.TryParse(bits[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return null;
            if (!int.TryParse(bits[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind)) return null;
            return new ProtZone
            {
                Name = name,
                Pos = new Vector3(x, y, z),
                Radius = Mathf.Clamp(r, 1f, HardMaxRadius),
                Kind = Mathf.Clamp(kind, 0, 2),
            };
        }

        private static string ZoneValue(Vector3 p, float r, int kind) =>
            p.x.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
            p.y.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
            p.z.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
            r.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
            kind.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// True when <paramref name="point"/> lies inside a stored protection zone; reports the strictest
        /// kind found and that zone's name. Answers from the stored table whether or not enforcement is
        /// enabled, so a UI can show "this spot is protected" honestly.
        /// </summary>
        internal static bool IsInsideProtectedZone(Vector3 point, out int kind, out string zoneName)
        {
            kind = -1; zoneName = null;
            try
            {
                EnsureZones();
                for (var i = 0; i < Zones.Count; i++)
                {
                    var z = Zones[i];
                    var dx = point.x - z.Pos.x;
                    var dz = point.z - z.Pos.z;
                    if (dx * dx + dz * dz > z.Radius * z.Radius) continue;
                    if (z.Kind > kind) { kind = z.Kind; zoneName = z.Name; }
                }
            }
            catch (Exception) { }
            return kind >= 0;
        }

        // AP_SrvZoneListReq [Action<long>] -> AP_ZoneList
        private static void OnZoneListReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvZoneListReq")) return;
            EnsureZones();
            SendZoneList(sender);
        }

        // AP_ZoneList: {int ver, int shipped(<=40), shipped x (string name, float x, float y, float z,
        //               float radius, int kind)}
        private static void SendZoneList(long uid)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            var n = Math.Min(Zones.Count, ZoneListCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var z = Zones[i];
                pkg.Write(z.Name);
                pkg.Write(z.Pos.x);
                pkg.Write(z.Pos.y);
                pkg.Write(z.Pos.z);
                pkg.Write(z.Radius);
                pkg.Write(z.Kind);
            }
            Reply(uid, "AP_ZoneList", pkg);
        }

        // AP_SrvZoneSet: ZPackage{string name, float x, float y, float z, float radius, int kind, bool remove}
        private static void OnZoneSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvZoneSet")) return;

            string name; float x, y, z, radius; int kind; bool remove;
            try
            {
                name = pkg.ReadString();
                x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle();
                radius = pkg.ReadSingle(); kind = pkg.ReadInt(); remove = pkg.ReadBool();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvZoneSet: malformed packet dropped ({e.Message})");
                return;
            }

            name = SanitizeZoneName(name);
            if (string.IsNullOrEmpty(name))
            {
                CompanionPlugin.NotifySender(sender, "Protection zone: the name must be 1-48 printable characters.");
                return;
            }
            if (!FeatureStore.Ready)
            {
                CompanionPlugin.NotifySender(sender, "Protection zones unavailable: the server has no world data directory yet.");
                return;
            }

            Dictionary<string, string> table;
            try { table = FeatureStore.Table("zones"); }
            catch (Exception) { table = null; }
            if (table == null)
            {
                CompanionPlugin.NotifySender(sender, "Protection zones unavailable: the zone table could not be read.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            if (remove)
            {
                if (!table.Remove(name))
                {
                    CompanionPlugin.NotifySender(sender, $"No protection zone named '{name}'.");
                    SendZoneList(sender);
                    return;
                }
                FeatureStore.SaveTable("zones");
                _zoneSignature = null;
                EnsureZones();
                CompanionPlugin.SrvAudit(sender, "ZONEREMOVE", $"name={name}");
                Wave1AuditRpc.PostModLog($"PROTECTION ZONE '{name}' removed by {admin}");
                CompanionPlugin.NotifySender(sender, $"Protection zone '{name}' removed.");
                SendZoneList(sender);
                return;
            }

            if (!table.ContainsKey(name) && table.Count >= MaxZones)
            {
                CompanionPlugin.NotifySender(sender, $"Protection zone limit reached ({MaxZones}). Remove one first.");
                SendZoneList(sender);
                return;
            }
            var center = new Vector3(x, y, z);
            if (!CenterOk(sender, center)) { SendZoneList(sender); return; }
            if (float.IsNaN(radius) || float.IsInfinity(radius)) radius = 16f;
            radius = Mathf.Clamp(radius, 1f, HardMaxRadius);
            kind = Mathf.Clamp(kind, 0, 2);

            table[name] = ZoneValue(center, radius, kind);
            FeatureStore.SaveTable("zones");
            _zoneSignature = null;
            EnsureZones();

            var kindLabel = kind == 0 ? "no-build" : kind == 1 ? "no-damage" : "no-build + no-damage";
            CompanionPlugin.SrvAudit(sender, "ZONESET", $"name={name} center={x:0.#}/{y:0.#}/{z:0.#} radius={radius:0.#} kind={kind}");
            Wave1AuditRpc.PostModLog($"PROTECTION ZONE '{name}' set ({kindLabel}, {radius:0} m at {x:0}/{z:0}) by {admin}");
            CompanionPlugin.NotifySender(sender, ZonesEnabled
                ? $"Protection zone '{name}' saved ({kindLabel}, {radius:0} m). Everything already built inside it is grandfathered; only NEW pieces are removed."
                : $"Protection zone '{name}' saved ({kindLabel}, {radius:0} m), but enforcement is OFF (EnableProtectionZones=false) - nothing is being blocked.");
            SendZoneList(sender);
        }

        private static string SanitizeZoneName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Trim().Replace('|', '/').Replace('=', '-').Replace('\r', ' ').Replace('\n', ' ');
            if (s.Length > 48) s = s.Substring(0, 48);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        // ---- the enforcement pass (round-robin, at most every 2 s, time-boxed) ----

        private static void RunZoneEnforcement()
        {
            EnsureZones();
            if (Zones.Count == 0) return;
            if (!EnsurePrefabMap()) return;

            var sw = Stopwatch.StartNew();
            var budget = BudgetMs;
            var session = SessionId();
            var man = ZDOMan.instance;
            if (man == null) return;

            for (var pass = 0; pass < ZonesPerCycle && pass < Zones.Count; pass++)
            {
                if (sw.ElapsedMilliseconds >= budget) break;
                var z = Zones[_zoneCursor];
                _zoneCursor = (_zoneCursor + 1) % Zones.Count;
                if (z.Disabled) continue;

                var n = CollectInRadius(z.Pos, z.Radius, Collected);
                if (n < 0)
                {
                    if (n == -2)
                    {
                        z.Disabled = true;
                        CompanionPlugin.FeatureLog($"Protection zone '{z.Name}' disabled: more than {MaxCollect} objects inside it.");
                    }
                    Collected.Clear();
                    continue;
                }

                if (z.Baseline == null)
                {
                    // First look at this zone: everything present right now is grandfathered forever.
                    z.Baseline = new HashSet<ZDOID>();
                    for (var i = 0; i < Collected.Count; i++)
                    {
                        var info = InfoOf(Collected[i]);
                        if (info == null || !info.IsPiece) continue;
                        if (z.Baseline.Count >= MaxBaselinePerZone) { z.Disabled = true; break; }
                        try { z.Baseline.Add(Collected[i].m_uid); } catch (Exception) { }
                    }
                    if (z.Disabled)
                        CompanionPlugin.FeatureLog($"Protection zone '{z.Name}' disabled: more than {MaxBaselinePerZone} build pieces inside it.");
                    else
                        CompanionPlugin.FeatureLog($"Protection zone '{z.Name}': {z.Baseline.Count} existing piece(s) grandfathered.");
                    Collected.Clear();
                    continue;
                }

                var restores = 0;
                for (var i = 0; i < Collected.Count; i++)
                {
                    var zdo = Collected[i];
                    var info = InfoOf(zdo);
                    if (info == null) continue;
                    ZDOID id;
                    try { if (!zdo.IsValid()) continue; id = zdo.m_uid; }
                    catch (Exception) { continue; }

                    if (info.IsPiece && !z.Baseline.Contains(id))
                    {
                        HandleNewPieceInZone(z, zdo, id, info, session, man);
                        continue;
                    }

                    // no-damage: put partially damaged pieces back to full. Bounded per pass so a big zone
                    // full of chipped walls cannot eat the frame.
                    if (z.Kind >= 1 && info.HasWear && !info.RandomInitialDamage && info.MaxHealth > 0f &&
                        restores < RestoresPerCycle)
                    {
                        try
                        {
                            var cur = zdo.GetFloat(KeyHealth, info.MaxHealth);
                            if (cur < info.MaxHealth)
                            {
                                zdo.SetOwner(session);
                                zdo.Set(KeyHealth, info.MaxHealth);
                                man.ForceSendZDO(id);
                                try { ZRoutedRpc.instance?.InvokeRoutedRPC(0L, id, "RPC_HealthChanged", info.MaxHealth); }
                                catch (Exception) { }
                                restores++;
                            }
                        }
                        catch (Exception) { }
                    }
                }
                Collected.Clear();
            }
        }

        private static void HandleNewPieceInZone(ProtZone z, ZDO zdo, ZDOID id, PrefabInfo info, long session, ZDOMan man)
        {
            try
            {
                // Only player-placed pieces are policed. A world-generated ruin or a location that streamed
                // in after the baseline was taken has creator == 0 and is adopted, never deleted.
                var creator = zdo.GetLong(KeyCreator, 0L);
                if (creator == 0L) { AdoptIntoBaseline(z, id); return; }

                // A no-damage-only zone is not a build restriction: accept the new piece.
                if (z.Kind == 1) { AdoptIntoBaseline(z, id); return; }

                var owner = zdo.GetOwner();
                var peer = owner != 0L ? FindPeer(owner) : null;
                if (peer != null && PeerIsAdmin(peer)) { AdoptIntoBaseline(z, id); return; }

                zdo.SetOwner(session);
                man.DestroyZDO(zdo);
                if (peer != null)
                    Wave1Moderation.SendPlayerText(peer.m_uid, $"This area is protected ({z.Name}) - you cannot build here.");
                CompanionPlugin.FeatureLog(
                    $"Protection zone '{z.Name}': removed newly placed {info.Name ?? "?"} " +
                    $"(builder id {creator}{(peer != null ? ", " + peer.m_playerName : "")}).");
            }
            catch (Exception)
            {
                AdoptIntoBaseline(z, id);   // never loop forever on one problem ZDO
            }
        }

        private static void AdoptIntoBaseline(ProtZone z, ZDOID id)
        {
            if (z.Baseline == null) return;
            if (z.Baseline.Count >= MaxBaselinePerZone)
            {
                z.Disabled = true;
                CompanionPlugin.FeatureLog($"Protection zone '{z.Name}' disabled: baseline exceeded {MaxBaselinePerZone} pieces.");
                return;
            }
            z.Baseline.Add(id);
        }

        // ---- the no-damage destroy guard ----
        //
        // ZDOMan.RPC_DestroyZDO(long sender, ZPackage pkg) is the ONLY way a client tells the server that an
        // object is gone (ZDOMan.cs:654-662; the package is {int count, count x ZDOID}). The prefix rewrites
        // that package, dropping the ids of player-built pieces that sit inside an enforced no-damage zone.
        // Fail-open in every direction: any exception, any disabled switch, any non-client sender lets the
        // original run untouched. The server's own removals arrive with sender = the server's routed-rpc id
        // (ZRoutedRpc.cs:120-135), which is never a connected peer, so undo / cleanup / mass-remove / the
        // zone enforcement above are never blocked by their own guard.
        [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO")]
        internal static class Wave5ZoneDestroyGuard
        {
            private static bool Prefix(long sender, ref ZPackage pkg)
            {
                if (pkg == null) return true;
                if (!ZonesEnabled || !BlockDestroyEnabled) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                var pos = pkg.GetPos();
                try
                {
                    if (Zones.Count == 0) return true;
                    var anyDamageZone = false;
                    for (var i = 0; i < Zones.Count; i++)
                        if (Zones[i].Kind >= 1 && !Zones[i].Disabled) { anyDamageZone = true; break; }
                    if (!anyDamageZone) return true;

                    var peer = FindPeer(sender);
                    if (peer == null) return true;              // the server itself: always allowed
                    if (PeerIsAdmin(peer)) return true;         // admins may demolish inside their own zone

                    var count = pkg.ReadInt();
                    if (count <= 0 || count > 4096) { pkg.SetPos(pos); return true; }

                    var kept = new List<ZDOID>(count);
                    var blocked = 0;
                    for (var i = 0; i < count; i++)
                    {
                        var id = pkg.ReadZDOID();
                        if (IsProtectedPiece(id)) { blocked++; continue; }
                        kept.Add(id);
                    }
                    if (blocked == 0) { pkg.SetPos(pos); return true; }

                    var rebuilt = new ZPackage();
                    rebuilt.Write(kept.Count);
                    for (var i = 0; i < kept.Count; i++) rebuilt.Write(kept[i]);
                    rebuilt.SetPos(0);
                    pkg = rebuilt;

                    Wave1Moderation.SendPlayerText(peer.m_uid, "This area is protected - that structure cannot be destroyed.");
                    return true;
                }
                catch (Exception)
                {
                    try { pkg.SetPos(pos); } catch (Exception) { }
                    return true;   // never break the destroy bus
                }
            }
        }

        private static bool IsProtectedPiece(ZDOID id)
        {
            try
            {
                var man = ZDOMan.instance;
                if (man == null) return false;
                var zdo = man.GetZDO(id);
                if (zdo == null || !zdo.IsValid()) return false;
                var info = InfoOf(zdo);
                if (info == null || !info.IsPiece) return false;
                if (zdo.GetLong(KeyCreator, 0L) == 0L) return false;   // world-generated: not protected
                var p = zdo.GetPosition();
                for (var i = 0; i < Zones.Count; i++)
                {
                    var z = Zones[i];
                    if (z.Kind < 1 || z.Disabled) continue;
                    var dx = p.x - z.Pos.x;
                    var dz = p.z - z.Pos.z;
                    if (dx * dx + dz * dz <= z.Radius * z.Radius) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // ==================== 5. ward management ====================
        //
        // A ward list is world-wide, so unlike everything else in this file it CANNOT be a radius query and
        // it CANNOT be synchronous. It uses ZDOMan.GetAllZDOsWithPrefabIterative (ZDOMan.cs:1126-1168,
        // ~400 sectors per call) driven from Tick with a millisecond budget, exactly like wave 2's scanner.
        // One scan at a time; a second request while one runs is answered immediately with a reason.
        private sealed class WardScan
        {
            public long Requester;
            public List<string> Prefabs;
            public int PrefabIndex;
            public int SectorIndex;
            public readonly List<ZDO> Found = new List<ZDO>();
            public readonly Stopwatch Watch = new Stopwatch();
        }

        private static WardScan _wardScan;

        private static void OnWardListReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWardListReq")) return;

            if (_wardScan != null)
            {
                CompanionPlugin.NotifySender(sender, "A ward scan is already running - try again in a moment.");
                SendWardList(null, sender);
                return;
            }
            if (!EnsurePrefabMap() || _wardPrefabNames == null || _wardPrefabNames.Count == 0)
            {
                CompanionPlugin.NotifySender(sender, "Ward management unavailable: no guard-stone prefab could be identified on this server build.");
                SendWardList(null, sender);
                return;
            }
            if (ZDOMan.instance == null) { SendWardList(null, sender); return; }

            var job = new WardScan { Requester = sender, Prefabs = new List<string>(_wardPrefabNames) };
            job.Watch.Start();
            _wardScan = job;
        }

        private static void StepWardScan()
        {
            var job = _wardScan;
            if (job == null) return;
            var man = ZDOMan.instance;
            if (man == null) { _wardScan = null; SendWardList(null, job.Requester); return; }

            var sw = Stopwatch.StartNew();
            var budget = BudgetMs;
            while (job.PrefabIndex < job.Prefabs.Count && sw.ElapsedMilliseconds < budget)
            {
                bool done;
                try
                {
                    var idx = job.SectorIndex;
                    done = man.GetAllZDOsWithPrefabIterative(job.Prefabs[job.PrefabIndex], job.Found, ref idx);
                    job.SectorIndex = idx;
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Ward scan: prefab '{job.Prefabs[job.PrefabIndex]}' failed ({e.Message}).");
                    done = true;
                }
                if (done) { job.PrefabIndex++; job.SectorIndex = 0; }
                if (job.Found.Count >= WardScanCap) { job.PrefabIndex = job.Prefabs.Count; break; }
            }
            if (job.PrefabIndex < job.Prefabs.Count) return;

            _wardScan = null;
            job.Watch.Stop();
            SendWardList(job, job.Requester);
        }

        // AP_WardList: {int ver, int total, int shipped(<=40),
        //               shipped x (float x, float y, float z, string ownerName, bool enabled, bool permitted)}
        // Ordering: nearest to the requesting admin's last known position first when that position is known
        // (ZNetPeer.GetRefPos, which the client updates continuously). For a request that arrives with no
        // peer — the listen-server host, whose actions bypass routing — the order is whatever the scan
        // produced, i.e. sector order, which is arbitrary from the admin's point of view.
        private static void SendWardList(WardScan job, long uid)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);

            var list = job != null ? job.Found : new List<ZDO>();
            var haveRef = false;
            var refPos = Vector3.zero;
            var peer = FindPeer(uid);
            if (peer != null)
            {
                try { refPos = peer.GetRefPos(); haveRef = true; }
                catch (Exception) { }
            }
            if (haveRef)
            {
                var c = refPos;
                list.Sort((a, b) =>
                {
                    try { return SqrXZ(a, c).CompareTo(SqrXZ(b, c)); }
                    catch (Exception) { return 0; }
                });
            }

            var requesterPlayerId = peer != null ? PlayerIdOfPeer(peer) : 0L;

            pkg.Write(list.Count);
            var n = Math.Min(list.Count, WardListCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var zdo = list[i];
                var p = Vector3.zero;
                var owner = "?";
                var enabled = false;
                var permitted = false;
                try
                {
                    p = zdo.GetPosition();
                    owner = zdo.GetString(KeyCreatorName, "");
                    if (string.IsNullOrEmpty(owner)) owner = "?";
                    enabled = zdo.GetBool(KeyEnabled, false);
                    permitted = requesterPlayerId != 0L && RequesterHasWardAccess(zdo, requesterPlayerId);
                }
                catch (Exception) { }
                pkg.Write(p.x); pkg.Write(p.y); pkg.Write(p.z);
                pkg.Write(owner);
                pkg.Write(enabled);
                pkg.Write(permitted);
            }
            Reply(uid, "AP_WardList", pkg);
        }

        private static float SqrXZ(ZDO zdo, Vector3 c)
        {
            var p = zdo.GetPosition();
            var dx = p.x - c.x;
            var dz = p.z - c.z;
            return dx * dx + dz * dz;
        }

        // "permitted" on the wire = the REQUESTING admin would be allowed to build here, i.e. they are the
        // ward's creator or appear in its permitted list (PrivateArea.HaveLocalAccess). Both comparisons are
        // against the player PROFILE id, which is what the ward stores.
        private static bool RequesterHasWardAccess(ZDO zdo, long playerId)
        {
            try
            {
                if (zdo.GetLong(KeyCreator, 0L) == playerId) return true;
                var count = zdo.GetInt(KeyPermitted, 0);
                if (count <= 0 || count > 256) return false;
                for (var i = 0; i < count; i++)
                    if (zdo.GetLong("pu_id" + i, 0L) == playerId) return true;
            }
            catch (Exception) { }
            return false;
        }

        // AP_SrvWardAction: ZPackage{float x, float y, float z, int action (0 disable, 1 enable, 2 remove)}
        private static void OnWardAction(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWardAction")) return;

            float x, y, z; int action;
            try { x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle(); action = pkg.ReadInt(); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvWardAction: malformed packet dropped ({e.Message})");
                return;
            }
            action = Mathf.Clamp(action, 0, 2);
            var point = new Vector3(x, y, z);
            if (!CenterOk(sender, point)) return;
            if (!EnsurePrefabMap())
            {
                CompanionPlugin.NotifySender(sender, "Ward management unavailable: the server could not read the prefab registry.");
                return;
            }

            var n = CollectInRadius(point, WardActionRadius, Collected);
            if (n < 0)
            {
                CompanionPlugin.NotifySender(sender, "Ward management unavailable: the server could not read the world object store.");
                Collected.Clear();
                return;
            }

            ZDO best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < Collected.Count; i++)
            {
                var info = InfoOf(Collected[i]);
                if (info == null || !info.IsWard) continue;
                var d = SqrXZ(Collected[i], point);
                if (d < bestDist) { bestDist = d; best = Collected[i]; }
            }
            Collected.Clear();

            if (best == null)
            {
                CompanionPlugin.NotifySender(sender, $"No ward found within {WardActionRadius:0} m of that point (it may have been removed already).");
                return;
            }

            var session = SessionId();
            var man = ZDOMan.instance;
            var ownerName = "?";
            var pos = Vector3.zero;
            try
            {
                pos = best.GetPosition();
                ownerName = best.GetString(KeyCreatorName, "");
                if (string.IsNullOrEmpty(ownerName)) ownerName = "?";
            }
            catch (Exception) { }

            var ok = false;
            var verb = action == 0 ? "disabled" : action == 1 ? "enabled" : "removed";
            try
            {
                best.SetOwner(session);   // claim before writing or destroying
                if (action == 2)
                {
                    if (man != null) { man.DestroyZDO(best); ok = true; }
                }
                else
                {
                    best.Set(KeyEnabled, action == 1);
                    if (man != null) man.ForceSendZDO(best.m_uid);
                    ok = true;
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Ward action failed: {e.Message}");
            }

            if (!ok)
            {
                CompanionPlugin.NotifySender(sender, "Ward action failed - see the server log.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var detail = $"action={verb} pos={pos.x:0.#}/{pos.y:0.#}/{pos.z:0.#} owner={ownerName}";
            CompanionPlugin.SrvAudit(sender, "WARDACTION", detail);
            Wave1AuditRpc.PostModLog($"WARD {verb} at {pos.x:0}/{pos.z:0} (owner {ownerName}) by {admin}");
            CompanionPlugin.FeatureLog($"Ward {verb} by {admin}: {detail}");
            CompanionPlugin.NotifySender(sender, action == 2
                ? $"Ward at {pos.x:0}/{pos.z:0} (owner {ownerName}) removed."
                : $"Ward at {pos.x:0}/{pos.z:0} (owner {ownerName}) {verb}. Players standing there see the change within a second or so.");
        }

        // ==================== prefab registry ====================

        private static void EnsureTypes()
        {
            if (_typesProbed) return;
            _typesProbed = true;
            try
            {
                // Resolved by NAME, never by a hard type reference: a future game build that drops one of
                // these must degrade a feature, not fail to JIT this whole class.
                _tPiece = AccessTools.TypeByName("Piece");
                _tWear = AccessTools.TypeByName("WearNTear");
                _tPrivateArea = AccessTools.TypeByName("PrivateArea");
                if (_tWear != null)
                {
                    _fWearHealth = AccessTools.Field(_tWear, "m_health");
                    _fWearRandomDamage = AccessTools.Field(_tWear, "m_randomInitialDamage");
                }
                if (_tPrivateArea != null) _fAreaFaction = AccessTools.Field(_tPrivateArea, "m_ownerFaction");
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave5Area type probe failed: {e.Message}");
            }
        }

        private static bool EnsurePrefabMap()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return false;
            if (_info != null && ReferenceEquals(_mapScene, scene)) return true;

            EnsureTypes();
            if (_tPiece == null && _tWear == null) return false;

            // Full health is the prefab value scaled by the world level, exactly as WearNTear.Awake does
            // (WearNTear.cs:250): m_health += worldLevel * m_worldLevelPieceHPMultiplier * m_health.
            var hpScale = 1f;
            try
            {
                var wl = AccessTools.Field(typeof(Game), "m_worldLevel")?.GetValue(null);
                var mulField = AccessTools.Field(typeof(Game), "m_worldLevelPieceHPMultiplier");
                var mul = Game.instance != null && mulField != null ? mulField.GetValue(Game.instance) : null;
                if (wl != null && mul != null)
                    hpScale = 1f + Convert.ToInt32(wl) * Convert.ToSingle(mul);
                if (hpScale < 1f || float.IsNaN(hpScale) || float.IsInfinity(hpScale)) hpScale = 1f;
            }
            catch (Exception) { hpScale = 1f; }

            var map = new Dictionary<int, PrefabInfo>();
            var wardNames = new List<string>();
            try
            {
                var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")?.GetValue(scene)
                    as Dictionary<int, GameObject>;
                if (named != null)
                {
                    foreach (var kv in named) Register(map, wardNames, kv.Key, kv.Value, hpScale);
                }
                else if (scene.m_prefabs != null)
                {
                    foreach (var go in scene.m_prefabs)
                    {
                        if (go == null) continue;
                        Register(map, wardNames, go.name.GetStableHashCode(), go, hpScale);
                    }
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Wave5Area prefab map unavailable ({e.Message}); area tools will refuse to run.");
                return false;
            }
            if (map.Count == 0) return false;

            _info = map;
            _wardPrefabNames = wardNames;
            _mapScene = scene;
            var pieces = 0;
            foreach (var kv in map) if (kv.Value.IsPiece) pieces++;
            CompanionPlugin.FeatureLog(
                $"Area tools: prefab map built ({map.Count} prefabs, {pieces} build pieces, {wardNames.Count} ward prefab(s), hp scale {hpScale:0.##}).");
            return true;
        }

        private static void Register(Dictionary<int, PrefabInfo> map, List<string> wardNames, int hash,
            GameObject go, float hpScale)
        {
            if (go == null) return;
            var info = new PrefabInfo { Name = go.name };
            try { info.IsPiece = _tPiece != null && go.GetComponent(_tPiece) != null; } catch (Exception) { }
            try
            {
                if (_tWear != null)
                {
                    var w = go.GetComponent(_tWear);
                    if (w != null)
                    {
                        info.HasWear = true;
                        if (_fWearHealth != null) info.MaxHealth = Convert.ToSingle(_fWearHealth.GetValue(w)) * hpScale;
                        if (_fWearRandomDamage != null) info.RandomInitialDamage = Convert.ToBoolean(_fWearRandomDamage.GetValue(w));
                    }
                }
            }
            catch (Exception) { info.MaxHealth = 0f; }
            try
            {
                if (_tPrivateArea != null)
                {
                    var pa = go.GetComponent(_tPrivateArea);
                    if (pa != null)
                    {
                        // Character.Faction.Players == 0. Camps and Dvergr bases also use PrivateArea with a
                        // non-player faction; those are world furniture, not player wards, and are excluded.
                        var faction = _fAreaFaction != null ? Convert.ToInt32(_fAreaFaction.GetValue(pa)) : 0;
                        if (faction == 0 && info.IsPiece)
                        {
                            info.IsWard = true;
                            wardNames.Add(go.name);
                        }
                    }
                }
            }
            catch (Exception) { }
            map[hash] = info;
        }

        private static PrefabInfo InfoOf(ZDO zdo)
        {
            if (zdo == null || _info == null) return null;
            try
            {
                PrefabInfo info;
                return _info.TryGetValue(zdo.GetPrefab(), out info) ? info : null;
            }
            catch (Exception) { return null; }
        }

        // ==================== small helpers ====================

        private static long SessionId()
        {
            try { return ZDOMan.GetSessionID(); }
            catch (Exception) { return 0L; }
        }

        private static ZNetPeer FindPeer(long uid)
        {
            if (uid == 0L || ZNet.instance == null) return null;
            try
            {
                var peers = ZNet.instance.GetPeers();
                if (peers == null) return null;
                foreach (var p in peers) if (p != null && p.m_uid == uid) return p;
            }
            catch (Exception) { }
            return null;
        }

        private static bool PeerIsAdmin(ZNetPeer peer)
        {
            try
            {
                var host = peer?.m_socket?.GetHostName();
                return !string.IsNullOrEmpty(host) && CompanionPlugin.FeatureIsAdminId(host);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The stable PLAYER PROFILE id behind a connected peer: peer.m_characterID names the player's ZDO
        /// (ZNet.cs:1812) and that ZDO carries ZDOVars.s_playerID ("playerID", Player.cs:667). Returns 0 when
        /// the character has not spawned yet, in which case creator rewrites are skipped rather than guessed.
        /// </summary>
        private static long PlayerIdOfPeer(ZNetPeer peer)
        {
            try
            {
                if (peer == null || ZDOMan.instance == null) return 0L;
                var cid = peer.m_characterID;
                if (cid.IsNone()) return 0L;
                var zdo = ZDOMan.instance.GetZDO(cid);
                if (zdo == null || !zdo.IsValid()) return 0L;
                return zdo.GetLong(KeyPlayerId, 0L);
            }
            catch (Exception) { return 0L; }
        }

        // Every server->admin reply in this file funnels through here. ReplyTo hands the payload to the
        // panel in-process when the requester IS this process (listen-server host, where a routed reply has
        // no server peer to authenticate against and the panel's anti-spoof gate correctly discards it) and
        // falls back to the ordinary routed send for a remote admin.
        private static void Reply(long uid, string rpc, ZPackage pkg)
        {
            CompanionPlugin.ReplyTo(uid, rpc, pkg);
        }
    }
}

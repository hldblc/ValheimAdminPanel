using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace AdminPanelCompanion
{
    // ==================== Feature-module core (companion side) ====================
    // Additive counterpart to the panel's Features\FeaturesCore.cs. Owns: the [Features] config, the
    // admin-action chokepoint (audit trail + tiered-role enforcement for EVERY AP_Srv* RPC, current and
    // future, without touching a single existing handler), and the Update() lifecycle slot for wave
    // timers (the main file defines no Update, so this partial can claim it).
    public partial class CompanionPlugin
    {
        internal static ConfigEntry<bool> AuditEnabledCfg;
        internal static ConfigEntry<bool> RolesEnabledCfg;
        private static bool _featuresInited;

        // methodHash -> RPC name, for every server-side admin ACTION. Periodic reads (AP_SrvVersion,
        // AP_SrvInfoReq/ListsReq/JoinLogReq) are deliberately absent: they fire on 10-30s timers and
        // would drown the audit log in noise while revealing nothing an admin did.
        private static readonly Dictionary<int, string> AuditedRpcs = new Dictionary<int, string>();

        // Built-in role grants. Owner is a wildcard. An adminlist admin with NO explicit role entry is
        // treated as Owner so enabling roles never strips existing admins of anything by surprise —
        // restrictions are always an explicit assignment.
        private static readonly Dictionary<string, HashSet<string>> BuiltinRoles =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["moderator"] = new HashSet<string>
                {
                    "AP_SrvKick", "AP_SrvBan", "AP_SrvBanId", "AP_SrvUnban", "AP_SrvTeleport",
                    "AP_SrvHeal", "AP_SrvBroadcast", "AP_SrvMsg", "AP_SrvReqInv", "AP_SrvInvRemove",
                },
                ["builder"] = new HashSet<string>
                {
                    "AP_SrvGive", "AP_SrvSpawn", "AP_SrvUndo", "AP_SrvTeleport", "AP_SrvHeal",
                },
            };

        private void FeaturesInit()
        {
            if (_featuresInited) return;
            _featuresInited = true;

            AuditEnabledCfg = Config.Bind("Features", "EnableAuditLog", true,
                "Log every admin action (who/when/what) to BepInEx/config/AdminPanelCompanion/<world>/audit.log. Passive: changes no behavior.");
            RolesEnabledCfg = Config.Bind("Features", "EnableTieredRoles", false,
                "Enforce per-admin roles (owner/moderator/builder/custom) on admin actions. Off = every adminlist admin keeps full power, exactly as before.");

            foreach (var name in new[]
            {
                "AP_SrvGive", "AP_SrvSpawn", "AP_SrvReqInv", "AP_SrvUndo", "AP_SrvTeleport",
                "AP_SrvHeal", "AP_SrvKick", "AP_SrvBan", "AP_SrvUnban", "AP_SrvBroadcast",
                "AP_SrvMsg", "AP_SrvEvent", "AP_SrvPeaceful", "AP_SrvInvRemove", "AP_SrvSkillRaise",
                "AP_SrvApplySE", "AP_SrvSkipNight", "AP_SrvSaveWorld", "AP_SrvBanId",
            })
                AuditedRpcs[name.GetStableHashCode()] = name;

            try { Harmony.CreateAndPatchAll(typeof(AdminActionChokepoint)); }
            catch (Exception e) { Logger.LogWarning($"Admin-action chokepoint patch failed (audit/roles unavailable): {e.Message}"); }

            // Isolated per wave: FeaturesInit() runs from Awake, so an escaping exception would fail the
            // whole companion — on a dedicated server that means NO admin RPCs at all, including the
            // pre-existing ones. One broken feature must not cost an operator their admin tooling.
            try { FeaturesInitSrvWave1(); } catch (Exception e) { WaveInitFailed("1 (moderation/chat/audit)", e); }
            try { FeaturesInitSrvWave2(); } catch (Exception e) { WaveInitFailed("2 (world/backup/ops)", e); }
            try { FeaturesInitSrvWave3(); } catch (Exception e) { WaveInitFailed("3 (core/discord)", e); }
            try { FeaturesInitSrvWave4(); } catch (Exception e) { WaveInitFailed("4 (vault/player data)", e); }
            try { FeaturesInitSrvWave5(); } catch (Exception e) { WaveInitFailed("5 (area/portals)", e); }
            try { FeaturesInitSrvWave6(); } catch (Exception e) { WaveInitFailed("6 (economy/events/slots)", e); }
            try { FeaturesInitSrvWave7(); } catch (Exception e) { WaveInitFailed("7 (guard/SDK)", e); }
            try { FeaturesInitSrvExtra(); } catch (Exception e) { WaveInitFailed("extras", e); }
            // Round 2 (2026-09): four groups, each its own partial so one can be dropped by deleting its files.
            try { FeaturesInitSrvWave8Objects(); } catch (Exception e) { WaveInitFailed("8 (world objects)", e); }
            try { FeaturesInitSrvWave8Toolkit(); } catch (Exception e) { WaveInitFailed("8 (toolkit)", e); }
            try { FeaturesInitSrvWave8Rules(); } catch (Exception e) { WaveInitFailed("8 (server rules)", e); }
            try { FeaturesInitSrvWave8Systems(); } catch (Exception e) { WaveInitFailed("8 (systems)", e); }
        }

        private void WaveInitFailed(string wave, Exception e) =>
            Logger.LogWarning($"Feature wave {wave} failed to initialise (its RPCs are unavailable; the rest of the companion is unaffected): {e}");

        // Lifecycle slot for wave timers (announcement scheduler, temp-ban expiry, anti-cheat sampling,
        // backups). The main file has no Update; ticks must themselves guard on ZNet/world state.
        private void Update()
        {
            if (!_featuresInited) return;
            AuditTick();
            FeaturesTickSrvWave1();
            FeaturesTickSrvWave2();
            FeaturesTickSrvWave3();
            FeaturesTickSrvWave4();
            FeaturesTickSrvWave5();
            FeaturesTickSrvWave6();
            FeaturesTickSrvWave7();
            FeaturesTickSrvWave8Objects();
            FeaturesTickSrvWave8Toolkit();
            FeaturesTickSrvWave8Rules();
            FeaturesTickSrvWave8Systems();
        }

        // ---- shared services for wave files ----

        // Waves call this when registering their own admin RPCs so the chokepoint covers them too.
        internal static void RegisterAuditedRpc(string name, string minRoleGrant = null)
        {
            AuditedRpcs[name.GetStableHashCode()] = name;
            if (minRoleGrant != null && BuiltinRoles.TryGetValue(minRoleGrant, out var set)) set.Add(name);
        }

        // Rich audit entries for wave features (the chokepoint only knows the RPC name; a wave knows the
        // target and parameters). Format: ISO-utc|senderUid|senderId|senderName|action|detail
        internal static void SrvAudit(long sender, string action, string detail)
        {
            if (AuditEnabledCfg == null || !AuditEnabledCfg.Value) return;
            AuditEnqueue(
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|{sender}|{AuditField(SenderPlatformId(sender))}|{AuditField(SenderDisplayName(sender))}|{AuditField(action)}|{AuditDetail(detail)}");
        }

        // The row is '|'-delimited and the panel's reader takes fixed column indices, so one pipe in a
        // player-controlled field (m_playerName, the socket host name) shifts every later column — a player
        // named "Bob|BAN|target=Odin" would otherwise choose the Action he is recorded under. Every
        // non-trailing field goes through here.
        private static string AuditField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        }

        // Detail is the trailing field and wave callers use '|' inside it as a sub-delimiter (the reader
        // folds the tail back together), so only newlines — which would forge a whole extra row — and
        // runaway length are stripped.
        private static string AuditDetail(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > 400) s = s.Substring(0, 400);
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        // The chokepoint below writes from inside the routed-RPC prefix on the main thread, and any
        // connected player can make it fire at packet rate, so a file open/append/close per row stalls the
        // simulation. Rows are queued in memory and written in one batch from Update.
        private static readonly List<string> AuditQueue = new List<string>();
        private const int AuditFlushLines = 50;    // flush ahead of the timer once the queue reaches this
        private const int AuditQueueCap = 500;     // no Tick running (shutdown): write inline rather than grow
        private static DateTime _auditNextFlush;

        private static void AuditEnqueue(string row)
        {
            bool full;
            lock (AuditQueue) { AuditQueue.Add(row); full = AuditQueue.Count >= AuditQueueCap; }
            if (full) AuditFlush();
        }

        private static void AuditFlush()
        {
            List<string> batch;
            lock (AuditQueue)
            {
                if (AuditQueue.Count == 0) return;
                batch = new List<string>(AuditQueue);
                AuditQueue.Clear();
            }
            FeatureStore.AppendBatch("audit", batch);
        }

        private static void AuditTick()
        {
            var now = DateTime.UtcNow;
            if (now < _auditNextFlush)
            {
                int n;
                lock (AuditQueue) n = AuditQueue.Count;
                if (n < AuditFlushLines) return;
            }
            _auditNextFlush = now.AddSeconds(1);
            AuditFlush();
            if (DeniedAudits.Count > 0) PruneDeniedAudits(now);
        }

        // A non-admin can invoke any audited RPC name, so "one audit row per packet" is a remote write
        // amplifier. Admin actions are still logged every time; a non-admin's denials collapse to one row
        // per sender per window, carrying the count of what was folded into it.
        private class DeniedAuditState { internal DateTime Next; internal int Suppressed; }
        private static readonly Dictionary<long, DeniedAuditState> DeniedAudits =
            new Dictionary<long, DeniedAuditState>();
        private const double DeniedAuditWindowSeconds = 60.0;

        private static void AuditDenial(long sender, string action)
        {
            var now = DateTime.UtcNow;
            DeniedAuditState st;
            if (!DeniedAudits.TryGetValue(sender, out st)) DeniedAudits[sender] = st = new DeniedAuditState();
            if (now < st.Next)
            {
                if (st.Suppressed < int.MaxValue) st.Suppressed++;
                return;
            }
            var folded = st.Suppressed > 0 ? $" ({st.Suppressed} suppressed)" : "";
            st.Suppressed = 0;
            st.Next = now.AddSeconds(DeniedAuditWindowSeconds);
            AuditRow(sender, action, "DENIED-NOT-ADMIN" + folded);   // stays one field: the row shape is unchanged
        }

        private static void PruneDeniedAudits(DateTime now)
        {
            List<long> gone = null;
            foreach (var kv in DeniedAudits)
                if (kv.Value.Suppressed == 0 && now >= kv.Value.Next)
                    (gone ?? (gone = new List<long>())).Add(kv.Key);
            if (gone != null) foreach (var k in gone) DeniedAudits.Remove(k);
        }

        // Chokepoint row: same six fields as SrvAudit, with the verdict as the trailing field. The peer
        // lookups happen here so a throttled denial costs no peer-list walk at all.
        private static void AuditRow(long sender, string action, string verdict)
        {
            AuditEnqueue(
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|{sender}|{AuditField(SenderPlatformId(sender))}|{AuditField(SenderDisplayName(sender))}|{AuditField(action)}|{verdict}");
        }

        // The gate every NEW admin RPC handler uses: adminlist first (identical to today), then roles
        // only when enforcement is on. Existing handlers keep their SenderIsAdmin call untouched — for
        // them role enforcement happens in the chokepoint below, before the handler ever runs.
        internal static bool SenderCanFeature(long sender, string action)
        {
            if (!SenderIsAdmin(sender)) return false;
            if (RolesEnabledCfg == null || !RolesEnabledCfg.Value) return true;
            return RoleAllows(SenderPlatformId(sender), action);
        }

        internal static string SenderPlatformId(long sender)
        {
            if (IsLocalHostSender(sender)) return "HOST";
            var peer = ZNet.instance?.GetPeer(sender);
            return peer?.m_socket?.GetHostName() ?? "?";
        }

        internal static string SenderDisplayName(long sender)
        {
            if (IsLocalHostSender(sender)) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "HOST";
            var peer = ZNet.instance?.GetPeer(sender);
            return peer?.m_playerName ?? "?";
        }

        internal static void NotifySender(long sender, string text)
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(sender, "AP_Msg", text); }
            catch (Exception) { }
        }

        // ---- reply delivery ----

        // Every server->admin reply goes through here instead of calling InvokeRoutedRPC directly.
        // On a listen-server host the requesting admin IS this process, and a routed packet would arrive at
        // the panel with no server peer to authenticate it against (ServerUid() == 0 there), so the panel's
        // anti-spoof gate — correctly — throws it away. Handing the payload to the panel in-process skips
        // the packet entirely: no sender to verify, nothing a remote player can reach. Remote admins are
        // unaffected and still get the ordinary routed reply.
        private static System.Reflection.MethodInfo _localReply;
        private static bool _localReplyResolved;

        internal static void ReplyTo(long sender, string name, ZPackage pkg)
        {
            if (IsLocalHostSender(sender) && TryLocalDeliver(name, pkg)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(sender, name, pkg); }
            catch (Exception e) { FeatureLog($"Reply {name} could not be sent: {e.Message}"); }
        }

        private static bool TryLocalDeliver(string name, ZPackage pkg)
        {
            if (!_localReplyResolved)
            {
                _localReplyResolved = true;
                // Resolved by name: the companion never references the panel assembly, because it also ships
                // on dedicated servers where no panel exists.
                _localReply = AccessTools.Method("AdminPanel.AdminPanelLocalBridge:LocalReply");
                if (_localReply == null)
                    FeatureLog("Panel local-reply bridge not found; host replies fall back to the routed path.");
            }
            if (_localReply == null) return false;
            try { return _localReply.Invoke(null, new object[] { name, pkg }) is bool ok && ok; }
            catch (Exception) { return false; }
        }

        // Bridge the main file's private helpers to the standalone feature classes (Wave*Srv files are
        // plain static classes, not partials, so they can't reach private members directly).
        internal static bool FeatureSenderIsAdmin(long sender) => SenderIsAdmin(sender);
        internal static bool FeatureKick(long uid) => KickByUid(uid);
        internal static string FeatureBareId(string id) => BareId(id);
        internal static void FeatureLog(string msg) => Log(msg);

        // Adminlist membership by platform id (for join-time gates where no peer uid exists yet).
        // SyncedList's surface has shifted across game versions, so probe Contains then GetList().
        internal static bool FeatureIsAdminId(string hostId)
        {
            if (string.IsNullOrEmpty(hostId)) return false;
            // Game-rule matching first (Steam_/bare/V_ forms, see AdminListContains); the reflective scan
            // below is the last resort for a SyncedList whose surface has shifted.
            try { if (AdminListContains(hostId)) return true; } catch (Exception) { }
            try
            {
                var list = AccessTools.Field(typeof(ZNet), "m_adminList")?.GetValue(ZNet.instance);
                if (list == null) return false;
                var bare = BareId(hostId);
                var contains = AccessTools.Method(list.GetType(), "Contains", new[] { typeof(string) });
                if (contains != null)
                    return (bool)contains.Invoke(list, new object[] { hostId })
                        || (bool)contains.Invoke(list, new object[] { bare });
                if (AccessTools.Method(list.GetType(), "GetList")?.Invoke(list, null) is List<string> raw)
                    return raw.Contains(hostId) || raw.Contains(bare);
            }
            catch (Exception) { }
            return false;
        }

        // roles.txt: <platformId>=<role> (full id as stored by the panel, or bare — both match, same
        // leniency as the adminlist check). The HOST pseudo-id can be assigned a role too, though the
        // host's own local actions bypass routing and thus enforcement (documented limitation).
        private static string ResolveRole(string platformId)
        {
            if (string.IsNullOrEmpty(platformId) || platformId == "?") return null;
            var roles = FeatureStore.Table("roles");
            if (roles.TryGetValue(platformId, out var r)) return r;
            var bare = BareId(platformId);
            return roles.TryGetValue(bare, out r) ? r : null;
        }

        private static bool RoleAllows(string platformId, string action)
        {
            var role = ResolveRole(platformId);
            if (role == null || role.Equals("owner", StringComparison.OrdinalIgnoreCase))
                return true;   // unassigned admins stay fully powered (see BuiltinRoles comment)
            if (BuiltinRoles.TryGetValue(role, out var set) && set.Contains(action)) return true;
            // Custom roles: roleperms.txt maps <role>=<comma-separated RPC names>; "*" is a wildcard.
            var perms = FeatureStore.Table("roleperms");
            if (perms.TryGetValue(role, out var list))
            {
                if (list.Trim() == "*") return true;
                foreach (var a in list.Split(','))
                    if (a.Trim() == action) return true;
            }
            return false;
        }

        // One prefix covers every admin action, current and future: audit + role enforcement at the
        // routing chokepoint. Runs AFTER RouteRpcSanitizer (Priority.Low vs its default Normal), so the
        // sender long at offset 8 is already re-stamped and trustworthy. Fail-open by design: any
        // exception here must never break the RPC bus, and non-AP hashes pass through untouched.
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPriority(Priority.Low)]
        private static class AdminActionChokepoint
        {
            private static bool Prefix(ZRpc rpc, ZPackage pkg)
            {
                if (!_featuresInited || ZNet.instance == null || !ZNet.instance.IsServer()) return true;
                var pos = pkg.GetPos();
                try
                {
                    // Wire layout (ZRoutedRpc.RoutedRPCData): 0 msgID(8) | 8 sender(8) | 16 target(8) |
                    // 24 targetZDO(12) | 36 methodHash(4). Position is restored on every path.
                    pkg.SetPos(8);
                    var sender = pkg.ReadLong();
                    pkg.SetPos(36);
                    var hash = pkg.ReadInt();
                    pkg.SetPos(pos);

                    if (!AuditedRpcs.TryGetValue(hash, out var action)) return true;

                    var isAdmin = SenderIsAdmin(sender);
                    var roleOk = !isAdmin || !RolesEnabledCfg.Value || RoleAllows(SenderPlatformId(sender), action);

                    if (AuditEnabledCfg.Value)
                    {
                        if (isAdmin) AuditRow(sender, action, roleOk ? "ALLOWED" : "DENIED-ROLE");
                        else AuditDenial(sender, action);   // throttled: see DeniedAudits
                    }

                    if (isAdmin && !roleOk)
                    {
                        Log($"DENIED {action} from {SenderDisplayName(sender)} ({SenderPlatformId(sender)}): role forbids it");
                        NotifySender(sender, "You don't have permission for that action (role restriction).");
                        return false;   // drop before the handler; non-admins fall through to the handler's own gate as always
                    }
                    return true;
                }
                catch (Exception)
                {
                    try { pkg.SetPos(pos); } catch (Exception) { }
                    return true;
                }
            }
        }

        // ---- wave hook declarations (implemented by wave files; calls vanish if a wave is absent) ----
        partial void FeaturesInitSrvWave1();
        partial void FeaturesInitSrvWave2();
        partial void FeaturesInitSrvWave3();
        partial void FeaturesInitSrvWave4();
        partial void FeaturesInitSrvWave5();
        partial void FeaturesInitSrvWave6();
        partial void FeaturesInitSrvWave7();
        partial void FeaturesInitSrvExtra();

        partial void FeaturesTickSrvWave1();
        partial void FeaturesTickSrvWave2();
        partial void FeaturesTickSrvWave3();
        partial void FeaturesTickSrvWave4();
        partial void FeaturesTickSrvWave5();
        partial void FeaturesTickSrvWave6();
        partial void FeaturesTickSrvWave7();

        // ---- round 2 (wave 8) hooks: one group per glue file (FeaturesWave8ObjectsGlue.cs etc.) ----
        partial void FeaturesInitSrvWave8Objects();
        partial void FeaturesInitSrvWave8Toolkit();
        partial void FeaturesInitSrvWave8Rules();
        partial void FeaturesInitSrvWave8Systems();

        partial void FeaturesTickSrvWave8Objects();
        partial void FeaturesTickSrvWave8Toolkit();
        partial void FeaturesTickSrvWave8Rules();
        partial void FeaturesTickSrvWave8Systems();
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class CompanionPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.halitb.adminpanelcompanion";
        public const string PluginName = "AdminPanelCompanion";
        // Version policy: lockstep with the panel — both DLLs of a release always carry the SAME number,
        // and the panel warns in-game when the server's companion doesn't match (AP_SrvVersion handshake).
        public const string PluginVersion = "2.5.6";
        // The Valheim release this build was compiled and reflection-swept against (leading major.minor.patch of
        // global::Version.GetVersionString(false), which carries a platform prefix such as "l-1.0.12" on Linux
        // servers). A mismatch at runtime is logged once and reported in the health payload; it never disables
        // anything — the bind probe below is what tells whether the mismatch actually broke something.
        internal const string CompiledForGameVersion = "1.0.16";

        internal static CompanionPlugin Instance;

        // Undo history, keyed by admin peer id. Deliberately NOT one shared "last batch" as before: that had two
        // problems on a multi-admin server — one admin's Undo deleted whichever admin had spawned most recently,
        // and it could only ever step back a single spawn. Depth is bounded so a long session cannot grow without
        // limit, and entries for peers that have disconnected are pruned whenever a new batch is pushed.
        private const int UndoDepth = 20;
        private static readonly Dictionary<long, List<List<ZDOID>>> UndoHistory =
            new Dictionary<long, List<List<ZDOID>>>();

        // Recognising the host by its session id is only safe while the sanitizer is re-stamping incoming senders.
        private static bool SenderSanitizerActive;

        private void Awake()
        {
            Instance = this;
            Harmony.CreateAndPatchAll(typeof(RpcRegistration));
            try { Harmony.CreateAndPatchAll(typeof(RouteRpcSanitizer)); SenderSanitizerActive = true; }
            catch (Exception e) { Logger.LogWarning($"RoutedRPC sender-sanitizer patch failed (server security reduced): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(PeerJoinLogPatch)); }
            catch (Exception e) { Logger.LogWarning($"Peer join-log patch failed (join/leave history unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(PeerLeaveLogPatch)); }
            catch (Exception e) { Logger.LogWarning($"Peer leave-log patch failed (join/leave history unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(SaveTimestampPatch)); }
            catch (Exception e) { Logger.LogWarning($"Save-timestamp patch failed (last-save time unavailable): {e.Message}"); }
            // 2.5.5: drop the client-bound list RPCs on the server (vanilla lets any peer rewrite the live adminlist).
            try
            {
                Harmony.CreateAndPatchAll(typeof(BlockInboundAdminListPatch));
                Harmony.CreateAndPatchAll(typeof(BlockInboundPlayerListPatch));
                Harmony.CreateAndPatchAll(typeof(BlockInboundHistoricalListPatch));
                InboundListGuardActive = true;
            }
            catch (Exception e) { Logger.LogWarning($"Inbound list-RPC guard patch failed (a modified client could rewrite this server's adminlist over the network): {e.Message}"); }
            FeaturesInit();   // additive feature modules (Features*.cs); safe no-op if none are compiled in
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
            // Health self-check, after FeaturesInit so every feature type is loaded and registered. Neither step
            // can disable anything: the version note is one warning line, the probe reports bind failures.
            WarnIfGameVersionDiffers();
            StartBindProbe();
        }

        private static void Log(string msg) => Instance?.Logger.LogInfo(msg);

        // ---------- health: game version, bind probe, AP_HealthData payload ----------

        // The running game's version string as the game reports it ("1.0.12", "l-1.0.12" on Linux servers).
        private static string GameVersion
        {
            get
            {
                try { return global::Version.GetVersionString(false) ?? "unknown"; }
                catch (Exception) { return "unknown"; }
            }
        }

        // Steam build id of app 892970 that the DLL was built against, stamped by the csproj as
        // AssemblyMetadata("ValheimSteamBuild", ...); "unknown" when the attribute is absent (older build scripts).
        private static string _steamBuild;
        private static string CompiledForSteamBuild
        {
            get
            {
                if (_steamBuild != null) return _steamBuild;
                var v = "unknown";
                try
                {
                    foreach (var a in typeof(CompanionPlugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                    {
                        var m = a as System.Reflection.AssemblyMetadataAttribute;
                        if (m != null && m.Key == "ValheimSteamBuild" && !string.IsNullOrEmpty(m.Value)) { v = m.Value; break; }
                    }
                }
                catch (Exception) { }
                return _steamBuild = v;
            }
        }

        // Leading "major.minor.patch" of a version string, skipping any platform prefix ("l-1.0.12" -> "1.0.12").
        // A missing patch component reads as 0 ("1.0" -> "1.0.0"), matching GameVersion.ToString(), which omits it.
        internal static string LeadingVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
            var parts = s.Substring(i, j - i).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            var major = parts[0];
            var minor = parts.Length > 1 ? parts[1] : "0";
            var patch = parts.Length > 2 ? parts[2] : "0";
            return major + "." + minor + "." + patch;
        }

        // Contract rule 6: a mismatch is ONE warning line, never a disable. The bind probe decides whether the
        // mismatch matters; a game patch that touched nothing the companion uses still binds cleanly.
        private void WarnIfGameVersionDiffers()
        {
            var game = GameVersion;
            var running = LeadingVersion(game);
            var built = LeadingVersion(CompiledForGameVersion);
            if (running.Length == 0)
                Logger.LogWarning($"{PluginName} {PluginVersion} could not read the game version (built for Valheim {CompiledForGameVersion}); the bind probe below tells whether that matters.");
            else if (running != built)
                Logger.LogWarning($"{PluginName} {PluginVersion} was built for Valheim {CompiledForGameVersion} but is running on Valheim {game}. Nothing is disabled; the bind probe below reports any member that no longer exists.");
        }

        // BindProbe.Start runs the type initializers on this (main) thread and hands the JIT loop to a worker;
        // the report is written from the main thread by a coroutine so the worker never touches the BepInEx
        // logger or a Unity object. The result is also carried in the AP_HealthData handshake (HealthSummary).
        private void StartBindProbe()
        {
            try { BindProbe.Start(); }
            catch (Exception e) { Logger.LogWarning($"Bind probe could not start: {e.GetType().Name}: {e.Message}"); return; }
            if (BindProbe.Finished) { LogBindProbeReport(); return; }   // phase 1 already failed; nothing to wait for
            try { StartCoroutine(BindProbeReportWhenDone()); }
            catch (Exception e) { Logger.LogWarning($"Bind probe report coroutine could not start (result still reaches the panel via AP_HealthData): {e.GetType().Name}: {e.Message}"); }
        }

        private IEnumerator BindProbeReportWhenDone()
        {
            // The JIT loop takes well under a second on a dedicated server; the cap only guards against a wedged
            // worker (the payload then keeps saying probe=skipped, which the panel shows as "not checked").
            const float pollSeconds = 0.25f, capSeconds = 120f;
            float waited = 0f;
            while (!BindProbe.Finished && waited < capSeconds)
            {
                yield return new WaitForSecondsRealtime(pollSeconds);
                waited += pollSeconds;
            }
            if (BindProbe.Finished) LogBindProbeReport();
            else Logger.LogWarning($"Bind probe did not finish within {capSeconds:0} s; no self-check result this session (probe=skipped).");
        }

        private void LogBindProbeReport()
        {
            try { BindProbe.LogReport(Logger, GameVersion, CompiledForGameVersion, PluginName + " " + PluginVersion); }
            catch (Exception e) { Logger.LogWarning($"Bind probe report failed: {e.GetType().Name}: {e.Message}"); }
        }

        // SHARED CONTRACT health payload, key=value pairs joined by ';' in this exact order:
        //   game=<Version.GetVersionString(false)>;built=<CompiledForGameVersion>;steam=<ValheimSteamBuild|unknown>;
        //   probe=<ok|failed|skipped>;checked=<int>;failed=<int>;names=<up to 8 "Type.Method" joined by ','>
        // Sent to the asker right after AP_VersionData (OnServerVersionReq), which appends ";admin=0|1" for that
        // asker (2.5.5; not part of this property, a host is admin by definition); read by reflection on a listen host.
        // probe=skipped until the worker has finished, so an early handshake may say skipped — the panel re-asks
        // on its next handshake. Cheap: the probe never re-runs, only the string is rebuilt.
        public static string HealthSummary
        {
            get
            {
                var r = BindProbe.Current;
                var names = "";
                if (r != null && r.FailedNames.Length > 0)
                {
                    var clean = new string[r.FailedNames.Length];
                    for (int i = 0; i < clean.Length; i++) clean[i] = PayloadSafe(r.FailedNames[i]);
                    names = string.Join(",", clean);
                }
                return "game=" + PayloadSafe(GameVersion)
                     + ";built=" + PayloadSafe(CompiledForGameVersion)
                     + ";steam=" + PayloadSafe(CompiledForSteamBuild)
                     + ";probe=" + BindProbe.State
                     + ";checked=" + (r != null ? r.Checked : 0)
                     + ";failed=" + (r != null ? r.Failed : 0)
                     + ";names=" + names
                     + ";listguard=" + (InboundListGuardActive ? "1" : "0");
            }
        }

        // The payload's own separators may not appear inside a value.
        private static string PayloadSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(";", "").Replace(",", "").Replace("=", "").Replace("\r", "").Replace("\n", " ").Trim();
        }

        [HarmonyPatch]
        private static class RpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // server-side entry points (validated against adminlist.txt)
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvGive", OnServerGive);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSpawn", OnServerSpawn);
                ZRoutedRpc.instance.Register<long>("AP_SrvReqInv", OnServerRequestInventory);
                ZRoutedRpc.instance.Register("AP_SrvUndo", new Action<long>(OnServerUndo));
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTeleport", OnServerTeleport);
                ZRoutedRpc.instance.Register<long>("AP_SrvHeal", OnServerHeal);
                ZRoutedRpc.instance.Register<long>("AP_SrvKick", OnServerKick);
                ZRoutedRpc.instance.Register<long>("AP_SrvBan", OnServerBan);
                ZRoutedRpc.instance.Register<string>("AP_SrvUnban", OnServerUnban);
                ZRoutedRpc.instance.Register<string>("AP_SrvBroadcast", OnServerBroadcast);
                ZRoutedRpc.instance.Register<long, string>("AP_SrvMsg", OnServerMessage);
                ZRoutedRpc.instance.Register<string, Vector3>("AP_SrvEvent", OnServerEvent);
                ZRoutedRpc.instance.Register<bool>("AP_SrvPeaceful", OnServerPeaceful);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvInvRemove", OnServerInvRemove);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSkillRaise", OnServerSkillRaise);
                ZRoutedRpc.instance.Register<long, int>("AP_SrvApplySE", OnServerApplyStatusEffect);
                ZRoutedRpc.instance.Register("AP_SrvVersion", new Action<long>(OnServerVersionReq));
                ZRoutedRpc.instance.Register("AP_SrvSkipNight", new Action<long>(OnServerSkipNight));
                // 2.4.0 — server-truth suite (all admin-gated, reply only to the requesting admin)
                ZRoutedRpc.instance.Register("AP_SrvInfoReq", new Action<long>(OnServerInfoReq));
                ZRoutedRpc.instance.Register("AP_SrvListsReq", new Action<long>(OnServerListsReq));
                ZRoutedRpc.instance.Register("AP_SrvJoinLogReq", new Action<long>(OnServerJoinLogReq));
                ZRoutedRpc.instance.Register("AP_SrvSaveWorld", new Action<long>(OnServerSaveWorld));
                ZRoutedRpc.instance.Register<string>("AP_SrvBanId", OnServerBanId);
                // 2.5.6 - ask instead of guess: a target's skill table, a player's real position
                ZRoutedRpc.instance.Register<long>("AP_SrvSkillReq", OnServerSkillReq);
                ZRoutedRpc.instance.Register<long>("AP_SrvPlayerPos", OnServerPlayerPos);

                // client-side executors (only accepted when sent by the server)
                ZRoutedRpc.instance.Register<string, int, int, string>("AP_GiveItem", OnGiveItem);
                ZRoutedRpc.instance.Register<ZPackage>("AP_RemoveItem", OnRemoveItem);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SkillRaise", OnSkillRaise);
                ZRoutedRpc.instance.Register<long>("AP_InvRequest", OnInventoryRequest);
                ZRoutedRpc.instance.Register<Vector3>("AP_Teleport", OnTeleport);
                ZRoutedRpc.instance.Register("AP_HealSelf", new Action<long>(OnHealSelf));
                ZRoutedRpc.instance.Register<int>("AP_ApplySE", OnApplyStatusEffect);
                ZRoutedRpc.instance.Register<string>("AP_Msg", OnMessage);
                ZRoutedRpc.instance.Register<long>("AP_SkillReq", OnSkillReq);
            }
        }

        // Server-side hard authentication of the routed-RPC sender. Valheim's ZRoutedRpc.RPC_RoutedRPC reads
        // m_senderPeerID straight from the packet the client sent and NEVER re-stamps it with the id of the real
        // transport connection, so a malicious client can forge sender==<any admin> (privilege escalation on the
        // AP_Srv* handlers) or sender==<server> (impersonating the server toward other clients on AP_Teleport/etc.).
        // We patch the server's receive handler and overwrite the forged sender in-place with the verified uid of
        // the socket that actually delivered the packet BEFORE it is dispatched/relayed. This closes both holes for
        // every AP_* (and every other) routed RPC. On clients this is a no-op (the delivering rpc is the server).
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        private static class RouteRpcSanitizer
        {
            private static void Prefix(ZRpc rpc, ZPackage pkg)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // only the server relays/dispatches
                if (pkg == null) return;
                long realUid = 0L;
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_rpc == rpc) { realUid = peer.m_uid; break; }
                if (realUid == 0L) return; // unknown/local connection — nothing to verify against
                // RoutedRPCData layout: long m_msgID, long m_senderPeerID, long m_targetPeerID, ...
                // so m_senderPeerID is the second long, at byte offset 8.
                var saved = pkg.GetPos();
                try
                {
                    pkg.SetPos(8); // skip m_msgID
                    var claimed = pkg.ReadLong();
                    if (claimed != realUid)
                    {
                        pkg.SetPos(8);
                        pkg.Write(realUid); // overwrite the forged sender in place (same width, same length)
                    }
                }
                catch { /* malformed packet — leave it for the game's own handler to reject */ }
                finally { pkg.SetPos(saved); }
            }
        }

        // ---------- security ----------
        private static bool IsDedicatedServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static long ServerUid()
        {
            var peer = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            return peer != null ? peer.m_uid : 0L;
        }

        // A host (single-player or listen-server) is never in ZNet.m_peers — that list is filled only from
        // OnNewConnection, i.e. remote sockets — so it cannot be resolved by peer lookup and ServerUid() is
        // structurally 0 for it (GetServerPeer() returns null when IsServer()). Both checks below therefore have
        // to recognise the host by its own session id, which is what ZRoutedRpc stamps on a locally dispatched
        // packet. A remote client cannot forge it: RouteRpcSanitizer overwrites the sender of every
        // socket-delivered packet with the real peer uid before dispatch.
        // Gated on the sanitizer: if that patch ever fails to apply, an incoming packet's sender is attacker-chosen,
        // so the host's session id would be forgeable and this would become a privilege escalation.
        private static bool IsLocalHostSender(long sender) =>
            SenderSanitizerActive && ZNet.instance != null && ZNet.instance.IsServer() &&
            ZDOMan.instance != null && sender == ZDOMan.GetSessionID();

        private static bool SenderIsServer(long sender)
        {
            var s = ServerUid();
            if (s != 0L && sender == s) return true;   // client: the packet really came from the server peer
            return IsLocalHostSender(sender);          // host: we are the server
        }

        private static string BareId(string host) =>
            !string.IsNullOrEmpty(host) && host.Contains("_") ? host.Substring(host.IndexOf('_') + 1) : host;

        private static SyncedList GetList(string field) =>
            AccessTools.Field(typeof(ZNet), field)?.GetValue(ZNet.instance) as SyncedList;

        // Adminlist membership with the GAME's own matching rules. A Steam socket reports the bare
        // SteamID64 while adminlist.txt may hold "Steam_<id>", "<id>" or the display form "V_<id>" that
        // hosting panels write; vanilla ZNet.IsAdmin -> ListContainsId accepts all three, so a hand-rolled
        // Contains(host)/Contains(BareId(host)) pair denied real admins (2.5.3 fix, GPortal report).
        // The old pair stays as a fallback in case IsAdmin ever moves or throws on a future game build.
        internal static bool AdminListContains(string host)
        {
            if (string.IsNullOrEmpty(host) || ZNet.instance == null) return false;
            try { if (ZNet.instance.IsAdmin(host)) return true; }
            catch (Exception e) { Log($"ZNet.IsAdmin failed for {host}, falling back to list scan: {e.Message}"); }
            var adminList = GetList("m_adminList");
            return adminList != null && (adminList.Contains(host) || adminList.Contains(BareId(host)));
        }

        // logDenied=false is for QUERIES (the AP_SrvVersion handshake asks "am I an admin?"): a non-admin
        // pressing F7 is not an admin action and must not be logged as one.
        private static bool SenderIsAdmin(long sender, bool logDenied = true)
        {
            if (ZNet.instance == null) return false;
            // The host is implicitly admin, exactly as the engine treats it in ZNet.LocalPlayerIsAdminOrHost().
            // Without this the peer lookup below returns null for the host and denies every admin action before
            // adminlist.txt is ever consulted, which is why adding your own id to the list had no effect.
            if (IsLocalHostSender(sender)) return true;
            var peer = ZNet.instance.GetPeer(sender);
            var host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (string.IsNullOrEmpty(host)) { if (logDenied) Log($"DENIED admin action from unresolvable peer {sender}"); return false; }

            var isAdmin = AdminListContains(host);
            if (!isAdmin && logDenied) Log($"DENIED admin action from non-admin {host} (peer {sender})");
            return isAdmin;
        }

        // ---------- relay targets ----------
        // Every relay below forwards an admin action to ONE player's client, addressed by peer uid, and two uids
        // must never go out. 0 is ZRoutedRpc.Everybody: InvokeRoutedRPC dispatches it locally AND routes it to
        // every peer (ZRoutedRpc.cs:130), so a panel that sent 0 - a dead player's roster row has no character id,
        // hence uid 0, until they respawn (Game._RequestRespawn) - handed an item, a teleport or a skill raise to
        // the WHOLE server. And a uid that is not connected, which the relay would drop without a word.
        // The listen host is a valid target although it is never in m_peers.
        // needsMod: the executor is a companion RPC, which a vanilla client ignores without a trace - so the target
        //   must be KNOWN to run the companion (the Item Forge rule). Not needed for the admin's own client, which
        //   just sent this request, nor for the listen host, which is this very process.
        // needsCharacter: every executor but the message acts on the target's Player. The server learns of a death
        //   at once (Game._RequestRespawn -> SetCharacterID(None) -> ZNet.RPC_CharacterID) while the admin's roster
        //   can lag ~2 s; a grant landing in that gap vanished (the executor returns without a Player).
        // quiet: for timer-driven requests, where a refusal every few seconds would spam the admin's screen.
        private static bool RelayTargetOk(long sender, long targetUid, string action, bool needsMod,
            bool needsCharacter = true, bool quiet = false)
        {
            const string noCharacter = "That player has no character right now (dead or still loading). Nothing was sent.";
            if (targetUid == 0L)
            {
                Log($"{action}: refused a relay to peer 0 from {sender} (0 addresses every player)");
                if (!quiet) RelayNotice(sender, targetUid, noCharacter);
                return false;
            }
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;
            if (IsLocalHostTarget(targetUid)) return true;
            if (ZNet.instance.IsServer() && !ZNet.instance.IsDedicated() && ZDOMan.instance != null &&
                targetUid == ZDOMan.GetSessionID())
            {
                // The listen host itself, between death and respawn (no local Player).
                if (!needsCharacter) return true;
                if (!quiet) RelayNotice(sender, targetUid, noCharacter);
                return false;
            }
            var peer = ZNet.instance.GetPeer(targetUid);
            if (peer == null)
            {
                if (!quiet) RelayNotice(sender, targetUid, "That player is no longer online. Nothing was sent.");
                return false;
            }
            if (needsCharacter && peer.m_characterID.IsNone())
            {
                if (!quiet) RelayNotice(sender, targetUid, noCharacter);
                return false;
            }
            if (needsMod && targetUid != sender)
            {
                var has = Wave34Core.HasMod(targetUid);
                if (has == true) return true;
                // Ask (again) either way: "false" is the verdict after three unanswered probes in the first ~90 s,
                // and nothing probes after that, so a modded client that took longer to load into the world would
                // be refused for the whole session. A late answer always wins (Wave34Core.OnCapReply).
                Reprobe(targetUid);
                if (has == null && quiet) return true;   // a read: if they cannot answer, the panel says so itself
                if (!quiet)
                    RelayNotice(sender, targetUid, has == null
                        ? $"Not sent yet: still checking whether {peer.m_playerName} runs AdminPanelCompanion.dll, which applies items, " +
                          "skills, status effects and inventories on their side. Try again in a few seconds."
                        : $"Not delivered: {peer.m_playerName} has not answered as running AdminPanelCompanion.dll, which applies items, " +
                          "skills, status effects and inventories on their side. Checking again now - if they do run it, try once " +
                          "more in a few seconds.");
                return false;
            }
            return true;
        }

        // A refused "All skills" click is 24 relays in one frame: one notice and one probe per burst, not 24.
        private static long _noticeSender, _noticeTarget, _probedTarget;
        private static string _noticeText;
        private static float _noticeAt, _probedAt;

        private static void RelayNotice(long sender, long targetUid, string text)
        {
            var now = Time.realtimeSinceStartup;
            if (sender == _noticeSender && targetUid == _noticeTarget && text == _noticeText && now - _noticeAt < 3f) return;
            _noticeSender = sender; _noticeTarget = targetUid; _noticeText = text; _noticeAt = now;
            NotifySender(sender, text);
        }

        private static void Reprobe(long targetUid)
        {
            var now = Time.realtimeSinceStartup;
            if (targetUid == _probedTarget && now - _probedAt < 3f) return;
            _probedTarget = targetUid; _probedAt = now;
            try { Wave34Core.ProbePeer(targetUid); } catch (Exception) { }
        }

        // The listen-server host's own player, which a relay reaches by its session id (dispatched in-process).
        private static bool IsLocalHostTarget(long uid) =>
            ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null &&
            Player.m_localPlayer != null && uid == ZDOMan.GetSessionID();

        // ---------- server: give / spawn / inventory ----------
        private static void OnServerGive(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string prefabName, crafter; int amount, quality;
            try
            {
                targetUid = pkg.ReadLong();
                prefabName = pkg.ReadString();
                amount = pkg.ReadInt();
                quality = pkg.ReadInt();
                crafter = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvGive: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(prefabName) || amount <= 0) return;
            if (!RelayTargetOk(sender, targetUid, "AP_SrvGive", needsMod: true)) return;
            amount = Mathf.Min(amount, 100000); // guard against a client freeze from an absurd stack loop
            Log($"Admin {sender} gives {amount}x {prefabName} (q{quality}) to peer {targetUid}");
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_GiveItem", prefabName, amount, quality, crafter);
        }

        private static void OnServerSpawn(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            int kind, count, levelOrQuality; string prefabName, petName; Vector3 pos; bool tamed;
            try
            {
                kind = pkg.ReadInt();          // 0 = item drop, 1 = creature
                prefabName = pkg.ReadString();
                pos = pkg.ReadVector3();
                count = pkg.ReadInt();
                levelOrQuality = pkg.ReadInt();
                tamed = pkg.ReadBool();
                petName = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvSpawn: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(prefabName)) return;
            // Hard server-side caps: never trust the client UI to bound these — a huge count would run millions of
            // synchronous Instantiate/ZDO creations on the main thread and freeze/OOM the dedicated server.
            count = Mathf.Clamp(count, 0, 100);

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null) { Log($"AP_SrvSpawn: prefab '{prefabName}' not found"); return; }

            Log($"Admin {sender} spawns {count}x {prefabName} (kind {kind}) at {pos}");
            var batch = new List<ZDOID>();

            if (kind == 0)
            {
                var drop = prefab.GetComponent<ItemDrop>();
                var maxStack = drop != null ? drop.m_itemData.m_shared.m_maxStackSize : 1;
                if (maxStack < 1) maxStack = 1; // guard: a 0 max-stack would loop forever
                var remaining = count;
                while (remaining > 0)
                {
                    var stack = Mathf.Min(remaining, maxStack);
                    remaining -= stack;
                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    RememberSpawn(go, batch);
                    var d = go.GetComponent<ItemDrop>();
                    if (d != null)
                    {
                        d.m_itemData.m_stack = stack;
                        d.m_itemData.m_quality = Mathf.Clamp(levelOrQuality, 1, d.m_itemData.m_shared.m_maxQuality);
                        d.m_itemData.m_worldLevel = Game.m_worldLevel;   // as vanilla's own spawn command sets it (OnCreateNew)
                        d.m_itemData.m_durability = d.m_itemData.GetMaxDurability();
                    }
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var offset = new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0.5f, UnityEngine.Random.Range(-1.5f, 1.5f));
                    var go = UnityEngine.Object.Instantiate(prefab, pos + offset, Quaternion.identity);
                    RememberSpawn(go, batch);
                    var character = go.GetComponent<Character>();
                    if (character != null)
                    {
                        if (levelOrQuality > 1) character.SetLevel(Mathf.Clamp(levelOrQuality, 1, 10));
                        if (tamed) character.SetTamed(true);
                        if (tamed && !string.IsNullOrEmpty(petName))
                        {
                            var nview = go.GetComponent<ZNetView>();
                            nview?.GetZDO()?.Set("TamedName", petName);
                        }
                    }
                }
            }

            // One spawn action = one undo step, recorded after the whole batch exists so a partially-built
            // batch can never be popped.
            PushUndo(sender, batch);
        }

        private static void RememberSpawn(GameObject go, List<ZDOID> batch)
        {
            var nview = go.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null) batch.Add(zdo.m_uid);
        }

        /// <summary>Record a completed spawn batch as one undo step for this admin.</summary>
        private static void PushUndo(long sender, List<ZDOID> batch)
        {
            if (batch == null || batch.Count == 0) return;   // nothing spawned = nothing to step back over
            PruneUndoHistory();

            if (!UndoHistory.TryGetValue(sender, out var stack))
            {
                stack = new List<List<ZDOID>>();
                UndoHistory[sender] = stack;
            }
            stack.Add(batch);
            if (stack.Count > UndoDepth) stack.RemoveAt(0);   // drop the oldest step, keep the newest UndoDepth
        }

        // Forget history for peers that are no longer connected. Peer ids are session-scoped, so without this a
        // long-lived server would accumulate a stack per admin who ever joined.
        private static void PruneUndoHistory()
        {
            if (UndoHistory.Count == 0 || ZNet.instance == null) return;
            List<long> dead = null;
            foreach (var kv in UndoHistory)
                // The host is never in ZNet.m_peers — only OnNewConnection fills it — so GetPeer() returns null
                // for our own session id. Without this exemption a host's history would be pruned on the very
                // next spawn (PushUndo prunes before it pushes) and multi-step undo would silently never work
                // in single-player, while testing fine against a dedicated server.
                if (!IsLocalHostSender(kv.Key) && ZNet.instance.GetPeer(kv.Key) == null)
                    (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var id in dead) UndoHistory.Remove(id);
        }

        private static void OnServerUndo(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;

            if (!UndoHistory.TryGetValue(sender, out var stack) || stack.Count == 0)
            {
                Log($"Admin {sender} undo: nothing left to undo");
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg", "Nothing left to undo");
                return;
            }

            var batch = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) UndoHistory.Remove(sender);

            var removed = 0;
            foreach (var id in batch)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) continue;   // already gone (killed, despawned, or undone by a world reload)
                var go = ZNetScene.instance.FindInstance(zdo);
                var nview = go != null ? go.GetComponent<ZNetView>() : null;
                if (nview != null) { nview.ClaimOwnership(); nview.Destroy(); removed++; }
                else
                {
                    // The no-instance path is the NORMAL one on a dedicated server: the server never
                    // instantiates creatures — the nearest client simulates them and owns their ZDO.
                    // ZDOMan.DestroyZDO is a SILENT NO-OP unless the caller owns the ZDO (it only queues
                    // m_destroySendList behind an IsOwner() check), which is why undo "worked" in the log
                    // (removed++) while the creature kept standing on every client. Claim ownership on the
                    // raw ZDO first and the destroy actually broadcasts.
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                    removed++;
                }
            }

            var left = stack.Count;
            Log($"Admin {sender} undo: removed {removed} spawned objects ({left} step(s) left)");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg",
                $"Undo: removed {removed} object(s) — {left} step(s) left");
        }

        private static void OnServerRequestInventory(long sender, long targetUid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            // Target 0 made EVERY client send its inventory to this admin.
            if (!RelayTargetOk(sender, targetUid, "AP_SrvReqInv", needsMod: true)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_InvRequest", sender);
        }

        // ==================== 2.4.0: server-truth suite ====================
        // Everything below reports what the SERVER knows — the panel's local numbers are only that
        // client's view of the world (its loaded ZDOs, its FPS), which on a dedicated server is a
        // fraction of the truth.

        private static long _lastSaveTicksUtc;   // last completed save, UTC ticks (0 = none yet)

        // Reliable "last save" signal. NOT ZNet.WorldSaveFinished: that fires from PrintWorldSaveMessage,
        // which calls MessageHud.instance.MessageAll FIRST — and MessageHud.instance is null on a headless
        // dedicated server, so on the exact machine this feature targets the finished event may never fire.
        // SaveWorldThread IS the worker that writes the file; its postfix runs when the write completes, on
        // the worker thread (an aligned long store is atomic, no lock needed), for manual AND autosaves.
        [HarmonyPatch(typeof(ZNet), "SaveWorldThread")]
        private static class SaveTimestampPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _lastSaveTicksUtc = DateTime.UtcNow.Ticks;
        }

        // Join/leave history, server-side so it survives any admin's relog. Ring-buffered: a long-running
        // server must not grow it without bound.
        private const int JoinLogCap = 200;
        private static readonly List<(long unix, string name, string host, bool joined)> JoinLog =
            new List<(long, string, string, bool)>();
        // Peers we've logged a JOIN for, by uid. Pairs join↔leave so (a) the double ZNet.Disconnect a kick
        // produces can't log two "left" lines, and (b) a leave is only recorded for a peer that actually
        // joined — rejected connections (bad password / banned) that trip RPC_PeerInfo without ever entering
        // this set produce no phantom pair.
        private static readonly HashSet<long> _loggedPeers = new HashSet<long>();

        private static void RecordPeerEvent(ZNetPeer peer, bool joined)
        {
            if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) return;
            if (joined) { if (!_loggedPeers.Add(peer.m_uid)) return; }   // already logged this join
            else { if (!_loggedPeers.Remove(peer.m_uid)) return; }       // no matching join → skip
            var host = peer.m_socket != null ? BareId(peer.m_socket.GetHostName()) : "";
            JoinLog.Add((DateTimeOffset.UtcNow.ToUnixTimeSeconds(), peer.m_playerName, host ?? "", joined));
            if (JoinLog.Count > JoinLogCap) JoinLog.RemoveAt(0);
        }

        // RPC_PeerInfo is where a connecting peer's name becomes known (the handshake fills
        // peer.m_playerName just before this postfix runs). It fires on clients too — the IsServer gate
        // keeps the log server-authoritative. A connection rejected inside RPC_PeerInfo is torn down
        // immediately; if it never got a name it is filtered by RecordPeerEvent.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class PeerJoinLogPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!__instance.IsServer()) return;
                foreach (var peer in __instance.GetPeers())
                    if (peer != null && peer.m_rpc == rpc) { RecordPeerEvent(peer, true); return; }
            }
        }

        // Vanilla registers the client-bound list RPCs ("AdminList", "PlayerList", "HistoricalPlayerList") on
        // EVERY peer connection, server side included, with no IsServer guard (ZNet.RPC_PeerInfo, 1.0.14
        // ZNet.cs:1126-1128). On the server m_adminListForRpc is the SyncedList's own live list
        // (ZNet.cs:375 m_adminListForRpc = m_adminList.GetList()), so a modified client that invokes
        // "AdminList" with its own id REPLACES the server's in-memory adminlist.txt until the file changes
        // on disk — every adminlist check in the game and in this companion would then say yes. A server never
        // legitimately receives any of the three; drop them there. Clients (and the host's own receive path
        // for its in-process copies) are untouched because IsServer() is false for a remote client.
        [HarmonyPatch(typeof(ZNet), "RPC_AdminList")]
        private static class BlockInboundAdminListPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ZNet __instance, ZRpc rpc) => !DropInboundListRpc(__instance, rpc, "AdminList");
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PlayerList")]
        private static class BlockInboundPlayerListPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ZNet __instance, ZRpc rpc) => !DropInboundListRpc(__instance, rpc, "PlayerList");
        }

        [HarmonyPatch(typeof(ZNet), "RPC_HistoricalPlayerList")]
        private static class BlockInboundHistoricalListPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ZNet __instance, ZRpc rpc) => !DropInboundListRpc(__instance, rpc, "HistoricalPlayerList");
        }

        private static readonly HashSet<long> _listRpcWarned = new HashSet<long>();
        internal static bool InboundListGuardActive;   // true once the three prefixes above are applied (Awake)

        private static bool DropInboundListRpc(ZNet znet, ZRpc rpc, string name)
        {
            if (znet == null || !znet.IsServer()) return false;
            try
            {
                ZNetPeer peer = null;
                if (rpc != null)
                    foreach (var p in znet.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }   // GetPeer(ZRpc) is private; same walk as PeerJoinLogPatch
                var uid = peer != null ? peer.m_uid : 0L;
                if (_listRpcWarned.Add(uid))
                    Log($"DROPPED inbound '{name}' RPC from peer {uid} ({(peer != null ? peer.m_playerName : "?")} / {(peer?.m_socket != null ? peer.m_socket.GetHostName() : "?")}): a server never receives this; a stock client never sends it (adminlist tampering attempt)");
            }
            catch (Exception) { /* logging only */ }
            return true;
        }

        // Prefix, because Disconnect tears the peer down — the name is still readable here. A kick makes the
        // game call Disconnect twice on the same peer; the pairing set in RecordPeerEvent absorbs the second.
        [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
        private static class PeerLeaveLogPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (!__instance.IsServer()) return;
                RecordPeerEvent(peer, false);
                if (peer != null) _panelNotified.Remove(peer.m_uid);
            }
        }

        private static int ServerZdoCount()
        {
            try
            {
                var f = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
                return f?.GetValue(ZDOMan.instance) is System.Collections.ICollection c ? c.Count : -1;
            }
            catch { return -1; }
        }

        private static void OnServerInfoReq(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var pkg = new ZPackage();
            pkg.Write(1);                                   // payload version, for future shape changes
            pkg.Write(Time.realtimeSinceStartup);           // server process uptime (s)
            pkg.Write(ServerZdoCount());                    // authoritative world object count
            var lastSave = System.Threading.Interlocked.Read(ref _lastSaveTicksUtc);
            pkg.Write(lastSave == 0 ? -1f : (float)(DateTime.UtcNow - new DateTime(lastSave, DateTimeKind.Utc)).TotalSeconds);
            var next = -1f;                                  // seconds until autosave; Game owns the timer
            if (Game.instance != null) next = Mathf.Max(0f, Game.m_saveInterval - Game.instance.m_saveTimer);
            pkg.Write(next);

            // Per-peer: name, bare id, ping. GetConnectionQuality is socket-level truth; a backend that
            // doesn't implement it just reports -1 for that peer instead of failing the whole reply.
            // Capped like every other list here (total + shipped): an uncapped count could exceed the
            // client's bound and make it discard the WHOLE reply — uptime, save timers and all.
            var peers = ZNet.instance.GetPeers();
            const int peerCap = 64;
            pkg.Write(peers.Count);
            pkg.Write(Math.Min(peers.Count, peerCap));
            for (var i = 0; i < peers.Count && i < peerCap; i++)
            {
                var p = peers[i];
                pkg.Write(p?.m_playerName ?? "?");
                pkg.Write(p?.m_socket != null ? BareId(p.m_socket.GetHostName()) : "");
                var ping = -1;
                try
                {
                    if (p?.m_socket != null)
                    { p.m_socket.GetConnectionQuality(out _, out _, out ping, out _, out _); }
                }
                catch { ping = -1; }
                pkg.Write(ping);
            }

            // Server plugin roster — the "what is ACTUALLY running here" answer that would have caught
            // every stale-companion incident instantly. Capped: a heavily modded server can carry 100+.
            var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos;
            var names = new List<string>();
            foreach (var kv in plugins)
                if (kv.Value?.Metadata != null)
                    names.Add(kv.Value.Metadata.Name + "|" + kv.Value.Metadata.Version);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            const int cap = 60;
            pkg.Write(names.Count);
            pkg.Write(Math.Min(names.Count, cap));
            for (var i = 0; i < names.Count && i < cap; i++) pkg.Write(names[i]);

            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_ServerInfo", pkg);
        }

        private static void WriteSyncedList(ZPackage pkg, string field, int cap)
        {
            var list = GetList(field)?.GetList();
            if (list == null) { pkg.Write(0); pkg.Write(0); return; }
            pkg.Write(list.Count);
            pkg.Write(Math.Min(list.Count, cap));
            for (var i = 0; i < list.Count && i < cap; i++) pkg.Write(list[i] ?? "");
        }

        private static void OnServerListsReq(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            WriteSyncedList(pkg, "m_adminList", 200);
            WriteSyncedList(pkg, "m_bannedList", 200);
            WriteSyncedList(pkg, "m_permittedList", 200);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_AccessLists", pkg);
        }

        private static void OnServerJoinLogReq(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            var start = Math.Max(0, JoinLog.Count - 100);   // newest 100 is plenty for the panel
            pkg.Write(JoinLog.Count - start);
            for (var i = start; i < JoinLog.Count; i++)
            {
                var e = JoinLog[i];
                pkg.Write(e.unix); pkg.Write(e.name); pkg.Write(e.host); pkg.Write(e.joined);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_JoinLog", pkg);
        }

        private static void OnServerSaveWorld(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            // Refuse to stack a save. SaveWorld does a blocking Join on any live save thread, and routed-RPC
            // handlers run on the main thread — so kicking a second save while the autosave worker is still
            // writing would freeze the whole server until the first finishes. IsSaving() gates that.
            if (ZNet.instance.IsSaving())
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg", "A save is already in progress");
                return;
            }
            Log($"Admin {sender} requested world save");
            // waitForNextFrame:true → the game defers the actual write to a coroutine on the next frame
            // rather than running it inline in this RPC handler, matching every vanilla non-dedicated call site.
            ZNet.instance.Save(false, false, true);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg", "World save started");
        }

        // Ban by raw ID — the existing AP_SrvBan needs a CONNECTED peer; this one covers offline players.
        // Stores the id AS ENTERED (trimmed): the game's ban check compares the full networkUserId, so
        // stripping the platform prefix would silently no-op crossplay (Xbox/PlayStation) bans. Admins paste
        // whatever their ban source gives them — a bare SteamID64 or a full "Platform_id".
        private static void OnServerBanId(long sender, string id)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var clean = id?.Trim();
            if (string.IsNullOrEmpty(clean) || clean.Contains(" ")) return;
            var banned = GetList("m_bannedList");
            if (banned == null) return;
            if (!banned.Contains(clean)) banned.Add(clean);
            Log($"Admin {sender} banned id {clean} (offline ban)");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg", $"Banned {clean}");
        }

        // ---------- server: player admin ----------
        private static void OnServerTeleport(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; Vector3 pos;
            try
            {
                targetUid = pkg.ReadLong();
                pos = pkg.ReadVector3();
            }
            catch (Exception e) { Log($"AP_SrvTeleport: malformed packet dropped ({e.Message})"); return; }
            // Target 0 summoned the whole server to the admin.
            if (!RelayTargetOk(sender, targetUid, "AP_SrvTeleport", needsMod: false)) return;
            Log($"Admin {sender} teleports peer {targetUid} to {pos}");
            // The game's own RPC_TeleportPlayer (Chat.cs:130, registered on every client, no sender check) moves a
            // player whether or not their client runs this mod; the old AP_Teleport executor silently ignored
            // anyone without it. Same for the notice: ShowMessage is MessageHud's, on every client.
            // Keep the player's facing, as the old executor did: the server holds their character ZDO.
            var character = IsLocalHostTarget(targetUid)
                ? Player.m_localPlayer.GetZDOID()
                : ZNet.instance.GetPeer(targetUid).m_characterID;
            var zdo = !character.IsNone() && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(character) : null;
            var rot = zdo != null ? zdo.GetRotation() : Quaternion.identity;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "RPC_TeleportPlayer", pos + Vector3.up, rot, true);
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "ShowMessage", (int)MessageHud.MessageType.Center, "An admin teleported you!");
        }

        private static void OnServerHeal(long sender, long targetUid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            // Target 0 healed every player on the server.
            if (!RelayTargetOk(sender, targetUid, "AP_SrvHeal", needsMod: false)) return;
            // Vanilla path first, so a player without the mod is healed too: Character.RPC_Heal on their own
            // character (registered for every character, applied by its owner - the player's client). The server
            // knows that character's id from the peer (ZNet.RPC_CharacterID). The large amount is clamped to max
            // health by the game, and the damage-text popup is off: it would show that raw number.
            var character = IsLocalHostTarget(targetUid)
                ? Player.m_localPlayer.GetZDOID()
                : ZNet.instance.GetPeer(targetUid).m_characterID;
            if (character.IsNone())
            {
                NotifySender(sender, "That player has no character right now (dead or still loading). Nothing was sent.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, character, "RPC_Heal", 1000000f, false);
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "ShowMessage", (int)MessageHud.MessageType.Center, "An admin healed you!");
        }

        // Look up the real network host id (Steam ID) of a connected peer by its uid.
        // uid 0 never names a player: it is what a client sends for a roster row without a character id (a dead or
        // loading player), and vanilla keeps a brand-new connection in m_peers with m_uid still 0 until its
        // PeerInfo passes the password check (ZNet.OnNewConnection adds it first, RPC_PeerInfo sets the uid). A
        // peer lookup by 0 therefore found whoever sat at the password prompt - Kick/Ban hit an innocent joiner.
        private static string HostOfPeer(long uid)
        {
            if (ZNet.instance == null || uid == 0L) return null;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.m_uid == uid)
                    return peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            return null;
        }

        // Kick a peer by uid. Returns true if a matching peer was found and kicked.
        private static bool KickByUid(long uid)
        {
            if (ZNet.instance == null || uid == 0L) return false;   // see HostOfPeer: 0 matched a pre-auth peer
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid != uid) continue;
                var m = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) })
                        ?? AccessTools.Method(typeof(ZNet), "Kick", new[] { typeof(ZNetPeer) });
                if (m != null) m.Invoke(ZNet.instance, new object[] { peer });
                else peer.m_rpc?.GetSocket()?.Close();
                return true;
            }
            return false;
        }

        private static void OnServerKick(long sender, long uid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var ok = KickByUid(uid);
            Log($"Admin {sender} kick peer {uid}: {(ok ? "kicked" : "peer not found")}");
        }

        private static void OnServerBan(long sender, long uid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var host = HostOfPeer(uid);          // read the Steam ID BEFORE kicking (socket closes)
            var bare = BareId(host);
            var banned = GetList("m_bannedList");
            if (banned != null)
            {
                // Store BOTH forms. The bare id keeps every pre-existing bannedlist.txt entry working
                // (earlier versions only ever wrote bare ids), while the full "Platform_id" is what
                // actually matches a crossplay peer — an Xbox/PlayStation id is not a bare SteamID64,
                // so storing only the stripped form made bans through the roster button silently no-op.
                // OnServerUnban already removes both forms, so this stays symmetric.
                if (!string.IsNullOrEmpty(bare) && !banned.Contains(bare)) banned.Add(bare);
                if (!string.IsNullOrEmpty(host) && host != bare && !banned.Contains(host)) banned.Add(host);
            }
            var ok = KickByUid(uid);
            Log($"Admin {sender} ban peer {uid} ({host ?? "unknown"}): {(ok ? "kicked" : "peer not found")}");
        }

        private static void OnServerUnban(long sender, string host)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var banned = GetList("m_bannedList");
            var bare = BareId(host);
            if (banned != null)
            {
                // Match on the BARE form of every stored entry rather than on the two literal spellings of
                // what the admin typed. Bans now store both "Steam_765..." and "765...", so removing only
                // the entered string and its bare form leaves the OTHER spelling behind and the player
                // stays banned — which is exactly what happens when an admin unbans using the bare id.
                var list = banned.GetList();
                if (list != null)
                    foreach (var entry in new List<string>(list))
                        if (BareId(entry) == bare) banned.Remove(entry);
                banned.Remove(host);   // belt and braces for an entry BareId cannot normalise
            }
            Log($"Admin {sender} unbans {bare} (all stored spellings)");
        }

        private static void OnServerBroadcast(long sender, string text)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            Log($"Admin broadcast: {text}");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "AP_Msg", text);
        }

        private static void OnServerMessage(long sender, long targetUid, string text)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (string.IsNullOrEmpty(text)) return;
            // Target 0 turned a private message into a broadcast.
            // No character needed: MessageHud shows it on the respawn screen too.
            if (!RelayTargetOk(sender, targetUid, "AP_SrvMsg", needsMod: false, needsCharacter: false)) return;
            // MessageHud's ShowMessage is on every client, so a player without the mod reads it too (AP_Msg,
            // the companion's own executor, reached only modded clients). Same "[Server] " look as AP_Msg.
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "ShowMessage", (int)MessageHud.MessageType.Center, "[Server] " + text);
        }

        private static void OnServerEvent(long sender, string eventName, Vector3 pos)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (RandEventSystem.instance == null) return;
            Log($"Admin {sender} starts event '{eventName}' at {pos}");
            RandEventSystem.instance.SetRandomEventByName(eventName, pos);
        }

        private static void OnServerPeaceful(long sender, bool peaceful)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var field = AccessTools.Field(typeof(RandEventSystem), "m_events")
                        ?? AccessTools.Field(typeof(RandEventSystem), "m_randomEvents");
            var list = field?.GetValue(RandEventSystem.instance) as IList;
            if (list == null) { Log("Peaceful toggle: event list not found"); return; }
            var changed = 0;
            foreach (var ev in list)
            {
                var enabledField = AccessTools.Field(ev.GetType(), "m_enabled");
                if (enabledField == null) continue;
                enabledField.SetValue(ev, !peaceful);
                changed++;
            }
            if (peaceful && RandEventSystem.instance != null)
                RandEventSystem.instance.ResetRandomEvent();
            Log($"Peaceful mode {(peaceful ? "ON" : "OFF")} ({changed} events toggled)");
        }

        // ---------- client: executors, server-only ----------
        private static void OnGiveItem(long sender, string prefabName, int amount, int quality, string crafter)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!SenderIsServer(sender)) return;

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null) return;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) return;
            // icon-less items (hair, beards, effects) corrupt the inventory grid — never add them
            if (drop.m_itemData.m_shared.m_icons == null || drop.m_itemData.m_shared.m_icons.Length == 0) return;

            var maxStack = drop.m_itemData.m_shared.m_maxStackSize;
            if (maxStack < 1) maxStack = 1; // guard: a 0 max-stack would loop forever
            var remaining = amount;
            while (remaining > 0)
            {
                var stack = Mathf.Min(remaining, maxStack);
                remaining -= stack;
                var data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = stack;
                data.m_quality = Mathf.Clamp(quality, 1, data.m_shared.m_maxQuality);
                // New items carry the world's NG+ level, as vanilla's own Inventory.AddItem(prefab, n) sets it: gear
                // at level 0 on an NG+ world has less damage/armor and never stacks with the player's own items.
                data.m_worldLevel = Game.m_worldLevel;
                data.m_durability = data.GetMaxDurability();
                if (!string.IsNullOrEmpty(crafter)) { data.m_crafterID = 1; data.m_crafterName = crafter; }
                AddOrDrop(player, data);
            }
            player.Message(MessageHud.MessageType.Center, $"An admin granted you {amount}x {prefabName}!");
        }

        // Puts a stack in the player's bag and drops at their feet whatever does not fit. Shared by every grant
        // executor (give, vault restore / offline queue, economy delivery, item forge).
        //
        // Inventory.AddItem(ItemData) merges into partial stacks one unit at a time; when it then finds no empty
        // slot it cuts item.m_stack down to the part it could NOT place and returns false (Inventory.cs:112-158).
        // So only data.m_stack belongs on the ground. Every executor used to drop the stack as it was BEFORE the
        // call, duplicating whatever had already been merged - a nearly full bag with partial stacks of the item
        // turned a 50-wood grant (or a paid shop purchase) into more than 50.
        //
        // ItemDrop.DropItem clones every field (quality, durability, variant, crafter, world level, custom data)
        // and saves it to the new drop's ZDO; the old fallback instantiated a bare prefab and hand-copied two.
        internal static void AddOrDrop(Player player, ItemDrop.ItemData data)
        {
            if (player.GetInventory().AddItem(data)) return;
            if (data.m_stack <= 0 || data.m_dropPrefab == null) return;
            ItemDrop.DropItem(data, data.m_stack, player.transform.position + Vector3.up, Quaternion.identity);
        }

        // ---------- server: remove items from a player's inventory (admin moderation) ----------
        private static void OnServerInvRemove(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string itemName; int amount;
            try
            {
                targetUid = pkg.ReadLong();
                itemName = pkg.ReadString();
                amount = pkg.ReadInt();
            }
            catch (Exception e) { Log($"AP_SrvInvRemove: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(itemName) || amount <= 0) return;
            // Target 0 removed the item from EVERY player's inventory.
            if (!RelayTargetOk(sender, targetUid, "AP_SrvInvRemove", needsMod: true)) return;
            Log($"Admin {sender} removes {amount}x {itemName} from peer {targetUid}");
            var relay = new ZPackage();
            relay.Write(itemName);
            relay.Write(amount);
            relay.Write(sender);   // whom the target pushes its refreshed inventory to
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_RemoveItem", relay);
        }

        // Runs on the TARGET player's client (inventory lives with its owner). Matches items by the same
        // key OnInventoryRequest serializes (prefab name, falling back to shared name), removes up to the
        // requested amount across stacks, then pushes the fresh inventory back to the admin's viewer.
        private static void OnRemoveItem(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            string itemName; int amount; long replyTo;
            try
            {
                itemName = pkg.ReadString();
                amount = pkg.ReadInt();
                replyTo = pkg.ReadLong();
            }
            catch { return; }
            if (string.IsNullOrEmpty(itemName) || amount <= 0) return;

            var inv = player.GetInventory();
            var removed = 0;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (removed >= amount) break;
                var key = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                if (key != itemName) continue;
                // an equipped item must be unequipped first, or the player keeps a ghost-equipped
                // copy in hand (invisible in the bag, still usable) until they relog
                if (item.m_equipped) player.UnequipItem(item, false);
                var take = Mathf.Min(item.m_stack, amount - removed);
                inv.RemoveItem(item, take);
                removed += take;
            }
            if (removed > 0)
                player.Message(MessageHud.MessageType.Center, $"An admin removed {removed}x {itemName} from your inventory");
            OnInventoryRequest(sender, replyTo);   // refresh the admin's inventory viewer
        }

        // ---------- server: version handshake ----------
        // Any client may ask; the reply goes only to the asker. No admin gate needed — the version string
        // is not sensitive, and gating it would hide exactly the mismatch the panel wants to display.
        // Peers already told (once per connection) that they opened the panel without being an admin. Uids are
        // per connection, so a rejoin logs again; cleared in PeerLeaveLogPatch.
        private static readonly HashSet<long> _panelNotified = new HashSet<long>();

        private static void OnServerVersionReq(long sender)
        {
            if (!IsDedicatedServer) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_VersionData", PluginVersion);
            // Health payload (SHARED CONTRACT) right behind the version. Panels older than 2.5.2 never registered
            // AP_HealthData; ZRoutedRpc.HandleRoutedRPC drops a routed call whose name has no registered method
            // (the m_functions lookup simply misses), so the extra reply is invisible to them.
            // 2.5.5: ";admin=0|1" is appended - the asker's adminlist.txt verdict under the SAME rules every
            // AP_Srv* handler applies (SenderIsAdmin -> vanilla ZNet.IsAdmin). The panel replaces its whole body
            // with a "not an admin here" notice on 0. Older panels ignore unknown keys (ParseHealth). This is a
            // query, so the DENIED log line is suppressed; instead the owner gets ONE line per connection.
            var isAdmin = SenderIsAdmin(sender, logDenied: false);
            if (!isAdmin && _panelNotified.Add(sender))
            {
                var peer = ZNet.instance.GetPeer(sender);
                var who = peer != null ? $"{peer.m_playerName} / {(peer.m_socket != null ? peer.m_socket.GetHostName() : "?")}" : "?";
                Log($"peer {sender} ({who}) runs the Admin Panel client but is not in adminlist.txt - the panel is disabled for them and every server action they send is denied");
            }
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_HealthData", HealthSummary + ";admin=" + (isAdmin ? "1" : "0")); }
            catch (Exception e) { Log($"AP_HealthData reply failed: {e.GetType().Name}: {e.Message}"); }
        }

        // ---------- server: skip to morning ----------
        // World time is server-owned (ZNet.m_netTime), so a client can't skip night by itself — the old
        // panel button only flipped a local debug flag and changed nothing. EnvMan.SkipToMorning() is the
        // game's own sleep-skip: it advances net time to the next morning for everyone.
        private static void OnServerSkipNight(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (EnvMan.instance == null) return;
            Log($"Admin {sender} skips to morning");
            EnvMan.instance.SkipToMorning();
        }

        // ---------- server: raise a skill on any player ----------
        private static void OnServerSkillRaise(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string skillName; float amount; string note;
            try
            {
                targetUid = pkg.ReadLong();
                skillName = pkg.ReadString();
                amount = pkg.ReadSingle();
                note = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvSkillRaise: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(skillName)) return;
            if (!RelayTargetOk(sender, targetUid, "AP_SrvSkillRaise", needsMod: true)) return;
            amount = Mathf.Clamp(amount, -100f, 100f);   // one click can never exceed the whole skill range
            Log($"Admin {sender} raises {skillName} by {amount} for peer {targetUid}");
            var relay = new ZPackage();
            relay.Write(skillName);
            relay.Write(amount);
            relay.Write(note ?? "");
            // 2.5.6: whom the target reports its new levels to (AP_SkillData). A trailing field, so a pre-2.5.6
            // target simply never reads it and still applies the raise.
            relay.Write(sender);
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_SkillRaise", relay);
        }

        // AP_SrvSkillReq(long target): the admin panel's skill browser asks for a remote target's levels, which
        // live only in that player's own save. The target's client answers the admin directly (AP_SkillData,
        // like AP_InvData). Timer-driven while the browser is open, hence quiet refusals and no audit row.
        private static void OnServerSkillReq(long sender, long targetUid)
        {
            // Not an audited RPC (timer noise), so the chokepoint never applies tiered roles to it: gate it here,
            // as whoever may RAISE skills (AP_SrvSkillRaise, role-enforced) - a builder has no business reading them.
            if (!IsDedicatedServer || !SenderCanFeature(sender, "AP_SrvSkillRaise")) return;
            if (!RelayTargetOk(sender, targetUid, "AP_SrvSkillReq", needsMod: true, quiet: true)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_SkillReq", sender);
        }

        // AP_SrvPlayerPos(long target) -> AP_PlayerPos v1 {int 1, long uid, bool known, Vector3 pos} to the admin.
        // The roster the game sends to clients carries a position only for players who share theirs on the map
        // (everyone else arrives as 0,0,0 - the world centre), but the server always has the reference position
        // each client reports for zone loading. Audited (FeaturesInit): it reveals a hidden position.
        private static void OnServerPlayerPos(long sender, long targetUid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var known = false;
            var pos = Vector3.zero;
            if (targetUid != 0L)
            {
                if (IsLocalHostTarget(targetUid)) { pos = Player.m_localPlayer.transform.position; known = true; }
                else
                {
                    var peer = ZNet.instance.GetPeer(targetUid);
                    if (peer != null) { pos = peer.m_refPos; known = true; }
                }
            }
            Log($"Admin {sender} locates peer {targetUid}: {(known ? pos.ToString() : "unknown")}");
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(targetUid);
            pkg.Write(known);
            pkg.Write(pos);
            ReplyTo(sender, "AP_PlayerPos", pkg);
        }

        // Status effects live with their owner exactly like skills, so the shape mirrors AP_SrvSkillRaise:
        // admin-validated on the server, executed on the target's own client. The effect is sent by hash
        // (StatusEffect.NameHash), which both sides resolve against their own ObjectDB — safe across
        // panel languages because hashes come from prefab names, not localized text.
        private static void OnServerApplyStatusEffect(long sender, long targetUid, int seHash)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (!RelayTargetOk(sender, targetUid, "AP_SrvApplySE", needsMod: true)) return;
            Log($"Admin {sender} applies status effect {seHash} to peer {targetUid}");
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_ApplySE", seHash);
        }

        // Runs on the TARGET player's client. Same trust model as AP_HealSelf: only the server may send it.
        private static void OnApplyStatusEffect(long sender, int seHash)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.GetSEMan().AddStatusEffect(seHash, true);
            // Small toast so the buff icon appearing isn't mysterious. The companion deliberately has no
            // Localization reference (client-only assembly here would add a dependency for one string), so
            // use the prefab name and skip raw "$se_..." tokens — the icon itself is the real feedback.
            if (ObjectDB.instance != null)
                foreach (var se in ObjectDB.instance.m_StatusEffects)
                    if (se != null && se.NameHash() == seHash)
                    {
                        var name = !string.IsNullOrEmpty(se.m_name) && !se.m_name.StartsWith("$") ? se.m_name : se.name;
                        if (!string.IsNullOrEmpty(name))
                            player.Message(MessageHud.MessageType.TopLeft, $"[Admin] {name}");
                        break;
                    }
        }

        // Runs on the TARGET player's client (skills live with their owner, like inventories).
        private static void OnSkillRaise(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            string skillName; float amount; string note;
            long replyTo = 0L;
            try
            {
                skillName = pkg.ReadString();
                amount = pkg.ReadSingle();
                note = pkg.ReadString();
                // 2.5.6 servers append the admin to report the new levels to; older ones end the packet here.
                if (pkg.GetPos() < pkg.Size()) replyTo = pkg.ReadLong();
            }
            catch { return; }
            if (string.IsNullOrEmpty(skillName)) return;
            player.GetSkills().CheatRaiseSkill(skillName, Mathf.Clamp(amount, -100f, 100f), false);
            if (replyTo != 0L)
            {
                // "All skills" is one raise per skill: answer the burst once, a moment after its last packet.
                _skillReplyTo = replyTo;
                if (Instance != null) { Instance.CancelInvoke(nameof(FlushSkillReply)); Instance.Invoke(nameof(FlushSkillReply), 0.25f); }
                else SendSkillData(player, replyTo);
            }
            // Only the admin's own note is shown, and only on THIS client (the routed RPC targets one
            // peer). Empty note = the change is completely silent — the admin decides what, if anything,
            // the player gets told. When a note is present it pops center-screen together with the
            // skill's NEW level, read back after the change was applied.
            if (!string.IsNullOrEmpty(note))
            {
                var levelLine = "";
                if (Enum.TryParse<Skills.SkillType>(skillName, out var st))
                    levelLine = $"\n{skillName}: {player.GetSkills().GetSkillLevel(st):0.#}";
                player.Message(MessageHud.MessageType.Center, note + levelLine);
            }
        }

        private static long _skillReplyTo;   // the admin a pending coalesced skill table goes to (0 = none)

        private void FlushSkillReply()
        {
            var to = _skillReplyTo;
            _skillReplyTo = 0L;
            var player = Player.m_localPlayer;
            if (to != 0L && player != null) SendSkillData(player, to);
        }

        // Runs on the TARGET player's client: the admin's skill browser asks for this player's levels.
        private static void OnSkillReq(long sender, long replyTo)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender) || replyTo == 0L) return;   // 0 would answer everybody
            SendSkillData(player, replyTo);
        }

        // AP_SkillData v1 {int 1, string playerName, int n, n x (string skill, float level)}, straight to the admin
        // (the server relays it with this client as the re-stamped sender, which is what the panel checks).
        // Only the skills this player HAS, at their stored level: Skills.GetSkillLevel goes through GetSkill,
        // which ADDS a missing skill to the save (Skills.cs:345-354), so asking for every type would have
        // written all of them, at 0, into this player's Skills tab. The panel shows an absent skill as 0.
        private static void SendSkillData(Player player, long replyTo)
        {
            var skills = player.GetSkills();
            if (skills == null || ZRoutedRpc.instance == null) return;
            var rows = new List<Skills.Skill>();
            foreach (var skill in skills.GetSkillList())
                if (skill?.m_info != null && skill.m_info.m_skill != Skills.SkillType.None && skill.m_info.m_skill != Skills.SkillType.All)
                    rows.Add(skill);
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(player.GetPlayerName());
            pkg.Write(rows.Count);
            foreach (var skill in rows)
            {
                pkg.Write(skill.m_info.m_skill.ToString());
                pkg.Write(skill.m_level);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(replyTo, "AP_SkillData", pkg);
        }

        private static void OnInventoryRequest(long sender, long replyTo)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!SenderIsServer(sender)) return;
            if (replyTo == 0L) return;   // 0 = everybody: this player's inventory would go to every client

            var pkg = new ZPackage();
            pkg.Write(player.GetPlayerName());
            var items = player.GetInventory().GetAllItems();
            pkg.Write(items.Count);
            foreach (var item in items)
            {
                pkg.Write(item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name);
                pkg.Write(item.m_stack);
                pkg.Write(item.m_quality);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(replyTo, "AP_InvData", pkg);
        }

        private static void OnTeleport(long sender, Vector3 pos)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.TeleportTo(pos + Vector3.up, player.transform.rotation, true);
            player.Message(MessageHud.MessageType.Center, "An admin teleported you!");
        }

        private static void OnHealSelf(long sender)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.Heal(player.GetMaxHealth());
            player.Message(MessageHud.MessageType.Center, "An admin healed you!");
        }

        private static void OnMessage(long sender, string text)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.Message(MessageHud.MessageType.Center, $"[Server] {text}");
        }
    }
}

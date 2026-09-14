using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — SYSTEMS group core (server + client-side executors) ====================
    // Three round-2 features share this core (the #24 companion self-update lived here until 2.5.4):
    //   #21 death rules            Wave8SrvSystemsDeath.cs   (Wave8SystemsDeath)
    //   #23 bounty board           Wave8SrvSystemsBounty.cs  (Wave8SystemsBounty)
    //   #25 client perf census     Wave8SrvSystemsPerf.cs    (Wave8SystemsPerf)
    //
    // This file owns what they share: the ONE ZNet.Awake registration class for every RPC name of the group,
    // the patch applier that reports to the self-test, the trusted-server gate the client-side executors use,
    // the join/leave hooks that fan out to the sub-modules (so each feature does not stack its own patch on
    // RPC_PeerInfo), the deferred "push state to a freshly joined peer" list, and the text/peer helpers.
    //
    // The companion DLL is the same file on the server and on every player's client, so every handler guards
    // its own side: server handlers refuse to run off-server, client executors refuse anything the server did
    // not send (SenderIsTrustedServer) and need a local Player.
    internal static class Wave8Systems
    {
        private static bool _inited;

        // A just-joined client has no Player yet (same reason the MOTD and the capability probe wait), so
        // per-peer state pushes are deferred and driven from Tick. Ten seconds matches Wave34Core.FirstProbeDelay.
        private const float JoinPushDelay = 10f;

        private sealed class JoinPush
        {
            public long Uid;
            public string HostId;
            public string Name;
            public float DueAt;
        }

        private static readonly List<JoinPush> Pending = new List<JoinPush>();

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            // Registration first so a failing sub-module can never leave the RPC names unbound for the others.
            ApplyPatch("Wave8SystemsRpcRegistration", typeof(Wave8SystemsRpcRegistration), "systems RPCs unavailable");
            ApplyPatch("Wave8SystemsJoinPatch", typeof(Wave8SystemsJoinPatch), "join-time pushes/announcements unavailable");
            ApplyPatch("Wave8SystemsLeavePatch", typeof(Wave8SystemsLeavePatch), "per-peer state is not pruned on disconnect");

            try { Wave8SystemsDeath.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 death rules init failed (feature unavailable): {e.Message}"); }
            try { Wave8SystemsBounty.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 bounty board init failed (feature unavailable): {e.Message}"); }
            try { Wave8SystemsPerf.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 client perf census init failed (feature unavailable): {e.Message}"); }
        }

        // Every frame from CompanionPlugin.Update. The two modules with CLIENT-side work (perf reporter, death
        // rule cache) tick before the server gate; each sub-tick self-throttles and catches its own exceptions.
        internal static void Tick()
        {
            if (!_inited) return;
            try { Wave8SystemsPerf.Tick(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 perf tick failed: {e.Message}"); }
            try { Wave8SystemsDeath.Tick(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 death rules tick failed: {e.Message}"); }
            try { Wave8SystemsBounty.Tick(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 bounty tick failed: {e.Message}"); }

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try { StepJoinPushes(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 join push failed: {e.Message}"); }
        }

        // ==================== patch applier (Wave34Core shape) ====================

        internal static void ApplyPatch(string name, Type patchClass, string degradation)
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

        // ==================== RPC registration ====================

        // Both sides register every name on purpose (same DLL on server and clients); each handler guards
        // its own side. Stacked on ZNet.Awake next to the other waves' registration classes.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave8SystemsRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                var rpc = ZRoutedRpc.instance;
                if (rpc == null) return;
                try
                {
                    // --- #21 death rules ---
                    rpc.Register("AP_SrvDeathRulesReq", new Action<long>(Wave8SystemsDeath.OnRulesReq));       // server (read)
                    rpc.Register<ZPackage>("AP_SrvDeathRulesSet", Wave8SystemsDeath.OnRulesSet);                // server (owner)
                    rpc.Register<ZPackage>("AP_SrvDeathRulesPlayer", Wave8SystemsDeath.OnPlayerAction);         // server (moderator)
                    rpc.Register<ZPackage>("AP_DeathRules", Wave8SystemsDeath.OnRulesExecutor);                 // client executor

                    // --- #23 bounty board ---
                    rpc.Register("AP_SrvBountyReq", new Action<long>(Wave8SystemsBounty.OnBountyReq));         // server (read)
                    rpc.Register<ZPackage>("AP_SrvBountySet", Wave8SystemsBounty.OnBountySet);                  // server (builder)
                    rpc.Register<ZPackage>("AP_SrvBountyAction", Wave8SystemsBounty.OnBountyAction);            // server (builder)
                    rpc.Register<ZPackage>("AP_BountyKill", Wave8SystemsBounty.OnBountyKill);                   // server, PLAYER-authored
                    rpc.Register<ZPackage>("AP_BountyTargets", Wave8SystemsBounty.OnTargetsExecutor);           // client executor

                    // --- #25 client performance census ---
                    rpc.Register<ZPackage>("AP_PerfReport", Wave8SystemsPerf.OnPerfReport);                     // server, PLAYER-authored
                    rpc.Register("AP_SrvClientPerfReq", new Action<long>(Wave8SystemsPerf.OnClientPerfReq));   // server (read)
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave8 systems RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== trusted-server gate for client-side executors ====================

        // CompanionPlugin.SenderIsServer is private and Wave34Core's copy is private too, so this is the
        // group's own copy of the same two-step check: bind the real helper reflectively (it lives in OUR
        // assembly, so the name is stable) and keep an equivalent fallback so a rename degrades to "the
        // executor stops answering", never to "anyone can impersonate the server".
        private static MethodInfo _senderIsServerMi;
        private static bool _senderIsServerProbed;

        internal static bool SenderIsTrustedServer(long sender)
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
            return FallbackSenderIsServer(sender);
        }

        private static bool FallbackSenderIsServer(long sender)
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return false;
                var serverPeer = znet.GetServerPeer();                       // non-null only on a client
                if (serverPeer != null && serverPeer.m_uid != 0L && sender == serverPeer.m_uid) return true;
                if (!znet.IsServer() || ZDOMan.instance == null) return false;
                if (sender != ZDOMan.GetSessionID()) return false;
                // Host case: trust our own session id ONLY while the sender sanitizer is live — otherwise the
                // id on an incoming packet is attacker-chosen.
                var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                return f != null && (bool)f.GetValue(null);
            }
            catch (Exception) { return false; }
        }

        // The uid a CLIENT-side sender addresses the server with. On a remote client that is the server peer;
        // on a listen-server host the server lives in this process and a packet to our own session id is
        // dispatched locally by ZRoutedRpc (target == m_id), exactly how NotifyOnlineAdmins reaches the host.
        // 0 = not connected: callers must not send (0 aliases ZRoutedRpc.Everybody).
        internal static long ServerTargetId()
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return 0L;
                if (znet.IsServer()) return ZDOMan.instance != null ? ZDOMan.GetSessionID() : 0L;
                var sp = znet.GetServerPeer();
                return sp != null ? sp.m_uid : 0L;
            }
            catch (Exception) { return 0L; }
        }

        // True when `sender` is this process acting as a listen-server host (never true for a remote peer:
        // the sanitizer re-stamps socket-delivered packets with the real peer uid).
        internal static bool IsHostSession(long sender)
        {
            try
            {
                return ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null
                       && sender == ZDOMan.GetSessionID();
            }
            catch (Exception) { return false; }
        }

        // ==================== join / leave hooks (one patch class per target, shared by the group) ====================

        // POSTFIX on RPC_PeerInfo: m_uid / m_playerName exist by now (the vanilla reject ladder runs first),
        // which is why every join hook in this mod is a postfix. Stacked alongside the other waves' classes.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class Wave8SystemsJoinPatch
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
                    var host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;

                    // A double RPC_PeerInfo is possible; one push per uid.
                    for (var i = 0; i < Pending.Count; i++)
                        if (Pending[i].Uid == peer.m_uid) return;
                    Pending.Add(new JoinPush
                    {
                        Uid = peer.m_uid,
                        HostId = CleanText(string.IsNullOrEmpty(host) ? "?" : host, 64),
                        Name = CleanText(peer.m_playerName, 60),
                        DueAt = Time.unscaledTime + JoinPushDelay,
                    });
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 join hook failed: {e.Message}"); }
            }
        }

        // PREFIX on Disconnect — the peer is still readable here (the original disposes it). A kick fires
        // Disconnect twice; every consumer below is idempotent.
        [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
        internal static class Wave8SystemsLeavePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (!_inited || __instance == null || !__instance.IsServer() || peer == null) return;
                try
                {
                    for (var i = Pending.Count - 1; i >= 0; i--)
                        if (Pending[i].Uid == peer.m_uid) Pending.RemoveAt(i);
                    Wave8SystemsPerf.OnPeerLeft(peer.m_uid);
                    Wave8SystemsBounty.OnPeerLeft(peer.m_uid);
                    Wave8SystemsDeath.OnPeerLeft(peer.m_uid);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 leave hook failed: {e.Message}"); }
            }
        }

        private static void StepJoinPushes()
        {
            if (Pending.Count == 0) return;
            var now = Time.unscaledTime;
            for (var i = Pending.Count - 1; i >= 0; i--)
            {
                var p = Pending[i];
                if (now < p.DueAt) continue;
                Pending.RemoveAt(i);
                if (!PeerConnected(p.Uid)) continue;   // left during the delay
                try { Wave8SystemsDeath.OnPeerReady(p.Uid, p.HostId, p.Name); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 death-rule push to {p.Name} failed: {e.Message}"); }
                try { Wave8SystemsBounty.OnPeerReady(p.Uid); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 bounty push to {p.Name} failed: {e.Message}"); }
            }
        }

        // ==================== peer helpers ====================

        internal static bool PeerConnected(long uid)
        {
            try { return uid != 0L && ZNet.instance != null && ZNet.instance.GetPeer(uid) != null; }
            catch (Exception) { return false; }
        }

        // Full "Platform_id" or bare id — either form matches, exactly like the adminlist read path.
        internal static long UidOfId(string id)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(id)) return 0L;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    if (Wave1Moderation.IdMatches(id, peer.m_socket.GetHostName())) return peer.m_uid;
                }
            }
            catch (Exception) { }
            return 0L;
        }

        // The peer whose CHARACTER has this ZDOID (ZNetPeer.m_characterID is filled once the player spawned).
        internal static ZNetPeer PeerOfCharacter(ZDOID characterId)
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

        // Vanilla-safe broadcast: "ShowMessage" is registered by MessageHud.Awake on every client, so this
        // reaches unmodded players too (the same delivery Wave6Events' announcements use). A listen-server
        // host is never in m_peers, so its own HUD is addressed directly.
        internal static void AnnounceAll(string text)
        {
            if (string.IsNullOrEmpty(text) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady()) continue;
                    Wave1Moderation.SendPlayerText(peer.m_uid, text);
                }
                if (Player.m_localPlayer != null && MessageHud.instance != null)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 announce failed: {e.Message}"); }
        }

        // Text to ONE player that lands on unmodded clients (center = 2, top-left = 1 in MessageHud's enum).
        // The host has no peer entry, so a uid equal to our own session id goes straight to the local HUD.
        internal static void Tell(long uid, string text, bool center = true)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            try
            {
                if (IsHostSession(uid))
                {
                    if (MessageHud.instance != null)
                        MessageHud.instance.ShowMessage(center ? MessageHud.MessageType.Center : MessageHud.MessageType.TopLeft, text);
                    return;
                }
                ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", center ? 2 : 1, text);
            }
            catch (Exception) { }
        }

        // ==================== text helpers ====================

        // '|' is the field separator inside stored rows, so it can never survive in free text.
        internal static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        internal static string Clamp(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // Ids are stored AS ENTERED (trimmed) — stripping a platform prefix silently breaks crossplay ids.
        internal static string CleanId(string s, int max = 64)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > max) s = s.Substring(0, max);
            return s.IndexOf(' ') >= 0 || s.IndexOf('|') >= 0 ? "" : s;
        }

        internal static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (Wave1Moderation.IdMatches(kv.Key, id)) return kv.Key;
            return null;
        }

        internal static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        internal static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        internal static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}

using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — toolkit group (server side): facade + shared plumbing ====================
    // Four unrelated admin tools share one lifecycle so the companion core needs exactly two hooks
    // (FeaturesWave8ToolkitGlue.cs):
    //
    //   #9  staff chat        Wave8SrvToolkitChat.cs   AP_SrvStaffChat / AP_SrvStaffChatReq -> AP_StaffChat, "!a"
    //   #13 location finder   Wave8SrvToolkitLoc.cs    AP_SrvLocTypesReq -> AP_LocTypes, AP_SrvLocFindReq -> AP_LocFind
    //   #17 item forge        Wave8SrvToolkitForge.cs  AP_SrvGiveEx -> AP_GiveItemEx (client executor)
    //   #22 raid composer     Wave8SrvToolkitRaid.cs   AP_SrvRaidStart / AP_SrvRaidStop / AP_SrvRaidStateReq -> AP_RaidState
    //
    // This file owns what all four need and nothing feature-specific: the chokepoint grants, the one
    // ZNet.Awake registration patch, the trusted-server gate for client-side executors, the sanitizers,
    // the vanilla-safe announce path and the requesting admin's position. The same DLL runs on the
    // server and on every modded client, so every handler guards its own side (see the registration class).
    internal static class Wave8Toolkit
    {
        private static bool _inited;

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            // Each tool binds its own config and registers its own chat command; a throw in one must not
            // cost the other three, so they are isolated here exactly like the wave list in FeaturesInit.
            try { Wave8ToolkitChat.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 staff chat init failed (staff chat unavailable): {e.Message}"); }
            try { Wave8ToolkitLoc.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 location finder init failed: {e.Message}"); }
            try { Wave8ToolkitForge.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 item forge init failed: {e.Message}"); }
            try { Wave8ToolkitRaid.Init(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid composer init failed: {e.Message}"); }

            // Every admin ACTION goes through the chokepoint (audit row + tiered-role enforcement before the
            // handler runs). Grants: staff chat and the location lookups are moderation work (coordinating
            // a ban, finding a lost player); giving forged items and firing raids change the world, so they
            // sit with the builder grant. The two periodic reads (AP_SrvStaffChatReq is one-shot per section
            // open, AP_SrvRaidStateReq is polled every few seconds) are deliberately NOT registered: an
            // audited RPC costs one audit row per call, and a polled read would make the panel the log's
            // biggest writer. Their role gates live in the handlers.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvStaffChat", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLocTypesReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLocFindReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvGiveEx", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRaidStart", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRaidStop", "builder");

            ApplyPatch("Wave8ToolkitRpcRegistration", typeof(Wave8ToolkitRpcRegistration),
                "staff chat / location finder / item forge / raid composer RPCs unavailable");
        }

        // Called every frame from Update; only the raid composer has a timer, and it self-throttles.
        internal static void Tick()
        {
            Wave8ToolkitRaid.Tick();
        }

        // One class per target method, applied in its own try/catch and reported to the self-test —
        // the shape Wave6Events.ApplyPatch established.
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

        // Every name is registered on BOTH sides on purpose: the companion is the same file on the server
        // and on an admin's client. Server handlers refuse to run off-server; the one client executor
        // (AP_GiveItemEx) refuses anything not sent by the server and needs a local Player. Each tool's
        // names sit in their own try so a failure costs that tool, not the group.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave8ToolkitRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                var r = ZRoutedRpc.instance;
                if (r == null) return;
                try
                {
                    r.Register<string>("AP_SrvStaffChat", Wave8ToolkitChat.OnStaffChat);
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    r.Register("AP_SrvStaffChatReq", new Action<long>(Wave8ToolkitChat.OnStaffChatReq));
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 staff-chat RPC registration failed: {e.Message}"); }
                try
                {
                    r.Register("AP_SrvLocTypesReq", new Action<long>(Wave8ToolkitLoc.OnTypesReq));
                    r.Register<ZPackage>("AP_SrvLocFindReq", Wave8ToolkitLoc.OnFindReq);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 location-finder RPC registration failed: {e.Message}"); }
                try
                {
                    r.Register<ZPackage>("AP_SrvGiveEx", Wave8ToolkitForge.OnGiveEx);
                    r.Register<ZPackage>("AP_GiveItemEx", Wave8ToolkitForge.OnGiveItemEx);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 item-forge RPC registration failed: {e.Message}"); }
                try
                {
                    r.Register<ZPackage>("AP_SrvRaidStart", Wave8ToolkitRaid.OnRaidStart);
                    r.Register("AP_SrvRaidStop", new Action<long>(Wave8ToolkitRaid.OnRaidStop));
                    r.Register("AP_SrvRaidStateReq", new Action<long>(Wave8ToolkitRaid.OnRaidStateReq));
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8 raid-composer RPC registration failed: {e.Message}"); }
            }
        }

        // ==================== trusted-server gate (client-side executors) ====================

        // CompanionPlugin.SenderIsServer is private and Wave34Core's copy of this gate is private too, so
        // the toolkit carries its own (the contract's "add your own copy" clause). Bound reflectively —
        // it lives in OUR assembly, so the name is stable — with an equivalent fallback so a future rename
        // degrades to "the executor stops answering", never to "anyone can impersonate the server".
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
                // Host case: trust the session id ONLY while the sender sanitizer is live — otherwise the
                // id is attacker-chosen (see CompanionPlugin.IsLocalHostSender).
                var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                return f != null && (bool)f.GetValue(null);
            }
            catch (Exception) { return false; }
        }

        // ==================== shared helpers ====================

        // The requesting admin's position: m_refPos is the client's own continuously reported reference
        // position — the only position the server has for a player it does not simulate. A listen-server
        // host is not in m_peers at all, so it falls back to its local player (Wave6Events.SenderPosition).
        internal static Vector3 SenderPosition(long sender)
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

        // Free text from a client: newlines would forge extra log rows and '|' would shift the columns of
        // every pipe-separated line this group writes, so both are folded before the string is stored.
        internal static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace('|', '/').Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // Identifiers (prefab names, platform ids): no whitespace at all, no pipes, bounded.
        internal static string CleanName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c) || c == '|' || c == '\r' || c == '\n') continue;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString();
        }

        // Vanilla-safe broadcast: "ShowMessage" is registered by MessageHud.Awake on every client, which is
        // why it reaches unmodded players (Wave6Events.AnnounceAll). A listen-server host is not a peer,
        // so its own HUD is addressed directly.
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
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, text);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8: announce failed: {e.Message}"); }
        }

        // Top-left vanilla toast to ONE peer (MessageHud "ShowMessage", MessageType TopLeft = 1). Used
        // where a center-screen banner would be intrusive: a staff line for an admin whose client runs no
        // companion, a usage hint for a chat command.
        internal static void SendTopLeft(long uid, string text)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", 1, text); }
            catch (Exception) { }
        }

        internal static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);
    }
}

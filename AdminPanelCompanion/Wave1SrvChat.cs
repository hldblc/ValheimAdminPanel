using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 1 — server-side chat interception ====================
    // One prefix on ZRoutedRpc.RPC_RoutedRPC is the whole chat plane: read (history), block (mute) and
    // consume (chat commands). There is no other way in — Chat/Talker are client-only MonoBehaviours
    // (Chat.instance is NULL on a dedicated server) and chat packets are ZDO-targeted ("Say") or
    // Everybody-targeted ("ChatMessage"), so ZRoutedRpc.Register would never see the traffic the server
    // merely relays (ZRoutedRpc.cs:175-187: a packet whose target is another peer is relayed, never
    // dispatched locally).
    //
    // Ordering: RouteRpcSanitizer runs at Priority.Normal (400) and re-stamps m_senderPeerID with the real
    // socket uid; the admin chokepoint runs at Priority.Low (200). This class runs at 150 — after both —
    // so the sender long it reads is already trustworthy.
    //
    // Failure policy: fail OPEN on every path. A parse bug here would otherwise silence the entire routed
    // RPC bus (every spawn, every teleport, every vanilla RPC in the game flows through this method).
    // The only packets this class may ever drop are the two chat hashes.
    internal static class Wave1Chat
    {
        // ---- config ----
        private static ConfigEntry<bool> _historyCfg;
        private static ConfigEntry<bool> _commandsCfg;
        private static ConfigEntry<bool> _rulesGateCfg;
        private static ConfigEntry<string> _rulesTextCfg;
        private static ConfigEntry<int> _rulesKickCfg;

        private static bool _inited;

        // Cached once: GetStableHashCode walks the string, and this runs for EVERY routed RPC.
        private static readonly int SayHash = "Say".GetStableHashCode();
        private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();

        // ---- limits ----
        private const int MaxChatLineLen = 300;     // stored history text cap
        private const int MaxNameLen = 40;
        private const int ChatLogShipCap = 100;     // wire contract cap
        private const int MaxRulesLines = 8;
        private const int MaxRulesLineLen = 200;
        private const double DedupWindowSeconds = 2.0;
        private const double MuteNoticeSeconds = 10.0;
        private const double CommandRateSeconds = 1.0;
        private const double RulesNoticeSeconds = 60.0;
        private const double RulesJoinDelaySeconds = 10.0;   // MessageHud must exist on the joiner first
        private const int MaxMuteMinutes = 60 * 24 * 30;
        private const int TypePing = 3;             // Talker.Type.Ping — a map marker, never chat

        // ---- in-memory state (all main-thread; routed-RPC handlers and Update share that thread) ----

        // One in-game message = N packets (Chat.CheckPermissionsAndSendChatMessageRPCsAsync loops the
        // player list and sends once per recipient), all with the same sender and text. Everything that
        // must happen ONCE per message (history append, command dispatch, rules nudge) is gated on this.
        private static readonly Dictionary<string, DateTime> _recentMsg = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly Dictionary<long, DateTime> _muteNotice = new Dictionary<long, DateTime>();
        private static readonly Dictionary<long, DateTime> _cmdRate = new Dictionary<long, DateTime>();
        private static readonly Dictionary<long, DateTime> _rulesNotice = new Dictionary<long, DateTime>();

        // Chat-command registry. Infrastructure for later waves (!warp, !shop, !vote): they call
        // RegisterChatCommand in their own Init and never touch this file again.
        private static readonly Dictionary<string, Action<long, string>> Commands =
            new Dictionary<string, Action<long, string>>(StringComparer.OrdinalIgnoreCase);

        private sealed class JoinInfo
        {
            public DateTime JoinedUtc;
            public string HostId;
            public string Name;
            public bool RulesSent;
        }

        private static readonly Dictionary<long, JoinInfo> _joined = new Dictionary<long, JoinInfo>();

        private static bool _parseWarned;
        private static float _nextTick;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _historyCfg = cfg.Bind("Features", "EnableChatHistory", true,
                    "Record all player chat to BepInEx/config/AdminPanelCompanion/<world>/chat.log. Passive: reads chat, changes nothing.");
                _commandsCfg = cfg.Bind("Features", "EnableChatCommands", true,
                    "Let feature modules answer '!' chat commands. Messages matching a registered command are consumed; all other chat passes through untouched.");
                _rulesGateCfg = cfg.Bind("Features", "EnableRulesGate", false,
                    "Show server rules to joiners and require '!accept'. Off = nothing is shown and nobody is kicked.");
                _rulesTextCfg = cfg.Bind("Features", "RulesText", "",
                    "Rules shown to joiners who have not accepted yet. Use | to separate lines (max 8).");
                _rulesKickCfg = cfg.Bind("Features", "RulesKickMinutes", 0,
                    "Kick players who have not typed !accept after this many minutes online. 0 = never kick (show the rules only).");
            }

            // Every new admin RPC goes through the chokepoint (audit + tiered roles). AP_SrvChatLogReq is
            // NOT one of them: the panel polls it every 20 s while the chat view is open, and an audited RPC
            // writes one line — a synchronous file append on the main thread — on EVERY call. Its role gate
            // lives in OnSrvChatLogReq instead.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMute", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUnmute", "moderator");

            try { Harmony.CreateAndPatchAll(typeof(ChatRpcRegistration)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"ChatRpcRegistration patch failed (mute/chat-log RPCs unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(ChatChokepoint)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"ChatChokepoint patch failed (chat history/mute/commands unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(RulesJoinPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"RulesJoinPatch patch failed (rules gate on join unavailable): {e.Message}"); }

            RegisterChatCommand("accept", OnAcceptCommand);
        }

        internal static void Tick()
        {
            if (!_inited) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 2f;

            try
            {
                var now = DateTime.UtcNow;
                PruneMaps(now);
                ExpireMutes(now);
                DeliverPendingRules(now);
                RulesKickSweep(now);
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"chat tick failed: {e.Message}");
            }
        }

        // ==================== public surface for sibling feature files ====================

        // Escalation path (auto-mod / warning ladder): mute by platform id. minutes <= 0 unmutes.
        internal static void MuteId(string id, int minutes)
        {
            var clean = id != null ? id.Trim() : null;
            if (string.IsNullOrEmpty(clean)) return;
            try
            {
                var mutes = FeatureStore.Table("mutes");
                if (minutes <= 0)
                {
                    if (mutes.Remove(clean) | mutes.Remove(CompanionPlugin.FeatureBareId(clean)))
                        FeatureStore.SaveTable("mutes");
                    NotifyId(clean, "You are no longer muted.");
                    return;
                }
                if (minutes > MaxMuteMinutes) minutes = MaxMuteMinutes;
                var expiry = DateTime.UtcNow.AddMinutes(minutes).Ticks;
                mutes[clean] = expiry.ToString(CultureInfo.InvariantCulture);
                FeatureStore.SaveTable("mutes");
                NotifyId(clean, $"You have been muted for {minutes} minute(s).");
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"MuteId failed for {clean}: {e.Message}");
            }
        }

        // Later waves register their commands here (Init-time). First registration wins so a duplicate
        // name in a later wave can never silently steal an existing command.
        internal static void RegisterChatCommand(string cmd, Action<long, string> handler)
        {
            if (string.IsNullOrEmpty(cmd) || handler == null) return;
            var key = cmd.Trim().TrimStart('!').ToLowerInvariant();
            if (key.Length == 0) return;
            if (Commands.ContainsKey(key))
            {
                CompanionPlugin.FeatureLog($"chat command !{key} already registered; ignoring the duplicate");
                return;
            }
            Commands[key] = handler;
        }

        // ==================== the chokepoint ====================

        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPriority(150)]
        internal static class ChatChokepoint
        {
            private static bool Prefix(ZRpc rpc, ZPackage pkg)
            {
                if (!_inited || pkg == null) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                int pos;
                try { pos = pkg.GetPos(); }
                catch (Exception) { return true; }

                try
                {
                    // RoutedRPCData layout: 0 msgID(8) | 8 sender(8) | 16 target(8) | 24 targetZDO(12) |
                    // 36 methodHash(4) | 40 parameters (length-prefixed sub-package).
                    pkg.SetPos(8);
                    var sender = pkg.ReadLong();
                    pkg.SetPos(36);
                    var hash = pkg.ReadInt();
                    pkg.SetPos(pos);

                    var global = hash == ChatMessageHash;
                    if (!global && hash != SayHash) return true;   // NEVER drop anything but chat

                    // The payload has to be read BEFORE the mute decision: the "ChatMessage" hash also
                    // carries map PINGS (Chat.SendPing routes through it), so dropping on hash + sender
                    // alone removes a muted player's ability to mark the map — an effect a mute never
                    // advertised, answered by a notice that talks about undelivered messages.
                    var ctype = -1;
                    string speaker = null, text = null;
                    try
                    {
                        pkg.SetPos(40);
                        var parms = pkg.ReadPackage();
                        ReadChat(parms, global, out ctype, out speaker, out text);
                    }
                    catch (Exception e)
                    {
                        ctype = -1;
                        speaker = null;
                        text = null;
                        NoteParseFailure(e);
                    }
                    finally { try { pkg.SetPos(pos); } catch (Exception) { } }

                    // Fail OPEN on a type we could not read: letting one message of a muted player through
                    // is a smaller failure than silently blocking traffic we cannot identify.
                    if (ctype >= 0 && ctype != TypePing && IsMutedSender(sender)) { NoticeMuted(sender); return false; }

                    if (string.IsNullOrEmpty(text)) return true;   // unreadable text: pass it through

                    var first = FirstCopy(sender, text);
                    if (TryHandleCommand(sender, text, first)) return false;   // consumed
                    if (first)
                    {
                        RecordHistory(sender, speaker, text);
                        RulesNudge(sender);
                    }
                    return true;
                }
                catch (Exception)
                {
                    try { pkg.SetPos(pos); } catch (Exception) { }
                    return true;   // fail open, always
                }
            }
        }

        // UserInfo.Serialize (UserInfo.cs) writes `string Name` then `UserId.ToString()`, so both chat
        // payloads are plain sequential values:
        //   "Say"         -> int ctype, string userName, string userId, string text
        //   "ChatMessage" -> Vector3 pos (3 floats), int ctype, string userName, string userId, string text
        // ctype is Talker.Type (Whisper=0, Normal=1, Shout=2, Ping=3) — the only field that separates real
        // talk from a map ping on the shared "ChatMessage" hash.
        private static void ReadChat(ZPackage parms, bool global, out int ctype, out string speaker, out string text)
        {
            ctype = -1;
            speaker = null;
            text = null;
            if (parms == null) return;
            parms.SetPos(0);
            if (global) { parms.ReadSingle(); parms.ReadSingle(); parms.ReadSingle(); }
            ctype = parms.ReadInt();    // Talker.Type
            speaker = parms.ReadString();
            parms.ReadString();         // PlatformUserID string — identity comes from the sanitized sender
            text = parms.ReadString();
        }

        private static void NoteParseFailure(Exception e)
        {
            if (_parseWarned) return;
            _parseWarned = true;
            CompanionPlugin.FeatureLog(
                $"chat payload unreadable ({e.Message}) — chat history, chat commands, the rules gate AND mute are skipped for these packets (fail open: a packet we cannot identify may be a map ping, not chat).");
        }

        // ==================== history ====================

        private static bool FirstCopy(long sender, string text)
        {
            var key = sender.ToString(CultureInfo.InvariantCulture) + "|" + text;
            var now = DateTime.UtcNow;
            DateTime seen;
            if (_recentMsg.TryGetValue(key, out seen) && (now - seen).TotalSeconds < DedupWindowSeconds)
                return false;   // keep the ORIGINAL timestamp: N packets must not extend the window
            _recentMsg[key] = now;
            if (_recentMsg.Count > 512) PruneRecent(now);
            return true;
        }

        private static void RecordHistory(long sender, string speaker, string text)
        {
            if (_historyCfg == null || !_historyCfg.Value) return;
            try
            {
                var id = CompanionPlugin.SenderPlatformId(sender);
                var name = !string.IsNullOrEmpty(speaker) ? speaker : CompanionPlugin.SenderDisplayName(sender);
                FeatureStore.Append("chat",
                    $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|{Field(id, MaxNameLen)}|{Field(name, MaxNameLen)}|{Clip(text, MaxChatLineLen)}");
            }
            catch (Exception) { /* a full disk must never break chat */ }
        }

        // Only the trailing text field may contain '|' (the reader splits into 4 parts, text last).
        private static string Field(string s, int max) =>
            string.IsNullOrEmpty(s) ? "?" : Clip(s.Replace("|", "/"), max);

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // ==================== mute ====================

        private static bool IsMutedSender(long sender)
        {
            try
            {
                var mutes = FeatureStore.Table("mutes");
                if (mutes.Count == 0) return false;
                var id = CompanionPlugin.SenderPlatformId(sender);
                if (string.IsNullOrEmpty(id) || id == "?") return false;
                return IsMutedId(mutes, id);
            }
            catch (Exception) { return false; }
        }

        private static bool IsMutedId(Dictionary<string, string> mutes, string id)
        {
            string raw;
            if (!mutes.TryGetValue(id, out raw))
            {
                var bare = CompanionPlugin.FeatureBareId(id);
                if (bare == null || !mutes.TryGetValue(bare, out raw)) return false;
            }
            long ticks;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return false;
            return ticks > DateTime.UtcNow.Ticks;   // expired entries are swept in Tick
        }

        private static void NoticeMuted(long sender)
        {
            var now = DateTime.UtcNow;
            DateTime last;
            if (_muteNotice.TryGetValue(sender, out last) && (now - last).TotalSeconds < MuteNoticeSeconds) return;
            _muteNotice[sender] = now;
            // A muted player still SEES their own text (the client renders it locally), so without this
            // notice a mute is indistinguishable from a working chat.
            CompanionPlugin.NotifySender(sender, "You are muted; your message was not delivered.");
            SendPlainTo(sender, "You are muted; your message was not delivered.", true);
        }

        private static void ExpireMutes(DateTime now)
        {
            var mutes = FeatureStore.Table("mutes");
            if (mutes.Count == 0) return;
            List<string> dead = null;
            foreach (var kv in mutes)
            {
                long ticks;
                if (long.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) && ticks > now.Ticks)
                    continue;
                (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var id in dead) mutes.Remove(id);
            FeatureStore.SaveTable("mutes");
        }

        // ==================== chat commands ====================

        private static bool TryHandleCommand(long sender, string text, bool first)
        {
            if (_commandsCfg != null && !_commandsCfg.Value) return false;
            if (string.IsNullOrEmpty(text)) return false;
            var t = text.Trim();
            if (t.Length < 2 || t[0] != '!') return false;

            var sp = t.IndexOf(' ');
            var cmd = (sp < 0 ? t.Substring(1) : t.Substring(1, sp - 1)).ToLowerInvariant();
            var args = sp < 0 ? "" : t.Substring(sp + 1).Trim();

            Action<long, string> handler;
            if (!Commands.TryGetValue(cmd, out handler)) return false;   // unmatched '!' text passes through

            if (!first) return true;            // duplicate copy of an already-consumed command
            if (!CommandRateOk(sender)) return true;   // consumed but ignored (flood guard)

            try { handler(sender, args); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"chat command !{cmd} failed: {e.Message}"); }
            return true;
        }

        private static bool CommandRateOk(long sender)
        {
            var now = DateTime.UtcNow;
            DateTime last;
            if (_cmdRate.TryGetValue(sender, out last) && (now - last).TotalSeconds < CommandRateSeconds) return false;
            _cmdRate[sender] = now;
            return true;
        }

        // ==================== rules gate ====================

        private static bool RulesGateOn =>
            _rulesGateCfg != null && _rulesGateCfg.Value && !string.IsNullOrEmpty(RulesTextRaw);

        private static string RulesTextRaw => _rulesTextCfg != null && _rulesTextCfg.Value != null ? _rulesTextCfg.Value : "";

        private static bool HasAccepted(string hostId)
        {
            if (string.IsNullOrEmpty(hostId) || hostId == "?") return false;
            var st = FeatureStore.Table("modstate");
            return st.ContainsKey("rules_ok:" + hostId)
                   || st.ContainsKey("rules_ok:" + CompanionPlugin.FeatureBareId(hostId));
        }

        private static void SendRules(long uid)
        {
            foreach (var line in RulesLines()) SendPlainTo(uid, line, false);
            SendPlainTo(uid, "Type !accept to play", true);
        }

        private static List<string> RulesLines()
        {
            var res = new List<string>();
            var raw = RulesTextRaw.Replace("\\n", "|").Replace("\r", "").Replace("\n", "|");
            foreach (var part in raw.Split('|'))
            {
                var line = part.Trim();
                if (line.Length == 0) continue;
                res.Add(Clip(line, MaxRulesLineLen));
                if (res.Count >= MaxRulesLines) break;
            }
            return res;
        }

        // Chat-side fallback: covers a joiner whose HUD was not up yet when the join-time copy was sent.
        private static void RulesNudge(long sender)
        {
            if (!RulesGateOn) return;
            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?" || id == "HOST") return;
            if (CompanionPlugin.FeatureIsAdminId(id) || HasAccepted(id)) return;
            var now = DateTime.UtcNow;
            DateTime last;
            if (_rulesNotice.TryGetValue(sender, out last) && (now - last).TotalSeconds < RulesNoticeSeconds) return;
            _rulesNotice[sender] = now;
            SendRules(sender);
        }

        private static void OnAcceptCommand(long sender, string args)
        {
            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?") return;
            if (!RulesGateOn)
            {
                SendPlainTo(sender, "There are no rules to accept on this server.", true);
                return;
            }
            if (HasAccepted(id))
            {
                SendPlainTo(sender, "You have already accepted the rules.", true);
                return;
            }
            var st = FeatureStore.Table("modstate");
            st["rules_ok:" + id] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable("modstate");
            CompanionPlugin.SrvAudit(sender, "RulesAccepted", id);
            CompanionPlugin.NotifySender(sender, "Rules accepted. Welcome!");
            SendPlainTo(sender, "Rules accepted. Welcome!", true);
        }

        // Join-time delivery is deferred: at RPC_PeerInfo time the joining client is still loading and its
        // MessageHud has not registered "ShowMessage" yet, so an immediate send would be dropped on the floor.
        private static void DeliverPendingRules(DateTime now)
        {
            if (!RulesGateOn) return;
            foreach (var kv in _joined)
            {
                var info = kv.Value;
                if (info.RulesSent) continue;
                if ((now - info.JoinedUtc).TotalSeconds < RulesJoinDelaySeconds) continue;
                info.RulesSent = true;
                if (CompanionPlugin.FeatureIsAdminId(info.HostId) || HasAccepted(info.HostId)) continue;
                SendRules(kv.Key);
            }
        }

        private static void RulesKickSweep(DateTime now)
        {
            if (!RulesGateOn) return;
            var minutes = _rulesKickCfg != null ? _rulesKickCfg.Value : 0;
            if (minutes <= 0) return;
            List<long> kicked = null;
            foreach (var kv in _joined)
            {
                var info = kv.Value;
                if ((now - info.JoinedUtc).TotalMinutes < minutes) continue;
                if (CompanionPlugin.FeatureIsAdminId(info.HostId) || HasAccepted(info.HostId)) continue;
                (kicked ?? (kicked = new List<long>())).Add(kv.Key);
            }
            if (kicked == null) return;
            foreach (var uid in kicked)
            {
                var info = _joined[uid];
                _joined.Remove(uid);
                SendPlainTo(uid, "Kicked: the server rules were not accepted (!accept).", true);
                CompanionPlugin.NotifySender(uid, "Kicked: the server rules were not accepted (!accept).");
                var ok = CompanionPlugin.FeatureKick(uid);
                ServerAudit("RulesKick", $"{info.HostId}|{info.Name}|after {minutes}m");
                CompanionPlugin.FeatureLog($"Rules gate kicked {info.Name} ({info.HostId}): {(ok ? "kicked" : "peer not found")}");
            }
        }

        // Stacked alongside CompanionPlugin.PeerJoinLogPatch (Harmony allows several classes per target;
        // one target per class is the house rule). Postfix, because m_uid/m_playerName are only assigned
        // after the vanilla reject ladder inside RPC_PeerInfo.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class RulesJoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!_inited || __instance == null || !__instance.IsServer()) return;
                try
                {
                    foreach (var peer in __instance.GetPeers())
                    {
                        if (peer == null || peer.m_rpc != rpc) continue;
                        if (peer.m_uid == 0L) return;   // rejected connection: never got an identity
                        var host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                        _joined[peer.m_uid] = new JoinInfo
                        {
                            JoinedUtc = DateTime.UtcNow,
                            HostId = string.IsNullOrEmpty(host) ? "?" : host,
                            Name = string.IsNullOrEmpty(peer.m_playerName) ? "?" : peer.m_playerName,
                            RulesSent = false,
                        };
                        return;
                    }
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"rules join hook failed: {e.Message}"); }
            }
        }

        // ==================== RPCs ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class ChatRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvMute", OnSrvMute);
                ZRoutedRpc.instance.Register<string>("AP_SrvUnmute", OnSrvUnmute);
                ZRoutedRpc.instance.Register<int>("AP_SrvChatLogReq", OnSrvChatLogReq);
            }
        }

        private static void OnSrvMute(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMute")) return;
            string id;
            int minutes;
            try
            {
                id = pkg.ReadString();
                minutes = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvMute: malformed packet dropped ({e.Message})"); return; }

            id = id != null ? id.Trim() : null;
            if (string.IsNullOrEmpty(id) || id.Contains(" ")) return;
            if (minutes < 1) minutes = 1;
            if (minutes > MaxMuteMinutes) minutes = MaxMuteMinutes;

            MuteId(id, minutes);
            CompanionPlugin.SrvAudit(sender, "AP_SrvMute", $"{id}|{minutes}m");
            CompanionPlugin.NotifySender(sender, $"Muted {id} for {minutes} minute(s)");
        }

        private static void OnSrvUnmute(long sender, string id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvUnmute")) return;
            var clean = id != null ? id.Trim() : null;
            if (string.IsNullOrEmpty(clean)) return;
            MuteId(clean, 0);
            CompanionPlugin.SrvAudit(sender, "AP_SrvUnmute", clean);
            CompanionPlugin.NotifySender(sender, $"Unmuted {clean}");
        }

        // Reply shape (wire contract): int ver=1, int total, int shipped(<=100), shipped x string line.
        // "total" is the shipped count: chat.log is append-only and unbounded, so any real total costs a
        // full pass over it — on the main thread, on a 20 s poll. The panel renders (total - shipped) as
        // "N older lines are on the server", and an uncomputable number is better left unclaimed.
        private static void OnSrvChatLogReq(long sender, int maxLines)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Not an audited RPC (see Init), so BuiltinRoles carries no grant for this name — a moderator is
            // recognised through a mute grant they already hold, otherwise taking this read out of the
            // audited set would silently demote it to owner-only whenever EnableTieredRoles is on.
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvChatLogReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvMute")) return;
            try
            {
                var want = maxLines < 1 ? 1 : (maxLines > ChatLogShipCap ? ChatLogShipCap : maxLines);
                var all = FeatureStore.Tail("chat", want);

                var pkg = new ZPackage();
                pkg.Write(1);
                pkg.Write(all.Count);
                pkg.Write(all.Count);
                for (var i = 0; i < all.Count; i++) pkg.Write(Clip(all[i], 400));   // newest LAST
                CompanionPlugin.ReplyTo(sender, "AP_ChatLog", pkg);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvChatLogReq failed: {e.Message}"); }
        }

        // ==================== helpers ====================

        // Text to a VANILLA client. AP_Msg only exists where the companion is installed, so anything a
        // plain client must read goes through MessageHud's own routed RPC (MessageHud.cs:111,
        // Register<int, string>("ShowMessage", ...); MessageType TopLeft=1, Center=2).
        private static void SendPlainTo(long uid, string text, bool center)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", center ? 2 : 1, text); }
            catch (Exception) { }
        }

        private static void NotifyId(string platformId, string text)
        {
            var uid = UidOfPlatformId(platformId);
            if (uid == 0L) return;
            CompanionPlugin.NotifySender(uid, text);
            SendPlainTo(uid, text, true);
        }

        private static long UidOfPlatformId(string id)
        {
            if (string.IsNullOrEmpty(id) || ZNet.instance == null) return 0L;
            try
            {
                var bare = CompanionPlugin.FeatureBareId(id);
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    var host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                    if (string.IsNullOrEmpty(host)) continue;
                    if (host == id || CompanionPlugin.FeatureBareId(host) == bare) return peer.m_uid;
                }
            }
            catch (Exception) { }
            return 0L;
        }

        private static void ServerAudit(string action, string detail)
        {
            if (CompanionPlugin.AuditEnabledCfg == null || !CompanionPlugin.AuditEnabledCfg.Value) return;
            FeatureStore.Append("audit",
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|0|SERVER|SERVER|{action}|{detail}");
        }

        private static void PruneRecent(DateTime now)
        {
            List<string> dead = null;
            foreach (var kv in _recentMsg)
                if ((now - kv.Value).TotalSeconds > 10.0) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead == null) { _recentMsg.Clear(); return; }   // pathological burst: start clean
            foreach (var k in dead) _recentMsg.Remove(k);
        }

        // Per-peer maps follow the PruneUndoHistory shape: a uid with no peer is gone. The host is never
        // in ZNet.m_peers, but it also never reaches this file (locally dispatched RPCs bypass
        // RPC_RoutedRPC and the host never runs RPC_PeerInfo), so no host exemption is needed here.
        private static void PruneMaps(DateTime now)
        {
            PruneRecent(now);
            PruneByPeer(_muteNotice);
            PruneByPeer(_cmdRate);
            PruneByPeer(_rulesNotice);
            List<long> deadJoins = null;
            foreach (var kv in _joined)
                if (ZNet.instance.GetPeer(kv.Key) == null) (deadJoins ?? (deadJoins = new List<long>())).Add(kv.Key);
            if (deadJoins != null) foreach (var uid in deadJoins) _joined.Remove(uid);
        }

        private static void PruneByPeer(Dictionary<long, DateTime> map)
        {
            if (map.Count == 0) return;
            List<long> dead = null;
            foreach (var kv in map)
                if (ZNet.instance.GetPeer(kv.Key) == null) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var uid in dead) map.Remove(uid);
        }
    }
}

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 1 — server moderation core ====================
    // Temp-bans (with real expiry, which vanilla has none of), freeze/jail, warnings + optional
    // auto-escalation, watchlist, lockdown, presence tracking and a report-only alt heuristic.
    //
    // Design rules this file obeys (see spec-companion.md / spec-valheim-api.md):
    //  * Everything durable lives in FeatureStore tables — the companion restarts often, moderation state
    //    must not. Ticks = DateTime.UtcNow.Ticks everywhere on the wire and in the store.
    //  * Every admin RPC is gated by CompanionPlugin.SenderCanFeature (adminlist + tiered roles) and
    //    audited with rich detail through CompanionPlugin.SrvAudit.
    //  * ONE Harmony class per target method; each applied in its own try/catch so a game update degrades
    //    one capability instead of taking the server down.
    //  * No vanilla member is touched that the specs did not verify; rejection codes and message types are
    //    written as plain ints (cited below) so a renamed enum cannot break us at runtime.
    internal static class Wave1Moderation
    {
        // ---- store tables / logs (shared contract with the panel + sibling wave files) ----
        private const string TblTempBans = "tempbans";    // id -> "expiryTicksUtc|reason"
        private const string TblMutes = "mutes";          // id -> "expiryTicksUtc"        (owned by Wave1Chat)
        private const string TblWatch = "watchlist";      // id -> "1"
        private const string TblWarnings = "warnings";    // id -> "count|lastReason|lastTicksUtc"
        private const string TblPresence = "presence";    // id -> "first|last|sessions|totalSeconds|lastName"
        private const string TblModState = "modstate";    // "lockdown" -> "0|1"
        private const string KeyLockdown = "lockdown";

        // ZNet.ConnectionStatus (ZNet.cs:23-38) — written as ints so a renamed/reordered enum in a future
        // game build cannot throw a type-load error inside a join handler.
        private const int ErrBanned = 8;   // ConnectionStatus.ErrorBanned
        private const int ErrFull = 9;     // ConnectionStatus.ErrorFull
        // MessageHud.MessageType.Center == 2 (MessageHud.cs:8-12); same reasoning.
        private const int MsgCenter = 2;

        private const int StateCap = 50;        // per-list cap in the AP_ModState reply
        private const int MaxTextLen = 200;     // reason / text clamp — never trust a client string length
        private const int MaxIdLen = 64;
        private const int MaxMinutes = 525600;  // one year; a temp ban longer than that is a real ban
        private const float FreezeSlack = 2f;   // metres of drift tolerated before we yank the player back
        private const double AltWindowMinutes = 5d;

        // ---- config ----
        private static ConfigEntry<bool> _enableWarnEscalation;
        private static ConfigEntry<int> _warnMuteThreshold;
        private static ConfigEntry<int> _warnMuteMinutes;
        private static ConfigEntry<int> _warnTempbanThreshold;
        private static ConfigEntry<int> _warnTempbanMinutes;
        private static ConfigEntry<bool> _enableAltAlerts;

        private static bool WarnEscalationOn => _enableWarnEscalation != null && _enableWarnEscalation.Value;
        private static int WarnMuteThreshold => _warnMuteThreshold != null ? Mathf.Max(1, _warnMuteThreshold.Value) : 2;
        private static int WarnMuteMinutes => _warnMuteMinutes != null ? Mathf.Clamp(_warnMuteMinutes.Value, 1, MaxMinutes) : 10;
        private static int WarnTempbanThreshold => _warnTempbanThreshold != null ? Mathf.Max(1, _warnTempbanThreshold.Value) : 3;
        private static int WarnTempbanMinutes => _warnTempbanMinutes != null ? Mathf.Clamp(_warnTempbanMinutes.Value, 1, MaxMinutes) : 60;
        private static bool AltAlertsOn => _enableAltAlerts == null || _enableAltAlerts.Value;

        // ---- in-memory state (never persisted; all of it is session-scoped by nature) ----
        private sealed class FrozenPeer
        {
            public Vector3 Pin;
            public string Name;
        }

        private static readonly Dictionary<long, FrozenPeer> Frozen = new Dictionary<long, FrozenPeer>();

        // uid -> (join ticks, platform id) — pairs join/leave so the double ZNet.Disconnect a kick fires
        // can only ever close one session (same discipline as CompanionPlugin's _loggedPeers).
        private static readonly Dictionary<long, KeyValuePair<long, string>> JoinedAt =
            new Dictionary<long, KeyValuePair<long, string>>();

        // Report-only ban-evasion heuristic input: (ticks, id) of everyone we removed recently.
        private static readonly List<KeyValuePair<long, string>> RecentRemovals = new List<KeyValuePair<long, string>>();

        private static float _nextFreezeTick;
        private static float _nextPruneTick;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableWarnEscalation = cfg.Bind("Features", "EnableWarnEscalation", false,
                    "Automatically mute / temp-ban a player once their warning count crosses the thresholds below. OFF by default: warnings stay a report-only record until an owner opts in.");
                _warnMuteThreshold = cfg.Bind("Features", "WarnMuteThreshold", 2,
                    "Warning count at which an automatic mute is applied (requires EnableWarnEscalation).");
                _warnMuteMinutes = cfg.Bind("Features", "WarnMuteMinutes", 10,
                    "Length in minutes of the automatic mute triggered by WarnMuteThreshold.");
                _warnTempbanThreshold = cfg.Bind("Features", "WarnTempbanThreshold", 3,
                    "Warning count at which an automatic temp-ban is applied (requires EnableWarnEscalation).");
                _warnTempbanMinutes = cfg.Bind("Features", "WarnTempbanMinutes", 60,
                    "Length in minutes of the automatic temp-ban triggered by WarnTempbanThreshold.");
                _enableAltAlerts = cfg.Bind("Features", "EnableAltAlerts", true,
                    "Report-only: alert online admins when an id that has NEVER been seen on this world before joins within 5 minutes of a temp-ban or an admin kick. Heuristic only — the game exposes no IP address to plugins, so this cannot prove anything.");
            }

            // Every new admin RPC must be known to the audit chokepoint. Grants: moderation is a moderator
            // job; lockdown closes the whole server, so it stays owner-only (null) when roles are enforced.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTempBan", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUnTempBan", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvFreeze", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWarn", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWatch", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLockdown", null);
            // AP_SrvModStateReq is deliberately NOT registered: the chokepoint writes one audit line (a
            // synchronous file append on the main thread) per matching packet, and the panel polls this read
            // every 15 s from any client that sends it. Its role gate lives in OnModStateReq instead.

            try { Harmony.CreateAndPatchAll(typeof(RpcRegisterPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"RpcRegisterPatch failed (moderation RPCs unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(JoinGatePatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"JoinGatePatch failed (temp-ban/lockdown join enforcement unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(JoinWatchPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"JoinWatchPatch failed (watchlist alerts / presence / alt alerts unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(PeerLeavePatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"PeerLeavePatch failed (presence session length unavailable): {e.Message}"); }
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;

            if (now >= _nextFreezeTick)
            {
                // Player.TeleportTo silently refuses inside a 2 s cooldown (Player.cs:5467-5493), so the
                // holding loop must never run faster than that or every second yank is a no-op.
                _nextFreezeTick = now + 2.5f;
                try { EnforceFreeze(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Freeze tick failed: {e.Message}"); }
            }

            if (now >= _nextPruneTick)
            {
                _nextPruneTick = now + 60f;
                try { PruneTempBans(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Temp-ban prune failed: {e.Message}"); }
                try { PruneRecentRemovals(); }
                catch (Exception) { }
            }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTempBan", OnTempBan);
                    ZRoutedRpc.instance.Register<string>("AP_SrvUnTempBan", OnUnTempBan);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvFreeze", OnFreeze);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvWarn", OnWarn);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvWatch", OnWatch);
                    ZRoutedRpc.instance.Register<bool>("AP_SrvLockdown", OnLockdown);
                    // No-arg RPCs must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvModStateReq", new Action<long>(OnModStateReq));
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Moderation RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== temp bans ====================

        // ZPackage: string id, int minutes, string reason.
        private static void OnTempBan(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTempBan")) return;
            string id, reason; int minutes;
            try
            {
                id = CleanId(pkg.ReadString());
                minutes = pkg.ReadInt();
                reason = CleanText(pkg.ReadString());
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvTempBan: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(id)) return;
            minutes = Mathf.Clamp(minutes, 1, MaxMinutes);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            ApplyTempBan(id, minutes, reason, admin, sender);
            CompanionPlugin.NotifySender(sender, $"Temp-banned {id} for {minutes} min");
        }

        // Shared by the RPC handler and the warning auto-escalation path.
        private static void ApplyTempBan(string id, int minutes, string reason, string admin, long auditSender)
        {
            var expiry = DateTime.UtcNow.AddMinutes(minutes).Ticks;
            var t = FeatureStore.Table(TblTempBans);
            // Canonicalize the key the way OnWarn/OnWatch/RecordJoin do. The panel's id box is free text, so
            // the same player can arrive once as "Steam_<id>" and once as the bare "<id>"; two rows for one
            // player means the join gate can hit the stale one and let a live ban through.
            var key = FindKey(t, id) ?? id;
            t[key] = expiry.ToString() + "|" + reason;
            FeatureStore.SaveTable(TblTempBans);

            // Kick if online. The id we were given may be the full "Platform_id" or the bare one — the
            // socket reports whichever form its backend uses, so match both ways (same leniency as the
            // adminlist read path in CompanionPlugin.SenderIsAdmin).
            var kicked = false;
            string kickedName = null;
            var peer = FindPeerById(key);
            if (peer != null)
            {
                kickedName = peer.m_playerName;
                Frozen.Remove(peer.m_uid);
                kicked = CompanionPlugin.FeatureKick(peer.m_uid);
            }
            NoteRemoval(key);

            CompanionPlugin.SrvAudit(auditSender, "TEMPBAN",
                $"id={key} name={kickedName ?? "?"} minutes={minutes} expiryUtc={new DateTime(expiry, DateTimeKind.Utc):yyyy-MM-dd'T'HH:mm:ss'Z'} online={kicked} reason={reason}");
            CompanionPlugin.FeatureLog($"Temp-ban {key} for {minutes}m by {admin}: {reason} (online: {kicked})");
            Wave1AuditRpc.PostModLog($"TEMPBAN {key} {minutes}m: {reason} (by {admin})");
            NotifyOnlineAdmins($"Temp-ban: {kickedName ?? id} for {minutes} min ({reason})");
        }

        private static void OnUnTempBan(long sender, string rawId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvUnTempBan")) return;
            var id = CleanId(rawId);
            if (string.IsNullOrEmpty(id)) return;

            var t = FeatureStore.Table(TblTempBans);
            var key = FindKey(t, id);
            if (key == null)
            {
                CompanionPlugin.NotifySender(sender, $"No active temp-ban for {id}");
                CompanionPlugin.SrvAudit(sender, "UNTEMPBAN", $"id={id} result=not-found");
                return;
            }
            t.Remove(key);
            FeatureStore.SaveTable(TblTempBans);
            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "UNTEMPBAN", $"id={key} result=removed");
            CompanionPlugin.FeatureLog($"Temp-ban lifted for {key} by {admin}");
            Wave1AuditRpc.PostModLog($"UNTEMPBAN {key} (by {admin})");
            CompanionPlugin.NotifySender(sender, $"Temp-ban lifted for {key}");
        }

        // Expired entries are dropped on a 60 s cadence; SaveTable only runs when something actually
        // changed, so an idle server does not rewrite the file every minute.
        private static void PruneTempBans()
        {
            var t = FeatureStore.Table(TblTempBans);
            if (t.Count == 0) return;
            var nowTicks = DateTime.UtcNow.Ticks;
            List<string> dead = null;
            foreach (var kv in t)
            {
                long expiry; string reason;
                if (!ParseTempBan(kv.Value, out expiry, out reason) || expiry <= nowTicks)
                    (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var id in dead)
            {
                t.Remove(id);
                CompanionPlugin.FeatureLog($"Temp-ban expired for {id}");
                Wave1AuditRpc.PostModLog($"TEMPBAN EXPIRED {id}");
            }
            FeatureStore.SaveTable(TblTempBans);
        }

        // Returns the active temp-ban entry matching this platform id, or null. EVERY matching spelling has
        // to be inspected: a legacy table can still hold both "Steam_<id>" and the bare "<id>", and stopping
        // at the first (expired or unparseable) one would let a live ban be bypassed until the pruner runs.
        private static string ActiveTempBanKey(string hostId)
        {
            var t = FeatureStore.Table(TblTempBans);
            if (t.Count == 0) return null;
            var nowTicks = DateTime.UtcNow.Ticks;
            foreach (var kv in t)
            {
                if (!IdMatches(kv.Key, hostId)) continue;
                long expiry; string reason;
                if (ParseTempBan(kv.Value, out expiry, out reason) && expiry > nowTicks) return kv.Key;
                // matched but expired/unreadable — keep looking; the pruner will drop it
            }
            return null;
        }

        // ==================== freeze ====================

        // ZPackage: long targetUid, bool on.
        private static void OnFreeze(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvFreeze")) return;
            long uid; bool on;
            try { uid = pkg.ReadLong(); on = pkg.ReadBool(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvFreeze: malformed packet dropped ({e.Message})"); return; }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var peer = FindPeerByUid(uid);
            if (!on)
            {
                var had = Frozen.ContainsKey(uid);
                Frozen.Remove(uid);
                CompanionPlugin.SrvAudit(sender, "UNFREEZE", $"uid={uid} name={(peer != null ? peer.m_playerName : "?")} wasFrozen={had}");
                if (peer != null) SendPlayerText(uid, "You have been unfrozen.");
                Wave1AuditRpc.PostModLog($"UNFREEZE {(peer != null ? peer.m_playerName : uid.ToString())} (by {admin})");
                CompanionPlugin.NotifySender(sender, "Player unfrozen");
                return;
            }

            if (peer == null)
            {
                CompanionPlugin.SrvAudit(sender, "FREEZE", $"uid={uid} result=peer-not-found");
                CompanionPlugin.NotifySender(sender, "Freeze: player is not connected");
                return;
            }
            // m_refPos is the client's own continuously-reported reference position (ZNetPeer.cs:15) — the
            // only position the server has for a player whose character ZDO it does not simulate.
            Frozen[uid] = new FrozenPeer { Pin = peer.m_refPos, Name = peer.m_playerName ?? "?" };
            CompanionPlugin.SrvAudit(sender, "FREEZE", $"uid={uid} name={peer.m_playerName} pin={peer.m_refPos}");
            SendPlayerText(uid, "You have been frozen by an admin");
            Wave1AuditRpc.PostModLog($"FREEZE {peer.m_playerName} (by {admin})");
            NotifyOnlineAdmins($"Freeze: {peer.m_playerName} frozen by {admin}");
            CompanionPlugin.NotifySender(sender, $"Froze {peer.m_playerName}");
        }

        // Vanilla-client-safe jail: there is no RPC that roots a player, so we simply put anyone who drifts
        // more than FreezeSlack metres back on their pin. Paced at 2.5 s by Tick (TeleportTo refuses inside
        // its own 2 s cooldown).
        private static void EnforceFreeze()
        {
            if (Frozen.Count == 0) return;
            List<long> gone = null;
            foreach (var kv in Frozen)
            {
                var peer = FindPeerByUid(kv.Key);
                if (peer == null) { (gone ?? (gone = new List<long>())).Add(kv.Key); continue; }
                if (Vector3.Distance(peer.m_refPos, kv.Value.Pin) <= FreezeSlack) continue;
                try
                {
                    // Chat.RPC_TeleportPlayer is registered on EVERY client (Chat.cs:130) and has no sender
                    // or admin check — this is the one teleport that works on unmodded clients.
                    ZRoutedRpc.instance.InvokeRoutedRPC(kv.Key, "RPC_TeleportPlayer", kv.Value.Pin, Quaternion.identity, true);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Freeze teleport failed for {kv.Value.Name}: {e.Message}"); }
            }
            if (gone == null) return;
            foreach (var uid in gone) Frozen.Remove(uid);
        }

        // ==================== warnings + auto-escalation ====================

        // ZPackage: string id, string reason.
        private static void OnWarn(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWarn")) return;
            string id, reason;
            try { id = CleanId(pkg.ReadString()); reason = CleanText(pkg.ReadString()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvWarn: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(id)) return;
            if (reason.Length == 0) reason = "no reason given";

            var t = FeatureStore.Table(TblWarnings);
            var key = FindKey(t, id) ?? id;
            int count; string prevReason; long prevTicks;
            ParseWarning(t.TryGetValue(key, out var raw) ? raw : null, out count, out prevReason, out prevTicks);
            count++;
            t[key] = count.ToString() + "|" + reason + "|" + DateTime.UtcNow.Ticks;
            FeatureStore.SaveTable(TblWarnings);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var peer = FindPeerById(key);
            CompanionPlugin.SrvAudit(sender, "WARN",
                $"id={key} name={(peer != null ? peer.m_playerName : "?")} count={count} online={(peer != null)} reason={reason}");
            CompanionPlugin.FeatureLog($"Warning #{count} for {key} by {admin}: {reason}");
            Wave1AuditRpc.PostModLog($"WARN {key} #{count}: {reason} (by {admin})");
            if (peer != null) SendPlayerText(peer.m_uid, $"Warning #{count} from an admin: {reason}");
            NotifyOnlineAdmins($"Warning #{count} issued to {(peer != null ? peer.m_playerName : key)}: {reason}");
            CompanionPlugin.NotifySender(sender, $"Warned {key} (#{count})");

            if (!WarnEscalationOn) return;
            // Highest tier first: a player who crossed the temp-ban threshold should not merely be muted.
            if (count >= WarnTempbanThreshold)
            {
                var minutes = WarnTempbanMinutes;
                CompanionPlugin.SrvAudit(sender, "WARN-ESCALATE",
                    $"id={key} count={count} threshold={WarnTempbanThreshold} action=tempban minutes={minutes}");
                ApplyTempBan(key, minutes, $"auto: {count} warnings", "auto-escalation", sender);
                return;
            }
            if (count >= WarnMuteThreshold)
            {
                var minutes = WarnMuteMinutes;
                CompanionPlugin.SrvAudit(sender, "WARN-ESCALATE",
                    $"id={key} count={count} threshold={WarnMuteThreshold} action=mute minutes={minutes}");
                try { Wave1Chat.MuteId(key, minutes); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Auto-mute failed for {key}: {e.Message}"); return; }
                CompanionPlugin.FeatureLog($"Auto-mute {key} for {minutes}m ({count} warnings)");
                Wave1AuditRpc.PostModLog($"AUTOMUTE {key} {minutes}m ({count} warnings)");
                NotifyOnlineAdmins($"Auto-mute: {key} muted {minutes} min after {count} warnings");
            }
        }

        // ==================== watchlist ====================

        // ZPackage: string id, bool on.
        private static void OnWatch(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvWatch")) return;
            string id; bool on;
            try { id = CleanId(pkg.ReadString()); on = pkg.ReadBool(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvWatch: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(id)) return;

            var t = FeatureStore.Table(TblWatch);
            var key = FindKey(t, id);
            if (on) t[key ?? id] = "1";
            else if (key != null) t.Remove(key);
            FeatureStore.SaveTable(TblWatch);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, on ? "WATCH-ADD" : "WATCH-REMOVE", $"id={key ?? id}");
            CompanionPlugin.FeatureLog($"Watchlist {(on ? "add" : "remove")} {key ?? id} by {admin}");
            Wave1AuditRpc.PostModLog($"WATCH {(on ? "ON" : "OFF")} {key ?? id} (by {admin})");
            CompanionPlugin.NotifySender(sender, on ? $"Watching {id}" : $"No longer watching {id}");
        }

        // ==================== lockdown ====================

        private static void OnLockdown(long sender, bool on)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLockdown")) return;

            var t = FeatureStore.Table(TblModState);
            t[KeyLockdown] = on ? "1" : "0";
            FeatureStore.SaveTable(TblModState);

            var admin = CompanionPlugin.SenderDisplayName(sender);
            CompanionPlugin.SrvAudit(sender, "LOCKDOWN", $"state={(on ? "ON" : "OFF")}");
            CompanionPlugin.FeatureLog($"Lockdown {(on ? "ENABLED" : "DISABLED")} by {admin}");
            Wave1AuditRpc.PostModLog($"LOCKDOWN {(on ? "ON" : "OFF")} (by {admin})");
            NotifyOnlineAdmins(on
                ? $"Lockdown ENABLED by {admin} — only admins can join"
                : $"Lockdown DISABLED by {admin}");
            CompanionPlugin.NotifySender(sender, on ? "Lockdown enabled" : "Lockdown disabled");
        }

        private static bool LockdownOn()
        {
            var t = FeatureStore.Table(TblModState);
            return t.TryGetValue(KeyLockdown, out var v) && v == "1";
        }

        // ==================== join / leave hooks ====================

        // PREFIX — the only place a connection can be refused before the peer is admitted. Identity here is
        // peer.m_socket.GetHostName() ONLY: ZNet.RPC_PeerInfo assigns m_uid/m_playerName after its own
        // rejection ladder (ZNet.cs:931-933), so the name is not readable yet. Both rules below are id-based
        // for exactly that reason. Never return false without invoking "Error" — the client would hang on
        // "Connecting" forever.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class JoinGatePatch
        {
            private static bool Prefix(ZNet __instance, ZRpc rpc)
            {
                try
                {
                    if (__instance == null || !__instance.IsServer() || rpc == null) return true;
                    ZNetPeer peer = null;
                    foreach (var p in __instance.GetPeers())
                        if (p != null && p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null || peer.m_socket == null) return true;
                    var host = peer.m_socket.GetHostName();
                    if (string.IsNullOrEmpty(host)) return true;

                    var banKey = ActiveTempBanKey(host);
                    if (banKey != null)
                    {
                        long expiry; string reason;
                        ParseTempBan(FeatureStore.Table(TblTempBans)[banKey], out expiry, out reason);
                        var mins = Math.Max(1, (int)(new DateTime(expiry, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes);
                        CompanionPlugin.FeatureLog($"Refused temp-banned {host} ({mins} min left): {reason}");
                        NotifyOnlineAdmins($"Temp-banned player {host} tried to join ({mins} min left)");
                        Reject(rpc, peer, ErrBanned);
                        return false;
                    }

                    if (LockdownOn() && !CompanionPlugin.FeatureIsAdminId(host))
                    {
                        CompanionPlugin.FeatureLog($"Refused {host}: server is in lockdown");
                        NotifyOnlineAdmins($"Lockdown refused a join from {host}");
                        Reject(rpc, peer, ErrFull);
                        return false;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    // Fail OPEN: a bug in moderation must never make the server unjoinable.
                    CompanionPlugin.FeatureLog($"Join gate error (connection allowed): {e.Message}");
                    return true;
                }
            }

            private static void Reject(ZRpc rpc, ZNetPeer peer, int code)
            {
                try { rpc.Invoke("Error", code); }
                catch (Exception) { }
                // Explicit teardown so the socket does not linger until the connect timeout. ZNet.Disconnect
                // is public today but reached reflectively so a signature change degrades to "peer times
                // out" instead of throwing inside the connection handler. The socket's Close() flushes its
                // send queue first, so the Error above still reaches the client.
                try
                {
                    var m = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
                    if (m != null) m.Invoke(ZNet.instance, new object[] { peer });
                }
                catch (Exception) { }
            }
        }

        // POSTFIX — by now m_uid / m_playerName are filled (that is why the vanilla join logger uses a
        // postfix too). Watchlist alerts, presence bookkeeping and the alt heuristic all live here.
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class JoinWatchPatch
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
                    if (peer == null || peer.m_socket == null) return;
                    var host = peer.m_socket.GetHostName();
                    // A rejected connection never gets a name — same filter the vanilla join log uses.
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(peer.m_playerName)) return;
                    var name = CleanText(peer.m_playerName);

                    RecordJoin(peer.m_uid, host, name);

                    var watchKey = FindKey(FeatureStore.Table(TblWatch), host);
                    if (watchKey != null)
                    {
                        CompanionPlugin.FeatureLog($"Watchlist: {name} ({host}) joined");
                        NotifyOnlineAdmins($"Watchlist: {name} ({host}) joined");
                    }

                    if (AltAlertsOn) CheckAltHeuristic(host);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Join watch error: {e.Message}"); }
            }
        }

        // PREFIX on Disconnect — the peer is still readable here (it is disposed by the original). A kick
        // calls Disconnect twice; JoinedAt pairs by uid so only the first one closes the session.
        [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
        internal static class PeerLeavePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                try
                {
                    if (__instance == null || !__instance.IsServer() || peer == null) return;
                    Frozen.Remove(peer.m_uid);
                    RecordLeave(peer.m_uid);
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Peer leave presence error: {e.Message}"); }
            }
        }

        // ==================== presence ====================

        private static void RecordJoin(long uid, string host, string name)
        {
            if (!JoinedAt.ContainsKey(uid))
                JoinedAt[uid] = new KeyValuePair<long, string>(DateTime.UtcNow.Ticks, host);

            var t = FeatureStore.Table(TblPresence);
            var key = FindKey(t, host) ?? host;
            long first, last, seconds; int sessions; string lastName;
            ParsePresence(t.TryGetValue(key, out var raw) ? raw : null, out first, out last, out sessions, out seconds, out lastName);
            var nowTicks = DateTime.UtcNow.Ticks;
            if (first == 0) first = nowTicks;
            sessions++;
            t[key] = $"{first}|{nowTicks}|{sessions}|{seconds}|{name}";
            FeatureStore.SaveTable(TblPresence);   // rare event (one write per join), durability wins
        }

        private static void RecordLeave(long uid)
        {
            if (!JoinedAt.TryGetValue(uid, out var entry)) return;   // no paired join (or already closed)
            JoinedAt.Remove(uid);

            var t = FeatureStore.Table(TblPresence);
            var key = FindKey(t, entry.Value);
            if (key == null) return;
            long first, last, seconds; int sessions; string lastName;
            ParsePresence(t[key], out first, out last, out sessions, out seconds, out lastName);
            var nowTicks = DateTime.UtcNow.Ticks;
            var session = (long)Math.Max(0d, (new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(entry.Key, DateTimeKind.Utc)).TotalSeconds);
            t[key] = $"{first}|{nowTicks}|{sessions}|{seconds + session}|{lastName}";
            FeatureStore.SaveTable(TblPresence);
        }

        // ==================== alt / ban-evasion heuristic (report only) ====================

        // HARD LIMIT, do not promise more: the game's socket abstraction exposes NO IP address
        // (ZSteamSocket.GetEndPointString returns the SteamID, ZSteamSocket.cs:327-330). All this can
        // correlate is timing plus "never seen on this world before", so it is a hint for a human, never
        // evidence and never an automatic action. Fed by ApplyTempBan and by the admin kick handler
        // (Wave1AuditRpc.Wave1KickModLogPatch), which is what EnableAltAlerts' description promises.
        internal static void NoteRemoval(string id)
        {
            RecentRemovals.Add(new KeyValuePair<long, string>(DateTime.UtcNow.Ticks, id));
            PruneRecentRemovals();
        }

        private static void PruneRecentRemovals()
        {
            if (RecentRemovals.Count == 0) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-AltWindowMinutes).Ticks;
            for (var i = RecentRemovals.Count - 1; i >= 0; i--)
                if (RecentRemovals[i].Key < cutoff) RecentRemovals.RemoveAt(i);
            if (RecentRemovals.Count > 50) RecentRemovals.RemoveRange(0, RecentRemovals.Count - 50);
        }

        private static void CheckAltHeuristic(string host)
        {
            PruneRecentRemovals();
            if (RecentRemovals.Count == 0) return;
            // Timing alone is not a signal: on any populated server somebody joins within the window after
            // every removal, and heading that alert "possible ban evasion" with their name defames a regular.
            // Require the one concrete correlate the store actually holds — this id has never played on this
            // world before — and never flag an admin. RecordJoin ran first, so its own session is counted.
            if (CompanionPlugin.FeatureIsAdminId(host)) return;
            var pres = FeatureStore.Table(TblPresence);
            var pkey = FindKey(pres, host);
            long first, last, seconds; int sessions; string lastName;
            ParsePresence(pkey != null ? pres[pkey] : null, out first, out last, out sessions, out seconds, out lastName);
            // Exactly 1 = the session RecordJoin just opened. Anything else is either a returning player or
            // a presence row we could not read, and neither is worth naming someone over.
            if (sessions != 1) return;

            foreach (var entry in RecentRemovals)
            {
                if (IdMatches(entry.Value, host)) continue;   // the same player reconnecting is not an alt
                var mins = Math.Max(0, (int)(DateTime.UtcNow - new DateTime(entry.Key, DateTimeKind.Utc)).TotalMinutes);
                // Names only the JOINING id, and says plainly what this is: nothing here identifies an
                // account as anyone's alt, so no other player is named alongside it.
                var text = $"Possible (timing only): {host} has never played on this world and joined {mins} min after a removal. The game exposes no IP address to plugins, so this is a hint for a human, not evidence.";
                CompanionPlugin.FeatureLog(text);
                NotifyOnlineAdmins(text);
                return;   // one alert per join is enough
            }
        }

        // ==================== AP_SrvModStateReq -> AP_ModState ====================

        private static void OnModStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Not an audited RPC (see Init), so BuiltinRoles carries no grant for this name — a moderator is
            // recognised through a moderation grant they already hold, otherwise flipping this read out of
            // the audited set would silently demote it to owner-only whenever EnableTieredRoles is on.
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvModStateReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvTempBan")) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            var pkg = new ZPackage();
            pkg.Write(1);                 // payload version — bump, never reorder
            pkg.Write(LockdownOn());

            // Temp-bans (active only; expired rows are the pruner's business).
            var tb = new List<KeyValuePair<string, string>>();
            foreach (var kv in FeatureStore.Table(TblTempBans))
            {
                long expiry; string reason;
                if (!ParseTempBan(kv.Value, out expiry, out reason) || expiry <= nowTicks) continue;
                tb.Add(kv);
                if (tb.Count >= StateCap) break;
            }
            pkg.Write(tb.Count);
            foreach (var kv in tb)
            {
                long expiry; string reason;
                ParseTempBan(kv.Value, out expiry, out reason);
                pkg.Write(kv.Key); pkg.Write(expiry); pkg.Write(reason);
            }

            // Mutes (table owned by the chat/mute module; read-only here).
            var mu = new List<KeyValuePair<string, long>>();
            foreach (var kv in FeatureStore.Table(TblMutes))
            {
                if (!long.TryParse(kv.Value, out var expiry) || expiry <= nowTicks) continue;
                mu.Add(new KeyValuePair<string, long>(kv.Key, expiry));
                if (mu.Count >= StateCap) break;
            }
            pkg.Write(mu.Count);
            foreach (var kv in mu) { pkg.Write(kv.Key); pkg.Write(kv.Value); }

            // Watchlist.
            var wa = new List<string>();
            foreach (var kv in FeatureStore.Table(TblWatch))
            {
                wa.Add(kv.Key);
                if (wa.Count >= StateCap) break;
            }
            pkg.Write(wa.Count);
            foreach (var id in wa) pkg.Write(id);

            // Frozen players (in-memory, session state).
            var fr = new List<KeyValuePair<long, string>>();
            foreach (var kv in Frozen)
            {
                fr.Add(new KeyValuePair<long, string>(kv.Key, kv.Value.Name ?? "?"));
                if (fr.Count >= StateCap) break;
            }
            pkg.Write(fr.Count);
            foreach (var kv in fr) { pkg.Write(kv.Key); pkg.Write(kv.Value); }

            // Warnings.
            var wn = new List<string>();
            foreach (var kv in FeatureStore.Table(TblWarnings))
            {
                wn.Add(kv.Key);
                if (wn.Count >= StateCap) break;
            }
            pkg.Write(wn.Count);
            foreach (var id in wn)
            {
                int count; string reason; long ticks;
                ParseWarning(FeatureStore.Table(TblWarnings)[id], out count, out reason, out ticks);
                pkg.Write(id); pkg.Write(count); pkg.Write(reason);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_ModState", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_ModState reply failed: {e.Message}"); }
        }

        // ==================== shared helpers ====================

        /// <summary>Push a line to every connected admin's panel (AP_Msg — admins run the panel).</summary>
        internal static void NotifyOnlineAdmins(string text)
        {
            if (string.IsNullOrEmpty(text) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    var host = peer.m_socket.GetHostName();
                    if (string.IsNullOrEmpty(host) || !CompanionPlugin.FeatureIsAdminId(host)) continue;
                    CompanionPlugin.NotifySender(peer.m_uid, text);
                }

                // A listen-server HOST is never in ZNet.m_peers (CompanionPlugin.cs:141-151), so without
                // this the one admin actually running the panel receives none of these alerts. A local
                // player only exists on a listen server; a dedicated server skips this. AP_Msg addressed to
                // our own session id is dispatched locally and OnMessage accepts it (SenderIsServer ->
                // IsLocalHostSender), so nothing on the receiving side has to change.
                if (Player.m_localPlayer != null && ZDOMan.instance != null)
                    CompanionPlugin.NotifySender(ZDOMan.GetSessionID(), text);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"NotifyOnlineAdmins failed: {e.Message}"); }
        }

        /// <summary>
        /// Single-peer text that lands on UNMODDED clients. OnServerBroadcast (CompanionPlugin.cs:641-646)
        /// delivers with a routed RPC, but its "AP_Msg" name only exists on clients running the panel — a
        /// moderation notice must reach everyone, so this uses the same routed-RPC delivery with the name
        /// every vanilla client registers itself: MessageHud.Awake does
        /// Register&lt;int,string&gt;("ShowMessage", RPC_ShowMessage) (MessageHud.cs:111).
        /// </summary>
        internal static void SendPlayerText(long uid, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", MsgCenter, text); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"ShowMessage to {uid} failed: {e.Message}"); }
        }

        /// <summary>Full "Platform_id" or bare id — either form matches, exactly like the adminlist read.</summary>
        internal static bool IdMatches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(CompanionPlugin.FeatureBareId(a), CompanionPlugin.FeatureBareId(b),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (IdMatches(kv.Key, id)) return kv.Key;
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
                if (IdMatches(id, peer.m_socket.GetHostName())) return peer;
            }
            return null;
        }

        // Ids are stored AS ENTERED (trimmed), the same rule OnServerBanId documents: the game's own checks
        // compare the full networkUserId, so stripping a platform prefix silently no-ops crossplay ids.
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

        private static bool ParseTempBan(string value, out long expiry, out string reason)
        {
            expiry = 0; reason = "";
            if (string.IsNullOrEmpty(value)) return false;
            var bar = value.IndexOf('|');
            var head = bar < 0 ? value : value.Substring(0, bar);
            if (bar >= 0) reason = value.Substring(bar + 1);
            return long.TryParse(head, out expiry);
        }

        private static void ParseWarning(string value, out int count, out string reason, out long ticks)
        {
            count = 0; reason = ""; ticks = 0;
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split('|');
            if (parts.Length > 0) int.TryParse(parts[0], out count);
            if (parts.Length > 1) reason = parts[1];
            if (parts.Length > 2) long.TryParse(parts[2], out ticks);
        }

        private static void ParsePresence(string value, out long first, out long last, out int sessions,
            out long seconds, out string lastName)
        {
            first = 0; last = 0; sessions = 0; seconds = 0; lastName = "";
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 5);
            if (parts.Length > 0) long.TryParse(parts[0], out first);
            if (parts.Length > 1) long.TryParse(parts[1], out last);
            if (parts.Length > 2) int.TryParse(parts[2], out sessions);
            if (parts.Length > 3) long.TryParse(parts[3], out seconds);
            if (parts.Length > 4) lastName = parts[4];
        }
    }
}

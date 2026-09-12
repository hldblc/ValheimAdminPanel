using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;

namespace AdminPanelCompanion
{
    // ==================== Wave 1 — accountability RPC surface (server side) ====================
    // Three read/write endpoints the panel's accountability tab talks to, plus the mod-log fan-out that
    // every other Wave 1 module posts through:
    //
    //   AP_SrvAuditReq(int)      -> AP_AuditData   newest audit.log lines (owner-only: the trail contains
    //                                              OTHER admins' actions, so a moderator must not read it)
    //   AP_SrvRolesReq()         -> AP_RolesData   roles + roleperms tables (owner-only)
    //   AP_SrvRoleSet(ZPackage)                    assign/clear one role (owner-only)
    //   AP_SrvRapSheetReq(string)-> AP_RapSheet    one player's whole history (moderator)
    //
    // Nothing here changes gameplay: it reports what FeatureStore already persists. Roles enforcement is
    // still gated by CompanionPlugin.RolesEnabledCfg (default OFF) — with roles off every adminlist admin
    // reaches all four endpoints exactly as they reach every other AP_Srv* RPC today.
    internal static class Wave1AuditRpc
    {
        // Discord (or any) webhook. Empty = disabled; the FeatureStore "modlog" log is written either way,
        // so the feature is fully usable with no external service configured.
        internal static ConfigEntry<string> ModLogWebhookCfg;

        private static bool _inited;
        private static int _tlsPrepared;   // Interlocked flag: SecurityProtocol is a process-global

        private const int AuditCap = 100;    // wire contract: shipped <= 100
        private const int WebhookContentCap = 1900;   // Discord hard-limits a message to 2000 chars

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                ModLogWebhookCfg = cfg.Bind("Features", "ModLogWebhookUrl", "",
                    "Webhook URL that moderation events (kick/ban/mute/warn/role changes) are POSTed to as JSON {\"content\":\"...\"}. Empty = disabled; events are always written to <world>/modlog.log regardless.");

            // Every new admin RPC goes through the chokepoint too (audit line + role enforcement before the
            // handler runs). null grant = owner-only once roles are enabled; unassigned admins default to
            // owner (see BuiltinRoles in FeaturesCore) so enabling roles never silently locks anyone out.
            // AP_SrvAuditReq is the exception: the panel polls it every 20 s while the Audit view is open,
            // so auditing it would make the viewer the log's biggest writer (one synchronous file append per
            // poll) and grow the very file it then has to read. Its owner-only gate lives in OnAuditReq.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRolesReq", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRoleSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRapSheetReq", "moderator");

            try { Harmony.CreateAndPatchAll(typeof(Wave1AuditRpcRegistration)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave1AuditRpcRegistration patch failed (audit/roles/rap-sheet RPCs unavailable): {e.Message}"); }

            // Kick/ban mod-log mirrors. These patch PRIVATE handlers of CompanionPlugin by reflection, so
            // they are the most fragile thing in this file: if either fails the actions still land in
            // audit.log via the chokepoint, only the webhook/mod-log copy is missing.
            try { Harmony.CreateAndPatchAll(typeof(Wave1KickModLogPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave1KickModLogPatch patch failed (kick mod-log entries unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(Wave1BanModLogPatch)); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave1BanModLogPatch patch failed (ban mod-log entries unavailable): {e.Message}"); }

            Wave1Roles.Init();
        }

        // ---------------- RPC registration (own ZNet.Awake postfix; stacking is sanctioned) ----------------

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave1AuditRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<int>("AP_SrvAuditReq", OnAuditReq);
                ZRoutedRpc.instance.Register("AP_SrvRolesReq", new Action<long>(Wave1Roles.OnRolesReq));
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvRoleSet", Wave1Roles.OnRoleSet);
                ZRoutedRpc.instance.Register<string>("AP_SrvRapSheetReq", OnRapSheetReq);
            }
        }

        // ---------------- AP_SrvAuditReq ----------------

        private static void OnAuditReq(long sender, int maxLines)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvAuditReq")) return;

            var want = maxLines < 1 ? 1 : (maxLines > AuditCap ? AuditCap : maxLines);
            var lines = FeatureStore.Tail("audit", want);   // file order = oldest first, newest LAST

            var pkg = new ZPackage();
            pkg.Write(1);                 // payload version
            // "total" is the shipped count on purpose. audit.log has no size bound, so the only way to know
            // the real total is a full pass over it — on the main thread, every 20 s, for the whole time the
            // panel's Audit view is open. The panel renders (total - shipped) as "N older lines are on the
            // server", and a number we cannot compute cheaply is better left unclaimed than guessed at.
            pkg.Write(lines.Count);
            pkg.Write(lines.Count);       // shipped (<= 100 by construction)
            for (var i = 0; i < lines.Count; i++) pkg.Write(lines[i] ?? "");
            CompanionPlugin.ReplyTo(sender, "AP_AuditData", pkg);
        }

        // ---------------- AP_SrvRapSheetReq ----------------

        private static void OnRapSheetReq(long sender, string id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRapSheetReq")) return;

            var clean = (id ?? "").Trim();
            if (clean.Length > 64) clean = clean.Substring(0, 64);

            string lastName = "";
            long firstTicks = 0L, lastTicks = 0L, playSeconds = 0L;
            var sessions = 0;
            var warnings = 0;
            var warnLast = "";
            var watchlisted = false;
            var tempbanned = false;
            var role = "";
            var chat = new List<string>();

            if (clean.Length > 0)
            {
                // presence: "firstTicks|lastTicks|sessions|totalSeconds|lastName" — the name is LAST and may
                // itself contain '|', so the split is bounded to 5 fields.
                var presence = LookupById(FeatureStore.Table("presence"), clean);
                if (presence != null)
                {
                    var p = presence.Split(new[] { '|' }, 5);
                    if (p.Length > 0) long.TryParse(p[0], out firstTicks);
                    if (p.Length > 1) long.TryParse(p[1], out lastTicks);
                    if (p.Length > 2) int.TryParse(p[2], out sessions);
                    if (p.Length > 3) long.TryParse(p[3], out playSeconds);
                    if (p.Length > 4) lastName = p[4];
                }

                // warnings: "count|lastReason|lastTicksUtc" — the reason sits in the MIDDLE and may contain
                // '|', so take the first field as the count, the last as the timestamp, and rejoin the rest.
                var warn = LookupById(FeatureStore.Table("warnings"), clean);
                if (warn != null)
                {
                    var w = warn.Split('|');
                    if (w.Length > 0) int.TryParse(w[0], out warnings);
                    if (w.Length == 2) warnLast = w[1];
                    else if (w.Length > 2) warnLast = string.Join("|", w, 1, w.Length - 2);
                }

                watchlisted = LookupById(FeatureStore.Table("watchlist"), clean) == "1";

                var tb = LookupById(FeatureStore.Table("tempbans"), clean);
                if (tb != null)
                {
                    var t = tb.Split(new[] { '|' }, 2);
                    long expiry;
                    tempbanned = long.TryParse(t[0], out expiry) && expiry > DateTime.UtcNow.Ticks;
                }

                role = LookupById(FeatureStore.Table("roles"), clean) ?? "";

                chat = RecentChatOf(clean, 20);
            }

            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(clean);
            pkg.Write(lastName ?? "");
            pkg.Write(firstTicks);
            pkg.Write(lastTicks);
            pkg.Write(sessions);
            pkg.Write(playSeconds);
            pkg.Write(warnings);
            pkg.Write(warnLast ?? "");
            pkg.Write(watchlisted);
            pkg.Write(tempbanned);
            pkg.Write(role ?? "");
            pkg.Write(chat.Count);        // <= 20 by construction
            for (var i = 0; i < chat.Count; i++) pkg.Write(chat[i] ?? "");
            CompanionPlugin.ReplyTo(sender, "AP_RapSheet", pkg);
        }

        // Newest `max` chat lines authored by `id`. The chat log is written by the chat module as
        // pipe-separated fields; rather than hard-coding which column holds the platform id (that shape
        // belongs to another file and could drift), match the id against the first few fields — a player
        // name or timestamp can never equal a platform id, so false positives are not a practical concern.
        private static List<string> RecentChatOf(string id, int max)
        {
            var res = new List<string>();
            var bare = CompanionPlugin.FeatureBareId(id) ?? id;
            var all = FeatureStore.Tail("chat", 500);
            for (var i = all.Count - 1; i >= 0 && res.Count < max; i--)
            {
                var line = all[i];
                if (string.IsNullOrEmpty(line)) continue;
                var f = line.Split('|');
                var hit = false;
                for (var j = 0; j < f.Length && j < 4; j++)
                {
                    var v = f[j].Trim();
                    if (v.Length == 0) continue;
                    if (string.Equals(v, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(v, bare, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                }
                if (hit) res.Add(line);
            }
            res.Reverse();   // oldest first, newest LAST — same ordering as every other log payload
            return res;
        }

        // Store keys are whatever the writer used (full "Steam_765..." or bare "765..."). Accept both, the
        // same leniency SenderIsAdmin/ResolveRole already apply to adminlist and role lookups.
        internal static string LookupById(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            string v;
            if (table.TryGetValue(id, out v)) return v;
            var bare = CompanionPlugin.FeatureBareId(id);
            if (!string.IsNullOrEmpty(bare) && bare != id && table.TryGetValue(bare, out v)) return v;
            foreach (var kv in table)
                if (SameId(kv.Key, id)) return kv.Value;
            return null;
        }

        internal static bool SameId(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            var ba = CompanionPlugin.FeatureBareId(a);
            var bb = CompanionPlugin.FeatureBareId(b);
            return !string.IsNullOrEmpty(ba) && string.Equals(ba, bb, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- mod-log fan-out ----------------

        /// <summary>
        /// Record one moderation event: always appended to the per-world "modlog" log, and additionally
        /// POSTed to ModLogWebhookUrl when configured. Never throws, never blocks the caller (the HTTP call
        /// runs on a thread-pool task) — safe to call straight from an RPC handler or a Harmony patch.
        /// </summary>
        internal static void PostModLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                FeatureStore.Append("modlog",
                    $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}|{text}");
            }
            catch (Exception) { }

            var url = ModLogWebhookCfg != null ? ModLogWebhookCfg.Value : null;
            if (string.IsNullOrEmpty(url)) return;
            url = url.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

            var content = text.Length > WebhookContentCap ? text.Substring(0, WebhookContentCap) : text;
            var body = Encoding.UTF8.GetBytes("{\"content\":\"" + JsonEscape(content) + "\"}");

            PrepareTls();
            // Fire-and-forget: a slow/dead webhook must never stall the server's main thread. Everything
            // inside is plain data (a byte[] and a string) — no Unity/ZNet access off the main thread.
            try
            {
                Task.Run(() =>
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Method = "POST";
                        req.ContentType = "application/json";
                        req.Timeout = 10000;
                        req.ReadWriteTimeout = 10000;
                        req.UserAgent = "AdminPanelCompanion/" + CompanionPlugin.PluginVersion;
                        req.ContentLength = body.Length;
                        try { req.ServicePoint.Expect100Continue = false; } catch (Exception) { }
                        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                        using (var resp = req.GetResponse()) { }   // drain + dispose; status is ignored
                    }
                    catch (Exception) { /* rate limit, DNS, TLS, 404 — a webhook is never load-bearing */ }
                });
            }
            catch (Exception) { }
        }

        // Discord (and every other modern endpoint) refuses anything below TLS 1.2, while Mono's default
        // SecurityProtocol on this runtime can still be SSL3|TLS1. OR it in once, process-wide, and only
        // when a webhook is actually used so a server with no webhook configured changes nothing.
        private static void PrepareTls()
        {
            if (Interlocked.Exchange(ref _tlsPrepared, 1) != 0) return;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { /* older/patched runtime without Tls12 in the enum — leave the default */ }
        }

        // Minimal, dependency-free JSON string escaping (net48 ships no JSON writer and the mod ships no
        // deps). Covers the two structural characters plus every C0 control code.
        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // "Name (bareid)" for a connected peer, or the raw uid when it can no longer be resolved.
        internal static string PeerLabel(long uid)
        {
            try
            {
                if (ZNet.instance == null) return uid.ToString();
                var peer = ZNet.instance.GetPeer(uid);
                if (peer == null) return uid.ToString();
                var host = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                var bare = CompanionPlugin.FeatureBareId(host);
                var name = string.IsNullOrEmpty(peer.m_playerName) ? "?" : peer.m_playerName;
                return string.IsNullOrEmpty(bare) ? name : $"{name} ({bare})";
            }
            catch (Exception) { return uid.ToString(); }
        }

        // Platform id ("Steam_765...") of a connected peer, or null once its socket is gone.
        internal static string PeerHostId(long uid)
        {
            try
            {
                var peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
                return peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            }
            catch (Exception) { return null; }
        }

        internal static string AdminLabel(long sender) =>
            $"{CompanionPlugin.SenderDisplayName(sender)} ({CompanionPlugin.SenderPlatformId(sender)})";
    }

    // ==================== roles: read + assign ====================
    internal static class Wave1Roles
    {
        private const int RoleCap = 100;    // wire contract: count <= 100
        private const int PermCap = 20;     // wire contract: permCount <= 20

        internal static void Init() { /* config + RPC registration live in Wave1AuditRpc.Init */ }

        // ---------------- AP_SrvRolesReq ----------------

        internal static void OnRolesReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRolesReq")) return;

            var roles = FeatureStore.Table("roles");
            var perms = FeatureStore.Table("roleperms");

            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(CompanionPlugin.RolesEnabledCfg != null && CompanionPlugin.RolesEnabledCfg.Value);

            var n = roles.Count < RoleCap ? roles.Count : RoleCap;
            pkg.Write(n);
            var i = 0;
            foreach (var kv in roles)
            {
                if (i >= n) break;
                pkg.Write(kv.Key ?? "");
                pkg.Write(kv.Value ?? "");
                i++;
            }

            var m = perms.Count < PermCap ? perms.Count : PermCap;
            pkg.Write(m);
            i = 0;
            foreach (var kv in perms)
            {
                if (i >= m) break;
                pkg.Write(kv.Key ?? "");
                pkg.Write(kv.Value ?? "");
                i++;
            }

            CompanionPlugin.ReplyTo(sender, "AP_RolesData", pkg);
        }

        // ---------------- AP_SrvRoleSet ----------------

        internal static void OnRoleSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRoleSet")) return;

            string id, role;
            try
            {
                id = pkg.ReadString();
                role = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRoleSet: malformed packet dropped ({e.Message})"); return; }

            id = (id ?? "").Trim();
            role = (role ?? "").Trim();
            // Clamp both sides: these become table keys/values written to disk, and an id or role with
            // whitespace could never match a real platform id or role lookup anyway.
            if (id.Length == 0 || id.Length > 64 || id.IndexOf(' ') >= 0)
            {
                CompanionPlugin.NotifySender(sender, "Role change rejected: invalid player id");
                return;
            }
            if (role.Length > 32 || role.IndexOf(' ') >= 0)
            {
                CompanionPlugin.NotifySender(sender, "Role change rejected: invalid role name");
                return;
            }

            var roles = FeatureStore.Table("roles");
            var existing = Wave1AuditRpc.LookupById(roles, id) ?? "";

            // ---- last-owner guard ----
            // An adminlist admin with NO entry in this table is treated as owner (BuiltinRoles comment in
            // FeaturesCore), so an EMPTY roles table is not an "owner-less" server and clearing an entry
            // (role == "") can only ever hand owner rights back. The single irreversible move is an owner
            // demoting THEMSELVES to a restricted role while no other explicit owner exists: AP_SrvRoleSet
            // is owner-only, so after that they could not undo it from the panel (only by editing
            // roles.txt on disk or turning EnableTieredRoles off). Refuse exactly that; everything else
            // — including demoting another admin — stays allowed, because the demoted admin is never the
            // only route back.
            var rolesOn = CompanionPlugin.RolesEnabledCfg != null && CompanionPlugin.RolesEnabledCfg.Value;
            var demoting = role.Length > 0 && !role.Equals("owner", StringComparison.OrdinalIgnoreCase);
            if (rolesOn && demoting && Wave1AuditRpc.SameId(CompanionPlugin.SenderPlatformId(sender), id)
                && !HasOtherExplicitOwner(roles, id))
            {
                CompanionPlugin.NotifySender(sender,
                    "Refused: you are the last owner. Give someone else the owner role first, or clear your own assignment instead.");
                CompanionPlugin.FeatureLog($"AP_SrvRoleSet refused: {Wave1AuditRpc.AdminLabel(sender)} tried to demote the last owner to '{role}'");
                return;
            }

            // Keep one entry per identity: an admin who stores "Steam_765..." and later "765..." would
            // otherwise leave a stale row that ResolveRole might still hit first.
            List<string> stale = null;
            foreach (var kv in roles)
                if (kv.Key != id && Wave1AuditRpc.SameId(kv.Key, id))
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale != null) foreach (var k in stale) roles.Remove(k);

            if (role.Length == 0) roles.Remove(id);
            else roles[id] = role;
            FeatureStore.SaveTable("roles");

            var admin = Wave1AuditRpc.AdminLabel(sender);
            if (role.Length == 0)
            {
                CompanionPlugin.SrvAudit(sender, "AP_SrvRoleSet", $"clear|{id}|was={existing}");
                Wave1AuditRpc.PostModLog($"ROLE {admin} cleared the role of {id} (was '{(existing.Length == 0 ? "none" : existing)}')");
                CompanionPlugin.NotifySender(sender, $"Role cleared for {id}");
            }
            else
            {
                CompanionPlugin.SrvAudit(sender, "AP_SrvRoleSet", $"set|{id}|{role}|was={existing}");
                Wave1AuditRpc.PostModLog($"ROLE {admin} set {id} to '{role}' (was '{(existing.Length == 0 ? "none" : existing)}')");
                CompanionPlugin.NotifySender(sender, $"Role of {id} is now {role}");
            }
            CompanionPlugin.FeatureLog($"Role change by {admin}: {id} -> {(role.Length == 0 ? "<cleared>" : role)}");
        }

        private static bool HasOtherExplicitOwner(Dictionary<string, string> roles, string id)
        {
            foreach (var kv in roles)
            {
                if (Wave1AuditRpc.SameId(kv.Key, id)) continue;
                if (!string.IsNullOrEmpty(kv.Value) && kv.Value.Equals("owner", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    // ==================== kick / ban -> mod-log mirrors ====================
    // The two existing handlers are PRIVATE static methods of CompanionPlugin, which Harmony can still
    // patch (AccessTools resolves them by name + signature). Patching instead of editing keeps rule 1:
    // no existing file is touched, and if a future refactor renames either method TargetMethod() returns
    // null, CreateAndPatchAll throws, Init logs it, and the chokepoint's audit line still records the kick.
    //
    // Prefix + postfix on the SAME target (one class, one target — the mixed-target trap is about
    // DIFFERENT targets in one class): the prefix reads the peer's name/id while the socket is still open,
    // because KickByUid tears the peer down before the postfix runs. Routed-RPC handlers are strictly
    // sequential on the main thread, so the single-slot handoff cannot interleave.

    [HarmonyPatch]
    internal static class Wave1KickModLogPatch
    {
        private static string _target;
        private static string _targetId;

        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(CompanionPlugin), "OnServerKick", new[] { typeof(long), typeof(long) });

        [HarmonyPrefix]
        private static void Prefix(long uid)
        {
            _target = Wave1AuditRpc.PeerLabel(uid);
            _targetId = Wave1AuditRpc.PeerHostId(uid);
        }

        [HarmonyPostfix]
        private static void Postfix(long sender, long uid)
        {
            var target = _target;
            var targetId = _targetId;
            _target = null;
            _targetId = null;
            // The handler itself denies non-admins (and the chokepoint drops role-denied calls before it
            // ever runs), so mirror only what actually executed.
            if (!CompanionPlugin.FeatureSenderIsAdmin(sender)) return;
            Wave1AuditRpc.PostModLog($"KICK {Wave1AuditRpc.AdminLabel(sender)} kicked {target ?? uid.ToString()}");
            // EnableAltAlerts' description promises a window after a temp-ban OR a kick; ApplyTempBan is the
            // only other feeder, so without this an admin kick opens no window at all.
            if (!string.IsNullOrEmpty(targetId)) Wave1Moderation.NoteRemoval(targetId);
        }
    }

    [HarmonyPatch]
    internal static class Wave1BanModLogPatch
    {
        private static string _target;

        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(CompanionPlugin), "OnServerBan", new[] { typeof(long), typeof(long) });

        [HarmonyPrefix]
        private static void Prefix(long uid) => _target = Wave1AuditRpc.PeerLabel(uid);

        [HarmonyPostfix]
        private static void Postfix(long sender, long uid)
        {
            var target = _target;
            _target = null;
            if (!CompanionPlugin.FeatureSenderIsAdmin(sender)) return;
            Wave1AuditRpc.PostModLog($"BAN {Wave1AuditRpc.AdminLabel(sender)} banned {target ?? uid.ToString()}");
        }
    }
}

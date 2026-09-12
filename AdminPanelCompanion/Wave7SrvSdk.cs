using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace AdminPanelCompanion
{
    // ==================== Wave 7 — server extension API + dry-run contract ====================
    // Two halves, both deliberately small:
    //
    //  1. Wave7Sdk (internal) owns the SERVER half of the global dry-run contract. An admin's panel sends
    //     AP_SrvDryRunSet(bool); we remember "this platform id is simulating" in memory and expose
    //     IsDryRun(sender) for other server modules to consult.
    //
    //     WHAT DRY-RUN IS NOT: a guarantee. There is deliberately NO Harmony interception of the RPC bus
    //     here. A generic prefix that swallowed every AP_Srv* call would have to guess which packets are
    //     destructive, would silently break read-only requests (the panel would sit on stale data and the
    //     admin would think the feature is broken), and would still miss every action a server module
    //     takes on its own timer. So dry-run is ADVISORY and OPT-IN PER FEATURE: only code that calls
    //     IsDryRun (server side) or SdkGuardDestructive (panel side) honours it. Claiming a server-wide
    //     "nothing will happen" switch would be false, and an admin who believed it would do real damage
    //     while thinking they were simulating. Wave7_Guard.cs on the panel is the reference opt-in.
    //
    //  2. AdminPanelCompanionApi (public) is the server-side extension surface for OTHER BepInEx plugins:
    //     admin/role checks, the audit trail, admin notifications, per-world storage in a namespace they
    //     cannot use to clobber ours, and the client-capability probe. Everything is a safe no-op that
    //     returns false/null when the companion is not ready or the caller is not on a server.
    internal static class Wave7Sdk
    {
        // Only "on" is ever stored, so the set stays as small as the number of admins actively
        // simulating, and an eviction can never silently flip an admin from "simulated" to "for real".
        private static readonly HashSet<string> DryRunOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxDryRunEntries = 200;

        private static ConfigEntry<bool> _allowDryRun;
        private static ConfigEntry<bool> _allowExtensionApi;

        // Default TRUE on purpose, and it is NOT a behaviour change on upgrade: nothing simulates until an
        // admin turns dry-run on in their own panel (the per-admin flag starts empty on every restart).
        // The safe direction is important here — if this defaulted off, an admin who enabled dry-run in
        // the panel would get client-side simulation while the SERVER kept acting for real on the actions
        // it owns, which is exactly the false sense of safety this contract exists to avoid.
        internal static bool DryRunAllowed => _allowDryRun == null || _allowDryRun.Value;
        internal static bool ExtensionApiAllowed => _allowExtensionApi == null || _allowExtensionApi.Value;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _allowDryRun = cfg.Bind("Features", "AllowDryRunMode", true,
                    "Let an admin put THEIR OWN session into dry-run (simulate) mode from the panel. Nothing simulates until an admin switches it on, and the flag is per-admin, in memory only, and cleared by a restart. Advisory: only features that opt in honour it.");
                _allowExtensionApi = cfg.Bind("Features", "AllowExtensionApi", true,
                    "Let other server plugins use AdminPanelCompanionApi (admin/role checks, audit entries, admin notices, namespaced storage). Inert unless another plugin actually calls it.");
            }

            // Both grants: dry-run is a per-admin safety preference, not a power, so a role-restricted
            // moderator or builder must be able to set it for themselves. RegisterAuditedRpc is additive,
            // so calling it twice just adds the name to both builtin role sets.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDryRunSet", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvDryRunSet", "builder");

            var ok = true;
            try { Harmony.CreateAndPatchAll(typeof(RpcRegisterPatch)); }
            catch (Exception e)
            {
                ok = false;
                CompanionPlugin.FeatureLog($"Wave7 SDK RpcRegisterPatch failed (dry-run switch unavailable): {e.Message}");
            }
            try { Wave2Ops.ReportPatch("Wave7Sdk.RpcRegisterPatch", ok); }
            catch (Exception) { }

            // Chat fallback so an admin can flip their own simulate switch without the panel open.
            try
            {
                Wave1Chat.RegisterChatCommand("dryrun", OnDryRunChat);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave7 SDK chat command registration failed: {e.Message}"); }
        }

        // Reserved lifecycle slot (the glue calls every wave's Tick). Nothing here needs a timer: the
        // dry-run set is bounded by construction and holds no expiring state.
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class RpcRegisterPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try { ZRoutedRpc.instance.Register<bool>("AP_SrvDryRunSet", OnDryRunSet); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvDryRunSet registration failed: {e.Message}"); }
            }
        }

        // ==================== dry-run flag ====================

        private static void OnDryRunSet(long sender, bool on)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvDryRunSet")) return;

            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?") return;

            if (!DryRunAllowed)
            {
                CompanionPlugin.NotifySender(sender, "Dry-run mode is disabled on this server (AllowDryRunMode=false).");
                CompanionPlugin.SrvAudit(sender, "DRYRUN", $"id={id} requested={(on ? "ON" : "OFF")} result=refused-by-config");
                return;
            }

            if (on)
            {
                if (!DryRunOn.Contains(id) && DryRunOn.Count >= MaxDryRunEntries)
                {
                    // Refuse rather than evict: dropping somebody else's flag would put THEM back into
                    // acting-for-real without telling them.
                    CompanionPlugin.FeatureLog($"Dry-run refused for {id}: {MaxDryRunEntries} admins already simulating.");
                    CompanionPlugin.NotifySender(sender, "Dry-run could not be enabled (too many sessions).");
                    return;
                }
                DryRunOn.Add(id);
            }
            else DryRunOn.Remove(id);

            CompanionPlugin.SrvAudit(sender, "DRYRUN", $"id={id} state={(on ? "ON" : "OFF")}");
            CompanionPlugin.FeatureLog($"Dry-run {(on ? "ENABLED" : "DISABLED")} for {CompanionPlugin.SenderDisplayName(sender)} ({id})");
            CompanionPlugin.NotifySender(sender, on
                ? "Dry-run ON: features that support it will simulate instead of acting. Features that do not support it are unaffected."
                : "Dry-run OFF: actions run for real again.");
        }

        // !dryrun [on|off] — admin only, same per-admin flag as the panel toggle.
        private static void OnDryRunChat(long senderUid, string args)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (!CompanionPlugin.SenderCanFeature(senderUid, "AP_SrvDryRunSet")) return;
                var id = CompanionPlugin.SenderPlatformId(senderUid);
                if (string.IsNullOrEmpty(id) || id == "?") return;

                var a = (args ?? "").Trim().ToLowerInvariant();
                bool on;
                if (a == "on" || a == "1" || a == "true") on = true;
                else if (a == "off" || a == "0" || a == "false") on = false;
                else if (a.Length == 0) on = !DryRunOn.Contains(id);
                else
                {
                    Wave1Moderation.SendPlayerText(senderUid, "Usage: !dryrun [on|off]");
                    return;
                }
                OnDryRunSet(senderUid, on);
                Wave1Moderation.SendPlayerText(senderUid, on ? "Dry-run ON (simulate)" : "Dry-run OFF");
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"!dryrun failed: {e.Message}"); }
        }

        /// <summary>
        /// The check other server modules call before doing something destructive on this admin's behalf:
        /// <c>if (Wave7Sdk.IsDryRun(sender)) { NotifySender(sender, "(simulated) ..."); return; }</c>
        /// True only when that admin explicitly switched their own session into simulate mode. Never
        /// throws; false whenever the sender cannot be identified.
        /// </summary>
        internal static bool IsDryRun(long sender)
        {
            try
            {
                if (DryRunOn.Count == 0 || !DryRunAllowed) return false;
                return IsDryRunId(CompanionPlugin.SenderPlatformId(sender));
            }
            catch (Exception) { return false; }
        }

        /// <summary>Same check by platform id, for code paths that already resolved one.</summary>
        internal static bool IsDryRunId(string platformId)
        {
            if (string.IsNullOrEmpty(platformId) || platformId == "?" || DryRunOn.Count == 0) return false;
            if (DryRunOn.Contains(platformId)) return true;
            try { return DryRunOn.Contains(CompanionPlugin.FeatureBareId(platformId)); }
            catch (Exception) { return false; }
        }

        /// <summary>Count of admins currently simulating (diagnostics / status replies).</summary>
        internal static int DryRunCount => DryRunOn.Count;

        // ==================== namespaced store access for third-party plugins ====================

        // Third-party tables always live under "ext_<name>" so a plugin cannot write into "eco",
        // "guard_allow", "roles" or any other table this mod's own features trust.
        internal static string ExtTable(string name)
        {
            var sb = new StringBuilder("ext_");
            if (!string.IsNullOrEmpty(name))
                foreach (var c in name.Trim().ToLowerInvariant())
                {
                    if (sb.Length >= 36) break;
                    if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_') sb.Append(c);
                }
            return sb.Length == 4 ? "ext_misc" : sb.ToString();
        }
    }

    /// <summary>
    /// Public extension API for the Advanced Admin Panel companion (server side).
    ///
    /// <para>Stability contract: members are added, never removed or re-signatured, and the revision is
    /// <see cref="ApiVersion"/>. Every member is a safe no-op returning false/null/"" when the companion
    /// is not ready or the process is not a server, so callers never need their own try/catch.</para>
    ///
    /// <para>None of this bypasses the mod's own gates: <see cref="CanUse"/> is the same adminlist +
    /// tiered-role check every built-in RPC uses, and storage is confined to an "ext_" table namespace.</para>
    /// </summary>
    public static class AdminPanelCompanionApi
    {
        /// <summary>Extension API revision. Bumped only when members are ADDED.</summary>
        public const int ApiVersion = 1;

        /// <summary>True when the companion plugin is loaded and this process is acting as a server.</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return CompanionPlugin.Instance != null && ZNet.instance != null && ZNet.instance.IsServer(); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The companion's plugin version, e.g. "2.4.0".</summary>
        public static string CompanionVersion => CompanionPlugin.PluginVersion;

        /// <summary>Per-world data directory the companion writes to, or null before a world is loaded.</summary>
        public static string DataDir
        {
            get { try { return FeatureStore.DataDir; } catch (Exception) { return null; } }
        }

        /// <summary>Adminlist membership for a routed-RPC sender uid.</summary>
        public static bool IsAdmin(long senderUid)
        {
            try { return Enabled() && CompanionPlugin.FeatureSenderIsAdmin(senderUid); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Adminlist + tiered-role check. Pass the RPC/action name you registered with
        /// <see cref="RegisterAdminRpc"/> so custom roles can allow or deny it by name.
        /// </summary>
        public static bool CanUse(long senderUid, string actionName)
        {
            try { return Enabled() && CompanionPlugin.SenderCanFeature(senderUid, actionName ?? ""); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// True when this admin put their session into dry-run (simulate) mode. Honouring it is opt-in:
        /// check it before your plugin does something destructive and report what you WOULD have done.
        /// </summary>
        public static bool IsDryRun(long senderUid)
        {
            try { return Enabled() && Wave7Sdk.IsDryRun(senderUid); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Add your RPC name to the companion's audit chokepoint (who called it, when, allowed or denied).
        /// </summary>
        /// <param name="actionName">Your routed-RPC name; use an "AP_Srv"-free prefix of your own.</param>
        /// <param name="roleGrant">"moderator", "builder", or null for owner-only when roles are enforced.</param>
        public static bool RegisterAdminRpc(string actionName, string roleGrant = null)
        {
            if (string.IsNullOrEmpty(actionName) || !Enabled()) return false;
            try { CompanionPlugin.RegisterAuditedRpc(actionName, roleGrant); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Register a '!' chat command. The handler receives (senderUid, argumentText) and MUST do its own
        /// permission check - the chat registry does not gate anything. First registration of a name wins.
        /// </summary>
        public static bool RegisterChatCommand(string verb, Action<long, string> handler)
        {
            if (string.IsNullOrEmpty(verb) || handler == null || !Enabled()) return false;
            try { Wave1Chat.RegisterChatCommand(verb, handler); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Write a rich entry into the companion's audit log (no-op when auditing is off).</summary>
        public static void Audit(long senderUid, string action, string detail)
        {
            if (!Enabled()) return;
            try { CompanionPlugin.SrvAudit(senderUid, action ?? "EXT", detail ?? ""); }
            catch (Exception) { }
        }

        /// <summary>Send a line to one panel user (admins run the panel; nothing is shown to others).</summary>
        public static void NotifyPanel(long uid, string text)
        {
            if (!Enabled() || string.IsNullOrEmpty(text)) return;
            try { CompanionPlugin.NotifySender(uid, text); }
            catch (Exception) { }
        }

        /// <summary>Send a line to every online admin's panel.</summary>
        public static void NotifyAdmins(string text)
        {
            if (!Enabled() || string.IsNullOrEmpty(text)) return;
            try { Wave1Moderation.NotifyOnlineAdmins(text); }
            catch (Exception) { }
        }

        /// <summary>
        /// On-screen text for one player that works on UNMODDED clients (vanilla ShowMessage).
        /// </summary>
        public static void SendPlayerText(long uid, string text)
        {
            if (!Enabled() || string.IsNullOrEmpty(text)) return;
            try { Wave1Moderation.SendPlayerText(uid, text); }
            catch (Exception) { }
        }

        /// <summary>
        /// Whether a connected player runs the panel's client mod: true = answered, false = did not
        /// answer, null = not asked yet. Item grants and other modded-client features only work when true.
        /// </summary>
        public static bool? HasPanelClient(long uid)
        {
            try { return Enabled() ? Wave34Core.HasMod(uid) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Plain-English reason a modded-client feature is unavailable for this peer ("" when it is).</summary>
        public static string ClientCapabilityReason(long uid)
        {
            try { return Enabled() ? Wave34Core.CapReason(uid) : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Queue a line for the Discord feed, if the owner configured one. Never blocks.</summary>
        public static void FeedNotice(string title, string description)
        {
            if (!Enabled() || string.IsNullOrEmpty(title)) return;
            try { Wave34Core.Enqueue(title, description ?? "", Wave34Core.ColorInfo); }
            catch (Exception) { }
        }

        // ---- per-world storage, confined to the "ext_" table namespace ----

        /// <summary>Read one value from your plugin's per-world table, or null.</summary>
        public static string GetValue(string table, string key)
        {
            if (!Enabled() || string.IsNullOrEmpty(key)) return null;
            try
            {
                var t = FeatureStore.Table(Wave7Sdk.ExtTable(table));
                string v;
                return t.TryGetValue(key, out v) ? v : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Write one value into your plugin's per-world table (pass null to delete). Saves immediately.</summary>
        public static bool SetValue(string table, string key, string value)
        {
            if (!Enabled() || string.IsNullOrEmpty(key)) return false;
            try
            {
                var name = Wave7Sdk.ExtTable(table);
                var t = FeatureStore.Table(name);
                if (value == null) t.Remove(key);
                else t[key] = value;
                FeatureStore.SaveTable(name);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Append a line to your plugin's per-world log file (ext_&lt;name&gt;.log).</summary>
        public static void AppendLog(string log, string line)
        {
            if (!Enabled() || string.IsNullOrEmpty(line)) return;
            try { FeatureStore.Append(Wave7Sdk.ExtTable(log), line); }
            catch (Exception) { }
        }

        private static bool Enabled()
        {
            try
            {
                return CompanionPlugin.Instance != null && Wave7Sdk.ExtensionApiAllowed
                       && ZNet.instance != null && ZNet.instance.IsServer();
            }
            catch (Exception) { return false; }
        }
    }
}

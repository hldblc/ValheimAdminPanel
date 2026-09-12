using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 1 glue (Accountability suite) ====================
    // Implements the foundation's partial hooks for wave 1 and nothing else. The feature files themselves
    // (Wave1_Moderation/_Audit/_Roles/_RapSheet.cs) expose plain internal methods and never touch the
    // lifecycle, so a wave can be dropped from the build by deleting its files plus this one file.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave1()
        {
            ModInit();
            AudInit();
            RoleInit();
            RapInit();

            // Reply handlers. Registration rides a ZNet.Awake postfix (client RPCs bind on world join, not
            // plugin load); each patch is guarded so a failure costs one feature, not the panel.
            try { Harmony.CreateAndPatchAll(typeof(ModRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Moderation RPC registration failed (moderation state unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(Wave1RpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Wave 1 RPC registration failed (audit/roles/rap sheet unavailable): {e.Message}"); }

            // Chip order = expected frequency of use.
            RegisterFeatureSection(new FeatureSection
            { Id = "Moderation", LocKey = "mod.chip", Draw = DrawModerationSection, Enabled = ModSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Audit", LocKey = "audit.chip", Draw = DrawAuditSection, Enabled = AudSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Roles", LocKey = "role.chip", Draw = DrawRolesSection, Enabled = RoleSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "RapSheet", LocKey = "rap.chip", Draw = DrawRapSheetSection, Enabled = RapSectionEnabled });
        }

        // Per-world state: server-truth payloads, their Layout snapshots, throttles and typed targets. All
        // of it belongs to the server just left — see the stale-ban-list hazard in ResetSessionState.
        partial void FeaturesResetWave1()
        {
            ModReset();
            AudReset();
            RoleReset();
            RapReset();
        }
    }
}

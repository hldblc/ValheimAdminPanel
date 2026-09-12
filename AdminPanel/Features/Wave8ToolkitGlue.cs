using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 8 glue (toolkit group: staff chat, location finder, item forge, raid composer) ====================
    // Implements the foundation's partial hooks for the toolkit group and nothing else. The feature files
    // (Wave8_Toolkit*.cs) expose plain internal methods and never touch the lifecycle, so the group can be
    // dropped from the build by deleting its files plus this one.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave8Toolkit()
        {
            AchatInit();
            LocfInit();
            IattrInit();
            RaidcInit();

            // Reply handlers. Registration rides a ZNet.Awake postfix (client RPCs bind on world join, not
            // plugin load); guarded so a failure costs this group's server-truth views, not the panel.
            try { Harmony.CreateAndPatchAll(typeof(Wave8ToolkitRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Wave 8 toolkit RPC registration failed (staff chat / location finder / raid state unavailable): {e.Message}"); }

            // All four sit on the Tools tab; chip order = expected frequency of use.
            RegisterFeatureSection(new FeatureSection
            { Id = "StaffChat", LocKey = "achat.chip", Draw = DrawStaffChatSection, Enabled = AchatSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "LocationFinder", LocKey = "locf.chip", Draw = DrawLocationFinderSection, Enabled = LocfSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "ItemForge", LocKey = "iattr.chip", Draw = DrawItemForgeSection, Enabled = IattrSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "RaidComposer", LocKey = "raidc.chip", Draw = DrawRaidComposerSection, Enabled = RaidcSectionEnabled, Tab = ToolsTab });
        }

        // Per-world state: server-truth payloads, their Layout snapshots, throttles, typed targets and the
        // aimed item drop. All of it belongs to the server just left.
        partial void FeaturesResetWave8Toolkit()
        {
            AchatReset();
            LocfReset();
            IattrReset();
            RaidcReset();
        }

        // No overlays: every toolkit section lives inside the panel window.
        partial void FeaturesGuiWave8Toolkit() { }

        // LateUpdate slot: drops a destroyed aimed item drop before the next draw.
        partial void FeaturesTickWave8Toolkit() => IattrTick();
    }
}

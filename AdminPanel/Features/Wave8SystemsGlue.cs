using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 8 glue (SYSTEMS group: death rules, bounties, self-update, client perf) ====================
    // Implements the foundation's partial hooks for the systems group and nothing else. The feature files
    // (Wave8_SystemsDeath/_SystemsBounty/_SystemsUpdate/_SystemsPerf.cs) expose plain internal methods and
    // never touch the lifecycle, so the group can be dropped from the build by deleting its files plus this one.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave8Systems()
        {
            DeathInit();
            BountyInit();
            SelfupInit();
            PcensusInit();

            // Reply handlers. Registration rides a ZNet.Awake postfix (client RPCs bind on world join, not
            // plugin load); guarded so a failure costs this group's cards, not the panel.
            try { Harmony.CreateAndPatchAll(typeof(Wave8SystemsRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Wave 8 systems RPC registration failed (death rules / bounties / self-update / client perf unavailable): {e.Message}"); }

            // Every round-2 section lives on the Tools tab. Chip order = expected frequency of use.
            RegisterFeatureSection(new FeatureSection
            { Id = "DeathRules", LocKey = "death.chip", Draw = DrawDeathRulesSection, Enabled = DeathSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "Bounties", LocKey = "bounty.chip", Draw = DrawBountiesSection, Enabled = BountySectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "ClientPerf", LocKey = "pcensus.chip", Draw = DrawClientPerfSection, Enabled = PcensusSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "SelfUpdate", LocKey = "selfup.chip", Draw = DrawSelfUpdateSection, Enabled = SelfupSectionEnabled, Tab = ToolsTab });
        }

        // Per-world state: server-truth payloads, their Layout snapshots, throttles and typed targets. All of
        // it belongs to the server just left.
        partial void FeaturesResetWave8Systems()
        {
            DeathReset();
            BountyReset();
            SelfupReset();
            PcensusReset();
        }

        // No overlays: every card of this group lives inside the panel window.
        partial void FeaturesGuiWave8Systems() { }

        // No client-side timers: every request is Layout-gated inside its card (throttle-first polls), and
        // the client-side executors of this group run in the companion, not in the panel.
        partial void FeaturesTickWave8Systems() { }
    }
}

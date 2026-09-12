using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 8 glue (server RULES group) ====================
    // Implements the foundation's partial hooks for the rules group and nothing else. The feature files
    // (Wave8_RulesMapPins / _RulesMapReveal / _RulesTrader / _RulesBlacklist / _RulesSkills, plus the shared
    // Wave8_RulesCore) expose plain internal methods and never touch the lifecycle, so the whole group can be
    // dropped from the build by deleting its files plus this one.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave8Rules()
        {
            MpinInit();
            MaprInit();
            TrInit();
            BlkInit();
            SkrInit();

            // Reply handlers ride a ZNet.Awake postfix (client RPCs bind on world join, not plugin load);
            // guarded so a failure costs this group's server views, not the panel.
            try { Harmony.CreateAndPatchAll(typeof(Wave8RulesRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Wave 8 rules RPC registration failed (map pins / trader / blacklist / skill rules views unavailable): {e.Message}"); }

            // All five live on the Tools tab; chip order = expected frequency of use.
            RegisterFeatureSection(new FeatureSection
            { Id = "MapPins", LocKey = "mpin.chip", Draw = DrawMapPinsSection, Enabled = MpinSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "MapReveal", LocKey = "mapr.chip", Draw = DrawMapRevealSection, Enabled = MaprSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "TraderStock", LocKey = "trader.chip", Draw = DrawTraderStockSection, Enabled = TrSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "Blacklist", LocKey = "blk.chip", Draw = DrawBlacklistSection, Enabled = BlkSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "SkillRules", LocKey = "skillr.chip", Draw = DrawSkillRulesSection, Enabled = SkrSectionEnabled, Tab = ToolsTab });
        }

        // Per-world state: server-truth tables, their Layout snapshots, throttles and typed targets. All of
        // it belongs to the server just left (a stale pin list or trader table drawn on the next server would
        // be the cross-world leak ResetSessionState exists to prevent).
        partial void FeaturesResetWave8Rules()
        {
            MpinReset();
            MaprReset();
            TrReset();
            BlkReset();
            SkrReset();
        }

        // No overlays: every card lives inside the panel window.
        partial void FeaturesGuiWave8Rules()
        {
        }

        // No timers: the rules are applied by the companion, and the cards request on open / on click only.
        partial void FeaturesTickWave8Rules()
        {
        }
    }
}

namespace AdminPanelCompanion
{
    // ==================== Wave 8 glue (server rules, companion side) ====================
    // Implements the foundation's partial hooks for the RULES group (map pins, map reveal, trader stock,
    // recipe/piece blacklist, skill rules). Wave8Rules.Init applies its own Harmony patches and registers its
    // own RPCs (each guarded internally); Tick self-throttles (2 s signature check, 0.5 s blacklist pass).
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave8Rules()
        {
            Wave8Rules.Init();
        }

        partial void FeaturesTickSrvWave8Rules()
        {
            Wave8Rules.Tick();
        }
    }
}

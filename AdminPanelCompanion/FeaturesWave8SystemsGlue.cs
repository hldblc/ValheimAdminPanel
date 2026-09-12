namespace AdminPanelCompanion
{
    // ==================== Wave 8 glue (SYSTEMS group, server side) ====================
    // Implements the foundation's partial hooks for the systems group: death rules, bounty board, companion
    // self-update and the client performance census. Wave8Systems.Init applies the group's patches and
    // registers its RPCs (each guarded internally); the four feature classes never touch the lifecycle, so
    // the whole group can be dropped from the build by deleting its files plus this one.
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave8Systems()
        {
            Wave8Systems.Init();
        }

        // Called every frame; every sub-module self-throttles and guards on server/client state itself.
        partial void FeaturesTickSrvWave8Systems()
        {
            Wave8Systems.Tick();
        }
    }
}

namespace AdminPanelCompanion
{
    // ==================== Round 3 (wave 9) glue, server side ====================
    // Implements the foundation's two partial hooks for the round-3 group. Everything else lives in the
    // Wave9Srv*.cs static classes, so the group can be dropped from the build by deleting its files plus this one.
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave9() => Wave9.Init();

        // Called every frame; the scanner and each feature self-throttle.
        partial void FeaturesTickSrvWave9() => Wave9.Tick();
    }
}

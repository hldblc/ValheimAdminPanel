namespace AdminPanelCompanion
{
    // ==================== Wave 8 glue (toolkit group, server side) ====================
    // Implements the foundation's two partial hooks for the toolkit group. Everything else lives in the
    // Wave8SrvToolkit*.cs static classes, so the group can be dropped from the build by deleting its
    // files plus this one.
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave8Toolkit() => Wave8Toolkit.Init();

        // Called every frame; the raid composer is the only timer and self-throttles to 2 Hz.
        partial void FeaturesTickSrvWave8Toolkit() => Wave8Toolkit.Tick();
    }
}

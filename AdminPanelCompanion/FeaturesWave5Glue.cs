namespace AdminPanelCompanion
{
    // ==================== Wave 5 glue (area tools + portals, server side) ====================
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave5()
        {
            Wave5Area.Init();
            Wave5Portals.Init();
        }

        partial void FeaturesTickSrvWave5()
        {
            Wave5Area.Tick();     // protection-zone sweep (2s)
            Wave5Portals.Tick();
        }
    }
}

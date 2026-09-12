namespace AdminPanelCompanion
{
    // ==================== Wave 8 glue — group OBJECTS (tames / creature edits / spawners / containers), server side ====================
    // Implements the foundation's partial hooks for this group and nothing else. Wave8Objects applies its
    // own Harmony registration and reads its own config inside Init(), so dropping the group from the
    // build means deleting the Wave8SrvObjects*.cs files plus this one file.
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave8Objects()
        {
            Wave8Objects.Init();
        }

        // Every frame; the module self-throttles (it only does work while a scan job or a deletion queue
        // is live, and both are time-boxed per frame).
        partial void FeaturesTickSrvWave8Objects()
        {
            Wave8Objects.Tick();
        }
    }
}

namespace AdminPanelCompanion
{
    // ==================== Wave 2 glue (Server owner's toolkit, server side) ====================
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave2()
        {
            // Backup first: it owns the staged-restore check that must run before anything else touches
            // the world files, and Ops' self-test reads its health accessors.
            Wave2Backup.Init();
            Wave2World.Init();
            Wave2Ops.Init();
        }

        // World runs the frame-spread scanner (time-boxed, returns immediately when idle); Backup drives
        // auto-backup + restart countdowns; Ops samples frame time every frame and fires the schedulers.
        partial void FeaturesTickSrvWave2()
        {
            Wave2World.Tick();
            Wave2Backup.Tick();
            Wave2Ops.Tick();
        }
    }
}

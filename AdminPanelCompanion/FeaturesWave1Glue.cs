namespace AdminPanelCompanion
{
    // ==================== Wave 1 glue (Accountability suite, server side) ====================
    // Implements the foundation's partial hooks for wave 1. Each module's Init applies its own Harmony
    // patches and registers its own RPCs (guarded internally), so an ordering change here is harmless.
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave1()
        {
            // Chat first: it owns the '!' command registry that the rules gate (and later waves) register into.
            Wave1Chat.Init();
            Wave1Moderation.Init();
            Wave1AuditRpc.Init();   // also covers Wave1Roles (its config + RPCs live here)
        }

        // Called every frame; both modules guard on server state and self-throttle (freeze pin 2.5s,
        // temp-ban prune 60s, chat/rules sweep 2s).
        partial void FeaturesTickSrvWave1()
        {
            Wave1Moderation.Tick();
            Wave1Chat.Tick();
        }
    }
}

namespace AdminPanelCompanion
{
    // ==================== Wave 6 + 7 glue (economy/events/slots, guard, SDK — server side) ====================
    public partial class CompanionPlugin
    {
        partial void FeaturesInitSrvWave6()
        {
            Wave6Economy.Init();
            Wave6Events.Init();
            Wave6Slots.Init();
        }

        partial void FeaturesTickSrvWave6()
        {
            Wave6Economy.Tick();
            Wave6Events.Tick();
            Wave6Slots.Tick();
        }

        // The SDK inits before the guard so a third-party plugin's RegisterAdminRpc lands before the
        // guard's own registrations, and so IsDryRun is answerable by anything that consults it.
        partial void FeaturesInitSrvWave7()
        {
            Wave7Sdk.Init();
            Wave7Guard.Init();
        }

        partial void FeaturesTickSrvWave7()
        {
            Wave7Sdk.Tick();
            Wave7Guard.Tick();
        }
    }
}

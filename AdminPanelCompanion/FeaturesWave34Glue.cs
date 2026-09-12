namespace AdminPanelCompanion
{
    // ==================== Wave 3 + 4 glue (server side) ====================
    public partial class CompanionPlugin
    {
        // Wave34Core owns the capability probe, death detection and the Discord queue. It MUST init before
        // the modules that subscribe to its OnDeath / OnGlobalKeyAdded / OnRaidStarted events.
        partial void FeaturesInitSrvWave3()
        {
            Wave34Core.Init();
            Wave3Discord.Init();
        }

        partial void FeaturesTickSrvWave3()
        {
            Wave34Core.Tick();
            Wave3Discord.Tick();
        }

        partial void FeaturesInitSrvWave4()
        {
            Wave4Vault.Init();
            Wave4PlayerData.Init();
        }

        partial void FeaturesTickSrvWave4()
        {
            Wave4Vault.Tick();
            Wave4PlayerData.Tick();
        }
    }
}

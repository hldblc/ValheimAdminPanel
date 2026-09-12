using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 3 + 4 glue (Discord bridge, player data) ====================
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave3()
        {
            DiscInit();
            try { Harmony.CreateAndPatchAll(typeof(DiscRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Discord RPC registration failed (bridge status unavailable): {e.Message}"); }
            RegisterFeatureSection(new FeatureSection
            { Id = "Discord", LocKey = "disc.chip", Draw = DrawDiscordSection, Enabled = DiscSectionEnabled });
        }

        partial void FeaturesResetWave3() => DiscReset();

        partial void FeaturesInitWave4()
        {
            PdatInit();
            try { Harmony.CreateAndPatchAll(typeof(PdatRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Player-data RPC registration failed (vault/deaths/ledger unavailable): {e.Message}"); }
            RegisterFeatureSection(new FeatureSection
            { Id = "PlayerData", LocKey = "pdat.chip", Draw = DrawPlayerDataSection, Enabled = PdatSectionEnabled });
        }

        partial void FeaturesResetWave4() => PdatReset();
    }
}

using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 6 + 7 glue (economy/events, guard, extension SDK) ====================
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave6()
        {
            EcoInit();
            try { Harmony.CreateAndPatchAll(typeof(EcoRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Economy RPC registration failed (economy/events views unavailable): {e.Message}"); }
            RegisterFeatureSection(new FeatureSection
            { Id = "Economy", LocKey = "eco.chip", Draw = DrawEconomySection, Enabled = EcoSectionEnabled });
        }

        partial void FeaturesResetWave6() => EcoReset();

        partial void FeaturesInitWave7()
        {
            // SDK first: third-party plugins may have called AdminPanelApi.RegisterSection before this
            // panel finished Awake, and SdkInit flushes those queued registrations.
            SdkInit();
            GrdInit();
            try { Harmony.CreateAndPatchAll(typeof(GrdRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Guard RPC registration failed (anti-cheat view unavailable): {e.Message}"); }

            RegisterFeatureSection(new FeatureSection
            { Id = "Guard", LocKey = "grd.chip", Draw = DrawGuardSection, Enabled = GrdSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Extensions", LocKey = "sdk.chip", Draw = DrawSdkSection, Enabled = SdkSectionEnabled });
        }

        partial void FeaturesResetWave7()
        {
            EcoReset();   // harmless if already cleared; keeps wave 6 state tied to the session too
            GrdReset();
            SdkReset();
        }

        // Flushes late third-party registrations and pushes the dry-run flag to the server.
        partial void FeaturesTickWave7() => SdkTick();
    }
}

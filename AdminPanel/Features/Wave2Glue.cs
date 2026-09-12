using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 2 glue (Server owner's toolkit) ====================
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave2()
        {
            ToolInit();
            DiagInit();

            try { Harmony.CreateAndPatchAll(typeof(ToolRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Server-tools RPC registration failed (census/cleanup/backups/schedule unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(DiagRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Diagnostics RPC registration failed (perf/log/self-test unavailable): {e.Message}"); }

            RegisterFeatureSection(new FeatureSection
            { Id = "ServerTools", LocKey = "tool.chip", Draw = DrawServerToolsSection, Enabled = ToolSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Diagnostics", LocKey = "diag.chip", Draw = DrawDiagnosticsSection, Enabled = DiagSectionEnabled });
        }

        partial void FeaturesResetWave2()
        {
            ToolReset();
            DiagReset();
        }
    }
}

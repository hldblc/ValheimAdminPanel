using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 5 glue (Admin UX + build tools) ====================
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave5()
        {
            PalInit();
            WfInit();
            BldInit();
            AreaInit();

            // Area tools are the client half of Wave5SrvArea/Wave5SrvPortals — without these reply
            // handlers the zone/ward/portal tables never populate.
            try { Harmony.CreateAndPatchAll(typeof(AreaRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Area-tools RPC registration failed (zones/wards/portals unavailable): {e.Message}"); }

            // Bridge the quick-action bar to the palette's command runner. The two files were written
            // independently (neither may reference the other), so the wiring lives here; PalRunLine is
            // private, which is fine — every glue file is another partial of this same class.
            WfCommandRunner = PalRunLine;

            RegisterFeatureSection(new FeatureSection
            { Id = "Macros", LocKey = "ux.chip", Draw = DrawMacroSection, Enabled = PalSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Workflow", LocKey = "ux2.chip", Draw = DrawWorkflowSection, Enabled = WfSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "BuildTools", LocKey = "bld.chip", Draw = DrawBuildToolsSection, Enabled = BldSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "AreaTools", LocKey = "area.chip", Draw = DrawAreaToolsSection, Enabled = AreaSectionEnabled });
        }

        partial void FeaturesResetWave5()
        {
            PalReset();
            WfReset();
            BldReset();
            AreaReset();
        }

        // The palette is an overlay: it must draw even while the panel itself is closed.
        partial void FeaturesGuiWave5() => PalOnGUI();

        partial void FeaturesTickWave5()
        {
            PalTick();   // macro step pacing (250ms/step) + palette hotkey polling
            BldTick();   // crosshair targeting for the piece editor, free-cam movement
        }
    }
}

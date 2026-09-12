using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Wave 8 glue — group OBJECTS (tames / creature editor / spawners / containers) ====================
    // Implements the foundation's partial hooks for this group and nothing else. The feature files
    // (Wave8_Objects*.cs) expose plain internal methods and never touch the lifecycle, so the group can be
    // dropped from the build by deleting its files plus this one file.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave8Objects()
        {
            ObjInit();
            TameInit();
            CeditInit();
            NestInit();
            ChestInit();

            // Reply handlers. Registration rides a ZNet.Awake postfix (client RPCs bind on world join, not
            // plugin load); the patch is guarded so a failure costs this group, not the panel.
            try { Harmony.CreateAndPatchAll(typeof(Wave8ObjectsRpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"World-object RPC registration failed (tame roster / spawners / containers unavailable): {e.Message}"); }
            // The creature editor's invulnerable flag: two client-side damage prefixes, each guarded on its own.
            CeditApplyPatches();

            // Chip order = expected frequency of use. All four live on the Tools tab.
            RegisterFeatureSection(new FeatureSection
            { Id = "TameRoster", LocKey = "tame.chip", Draw = DrawTameRosterSection, Enabled = TameSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "CreatureEditor", LocKey = "cedit.chip", Draw = DrawCreatureEditorSection, Enabled = CeditSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "Spawners", LocKey = "nest.chip", Draw = DrawSpawnersSection, Enabled = NestSectionEnabled, Tab = ToolsTab });
            RegisterFeatureSection(new FeatureSection
            { Id = "Containers", LocKey = "chest.chip", Draw = DrawContainersSection, Enabled = ChestSectionEnabled, Tab = ToolsTab });
        }

        // Per-world state: server-truth payloads, their Layout snapshots, aimed targets, throttles and the
        // invulnerable set. All of it belongs to the world just left.
        partial void FeaturesResetWave8Objects()
        {
            TameReset();
            CeditReset();
            NestReset();
            ChestReset();
        }

        // No overlay is drawn with the panel closed by this group; the hook exists so the contract's four
        // lifecycle points are visibly accounted for.
        partial void FeaturesGuiWave8Objects()
        {
        }

        // LateUpdate slot: the two aiming sections refresh their crosshair target here; both guard on
        // world state and on their own section switch.
        partial void FeaturesTickWave8Objects()
        {
            CeditTick();
            ChestTick();
        }
    }
}

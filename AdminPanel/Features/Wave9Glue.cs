using System;
using HarmonyLib;

namespace AdminPanel
{
    // ==================== Round 3 (wave 9) glue ====================
    // Implements the foundation's partial hooks for the round-3 group: the Tools-tab sections it registers and the
    // fixed-tab cards the main file calls in place (Wave9* partials declared in FeaturesCore.cs). Feature files
    // (Wave9_*.cs) expose plain methods and never touch the lifecycle, so the group can be dropped from the build by
    // deleting its files plus this one — every main-file call site is a partial method and vanishes with it.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitWave9()
        {
            // Each feature binds its own config; one failing must not cost the others.
            W9Safe("input detector", InpInit);
            W9Safe("ownership", ZownInit);
            W9Safe("map overlays", OvlInit);
            W9Safe("provenance", ProvInit);
            W9Safe("progression", ProgInit);
            W9Safe("world diff", WdiffInit);
            W9Safe("bases", BasesInit);
            W9Safe("locations", LocrInit);
            W9Safe("rule zones", RzoneInit);
            W9Safe("triggers", TrigInit);
            W9Safe("undo timeline", UndoInit);
            W9Safe("status-effect forge", SefInit);
            W9Safe("encounters", EncInit);
            W9Safe("creature command", CcmdInit);
            W9Safe("controller mode", PadInit);

            try { Harmony.CreateAndPatchAll(typeof(Wave9RpcRegistration)); }
            catch (Exception e) { Logger.LogWarning($"Round 3 RPC registration failed (round-3 server views unavailable): {e.Message}"); }

            RegisterFeatureSection(new FeatureSection
            { Id = "Provenance", LocKey = "prov.chip", Draw = DrawProvSection, Enabled = ProvSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Automation", LocKey = "trig.chip", Draw = DrawTrigSection, Enabled = TrigSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "RuleZones", LocKey = "rzone.chip", Draw = DrawRzoneSection, Enabled = RzoneSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "Bases", LocKey = "bases.chip", Draw = DrawBasesSection, Enabled = BasesSectionEnabled });
            RegisterFeatureSection(new FeatureSection
            { Id = "UndoTimeline", LocKey = "undo.chip", Draw = DrawUndoSection, Enabled = UndoSectionEnabled });
        }

        private void W9Safe(string what, Action init)
        {
            try { init(); }
            catch (Exception e) { Logger.LogWarning($"Round 3 {what} failed to initialise (that feature is unavailable): {e.Message}"); }
        }

        // Per-world state: every server-truth payload belongs to the server just left.
        partial void FeaturesResetWave9()
        {
            InpReset();
            ZownReset();
            OvlReset();
            ProvReset();
            ProgReset();
            WdiffReset();
            BasesReset();
            LocrReset();
            RzoneReset();
            TrigReset();
            UndoReset();
            SefReset();
            EncReset();
            CcmdReset();
            PadReset();
        }

        // Overlays outside the panel window: the map-layer chips over the open map, the controller focus ring.
        partial void FeaturesGuiWave9()
        {
            OvlGui();
            PadGui();
        }

        partial void FeaturesTickWave9()
        {
            InpTick();
            OvlTick();
            RzoneTick();
            PadTick();
        }

        // Gate closed: nothing a round-3 card keeps outside the panel body may stay live.
        partial void Wave9OnGateClosed()
        {
            OvlGateClosed();
            RzoneGateClosed();
        }

        partial void Wave9OnGuiFirst()
        {
            InpOnGuiFirst();
            PadOnGuiFirst();
        }

        partial void Wave9WindowBegin() => PadWindowBegin();
        partial void Wave9WindowEnd() => PadWindowEnd();
        partial void Wave9TitleBar() => UndoTitleBar();
        partial void Wave9TopNotice() => InpTopNotice();
        partial void Wave9ItemTag(string prefab, ref string label) => ProvItemTag(prefab, ref label);
        partial void Wave9CreaturesCard(ref float reserve) => reserve = DrawCcmdCard();
        partial void Wave9BossesTop() => DrawEncCard();
        partial void Wave9PlayerSeCards() => DrawSefCard();
        partial void Wave9WorldTeleportRow() => DrawOvlChipRow();
        partial void Wave9WorldCards() => DrawLocrCard();
        partial void Wave9PlayersCards() => DrawProgCard();

        partial void Wave9ServerCards()
        {
            DrawZownCard();
            DrawWdiffCard();
        }

        partial void Wave9SettingsPanelRow() => DrawPadToggle();
        partial void Wave9SettingsCards() => DrawInpCard();
    }
}

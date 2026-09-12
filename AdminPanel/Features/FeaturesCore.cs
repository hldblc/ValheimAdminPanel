using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Feature-module core (additive; owns the Extras tab) ====================
    // Every backlog feature lives in Features\*.cs as another partial of AdminPanelPlugin and registers a
    // section here. The main file only carries eight one-line hooks; with no wave files compiled in, all
    // partial-method calls below vanish at compile time and the panel is byte-for-byte today's behavior.
    public partial class AdminPanelPlugin
    {
        // One section = one chip inside the Extras tab. Sections draw with the shared styles/card API, so
        // they inherit the runtime skin with no extra work. Draw runs inside DrawWindow's try/catch.
        internal sealed class FeatureSection
        {
            public string Id;             // stable English id — never localized (state key, config key)
            public string LocKey;         // chip label locale key
            public Action Draw;
            public Func<bool> Enabled;    // section hidden when false (re-check each Layout pass)
            // Which feature tab hosts the chip: 0 = Extras (waves 1-7, the default), ToolsTab = the Tools
            // tab added for the round-2 features. Two tabs because 30+ chips in one row wrap into six lines.
            public int Tab;
        }

        internal const int ToolsTab = 1;

        private ConfigEntry<bool> _extrasTabCfg;
        private readonly List<FeatureSection> _featureSections = new List<FeatureSection>();
        private bool _featuresInited;

        // Extras tab index sits after the fixed tabs; the switch in DrawWindow routes it via default:.
        private static int FeatureTabIndex => TabKeys.Length;

        // Chip selection follows the Items/Player-tab snapshot discipline: the live field flips on the
        // event pass, the Layout snapshot is what every pass of the same frame draws from.
        private string _extrasChip;
        private string _extrasChipLayout;
        private bool _featureTabVisibleLayout;
        private List<FeatureSection> _featureSectionsLayout;   // enabled subset, pinned per frame

        // Same trio for the Tools tab (round-2 sections). Kept as parallel fields rather than arrays so the
        // Extras tab's state and every existing reference to it stay byte-for-byte what waves 1-7 shipped.
        private static int ToolsTabIndex => TabKeys.Length + 1;
        private string _toolsChip;
        private string _toolsChipLayout;
        private bool _toolsTabVisibleLayout;
        private List<FeatureSection> _toolsSectionsLayout;

        // ---- lifecycle hooks (called from the main file) ----

        private void FeaturesInit()
        {
            if (_featuresInited) return;
            _featuresInited = true;
            _extrasTabCfg = Config.Bind("Features", "EnableExtrasTab", true,
                "Master switch for the Extras tab that hosts all feature modules. Off = the panel looks and behaves exactly as before.");

            // Waves register their sections + init their own config/patches. Unimplemented waves no-op.
            //
            // Each call is isolated: FeaturesInit() is the LAST thing Awake does, so an exception escaping
            // any one wave would propagate out of Awake, BepInEx would mark the whole plugin failed, and the
            // user would lose the eight original tabs along with the feature that broke. One bad wave should
            // cost that wave and nothing else. (Partial methods cannot be taken as delegates, hence the
            // repetition rather than a loop.)
            try { FeaturesInitWave1(); } catch (Exception e) { WaveInitFailed("1 (accountability)", e); }
            try { FeaturesInitWave2(); } catch (Exception e) { WaveInitFailed("2 (server tools)", e); }
            try { FeaturesInitWave3(); } catch (Exception e) { WaveInitFailed("3 (discord)", e); }
            try { FeaturesInitWave4(); } catch (Exception e) { WaveInitFailed("4 (player data)", e); }
            try { FeaturesInitWave5(); } catch (Exception e) { WaveInitFailed("5 (admin UX)", e); }
            try { FeaturesInitWave6(); } catch (Exception e) { WaveInitFailed("6 (economy)", e); }
            try { FeaturesInitWave7(); } catch (Exception e) { WaveInitFailed("7 (guard/SDK)", e); }
            try { FeaturesInitExtra(); } catch (Exception e) { WaveInitFailed("extras", e); }
            // Round 2 (2026-09): four groups, each its own partial so one can be dropped by deleting its files.
            try { FeaturesInitWave8Objects(); } catch (Exception e) { WaveInitFailed("8 (world objects)", e); }
            try { FeaturesInitWave8Toolkit(); } catch (Exception e) { WaveInitFailed("8 (toolkit)", e); }
            try { FeaturesInitWave8Rules(); } catch (Exception e) { WaveInitFailed("8 (server rules)", e); }
            try { FeaturesInitWave8Systems(); } catch (Exception e) { WaveInitFailed("8 (systems)", e); }
        }

        private void WaveInitFailed(string wave, Exception e) =>
            Logger.LogWarning($"Feature wave {wave} failed to initialise (its sections are unavailable; the rest of the panel is unaffected): {e}");

        private void FeaturesResetSession()
        {
            _featureSectionsLayout = null;
            _toolsSectionsLayout = null;
            FeaturesResetWave1();
            FeaturesResetWave2();
            FeaturesResetWave3();
            FeaturesResetWave4();
            FeaturesResetWave5();
            FeaturesResetWave6();
            FeaturesResetWave7();
            FeaturesResetExtra();
            FeaturesResetWave8Objects();
            FeaturesResetWave8Toolkit();
            FeaturesResetWave8Rules();
            FeaturesResetWave8Systems();
        }

        // Feature overlays (command palette, quick bar) draw before the panel-visibility gate so they can
        // exist with the panel closed. Each implementation must call EnsureSkin()/ApplyFont() itself and
        // keep its own control counts Layout-stable.
        private void FeaturesOnGUI()
        {
            if (!_featuresInited) return;
            FeaturesGuiWave1();
            FeaturesGuiWave3();
            FeaturesGuiWave5();
            FeaturesGuiWave6();
            FeaturesGuiWave8Objects();
            FeaturesGuiWave8Toolkit();
            FeaturesGuiWave8Rules();
            FeaturesGuiWave8Systems();
        }

        // Client-side timers. LateUpdate is unclaimed by the main file, so features get a lifecycle slot
        // without touching it. Runs from the main menu on; wave ticks must guard on world state themselves.
        private void LateUpdate()
        {
            if (!_featuresInited) return;
            FeatureClampTab();
            FeaturesTickWave1();
            FeaturesTickWave2();
            FeaturesTickWave3();
            FeaturesTickWave4();
            FeaturesTickWave5();
            FeaturesTickWave6();
            FeaturesTickWave7();
            FeaturesTickWave8Objects();
            FeaturesTickWave8Toolkit();
            FeaturesTickWave8Rules();
            FeaturesTickWave8Systems();
        }

        // ---- Extras tab plumbing ----

        internal void RegisterFeatureSection(FeatureSection s)
        {
            if (s == null || string.IsNullOrEmpty(s.Id) || s.Draw == null) return;
            if (_featureSections.Exists(x => x.Id == s.Id)) return;   // waves must not collide on ids
            _featureSections.Add(s);
        }

        // 660 keeps the 8 fixed tabs clickable; each feature tab (Extras, Tools) needs one more 90 px slot.
        // Not layout-affecting per-pass (it clamps the window rect, not a control), so live computation is
        // safe here.
        private float MinPanelWidth() =>
            660f + (FeatureTabVisible() ? 90f : 0f) + (ToolsTabVisible() ? 90f : 0f);

        private bool HasSectionsOnTab(int tab)
        {
            foreach (var s in _featureSections) if (s.Tab == tab) return true;
            return false;
        }

        private bool FeatureTabVisible() =>
            _featuresInited && _extrasTabCfg.Value && HasSectionsOnTab(0);

        private bool ToolsTabVisible() =>
            _featuresInited && _extrasTabCfg.Value && HasSectionsOnTab(ToolsTab);

        // The master switch alone, for wave ticks that must fall silent when the operator turned the Extras
        // tab off — a tick has no chip and does not care about the registered-section count FeatureTabVisible
        // folds in. Only ever called from LateUpdate, so reading the config live is safe here.
        internal bool FeaturesEnabled() => _featuresInited && _extrasTabCfg != null && _extrasTabCfg.Value;

        // Called inside the tab row. Pins its own visibility on Layout so the row's control count can
        // never change between the Layout and Repaint passes of one frame.
        private void DrawFeatureTabButton()
        {
            if (Event.current.type == EventType.Layout)
            {
                _featureTabVisibleLayout = FeatureTabVisible();
                _toolsTabVisibleLayout = ToolsTabVisible();
            }
            if (_featureTabVisibleLayout)
            {
                var pressed = GUILayout.Toggle(_tab == FeatureTabIndex, Loc.T("tab.extras"), _tabStyle);
                if (pressed && _tab != FeatureTabIndex) { _tab = FeatureTabIndex; _openDropdown = null; _rebindTarget = 0; }
            }
            if (_toolsTabVisibleLayout)
            {
                var pressed = GUILayout.Toggle(_tab == ToolsTabIndex, Loc.T("tab.tools"), _tabStyle);
                if (pressed && _tab != ToolsTabIndex) { _tab = ToolsTabIndex; _openDropdown = null; _rebindTarget = 0; }
            }
        }

        // The Extras button vanishes the moment the master switch goes off, but _tab is what DrawWindow's
        // switch routes on: left at the Extras index, the panel would keep showing feature UI with no tab
        // drawn as selected. The fallback lives here and not in OnGUI because _tab decides WHICH tab body
        // is emitted — changing it between the Layout and Repaint passes of one frame would hand IMGUI two
        // different control counts. It reads _featureTabVisibleLayout, the same Layout snapshot the tab row
        // gates the button on, so the button and the body can never disagree about whether the tab exists.
        private void FeatureClampTab()
        {
            if (_tab < FeatureTabIndex) return;
            var visible = _tab == FeatureTabIndex ? _featureTabVisibleLayout
                        : _tab == ToolsTabIndex && _toolsTabVisibleLayout;
            if (visible) return;
            _tab = 0;
            _openDropdown = null;
            _rebindTarget = 0;
        }

        // DrawWindow's default: case lands here for every index past the fixed tabs; the two feature tabs
        // share one chip-row implementation and differ only in which state trio they draw from.
        private void DrawFeaturesTab()
        {
            if (_tab == ToolsTabIndex)
                DrawFeatureChipTab(ToolsTab, _toolsTabVisibleLayout, ref _toolsChip, ref _toolsChipLayout, ref _toolsSectionsLayout);
            else
                DrawFeatureChipTab(0, _featureTabVisibleLayout, ref _extrasChip, ref _extrasChipLayout, ref _featureSectionsLayout);
        }

        private void DrawFeatureChipTab(int tab, bool visibleLayout, ref string chip, ref string chipLayout,
            ref List<FeatureSection> sectionsLayout)
        {
            // Pin the enabled-section list and chip selection per frame (sections toggle from config UI on
            // the event pass; an unpinned list would change the chip row's control count mid-frame). The
            // master switch is pinned through the tab row's own snapshot: with the Extras tab hidden this
            // body must emit nothing at all, or the whole feature UI stays drawn and clickable under a tab
            // row where no tab is selected. Reading _extrasTabCfg live instead would flip the count between
            // the two passes of one frame.
            if (Event.current.type == EventType.Layout)
            {
                List<FeatureSection> enabled = null;
                if (visibleLayout)
                {
                    enabled = new List<FeatureSection>();
                    foreach (var s in _featureSections)
                        if (s.Tab == tab && (s.Enabled == null || s.Enabled())) enabled.Add(s);
                    if (chip == null && enabled.Count > 0) chip = enabled[0].Id;
                }
                sectionsLayout = enabled;
                chipLayout = chip;
            }

            var list = sectionsLayout;
            if (list == null) return;   // tab not visible this frame — no controls, on every pass
            if (list.Count == 0)
            {
                GUILayout.Label(Loc.T("feat.none"), _hintStyle);
                return;
            }

            // Chip row, 6-wide wrap — mirrors the Player tab's subcategory chips.
            var perRow = 0;
            GUILayout.BeginHorizontal();
            foreach (var s in list)
            {
                if (perRow == 6) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); perRow = 0; }
                // Act only on an off->on FLIP. An untouched Toggle returns the value it was drawn with, so
                // the chip that is already selected reports true on every pass — and because it is drawn
                // later in this same loop than the chip the user just clicked, a plain "if (on)" would let
                // it overwrite the new selection and snap straight back. Symptom: you can move forward
                // through the chips but never back to an earlier one. The base panel hit this exact bug in
                // the What's New / Bug Report toggle; same rule applies here.
                var wasOn = chipLayout == s.Id;
                var on = GUILayout.Toggle(wasOn, Loc.T(s.LocKey), _chipStyleOrButton(), GUILayout.MinWidth(90));
                if (on && !wasOn) chip = s.Id;
                perRow++;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            foreach (var s in list)
            {
                if (s.Id != chipLayout) continue;
                s.Draw();
                break;
            }
        }

        // The Items/Player chips use _buttonStyle as their toggle style; reuse it so chips skin identically.
        private GUIStyle _chipStyleOrButton() => _buttonStyle ?? GUI.skin.button;

        // ---- wave hook declarations (implemented by wave files; calls vanish if a wave is absent) ----
        partial void FeaturesInitWave1();
        partial void FeaturesInitWave2();
        partial void FeaturesInitWave3();
        partial void FeaturesInitWave4();
        partial void FeaturesInitWave5();
        partial void FeaturesInitWave6();
        partial void FeaturesInitWave7();
        partial void FeaturesInitExtra();

        partial void FeaturesResetWave1();
        partial void FeaturesResetWave2();
        partial void FeaturesResetWave3();
        partial void FeaturesResetWave4();
        partial void FeaturesResetWave5();
        partial void FeaturesResetWave6();
        partial void FeaturesResetWave7();
        partial void FeaturesResetExtra();

        partial void FeaturesGuiWave1();
        partial void FeaturesGuiWave3();
        partial void FeaturesGuiWave5();
        partial void FeaturesGuiWave6();

        partial void FeaturesTickWave1();
        partial void FeaturesTickWave2();
        partial void FeaturesTickWave3();
        partial void FeaturesTickWave4();
        partial void FeaturesTickWave5();
        partial void FeaturesTickWave6();
        partial void FeaturesTickWave7();

        // ---- round 2 (wave 8) hooks: one group per glue file (Wave8ObjectsGlue.cs etc.) ----
        partial void FeaturesInitWave8Objects();
        partial void FeaturesInitWave8Toolkit();
        partial void FeaturesInitWave8Rules();
        partial void FeaturesInitWave8Systems();

        partial void FeaturesResetWave8Objects();
        partial void FeaturesResetWave8Toolkit();
        partial void FeaturesResetWave8Rules();
        partial void FeaturesResetWave8Systems();

        partial void FeaturesGuiWave8Objects();
        partial void FeaturesGuiWave8Toolkit();
        partial void FeaturesGuiWave8Rules();
        partial void FeaturesGuiWave8Systems();

        partial void FeaturesTickWave8Objects();
        partial void FeaturesTickWave8Toolkit();
        partial void FeaturesTickWave8Rules();
        partial void FeaturesTickWave8Systems();
    }
}

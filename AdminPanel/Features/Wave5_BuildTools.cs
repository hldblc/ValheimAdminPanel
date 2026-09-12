using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 5 - Build tools section (Extras tab) ====================
    // Client-side builder/inspection kit: piece editor, blueprint capture/stamp, terrain reset,
    // free camera + ruler, personal loadout vault.
    //
    // Member prefix: "Bld". Locale prefix: "bld.".
    //
    // WHY EVERYTHING HERE IS CLIENT-SIDE: these tools operate on objects the local client has actually
    // loaded, exactly like the shipping Clear Trees / Repair Builds / Cleanup Drops area tools
    // (AdminPanelPlugin.cs:3543-3596). The canonical shape is copied from ClearTrees: enumerate live
    // components, filter by Vector3.Distance, then nview.ClaimOwnership() + nview.Destroy(). Claiming
    // ownership FIRST is not optional - a ZDO write made by a non-owner is discarded on the next sync
    // and the edit silently reverts.
    //
    // IMGUI law observed throughout: BldTick() runs in LateUpdate and rewrites the raycast target at any
    // time, so DrawBuildToolsSection reads ONLY the *Layout snapshots pinned on the Layout pass. Every
    // branch that decides HOW MANY controls are emitted (target present / absent, blueprint rows,
    // loadout rows) is gated on a snapshot; the live field flips whenever it likes and the change shows
    // up on the next frame.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _bldSectionCfg;
        private ConfigEntry<float> _bldTargetRangeCfg;
        private ConfigEntry<float> _bldMoveStepCfg;
        private ConfigEntry<int> _bldBpRadiusCfg;
        private ConfigEntry<string> _bldBlueprintsCfg;
        private ConfigEntry<int> _bldTerrainRadiusCfg;
        private ConfigEntry<float> _bldCamSpeedCfg;
        private ConfigEntry<string> _bldLoadoutsCfg;
        private bool _bldInited;

        // Hard caps. Both are storage caps as much as UI caps: the two stores live in the BepInEx config
        // file, and an unbounded capture would write a multi-megabyte line into it.
        private const int BldMaxBlueprintPieces = 200;
        private const int BldMaxLoadoutEntries = 64;
        private const float BldMaxRadius = 64f;

        // ---- live state (written by BldTick / event pass; NEVER read for gating) ----
        private bool _bldTargeting;
        private Piece _bldTarget;
        private string _bldTgtName = "", _bldTgtHealth = "", _bldTgtOwner = "";
        private bool _bldFreeCamOurs;          // we turned free-fly on (so Escape may turn it off again)
        private Vector3 _bldRulerA, _bldRulerB;
        private bool _bldRulerASet, _bldRulerBSet;

        // ---- per-frame Layout snapshots (the ONLY things draw code gates on) ----
        private bool _bldTargetingLayout;
        private bool _bldHasTargetLayout;
        private Piece _bldTargetLayout;        // the click handlers act on THIS, not on the live field
        private string _bldTgtNameLayout = "", _bldTgtHealthLayout = "", _bldTgtOwnerLayout = "";
        private bool _bldFreeCamLayout;
        private bool _bldPlayerLayout;
        private List<BldStoreRow> _bldBpRowsLayout;
        private List<BldStoreRow> _bldLoRowsLayout;

        // ---- editable numeric fields (text so a half-typed value can never be parsed) ----
        private string _bldMoveStepText = "0.25";
        private string _bldBpRadiusText = "12";
        private string _bldBpNameText = "";
        private string _bldTerrainRadiusText = "20";
        private string _bldCamSpeedText = "20";
        private string _bldLoNameText = "";
        private Vector2 _bldScroll;

        // ---- store row model (name + payload + entry count, built once per Layout pass) ----
        private struct BldStoreRow { public string Name; public string Payload; public int Count; }

        // Cache the ParseKv of each store against its raw config string: any edit changes the string,
        // which invalidates the cache with no explicit bookkeeping (same trick as PresetsKv()).
        private string _bldBpRaw, _bldLoRaw;
        private Dictionary<string, string> _bldBpKv, _bldLoKv;

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and wires BldTick into the
        // wave-5 tick hook. No Harmony patches: the free camera uses GameCamera's OWN public
        // ToggleFreeFly()/InFreeFly() API, so nothing here has to be patched into the game.
        internal void BldInit()
        {
            if (_bldInited) return;
            _bldInited = true;

            _bldSectionCfg = Config.Bind("Features", "ShowBuildToolsSection", true,
                "Show the Build Tools section in the Extras tab (piece editor, blueprints, terrain reset, free camera, loadouts). Client-side UI only - it changes nothing on its own.");
            _bldTargetRangeCfg = Config.Bind("Features", "BuildPieceTargetRange", 50f,
                new ConfigDescription("How far the piece editor's crosshair ray reaches, in metres.",
                    new AcceptableValueRange<float>(5f, 200f)));
            _bldMoveStepCfg = Config.Bind("Features", "BuildPieceMoveStep", 0.25f,
                new ConfigDescription("Default nudge distance for the piece editor's Move buttons, in metres.",
                    new AcceptableValueRange<float>(0.05f, 5f)));
            _bldBpRadiusCfg = Config.Bind("Features", "BuildBlueprintRadius", 12,
                new ConfigDescription("Default capture radius for blueprints, in metres.",
                    new AcceptableValueRange<int>(1, (int)BldMaxRadius)));
            _bldBlueprintsCfg = Config.Bind("Features", "BuildBlueprints", "",
                "Saved blueprints (percent-escaped name=payload records). Edited through the panel; hand-editing is possible but a malformed record is skipped.");
            _bldTerrainRadiusCfg = Config.Bind("Features", "BuildTerrainResetRadius", 20,
                new ConfigDescription("Default radius for Terrain Reset, in metres. IRREVERSIBLE - see the in-panel warning.",
                    new AcceptableValueRange<int>(1, (int)BldMaxRadius)));
            _bldCamSpeedCfg = Config.Bind("Features", "BuildFreeCamSpeed", 20f,
                new ConfigDescription("Free camera movement speed, in metres per second.",
                    new AcceptableValueRange<float>(1f, 200f)));
            _bldLoadoutsCfg = Config.Bind("Features", "BuildLoadouts", "",
                "Saved personal loadouts (percent-escaped name=payload records, prefab:count:quality per entry).");

            // Seed the editable text fields from config so the panel opens on the persisted values.
            _bldMoveStepText = BldF(_bldMoveStepCfg.Value);
            _bldBpRadiusText = _bldBpRadiusCfg.Value.ToString(CultureInfo.InvariantCulture);
            _bldTerrainRadiusText = _bldTerrainRadiusCfg.Value.ToString(CultureInfo.InvariantCulture);
            _bldCamSpeedText = BldF(_bldCamSpeedCfg.Value);
        }

        internal bool BldSectionEnabled() => _bldSectionCfg == null || _bldSectionCfg.Value;

        // Called on logout. Every per-world field goes: a Piece reference from the world just left would
        // otherwise keep a destroyed GameObject alive in the snapshot and paint live Remove buttons for
        // it on the next world. The two stores are config-backed and deliberately survive (they are the
        // admin's own library, not world state) - blueprints are position-relative, loadouts are prefab
        // lists, so neither carries anything world-specific.
        internal void BldReset()
        {
            _bldTargeting = false;
            _bldTarget = null;
            _bldTgtName = _bldTgtHealth = _bldTgtOwner = "";
            _bldFreeCamOurs = false;
            _bldRulerA = _bldRulerB = Vector3.zero;
            _bldRulerASet = _bldRulerBSet = false;

            _bldTargetingLayout = false;
            _bldHasTargetLayout = false;
            _bldTargetLayout = null;
            _bldTgtNameLayout = _bldTgtHealthLayout = _bldTgtOwnerLayout = "";
            _bldFreeCamLayout = false;
            _bldPlayerLayout = false;
            _bldBpRowsLayout = null;
            _bldLoRowsLayout = null;

            _bldBpNameText = "";
            _bldLoNameText = "";
            _bldScroll = Vector2.zero;

            _bldBpRaw = _bldLoRaw = null;
            _bldBpKv = _bldLoKv = null;
        }

        // ==================== tick (LateUpdate) ====================

        // Two live jobs: refresh the crosshair target while targeting mode is on, and drive the free
        // camera's extra Q/E axis + Escape exit. Everything is guarded so this is a no-op on the main
        // menu and cannot throw into the shared LateUpdate.
        internal void BldTick()
        {
            if (!_bldInited) return;
            // Nothing here may run while the operator has the Extras tab or this section switched off:
            // the tick reads raw Input and moves the camera, so an "off" section that keeps ticking is a
            // feature the admin cannot turn off from the UI it belongs to.
            if (!FeaturesEnabled() || !BldSectionEnabled()) return;
            try
            {
                var player = LocalPlayer;
                if (player == null)
                {
                    _bldTarget = null;
                    return;
                }
                if (_bldTargeting) BldUpdateTarget();
                else if (_bldTarget != null) _bldTarget = null;
                BldTickFreeCam();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Build tools tick failed (feature degraded, panel unaffected): {e.Message}");
            }
        }

        // Reused hit buffer: RaycastNonAlloc keeps a per-frame raycast from allocating. The nearest hit
        // is not necessarily the piece (a shrub collider, a character, the terrain in front of a wall),
        // so scan every hit and keep the closest one that resolves to a Piece.
        private static readonly RaycastHit[] BldHits = new RaycastHit[48];

        private void BldUpdateTarget()
        {
            var cam = GameCamera.instance;
            if (cam == null) { _bldTarget = null; return; }
            var range = _bldTargetRangeCfg != null ? Mathf.Clamp(_bldTargetRangeCfg.Value, 5f, 200f) : 50f;

            var n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, BldHits, range,
                ~0, QueryTriggerInteraction.Ignore);
            Piece best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var col = BldHits[i].collider;
                if (col == null) continue;
                var piece = col.GetComponentInParent<Piece>();
                if (piece == null) continue;
                if (BldHits[i].distance >= bestDist) continue;
                bestDist = BldHits[i].distance;
                best = piece;
            }

            _bldTarget = best;
            if (best == null) { _bldTgtName = _bldTgtHealth = _bldTgtOwner = ""; return; }

            _bldTgtName = BldPieceLabel(best);
            _bldTgtHealth = BldPieceHealth(best);
            _bldTgtOwner = BldPieceOwner(best);
        }

        // Free camera extras. The camera detach itself is GameCamera's own free-fly mode (see
        // BldToggleFreeCam) - the game already moves the camera with WASD + Space/Ctrl while free-fly is
        // on, and it integrates onto the camera transform (GameCamera.UpdateFreeFly:
        // transform.position += vel * dt), so an extra Q/E offset written here simply composes with it.
        // Q/E are ignored while an IMGUI text field owns the keyboard so typing in the panel cannot fly
        // the camera; the game's own WASD is outside our reach and is called out in the hint text.
        private void BldTickFreeCam()
        {
            if (!BldFreeCamActive()) { _bldFreeCamOurs = false; return; }
            var cam = GameCamera.instance;
            if (cam == null) return;

            if (_bldFreeCamOurs && Input.GetKeyDown(KeyCode.Escape))
            {
                BldToggleFreeCam();
                return;
            }

            // Only a free-fly session THIS panel started gets the extra axis. GameCamera.InFreeFly() is
            // global game state that the vanilla `freefly` console command and other mods set too, and
            // rebinding E (the game's own Use key) under someone else's free camera is a hijack the admin
            // never asked for and cannot switch off from here.
            if (!_bldFreeCamOurs || GUIUtility.keyboardControl != 0) return;
            var dir = 0f;
            if (Input.GetKey(KeyCode.E)) dir += 1f;
            if (Input.GetKey(KeyCode.Q)) dir -= 1f;
            if (Mathf.Approximately(dir, 0f)) return;
            cam.transform.position += Vector3.up * (dir * BldCamSpeed() * Time.unscaledDeltaTime);
        }

        // ==================== drawing ====================

        internal void DrawBuildToolsSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _bldTargetingLayout = _bldTargeting;
                _bldTargetLayout = BldTargetUsable(_bldTarget) ? _bldTarget : null;
                _bldHasTargetLayout = _bldTargetLayout != null;
                _bldTgtNameLayout = _bldTgtName ?? "";
                _bldTgtHealthLayout = _bldTgtHealth ?? "";
                _bldTgtOwnerLayout = _bldTgtOwner ?? "";
                _bldFreeCamLayout = BldFreeCamActive();
                _bldPlayerLayout = LocalPlayer != null;
                _bldBpRowsLayout = BldRows(BldBlueprintKv());
                _bldLoRowsLayout = BldRows(BldLoadoutKv());
            }

            _bldScroll = GUILayout.BeginScrollView(_bldScroll, GUILayout.Height(ListView(150f)));

            BldDrawPieceCard();
            BldDrawBlueprintCard();
            BldDrawTerrainCard();
            BldDrawCameraCard();
            BldDrawLoadoutCard();

            GUILayout.EndScrollView();
        }

        // ---- 1. piece editor ----

        private void BldDrawPieceCard()
        {
            BeginCard(Loc.T("bld.piece_section"));

            GUILayout.BeginHorizontal();
            var targeting = GUILayout.Toggle(_bldTargetingLayout, " " + Loc.T("bld.targeting"), _toggleStyle);
            if (targeting != _bldTargetingLayout) _bldTargeting = targeting;   // live field flips; snapshot lands next frame
            GUILayout.FlexibleSpace();
            GUILayout.Label(Loc.T("bld.move_step"), _labelStyle, GUILayout.MinWidth(70));
            _bldMoveStepText = GUILayout.TextField(_bldMoveStepText ?? "", _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.EndHorizontal();

            // Info lines are plain labels either way - a label with a dash placeholder is still one
            // control, so the target coming and going never perturbs the count here.
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.target_name"), _labelStyle, GUILayout.MinWidth(70));
            GUILayout.Label(_bldHasTargetLayout ? _bldTgtNameLayout : "-", _cellStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.target_health"), _labelStyle, GUILayout.MinWidth(70));
            GUILayout.Label(_bldHasTargetLayout ? _bldTgtHealthLayout : "-", _cellStyle, GUILayout.MinWidth(120));
            GUILayout.Label(Loc.T("bld.target_owner"), _labelStyle, GUILayout.MinWidth(70));
            GUILayout.Label(_bldHasTargetLayout ? _bldTgtOwnerLayout : "-", _dimCellStyle, GUILayout.MinWidth(140));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // THE control-count gate: this branch reads the Layout snapshot, never the live raycast
            // result. Losing the target between the Layout and Repaint pass of one frame therefore
            // cannot change how many controls this card emitted - the swap happens on the next frame.
            if (!_bldHasTargetLayout)
            {
                GUILayout.Label(Loc.T("bld.no_target"), _hintStyle);
            }
            else
            {
                var target = _bldTargetLayout;   // act on the pinned reference, not on the live one
                GUILayout.BeginHorizontal();
                if (ConfirmButton("bld:remove", Loc.T("bld.remove"), GUILayout.MinWidth(90)))
                    BldRemovePiece(target);
                if (GUILayout.Button(Loc.T("bld.repair"), _buttonStyle, GUILayout.MinWidth(90)))
                    BldRepairPiece(target);
                if (GUILayout.Button(Loc.T("bld.rot_left"), _buttonStyle, GUILayout.MinWidth(80)))
                    BldRotatePiece(target, -22.5f);
                if (GUILayout.Button(Loc.T("bld.rot_right"), _buttonStyle, GUILayout.MinWidth(80)))
                    BldRotatePiece(target, 22.5f);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                var step = BldMoveStep();
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("bld.move_label"), _labelStyle, GUILayout.MinWidth(60));
                if (GUILayout.Button("X-", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(-step, 0f, 0f));
                if (GUILayout.Button("X+", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(step, 0f, 0f));
                if (GUILayout.Button("Y-", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(0f, -step, 0f));
                if (GUILayout.Button("Y+", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(0f, step, 0f));
                if (GUILayout.Button("Z-", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(0f, 0f, -step));
                if (GUILayout.Button("Z+", _buttonStyle, GUILayout.MinWidth(46))) BldMovePiece(target, new Vector3(0f, 0f, step));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.Label(Loc.T("bld.targeting_hint"), _hintStyle);
            EndCard();
        }

        // ---- 2. blueprints ----

        private void BldDrawBlueprintCard()
        {
            BeginCard(Loc.T("bld.bp_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.bp_name"), _labelStyle, GUILayout.MinWidth(70));
            _bldBpNameText = GUILayout.TextField(_bldBpNameText ?? "", _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.Label(Loc.T("bld.bp_radius"), _labelStyle, GUILayout.MinWidth(60));
            _bldBpRadiusText = GUILayout.TextField(_bldBpRadiusText ?? "", _textFieldStyle, GUILayout.MinWidth(50));
            if (GUILayout.Button(Loc.T("bld.bp_capture"), _buttonStyle, GUILayout.MinWidth(90)))
                BldCaptureBlueprint();
            GUILayout.EndHorizontal();

            var rows = _bldBpRowsLayout;
            if (rows == null || rows.Count == 0)
            {
                GUILayout.Label(Loc.T("bld.bp_empty"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(row.Name, _cellStyle, GUILayout.MinWidth(180));
                    GUILayout.Label(Loc.T("bld.bp_count", row.Count), _dimCellStyle, GUILayout.MinWidth(90));
                    GUILayout.FlexibleSpace();
                    // Per-name confirm ids: arming Stamp on one blueprint must never arm it on another.
                    if (ConfirmButton("bld:bpstamp:" + row.Name, Loc.T("bld.bp_stamp"), GUILayout.MinWidth(80)))
                        BldStampBlueprint(row);
                    if (ConfirmButton("bld:bpdel:" + row.Name, Loc.T("bld.bp_delete"), GUILayout.MinWidth(70)))
                        BldDeleteStoreEntry(_bldBlueprintsCfg, row.Name, "bld.msg_bp_deleted");
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("bld.bp_hint"), _hintStyle);
            EndCard();
        }

        // ---- 3. terrain reset ----

        private void BldDrawTerrainCard()
        {
            BeginCard(Loc.T("bld.terrain_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.terrain_radius"), _labelStyle, GUILayout.MinWidth(70));
            _bldTerrainRadiusText = GUILayout.TextField(_bldTerrainRadiusText ?? "", _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.FlexibleSpace();
            if (ConfirmButton("bld:terrainreset", Loc.T("bld.terrain_reset"), GUILayout.MinWidth(140)))
                BldResetTerrain();
            GUILayout.EndHorizontal();

            // Colour via GUI.contentColor save/restore (no new GUIStyle may be created here). The
            // restore is on the straight-line path with nothing between that can return or throw.
            var prev = GUI.contentColor;
            GUI.contentColor = new Color(1f, 0.55f, 0.45f);
            GUILayout.Label(Loc.T("bld.terrain_warn"), _hintStyle);
            GUI.contentColor = prev;
            GUILayout.Label(Loc.T("bld.terrain_hint"), _hintStyle);
            EndCard();
        }

        // ---- 4. free camera + ruler ----

        private void BldDrawCameraCard()
        {
            BeginCard(Loc.T("bld.cam_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.cam_state", Loc.T(_bldFreeCamLayout ? "bld.cam_active" : "bld.cam_inactive")),
                _labelStyle, GUILayout.MinWidth(160));
            GUILayout.Label(Loc.T("bld.cam_speed"), _labelStyle, GUILayout.MinWidth(60));
            _bldCamSpeedText = GUILayout.TextField(_bldCamSpeedText ?? "", _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.FlexibleSpace();
            // One button, label swap - never a conditionally emitted control.
            if (GUILayout.Button(Loc.T(_bldFreeCamLayout ? "bld.cam_disable" : "bld.cam_enable"), _buttonStyle,
                    GUILayout.MinWidth(130)))
                BldToggleFreeCam();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("bld.cam_hint"), _hintStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.ruler_a", _bldRulerASet ? BldVec(_bldRulerA) : Loc.T("bld.ruler_unset")),
                _cellStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("bld.ruler_set_a"), _buttonStyle, GUILayout.MinWidth(80))) BldSetRuler(true);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.ruler_b", _bldRulerBSet ? BldVec(_bldRulerB) : Loc.T("bld.ruler_unset")),
                _cellStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("bld.ruler_set_b"), _buttonStyle, GUILayout.MinWidth(80))) BldSetRuler(false);
            GUILayout.EndHorizontal();

            var both = _bldRulerASet && _bldRulerBSet;
            var d3 = both ? Vector3.Distance(_bldRulerA, _bldRulerB) : 0f;
            var flat = both
                ? Vector2.Distance(new Vector2(_bldRulerA.x, _bldRulerA.z), new Vector2(_bldRulerB.x, _bldRulerB.z))
                : 0f;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.ruler_dist", both ? BldF(d3) : "-"), _labelStyle, GUILayout.MinWidth(140));
            GUILayout.Label(Loc.T("bld.ruler_flat", both ? BldF(flat) : "-"), _labelStyle, GUILayout.MinWidth(140));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("bld.ruler_clear"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                _bldRulerASet = _bldRulerBSet = false;
                _bldRulerA = _bldRulerB = Vector3.zero;
                Message(Loc.T("bld.msg_ruler_cleared"));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("bld.ruler_hint"), _hintStyle);
            EndCard();
        }

        // ---- 5. personal loadout vault ----

        private void BldDrawLoadoutCard()
        {
            BeginCard(Loc.T("bld.lo_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bld.lo_name"), _labelStyle, GUILayout.MinWidth(70));
            _bldLoNameText = GUILayout.TextField(_bldLoNameText ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("bld.lo_save"), _buttonStyle, GUILayout.MinWidth(90)))
                BldSaveLoadout();
            GUILayout.EndHorizontal();

            var rows = _bldLoRowsLayout;
            if (rows == null || rows.Count == 0)
            {
                GUILayout.Label(Loc.T("bld.lo_empty"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(row.Name, _cellStyle, GUILayout.MinWidth(180));
                    GUILayout.Label(Loc.T("bld.lo_count", row.Count), _dimCellStyle, GUILayout.MinWidth(90));
                    GUILayout.FlexibleSpace();
                    if (ConfirmButton("bld:lorestore:" + row.Name, Loc.T("bld.lo_restore"), GUILayout.MinWidth(90)))
                        BldRestoreLoadout(row);
                    if (ConfirmButton("bld:lodel:" + row.Name, Loc.T("bld.lo_delete"), GUILayout.MinWidth(70)))
                        BldDeleteStoreEntry(_bldLoadoutsCfg, row.Name, "bld.msg_lo_deleted");
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("bld.lo_hint"), _hintStyle);
            EndCard();
        }

        // ==================== piece mutations ====================
        // Every one of these claims ownership FIRST. A ZDO written by a non-owner is thrown away on the
        // next sync, so without the claim the edit appears to work for one frame and then reverts.

        private void BldRemovePiece(Piece piece)
        {
            var nview = BldNView(piece);
            if (nview == null) { Message(Loc.T("bld.msg_no_target")); return; }
            var label = BldPieceLabel(piece);
            nview.ClaimOwnership();
            nview.Destroy();
            _bldTarget = null;
            Message(Loc.T("bld.msg_removed", label));
        }

        // WearNTear stores current health in the ZDO under ZDOVars.s_health and treats WearNTear.m_health
        // as the maximum (WearNTear.Awake / RPC_Repair). WearNTear.Repair() is deliberately NOT used: it
        // is rate-limited to one call per second, refuses when health is already full, and routes through
        // an RPC to the owner - having just claimed ownership we can do exactly what RPC_Repair does,
        // directly and unthrottled. The RPC_HealthChanged broadcast is what refreshes the damage visuals
        // on every peer; it is best-effort and a failure only leaves stale cracks on other screens.
        private void BldRepairPiece(Piece piece)
        {
            var nview = BldNView(piece);
            if (nview == null) { Message(Loc.T("bld.msg_no_target")); return; }
            nview.ClaimOwnership();
            var zdo = nview.GetZDO();
            var wnt = piece.GetComponent<WearNTear>();
            if (zdo == null || wnt == null) { Message(Loc.T("bld.msg_no_wear")); return; }
            zdo.Set(ZDOVars.s_health, wnt.m_health);
            try { nview.InvokeRPC(ZNetView.Everybody, "RPC_HealthChanged", wnt.m_health); }
            catch (Exception) { /* visuals only - the authoritative value is already in the ZDO */ }
            _bldTgtHealth = BldPieceHealth(piece);
            Message(Loc.T("bld.msg_repaired", BldPieceLabel(piece)));
        }

        // Rotation is applied to the transform AND mirrored into the ZDO: the transform is what the local
        // client renders, the ZDO is what every other peer (and the next load of this zone) reads.
        private void BldRotatePiece(Piece piece, float degrees)
        {
            var nview = BldNView(piece);
            if (nview == null) { Message(Loc.T("bld.msg_no_target")); return; }
            nview.ClaimOwnership();
            var zdo = nview.GetZDO();
            if (zdo == null) { Message(Loc.T("bld.msg_no_target")); return; }
            var t = piece.transform;
            t.rotation = Quaternion.Euler(0f, degrees, 0f) * t.rotation;
            zdo.SetRotation(t.rotation);
            Message(Loc.T("bld.msg_rotated", BldF(degrees)));
        }

        private void BldMovePiece(Piece piece, Vector3 delta)
        {
            var nview = BldNView(piece);
            if (nview == null) { Message(Loc.T("bld.msg_no_target")); return; }
            nview.ClaimOwnership();
            var zdo = nview.GetZDO();
            if (zdo == null) { Message(Loc.T("bld.msg_no_target")); return; }
            var t = piece.transform;
            t.position += delta;
            zdo.SetPosition(t.position);
            Message(Loc.T("bld.msg_moved", BldF(delta.magnitude)));
        }

        private static ZNetView BldNView(Piece piece)
        {
            if (piece == null) return null;
            var nview = piece.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() ? nview : null;
        }

        private static bool BldTargetUsable(Piece piece) => BldNView(piece) != null;

        // ==================== blueprint capture / stamp ====================

        // Capture records every Piece in radius as (prefab, offset relative to the player, yaw relative
        // to the player's yaw), so a blueprint can be stamped facing any direction later. Refuses rather
        // than truncates past the cap: a silently clipped building is worse than no blueprint.
        private void BldCaptureBlueprint()
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("bld.msg_no_player")); return; }
            var name = BldTrim(_bldBpNameText);
            if (name == null) { Message(Loc.T("bld.msg_bp_need_name")); return; }

            var radius = BldRadius(_bldBpRadiusText, _bldBpRadiusCfg);
            var origin = player.transform.position;
            var yaw = player.transform.rotation.eulerAngles.y;
            var toLocal = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));

            var found = new List<string>();
            var overflow = false;
            foreach (var piece in FindObjectsOfType<Piece>())
            {
                if (piece == null) continue;
                if (Vector3.Distance(piece.transform.position, origin) > radius) continue;
                if (BldNView(piece) == null) continue;
                var prefab = BldPrefabName(piece.gameObject);
                if (string.IsNullOrEmpty(prefab)) continue;
                if (found.Count >= BldMaxBlueprintPieces) { overflow = true; break; }
                var local = toLocal * (piece.transform.position - origin);
                var relYaw = Mathf.DeltaAngle(yaw, piece.transform.rotation.eulerAngles.y);
                found.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:0.###},{2:0.###},{3:0.###},{4:0.#}",
                    prefab, local.x, local.y, local.z, relYaw));
            }

            if (overflow) { Message(Loc.T("bld.msg_bp_too_many", BldMaxBlueprintPieces)); return; }
            if (found.Count == 0) { Message(Loc.T("bld.msg_bp_none_found")); return; }

            BldWriteStoreEntry(_bldBlueprintsCfg, name, string.Join(";", found.ToArray()));
            Message(Loc.T("bld.msg_bp_captured", found.Count, name));
        }

        // Stamp goes through the panel's EXISTING server spawn path (SendServerSpawn -> AP_SrvSpawn),
        // kind 0, one object per request: kind 0 instantiates the prefab at the exact position given,
        // while kind 1 deliberately scatters spawns by up to 1.5m (right for creatures, wrong for
        // buildings). See the limitations list for what this path cannot carry - most importantly the
        // spawn RPC has no rotation field, so pieces land at the world's default rotation even though
        // the blueprint stores the captured yaw.
        private void BldStampBlueprint(BldStoreRow row)
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("bld.msg_no_player")); return; }
            if (string.IsNullOrEmpty(row.Payload)) { Message(Loc.T("bld.msg_bp_none_found")); return; }

            var origin = player.transform.position;
            var yaw = player.transform.rotation.eulerAngles.y;
            var toWorld = Quaternion.Euler(0f, yaw, 0f);

            var sent = 0;
            foreach (var entry in row.Payload.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(',');
                if (f.Length < 4) continue;   // malformed record: skip it, keep the rest of the blueprint
                float x, y, z;
                if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                var pos = origin + toWorld * new Vector3(x, y, z);
                SendServerSpawn(0, f[0], pos, 1, 1, false);
                sent++;
                if (sent >= BldMaxBlueprintPieces) break;
            }
            Message(Loc.T("bld.msg_bp_stamped", sent));
        }

        // ==================== terrain reset ====================

        // Two independent terrain systems have to be undone, which is why this is not a one-liner:
        //
        //  (a) TerrainModifier components. Heightmap.ApplyModifiers walks TerrainModifier.GetAllInstances()
        //      every time a zone regenerates, so removing the object removes its contribution, and
        //      TerrainModifier.OnDestroy pokes every affected heightmap for us. Destroying alone DOES
        //      visually revert this class of edit.
        //
        //  (b) TerrainComp deltas. The hoe/pickaxe write per-vertex level+smooth deltas into the zone's
        //      TerrainComp arrays, which are serialized into the ZDO blob ZDOVars.s_TCData and applied by
        //      Heightmap.ApplyModifiers via TerrainComp.ApplyToHeightmap. No object exists to destroy -
        //      destroying modifiers does NOT revert these. They are cleared here by zeroing the affected
        //      vertices and re-running TerrainComp's own private Save(bool paintOnly), then poking the
        //      heightmap. Those members are private, so they are reached through AccessTools; every handle
        //      is validated BEFORE a single array is written, each compiler's arrays are snapshotted first,
        //      and a save the engine does not accept restores the snapshot. A game update that renames any
        //      of them degrades this to "modifier edits only" plus a logged warning - never a crash, and
        //      never a zone left zeroed on this client but unsaved.
        private void BldResetTerrain()
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("bld.msg_no_player")); return; }
            var radius = BldRadius(_bldTerrainRadiusText, _bldTerrainRadiusCfg);
            var origin = player.transform.position;

            var mods = 0;
            foreach (var tm in FindObjectsOfType<TerrainModifier>())
            {
                if (tm == null || Vector3.Distance(tm.transform.position, origin) > radius) continue;
                var nview = tm.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                nview.ClaimOwnership();
                nview.Destroy();
                mods++;
            }

            var zones = 0;
            try { zones = BldResetTerrainComps(origin, radius); }
            catch (Exception e)
            {
                Logger.LogWarning($"Terrain reset: compiler pass failed, only modifier-based edits reverted: {e.Message}");
            }

            Message(Loc.T("bld.msg_terrain", mods, zones));
        }

        // Cached reflection handles for TerrainComp's private state, resolved once. 1.0.12 shapes
        // (TerrainComp.cs): bool m_initialized; int m_width; bool[] m_modifiedHeight; float[] m_levelDelta;
        // float[] m_smoothDelta; bool[] m_modifiedPaint; Color[] m_paintMask; Heightmap m_hmap; and
        // private void Save(bool paintOnly = false). ALL of them are required: Save serializes every one
        // of the five arrays unconditionally, so a missing paint field means the blob format changed and
        // the reset must not write at all.
        private static bool _bldTcResolved;
        private static System.Reflection.FieldInfo _bldTcInit, _bldTcWidth, _bldTcModH, _bldTcLevel,
            _bldTcSmooth, _bldTcModP, _bldTcPaint, _bldTcHmap;
        private static System.Reflection.MethodInfo _bldTcSave;
        private static object[] _bldTcSaveArgs;   // matches _bldTcSave's arity: { false } for Save(bool), empty for Save()

        private static void BldResolveTerrainComp()
        {
            if (_bldTcResolved) return;
            _bldTcResolved = true;
            var t = AccessTools.TypeByName("TerrainComp");
            if (t == null) return;
            _bldTcInit = AccessTools.Field(t, "m_initialized");
            _bldTcWidth = AccessTools.Field(t, "m_width");
            _bldTcModH = AccessTools.Field(t, "m_modifiedHeight");
            _bldTcLevel = AccessTools.Field(t, "m_levelDelta");
            _bldTcSmooth = AccessTools.Field(t, "m_smoothDelta");
            _bldTcModP = AccessTools.Field(t, "m_modifiedPaint");
            _bldTcPaint = AccessTools.Field(t, "m_paintMask");
            _bldTcHmap = AccessTools.Field(t, "m_hmap");
            // Save is resolved by explicit parameter shape, never by name alone: 1.0.12 made it
            // Save(bool paintOnly = false), and a null-args Invoke against that overload throws
            // TargetParameterCountException. paintOnly=false is the full save (heights + paint). The
            // zero-parameter overload is accepted as a fallback for builds that still have it.
            _bldTcSave = AccessTools.Method(t, "Save", new[] { typeof(bool) });
            if (_bldTcSave != null) _bldTcSaveArgs = new object[] { false };
            else
            {
                _bldTcSave = AccessTools.Method(t, "Save", Type.EmptyTypes);
                if (_bldTcSave != null) _bldTcSaveArgs = new object[0];
            }
        }

        private static bool BldTcHandlesOk() =>
            _bldTcInit != null && _bldTcWidth != null && _bldTcModH != null && _bldTcLevel != null &&
            _bldTcSmooth != null && _bldTcModP != null && _bldTcPaint != null && _bldTcHmap != null &&
            _bldTcSave != null && !_bldTcSave.IsStatic && _bldTcSaveArgs != null;

        // Pre-reset copy of one compiler's five arrays. They are zeroed IN PLACE (they are the compiler's
        // own instances, reached by reference), so Restore() copies the clones back into those same
        // instances and the local heightmap keeps matching the blob that is still in the ZDO.
        private sealed class BldTcSnapshot
        {
            private readonly Array[] _live, _bak;

            public BldTcSnapshot(params Array[] live)
            {
                _live = live;
                _bak = new Array[live.Length];
                for (var i = 0; i < live.Length; i++) _bak[i] = (Array)live[i].Clone();
            }

            public void Restore()
            {
                for (var i = 0; i < _live.Length; i++) Array.Copy(_bak[i], _live[i], _bak[i].Length);
            }
        }

        private int BldResetTerrainComps(Vector3 origin, float radius)
        {
            BldResolveTerrainComp();
            if (!BldTcHandlesOk())
            {
                Logger.LogWarning("Terrain reset: TerrainComp internals not found (game updated?) - only modifier-based edits were reverted.");
                return 0;
            }

            var touched = 0;
            foreach (var comp in FindObjectsOfType<TerrainComp>())
            {
                if (comp == null) continue;
                var nview = comp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                if (!(_bldTcInit.GetValue(comp) is bool init) || !init) continue;

                var hmap = _bldTcHmap.GetValue(comp) as Heightmap;
                if (hmap == null) continue;
                // Zone bounds test first: a world can hold dozens of loaded zones and only the ones the
                // radius actually reaches need touching.
                var half = hmap.m_width * hmap.m_scale * 0.5f + radius;
                var zp = hmap.transform.position;
                if (Mathf.Abs(origin.x - zp.x) > half || Mathf.Abs(origin.z - zp.z) > half) continue;

                // Every per-instance value is read and shape-checked here, before anything is written.
                // TerrainComp.Initialize sizes all five arrays to (m_width+1)^2 and Save serializes all
                // five, so an unexpected shape means "not the compiler we know": skip it untouched rather
                // than write a blob the engine may not read back.
                var width = _bldTcWidth.GetValue(comp) as int? ?? 0;
                var modH = _bldTcModH.GetValue(comp) as bool[];
                var level = _bldTcLevel.GetValue(comp) as float[];
                var smooth = _bldTcSmooth.GetValue(comp) as float[];
                var modP = _bldTcModP.GetValue(comp) as bool[];
                var paint = _bldTcPaint.GetValue(comp) as Color[];
                var side = width + 1;
                var cells = side * side;
                if (width <= 0 || modH == null || level == null || smooth == null || modP == null || paint == null ||
                    modH.Length < cells || level.Length < cells || smooth.Length < cells ||
                    modP.Length < cells || paint.Length < cells)
                {
                    Logger.LogWarning($"Terrain reset: TerrainComp at {zp} has an unexpected shape (width {width}) - left untouched.");
                    continue;
                }

                // Snapshot BEFORE the first write: the loop below mutates the compiler's own arrays.
                var snapshot = new BldTcSnapshot(modH, level, smooth, modP, paint);

                // Vertex -> world mapping is the inverse of Heightmap.WorldToVertex: world = zoneOrigin +
                // (index - width/2) * scale. The paint mask uses WorldToVertexMask, whose offset is
                // (width+1)/2, so it gets its own test rather than being assumed identical.
                var scale = hmap.m_scale;
                var hOff = width / 2;
                var pOff = (width + 1) / 2;
                var changed = false;
                for (var gy = 0; gy < side; gy++)
                {
                    for (var gx = 0; gx < side; gx++)
                    {
                        var idx = gy * side + gx;
                        var hx = zp.x + (gx - hOff) * scale;
                        var hz = zp.z + (gy - hOff) * scale;
                        if (BldFlatDist(hx, hz, origin) <= radius && modH[idx])
                        {
                            // Exactly the state TerrainComp.Load leaves an unmodified vertex in: flag off,
                            // both deltas zero.
                            modH[idx] = false;
                            level[idx] = 0f;
                            smooth[idx] = 0f;
                            changed = true;
                        }
                        var px = zp.x + (gx - pOff) * scale;
                        var pz = zp.z + (gy - pOff) * scale;
                        if (BldFlatDist(px, pz, origin) <= radius && modP[idx])
                        {
                            // An unmodified paint entry is neither applied nor serialized; its colour only
                            // matters as the "from" of a later paint op, and "nothing" (black, alpha 1 -
                            // Heightmap.m_paintMaskNothing) is the engine's own reset target.
                            modP[idx] = false;
                            paint[idx] = new Color(0f, 0f, 0f, paint[idx].a);   // alpha is the world-gen base mask, not paint (Heightmap.cs:1205): keep it, as every engine writer does
                            changed = true;
                        }
                    }
                }
                if (!changed) continue;

                // Save early-outs silently unless we own the ZDO, so ownership is claimed right before the
                // call. A save the engine performs always bumps DataRevision (ZDO.Set(int, byte[]) ->
                // IncreaseDataRevision; the compressed blob is a fresh array every time), so an unchanged
                // revision means the write was refused. Either way the snapshot goes back so this client
                // never keeps zeroed arrays that the ZDO does not hold - the next vanilla Save in the zone
                // would otherwise persist them.
                var zdo = nview.GetZDO();
                var saved = false;
                try
                {
                    nview.ClaimOwnership();
                    var rev = zdo.DataRevision;
                    _bldTcSave.Invoke(comp, _bldTcSaveArgs);
                    saved = zdo.DataRevision != rev;
                    if (!saved) Logger.LogWarning($"Terrain reset: TerrainComp at {zp} did not accept the save (not owner?) - zone left as it was.");
                }
                catch (Exception e)
                {
                    var inner = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
                    Logger.LogWarning($"Terrain reset: TerrainComp at {zp} save failed - zone left as it was: {inner.Message}");
                }
                if (!saved)
                {
                    snapshot.Restore();
                    continue;
                }

                // Only a saved zone is regenerated, so the local visual state can never outrun the blob.
                // Poke is a direct call - 1.0.12: public void Poke(int delayed = 0, bool paintOnly = false),
                // immediate full regen with the defaults. If it ever fails the data is already consistent,
                // so the zone still counts and only the regen waits for the next vanilla poke.
                try { hmap.Poke(); }
                catch (Exception e) { Logger.LogWarning($"Terrain reset: heightmap regen at {zp} failed after save: {e.Message}"); }
                BldResetGrass(hmap, origin, radius);
                touched++;
            }
            return touched;
        }

        // ClutterSystem lives in its own class; keeping the call in a separate method means a game update
        // that removes it fails when THIS method is first jitted - inside the caller's try/catch - instead
        // of taking the whole terrain reset down.
        private static void BldResetGrass(Heightmap hmap, Vector3 origin, float radius)
        {
            try { if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(origin, radius); }
            catch (Exception) { /* grass is cosmetic; it regrows on the next zone rebuild */ }
        }

        private static float BldFlatDist(float x, float z, Vector3 origin) =>
            Mathf.Sqrt((x - origin.x) * (x - origin.x) + (z - origin.z) * (z - origin.z));

        // ==================== free camera + ruler ====================

        // GameCamera ships a real detached camera: ToggleFreeFly() flips m_freeFly, and UpdateCamera then
        // stops following the player entirely and hands over to UpdateFreeFly (which is also what the
        // game's own "freefly" console command uses). Both entry points are public, so this needs no
        // Harmony patch and no camera state of our own to restore - turning it back off resumes the
        // normal follow camera on the very next frame.
        private static bool BldFreeCamActive()
        {
            try { return GameCamera.InFreeFly(); }
            catch (Exception) { return false; }
        }

        private void BldToggleFreeCam()
        {
            var cam = GameCamera.instance;
            if (cam == null) { Message(Loc.T("bld.cam_unavailable")); return; }
            var wasOn = BldFreeCamActive();
            try { cam.ToggleFreeFly(); }
            catch (Exception e)
            {
                Logger.LogWarning($"Free camera toggle failed (game updated?): {e.Message}");
                Message(Loc.T("bld.cam_unavailable"));
                return;
            }
            var nowOn = BldFreeCamActive();
            _bldFreeCamOurs = nowOn && !wasOn;
            if (nowOn) BldApplyCamSpeed();
            Message(Loc.T(nowOn ? "bld.msg_cam_on" : "bld.msg_cam_off"));
        }

        // m_freeFlySpeed is private with no setter. Failing to set it is harmless: the camera keeps the
        // game's default speed and the mouse wheel still scales it (vanilla behaviour) - so this is a
        // convenience, never a dependency.
        private static bool _bldCamSpeedResolved;
        private static System.Reflection.FieldInfo _bldCamSpeedField;

        private void BldApplyCamSpeed()
        {
            try
            {
                if (!_bldCamSpeedResolved)
                {
                    _bldCamSpeedResolved = true;
                    _bldCamSpeedField = AccessTools.Field(typeof(GameCamera), "m_freeFlySpeed");
                }
                if (_bldCamSpeedField != null && GameCamera.instance != null)
                    _bldCamSpeedField.SetValue(GameCamera.instance, BldCamSpeed());
            }
            catch (Exception) { /* speed stays at the game's default; wheel still adjusts it */ }
        }

        private float BldCamSpeed()
        {
            var v = BldParseFloat(_bldCamSpeedText, _bldCamSpeedCfg != null ? _bldCamSpeedCfg.Value : 20f);
            v = Mathf.Clamp(v, 1f, 200f);
            if (_bldCamSpeedCfg != null && !Mathf.Approximately(_bldCamSpeedCfg.Value, v)) _bldCamSpeedCfg.Value = v;
            return v;
        }

        // The ruler measures from wherever the admin's viewpoint currently is: the free camera when it is
        // detached, the player otherwise. Numbers only - the panel has no line renderer.
        private void BldSetRuler(bool isA)
        {
            var pos = BldRulerPoint();
            if (!pos.HasValue) { Message(Loc.T("bld.msg_no_player")); return; }
            if (isA) { _bldRulerA = pos.Value; _bldRulerASet = true; Message(Loc.T("bld.msg_ruler_a")); }
            else { _bldRulerB = pos.Value; _bldRulerBSet = true; Message(Loc.T("bld.msg_ruler_b")); }
        }

        private Vector3? BldRulerPoint()
        {
            if (BldFreeCamActive() && GameCamera.instance != null) return GameCamera.instance.transform.position;
            var player = LocalPlayer;
            return player != null ? player.transform.position : (Vector3?)null;
        }

        // ==================== loadout vault ====================

        // Local-only by design: this is the admin saving and re-granting their OWN gear, so it needs no
        // new server support. Restore reuses the panel's existing give path (SendServerGive -> AP_SrvGive
        // -> AP_GiveItem back to us), the same one the Items tab's Bag button uses.
        private void BldSaveLoadout()
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("bld.msg_no_player")); return; }
            var name = BldTrim(_bldLoNameText);
            if (name == null) { Message(Loc.T("bld.msg_lo_need_name")); return; }

            var items = player.GetInventory() != null ? player.GetInventory().GetAllItems() : null;
            if (items == null || items.Count == 0) { Message(Loc.T("bld.msg_lo_empty_inv")); return; }

            var parts = new List<string>();
            foreach (var item in items)
            {
                if (item == null) continue;
                // m_dropPrefab is the only reliable prefab identity on an ItemData; an item without one
                // (a mod item built at runtime) cannot be re-granted by name, so it is skipped.
                var prefab = item.m_dropPrefab != null ? BldPrefabName(item.m_dropPrefab) : null;
                if (string.IsNullOrEmpty(prefab)) continue;
                if (parts.Count >= BldMaxLoadoutEntries) { Message(Loc.T("bld.msg_lo_too_many", BldMaxLoadoutEntries)); return; }
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}",
                    prefab, Mathf.Max(1, item.m_stack), Mathf.Max(1, item.m_quality)));
            }
            if (parts.Count == 0) { Message(Loc.T("bld.msg_lo_empty_inv")); return; }

            BldWriteStoreEntry(_bldLoadoutsCfg, name, string.Join(";", parts.ToArray()));
            Message(Loc.T("bld.msg_lo_saved", parts.Count, name));
        }

        private void BldRestoreLoadout(BldStoreRow row)
        {
            if (LocalPlayer == null) { Message(Loc.T("bld.msg_no_player")); return; }
            var self = SelfUid();
            if (self == 0L) { Message(Loc.T("bld.msg_no_player")); return; }
            if (string.IsNullOrEmpty(row.Payload)) { Message(Loc.T("bld.msg_lo_empty_inv")); return; }

            var given = 0;
            foreach (var entry in row.Payload.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(':');
                if (f.Length < 3) continue;
                int count, quality;
                if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) continue;
                if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out quality)) continue;
                SendServerGive(self, f[0], Mathf.Clamp(count, 1, 9999), Mathf.Clamp(quality, 1, 100));
                given++;
                if (given >= BldMaxLoadoutEntries) break;
            }
            Message(Loc.T("bld.msg_lo_restored", given));
        }

        // ==================== store helpers ====================
        // Both stores are name=payload tables in the plugin config, written through the main file's
        // EncKv/JoinKv so a '|' or '=' inside a blueprint name cannot corrupt the record separators.
        // Payloads only ever use ';' ',' and ':' internally, none of which the escaping touches.

        private Dictionary<string, string> BldBlueprintKv()
        {
            var raw = _bldBlueprintsCfg != null ? _bldBlueprintsCfg.Value ?? "" : "";
            if (_bldBpKv == null || _bldBpRaw != raw) { _bldBpKv = ParseKv(raw); _bldBpRaw = raw; }
            return _bldBpKv;
        }

        private Dictionary<string, string> BldLoadoutKv()
        {
            var raw = _bldLoadoutsCfg != null ? _bldLoadoutsCfg.Value ?? "" : "";
            if (_bldLoKv == null || _bldLoRaw != raw) { _bldLoKv = ParseKv(raw); _bldLoRaw = raw; }
            return _bldLoKv;
        }

        // Rebuilt on the Layout pass only: Capture/Del rewrite the config during the event pass, and a
        // draw loop reading the table live would hand Repaint a different row count than Layout reserved.
        private static List<BldStoreRow> BldRows(Dictionary<string, string> table)
        {
            var rows = new List<BldStoreRow>();
            if (table == null) return rows;
            foreach (var kv in table)
                rows.Add(new BldStoreRow { Name = kv.Key, Payload = kv.Value, Count = BldEntryCount(kv.Value) });
            return rows;
        }

        private static int BldEntryCount(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return 0;
            return payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private void BldWriteStoreEntry(ConfigEntry<string> cfg, string name, string payload)
        {
            if (cfg == null) return;
            var table = ParseKv(cfg.Value ?? "");
            table[name] = payload;
            cfg.Value = JoinKv(table);
        }

        private void BldDeleteStoreEntry(ConfigEntry<string> cfg, string name, string msgKey)
        {
            if (cfg == null) return;
            var table = ParseKv(cfg.Value ?? "");
            if (!table.Remove(name)) return;
            cfg.Value = JoinKv(table);
            Message(Loc.T(msgKey, name));
        }

        // ==================== small helpers ====================

        private static string BldTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static string BldF(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string BldVec(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", v.x, v.y, v.z);

        private static float BldParseFloat(string s, float fallback)
        {
            float v;
            return float.TryParse(BldTrim(s) ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                ? v : fallback;
        }

        // Radius fields share one rule: parse, clamp to 1..64, and write the clamped value back to config
        // so the field persists and can never be talked into a world-sized sweep by a typo.
        private float BldRadius(string text, ConfigEntry<int> cfg)
        {
            var fallback = cfg != null ? cfg.Value : 20;
            var v = Mathf.Clamp(Mathf.RoundToInt(BldParseFloat(text, fallback)), 1, (int)BldMaxRadius);
            if (cfg != null && cfg.Value != v) cfg.Value = v;
            return v;
        }

        private float BldMoveStep()
        {
            var fallback = _bldMoveStepCfg != null ? _bldMoveStepCfg.Value : 0.25f;
            var v = Mathf.Clamp(BldParseFloat(_bldMoveStepText, fallback), 0.05f, 5f);
            if (_bldMoveStepCfg != null && !Mathf.Approximately(_bldMoveStepCfg.Value, v)) _bldMoveStepCfg.Value = v;
            return v;
        }

        // Prefab identity from a live instance: Unity appends "(Clone)" to every instantiated object.
        private static string BldPrefabName(GameObject go)
        {
            if (go == null) return null;
            var n = go.name;
            if (string.IsNullOrEmpty(n)) return null;
            var idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx > 0 ? n.Substring(0, idx) : n;
        }

        // Piece.m_name is a localization token ("$piece_workbench"). Localize it through the game's own
        // table when available, otherwise fall back to the prefab name - never blank.
        private static string BldPieceLabel(Piece piece)
        {
            if (piece == null) return "-";
            try
            {
                var token = piece.m_name;
                if (!string.IsNullOrEmpty(token))
                {
                    var localized = BldLocalize(token);
                    if (!string.IsNullOrEmpty(localized)) return localized;
                }
            }
            catch (Exception) { /* fall through to the prefab name */ }
            return BldPrefabName(piece.gameObject) ?? "-";
        }

        private static string BldLocalize(string token)
        {
            var loc = Localization.instance;
            return loc != null ? loc.Localize(token) : null;
        }

        private static string BldPieceHealth(Piece piece)
        {
            var wnt = piece != null ? piece.GetComponent<WearNTear>() : null;
            if (wnt == null) return Loc.T("bld.health_none");
            var max = wnt.m_health;
            var nview = BldNView(piece);
            var zdo = nview != null ? nview.GetZDO() : null;
            var cur = zdo != null ? zdo.GetFloat(ZDOVars.s_health, max) : max;
            var pct = max > 0f ? Mathf.Clamp01(cur / max) * 100f : 100f;
            return string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0} ({2:0}%)", cur, max, pct);
        }

        // Piece.GetCreator() returns the BUILDER's player id (ZDOVars.s_playerID), which is a different
        // number from the session peer id in ZNet.PlayerInfo - so it can be matched against the local
        // player and nothing else. Shown as a raw id for anyone else rather than guessed at.
        private static string BldPieceOwner(Piece piece)
        {
            try
            {
                var creator = piece.GetCreator();
                if (creator == 0L) return Loc.T("bld.owner_none");
                var me = Player.m_localPlayer;
                if (me != null && me.GetPlayerID() == creator) return Loc.T("bld.owner_you");
                return Loc.T("bld.owner_id", creator);
            }
            catch (Exception) { return Loc.T("bld.owner_none"); }
        }
    }
}

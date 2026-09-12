using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #17 item attribute editor (client side) ====================
    // Two cards. (a) "Give with attributes": the Items tab's give at a chosen quality, durability,
    // variant and crafter stamp, relayed by the server (AP_SrvGiveEx -> AP_GiveItemEx) and built on the
    // TARGET's own client — so that client must run the companion; the server says so when it does not.
    // (b) "Aimed item drop": the same four attributes rewritten on an item lying in the world, entirely
    // client-side the way the Piece Editor edits pieces (ClaimOwnership, edit, write the ZDO).
    //
    // The panel has no "selected item" notion (the Items tab gives straight from its rows), so the
    // prefab field comes with a small picker: type two letters, get up to six matches from the Items
    // index. The match list is pinned on the Layout pass because the filter text changes on the event pass.
    public partial class AdminPanelPlugin
    {
        private const int IattrPickMax = 6;
        private const int IattrMaxCrafter = 40;
        private const float IattrAimRange = 50f;

        private ConfigEntry<bool> _iattrSectionCfg;

        // ---- (a) give: live state ----
        private string _iattrPrefab = "";
        private string _iattrPick = "";
        private string _iattrAmountText = "1";
        private float _iattrQuality = 1f;
        private float _iattrDurability = 100f;
        private float _iattrVariant;
        private string _iattrCrafter = "";
        private long _iattrTargetId;
        private float _iattrNextGive;

        // ---- (a) give: Layout snapshots ----
        private List<ItemEntry> _iattrMatchesLayout;   // <= 6 picker matches, null = none
        private string _iattrPickSeen;                 // filter text the matches above were built from
        private bool _iattrResolvedLayout;             // the typed prefab is an item on this client
        private string _iattrDisplayLayout = "";
        private int _iattrMaxQualityLayout = 1;
        private int _iattrVariantsLayout;

        // ---- (b) aim: live state ----
        private ItemDrop _iattrAim;                    // the drop under the crosshair at the last scan
        private string _iattrAimLabel = "";
        private string _iattrEdStack = "1", _iattrEdQuality = "1", _iattrEdDurability = "100", _iattrEdVariant = "0", _iattrEdCrafter = "";

        // ---- (b) aim: Layout snapshots ----
        private bool _iattrAimLayout;
        private string _iattrAimLabelLayout = "";

        // Reused hit buffer (Wave5_BuildTools.BldHits): the nearest hit is not necessarily the item (a
        // shrub, the terrain in front of it), so every hit is checked and the closest ItemDrop wins.
        private static readonly RaycastHit[] IattrHits = new RaycastHit[48];

        // ---- lifecycle ----

        internal void IattrInit()
        {
            _iattrSectionCfg = Config.Bind("Features", "ShowItemForgeSection", true,
                "Show the Item Forge section in the Tools tab (give an item with a chosen quality, durability, variant and crafter name; edit those on an aimed item drop).");
        }

        internal bool IattrSectionEnabled() => _iattrSectionCfg == null || _iattrSectionCfg.Value;

        internal void IattrReset()
        {
            _iattrPick = "";
            _iattrTargetId = 0L;
            _iattrNextGive = 0f;
            _iattrMatchesLayout = null;
            _iattrPickSeen = null;
            _iattrResolvedLayout = false;
            _iattrDisplayLayout = "";
            _iattrMaxQualityLayout = 1;
            _iattrVariantsLayout = 0;
            _iattrAim = null;
            _iattrAimLabel = "";
            _iattrAimLayout = false;
            _iattrAimLabelLayout = "";
            // Drafts too: the aim-edit buffers were seeded from an item on the server just left, and a
            // crafter name or prefab typed there must not surface in the next world's form.
            _iattrPrefab = "";
            _iattrAmountText = "1";
            _iattrQuality = 1f;
            _iattrDurability = 100f;
            _iattrVariant = 0f;
            _iattrCrafter = "";
            _iattrEdStack = "1"; _iattrEdQuality = "1"; _iattrEdDurability = "100"; _iattrEdVariant = "0"; _iattrEdCrafter = "";
        }

        // LateUpdate slot: an item that was picked up or despawned since the scan is a destroyed Unity
        // object behind a live C# reference. Drop it here so the draw code never meets one mid-frame.
        internal void IattrTick()
        {
            if (!ReferenceEquals(_iattrAim, null) && _iattrAim == null) { _iattrAim = null; _iattrAimLabel = ""; }
        }

        // ---- (a) give ----

        // Up to six Items-index entries whose display or prefab name contains the filter (two letters minimum).
        private List<ItemEntry> IattrMatches(string filter)
        {
            var f = (filter ?? "").Trim();
            if (f.Length < 2 || _itemIndex == null) return null;
            List<ItemEntry> res = null;
            foreach (var e in _itemIndex)
            {
                if (e == null) continue;
                if ((e.Display ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (e.Prefab ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                (res ?? (res = new List<ItemEntry>(IattrPickMax))).Add(e);
                if (res.Count >= IattrPickMax) break;
            }
            return res;
        }

        // Resolve the typed prefab on THIS client (ObjectDB is the same list the server relays against)
        // so the sliders know the real quality/variant ranges. A dictionary lookup per Layout pass.
        private void IattrResolve()
        {
            var name = (_iattrPrefab ?? "").Trim();
            ItemDrop drop = null;
            if (name.Length > 0 && ObjectDB.instance != null)
            {
                var go = ObjectDB.instance.GetItemPrefab(name);
                drop = go != null ? go.GetComponent<ItemDrop>() : null;
            }
            _iattrResolvedLayout = drop != null;
            if (drop == null)
            {
                _iattrDisplayLayout = "";
                _iattrMaxQualityLayout = 1;
                _iattrVariantsLayout = 0;
                return;
            }
            var shared = drop.m_itemData.m_shared;
            _iattrDisplayLayout = LocalizeSafe(shared.m_name, name);
            _iattrMaxQualityLayout = Mathf.Max(1, shared.m_maxQuality);
            _iattrVariantsLayout = shared.m_variants;
        }

        private void IattrGive()
        {
            var prefab = (_iattrPrefab ?? "").Trim();
            if (prefab.Length == 0) { Message(Loc.T("iattr.msg_no_prefab")); return; }
            if (!TkRequireReachable()) return;
            if (Time.time < _iattrNextGive) return;
            _iattrNextGive = Time.time + 1f;
            // 0 = me. The server needs a real uid either way; on a host SelfUid() is the session id the
            // local-dispatch path recognises.
            var target = _iattrTargetId != 0L ? _iattrTargetId : SelfUid();
            if (target == 0L) { Message(Loc.T("common.not_connected_srv")); return; }
            var amount = Mathf.Clamp(TkInt(_iattrAmountText, 1), 1, 100000);
            _iattrAmountText = TkI(amount);
            var crafter = (_iattrCrafter ?? "").Trim();
            if (crafter.Length > IattrMaxCrafter) crafter = crafter.Substring(0, IattrMaxCrafter);

            var pkg = new ZPackage();
            pkg.Write(1);                                                     // payload version
            pkg.Write(target);
            pkg.Write(prefab);
            pkg.Write(amount);
            pkg.Write(Mathf.Clamp(Mathf.RoundToInt(_iattrQuality), 1, 10));
            pkg.Write(Mathf.Clamp(Mathf.RoundToInt(_iattrDurability), 0, 100));
            pkg.Write(Mathf.Clamp(Mathf.RoundToInt(_iattrVariant), 0, 31));
            pkg.Write(crafter);
            SrvRpc("AP_SrvGiveEx", pkg);
            Message(Loc.T("iattr.msg_sent", amount, prefab, TargetLabel(ref _iattrTargetId)));
        }

        // ---- (b) aimed item drop ----

        private static string IattrPrefabOf(ItemDrop drop)
        {
            if (drop == null) return "";
            if (drop.m_itemData != null && drop.m_itemData.m_dropPrefab != null) return drop.m_itemData.m_dropPrefab.name;
            var n = drop.gameObject.name ?? "";
            var i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i) : n;
        }

        private void IattrScanAim()
        {
            var cam = GameCamera.instance;
            if (cam == null || LocalPlayer == null) { Message(Loc.T("iattr.msg_no_aim")); return; }
            var n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, IattrHits, IattrAimRange,
                ~0, QueryTriggerInteraction.Ignore);
            ItemDrop best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var col = IattrHits[i].collider;
                if (col == null) continue;
                var drop = col.GetComponentInParent<ItemDrop>();
                if (drop == null || IattrHits[i].distance >= bestDist) continue;
                bestDist = IattrHits[i].distance;
                best = drop;
            }
            if (best == null)
            {
                _iattrAim = null;
                _iattrAimLabel = "";
                Message(Loc.T("iattr.msg_no_aim"));
                return;
            }
            _iattrAim = best;
            var d = best.m_itemData;
            var label = LocalizeSafe(d.m_shared.m_name, IattrPrefabOf(best)) + " (" + IattrPrefabOf(best) + ")";
            _iattrAimLabel = label;
            // Prefill the editors with what the drop holds now, durability as a percentage of its max.
            var max = d.GetMaxDurability();
            _iattrEdStack = TkI(d.m_stack);
            _iattrEdQuality = TkI(d.m_quality);
            _iattrEdDurability = TkF(max > 0f ? Mathf.Clamp(d.m_durability / max * 100f, 0f, 100f) : 100f);
            _iattrEdVariant = TkI(d.m_variant);
            _iattrEdCrafter = d.m_crafterName ?? "";
            Message(Loc.T("iattr.msg_aimed", label));
        }

        // Claim first: a ZDO written by a non-owner is thrown away on the next sync (the Piece Editor
        // rule). ItemDrop.Save() is private, so the public ItemDrop.SaveToZDO writes the same fields;
        // the drop's own Load() then re-reads our revision instead of restoring the old values.
        private void IattrApplyAim()
        {
            var aim = _iattrAim;
            if (aim == null) { Message(Loc.T("iattr.msg_no_aim")); return; }
            var nview = aim.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                _iattrAim = null;
                _iattrAimLabel = "";
                Message(Loc.T("iattr.msg_aim_gone"));
                return;
            }
            nview.ClaimOwnership();
            var d = aim.m_itemData;
            var shared = d.m_shared;
            d.m_stack = Mathf.Clamp(TkInt(_iattrEdStack, d.m_stack), 1, Mathf.Max(1, shared.m_maxStackSize));
            d.m_quality = Mathf.Clamp(TkInt(_iattrEdQuality, d.m_quality), 1, Mathf.Max(1, shared.m_maxQuality));
            // Quality first: GetMaxDurability() scales with m_quality.
            var pct = Mathf.Clamp(TkFloat(_iattrEdDurability, 100f), 0f, 100f);
            d.m_durability = d.GetMaxDurability() * pct / 100f;
            d.m_variant = shared.m_variants > 1 ? Mathf.Clamp(TkInt(_iattrEdVariant, 0), 0, shared.m_variants - 1) : 0;
            var crafter = (_iattrEdCrafter ?? "").Trim();
            if (crafter.Length > IattrMaxCrafter) crafter = crafter.Substring(0, IattrMaxCrafter);
            if (crafter.Length > 0) { d.m_crafterID = 1L; d.m_crafterName = crafter; }
            else { d.m_crafterID = 0L; d.m_crafterName = ""; }

            var zdo = nview.GetZDO();
            if (zdo == null) { Message(Loc.T("iattr.msg_aim_gone")); return; }
            ItemDrop.SaveToZDO(d, zdo);
            try { aim.SetQuality(d.m_quality); }   // rescales the world model for scale-by-quality items
            catch (Exception) { /* cosmetic only */ }

            // Echo what was actually stored (clamps may have moved a value).
            _iattrEdStack = TkI(d.m_stack);
            _iattrEdQuality = TkI(d.m_quality);
            _iattrEdVariant = TkI(d.m_variant);
            _iattrEdCrafter = d.m_crafterName ?? "";
            Message(Loc.T("iattr.msg_applied", _iattrAimLabel));
        }

        // ---- draw ----

        internal void DrawItemForgeSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                // The index walk costs two IndexOf per item; only redo it when the filter text changed
                // (the Items tab debounces its identical scan for the same reason).
                if (!string.Equals(_iattrPick, _iattrPickSeen, StringComparison.Ordinal))
                {
                    _iattrPickSeen = _iattrPick;
                    _iattrMatchesLayout = IattrMatches(_iattrPick);
                }
                IattrResolve();
                // Unity's overloaded null: a picked-up / despawned drop reads as gone. Pinned here so the
                // aim card's label text is the same on both passes.
                _iattrAimLayout = _iattrAim != null;
                _iattrAimLabelLayout = _iattrAimLayout ? _iattrAimLabel : "";
            }

            // ---- (a) give with attributes ----
            BeginCard(Loc.T("iattr.section"));
            GUILayout.Label(Loc.T("iattr.hint"), _hintStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.prefab"), _labelStyle, GUILayout.MinWidth(90));
            _iattrPrefab = GUILayout.TextField(_iattrPrefab ?? "", 64, _textFieldStyle, GUILayout.MinWidth(200));
            // One label either way: the text swaps between the resolved display name and "unknown".
            GUILayout.Label(_iattrResolvedLayout ? _iattrDisplayLayout : Loc.T("iattr.prefab_unknown"),
                _iattrResolvedLayout ? _headerStyle : _dimLabelStyle, GUILayout.MinWidth(120));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.pick"), _labelStyle, GUILayout.MinWidth(90));
            _iattrPick = GUILayout.TextField(_iattrPick ?? "", 40, _textFieldStyle, GUILayout.MinWidth(200));
            GUILayout.Label(Loc.T("iattr.pick_hint"), _dimLabelStyle);
            GUILayout.EndHorizontal();
            var matches = _iattrMatchesLayout;
            if (matches != null && matches.Count > 0)
            {
                GUILayout.BeginHorizontal();
                for (var i = 0; i < matches.Count; i++)
                    if (GUILayout.Button(matches[i].Display ?? matches[i].Prefab, _buttonStyle, GUILayout.MinWidth(90)))
                        _iattrPrefab = matches[i].Prefab;
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.amount"), _labelStyle, GUILayout.MinWidth(90));
            _iattrAmountText = GUILayout.TextField(_iattrAmountText ?? "", 6, _textFieldStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var maxQ = _iattrMaxQualityLayout;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.quality"), _labelStyle, GUILayout.MinWidth(90));
            _iattrQuality = GUILayout.HorizontalSlider(Mathf.Clamp(_iattrQuality, 1f, maxQ), 1f, maxQ, GUILayout.Width(200));
            GUILayout.Label(Loc.T("iattr.quality_value", Mathf.RoundToInt(_iattrQuality), maxQ), _dimLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.durability"), _labelStyle, GUILayout.MinWidth(90));
            _iattrDurability = GUILayout.HorizontalSlider(_iattrDurability, 0f, 100f, GUILayout.Width(200));
            GUILayout.Label(Loc.T("iattr.percent", Mathf.RoundToInt(_iattrDurability)), _dimLabelStyle);
            GUILayout.EndHorizontal();

            var variants = _iattrVariantsLayout;
            var maxVar = Mathf.Max(0, variants - 1);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.variant"), _labelStyle, GUILayout.MinWidth(90));
            _iattrVariant = GUILayout.HorizontalSlider(Mathf.Clamp(_iattrVariant, 0f, maxVar), 0f, maxVar, GUILayout.Width(200));
            GUILayout.Label(variants > 1
                    ? Loc.T("iattr.variant_value", Mathf.RoundToInt(_iattrVariant), maxVar)
                    : Loc.T("iattr.no_variants"), _dimLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.crafter"), _labelStyle, GUILayout.MinWidth(90));
            _iattrCrafter = GUILayout.TextField(_iattrCrafter ?? "", IattrMaxCrafter, _textFieldStyle, GUILayout.MinWidth(200));
            GUILayout.Label(Loc.T("iattr.crafter_hint"), _dimLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.target"), _labelStyle, GUILayout.MinWidth(90));
            GUILayout.Label(TargetLabel(ref _iattrTargetId), _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("iattr.cycle"), _buttonStyle, GUILayout.MinWidth(110))) CycleTarget(ref _iattrTargetId);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("iattr.give"), _buttonStyle, GUILayout.MinWidth(140))) IattrGive();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("iattr.give_hint"), _hintStyle);
            EndCard();

            // ---- (b) aimed item drop ----
            BeginCard(Loc.T("iattr.aim_section"));
            GUILayout.Label(Loc.T("iattr.aim_hint"), _hintStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("iattr.scan"), _buttonStyle, GUILayout.MinWidth(110))) IattrScanAim();
            GUILayout.Label(_iattrAimLayout ? _iattrAimLabelLayout : Loc.T("iattr.aim_none"),
                _iattrAimLayout ? _headerStyle : _dimLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.stack"), _labelStyle, GUILayout.MinWidth(90));
            _iattrEdStack = GUILayout.TextField(_iattrEdStack ?? "", 4, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("iattr.quality"), _labelStyle, GUILayout.MinWidth(70));
            _iattrEdQuality = GUILayout.TextField(_iattrEdQuality ?? "", 2, _textFieldStyle, GUILayout.Width(40));
            GUILayout.Label(Loc.T("iattr.durability_pct"), _labelStyle, GUILayout.MinWidth(90));
            _iattrEdDurability = GUILayout.TextField(_iattrEdDurability ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("iattr.variant"), _labelStyle, GUILayout.MinWidth(60));
            _iattrEdVariant = GUILayout.TextField(_iattrEdVariant ?? "", 2, _textFieldStyle, GUILayout.Width(40));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("iattr.crafter"), _labelStyle, GUILayout.MinWidth(90));
            _iattrEdCrafter = GUILayout.TextField(_iattrEdCrafter ?? "", IattrMaxCrafter, _textFieldStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("iattr.apply"), _buttonStyle, GUILayout.MinWidth(100))) IattrApplyAim();
            if (GUILayout.Button(Loc.T("iattr.forget"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                _iattrAim = null;
                _iattrAimLabel = "";
            }
            GUILayout.EndHorizontal();
            EndCard();
        }
    }
}

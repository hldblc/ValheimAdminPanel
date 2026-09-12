using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #11 creature stat editor (client side) ====================
    // Aim the camera at a creature (never a player), read its name / prefab / level / health / tame state /
    // faction, and edit it in place: current and max health, star level, pet name, tame or un-tame, an
    // invulnerable flag, and despawn. Every edit is CLIENT-SIDE, exactly the way the Piece Editor edits
    // pieces: nview.ClaimOwnership() first (a ZDO written by a non-owner is thrown away on the next sync,
    // so without the claim the edit appears to work for one frame and then reverts), then the Character
    // setters, which write the ZDO themselves. One audit line per apply goes to the server through
    // AP_SrvCreatureEditAudit so the trail records it; the server does nothing else with it.
    //
    // INVULNERABLE: the engine has no persisted "invulnerable" flag for creatures (ZDOVars has none, and
    // Character.RPC_Damage checks nothing of the kind), so it is a client-side Harmony prefix on
    // Character.Damage (the attacker side: Character.cs:1883 forwards the hit to the owner) and on
    // Character.RPC_Damage (the owner side, where the hit is applied), both gated on a set of ZDOIDs. The
    // flag therefore holds for hits this client deals and for hits applied while THIS client simulates the
    // creature (it claims ownership when the flag is set; the creature stays ours while we are near it).
    // Another player's client hitting a creature it owns itself is not covered - the hint says so.
    //
    // IMGUI discipline: CeditTick() runs in LateUpdate and rewrites the aimed target at any time, so the
    // draw code reads ONLY the *Layout snapshots pinned at the top of DrawCreatureEditorSection.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _ceditSectionCfg;
        private bool _ceditInited;

        // ---- live state (tick + clicks write; the draw never reads these directly) ----
        private bool _ceditTargeting;
        private Character _ceditTarget;
        private ZDOID _ceditTargetId;
        private string _ceditName = "", _ceditPrefab = "", _ceditLevel = "", _ceditHealth = "", _ceditFaction = "";
        private bool _ceditTamed;

        // ---- edit fields (text only; seeded when the aimed creature changes) ----
        private string _ceditHealthText = "";
        private string _ceditMaxText = "";
        private int _ceditStars;
        private string _ceditNameText = "";

        // ---- Layout snapshots ----
        private bool _ceditTargetingLayout;
        private Character _ceditTargetLayout;
        private bool _ceditHasTargetLayout;
        private string _ceditNameLayout = "", _ceditPrefabLayout = "", _ceditLevelLayout = "", _ceditHealthLayout = "", _ceditFactionLayout = "";
        private bool _ceditTamedLayout;
        private bool _ceditInvulnLayout;
        private int _ceditStarsLayout;

        // ZDOIDs currently flagged invulnerable on this client. Static because the Harmony prefixes are
        // static; cleared on every world reset.
        private static readonly HashSet<ZDOID> CeditInvulnerable = new HashSet<ZDOID>();
        private static bool _ceditDamagePatchOk;
        private static bool _ceditRpcDamagePatchOk;

        private static readonly Func<Character, bool> CeditAccept = c => c != null && !c.IsPlayer();

        // ---- lifecycle ----

        internal void CeditInit()
        {
            _ceditSectionCfg = Config.Bind("Features", "ShowCreatureEditorSection", true,
                "Show the Creature Editor section in the Tools tab (aim at a creature: edit health, max health, stars, name, tame state, invulnerable, despawn).");
            _ceditInited = true;
        }

        // Applied from the glue, one class per target method, each in its own try/catch: a failure costs
        // the invulnerable toggle only, and the section says so instead of pretending.
        internal void CeditApplyPatches()
        {
            try { Harmony.CreateAndPatchAll(typeof(Wave8ObjectsDamagePatch)); _ceditDamagePatchOk = true; }
            catch (Exception e) { Logger.LogWarning($"Creature editor: Character.Damage patch failed (invulnerable flag unavailable): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(Wave8ObjectsRpcDamagePatch)); _ceditRpcDamagePatchOk = true; }
            catch (Exception e) { Logger.LogWarning($"Creature editor: Character.RPC_Damage patch failed (invulnerable flag unavailable): {e.Message}"); }
        }

        internal bool CeditSectionEnabled() => _ceditSectionCfg == null || _ceditSectionCfg.Value;

        internal void CeditReset()
        {
            _ceditTargeting = false;
            _ceditTarget = null;
            _ceditTargetId = ZDOID.None;
            _ceditName = _ceditPrefab = _ceditLevel = _ceditHealth = _ceditFaction = "";
            _ceditTamed = false;
            _ceditHealthText = _ceditMaxText = _ceditNameText = "";
            _ceditStars = 0;
            _ceditTargetingLayout = false;
            _ceditTargetLayout = null;
            _ceditHasTargetLayout = false;
            _ceditNameLayout = _ceditPrefabLayout = _ceditLevelLayout = _ceditHealthLayout = _ceditFactionLayout = "";
            _ceditTamedLayout = false;
            _ceditInvulnLayout = false;
            _ceditStarsLayout = 0;
            CeditInvulnerable.Clear();   // flags belong to the world just left
        }

        private static bool CeditInvulnAvailable => _ceditDamagePatchOk && _ceditRpcDamagePatchOk;

        // ---- invulnerable patches ----

        private static bool CeditIsInvulnerable(Character c)
        {
            if (c == null || CeditInvulnerable.Count == 0) return false;
            try { return CeditInvulnerable.Contains(c.GetZDOID()); }
            catch (Exception) { return false; }
        }

        [HarmonyPatch(typeof(Character), "Damage")]
        private static class Wave8ObjectsDamagePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Character __instance) => !CeditIsInvulnerable(__instance);
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Wave8ObjectsRpcDamagePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Character __instance) => !CeditIsInvulnerable(__instance);
        }

        // ---- tick (LateUpdate) ----

        internal void CeditTick()
        {
            if (!_ceditInited) return;
            if (!FeaturesEnabled() || !CeditSectionEnabled()) { _ceditTarget = null; return; }
            try
            {
                if (LocalPlayer == null || !_ceditTargeting) { _ceditTarget = null; return; }
                var c = ObjAim<Character>(CeditAccept);
                if (!ReferenceEquals(c, _ceditTarget))
                {
                    _ceditTarget = c;
                    CeditSeed(c);
                }
                else if (c != null) CeditRefresh(c);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Creature editor tick failed (feature degraded, panel unaffected): {e.Message}");
            }
        }

        private static ZNetView CeditView(Character c)
        {
            if (c == null) return null;
            var nview = c.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() ? nview : null;
        }

        private static bool CeditUsable(Character c) => c != null && CeditView(c) != null;

        // New target: refresh the read-out AND seed the edit fields (only here, never per tick, or typing
        // would be clobbered every frame).
        private void CeditSeed(Character c)
        {
            if (c == null)
            {
                _ceditTargetId = ZDOID.None;
                _ceditName = _ceditPrefab = _ceditLevel = _ceditHealth = _ceditFaction = "";
                _ceditTamed = false;
                return;
            }
            CeditRefresh(c);
            try
            {
                _ceditHealthText = c.GetHealth().ToString("0", CultureInfo.InvariantCulture);
                _ceditMaxText = c.GetMaxHealth().ToString("0", CultureInfo.InvariantCulture);
                _ceditStars = Mathf.Clamp(c.GetLevel() - 1, 0, 2);
                var tame = c.GetComponent<Tameable>();
                _ceditNameText = _ceditTamed && tame != null ? (tame.GetText() ?? "") : "";
            }
            catch (Exception) { }
        }

        private void CeditRefresh(Character c)
        {
            try
            {
                var nview = CeditView(c);
                _ceditTargetId = nview != null ? nview.GetZDO().m_uid : ZDOID.None;
                _ceditName = c.GetHoverName() ?? "";
                _ceditPrefab = ObjPrefabName(c.gameObject);
                var level = c.GetLevel();
                _ceditLevel = Loc.T("cedit.level_line", level, ObjStars(level));
                _ceditHealth = ObjHealth(c.GetHealth(), c.GetMaxHealth());
                _ceditTamed = c.IsTamed();
                _ceditFaction = c.IsBoss() ? Loc.T("cedit.faction_boss", c.GetFaction().ToString()) : c.GetFaction().ToString();
            }
            catch (Exception) { }
        }

        // ---- mutations (claim first, always) ----

        private ZNetView CeditClaim(Character c)
        {
            var nview = CeditView(c);
            if (nview == null) { Message(Loc.T("cedit.msg_no_target")); return null; }
            nview.ClaimOwnership();
            return nview;
        }

        // One line per apply; the server writes it to the audit trail and does nothing else with it.
        private void CeditAudit(string what, string value)
        {
            try
            {
                var s = ServerUid();
                if (s == 0L && !(ZNet.instance != null && ZNet.instance.IsServer())) return;   // no companion to tell
                if (ZRoutedRpc.instance == null) return;
                var line = $"{what}|{_ceditPrefab}|{_ceditTargetId}|{value}";
                if (line.Length > 200) line = line.Substring(0, 200);
                ZRoutedRpc.instance.InvokeRoutedRPC(s, "AP_SrvCreatureEditAudit", line);
            }
            catch (Exception) { /* the audit line is best-effort; the edit itself already happened */ }
        }

        private void CeditApplyHealth(Character c)
        {
            var nview = CeditClaim(c);
            if (nview == null) return;
            var max = c.GetMaxHealth();
            var v = Mathf.Clamp(ObjParseFloat(_ceditHealthText, c.GetHealth()), 1f, Mathf.Max(1f, max));
            c.SetHealth(v);
            CeditRefresh(c);
            CeditAudit("health", v.ToString("0", CultureInfo.InvariantCulture));
            Message(Loc.T("cedit.msg_health", _ceditName, v.ToString("0", CultureInfo.InvariantCulture)));
        }

        private void CeditApplyMax(Character c)
        {
            var nview = CeditClaim(c);
            if (nview == null) return;
            var v = Mathf.Clamp(ObjParseFloat(_ceditMaxText, c.GetMaxHealth()), 1f, 1000000f);
            c.SetMaxHealth(v);
            CeditRefresh(c);
            _ceditHealthText = c.GetHealth().ToString("0", CultureInfo.InvariantCulture);
            CeditAudit("maxhealth", v.ToString("0", CultureInfo.InvariantCulture));
            Message(Loc.T("cedit.msg_max", _ceditName, v.ToString("0", CultureInfo.InvariantCulture)));
        }

        // SetLevel re-runs SetupMaxHealth (base * level), so the max-health field is re-seeded afterwards.
        private void CeditApplyStars(Character c)
        {
            var nview = CeditClaim(c);
            if (nview == null) return;
            var level = Mathf.Clamp(_ceditStars, 0, 2) + 1;
            c.SetLevel(level);
            CeditRefresh(c);
            _ceditMaxText = c.GetMaxHealth().ToString("0", CultureInfo.InvariantCulture);
            _ceditHealthText = c.GetHealth().ToString("0", CultureInfo.InvariantCulture);
            CeditAudit("level", level.ToString(CultureInfo.InvariantCulture));
            Message(Loc.T("cedit.msg_stars", _ceditName, ObjStars(level)));
        }

        // Tameable.SetText routes "SetName" to the owner - which is us after the claim - and stamps the
        // author id the same way the game's own rename dialog does.
        private void CeditApplyName(Character c)
        {
            if (!c.IsTamed()) { Message(Loc.T("cedit.msg_name_wild")); return; }
            var tame = c.GetComponent<Tameable>();
            if (tame == null) { Message(Loc.T("cedit.msg_name_no_tameable")); return; }
            var name = (_ceditNameText ?? "").Trim();
            if (name.Length == 0) { Message(Loc.T("cedit.msg_name_empty")); return; }
            if (name.Length > 40) name = name.Substring(0, 40);
            var nview = CeditClaim(c);
            if (nview == null) return;
            tame.SetText(name);
            CeditRefresh(c);
            CeditAudit("name", name);
            Message(Loc.T("cedit.msg_name", name));
        }

        // MonsterAI.MakeTame is what the game calls when taming completes (SetTamed + un-alert + drop
        // targets); plain SetTamed is the fallback for creatures without a MonsterAI.
        private void CeditToggleTame(Character c)
        {
            var nview = CeditClaim(c);
            if (nview == null) return;
            var tamed = c.IsTamed();
            if (tamed) c.SetTamed(false);
            else
            {
                var ai = c.GetComponent<MonsterAI>();
                if (ai != null) ai.MakeTame(); else c.SetTamed(true);
            }
            CeditRefresh(c);
            CeditAudit(tamed ? "untame" : "tame", "");
            Message(Loc.T(tamed ? "cedit.msg_untamed" : "cedit.msg_tamed", _ceditName));
        }

        private void CeditSetInvulnerable(Character c, bool on)
        {
            if (!CeditInvulnAvailable) { Message(Loc.T("cedit.msg_invuln_unavailable")); return; }
            var nview = CeditClaim(c);
            if (nview == null) return;
            var id = nview.GetZDO().m_uid;
            if (on) CeditInvulnerable.Add(id); else CeditInvulnerable.Remove(id);
            CeditAudit(on ? "invulnerable_on" : "invulnerable_off", "");
            Message(Loc.T(on ? "cedit.msg_invuln_on" : "cedit.msg_invuln_off", _ceditName));
        }

        private void CeditDespawn(Character c)
        {
            var nview = CeditClaim(c);
            if (nview == null) return;
            var label = _ceditName;
            CeditInvulnerable.Remove(nview.GetZDO().m_uid);
            CeditAudit("despawn", "");
            nview.Destroy();
            _ceditTarget = null;
            Message(Loc.T("cedit.msg_despawned", label));
        }

        // ---- draw ----

        internal void DrawCreatureEditorSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _ceditTargetingLayout = _ceditTargeting;
                _ceditTargetLayout = CeditUsable(_ceditTarget) ? _ceditTarget : null;
                _ceditHasTargetLayout = _ceditTargetLayout != null;
                _ceditNameLayout = _ceditName ?? "";
                _ceditPrefabLayout = _ceditPrefab ?? "";
                _ceditLevelLayout = _ceditLevel ?? "";
                _ceditHealthLayout = _ceditHealth ?? "";
                _ceditFactionLayout = _ceditFaction ?? "";
                _ceditTamedLayout = _ceditTamed;
                _ceditInvulnLayout = _ceditHasTargetLayout && CeditInvulnerable.Contains(_ceditTargetId);
                _ceditStarsLayout = _ceditStars;
            }
            var t = _ceditTargetLayout;
            var has = _ceditHasTargetLayout;

            BeginCard(Loc.T("cedit.section"));

            GUILayout.BeginHorizontal();
            var targeting = GUILayout.Toggle(_ceditTargetingLayout, " " + Loc.T("cedit.targeting"), _toggleStyle);
            if (targeting != _ceditTargetingLayout) _ceditTargeting = targeting;   // live field flips; snapshot lands next frame
            GUILayout.FlexibleSpace();
            GUILayout.Label(Loc.T("cedit.range", ObjTargetRange().ToString("0", CultureInfo.InvariantCulture)), _dimLabelStyle);
            GUILayout.EndHorizontal();

            // Read-out: plain labels either way, a dash placeholder is still one control.
            CeditInfoRow(Loc.T("cedit.target_name"), has ? _ceditNameLayout : "-");
            CeditInfoRow(Loc.T("cedit.target_prefab"), has ? _ceditPrefabLayout : "-");
            CeditInfoRow(Loc.T("cedit.target_level"), has ? _ceditLevelLayout : "-");
            CeditInfoRow(Loc.T("cedit.target_health"), has ? _ceditHealthLayout : "-");
            CeditInfoRow(Loc.T("cedit.target_tamed"), has ? Loc.T(_ceditTamedLayout ? "cedit.yes" : "cedit.no") : "-");
            CeditInfoRow(Loc.T("cedit.target_faction"), has ? _ceditFactionLayout : "-");
            GUILayout.Label(Loc.T(_ceditTargetingLayout ? (has ? "cedit.hint_aimed" : "cedit.hint_nothing") : "cedit.hint_off"), _hintStyle);

            DrawSection(Loc.T("cedit.edit_title"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("cedit.health"), _labelStyle, GUILayout.MinWidth(90));
            _ceditHealthText = GUILayout.TextField(_ceditHealthText ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            if (GUILayout.Button(Loc.T("cedit.apply"), _buttonStyle, GUILayout.MinWidth(70)))
            { if (t != null) CeditApplyHealth(t); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.Space(12);
            GUILayout.Label(Loc.T("cedit.max_health"), _labelStyle, GUILayout.MinWidth(90));
            _ceditMaxText = GUILayout.TextField(_ceditMaxText ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            if (GUILayout.Button(Loc.T("cedit.apply"), _buttonStyle, GUILayout.MinWidth(70)))
            { if (t != null) CeditApplyMax(t); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("cedit.stars"), _labelStyle, GUILayout.MinWidth(90));
            for (var k = 0; k <= 2; k++)
            {
                // Chips: act only on an off->on FLIP (the selected chip reports true on every pass).
                var wasOn = _ceditStarsLayout == k;
                var on = GUILayout.Toggle(wasOn, k == 0 ? "0" : ObjStars(k + 1), _chipStyleOrButton(), GUILayout.Width(44));
                if (on && !wasOn) _ceditStars = k;
            }
            if (GUILayout.Button(Loc.T("cedit.apply"), _buttonStyle, GUILayout.MinWidth(70)))
            { if (t != null) CeditApplyStars(t); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("cedit.name"), _labelStyle, GUILayout.MinWidth(90));
            _ceditNameText = GUILayout.TextField(_ceditNameText ?? "", 40, _textFieldStyle, GUILayout.MinWidth(160));
            if (GUILayout.Button(Loc.T("cedit.apply"), _buttonStyle, GUILayout.MinWidth(70)))
            { if (t != null) CeditApplyName(t); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            // Tame/untame is one button whose TEXT swaps on the pinned tame state.
            if (GUILayout.Button(Loc.T(_ceditTamedLayout ? "cedit.untame" : "cedit.tame"), _buttonStyle, GUILayout.MinWidth(100)))
            { if (t != null) CeditToggleTame(t); else Message(Loc.T("cedit.msg_no_target")); }
            var wasInvuln = _ceditInvulnLayout;
            var invuln = GUILayout.Toggle(wasInvuln, " " + Loc.T("cedit.invulnerable"), _toggleStyle);
            if (invuln != wasInvuln)
            { if (t != null) CeditSetInvulnerable(t, invuln); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.FlexibleSpace();
            if (ConfirmButton("CeditDespawn", Loc.T("cedit.despawn"), GUILayout.MinWidth(100)))
            { if (t != null) CeditDespawn(t); else Message(Loc.T("cedit.msg_no_target")); }
            GUILayout.EndHorizontal();

            // Two hints, both always emitted: the invulnerable caveat (text swaps on patch availability)
            // and the general "claims ownership" note.
            GUILayout.Label(Loc.T(CeditInvulnAvailable ? "cedit.hint_invuln" : "cedit.hint_invuln_unavailable"), _hintStyle);
            GUILayout.Label(Loc.T("cedit.hint"), _hintStyle);

            EndCard();
        }

        private void CeditInfoRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.MinWidth(90));
            GUILayout.Label(value ?? "-", _cellStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}

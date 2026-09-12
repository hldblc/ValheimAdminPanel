using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — Server-enforced skill gain rate and cap (client side, #20) ====================
    // A gain multiplier, an optional level cap and per-skill overrides, set once on the server (they are
    // companion config entries, so they survive a restart) and applied by the companion on modded clients
    // through a Skills.RaiseSkill patch. Unmodded clients keep vanilla gain — the hint says so. Writing the
    // rules is owner-only (AP_SrvSkillRulesSet); reading them is open to every admin.
    //
    // IMGUI discipline: the server values are pinned per frame; the edit fields are the admin's own text and
    // are seeded from the first reply only, so a reply can never overwrite a half-typed edit.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _skrSectionCfg;

        // ---- live payload (server truth) ----
        private bool _skrHave;
        private bool _skrEnabled;
        private float _skrMult = 1f;
        private int _skrCap;
        private string _skrOverrides = "";
        private bool _skrPending;
        private bool _skrRequested;
        private bool _skrSeeded;      // edit fields filled from the first reply
        private float _skrNextReq;

        // ---- UI state ----
        private bool _skrEnableEdit;
        private string _skrMultText = "1";
        private string _skrCapText = "0";
        private string _skrOverridesText = "";

        // ---- Layout snapshots ----
        private bool _skrHaveLayout;
        private bool _skrPendingLayout;
        private bool _skrEnabledLayout;
        private float _skrMultLayout = 1f;
        private int _skrCapLayout;
        private string _skrOverridesLayout = "";

        private const int SkrOverrideCap = 30;   // wire contract with the companion

        // ---- lifecycle ----

        internal void SkrInit()
        {
            _skrSectionCfg = Config.Bind("Features", "ShowSkillRulesSection", true,
                "Show the Skill Rules section in the Tools tab (server-wide skill gain multiplier, level cap and per-skill overrides).");
        }

        internal bool SkrSectionEnabled() => _skrSectionCfg == null || _skrSectionCfg.Value;

        internal void SkrReset()
        {
            _skrHave = false;
            _skrEnabled = false;
            _skrMult = 1f;
            _skrCap = 0;
            _skrOverrides = "";
            _skrPending = false;
            _skrRequested = false;
            _skrSeeded = false;
            _skrNextReq = 0f;
            _skrEnableEdit = false;
            _skrMultText = "1";
            _skrCapText = "0";
            _skrOverridesText = "";
            _skrHaveLayout = false;
            _skrPendingLayout = false;
            _skrEnabledLayout = false;
            _skrMultLayout = 1f;
            _skrCapLayout = 0;
            _skrOverridesLayout = "";
        }

        // ---- reply ----

        private static void SkrOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseSkillRulesData(self, pkg);
        }

        // AP_SkillRulesData: {int ver, bool enabled, float mult, int cap, string overrides, int n(<=30),
        //                     n x (int skillType, float mult)}  — the parsed pairs are informational here;
        //                     the canonical text is what the card shows and edits.
        internal static void ParseSkillRulesData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != RulesVer) return;
                var enabled = pkg.ReadBool();
                var mult = pkg.ReadSingle();
                var cap = pkg.ReadInt();
                var overrides = pkg.ReadString() ?? "";
                var n = pkg.ReadInt();
                if (n < 0 || n > SkrOverrideCap) return;
                for (var i = 0; i < n; i++) { pkg.ReadInt(); pkg.ReadSingle(); }
                if (float.IsNaN(mult) || float.IsInfinity(mult)) mult = 1f;

                self._skrEnabled = enabled;
                self._skrMult = mult;
                self._skrCap = cap;
                self._skrOverrides = overrides;
                self._skrHave = true;
                self._skrPending = false;
                if (!self._skrSeeded)
                {
                    self._skrSeeded = true;
                    self._skrEnableEdit = enabled;
                    self._skrMultText = RulesF(mult);
                    self._skrCapText = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    self._skrOverridesText = overrides;
                }
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        private void SkrRequest()
        {
            if (!RulesReachable(false)) return;
            if (Time.time < _skrNextReq) return;
            _skrNextReq = Time.time + 2f;
            _skrPending = true;
            SrvRpc("AP_SrvSkillRulesReq");
        }

        // AP_SrvSkillRulesSet: {int ver, bool enabled, float mult, int cap, string overrides}
        private void SkrApply()
        {
            if (!RulesReachable(true)) return;
            float mult;
            int cap;
            if (!RulesParseFloat(_skrMultText, out mult) || mult < 0.1f || mult > 10f) { Message(Loc.T("skillr.msg_bad_mult")); return; }
            if (!RulesParseInt(_skrCapText, out cap) || cap < 0 || cap > 100) { Message(Loc.T("skillr.msg_bad_cap")); return; }
            var overrides = (_skrOverridesText ?? "").Trim();
            if (overrides.Length > 240) { Message(Loc.T("skillr.msg_overrides_long")); return; }

            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(_skrEnableEdit);
            pkg.Write(mult);
            pkg.Write(cap);
            pkg.Write(overrides);
            SrvRpc("AP_SrvSkillRulesSet", pkg);
            _skrPending = true;
            Message(Loc.T("skillr.msg_sent"));
        }

        // ---- draw ----

        internal void DrawSkillRulesSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _skrHaveLayout = _skrHave;
                _skrPendingLayout = _skrPending;
                _skrEnabledLayout = _skrEnabled;
                _skrMultLayout = _skrMult;
                _skrCapLayout = _skrCap;
                _skrOverridesLayout = _skrOverrides ?? "";
                if (!_skrRequested && RulesReachable(false)) { _skrRequested = true; SkrRequest(); }   // see MapPins: only once reachable
            }

            BeginCard(Loc.T("skillr.section"));

            // Server truth: exactly one status label and one summary label, text only.
            GUILayout.Label(Loc.T(!_skrHaveLayout ? "skillr.status_unknown" : _skrEnabledLayout ? "skillr.status_on" : "skillr.status_off"),
                _skrHaveLayout && _skrEnabledLayout ? _headerStyle : _dimLabelStyle);
            GUILayout.Label(_skrHaveLayout
                    ? Loc.T("skillr.cur_line", RulesF(_skrMultLayout),
                        _skrCapLayout > 0 ? _skrCapLayout.ToString(System.Globalization.CultureInfo.InvariantCulture) : Loc.T("skillr.no_cap"),
                        _skrOverridesLayout.Length > 0 ? _skrOverridesLayout : Loc.T("skillr.no_overrides"))
                    : Loc.T(_skrPendingLayout ? "skillr.pending" : "skillr.idle"),
                _hintStyle);

            // Edit form.
            GUILayout.BeginHorizontal();
            _skrEnableEdit = GUILayout.Toggle(_skrEnableEdit, " " + Loc.T("skillr.enable"), _toggleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("skillr.mult"), _labelStyle, GUILayout.MinWidth(120));
            _skrMultText = GUILayout.TextField(_skrMultText ?? "", 6, _textFieldStyle, GUILayout.MinWidth(60));
            GUILayout.Label(Loc.T("skillr.mult_range"), _dimLabelStyle, GUILayout.MinWidth(80));
            GUILayout.Label(Loc.T("skillr.cap"), _labelStyle, GUILayout.MinWidth(100));
            _skrCapText = GUILayout.TextField(_skrCapText ?? "", 3, _textFieldStyle, GUILayout.MinWidth(50));
            GUILayout.Label(Loc.T("skillr.cap_range"), _dimLabelStyle, GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("skillr.overrides"), _labelStyle, GUILayout.MinWidth(120));
            _skrOverridesText = GUILayout.TextField(_skrOverridesText ?? "", 240, _textFieldStyle, GUILayout.MinWidth(300));
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("skillr.overrides_hint"), _hintStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("skillr.apply"), _buttonStyle, GUILayout.MinWidth(120))) SkrApply();
            if (GUILayout.Button(Loc.T("skillr.reload"), _buttonStyle, GUILayout.MinWidth(120)))
            {
                // Re-seed the fields from the next reply (the admin asked for the server's values back).
                _skrSeeded = false;
                _skrNextReq = 0f;
                _skrRequested = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("skillr.hint"), _hintStyle);
            EndCard();
        }
    }
}

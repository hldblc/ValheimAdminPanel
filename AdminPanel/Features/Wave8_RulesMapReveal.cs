using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — Map reveal / reset for any player (client side, #15) ====================
    // Reveal the whole map, reveal a radius around the player, or reset exploration for one online player.
    // The explored map lives in that player's own save file, so the companion on THEIR client has to do it:
    // the server relays AP_SrvMapReveal -> AP_MapReveal and refuses (with the capability reason) when the
    // target's client does not run the companion. "Me" always works — on a host the relay dispatches
    // in-process. Nothing here stores state; it is a fire-and-forget action with a 2 s click guard.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _maprSectionCfg;

        private long _maprTargetId;              // 0 = self
        private string _maprRadius = "200";
        private float _maprNextReq;

        internal void MaprInit()
        {
            _maprSectionCfg = Config.Bind("Features", "ShowMapRevealSection", true,
                "Show the Map Reveal section in the Tools tab (reveal all / reveal a radius / reset exploration for one online player).");
        }

        internal bool MaprSectionEnabled() => _maprSectionCfg == null || _maprSectionCfg.Value;

        internal void MaprReset()
        {
            _maprTargetId = 0;
            _maprRadius = "200";
            _maprNextReq = 0f;
        }

        // AP_SrvMapReveal: {int ver, long target (0 = me), int mode (0 all / 1 radius / 2 reset), float radius}
        private void MaprSend(int mode)
        {
            if (!RulesReachable(true)) return;
            if (Time.time < _maprNextReq) return;   // throttle-first
            _maprNextReq = Time.time + 2f;

            float radius;
            if (!RulesParseFloat(_maprRadius, out radius)) radius = 200f;
            radius = Mathf.Clamp(radius, 10f, 2000f);

            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(_maprTargetId);
            pkg.Write(mode);
            pkg.Write(radius);
            SrvRpc("AP_SrvMapReveal", pkg);
            Message(Loc.T(mode == 0 ? "mapr.msg_sent_all" : mode == 1 ? "mapr.msg_sent_radius" : "mapr.msg_sent_reset",
                TargetLabel(ref _maprTargetId)));
        }

        internal void DrawMapRevealSection()
        {
            BeginCard(Loc.T("mapr.section"));

            // Target row: label + name + cycle button, constant count (only the name text swaps).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mapr.target"), _labelStyle, GUILayout.MinWidth(90));
            GUILayout.Label(TargetLabel(ref _maprTargetId), _headerStyle, GUILayout.MinWidth(140));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("mapr.cycle"), _buttonStyle, GUILayout.MinWidth(110))) CycleTarget(ref _maprTargetId);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("mapr.radius"), _labelStyle, GUILayout.MinWidth(90));
            _maprRadius = GUILayout.TextField(_maprRadius ?? "", 6, _textFieldStyle, GUILayout.MinWidth(70));
            GUILayout.Label(Loc.T("mapr.radius_range"), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("mapr.reveal_all"), _buttonStyle, GUILayout.MinWidth(120))) MaprSend(0);
            if (GUILayout.Button(Loc.T("mapr.reveal_radius"), _buttonStyle, GUILayout.MinWidth(120))) MaprSend(1);
            // Reset wipes the player's exploration: destructive, so it takes the two-click confirm.
            if (ConfirmButton("mapr:reset", Loc.T("mapr.reset"), GUILayout.MinWidth(120))) MaprSend(2);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("mapr.hint"), _hintStyle);
            EndCard();
        }
    }
}

using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Direct message (finishes a half-built feature) ====================
    // The companion has always registered and fully implemented AP_SrvMsg (admin-gated relay of a private
    // line to ONE player), but no client ever invoked it — the only message-to-a-player UI was the skill
    // raise's "private note", which rides AP_SrvSkillRaise instead. This section is the missing caller.
    //
    // Reaches the target only if they run the panel/companion (AP_Msg is our own RPC). The hint says so
    // rather than letting an admin type into a void; server-side moderation notices use the vanilla
    // ShowMessage path instead, which is why those reach unmodded clients and this does not.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _dmSectionCfg;
        private long _dmTargetId;
        private string _dmText = "";

        internal void DmInit()
        {
            _dmSectionCfg = Config.Bind("Features", "ShowDirectMessageSection", true,
                "Show the Direct Message section in the Extras tab (send a private line to one online player).");
        }

        internal bool DmSectionEnabled() => _dmSectionCfg == null || _dmSectionCfg.Value;

        internal void DmReset()
        {
            _dmTargetId = 0;
            _dmText = "";
        }

        // A DM needs a second person. _othersSnapshot is the roster of OTHER players, pinned once per frame
        // in DrawWindow's Layout block - read it, never rebuild it, and never branch the control count on
        // it (only label TEXT swaps below). Null means the roster has not been pinned yet this session, and
        // that is deliberately treated as "not alone" so a busy server is never mislabelled as empty.
        private bool DmSolo() => _othersSnapshot != null && _othersSnapshot.Count == 0;

        internal void DrawDirectMessageSection()
        {
            BeginCard(Loc.T("dm.section"));

            // Control count is constant: the target row is always label + button, and the label text
            // simply swaps between a player name and the "nobody picked" / "nobody online" string.
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("dm.target"), _labelStyle, GUILayout.MinWidth(90));
            GUILayout.Label(_dmTargetId == 0
                    ? Loc.T(DmSolo() ? "dm.no_target_solo" : "dm.no_target")
                    : TargetLabel(ref _dmTargetId),
                _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("dm.cycle"), _buttonStyle, GUILayout.MinWidth(110)))
            {
                // CycleTarget walks the roster and wraps back to 0 (= self). Self is not a valid
                // destination here, so step once more to land on a real player when anyone is online.
                CycleTarget(ref _dmTargetId);
                if (_dmTargetId == 0) CycleTarget(ref _dmTargetId);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("dm.text"), _labelStyle, GUILayout.MinWidth(90));
            _dmText = GUILayout.TextField(_dmText ?? "", 200, _textFieldStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("dm.send"), _buttonStyle, GUILayout.MinWidth(120)))
            {
                // "Choose a player first" is wrong when there is nobody to choose; say which it is.
                if (_dmTargetId == 0) Message(Loc.T(DmSolo() ? "dm.none_online" : "dm.pick_first"));
                else if (string.IsNullOrEmpty(_dmText?.Trim())) Message(Loc.T("dm.empty"));
                else
                {
                    SrvRpc("AP_SrvMsg", _dmTargetId, _dmText.Trim());
                    Message(Loc.T("dm.sent", TargetLabel(ref _dmTargetId)));
                    _dmText = "";
                }
            }
            GUILayout.EndHorizontal();

            // One hint label either way - the text swaps, the control count does not.
            GUILayout.Label(Loc.T(DmSolo() ? "dm.hint_solo" : "dm.hint"), _hintStyle);
            EndCard();
        }
    }
}

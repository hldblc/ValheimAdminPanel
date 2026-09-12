using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #24 Companion self-update (client card) ====================
    // Three owner-only, explicitly clicked steps against the server companion: Check (GitHub latest release),
    // Download & stage (verified download next to the running DLL), Apply (swap it in; loads at the next
    // restart). The card renders the state the companion reports after each step (AP_UpdateState) and never
    // decides anything itself.
    //
    // Member prefix: "Selfup". Locale prefix: "selfup.".
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _selfupSectionCfg;

        private sealed class UpdateStateData
        {
            public int Status;
            public string Current = "";
            public string Latest = "";
            public string Staged = "";
            public string Msg = "";
            public bool Enabled;
            public string Repo = "";
            public long AssetSize;
            public bool Busy;
            public float ReceivedAt;
        }

        // ---- live payload ----
        private UpdateStateData _selfupState;
        private bool _selfupPending;

        // ---- UI state ----
        private float _selfupNextReq;

        // ---- Layout snapshots ----
        private UpdateStateData _selfupStateLayout;
        private bool _selfupHostLayout;
        private bool _selfupPendingLayout;

        // ==================== lifecycle ====================

        internal void SelfupInit()
        {
            _selfupSectionCfg = Config.Bind("Features", "ShowSelfUpdateSection", true,
                "Show the Companion self-update card in the Tools tab (owner-only: check GitHub, stage and apply a newer AdminPanelCompanion.dll on the server).");
        }

        internal bool SelfupSectionEnabled() => _selfupSectionCfg == null || _selfupSectionCfg.Value;

        internal void SelfupReset()
        {
            _selfupState = null;
            _selfupPending = false;
            _selfupNextReq = 0f;
            _selfupStateLayout = null;
            _selfupHostLayout = false;
            _selfupPendingLayout = false;
        }

        // ==================== reply ====================

        private static void SelfupOnState(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseUpdateState(self, pkg);
        }

        // AP_UpdateState (v1): int ver, int status, string current, string latest, string staged,
        // string message, bool enabled, string repo, long assetSize, bool busy.
        internal static void ParseUpdateState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var s = new UpdateStateData
                {
                    Status = pkg.ReadInt(),
                    Current = pkg.ReadString() ?? "",
                    Latest = pkg.ReadString() ?? "",
                    Staged = pkg.ReadString() ?? "",
                    Msg = pkg.ReadString() ?? "",
                    Enabled = pkg.ReadBool(),
                    Repo = pkg.ReadString() ?? "",
                    AssetSize = pkg.ReadLong(),
                    Busy = pkg.ReadBool(),
                    ReceivedAt = Time.time,
                };
                self._selfupState = s;
                self._selfupPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ==================== requests ====================

        // The companion answers every step on its own; the poll only covers "card opened" and the tail end
        // of a download (30 s, or 5 s while the companion reports busy).
        private void SelfupPoll()
        {
            var busy = _selfupState != null && _selfupState.Busy;
            if (!SysPollDue(ref _selfupNextReq, busy ? 5f : 30f, _selfupHostLayout)) return;
            _selfupPending = _selfupState == null;
            SrvRpc("AP_SrvUpdateStateReq");
        }

        private void SelfupSend(string rpc, string msgKey)
        {
            if (!SysReachable()) return;
            SrvRpc(rpc);
            Message(Loc.T(msgKey));
            _selfupNextReq = Time.time + 5f;   // give the reply a moment before the timer asks again
        }

        // ==================== draw ====================

        private static string SelfupStatusKey(int status)
        {
            switch (status)
            {
                case 0: return "selfup.st_idle";
                case 1: return "selfup.st_disabled";
                case 2: return "selfup.st_checking";
                case 3: return "selfup.st_uptodate";
                case 4: return "selfup.st_available";
                case 5: return "selfup.st_downloading";
                case 6: return "selfup.st_staged";
                case 7: return "selfup.st_applied";
                case 8: return "selfup.st_error";
                case 9: return "selfup.st_locked";
                default: return "selfup.st_unknown";
            }
        }

        private void SelfupRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.MinWidth(120));
            GUILayout.Label(string.IsNullOrEmpty(value) ? Loc.T("selfup.none") : value, _headerStyle, GUILayout.MinWidth(120));
            GUILayout.EndHorizontal();
        }

        internal void DrawSelfUpdateSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _selfupHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _selfupStateLayout = _selfupState;
                _selfupPendingLayout = _selfupPending;
                SelfupPoll();
            }

            var s = _selfupStateLayout;
            BeginCard(Loc.T("selfup.section"));
            GUILayout.Label(Loc.T("selfup.hint"), _hintStyle);

            if (s == null)
            {
                GUILayout.Label(Loc.T(_selfupPendingLayout ? "selfup.pending" : "selfup.no_reply"), _hintStyle);
            }
            else
            {
                // Status is the line an admin scans for; the tail swaps between "(working...)" and nothing.
                SelfupRow(Loc.T("selfup.status"), Loc.T(SelfupStatusKey(s.Status)) + (s.Busy ? " " + Loc.T("selfup.busy") : ""));
                SelfupRow(Loc.T("selfup.current"), s.Current);
                SelfupRow(Loc.T("selfup.latest"), s.Latest.Length > 0 && s.AssetSize > 0L
                    ? s.Latest + " (" + Loc.T("selfup.size", s.AssetSize / 1024L) + ")" : s.Latest);
                SelfupRow(Loc.T("selfup.staged"), s.Staged);
                SelfupRow(Loc.T("selfup.repo"), s.Repo);
                // One prose label either way: the companion's own message, or the disabled note.
                GUILayout.Label(s.Enabled ? (s.Msg.Length > 0 ? s.Msg : Loc.T("selfup.no_message")) : Loc.T("selfup.disabled_hint"), _proseStyle);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("selfup.check"), _buttonStyle, GUILayout.MinWidth(90)))
                SelfupSend("AP_SrvUpdateCheck", "selfup.msg_check");
            if (GUILayout.Button(Loc.T("selfup.stage"), _buttonStyle, GUILayout.MinWidth(140)))
                SelfupSend("AP_SrvUpdateStage", "selfup.msg_stage");
            if (ConfirmButton("selfup:apply", Loc.T("selfup.apply"), GUILayout.MinWidth(90)))
                SelfupSend("AP_SrvUpdateApply", "selfup.msg_apply");
            if (GUILayout.Button(Loc.T("selfup.refresh"), _buttonStyle, GUILayout.MinWidth(80)))
                _selfupNextReq = 0f;   // the next Layout pass sends
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("selfup.restart_hint"), _hintStyle);

            EndCard();
        }
    }
}

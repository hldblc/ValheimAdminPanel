using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #21 Death rules (client card) ====================
    // Owner sets the policy (mode / lives / keep gear / keep skills / announce) — the companion writes its
    // own config so it survives restarts — and moderators act on the per-player lives table (reset, revive).
    // Everything shown is server truth from AP_DeathRulesData; the edit fields are seeded from the first reply
    // and re-seeded after Apply, never overwritten while the admin is typing.
    //
    // Member prefix: "Death". Locale prefix: "death.".
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _deathSectionCfg;

        private const int DeathRowCap = 100;   // wire contract: rows <= 100

        private sealed class DeathRulesData
        {
            public int Mode;
            public int Lives;
            public bool KeepGear;
            public bool KeepSkills;
            public bool Announce;
            public bool StoreReady;
            public readonly List<DeathRow> Rows = new List<DeathRow>();
            public float ReceivedAt;
        }

        private struct DeathRow
        {
            public string Id;
            public string Name;
            public int Remaining;
            public long LastDeathTicks;
            public bool Locked;
        }

        // ---- live payload (written by the RPC handler, any time) ----
        private DeathRulesData _deathData;
        private bool _deathPending;

        // ---- UI state ----
        private float _deathNextReq;
        private bool _deathEditSynced;
        private int _deathModeEdit;
        private int _deathModeEditLayout;   // chip row reads this snapshot, never the live field
        private string _deathLivesEdit = "3";
        private bool _deathKeepGearEdit;
        private bool _deathKeepSkillsEdit;
        private bool _deathAnnounceEdit = true;
        private Vector2 _deathScroll;

        // ---- Layout snapshots (the ONLY things the draw code reads) ----
        private DeathRulesData _deathDataLayout;
        private bool _deathHostLayout;
        private bool _deathPendingLayout;

        // ==================== lifecycle ====================

        internal void DeathInit()
        {
            _deathSectionCfg = Config.Bind("Features", "ShowDeathRulesSection", true,
                "Show the Death Rules card in the Tools tab (lives / permadeath / keep gear policy enforced by the server companion).");
        }

        internal bool DeathSectionEnabled() => _deathSectionCfg == null || _deathSectionCfg.Value;

        // Per-world: the lives table belongs to the server just left.
        internal void DeathReset()
        {
            _deathData = null;
            _deathPending = false;
            _deathNextReq = 0f;
            _deathEditSynced = false;
            _deathModeEdit = 0;
            _deathModeEditLayout = 0;
            _deathLivesEdit = "3";
            _deathKeepGearEdit = false;
            _deathKeepSkillsEdit = false;
            _deathAnnounceEdit = true;
            _deathScroll = Vector2.zero;
            _deathDataLayout = null;
            _deathHostLayout = false;
            _deathPendingLayout = false;
        }

        // ==================== reply ====================

        private static void DeathOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseDeathRulesData(self, pkg);
        }

        // AP_DeathRulesData (v1): int ver, int mode, int lives, bool keepGear, bool keepSkills, bool announce,
        // bool storeReady, int rows(<=100) x (string id, string name, int remaining, long lastDeathTicks, bool locked).
        internal static void ParseDeathRulesData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new DeathRulesData
                {
                    Mode = pkg.ReadInt(),
                    Lives = pkg.ReadInt(),
                    KeepGear = pkg.ReadBool(),
                    KeepSkills = pkg.ReadBool(),
                    Announce = pkg.ReadBool(),
                    StoreReady = pkg.ReadBool(),
                    ReceivedAt = Time.time,
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > DeathRowCap) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new DeathRow
                    {
                        Id = pkg.ReadString(),
                        Name = pkg.ReadString(),
                        Remaining = pkg.ReadInt(),
                        LastDeathTicks = pkg.ReadLong(),
                        Locked = pkg.ReadBool(),
                    });
                self._deathData = d;
                self._deathPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ==================== requests ====================

        // Layout pass only (see SysPollDue). 30 s: the table changes when someone dies, not per frame.
        private void DeathPoll()
        {
            if (!SysPollDue(ref _deathNextReq, 30f, _deathHostLayout)) return;
            _deathPending = _deathData == null;
            SrvRpc("AP_SrvDeathRulesReq");
        }

        private void DeathApply()
        {
            if (!SysReachable()) return;
            int lives;
            if (!int.TryParse((_deathLivesEdit ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out lives)
                || lives < 1 || lives > 20)
            { Message(Loc.T("death.msg_bad_lives")); return; }
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(Mathf.Clamp(_deathModeEdit, 0, 2));
            pkg.Write(lives);
            pkg.Write(_deathKeepGearEdit);
            pkg.Write(_deathKeepSkillsEdit);
            pkg.Write(_deathAnnounceEdit);
            SrvRpc("AP_SrvDeathRulesSet", pkg);
            Message(Loc.T("death.msg_applied"));
            _deathEditSynced = false;   // the reply re-seeds the fields with what the server actually stored
        }

        // op 0 = reset lives (and unlock), 1 = revive (unlock only).
        private void DeathPlayerAction(string id, string label, int op)
        {
            if (string.IsNullOrEmpty(id) || !SysReachable()) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(id);
            pkg.Write(op);
            SrvRpc("AP_SrvDeathRulesPlayer", pkg);
            Message(Loc.T(op == 0 ? "death.msg_reset" : "death.msg_revive", label));
        }

        // ==================== draw ====================

        private static string DeathModeKey(int mode) =>
            mode == 2 ? "death.mode_perma" : mode == 1 ? "death.mode_lives" : "death.mode_off";

        internal void DrawDeathRulesSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _deathHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _deathDataLayout = _deathData;
                _deathPendingLayout = _deathPending;
                if (_deathData != null && !_deathEditSynced)
                {
                    _deathEditSynced = true;
                    _deathModeEdit = _deathData.Mode;
                    _deathLivesEdit = _deathData.Lives.ToString(CultureInfo.InvariantCulture);
                    _deathKeepGearEdit = _deathData.KeepGear;
                    _deathKeepSkillsEdit = _deathData.KeepSkills;
                    _deathAnnounceEdit = _deathData.Announce;
                }
                _deathModeEditLayout = _deathModeEdit;   // after the seed above, so both passes see the same mode
                DeathPoll();
            }

            BeginCard(Loc.T("death.section"));
            GUILayout.Label(Loc.T("death.hint"), _hintStyle);

            // Mode chips: act only on an off->on FLIP (see the chip-row comment in FeaturesCore).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("death.mode"), _labelStyle, GUILayout.MinWidth(90));
            for (var m = 0; m < 3; m++)
            {
                var wasOn = _deathModeEditLayout == m;
                var on = GUILayout.Toggle(wasOn, Loc.T(DeathModeKey(m)), _chipStyleOrButton(), GUILayout.MinWidth(100));
                if (on && !wasOn) _deathModeEdit = m;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("death.lives"), _labelStyle, GUILayout.MinWidth(90));
            _deathLivesEdit = GUILayout.TextField(_deathLivesEdit ?? "", 3, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Space(12);
            _deathKeepGearEdit = GUILayout.Toggle(_deathKeepGearEdit, Loc.T("death.keep_gear"), _toggleStyle);
            GUILayout.Space(8);
            _deathKeepSkillsEdit = GUILayout.Toggle(_deathKeepSkillsEdit, Loc.T("death.keep_skills"), _toggleStyle);
            GUILayout.Space(8);
            _deathAnnounceEdit = GUILayout.Toggle(_deathAnnounceEdit, Loc.T("death.announce"), _toggleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var d = _deathDataLayout;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("death.apply"), _buttonStyle, GUILayout.MinWidth(110))) DeathApply();
            if (GUILayout.Button(Loc.T("death.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                _deathNextReq = 0f;          // the next Layout pass sends; never send from the event pass
                _deathEditSynced = false;
            }
            GUILayout.FlexibleSpace();
            // One label either way - only the TEXT swaps.
            GUILayout.Label(d == null
                    ? Loc.T("death.state_pending")
                    : d.Mode == 0 ? Loc.T("death.state_off")
                    : Loc.T("death.state", Loc.T(DeathModeKey(d.Mode)), d.Lives),
                _dimLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("death.apply_hint"), _hintStyle);

            DrawSection(Loc.T("death.players_title"));
            if (d == null)
            {
                GUILayout.Label(Loc.T(_deathPendingLayout ? "death.pending" : "death.no_reply"), _hintStyle);
            }
            else if (!d.StoreReady)
            {
                GUILayout.Label(Loc.T("death.store_not_ready"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("death.empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("death.col_player"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("death.col_id"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("death.col_lives"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("death.col_last"), _headerStyle, GUILayout.Width(100));
                GUILayout.Label(Loc.T("death.col_status"), _headerStyle, GUILayout.Width(110));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                _deathScroll = GUILayout.BeginScrollView(_deathScroll,
                    GUILayout.Height(Mathf.Min(ListView(430f), d.Rows.Count * 30f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    var label = string.IsNullOrEmpty(r.Name) ? (string.IsNullOrEmpty(r.Id) ? "?" : r.Id) : r.Name;
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(r.Name) ? Loc.T("death.unknown_player") : r.Name, _cellStyle, GUILayout.Width(130));
                    GUILayout.Label(r.Id ?? "", _dimCellStyle, GUILayout.Width(150));
                    GUILayout.Label(Loc.T("death.lives_cell", r.Remaining, d.Lives), _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(SysAgo(r.LastDeathTicks, "death.never", "death.now", "death.ago_min", "death.ago_hour", "death.ago_day"),
                        _dimCellStyle, GUILayout.Width(100));
                    // A locked-out player is the one thing an admin scans for: bright cell style.
                    GUILayout.Label(Loc.T(r.Locked ? "death.locked" : "death.alive"),
                        r.Locked ? _cellStyle : _dimCellStyle, GUILayout.Width(110));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("death.reset"), _buttonStyle, GUILayout.MinWidth(90)))
                        DeathPlayerAction(r.Id, label, 0);
                    if (GUILayout.Button(Loc.T("death.revive"), _buttonStyle, GUILayout.MinWidth(70)))
                        DeathPlayerAction(r.Id, label, 1);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            EndCard();
        }
    }
}

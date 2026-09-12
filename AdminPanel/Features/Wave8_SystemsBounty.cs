using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #23 Bounty board (client card) ====================
    // Create kill / boss / deliver bounties, watch progress, close or delete them, and collect a deliver
    // bounty from a chosen player's client. The board itself is server truth (AP_BountyData): kill tracking
    // happens on the killer's client, hand-ins go through the economy's item-take executor, and rewards are
    // paid from the Wave 6 ledger — none of that lives in the panel.
    //
    // No "use selected creature/item" button: the Creatures/Items tabs keep a filtered window, not a
    // selection, so the prefab is typed and the server verifies it against its prefab database.
    //
    // Member prefix: "Bounty". Locale prefix: "bounty.".
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _bountySectionCfg;

        private const int BountyRowCap = 100;   // wire contract: rows <= 100

        private sealed class BountyData
        {
            public bool Enabled;
            public string Currency = "";
            public bool DeliverOk;
            public readonly List<BountyRow> Rows = new List<BountyRow>();
            public float ReceivedAt;
        }

        private struct BountyRow
        {
            public string Id;
            public string Kind;       // kill | boss | deliver
            public string Target;
            public int Count;
            public long Reward;
            public long CreatedTicks;
            public string CreatedBy;
            public string Status;     // active | closed | done
            public string Winner;
            public string Progress;   // "Name 4, Name 2, Name 1"
        }

        // ---- live payload ----
        private BountyData _bountyData;
        private bool _bountyPending;

        // ---- UI state ----
        private float _bountyNextReq;
        private int _bountyKindEdit;
        private int _bountyKindEditLayout;   // chip row reads this snapshot, never the live field
        private string _bountyTargetEdit = "";
        private string _bountyCountEdit = "10";
        private string _bountyRewardEdit = "50";
        private long _bountyCollectTarget;
        private Vector2 _bountyScroll;

        // ---- Layout snapshots ----
        private BountyData _bountyDataLayout;
        private bool _bountyHostLayout;
        private bool _bountyPendingLayout;

        // ==================== lifecycle ====================

        internal void BountyInit()
        {
            _bountySectionCfg = Config.Bind("Features", "ShowBountiesSection", true,
                "Show the Bounty board card in the Tools tab (post kill / boss / deliver bounties paid from the economy ledger).");
        }

        internal bool BountySectionEnabled() => _bountySectionCfg == null || _bountySectionCfg.Value;

        internal void BountyReset()
        {
            _bountyData = null;
            _bountyPending = false;
            _bountyNextReq = 0f;
            _bountyKindEdit = 0;
            _bountyKindEditLayout = 0;
            _bountyTargetEdit = "";
            _bountyCountEdit = "10";
            _bountyRewardEdit = "50";
            _bountyCollectTarget = 0L;
            _bountyScroll = Vector2.zero;
            _bountyDataLayout = null;
            _bountyHostLayout = false;
            _bountyPendingLayout = false;
        }

        // ==================== reply ====================

        private static void BountyOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseBountyData(self, pkg);
        }

        // AP_BountyData (v1): int ver, bool enabled, string currency, bool deliverOk, int rows(<=100) x
        // (string id, string kind, string target, int count, long reward, long createdTicks, string createdBy,
        //  string status, string winner, string topProgress).
        internal static void ParseBountyData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new BountyData
                {
                    Enabled = pkg.ReadBool(),
                    Currency = pkg.ReadString() ?? "",
                    DeliverOk = pkg.ReadBool(),
                    ReceivedAt = Time.time,
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > BountyRowCap) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new BountyRow
                    {
                        Id = pkg.ReadString(),
                        Kind = pkg.ReadString(),
                        Target = pkg.ReadString(),
                        Count = pkg.ReadInt(),
                        Reward = pkg.ReadLong(),
                        CreatedTicks = pkg.ReadLong(),
                        CreatedBy = pkg.ReadString(),
                        Status = pkg.ReadString(),
                        Winner = pkg.ReadString(),
                        Progress = pkg.ReadString(),
                    });
                self._bountyData = d;
                self._bountyPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ==================== requests ====================

        private void BountyPoll()
        {
            if (!SysPollDue(ref _bountyNextReq, 30f, _bountyHostLayout)) return;
            _bountyPending = _bountyData == null;
            SrvRpc("AP_SrvBountyReq");
        }

        private void BountyCreate()
        {
            if (!SysReachable()) return;
            var target = (_bountyTargetEdit ?? "").Trim();
            if (target.Length == 0 || target.Length > 64 || target.IndexOf(' ') >= 0) { Message(Loc.T("bounty.msg_bad_target")); return; }
            int count;
            if (!int.TryParse((_bountyCountEdit ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                || count < 1 || count > 10000) { Message(Loc.T("bounty.msg_bad_count")); return; }
            long reward;
            if (!long.TryParse((_bountyRewardEdit ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out reward)
                || reward < 0L) { Message(Loc.T("bounty.msg_bad_reward")); return; }
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(Mathf.Clamp(_bountyKindEdit, 0, 2));
            pkg.Write(target);
            pkg.Write(count);
            pkg.Write(reward);
            SrvRpc("AP_SrvBountySet", pkg);
            Message(Loc.T("bounty.msg_posted"));
        }

        // op 0 close, 1 delete, 2 collect (targetUid: 0 = the admin's own client).
        private void BountyAction(string id, int op, long targetUid)
        {
            if (string.IsNullOrEmpty(id) || !SysReachable()) return;
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(id);
            pkg.Write(op);
            if (op == 2) pkg.Write(targetUid);
            SrvRpc("AP_SrvBountyAction", pkg);
            if (op == 0) Message(Loc.T("bounty.msg_closed", id));
            else if (op == 1) Message(Loc.T("bounty.msg_deleted", id));
            else Message(Loc.T("bounty.msg_collect", TargetLabel(ref _bountyCollectTarget), id));
        }

        // ==================== draw ====================

        private static string BountyKindKey(string kind)
        {
            switch (kind)
            {
                case "boss": return "bounty.kind_boss";
                case "deliver": return "bounty.kind_deliver";
                default: return "bounty.kind_kill";
            }
        }

        private static string BountyKindKey(int kind) =>
            kind == 2 ? "bounty.kind_deliver" : kind == 1 ? "bounty.kind_boss" : "bounty.kind_kill";

        internal void DrawBountiesSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _bountyHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _bountyDataLayout = _bountyData;
                _bountyPendingLayout = _bountyPending;
                _bountyKindEditLayout = _bountyKindEdit;
                BountyPoll();
            }

            var d = _bountyDataLayout;
            BeginCard(Loc.T("bounty.section"));
            GUILayout.Label(Loc.T("bounty.hint"), _hintStyle);
            // One status label either way; only the key swaps.
            GUILayout.Label(d == null ? Loc.T(_bountyPendingLayout ? "bounty.pending" : "bounty.no_reply")
                          : !d.Enabled ? Loc.T("bounty.disabled")
                          : !d.DeliverOk ? Loc.T("bounty.deliver_off")
                          : Loc.T("bounty.create_hint", d.Currency.Length > 0 ? d.Currency : Loc.T("bounty.currency_default")),
                _hintStyle);

            // ---- create ----
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bounty.kind"), _labelStyle, GUILayout.MinWidth(70));
            for (var k = 0; k < 3; k++)
            {
                var wasOn = _bountyKindEditLayout == k;
                var on = GUILayout.Toggle(wasOn, Loc.T(BountyKindKey(k)), _chipStyleOrButton(), GUILayout.MinWidth(90));
                if (on && !wasOn) _bountyKindEdit = k;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bounty.target"), _labelStyle, GUILayout.MinWidth(70));
            _bountyTargetEdit = GUILayout.TextField(_bountyTargetEdit ?? "", 64, _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("bounty.count"), _labelStyle, GUILayout.MinWidth(50));
            _bountyCountEdit = GUILayout.TextField(_bountyCountEdit ?? "", 5, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("bounty.reward"), _labelStyle, GUILayout.MinWidth(60));
            _bountyRewardEdit = GUILayout.TextField(_bountyRewardEdit ?? "", 10, _textFieldStyle, GUILayout.Width(80));
            if (GUILayout.Button(Loc.T("bounty.create"), _buttonStyle, GUILayout.MinWidth(110))) BountyCreate();
            if (GUILayout.Button(Loc.T("bounty.refresh"), _buttonStyle, GUILayout.MinWidth(80))) _bountyNextReq = 0f;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ---- collect target (deliver bounties): label + cycle, control count constant ----
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("bounty.collect_target"), _labelStyle, GUILayout.MinWidth(90));
            GUILayout.Label(TargetLabel(ref _bountyCollectTarget), _headerStyle, GUILayout.MinWidth(140));
            if (GUILayout.Button(Loc.T("bounty.cycle"), _buttonStyle, GUILayout.MinWidth(80))) CycleTarget(ref _bountyCollectTarget);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ---- board ----
            if (d != null && d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("bounty.empty"), _hintStyle);
            }
            else if (d != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("bounty.col_id"), _headerStyle, GUILayout.Width(40));
                GUILayout.Label(Loc.T("bounty.col_kind"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("bounty.col_target"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("bounty.col_reward"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("bounty.col_status"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("bounty.col_progress"), _headerStyle, GUILayout.MinWidth(120));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                _bountyScroll = GUILayout.BeginScrollView(_bountyScroll,
                    GUILayout.Height(Mathf.Min(ListView(470f), d.Rows.Count * 30f + 16f)));
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    var active = r.Status == "active";
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label("#" + (r.Id ?? "?"), _dimCellStyle, GUILayout.Width(40));
                    GUILayout.Label(Loc.T(BountyKindKey(r.Kind)), _cellStyle, GUILayout.Width(70));
                    GUILayout.Label(Loc.T("bounty.target_x", r.Count, r.Target ?? ""), _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(r.Reward.ToString(CultureInfo.InvariantCulture) + " " + d.Currency, _cellStyle, GUILayout.Width(90));
                    // Open bounties bright, finished ones dim. The status text carries the winner when done.
                    GUILayout.Label(r.Status == "done" ? Loc.T("bounty.status_done", r.Winner ?? "")
                                  : Loc.T(active ? "bounty.status_active" : "bounty.status_closed"),
                        active ? _cellStyle : _dimCellStyle, GUILayout.Width(110));
                    GUILayout.Label(string.IsNullOrEmpty(r.Progress) ? "-" : r.Progress, _dimCellStyle, GUILayout.MinWidth(120));
                    GUILayout.FlexibleSpace();
                    // The row's button set follows the SNAPSHOT row (kind/status), so it is stable per frame.
                    if (active && GUILayout.Button(Loc.T("bounty.close"), _buttonStyle, GUILayout.MinWidth(70)))
                        BountyAction(r.Id, 0, 0L);
                    if (active && r.Kind == "deliver" && GUILayout.Button(Loc.T("bounty.collect"), _buttonStyle, GUILayout.MinWidth(80)))
                        BountyAction(r.Id, 2, _bountyCollectTarget);
                    // Per-row confirm ids are suffixed with the stable bounty id, never the row index.
                    if (ConfirmButton("bounty:del:" + (r.Id ?? "?"), Loc.T("bounty.delete"), GUILayout.MinWidth(70)))
                        BountyAction(r.Id, 1, 0L);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            EndCard();
        }
    }
}

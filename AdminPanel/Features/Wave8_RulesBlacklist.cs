using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — Recipe and build-piece blacklist (client side, #19) ====================
    // Ban build pieces or recipes server-wide. The table lives on the server ("blacklist": prefab -> kind).
    // Modded clients hide the entries (Piece.m_enabled / Recipe.m_enabled); for UNMODDED clients the
    // companion removes a banned piece the moment it is placed, like the round-1 protection zones — so a
    // piece ban reaches everyone, a recipe ban only modded clients (crafting never touches the server).
    //
    // IMGUI discipline: the reply is split into the two lists on the Layout pass; each list's branch reads
    // that snapshot only. "Use aimed piece" is a one-shot raycast on the click, not a per-frame tick.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _blkSectionCfg;

        private sealed class BlkRow
        {
            public string Prefab = "";
            public int Kind;   // 0 piece, 1 recipe
        }

        // ---- live payload ----
        private List<BlkRow> _blkRows;     // null = no reply yet
        private bool _blkEnabled;
        private bool _blkPending;
        private bool _blkRequested;
        private float _blkNextReq;

        // ---- UI state ----
        private string _blkPieceName = "";
        private string _blkRecipeName = "";
        private Vector2 _blkPieceScroll, _blkRecipeScroll;

        // ---- Layout snapshots ----
        private List<BlkRow> _blkPiecesLayout;   // null = no reply yet
        private List<BlkRow> _blkRecipesLayout;
        private bool _blkEnabledLayout;
        private bool _blkPendingLayout;
        private bool _blkHaveLayout;

        private const int BlkCap = 200;   // wire contract with the companion

        // ---- lifecycle ----

        internal void BlkInit()
        {
            _blkSectionCfg = Config.Bind("Features", "ShowBlacklistSection", true,
                "Show the Blacklist section in the Tools tab (server-wide banned build pieces and recipes).");
        }

        internal bool BlkSectionEnabled() => _blkSectionCfg == null || _blkSectionCfg.Value;

        internal void BlkReset()
        {
            _blkRows = null;
            _blkEnabled = false;
            _blkPending = false;
            _blkRequested = false;
            _blkNextReq = 0f;
            _blkPieceName = "";
            _blkRecipeName = "";
            _blkPieceScroll = _blkRecipeScroll = Vector2.zero;
            _blkPiecesLayout = null;
            _blkRecipesLayout = null;
            _blkEnabledLayout = false;
            _blkPendingLayout = false;
            _blkHaveLayout = false;
        }

        // ---- reply ----

        private static void BlkOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseBlacklistData(self, pkg);
        }

        // AP_BlacklistData: {int ver, bool enabled, int n(<=200), n x (string prefab, int kind)}
        internal static void ParseBlacklistData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != RulesVer) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > BlkCap) return;
                var rows = new List<BlkRow>(n);
                for (var i = 0; i < n; i++)
                    rows.Add(new BlkRow { Prefab = pkg.ReadString() ?? "", Kind = pkg.ReadInt() });
                self._blkRows = rows;
                self._blkEnabled = enabled;
                self._blkPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        private void BlkRequest()
        {
            if (!RulesReachable(false)) return;
            if (Time.time < _blkNextReq) return;
            _blkNextReq = Time.time + 2f;
            _blkPending = true;
            SrvRpc("AP_SrvBlacklistReq");
        }

        // AP_SrvBlacklistSet: {int ver, int action (0 add / 1 remove), string prefab, int kind (0 piece / 1 recipe)}
        private void BlkSend(int action, string prefab, int kind)
        {
            if (!RulesReachable(true)) return;
            prefab = (prefab ?? "").Trim();
            if (prefab.Length == 0) { Message(Loc.T(kind == 0 ? "blk.msg_need_piece" : "blk.msg_need_recipe")); return; }
            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(action);
            pkg.Write(prefab);
            pkg.Write(kind);
            SrvRpc("AP_SrvBlacklistSet", pkg);
            _blkPending = true;
            Message(Loc.T(action == 0 ? "blk.msg_ban_sent" : "blk.msg_unban_sent", prefab));
        }

        private void BlkUseAimed()
        {
            if (LocalPlayer == null) { Message(Loc.T("blk.msg_no_player")); return; }
            var range = _bldTargetRangeCfg != null ? Mathf.Clamp(_bldTargetRangeCfg.Value, 5f, 200f) : 50f;
            var piece = RulesAimedPiece(range);
            var name = piece != null ? RulesPrefabName(piece.gameObject) : null;
            if (string.IsNullOrEmpty(name)) { Message(Loc.T("blk.msg_no_aim")); return; }
            _blkPieceName = name;
            Message(Loc.T("blk.msg_aimed", name));
        }

        private void BlkRebuildLayout()
        {
            _blkHaveLayout = _blkRows != null;
            _blkEnabledLayout = _blkEnabled;
            _blkPendingLayout = _blkPending;
            if (_blkRows == null) { _blkPiecesLayout = null; _blkRecipesLayout = null; return; }
            var pieces = new List<BlkRow>();
            var recipes = new List<BlkRow>();
            foreach (var r in _blkRows) (r.Kind == 1 ? recipes : pieces).Add(r);
            pieces.Sort((a, b) => string.CompareOrdinal(a.Prefab, b.Prefab));
            recipes.Sort((a, b) => string.CompareOrdinal(a.Prefab, b.Prefab));
            _blkPiecesLayout = pieces;
            _blkRecipesLayout = recipes;
        }

        // ---- draw ----

        internal void DrawBlacklistSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                BlkRebuildLayout();
                if (!_blkRequested && RulesReachable(false)) { _blkRequested = true; BlkRequest(); }   // see MapPins: only once reachable
            }

            BeginCard(Loc.T("blk.section"));
            RulesDrawStatusRow(_blkHaveLayout, _blkEnabledLayout, RulesChBlacklist,
                "blk.status_unknown", "blk.status_on", "blk.status_off", "blk.turn_on", "blk.turn_off");

            // ---- build pieces ----
            DrawSection(Loc.T("blk.pieces_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("blk.prefab"), _labelStyle, GUILayout.MinWidth(60));
            _blkPieceName = GUILayout.TextField(_blkPieceName ?? "", 64, _textFieldStyle, GUILayout.MinWidth(170));
            if (GUILayout.Button(Loc.T("blk.use_aimed"), _buttonStyle, GUILayout.MinWidth(110))) BlkUseAimed();
            if (GUILayout.Button(Loc.T("blk.ban_piece"), _buttonStyle, GUILayout.MinWidth(100))) BlkSend(0, _blkPieceName, 0);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            BlkDrawList(_blkPiecesLayout, ref _blkPieceScroll, "blk.pieces_empty", "blk:delp:");

            // ---- recipes ----
            DrawSection(Loc.T("blk.recipes_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("blk.item"), _labelStyle, GUILayout.MinWidth(60));
            _blkRecipeName = GUILayout.TextField(_blkRecipeName ?? "", 64, _textFieldStyle, GUILayout.MinWidth(170));
            if (GUILayout.Button(Loc.T("blk.ban_recipe"), _buttonStyle, GUILayout.MinWidth(100))) BlkSend(0, _blkRecipeName, 1);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            BlkDrawList(_blkRecipesLayout, ref _blkRecipeScroll, "blk.recipes_empty", "blk:delr:");

            GUILayout.BeginHorizontal();
            var total = _blkPiecesLayout != null && _blkRecipesLayout != null ? _blkPiecesLayout.Count + _blkRecipesLayout.Count : 0;
            GUILayout.Label(Loc.T("blk.count", total, BlkCap), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("blk.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                _blkNextReq = 0f;
                _blkRequested = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("blk.hint"), _hintStyle);
            EndCard();
        }

        // One list body: idle / pending / empty / rows — every branch gated on the snapshot passed in.
        private void BlkDrawList(List<BlkRow> rows, ref Vector2 scroll, string emptyKey, string confirmPrefix)
        {
            if (rows == null)
            {
                GUILayout.Label(Loc.T(_blkPendingLayout ? "blk.pending" : "blk.idle"), _hintStyle);
                return;
            }
            if (rows.Count == 0)
            {
                GUILayout.Label(Loc.T(emptyKey), _hintStyle);
                return;
            }
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(ListView(420f) * 0.5f, rows.Count * 28f + 16f)));
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(r.Prefab, _cellStyle, GUILayout.MinWidth(220));
                GUILayout.FlexibleSpace();
                if (ConfirmButton(confirmPrefix + r.Prefab, Loc.T("blk.remove"), GUILayout.MinWidth(80)))
                    BlkSend(1, r.Prefab, r.Kind);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
    }
}

using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — server RULES (client side, shared plumbing) ====================
    // Five Tools-tab sections that all edit a rule table the companion stores on the server and pushes to
    // every modded client: map pins (mpin), map reveal (mapr), trader stock (trader), recipe/piece blacklist
    // (blk), skill rules (skillr). The panel never applies a rule itself — the companion running on THIS
    // client does that (it is the same DLL ordinary players run) — so every card here is a remote control:
    // send an edit, get the whole table back (AP_*Data), draw from a Layout snapshot of it. Unmodded clients
    // see vanilla; every card's hint says so.
    //
    // Reply registration follows Wave1_Audit: one ZNet.Awake postfix registers the network half (gate +
    // parse) and the in-process bridge half side by side, so a listen-server host gets the same replies.
    public partial class AdminPanelPlugin
    {
        // Wire version of every payload in this group (the companion's Wave8Rules.Ver). Readers discard the
        // whole packet on a mismatch.
        private const int RulesVer = 1;

        // AP_SrvRulesToggle channel numbers (companion: Wave8Rules.ChPins etc.).
        private const int RulesChPins = 0;
        private const int RulesChTrader = 1;
        private const int RulesChBlacklist = 2;
        private const int RulesChSkills = 3;

        [HarmonyPatch]
        private static class Wave8RulesRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_MapPinsData", MpinOnData);
                AdminPanelLocalBridge.Register("AP_MapPinsData", ParseMapPinsData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_TraderStockData", TrOnData);
                AdminPanelLocalBridge.Register("AP_TraderStockData", ParseTraderStockData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_BlacklistData", BlkOnData);
                AdminPanelLocalBridge.Register("AP_BlacklistData", ParseBlacklistData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SkillRulesData", SkrOnData);
                AdminPanelLocalBridge.Register("AP_SkillRulesData", ParseSkillRulesData);
            }
        }

        // Reachable companion = a remote server peer OR a listen-server host (the companion runs in this
        // process and answers locally). Only a missing ZNet or a half-open connection is "not connected".
        private bool RulesReachable(bool toast)
        {
            if (ZNet.instance == null || (ServerUid() == 0L && !ZNet.instance.IsServer()))
            {
                if (toast) Message(Loc.T("common.not_connected_srv"));
                return false;
            }
            return true;
        }

        // Owner-only flip of one channel's Enable* config switch on the server; the reply that follows
        // carries the channel's table, so the card refreshes by itself.
        private void RulesSendToggle(int channel, bool on)
        {
            if (!RulesReachable(true)) return;
            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(channel);
            pkg.Write(on);
            SrvRpc("AP_SrvRulesToggle", pkg);
        }

        // Status line + on/off button shared by the four table cards. Always one label and one button; the
        // caller passes its own prefix's keys so the control count never depends on the data and every
        // key literal stays greppable by the locale checker.
        private void RulesDrawStatusRow(bool haveData, bool enabled, int channel,
            string keyUnknown, string keyOn, string keyOff, string keyTurnOn, string keyTurnOff)
        {
            var on = haveData && enabled;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(!haveData ? keyUnknown : on ? keyOn : keyOff), on ? _headerStyle : _dimLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T(on ? keyTurnOff : keyTurnOn), _buttonStyle, GUILayout.MinWidth(120)))
                RulesSendToggle(channel, !on);
            GUILayout.EndHorizontal();
        }

        private static string RulesF(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool RulesParseFloat(string s, out float v)
        {
            if (float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return !float.IsNaN(v) && !float.IsInfinity(v);
            return false;
        }

        private static bool RulesParseInt(string s, out int v) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        // Prefab name of a live object ("piece_ballista(Clone)" -> "piece_ballista").
        private static string RulesPrefabName(GameObject go)
        {
            if (go == null) return null;
            var n = go.name;
            if (string.IsNullOrEmpty(n)) return null;
            var idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx > 0 ? n.Substring(0, idx) : n;
        }

        // One-shot crosshair raycast for a build piece — the Build Tools target scan (Wave5_BuildTools.cs
        // BldUpdateTarget) without the per-frame tick: the nearest hit is not necessarily the piece (a
        // shrub collider, the terrain in front of a wall), so every hit is scanned for the closest Piece.
        private static readonly RaycastHit[] RulesHits = new RaycastHit[48];

        private static Piece RulesAimedPiece(float range)
        {
            var cam = GameCamera.instance;
            if (cam == null) return null;
            var n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, RulesHits, range,
                ~0, QueryTriggerInteraction.Ignore);
            Piece best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var col = RulesHits[i].collider;
                if (col == null) continue;
                var piece = col.GetComponentInParent<Piece>();
                if (piece == null) continue;
                if (RulesHits[i].distance >= bestDist) continue;
                bestDist = RulesHits[i].distance;
                best = piece;
            }
            return best;
        }
    }
}

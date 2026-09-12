using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — toolkit group (client side): reply registration + shared helpers ====================
    // Four Tools-tab sections share this file: staff chat (Wave8_ToolkitStaffChat.cs), the location
    // finder (Wave8_ToolkitLocationFinder.cs), the item forge (Wave8_ToolkitItemForge.cs) and the raid
    // composer (Wave8_ToolkitRaidComposer.cs). Each section keeps the request/reply/snapshot discipline
    // of Wave1_RapSheet.cs: RPC replies write live fields, clicks write live fields, and the draw code
    // reads only the *Layout copies pinned on the Layout pass.
    public partial class AdminPanelPlugin
    {
        // ---- Wave 8 toolkit reply registration (one patch class for the group's client replies) ----
        // Registered on ZNet.Awake, once per world join, exactly like Wave1RpcRegistration. The glue file
        // applies it with Harmony.CreateAndPatchAll(typeof(Wave8ToolkitRpcRegistration)).
        [HarmonyPatch]
        private static class Wave8ToolkitRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Each reply registers BOTH halves side by side: the network handler (gate + parse) and the
                // parse half the in-process bridge calls on a listen-server host, where the companion lives
                // in this process and there is no server peer for the gate to authenticate against.
                ZRoutedRpc.instance.Register<ZPackage>("AP_StaffChat", AchatOnStaffChat);
                AdminPanelLocalBridge.Register("AP_StaffChat", ParseStaffChat);
                ZRoutedRpc.instance.Register<ZPackage>("AP_LocTypes", LocfOnTypes);
                AdminPanelLocalBridge.Register("AP_LocTypes", ParseLocTypes);
                ZRoutedRpc.instance.Register<ZPackage>("AP_LocFind", LocfOnFind);
                AdminPanelLocalBridge.Register("AP_LocFind", ParseLocFind);
                ZRoutedRpc.instance.Register<ZPackage>("AP_RaidState", RaidcOnState);
                AdminPanelLocalBridge.Register("AP_RaidState", ParseRaidState);
            }
        }

        // ---- reachability ----

        // Reachable companion = a remote server peer OR a listen-server host, where the companion runs in
        // this process and answers locally. Only a missing ZNet or a half-open connection (no server peer
        // and we are not the server) is a real "not connected" (Wave1_RapSheet.RapLookup).
        private static bool TkReachable() =>
            ZNet.instance != null && (ServerUid() != 0L || ZNet.instance.IsServer());

        private bool TkRequireReachable()
        {
            if (TkReachable()) return true;
            Message(Loc.T("common.not_connected_srv"));
            return false;
        }

        // ---- parsing / formatting (invariant culture: the panel ships in nine languages, numbers in one) ----

        private static int TkInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static float TkFloat(string s, float fallback)
        {
            float v;
            var t = (s ?? "").Trim().Replace(',', '.');
            return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v)
                ? v : fallback;
        }

        private static string TkF(float v) => v.ToString("0", CultureInfo.InvariantCulture);
        private static string TkF1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string TkI(int v) => v.ToString(CultureInfo.InvariantCulture);

        // Map distance between two world points (Valheim's map is the XZ plane).
        private static float TkMapDist(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Compass sector from -> to. Valheim's world: +Z is north (the map's top), +X is east.
        private static readonly string[] TkCompassKeys =
        {
            "locf.dir_n", "locf.dir_ne", "locf.dir_e", "locf.dir_se", "locf.dir_s", "locf.dir_sw", "locf.dir_w", "locf.dir_nw",
        };

        private static string TkCompass(Vector3 from, Vector3 to)
        {
            var dx = to.x - from.x;
            var dz = to.z - from.z;
            if (dx * dx + dz * dz < 4f) return Loc.T("locf.dir_here");
            var ang = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (ang < 0f) ang += 360f;
            var idx = Mathf.RoundToInt(ang / 45f) % 8;
            return Loc.T(TkCompassKeys[idx]);
        }

        // Ticks are DateTime.UtcNow.Ticks written by the server; out-of-range values read as blank, never throw.
        private static string TkClock(long ticks)
        {
            if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks) return "";
            try { return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); }
            catch (Exception) { return ""; }
        }

        // Client-side teleport used by every "Teleport to" row in the group: the aimed point plus a small
        // lift so the player never lands inside the ground or a location's floor.
        private void TkTeleport(Vector3 pos, float lift, string label)
        {
            var lp = LocalPlayer;
            if (lp == null) return;
            lp.TeleportTo(pos + Vector3.up * lift, lp.transform.rotation, true);
            Message(Loc.T("locf.msg_tp", label));
        }
    }
}

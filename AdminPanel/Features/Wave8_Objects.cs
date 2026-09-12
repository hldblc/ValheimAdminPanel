using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — world objects (client side): shared plumbing ====================
    // Group OBJECTS = four Tools-tab sections that all deal with things standing in the world rather than
    // with players: the tame roster (#10), the creature editor (#11), the spawner manager (#12) and the
    // container viewer/search (#16). This file carries what they share: the reply registration (both
    // halves — network gate + in-process bridge — side by side, exactly like Wave1_Audit.cs), the camera
    // raycast that the creature editor and the chest viewer aim with, and a handful of formatting helpers.
    //
    // Member prefix: "Obj" (shared), "Tame", "Cedit", "Nest", "Chest" (per feature).
    public partial class AdminPanelPlugin
    {
        // ---- reply registration (one patch class for the whole group's client replies) ----
        // Registered on ZNet.Awake, i.e. once per world join, exactly like the main file's RpcRegistration.
        // The glue applies it with Harmony.CreateAndPatchAll(typeof(Wave8ObjectsRpcRegistration)).
        [HarmonyPatch]
        private static class Wave8ObjectsRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_TameRoster", TameOnRoster);
                AdminPanelLocalBridge.Register("AP_TameRoster", ParseTameRoster);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SpawnerList", NestOnList);
                AdminPanelLocalBridge.Register("AP_SpawnerList", ParseSpawnerList);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ChestData", ChestOnData);
                AdminPanelLocalBridge.Register("AP_ChestData", ParseChestData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ChestSearch", ChestOnSearch);
                AdminPanelLocalBridge.Register("AP_ChestSearch", ParseChestSearch);
            }
        }

        // ---- config shared by the two aiming sections ----
        private ConfigEntry<int> _objTargetRangeCfg;

        internal void ObjInit()
        {
            _objTargetRangeCfg = Config.Bind("Features", "ObjectsTargetRange", 50,
                "How far (metres) the Creature Editor and the Container viewer look for what the camera is aimed at. Clamped to 5-200.");
        }

        private float ObjTargetRange() =>
            _objTargetRangeCfg != null ? Mathf.Clamp(_objTargetRangeCfg.Value, 5, 200) : 50f;

        // Reused hit buffer: RaycastNonAlloc keeps a per-frame raycast from allocating (the build tools do
        // the same). The nearest hit is not necessarily the wanted object (a shrub, the terrain in front,
        // the admin's own body in third person), so every hit is scanned and the closest one that resolves
        // to a T and passes the filter wins.
        private static readonly RaycastHit[] ObjHits = new RaycastHit[48];

        private T ObjAim<T>(Func<T, bool> accept) where T : Component
        {
            var cam = GameCamera.instance;
            if (cam == null) return null;
            var n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, ObjHits, ObjTargetRange(),
                ~0, QueryTriggerInteraction.Ignore);
            T best = null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var col = ObjHits[i].collider;
                if (col == null) continue;
                var c = col.GetComponentInParent<T>();
                if (c == null) continue;
                if (accept != null && !accept(c)) continue;
                if (ObjHits[i].distance >= bestDist) continue;
                bestDist = ObjHits[i].distance;
                best = c;
            }
            return best;
        }

        // ---- formatting (invariant culture: these are numbers, not prose) ----

        private static string ObjPrefabName(GameObject go)
        {
            if (go == null) return "";
            var n = go.name;
            if (string.IsNullOrEmpty(n)) return "";
            var idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx > 0 ? n.Substring(0, idx) : n;
        }

        private static string ObjPos(Vector3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", p.x, p.y, p.z);

        private static string ObjDist(float d) =>
            d < 1000f
                ? string.Format(CultureInfo.InvariantCulture, "{0:0} m", d)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0} km", d / 1000f);

        private static string ObjHealth(float cur, float max) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0}", cur, max);

        // Star level as the game shows it: level 1 = no stars, level 2 = one star ...
        private static string ObjStars(int level)
        {
            var stars = level - 1;
            return stars <= 0 ? "-" : new string('★', Mathf.Clamp(stars, 1, 9));
        }

        private static float ObjParseFloat(string s, float fallback)
        {
            float v;
            return float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static int ObjParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        // Same offset the World tab's player teleport uses; distant = true so the loading screen covers a
        // long jump instead of dropping the admin into unloaded terrain.
        // The toast reuses the World tab's own "Teleporting to {0}" line; the "not in a world" line lives
        // under the tame prefix because this helper's home file has no prefix of its own.
        private void ObjTeleport(Vector3 pos, string label)
        {
            var player = LocalPlayer;
            if (player == null) { Message(Loc.T("tame.msg_not_in_world")); return; }
            player.TeleportTo(pos + Vector3.up, player.transform.rotation, true);
            Message(Loc.T("world.msg_tp_to", (label ?? "") + " (" + ObjPos(pos) + ")"));
        }

        // Reachable companion = a remote server peer OR a listen-server host (the companion runs in this
        // process and answers locally). Only a missing ZNet or a half-open connection is "not connected".
        private bool ObjReachable()
        {
            if (ZNet.instance == null || (ServerUid() == 0L && !ZNet.instance.IsServer()))
            {
                Message(Loc.T("common.not_connected_srv"));
                return false;
            }
            return true;
        }

        // Silent variant for Layout-pass polls, which must never toast.
        private static bool ObjCanPoll() =>
            ZNet.instance != null && (ServerUid() != 0L || ZNet.instance.IsServer());
    }
}

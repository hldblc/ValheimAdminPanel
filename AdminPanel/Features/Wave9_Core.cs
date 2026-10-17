using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 (wave 9) — reply registration + shared client helpers ====================
    // Every round-3 card keeps the request/reply/snapshot discipline of Wave1_RapSheet.cs: RPC replies and clicks
    // write live fields, the draw code reads only the *Layout copies pinned on the Layout pass. Replies register
    // BOTH halves side by side — the network handler (SenderIsServerReply gate + parse) and the parse half the
    // in-process bridge calls on a listen-server host (AdminPanelLocalBridge).
    public partial class AdminPanelPlugin
    {
        [HarmonyPatch]
        private static class Wave9RpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                var r = ZRoutedRpc.instance;
                if (r == null) return;
                W9Reg(r, ZownRegisterReplies);
                W9Reg(r, OvlRegisterReplies);
                W9Reg(r, ProvRegisterReplies);
                W9Reg(r, ProgRegisterReplies);
                W9Reg(r, WdiffRegisterReplies);
                W9Reg(r, BasesRegisterReplies);
                W9Reg(r, LocrRegisterReplies);
                W9Reg(r, RzoneRegisterReplies);
                W9Reg(r, TrigRegisterReplies);
                W9Reg(r, UndoRegisterReplies);
                W9Reg(r, SefRegisterReplies);
                W9Reg(r, EncRegisterReplies);
            }
        }

        private static void W9Reg(ZRoutedRpc r, Action<ZRoutedRpc> register)
        {
            try { register(r); }
            catch (Exception e) { Instance?.Logger.LogWarning($"Round 3 reply registration failed ({register.Method.Name}): {e.Message}"); }
        }

        /// <summary>Register one server reply: network form (gated) + local bridge form (host).</summary>
        private static void W9Reply(ZRoutedRpc r, string name, Action<AdminPanelPlugin, ZPackage> parse)
        {
            r.Register<ZPackage>(name, (sender, pkg) =>
            {
                var self = Instance;
                if (self == null || !SenderIsServerReply(sender)) return;
                try { parse(self, pkg); }
                catch (Exception e) { self.Logger.LogWarning($"{name} parse failed: {e.Message}"); }
            });
            AdminPanelLocalBridge.Register(name, parse);
        }

        // ---- requests ----

        private const float W9NoAnswerSeconds = 12f;

        private static ZPackage W9Pkg()
        {
            var p = new ZPackage();
            p.Write(1);   // wire version
            return p;
        }

        private bool W9Send(string rpc, ZPackage pkg)
        {
            if (!TkReachable()) { Message(Loc.T("common.not_connected_srv")); return false; }
            SrvRpc(rpc, pkg);
            return true;
        }

        private bool W9Send(string rpc)
        {
            if (!TkReachable()) { Message(Loc.T("common.not_connected_srv")); return false; }
            SrvRpc(rpc);
            return true;
        }

        /// <summary>Hint key for a request that is idle, waiting, or has gone unanswered (older companion?).</summary>
        private static string W9WaitKey(bool pending, float askedAt, string idleKey) =>
            !pending ? idleKey : (Time.time - askedAt > W9NoAnswerSeconds ? "w9.no_answer" : "w9.waiting");

        // ---- formatting ----

        private static string W9Ago(long ticksUtc)
        {
            if (ticksUtc <= 0L || ticksUtc > DateTime.MaxValue.Ticks) return Loc.T("w9.never");
            var secs = (DateTime.UtcNow - new DateTime(ticksUtc, DateTimeKind.Utc)).TotalSeconds;
            if (secs < 0) secs = 0;
            if (secs >= 2 * 86400d) return Loc.T("w9.days_ago", ((long)(secs / 86400d)).ToString(CultureInfo.InvariantCulture));
            return Loc.T("w9.ago", FormatAgo(secs));
        }

        private static string W9Date(long ticksUtc)
        {
            if (ticksUtc <= 0L || ticksUtc > DateTime.MaxValue.Ticks) return "-";
            try { return new DateTime(ticksUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
            catch (Exception) { return "-"; }
        }

        private static string W9Pos(Vector3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}", p.x, p.z);

        private static string W9Short(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, Mathf.Max(1, max - 2)) + "..";
        }

        private static string[] W9Unpack(string s, int min)
        {
            var parts = (s ?? "").Split('|');
            if (parts.Length >= min) return parts;
            var res = new string[min];
            for (var i = 0; i < min; i++) res[i] = i < parts.Length ? parts[i] : "";
            return res;
        }

        private static long W9Long(string s, long fallback)
        {
            long v;
            return long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        // ---- position pickers shared by zone/location/command cards ----

        private static System.Reflection.MethodInfo _w9ScreenToWorld;
        private static bool _w9ScreenToWorldProbed;

        /// <summary>The world point under the mouse while the large map is open, else false.</summary>
        private static bool W9MapCursor(out Vector3 world)
        {
            world = Vector3.zero;
            try
            {
                var mm = Minimap.instance;
                if (mm == null || !Minimap.IsOpen()) return false;
                if (!_w9ScreenToWorldProbed)
                {
                    _w9ScreenToWorldProbed = true;
                    _w9ScreenToWorld = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
                }
                if (_w9ScreenToWorld == null) return false;
                world = (Vector3)_w9ScreenToWorld.Invoke(mm, new object[] { Input.mousePosition });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>What the admin's crosshair points at (the camera ray), within <paramref name="range"/> metres.</summary>
        private static bool W9Crosshair(float range, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return false;
            var mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain",
                "vehicle", "character", "character_net", "character_ghost", "hitbox", "character_noenv", "item");
            return Physics.Raycast(cam.position, cam.forward, out hit, range, mask);
        }

        /// <summary>The networked object behind a raycast hit (walks up to the ZNetView), or null.</summary>
        private static ZNetView W9ViewOf(RaycastHit hit)
        {
            if (hit.collider == null) return null;
            var nv = hit.collider.GetComponentInParent<ZNetView>();
            return nv != null && nv.IsValid() ? nv : null;
        }

        // ---- unsaved map pins ----
        // Minimap.AddPin's trailing optional parameter is a PlatformUserID from the Splatform assembly, which this
        // project does not reference (same reasoning as Wave8SrvRulesClient.MinimapAddPin): call it by reflection,
        // passing the five real arguments and every further parameter's declared default.
        private static System.Reflection.MethodInfo _w9AddPin;
        private static bool _w9AddPinProbed;

        private static Minimap.PinData W9AddPin(Minimap map, Vector3 pos, Minimap.PinType type, string name)
        {
            if (map == null) return null;
            if (!_w9AddPinProbed)
            {
                _w9AddPinProbed = true;
                try { _w9AddPin = AccessTools.Method(typeof(Minimap), "AddPin"); } catch (Exception) { }
            }
            if (_w9AddPin == null) return null;
            var ps = _w9AddPin.GetParameters();
            if (ps.Length < 5) return null;
            var args = new object[ps.Length];
            args[0] = pos;
            args[1] = type;
            args[2] = name ?? "";
            args[3] = false;   // save: never written into the player's map data
            args[4] = false;   // isChecked
            for (var i = 5; i < ps.Length; i++)
            {
                object dflt = null;
                try { if (ps[i].HasDefaultValue) dflt = ps[i].DefaultValue; } catch (Exception) { }
                if (dflt == null && ps[i].ParameterType.IsValueType) dflt = Activator.CreateInstance(ps[i].ParameterType);
                args[i] = dflt;
            }
            return _w9AddPin.Invoke(map, args) as Minimap.PinData;
        }

        // ---- small shared widgets ----

        /// <summary>One labelled text field on its own line (label column 120 px).</summary>
        private string W9Field(string labelKey, string value, int maxLen, float width)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), _labelStyle, GUILayout.MinWidth(120));
            var v = GUILayout.TextField(value ?? "", maxLen, _textFieldStyle, GUILayout.Width(width));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return v;
        }
    }
}

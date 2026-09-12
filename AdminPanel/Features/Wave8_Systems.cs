using System;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — SYSTEMS group (client side, shared) ====================
    // The four systems cards (Death rules, Bounties, Self-update, Client perf) each live in their own file;
    // this one owns the ONE reply-registration class for the group and the two helpers every card uses: the
    // reachability test for button sends and the Layout-gated, throttle-first poll gate.
    //
    // IMGUI discipline is the same as every other wave: RPC replies land during ZNet.Update and write the
    // live payload fields; each card pins its own *Layout snapshots on the Layout pass and draws ONLY from
    // those, so the control count of a frame can never change between its Layout and Repaint passes.
    public partial class AdminPanelPlugin
    {
        // Registered on ZNet.Awake, once per world join, exactly like Wave1RpcRegistration. Each reply
        // registers BOTH halves side by side: the network handler (sender gate + parse) and the parse half
        // the in-process bridge calls on a listen-server host, where there is no server peer to gate on.
        [HarmonyPatch]
        private static class Wave8SystemsRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_DeathRulesData", DeathOnData);
                AdminPanelLocalBridge.Register("AP_DeathRulesData", ParseDeathRulesData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_BountyData", BountyOnData);
                AdminPanelLocalBridge.Register("AP_BountyData", ParseBountyData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_UpdateState", SelfupOnState);
                AdminPanelLocalBridge.Register("AP_UpdateState", ParseUpdateState);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ClientPerf", PcensusOnData);
                AdminPanelLocalBridge.Register("AP_ClientPerf", ParseClientPerf);
            }
        }

        // Reachable companion = a remote server peer OR a listen-server host, where the companion runs in
        // this process and answers locally. Only a missing ZNet or a half-open connection (no server peer
        // and we are not the server) is a real "not connected". Used before every button send.
        private bool SysReachable()
        {
            if (ZNet.instance != null && (ServerUid() != 0L || ZNet.instance.IsServer())) return true;
            Message(Loc.T("common.not_connected_srv"));
            return false;
        }

        // Poll gate for the Layout pass: throttle-FIRST (set before sending), so a companion without the
        // handler can never turn a card into a request loop. hostLayout is the card's own Layout snapshot of
        // ZNet.IsServer(), because this only ever runs on that pass.
        private static bool SysPollDue(ref float next, float every, bool hostLayout)
        {
            if (ZNet.instance == null) return false;
            if (ServerUid() == 0L && !hostLayout) return false;
            if (Time.time < next) return false;
            next = Time.time + every;
            return true;
        }

        // Server-stamped DateTime.UtcNow.Ticks -> "just now" / "5 min ago" / "never". The keys are passed by
        // the caller so each card keeps its own locale prefix.
        private static string SysAgo(long ticksUtc, string kNever, string kNow, string kMin, string kHour, string kDay)
        {
            if (ticksUtc <= 0L) return Loc.T(kNever);
            var delta = DateTime.UtcNow.Ticks - ticksUtc;
            if (delta < TimeSpan.TicksPerMinute) return Loc.T(kNow);
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T(kMin, mins);
            if (mins < 1440L) return Loc.T(kHour, mins / 60L);
            return Loc.T(kDay, mins / 1440L);
        }
    }
}

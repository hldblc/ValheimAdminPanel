using System;
using System.Collections.Generic;

namespace AdminPanel
{
    // ==================== In-process reply bridge (listen-server host only) ====================
    // On a host the companion runs in THIS process, so a "server reply" never crosses a network. Routing it
    // through ZRoutedRpc anyway would force the panel to decide whether to trust the packet's sender, and on
    // a host there is no server peer to compare against (ServerUid() is structurally 0) — which is exactly
    // why every server-truth section rendered empty for a hosting admin.
    //
    // Rather than relax SenderIsServerReply (that gate is what stops a peer forging "server truth", and it
    // stays exactly as 2.4.0 hardened it), the companion hands the payload straight to the parser through
    // this class. There is no packet and no sender, so there is nothing to spoof: a remote player has no way
    // to reach a direct method call inside our own process.
    //
    // Shape: each reply handler keeps its network form (gate + parse) and registers its PARSE half here.
    // The network path and the local path therefore run identical parsing code.
    public static class AdminPanelLocalBridge
    {
        // name -> parser. Populated by each wave file's registration class at world join, alongside its
        // ZRoutedRpc.Register call, so the two can never drift apart.
        private static readonly Dictionary<string, Action<AdminPanelPlugin, ZPackage>> Parsers =
            new Dictionary<string, Action<AdminPanelPlugin, ZPackage>>(StringComparer.Ordinal);

        internal static void Register(string name, Action<AdminPanelPlugin, ZPackage> parse)
        {
            if (string.IsNullOrEmpty(name) || parse == null) return;
            Parsers[name] = parse;   // last registration wins; re-registering on rejoin is expected
        }

        /// <summary>
        /// Called BY THE COMPANION, in-process, when it would otherwise have routed a reply to the local
        /// host. Public because the companion resolves it by reflection (the two assemblies deliberately do
        /// not reference each other — the panel must still load when the companion is absent).
        /// </summary>
        public static bool LocalReply(string name, ZPackage pkg)
        {
            var self = AdminPanelPlugin.Instance;
            if (self == null || pkg == null || string.IsNullOrEmpty(name)) return false;
            // Only meaningful while we are the server. If a future caller ever reached this on a pure
            // client, the network path is the only legitimate route and this must stay shut.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;

            Action<AdminPanelPlugin, ZPackage> parse;
            if (!Parsers.TryGetValue(name, out parse)) return false;
            try
            {
                pkg.SetPos(0);   // the companion may have written and not rewound
                parse(self, pkg);
                return true;
            }
            catch (Exception e)
            {
                // A malformed local payload must never take down the companion's own handler.
                self.LogBridgeError(name, e);
                return false;
            }
        }
    }

    public partial class AdminPanelPlugin
    {
        internal void LogBridgeError(string name, Exception e) =>
            Logger.LogWarning($"Local reply {name} failed to parse: {e.Message}");
    }
}

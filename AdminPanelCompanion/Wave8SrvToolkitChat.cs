using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #9 admin-only chat channel (server side) ====================
    // Staff-to-staff text that ordinary players never see. Two ways in:
    //   * the panel: AP_SrvStaffChat(string) — chokepoint-audited, moderator grant;
    //   * in-game chat: "!a <text>", registered with Wave1Chat's '!' registry. The chokepoint there
    //     consumes the packet, so the line never reaches the public channel; a non-admin gets "not staff".
    // One way out: AP_StaffChat(pkg) pushed to every online admin (adminlist membership by socket host id,
    // exactly the check the join gates use) plus the listen-server host through ReplyTo — a host has no
    // server peer, so a routed packet to it would be thrown away by the panel's anti-spoof gate.
    //
    // Memory: a 100-line ring buffer, seeded from the tail of <world>/staffchat.log so history survives a
    // restart. Wire format (both the live push and the history backfill use it):
    //   int ver=1 | bool replace | int n (<=100) | n x { long ticksUtc, string senderId, string name, string text }
    // replace=true carries the whole ring (answer to AP_SrvStaffChatReq); replace=false appends.
    internal static class Wave8ToolkitChat
    {
        private const int Ver = 1;
        private const int RingCap = 100;      // wire contract: n <= 100
        private const int MaxTextLen = 200;
        private const int MaxNameLen = 40;
        private const int MaxIdLen = 64;
        private const string LogName = "staffchat";

        private static ConfigEntry<bool> _enableCfg;
        private static bool _inited;

        private sealed class Line
        {
            public long Ticks;
            public string Id;
            public string Name;
            public string Text;
        }

        private static readonly List<Line> Ring = new List<Line>();
        // FeatureStore data dir the ring was seeded from. A listen-server host can load a different world
        // in the same process; a change here drops the old world's lines instead of leaking them across.
        private static string _seededDir;

        internal static bool Enabled => _enableCfg == null || _enableCfg.Value;

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _enableCfg = cfg.Bind("Features", "EnableStaffChat", true,
                    "Admin-only chat channel: the panel's Staff Chat card and the '!a <text>' chat command relay text to online admins only, and every line is appended to <world>/staffchat.log. Passive for ordinary players. The '!a' command is claimed at startup (restart to apply); the panel path reads this switch live.");

            // The '!' word is claimed only while the channel is enabled at startup, so an operator who turns
            // it off gets chat exactly as before (a "!a hi" line stays public) — the Wave6 rule.
            if (!Enabled) return;
            try { Wave1Chat.RegisterChatCommand("a", CmdStaff); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8: '!a' chat command registration failed: {e.Message}"); }
        }

        // ==================== inputs ====================

        // Panel path. The chokepoint has already written the audit row and enforced the role; the text
        // itself goes to staffchat.log, so no second audit line is written here.
        internal static void OnStaffChat(long sender, string text)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvStaffChat")) return;
            if (!Enabled)
            {
                CompanionPlugin.NotifySender(sender, "Staff chat is disabled on this server (Features.EnableStaffChat).");
                return;
            }
            var clean = Wave8Toolkit.CleanText(text, MaxTextLen);
            if (clean.Length == 0) return;
            try { Post(sender, clean); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvStaffChat failed: {e.Message}"); }
        }

        // Chat path ("!a <text>"). The host never reaches here (its chat bypasses RPC_RoutedRPC), so the
        // admin check is the peer-based one; a non-admin's attempt is answered on the vanilla path because
        // their client may run nothing of ours.
        private static void CmdStaff(long sender, string args)
        {
            if (!Enabled)
            {
                Wave8Toolkit.SendTopLeft(sender, "Staff chat is disabled on this server.");
                return;
            }
            if (!CompanionPlugin.FeatureSenderIsAdmin(sender))
            {
                Wave8Toolkit.SendTopLeft(sender, "Staff chat: you are not staff.");
                return;
            }
            var clean = Wave8Toolkit.CleanText(args, MaxTextLen);
            if (clean.Length == 0)
            {
                Wave8Toolkit.SendTopLeft(sender, "Usage: !a <message> (admins only)");
                return;
            }
            Post(sender, clean);
        }

        // History backfill, sent by the panel once when the card opens. Not an audited RPC (see
        // Wave8Toolkit.Init), so BuiltinRoles carries no grant for its name — a moderator is recognised
        // through the send grant they already hold, the same trick Wave1Chat.OnSrvChatLogReq uses.
        internal static void OnStaffChatReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvStaffChatReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvStaffChat")) return;
            try
            {
                Seed();
                CompanionPlugin.ReplyTo(sender, "AP_StaffChat", Build(true, Ring));
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvStaffChatReq failed: {e.Message}"); }
        }

        // ==================== core ====================

        private static void Post(long sender, string text)
        {
            Seed();
            var line = new Line
            {
                Ticks = DateTime.UtcNow.Ticks,
                Id = Field(CompanionPlugin.SenderPlatformId(sender), MaxIdLen),
                Name = Field(CompanionPlugin.SenderDisplayName(sender), MaxNameLen),
                Text = text,
            };
            Ring.Add(line);
            while (Ring.Count > RingCap) Ring.RemoveAt(0);

            // Only the trailing text field may contain '/'-folded pipes; the three leading fields were
            // cleaned by Field(). Append is best-effort — a full disk must never silence the channel.
            try
            {
                FeatureStore.Append(LogName,
                    line.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + line.Id + "|" + line.Name + "|" + line.Text);
            }
            catch (Exception) { }

            Deliver(line);
            CompanionPlugin.FeatureLog($"[staff] {line.Name}: {line.Text}");
        }

        // Push one line to every online admin. Admins whose client is known NOT to run the companion can
        // never receive AP_StaffChat, so they get a vanilla top-left toast instead — still staff-only,
        // because it is addressed per peer. Unknown capability (still probing) sends the panel packet; an
        // unmodded client ignores an unknown RPC name silently.
        private static void Deliver(Line line)
        {
            var pkg = Build(false, new List<Line> { line });
            var vanillaText = "[Staff] " + line.Name + ": " + line.Text;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;
                    if (!CompanionPlugin.FeatureIsAdminId(peer.m_socket.GetHostName())) continue;
                    if (Wave34Core.HasMod(peer.m_uid) == false) Wave8Toolkit.SendTopLeft(peer.m_uid, vanillaText);
                    else ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, "AP_StaffChat", pkg);
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Wave8: staff chat delivery failed: {e.Message}"); }

            // A listen-server host is implicitly admin and never in m_peers. Only a host with a local
            // Player is a candidate: a dedicated server has nobody to show it to, and ReplyTo would then
            // just route the packet to itself where no handler listens.
            if (Player.m_localPlayer != null && ZDOMan.instance != null)
            {
                try { CompanionPlugin.ReplyTo(ZDOMan.GetSessionID(), "AP_StaffChat", pkg); }
                catch (Exception) { }
            }
        }

        private static ZPackage Build(bool replace, List<Line> lines)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(replace);
            var n = Math.Min(lines.Count, RingCap);
            pkg.Write(n);
            for (var i = lines.Count - n; i < lines.Count; i++)
            {
                var l = lines[i];
                pkg.Write(l.Ticks);
                pkg.Write(l.Id ?? "");
                pkg.Write(l.Name ?? "");
                pkg.Write(l.Text ?? "");
            }
            return pkg;
        }

        // Seed the ring from the log's tail once per world. FeatureStore.DataDir is null until a world is
        // resolvable; in that case nothing is seeded yet and the next call retries.
        private static void Seed()
        {
            string dir;
            try { dir = FeatureStore.DataDir; }
            catch (Exception) { dir = null; }
            if (dir == null || dir == _seededDir) return;
            _seededDir = dir;
            Ring.Clear();
            List<string> tail;
            try { tail = FeatureStore.Tail(LogName, RingCap); }
            catch (Exception) { return; }
            foreach (var raw in tail)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                // ticks|id|name|text — the text is last and keeps any '|' it contains (bounded split).
                var p = raw.Split(new[] { '|' }, 4);
                if (p.Length < 4) continue;
                long ticks;
                if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                Ring.Add(new Line
                {
                    Ticks = ticks,
                    Id = Field(p[1], MaxIdLen),
                    Name = Field(p[2], MaxNameLen),
                    Text = Wave8Toolkit.CleanText(p[3], MaxTextLen),
                });
            }
            while (Ring.Count > RingCap) Ring.RemoveAt(0);
        }

        private static string Field(string s, int max) =>
            string.IsNullOrEmpty(s) ? "?" : Wave8Toolkit.CleanText(s, max);
    }
}

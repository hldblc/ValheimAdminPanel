using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #25 Client performance census (client reporter + server roster) ====================
    // "The server is lagging" is usually one client. Every MODDED client reports, once per PerfReportSeconds,
    // its average and minimum FPS over the window, its ping to the server, the companion version and its
    // BepInEx plugin list; the server keeps the latest report per peer in memory and hands the roster to the
    // panel on request. Unmodded clients never appear — the admin card says so.
    //
    //   FPS      frames are counted in Tick (Update runs once per rendered frame on a client); the average is
    //            frames / window seconds, the minimum is the lowest one-second frame count in the window.
    //   PING     the dedicated-server build stubs ZNet.GetNetStats (verified by decompiling), so the reporter
    //            asks the server peer's socket directly: ISocket.GetConnectionQuality(out ..., out int ping,
    //            ...) is the Steam/PlayFab round-trip in ms. GetNetStats is the second try, and a reflective
    //            read of an "m_averagePing" field on the peer's ZRpc the last (no current build has one;
    //            -1 = unknown).
    //   MODS     Wave7Guard already collects the plugin list from Chainloader.PluginInfos for AP_ModList; its
    //            collector is reused reflectively (it is private there) with an equivalent local fallback.
    //
    // AP_PerfReport is a CLIENT-authored packet on an open bus: the server accepts it only from a connected
    // peer, at most one per 20 s per sender, with every list bounded (60 entries x 80 chars) — and stores
    // nothing on disk.
    internal static class Wave8SystemsPerf
    {
        private const int Ver = 1;
        private const int MaxMods = 60;
        private const int MaxModLen = 80;
        private const int MaxVersionLen = 32;
        private const int MaxNameLen = 60;
        private const int MaxIdLen = 64;
        private const float MinReportInterval = 20f;   // server-side: anything faster is dropped
        private const int ShipCap = 100;               // wire contract: rows <= 100

        private static ConfigEntry<int> _reportSecondsCfg;
        private static int ReportSeconds => _reportSecondsCfg != null ? Mathf.Clamp(_reportSecondsCfg.Value, 30, 300) : 60;

        // ---- client reporter ----
        private static float _windowStart;
        private static int _frames;
        private static float _secondStart;
        private static int _secondFrames;
        private static int _minFpsSecond = int.MaxValue;
        private static float _nextReport;
        private static MethodInfo _pluginListMi;
        private static bool _pluginListProbed;
        private static FieldInfo _avgPingField;
        private static bool _avgPingProbed;

        // ---- server roster (memory only) ----
        private sealed class ClientPerf
        {
            public string Name;
            public string Id;
            public int AvgFps;
            public int MinFps;
            public int Ping;
            public string Version;
            public float ReceivedAt;
            public List<string> Mods;
        }

        private static readonly Dictionary<long, ClientPerf> Reports = new Dictionary<long, ClientPerf>();
        private static bool _inited;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _reportSecondsCfg = cfg.Bind("Features", "PerfReportSeconds", 60,
                    new ConfigDescription("CLIENT setting: how often this client reports its FPS, ping, companion version and plugin list to the server it is connected to (the server shows them in the panel's Client Perf card). Passive.",
                        new AcceptableValueRange<int>(30, 300)));
            // AP_PerfReport / AP_SrvClientPerfReq are plumbing and a timed read: neither is audited.
        }

        // Every frame. The frame counters cost two increments; everything else waits for the report timer.
        internal static void Tick()
        {
            if (!_inited) return;
            var now = Time.unscaledTime;
            if (_windowStart <= 0f) ResetWindow(now);
            _frames++;
            _secondFrames++;
            if (now - _secondStart >= 1f)
            {
                if (_secondFrames < _minFpsSecond) _minFpsSecond = _secondFrames;
                _secondFrames = 0;
                _secondStart = now;
            }
            if (now < _nextReport) return;
            _nextReport = now + ReportSeconds;

            // Only a CLIENT connected to a remote server reports: a dedicated server has no Player, and a
            // listen-server host is the server (its "ping" would be to itself).
            var znet = ZNet.instance;
            if (Player.m_localPlayer == null || znet == null || znet.IsServer()) { ResetWindow(now); return; }
            var sp = znet.GetServerPeer();
            if (sp == null || sp.m_uid == 0L) { ResetWindow(now); return; }

            var elapsed = now - _windowStart;
            var avg = elapsed > 0.5f ? Mathf.RoundToInt(_frames / elapsed) : 0;
            var min = _minFpsSecond == int.MaxValue ? avg : _minFpsSecond;
            var ping = ReadPing(sp);
            ResetWindow(now);

            try
            {
                var mods = LocalPluginList();
                var pkg = new ZPackage();
                pkg.Write(Ver);
                pkg.Write(avg);
                pkg.Write(min);
                pkg.Write(ping);
                pkg.Write(Wave8Systems.Clamp(CompanionPlugin.PluginVersion ?? "", MaxVersionLen));
                pkg.Write(mods.Count);
                for (var i = 0; i < mods.Count; i++) pkg.Write(mods[i]);
                ZRoutedRpc.instance?.InvokeRoutedRPC(sp.m_uid, "AP_PerfReport", pkg);
            }
            catch (Exception) { /* a census report must never break a client */ }
        }

        private static void ResetWindow(float now)
        {
            _windowStart = now;
            _frames = 0;
            _secondStart = now;
            _secondFrames = 0;
            _minFpsSecond = int.MaxValue;
        }

        // ms round-trip to the server, or -1 when no transport exposes one.
        private static int ReadPing(ZNetPeer sp)
        {
            try
            {
                if (sp.m_socket != null)
                {
                    float lq, rq, ob, ib; int ping;
                    sp.m_socket.GetConnectionQuality(out lq, out rq, out ping, out ob, out ib);
                    if (ping > 0) return ping;
                }
            }
            catch (Exception) { }
            try
            {
                float lq, rq, ob, ib; int ping;
                ZNet.instance.GetNetStats(out lq, out rq, out ping, out ob, out ib);
                if (ping > 0) return ping;
            }
            catch (Exception) { }
            try
            {
                if (!_avgPingProbed)
                {
                    _avgPingProbed = true;
                    _avgPingField = typeof(ZRpc).GetField("m_averagePing", AccessTools.all);   // gone in 1.0.12; GetNetStats above is the primary path
                }
                if (_avgPingField != null && sp.m_rpc != null)
                {
                    var v = _avgPingField.GetValue(sp.m_rpc);
                    if (v is float f && f > 0f) return Mathf.RoundToInt(f * (f < 10f ? 1000f : 1f));   // seconds vs ms: nobody has a 10 s ping
                    if (v is int n && n > 0) return n;
                }
            }
            catch (Exception) { }
            return -1;
        }

        // "guid version" per plugin, sorted, capped. Wave7Guard.LocalPluginList (private) is the shared
        // Chainloader reader; the fallback is the same read inlined so a rename there costs nothing here.
        private static List<string> LocalPluginList()
        {
            var res = new List<string>();
            try
            {
                if (!_pluginListProbed)
                {
                    _pluginListProbed = true;
                    _pluginListMi = AccessTools.Method(typeof(Wave7Guard), "LocalPluginList");
                }
                if (_pluginListMi != null)
                {
                    var list = _pluginListMi.Invoke(null, null) as List<KeyValuePair<string, string>>;
                    if (list != null)
                    {
                        foreach (var kv in list)
                        {
                            if (res.Count >= MaxMods) break;
                            res.Add(Wave8Systems.Clamp(string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Key + " " + kv.Value, MaxModLen));
                        }
                        return res;
                    }
                }
            }
            catch (Exception) { res.Clear(); }

            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (infos == null) return res;
                foreach (var kv in infos)
                {
                    if (res.Count >= MaxMods) break;
                    var meta = kv.Value != null ? kv.Value.Metadata : null;
                    var guid = meta != null && !string.IsNullOrEmpty(meta.GUID) ? meta.GUID : kv.Key;
                    if (string.IsNullOrEmpty(guid)) continue;
                    var ver = "";
                    try { ver = meta != null && meta.Version != null ? meta.Version.ToString() : ""; }
                    catch (Exception) { }
                    res.Add(Wave8Systems.Clamp(ver.Length == 0 ? guid : guid + " " + ver, MaxModLen));
                }
                res.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { res.Clear(); }
            return res;
        }

        // ==================== server side ====================

        internal static void OnPeerLeft(long uid)
        {
            Reports.Remove(uid);
        }

        // The sender is trustworthy AS AN IDENTITY (sanitized peer uid); the content is not — bounded and
        // stripped of the separators any downstream text might use, stored in memory only.
        internal static void OnPerfReport(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null || peer.m_socket == null) return;   // not a connected peer (the host never reports)
            var now = Time.unscaledTime;
            ClientPerf prev;
            if (Reports.TryGetValue(sender, out prev) && now - prev.ReceivedAt < MinReportInterval) return;

            int ver, avg, min, ping, n; string version;
            var mods = new List<string>();
            try
            {
                ver = pkg.ReadInt();
                if (ver != Ver) return;
                avg = pkg.ReadInt();
                min = pkg.ReadInt();
                ping = pkg.ReadInt();
                version = Wave8Systems.CleanText(pkg.ReadString(), MaxVersionLen);
                n = pkg.ReadInt();
                if (n < 0 || n > MaxMods) return;
                for (var i = 0; i < n; i++)
                {
                    var m = Wave8Systems.CleanText(pkg.ReadString(), MaxModLen);
                    if (m.Length > 0) mods.Add(m);
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_PerfReport: malformed packet dropped ({e.Message})"); return; }

            Reports[sender] = new ClientPerf
            {
                Name = Wave8Systems.CleanText(peer.m_playerName, MaxNameLen),
                Id = Wave8Systems.CleanText(peer.m_socket.GetHostName(), MaxIdLen),
                AvgFps = Mathf.Clamp(avg, 0, 10000),
                MinFps = Mathf.Clamp(min, 0, 10000),
                Ping = Mathf.Clamp(ping, -1, 60000),
                Version = version,
                ReceivedAt = now,
                Mods = mods,
            };
        }

        // Timed read (not audited): the roster is server observation, not an admin action.
        // AP_ClientPerf (v1): int ver, string serverVersion, int reportSeconds, int rows(<=100) x
        // (long uid, string name, string id, int avgFps, int minFps, int ping, string version, int ageSeconds,
        //  int mods(<=60) x string).
        internal static void OnClientPerfReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvClientPerfReq")) return;

            // Drop rows for peers that are gone (belt and braces next to the leave hook).
            List<long> dead = null;
            foreach (var kv in Reports)
                if (!Wave8Systems.PeerConnected(kv.Key)) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null) foreach (var uid in dead) Reports.Remove(uid);

            var rows = new List<KeyValuePair<long, ClientPerf>>(Reports);
            rows.Sort((a, b) => string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase));
            var now = Time.unscaledTime;

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(CompanionPlugin.PluginVersion ?? "");
            pkg.Write(ReportSeconds);
            var n = rows.Count < ShipCap ? rows.Count : ShipCap;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i].Value;
                pkg.Write(rows[i].Key);
                pkg.Write(r.Name ?? "");
                pkg.Write(r.Id ?? "");
                pkg.Write(r.AvgFps);
                pkg.Write(r.MinFps);
                pkg.Write(r.Ping);
                pkg.Write(r.Version ?? "");
                pkg.Write(Mathf.Max(0, Mathf.RoundToInt(now - r.ReceivedAt)));
                var m = r.Mods != null ? Math.Min(r.Mods.Count, MaxMods) : 0;
                pkg.Write(m);
                for (var j = 0; j < m; j++) pkg.Write(r.Mods[j] ?? "");
            }
            CompanionPlugin.ReplyTo(sender, "AP_ClientPerf", pkg);
        }
    }
}

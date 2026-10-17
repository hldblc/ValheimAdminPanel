using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #10 ZDO ownership census + lag-owner hand-off (server) ====================
    // Who simulates what. Every networked object (ZDO) in a loaded area has an OWNER peer that runs its physics/AI
    // and streams it to everyone else; a high-ping player who owns a busy base lags every visitor of that base.
    //
    // Engine fact (ZDOMan.ReleaseZDOS, decompile-1.0.20 ZDOMan.cs:931-986): every 2 s the server walks itself and
    // then each peer in m_peers order; a peer KEEPS a ZDO while it stays in that peer's active area, and an
    // unowned ZDO (or one whose owner left its area) goes to the first peer in the loop whose area covers it. So a
    // hand-off sticks only while the new owner is near; "release" lets the engine re-elect immediately. A dedicated
    // server has no meaningful reference position, so "give to the server" would just be a release - the card
    // offers Release and the in-range peers instead.
    //
    //   AP_SrvZownReq()                 -> frame-spread census -> AP_ZownData
    //   AP_SrvZownMove(ZPackage)        {ver, Vector3 center, float radius, long newOwner(0 = release)} -> AP_Msg + audit
    //
    // AP_ZownData: ver | long scanMs | int scanned | int owned | int unowned | int n(<=40) |
    //   n x { long uid, string name, string platformId, int ping, int count, Vector3 hotPos, int hotCount,
    //         int zones, int nCand(<=3), nCand x { long uid, string name, int ping } }
    internal static class Wave9Zown
    {
        private const string Label = "zown";
        private const int RowCap = 40;
        private const int CandCap = 3;
        private const float MaxMoveRadius = 96f;

        private sealed class OwnerAcc
        {
            public long Uid;
            public int Count;
            public readonly Dictionary<Vector2s, int> Zones = new Dictionary<Vector2s, int>();
        }

        private static Dictionary<long, OwnerAcc> _acc;
        private static int _owned, _unowned;
        private static readonly List<long> Waiting = new List<long>();

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvZownReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvZownMove", "moderator");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvZownReq", new Action<long>(OnReq));
            r.Register<ZPackage>("AP_SrvZownMove", OnMove);
        }

        // ==================== census ====================

        private static void OnReq(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvZownReq")) return;
            if (!Waiting.Contains(sender)) Waiting.Add(sender);
            if (Wave9Scan.Busy(Label)) return;   // the running census answers everyone waiting
            _acc = new Dictionary<long, OwnerAcc>();
            _owned = _unowned = 0;
            if (!Wave9Scan.Start(Label, sender, Visit, Done)) Waiting.Remove(sender);
        }

        private static void Visit(ZDO zdo)
        {
            if (!zdo.Persistent) return;   // transient effects are not what lags a base
            var owner = zdo.GetOwner();
            if (owner == 0L) { _unowned++; return; }
            _owned++;
            OwnerAcc a;
            if (!_acc.TryGetValue(owner, out a)) _acc[owner] = a = new OwnerAcc { Uid = owner };
            a.Count++;
            var zone = ZoneSystem.GetZone(zdo.GetPosition());
            int n;
            a.Zones.TryGetValue(zone, out n);
            a.Zones[zone] = n + 1;
        }

        private static void Done(Wave9Scan.Job job)
        {
            var waiting = new List<long>(Waiting);
            Waiting.Clear();
            if (job.Aborted || _acc == null)
            {
                foreach (var w in waiting) Wave9.Tell(w, "Ownership scan aborted: " + job.AbortReason);
                _acc = null;
                return;
            }
            var rows = new List<OwnerAcc>(_acc.Values);
            rows.Sort((x, y) => y.Count.CompareTo(x.Count));
            var peers = Wave9.ReadyPeers();

            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(job.Millis);
            pkg.Write(job.Scanned);
            pkg.Write(_owned);
            pkg.Write(_unowned);
            var n = Math.Min(rows.Count, RowCap);
            pkg.Write(n);
            var serverUid = ZDOMan.GetSessionID();
            for (var i = 0; i < n; i++)
            {
                var a = rows[i];
                var peer = Wave9.PeerByUid(a.Uid);
                string name, pid;
                int ping;
                if (peer != null) { name = peer.m_playerName ?? "?"; pid = Wave9.PeerId(peer); ping = Wave9.PeerPing(peer); }
                else if (a.Uid == serverUid)
                {
                    name = ZNet.instance != null && ZNet.instance.IsDedicated() ? "(server)"
                         : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() + " (host)" : "(host)");
                    pid = "HOST";
                    ping = 0;
                }
                else { name = "(gone)"; pid = ""; ping = -1; }

                var hot = default(Vector2s);
                var hotCount = 0;
                foreach (var kv in a.Zones)
                    if (kv.Value > hotCount) { hot = kv.Key; hotCount = kv.Value; }
                var hotPos = ZoneSystem.GetZonePos(hot);

                pkg.Write(a.Uid);
                pkg.Write(name);
                pkg.Write(pid);
                pkg.Write(ping);
                pkg.Write(a.Count);
                pkg.Write(hotPos);
                pkg.Write(hotCount);
                pkg.Write(a.Zones.Count);

                // Hand-off candidates: other peers whose active area covers the hot zone, best ping first.
                var cands = new List<KeyValuePair<ZNetPeer, int>>();
                foreach (var p in peers)
                {
                    if (p.m_uid == a.Uid) continue;
                    bool inRange;
                    try { inRange = ZNetScene.InActiveArea(hotPos, p.m_refPos); }
                    catch (Exception) { inRange = false; }
                    if (inRange) cands.Add(new KeyValuePair<ZNetPeer, int>(p, Wave9.PeerPing(p)));
                }
                cands.Sort((x, y) => (x.Value < 0 ? int.MaxValue : x.Value).CompareTo(y.Value < 0 ? int.MaxValue : y.Value));
                var nc = Math.Min(cands.Count, CandCap);
                pkg.Write(nc);
                for (var c = 0; c < nc; c++)
                {
                    pkg.Write(cands[c].Key.m_uid);
                    pkg.Write(cands[c].Key.m_playerName ?? "?");
                    pkg.Write(cands[c].Value);
                }
            }
            foreach (var w in waiting) Wave9.Reply(w, "AP_ZownData", pkg);
            _acc = null;
        }

        // ==================== hand-off ====================

        private static readonly List<ZDO> Scratch = new List<ZDO>();

        private static void OnMove(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvZownMove")) return;
            Vector3 center; float radius; long newOwner;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                center = pkg.ReadVector3();
                radius = pkg.ReadSingle();
                newOwner = pkg.ReadLong();
            }
            catch (Exception) { return; }
            if (!Wave9.Finite(center) || float.IsNaN(radius)) return;
            radius = Mathf.Clamp(radius, 4f, MaxMoveRadius);

            string label;
            if (newOwner == 0L) label = "released (the engine re-elects within 2 s)";
            else
            {
                var peer = Wave9.PeerByUid(newOwner);
                var host = Wave9.IsHostUid(newOwner);
                if (peer == null && !host) { Wave9.Tell(sender, "That player is no longer connected."); return; }
                bool inRange;
                try { inRange = host || ZNetScene.InActiveArea(center, peer.m_refPos); }
                catch (Exception) { inRange = false; }
                if (!inRange) { Wave9.Tell(sender, "That player is not near the area - the engine would hand it straight back. Pick a nearby player or Release."); return; }
                label = "handed to " + (peer != null ? peer.m_playerName : "the host");
            }

            Wave9.ZdosInRadius(center, radius, Scratch);
            var moved = 0;
            foreach (var zdo in Scratch)
            {
                if (!zdo.Persistent || zdo.GetOwner() == newOwner) continue;
                // A ship or cart may be steered or pulled right now; its driver must keep simulating it.
                if ((Wave9.KindOf(zdo.GetPrefab()) & (Wave9.KShip | Wave9.KCart | Wave9.KPlayer)) != 0) continue;
                try { zdo.SetOwner(newOwner); moved++; } catch (Exception) { }
            }
            Scratch.Clear();
            CompanionPlugin.SrvAudit(sender, "ZownMove", $"{moved} ZDOs within {radius:0} m of {Wave9.Vec(center)} {label}");
            Wave9.Tell(sender, $"Ownership: {moved} object(s) within {radius:0} m {label}.");
        }
    }
}

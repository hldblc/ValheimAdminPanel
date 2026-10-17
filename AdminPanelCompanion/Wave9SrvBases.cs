using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #8 abandoned-base finder + cleanup (server) ====================
    // Abandoned bases are the biggest source of ZDO bloat on old servers. One frame-spread world walk collects every
    // player-built piece (ZDO "creator" != 0), counts every ZDO per 32 m cell (the base's real cost: pickables,
    // items, creatures around it too), and notes ships and carts. Occupied cells are joined 8-connected into bases.
    // A base's "last seen" is the latest of its builders' last-seen (online now / Wave9Pids ledger / wave-1
    // presence). A builder the ledger never recorded has not been online since the ledger started (Wave9Pids.Since),
    // so that date stands in for them: on a fresh install nothing is "abandoned" until the ledger is older than the
    // asked-for days. Such bases are flagged "unknown" (some builders' real last visit is older than the ledger).
    //
    // Every scan has a generation number; actions carry the generation they were picked from and are refused when
    // another scan has replaced the list since (row ids are renumbered per scan). Ruin and delete also re-check, at
    // action time, that no builder has been online since the scan's cut-off. Base actions are owner-only.
    //
    //   AP_SrvBasesScan(ZPackage {ver, int days})   -> AP_BasesData { ver, int gen, ... }
    //   AP_SrvBasesAct(ZPackage {ver, int gen, int baseId, int action, int param})
    //       action 1 claim (creator := the admin's player id on every piece; wards keep their own list),
    //              2 ruin (remove param% of the non-container pieces at random),
    //              3 delete (snapshot first, then remove every piece and container of the base)
    //   AP_SrvBasesSnaps()                          -> AP_BasesSnaps { ver, n x { string name, long ticks, int objects } }
    //   AP_SrvBasesRestore(string name)             -> recreate a snapshot's objects
    //
    // Snapshots are the engine's own ZDO.Save format (a chunk file: short 42 | int n | n x ZDO.Save) in the
    // companion's per-world folder as w9_base_<id>_<stamp>.zdos, read back with Wave9Wdiff.ReadZdo. Restoring
    // recreates objects under fresh ids; nothing that was not in the base (terrain, vegetation) is touched. The file
    // is renamed to .restoring BEFORE the first object is created, so a failed restore can never be re-run on top of
    // its own partial result. A claim writes w9_claim_<id>_<stamp>.txt (zdoid|previous creator) to make it reversible.
    internal static class Wave9Bases
    {
        private const string Label = "bases";
        private const float Cell = 32f;
        private const int BaseCap = 100;
        private const int VehicleCap = 50;
        private const float VehicleFar = 100f;
        private const int MinPieces = 5;
        private const int ActCap = 20000;

        private sealed class Base
        {
            public int Id;
            public readonly List<ZDOID> Pieces = new List<ZDOID>();
            public readonly HashSet<long> Cells = new HashSet<long>();
            public readonly Dictionary<long, int> Creators = new Dictionary<long, int>();
            public Vector3 Sum;
            public int Zdos;
            public int Containers;
            public long LastSeen;
            public bool Unknown;
            public long Cutoff;   // the scan's "not seen since" threshold, re-checked when an action runs
            public Vector3 Center => Pieces.Count > 0 ? Sum / Pieces.Count : Vector3.zero;
        }

        private sealed class Scan
        {
            public long Requester;
            public int Days;
            public readonly Dictionary<long, List<ZDOID>> PiecesByCell = new Dictionary<long, List<ZDOID>>();
            public readonly Dictionary<long, Vector3> PosSum = new Dictionary<long, Vector3>();
            public readonly Dictionary<long, Dictionary<long, int>> CreatorsByCell = new Dictionary<long, Dictionary<long, int>>();
            public readonly Dictionary<long, int> ZdosByCell = new Dictionary<long, int>();
            public readonly Dictionary<long, int> ContainersByCell = new Dictionary<long, int>();
            public readonly List<KeyValuePair<string, Vector3>> Vehicles = new List<KeyValuePair<string, Vector3>>();
        }

        private static Scan _scan;
        private static List<Base> _bases = new List<Base>();
        private static int _gen;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBasesScan", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBasesAct");   // owner-only: destroys or rewrites world objects (review 2026-10-15)
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBasesRestore");   // owner-only: destroys or rewrites world objects (review 2026-10-15)
            CompanionPlugin.GrantRole("AP_SrvBasesSnaps", "moderator");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register<ZPackage>("AP_SrvBasesScan", OnScan);
            r.Register<ZPackage>("AP_SrvBasesAct", OnAct);
            r.Register("AP_SrvBasesSnaps", new Action<long>(OnSnaps));
            r.Register<string>("AP_SrvBasesRestore", OnRestore);
        }

        private static long CellOf(Vector3 p) =>
            ((long)Mathf.FloorToInt(p.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(p.z / Cell);

        private static void CellXY(long c, out int x, out int y) { x = (int)(c >> 32); y = (int)(uint)(c & 0xFFFFFFFF); }

        private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        // ==================== scan ====================

        private static void OnScan(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvBasesScan")) return;
            int days;
            try { if (pkg.ReadInt() != Wave9.Ver) return; days = Mathf.Clamp(pkg.ReadInt(), 0, 3650); }
            catch (Exception) { return; }
            if (_scan != null) { Wave9.Tell(sender, "A base scan is already running."); return; }
            var s = new Scan { Requester = sender, Days = days };
            _scan = s;
            if (!Wave9Scan.Start(Label, sender, z => Visit(s, z), job => Done(s, job))) _scan = null;
        }

        private static void Visit(Scan s, ZDO zdo)
        {
            var pos = zdo.GetPosition();
            if (pos.y > 3000f) return;   // dungeon interiors sit high above their entrance; not a base
            var cell = CellOf(pos);
            int c;
            s.ZdosByCell.TryGetValue(cell, out c);
            s.ZdosByCell[cell] = c + 1;
            var creator = zdo.GetLong(Wave9.KeyCreator, 0L);
            var kind = Wave9.KindOf(zdo.GetPrefab());
            if ((kind & (Wave9.KShip | Wave9.KCart)) != 0)
            {
                s.Vehicles.Add(new KeyValuePair<string, Vector3>(Wave9.PrefabName(zdo.GetPrefab()), pos));
                return;
            }
            if (creator == 0L || (kind & Wave9.KPiece) == 0) return;
            List<ZDOID> l;
            if (!s.PiecesByCell.TryGetValue(cell, out l)) s.PiecesByCell[cell] = l = new List<ZDOID>();
            l.Add(zdo.m_uid);
            Vector3 sum;
            s.PosSum.TryGetValue(cell, out sum);
            s.PosSum[cell] = sum + pos;
            Dictionary<long, int> cr;
            if (!s.CreatorsByCell.TryGetValue(cell, out cr)) s.CreatorsByCell[cell] = cr = new Dictionary<long, int>();
            int n;
            cr.TryGetValue(creator, out n);
            cr[creator] = n + 1;
            if ((kind & Wave9.KContainer) != 0)
            {
                s.ContainersByCell.TryGetValue(cell, out n);
                s.ContainersByCell[cell] = n + 1;
            }
        }

        private static void Done(Scan s, Wave9Scan.Job job)
        {
            _scan = null;
            if (job.Aborted) { Wave9.Tell(s.Requester, "Base scan aborted: " + job.AbortReason); return; }

            // 8-connected components over the occupied cells.
            var bases = new List<Base>();
            var seen = new HashSet<long>();
            foreach (var start in s.PiecesByCell.Keys)
            {
                if (!seen.Add(start)) continue;
                var b = new Base();
                var stack = new Stack<long>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    b.Cells.Add(c);
                    b.Pieces.AddRange(s.PiecesByCell[c]);
                    b.Sum += s.PosSum[c];
                    int z;
                    if (s.ZdosByCell.TryGetValue(c, out z)) b.Zdos += z;
                    if (s.ContainersByCell.TryGetValue(c, out z)) b.Containers += z;
                    foreach (var kv in s.CreatorsByCell[c])
                    {
                        int n;
                        b.Creators.TryGetValue(kv.Key, out n);
                        b.Creators[kv.Key] = n + kv.Value;
                    }
                    int x, y;
                    CellXY(c, out x, out y);
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var nb = CellKey(x + dx, y + dy);
                            if (s.PiecesByCell.ContainsKey(nb) && seen.Add(nb)) stack.Push(nb);
                        }
                }
                if (b.Pieces.Count >= MinPieces) bases.Add(b);
            }

            var online = Wave9Pids.OnlineByPid();
            var cutoff = DateTime.UtcNow.AddDays(-s.Days).Ticks;
            var since = Wave9Pids.Since();
            var report = new List<Base>();
            foreach (var b in bases)
            {
                bool unknown;
                b.LastSeen = LastSeenOf(b, online, since, out unknown);
                b.Unknown = unknown;
                b.Cutoff = cutoff;
                if (b.LastSeen < cutoff) report.Add(b);
            }
            report.Sort((a, b2) => b2.Zdos.CompareTo(a.Zdos));
            if (report.Count > BaseCap) report.RemoveRange(BaseCap, report.Count - BaseCap);
            for (var i = 0; i < report.Count; i++) report[i].Id = i + 1;
            _bases = report;
            var gen = ++_gen;

            // Vehicles far from EVERY base (not only the reported ones).
            var allCells = new HashSet<long>(s.PiecesByCell.Keys);
            var vehicles = new List<KeyValuePair<string, Vector3>>();
            var reach = Mathf.CeilToInt(VehicleFar / Cell);
            foreach (var v in s.Vehicles)
            {
                var c = CellOf(v.Value);
                int x, y;
                CellXY(c, out x, out y);
                var near = false;
                for (var dx = -reach; dx <= reach && !near; dx++)
                    for (var dy = -reach; dy <= reach && !near; dy++)
                        if (allCells.Contains(CellKey(x + dx, y + dy))) near = true;
                if (!near) vehicles.Add(v);
                if (vehicles.Count >= VehicleCap) break;
            }

            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(gen);
            pkg.Write(job.Millis);
            pkg.Write(bases.Count);
            pkg.Write(s.Days);
            pkg.Write(report.Count);
            foreach (var b in report)
            {
                pkg.Write(b.Id);
                pkg.Write(b.Center);
                pkg.Write(b.Pieces.Count);
                pkg.Write(b.Zdos);
                pkg.Write(b.Containers);
                pkg.Write(b.LastSeen);
                pkg.Write(b.Unknown);
                var top = new List<KeyValuePair<long, int>>(b.Creators);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                var names = new List<string>();
                for (var k = 0; k < top.Count && k < 3; k++)
                {
                    var who = Wave9Pids.Lookup(top[k].Key, online);
                    names.Add(who.Name.Length > 0 ? who.Name : "#" + top[k].Key);
                }
                if (top.Count > 3) names.Add("+" + (top.Count - 3));
                pkg.Write(string.Join(", ", names.ToArray()));
            }
            pkg.Write(vehicles.Count);
            foreach (var v in vehicles) { pkg.Write(v.Key); pkg.Write(v.Value); }
            Wave9.Reply(s.Requester, "AP_BasesData", pkg);
        }

        // The latest last-seen over a base's builders; a builder the ledger never recorded counts as last seen when the
        // ledger started (they cannot have been online after it without being recorded).
        private static long LastSeenOf(Base b, Dictionary<long, ZNetPeer> online, long since, out bool unknown)
        {
            long last = 0;
            unknown = false;
            foreach (var pid in b.Creators.Keys)
            {
                var who = Wave9Pids.Lookup(pid, online);
                var t = who.LastSeenTicks;
                if (t == 0L) { unknown = true; t = since; }
                if (t > last) last = t;
            }
            return last;
        }

        // ==================== actions ====================

        private static Base Find(int id)
        {
            foreach (var b in _bases) if (b.Id == id) return b;
            return null;
        }

        private static void OnAct(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvBasesAct")) return;
            int gen, id, action, param;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                gen = pkg.ReadInt();
                id = pkg.ReadInt();
                action = pkg.ReadInt();
                param = pkg.ReadInt();
            }
            catch (Exception) { return; }
            if (gen != _gen) { Wave9.Tell(sender, "The base list changed since you picked that row (another scan ran) - scan again."); return; }
            var b = Find(id);
            var man = ZDOMan.instance;
            if (b == null || man == null) { Wave9.Tell(sender, "That base is not in the last scan - scan again."); return; }
            if (PlayerNear(b)) { Wave9.Tell(sender, "Refused: a player is in or next to that base right now."); return; }
            if (action == 2 || action == 3)
            {
                // The verdict may be hours old: a builder who came back since the scan makes the base not abandoned.
                bool unknownNow;
                var lastNow = LastSeenOf(b, Wave9Pids.OnlineByPid(), Wave9Pids.Since(), out unknownNow);
                if (lastNow >= b.Cutoff) { Wave9.Tell(sender, $"Refused: a builder of base #{b.Id} has been online since the scan's cut-off - scan again."); return; }
            }
            if (action == 3 && b.Pieces.Count > ActCap)
            {
                Wave9.Tell(sender, $"Refused: base #{b.Id} has {b.Pieces.Count} pieces, more than one delete handles ({ActCap}). Ruin it first, then scan and delete the rest.");
                return;
            }

            var done = 0;
            switch (action)
            {
                case 1:
                {
                    var adminPid = AdminPlayerId(sender);
                    if (adminPid == 0L) { Wave9.Tell(sender, "Your player id is not known yet - try again in a few seconds."); return; }
                    // Record who built what first: the claim rewrites "creator" and is otherwise irreversible.
                    var record = new System.Text.StringBuilder();
                    foreach (var id2 in b.Pieces)
                    {
                        var zdo = man.GetZDO(id2);
                        if (zdo == null) continue;
                        record.Append(id2.UserID.ToString(CultureInfo.InvariantCulture)).Append(':').Append(id2.ID.ToString(CultureInfo.InvariantCulture))
                              .Append('|').Append(zdo.GetLong(Wave9.KeyCreator, 0L).ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                    string claimFile;
                    if (!WriteSnapFile("w9_claim_" + b.Id + "_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt",
                                       System.Text.Encoding.UTF8.GetBytes(record.ToString()), out claimFile))
                    { Wave9.Tell(sender, "Refused: the claim record could not be written, so nothing was changed."); return; }
                    foreach (var id2 in b.Pieces)
                    {
                        var zdo = man.GetZDO(id2);
                        if (zdo == null) continue;
                        zdo.Set(Wave9.KeyCreator, adminPid);
                        done++;
                    }
                    CompanionPlugin.SrvAudit(sender, "BaseClaim", $"base #{b.Id} at {Wave9.Vec(b.Center)}: {done} piece(s) now credited to the admin (previous builders in {claimFile})");
                    Wave9.Tell(sender, $"Claimed base #{b.Id}: {done} piece(s) now list you as builder (players who have it loaded see it after a reload). Its wards keep their own permitted list.");
                    break;
                }
                case 2:
                {
                    var pct = Mathf.Clamp(param, 10, 90);
                    var rng = new System.Random();
                    foreach (var id2 in b.Pieces)
                    {
                        if (done >= ActCap) break;
                        var zdo = man.GetZDO(id2);
                        if (zdo == null) continue;
                        if ((Wave9.KindOf(zdo.GetPrefab()) & (Wave9.KContainer | Wave9.KPortal)) != 0) continue;
                        if (rng.Next(100) >= pct) continue;
                        if (Wave9.DestroyZdo(zdo)) done++;
                    }
                    CompanionPlugin.SrvAudit(sender, "BaseRuin", $"base #{b.Id} at {Wave9.Vec(b.Center)}: removed {done} piece(s) (~{pct}%)");
                    Wave9.Tell(sender, $"Base #{b.Id} is now a ruin: {done} piece(s) removed (~{pct}%). Containers and portals were kept.");
                    break;
                }
                case 3:
                {
                    string file;
                    var saved = Snapshot(b, out file);
                    if (saved < 0) { Wave9.Tell(sender, "Refused: the snapshot could not be written, so nothing was deleted."); return; }
                    foreach (var id2 in b.Pieces)
                    {
                        if (done >= ActCap) break;
                        var zdo = man.GetZDO(id2);
                        if (zdo != null && Wave9.DestroyZdo(zdo)) done++;
                    }
                    _bases.Remove(b);
                    b.Pieces.Clear();
                    CompanionPlugin.SrvAudit(sender, "BaseDelete", $"base #{b.Id} at {Wave9.Vec(b.Center)}: snapshot {file} ({saved} objects), removed {done}");
                    Wave9.Tell(sender, $"Base #{b.Id} deleted: {done} object(s) removed. Snapshot {file} can restore it (Bases > Snapshots).");
                    break;
                }
            }
        }

        private static bool PlayerNear(Base b)
        {
            var center = b.Center;
            var radius = Mathf.Sqrt(b.Cells.Count) * Cell + 30f;
            foreach (var p in Wave9.ReadyPeers())
                if (Wave9.MapDist(Wave9.PeerPos(p), center) < radius) return true;
            return false;
        }

        private static long AdminPlayerId(long sender)
        {
            if (Wave9.IsHostUid(sender) && Player.m_localPlayer != null) return Player.m_localPlayer.GetPlayerID();
            return Wave9.PeerPlayerId(Wave9.PeerByUid(sender));
        }

        // ==================== snapshots ====================

        private static string SnapDir()
        {
            var dir = FeatureStore.DataDir;
            if (string.IsNullOrEmpty(dir)) return null;
            var d = Path.Combine(dir, "base_snapshots");
            Directory.CreateDirectory(d);
            return d;
        }

        private static bool WriteSnapFile(string name, byte[] data, out string file)
        {
            file = name;
            try
            {
                var dir = SnapDir();
                if (dir == null) return false;
                File.WriteAllBytes(Path.Combine(dir, name), data);
                return true;
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Base record {name} failed: {e.Message}"); return false; }
        }

        private static int Snapshot(Base b, out string file)
        {
            file = "";
            try
            {
                var dir = SnapDir();
                if (dir == null) return -1;
                var man = ZDOMan.instance;
                var pkg = new ZPackage();
                pkg.Write((short)42);
                var zdos = new List<ZDO>();
                foreach (var id in b.Pieces) { var z = man.GetZDO(id); if (z != null) zdos.Add(z); }
                pkg.Write(zdos.Count);
                foreach (var z in zdos) z.Save(pkg);
                file = "w9_base_" + b.Id + "_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zdos";
                File.WriteAllBytes(Path.Combine(dir, file), pkg.GetArray());
                return zdos.Count;
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Base snapshot failed: {e.Message}"); return -1; }
        }

        private static void OnSnaps(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvBasesSnaps")) return;
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            var rows = new List<FileInfo>();
            try
            {
                var dir = SnapDir();
                if (dir != null) foreach (var f in Directory.GetFiles(dir, "w9_base_*.zdos")) rows.Add(new FileInfo(f));
            }
            catch (Exception) { }
            rows.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            var n = Math.Min(rows.Count, 40);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                pkg.Write(rows[i].Name);
                pkg.Write(rows[i].LastWriteTimeUtc.Ticks);
                pkg.Write((int)Math.Min(int.MaxValue, rows[i].Length));
            }
            Wave9.Reply(sender, "AP_BasesSnaps", pkg);
        }

        private static void OnRestore(long sender, string name)
        {
            if (!Wave9.Can(sender, "AP_SrvBasesRestore")) return;
            name = Path.GetFileName(Wave9.CleanName(name, 96) ?? "");
            if (!name.StartsWith("w9_base_", StringComparison.Ordinal) || !name.EndsWith(".zdos", StringComparison.Ordinal)) return;
            var man = ZDOMan.instance;
            var dir = SnapDir();
            if (man == null || dir == null) return;
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) { Wave9.Tell(sender, "That snapshot no longer exists."); return; }
            // Take the file out of the list BEFORE creating anything: a restore that fails half-way must not be
            // offered again, or the retry would duplicate every object the first attempt already recreated.
            var working = path + ".restoring";
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
                if (File.Exists(working)) File.Delete(working);
                File.Move(path, working);
            }
            catch (Exception e) { Wave9.Tell(sender, "Snapshot restore refused (file not readable/movable): " + e.Message); return; }
            var done = 0;
            try
            {
                var pkg = new ZPackage(bytes);
                int ver = pkg.ReadShort();
                var n = pkg.ReadInt();
                for (var i = 0; i < n && i < ActCap; i++)
                {
                    var o = Wave9Wdiff.ReadZdo(pkg, ver, true);
                    if (o != null && Wave9Wdiff.Recreate(man, o)) done++;
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.SrvAudit(sender, "BaseRestore", $"{name}: FAILED after {done} object(s): {e.Message}");
                Wave9.Tell(sender, $"Snapshot restore stopped after {done} object(s): {e.Message}. The file was set aside as {Path.GetFileName(working)} so it cannot be restored twice.");
                return;
            }
            try
            {
                var final = path + ".restored";
                if (File.Exists(final)) File.Delete(final);
                File.Move(working, final);
            }
            catch (Exception) { }
            CompanionPlugin.SrvAudit(sender, "BaseRestore", $"{name}: {done} object(s) recreated");
            Wave9.Tell(sender, $"Snapshot {name} restored: {done} object(s) recreated.");
        }
    }
}

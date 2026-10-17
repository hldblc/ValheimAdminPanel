using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #11 world diff between backups (server) ====================
    // "What changed at this base since last night?" Compare the LIVE world (or backup A) with backup B: player-built
    // objects (any ZDO with a non-zero "creator") added and removed per 64 m zone, with their builders, and
    // containers whose contents differ. A row can be teleported to, or restored: recreate the removed pieces from B,
    // put B's contents back into changed containers, or remove the pieces added since B (A must be the live world).
    //
    // Reading a backup never touches the engine's ZDO store: ZDO.Load writes into the GLOBAL ZDOExtraData tables
    // keyed by id, which would collide with live objects. The chunk files are parsed here field for field, mirroring
    // ZDO.Load (decompile-1.0.20 ZDO.cs:1295-1352): ushort flags | [pre-40: Vector2s sector] | SmallPosition ? Vector2s
    // : Vector3 | int prefab | Rotation ? small rotation (40+) | connection, floats, vec3s, quats, ints, longs,
    // strings, byte arrays (NumItems count + (int key, value) pairs each). Only the chunks the set's _main.N.chunks
    // index names are read - a backup copies every chunk vintage in the directory and the loader prunes the rest.
    // Chunked saves do not persist ZDO ids (ZDO.Load assigns fresh ones), so objects are matched by prefab + position
    // rounded to 10 cm - which is why ships and carts (they move) are left out of the comparison entirely. The parse
    // runs on a worker thread (pure IO + this parser, no Unity/engine state); the live side is collected by the
    // shared frame-spread scanner. A side with more than ObjCap player-built objects fails with "narrow the radius".
    //
    // Restore safety: every result has a run id; a restore must name it and is refused when another comparison has
    // replaced the result, when it is older than ResultMinutes, or after a world reload. "Recreate removed" skips an
    // object whose prefab already stands at that spot again; "remove added" re-checks each live object is still that
    // prefab at that spot and keeps containers that hold items (reported as kept). Restoring is owner-only.
    //
    //   AP_SrvWdiffSets()           -> AP_WdiffSets { ver, n x { string name, long ticks, long bytes } }
    //   AP_SrvWdiffRun(ZPackage)    { ver, string a ("live" | set), string b (set), Vector3 center, float radius (0 = all) }
    //                               -> AP_WdiffData when done
    //   AP_SrvWdiffRestore(ZPackage){ ver, int run, Vector3 zone, int what (1 recreate removed, 2 restore containers, 3 remove added) }
    //   AP_WdiffData { ver, int run, a, b, long ms, int added, int removed, int changed, string error,
    //                  n x { Vector3 zone, int added, int removed, int changed, string builders, string addedTop, string removedTop } }
    internal static class Wave9Wdiff
    {
        private const string Live = "live";
        private const string ScanLabel = "worlddiff";
        private const int ZoneRowCap = 80;
        private const int RestoreCap = 2000;
        private const float MaxRadius = 2000f;
        private const int ObjCap = 300000;          // per side; beyond it the comparison asks for a smaller radius
        private const double ResultMinutes = 30d;   // a restore acts on the world as it was compared, so results expire

        private static readonly int KeyCreatorHash = "creator".GetStableHashCode();
        private static readonly int KeyItemsHash = "items".GetStableHashCode();
        private static readonly int KeyInUse = "InUse".GetStableHashCode();

        internal sealed class Obj
        {
            public int Prefab;
            public Vector3 Pos;
            public Vector3 Rot;            // euler (backup) - live objects keep their own
            public long Creator;
            public byte[] Items;           // container blob (ZDO byte array "items"), null when none
            public ZDOID LiveId;
            public ushort Flags;
            public List<KeyValuePair<int, float>> Floats;
            public List<KeyValuePair<int, Vector3>> Vec3s;
            public List<KeyValuePair<int, Quaternion>> Quats;
            public List<KeyValuePair<int, int>> Ints;
            public List<KeyValuePair<int, long>> Longs;
            public List<KeyValuePair<int, string>> Strings;
            public List<KeyValuePair<int, byte[]>> Bytes;
        }

        private sealed class Job
        {
            public long Requester;
            public string A, B;
            public Vector3 Center;
            public float Radius;
            public long StartTicks;
            public volatile bool BDone, ADone;
            public Dictionary<string, Obj> ObjsA, ObjsB;
            public string Error = "";
            public bool LiveScanStarted;
        }

        private sealed class Result
        {
            public string A, B;
            public Dictionary<Vector2s, List<Obj>> Added = new Dictionary<Vector2s, List<Obj>>();
            public Dictionary<Vector2s, List<Obj>> Removed = new Dictionary<Vector2s, List<Obj>>();
            public Dictionary<Vector2s, List<KeyValuePair<Obj, Obj>>> Changed = new Dictionary<Vector2s, List<KeyValuePair<Obj, Obj>>>();
        }

        private static Job _job;
        private static Result _last;
        private static int _run;
        private static DateTime _lastAt;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWdiffRun", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvWdiffRestore");   // owner-only: destroys or rewrites world objects (review 2026-10-15)
            CompanionPlugin.GrantRole("AP_SrvWdiffSets", "moderator");
        }

        internal static void Register(ZRoutedRpc r)
        {
            _last = null;   // a new world: the old result's objects and ids are meaningless here
            r.Register("AP_SrvWdiffSets", new Action<long>(OnSets));
            r.Register<ZPackage>("AP_SrvWdiffRun", OnRun);
            r.Register<ZPackage>("AP_SrvWdiffRestore", OnRestore);
        }

        // ==================== sets ====================

        private static List<KeyValuePair<string, string>> ListSets()
        {
            var res = new List<KeyValuePair<string, string>>();   // name -> directory
            var parent = Wave2Backup.CurrentSetParent();
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return res;
            foreach (var dir in Directory.GetDirectories(parent))
            {
                var name = Path.GetFileName(dir);
                if (name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(dir, "apbackup.txt"))) continue;
                res.Add(new KeyValuePair<string, string>(name, dir));
            }
            res.Sort((x, y) => string.CompareOrdinal(y.Key, x.Key));   // stamps sort by time; newest first
            return res;
        }

        private static void OnSets(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvWdiffSets")) return;
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            List<KeyValuePair<string, string>> sets;
            try { sets = ListSets(); } catch (Exception) { sets = new List<KeyValuePair<string, string>>(); }
            var n = Math.Min(sets.Count, 60);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                long ticks = 0, bytes = 0;
                try
                {
                    ticks = Directory.GetLastWriteTimeUtc(sets[i].Value).Ticks;
                    foreach (var f in Directory.GetFiles(sets[i].Value)) bytes += new FileInfo(f).Length;
                }
                catch (Exception) { }
                pkg.Write(sets[i].Key);
                pkg.Write(ticks);
                pkg.Write(bytes);
            }
            Wave9.Reply(sender, "AP_WdiffSets", pkg);
        }

        // ==================== run ====================

        private static void OnRun(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvWdiffRun")) return;
            string a, b; Vector3 center; float radius;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                a = Wave9.CleanName(pkg.ReadString(), 64);
                b = Wave9.CleanName(pkg.ReadString(), 64);
                center = pkg.ReadVector3();
                radius = pkg.ReadSingle();
            }
            catch (Exception) { return; }
            if (_job != null) { Wave9.Tell(sender, "A backup comparison is already running."); return; }
            if (!Wave9.Finite(center) || float.IsNaN(radius)) return;
            radius = Mathf.Clamp(radius, 0f, MaxRadius);

            var sets = ListSets();
            string dirA = null, dirB = null;
            foreach (var s in sets) { if (s.Key == a) dirA = s.Value; if (s.Key == b) dirB = s.Value; }
            if (dirB == null) { Wave9.Tell(sender, "Backup B was not found (only complete chunked backup sets can be compared)."); return; }
            if (a != Live && dirA == null) { Wave9.Tell(sender, "Backup A was not found."); return; }
            if (a == b) { Wave9.Tell(sender, "Pick two different snapshots."); return; }

            var job = new Job { Requester = sender, A = a, B = b, Center = center, Radius = radius, StartTicks = DateTime.UtcNow.Ticks };
            _job = job;
            Task.Run(() =>
            {
                try { job.ObjsB = ParseSet(dirB, center, radius); }
                catch (Exception e) { job.Error = "backup B: " + e.Message; }
                job.BDone = true;
            });
            if (a != Live)
            {
                Task.Run(() =>
                {
                    try { job.ObjsA = ParseSet(dirA, center, radius); }
                    catch (Exception e) { job.Error = "backup A: " + e.Message; }
                    job.ADone = true;
                });
            }
            Wave9.Tell(sender, $"Comparing {(a == Live ? "the live world" : a)} with {b}{(radius > 0f ? $" within {radius:0} m" : "")} - this can take a minute on a big world.");
        }

        internal static void Tick()
        {
            var job = _job;
            if (job == null) return;
            if (job.A == Live && !job.LiveScanStarted && job.BDone)
            {
                job.LiveScanStarted = true;
                if (job.Error.Length > 0) { job.ADone = true; }
                else
                {
                    job.ObjsA = new Dictionary<string, Obj>(StringComparer.Ordinal);
                    if (!Wave9Scan.Start(ScanLabel, job.Requester, z => LiveVisit(job, z), j =>
                    {
                        if (j.Aborted) job.Error = "live scan aborted: " + j.AbortReason;
                        job.ADone = true;
                    }))
                    {
                        job.Error = "the world scanner is busy";
                        job.ADone = true;
                    }
                }
            }
            if (!job.ADone || !job.BDone) return;
            _job = null;
            Finish(job);
        }

        // Ships and carts move, so prefab+position matching would list every one as removed AND added.
        private static bool IsVehicle(int prefab) => (Wave9.KindOf(prefab) & (Wave9.KShip | Wave9.KCart)) != 0;

        private static void LiveVisit(Job job, ZDO zdo)
        {
            var creator = zdo.GetLong(KeyCreatorHash, 0L);
            if (creator == 0L) return;
            var pos = zdo.GetPosition();
            if (job.Radius > 0f && Wave9.MapDist(pos, job.Center) > job.Radius) return;
            if (IsVehicle(zdo.GetPrefab())) return;
            if (job.ObjsA.Count >= ObjCap) { if (job.Error.Length == 0) job.Error = $"more than {ObjCap} player-built objects - narrow the radius"; return; }
            var o = new Obj { Prefab = zdo.GetPrefab(), Pos = pos, Creator = creator, LiveId = zdo.m_uid };
            o.Items = InvCodec.ReadBlob(zdo);
            job.ObjsA[KeyOf(o.Prefab, pos)] = o;
        }

        private static string KeyOf(int prefab, Vector3 p) =>
            prefab + ":" + Mathf.RoundToInt(p.x * 10f) + ":" + Mathf.RoundToInt(p.y * 10f) + ":" + Mathf.RoundToInt(p.z * 10f);

        // ==================== chunk parser (worker thread: System.IO + ZPackage only) ====================

        private static Dictionary<string, Obj> ParseSet(string dir, Vector3 center, float radius)
        {
            var scan = FeatureStore.ScanChunkedSet(dir);
            if (scan.Generation < 0) throw new IOException("no complete save generation in the set");
            var names = FeatureStore.IndexedChunkFiles(Path.Combine(dir, "_main." + scan.Generation + ".chunks"));
            if (names == null) throw new IOException("the chunk index has an unknown format");
            var res = new Dictionary<string, Obj>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) throw new IOException("missing chunk " + name);
                var pkg = new ZPackage(File.ReadAllBytes(path));
                int ver = pkg.ReadShort();
                var n = pkg.ReadInt();
                if (n < 0) throw new IOException("bad chunk " + name);
                for (var i = 0; i < n; i++)
                {
                    var o = ReadZdo(pkg, ver);
                    if (o == null) continue;
                    if (radius > 0f && Wave9.MapDist(o.Pos, center) > radius) continue;
                    if (res.Count >= ObjCap) throw new IOException($"more than {ObjCap} player-built objects - narrow the radius");
                    res[KeyOf(o.Prefab, o.Pos)] = o;   // vehicles are dropped on the main thread (KindOf touches the scene)
                }
            }
            return res;
        }

        private static int NumItems(ZPackage pkg, int ver) => ver < 33 ? pkg.ReadByte() : pkg.ReadNumItems();

        // Returns the object when it is player-built (creator != 0, or any object with allObjects), null otherwise;
        // always consumes the record. Also used by the base finder to read back its delete snapshots (ZDO.Save format).
        internal static Obj ReadZdo(ZPackage pkg, int ver, bool allObjects = false)
        {
            var chunked = ver >= 40;
            var flags = pkg.ReadUShort();
            if (!chunked) pkg.ReadVector2s();
            Vector3 pos;
            if ((flags & 0x2000) != 0) { var v = pkg.ReadVector2s(); pos = new Vector3(v.x, 0f, v.y); }
            else pos = pkg.ReadVector3();
            var prefab = pkg.ReadInt();
            var rot = Vector3.zero;
            if ((flags & 0x1000) != 0) rot = chunked ? pkg.ReadSmallRotation() : pkg.ReadVector3();
            var o = new Obj { Prefab = prefab, Pos = pos, Rot = rot, Flags = flags };
            if ((flags & 0xFF) != 0)
            {
                if ((flags & 1) != 0) { pkg.ReadByte(); pkg.ReadInt(); }   // connection: not restorable, dropped
                if ((flags & 2) != 0) { var c = NumItems(pkg, ver); o.Floats = new List<KeyValuePair<int, float>>(c); for (var i = 0; i < c; i++) o.Floats.Add(new KeyValuePair<int, float>(pkg.ReadInt(), pkg.ReadSingle())); }
                if ((flags & 4) != 0) { var c = NumItems(pkg, ver); o.Vec3s = new List<KeyValuePair<int, Vector3>>(c); for (var i = 0; i < c; i++) o.Vec3s.Add(new KeyValuePair<int, Vector3>(pkg.ReadInt(), pkg.ReadVector3())); }
                if ((flags & 8) != 0) { var c = NumItems(pkg, ver); o.Quats = new List<KeyValuePair<int, Quaternion>>(c); for (var i = 0; i < c; i++) o.Quats.Add(new KeyValuePair<int, Quaternion>(pkg.ReadInt(), pkg.ReadQuaternion())); }
                if ((flags & 0x10) != 0) { var c = NumItems(pkg, ver); o.Ints = new List<KeyValuePair<int, int>>(c); for (var i = 0; i < c; i++) o.Ints.Add(new KeyValuePair<int, int>(pkg.ReadInt(), pkg.ReadInt())); }
                if ((flags & 0x20) != 0)
                {
                    var c = NumItems(pkg, ver);
                    o.Longs = new List<KeyValuePair<int, long>>(c);
                    for (var i = 0; i < c; i++)
                    {
                        var k = pkg.ReadInt();
                        var v = pkg.ReadLong();
                        o.Longs.Add(new KeyValuePair<int, long>(k, v));
                        if (k == KeyCreatorHash) o.Creator = v;
                    }
                }
                if ((flags & 0x40) != 0)
                {
                    var c = NumItems(pkg, ver);
                    o.Strings = new List<KeyValuePair<int, string>>(c);
                    for (var i = 0; i < c; i++)
                    {
                        var k = pkg.ReadInt();
                        var v = pkg.ReadString();
                        o.Strings.Add(new KeyValuePair<int, string>(k, v));
                        if (k == KeyItemsHash && !string.IsNullOrEmpty(v) && o.Items == null)
                        {
                            try { o.Items = new ZPackage(v).GetArray(); } catch (Exception) { }   // pre-1.0.12 save: base64 string
                        }
                    }
                }
                if ((flags & 0x80) != 0)
                {
                    var c = NumItems(pkg, ver);
                    o.Bytes = new List<KeyValuePair<int, byte[]>>(c);
                    for (var i = 0; i < c; i++)
                    {
                        var k = pkg.ReadInt();
                        var v = pkg.ReadByteArray();
                        o.Bytes.Add(new KeyValuePair<int, byte[]>(k, v));
                        if (k == KeyItemsHash && v != null && v.Length > 0) o.Items = v;   // Container.Save, 1.0.12+
                    }
                }
            }
            return o.Creator != 0L || allObjects ? o : null;
        }

        // ==================== diff + report ====================

        private static void Finish(Job job)
        {
            var ms = (DateTime.UtcNow.Ticks - job.StartTicks) / TimeSpan.TicksPerMillisecond;
            if (job.Error.Length > 0 || job.ObjsA == null || job.ObjsB == null)
            {
                SendData(job.Requester, job.A, job.B, ms, null, job.Error.Length > 0 ? job.Error : "nothing to compare");
                return;
            }
            var r = new Result { A = job.A, B = job.B };
            var scratchA = new List<InvCodec.Rec>();
            var scratchB = new List<InvCodec.Rec>();
            foreach (var kv in job.ObjsA)
            {
                if (IsVehicle(kv.Value.Prefab)) continue;
                Obj other;
                if (!job.ObjsB.TryGetValue(kv.Key, out other)) { Bucket(r.Added, kv.Value); continue; }
                if ((kv.Value.Items != null || other.Items != null) && !SameBytes(kv.Value.Items, other.Items) && !SameContents(kv.Value.Items, other.Items, scratchA, scratchB))
                {
                    var z = ZoneSystem.GetZone(kv.Value.Pos);
                    List<KeyValuePair<Obj, Obj>> l;
                    if (!r.Changed.TryGetValue(z, out l)) r.Changed[z] = l = new List<KeyValuePair<Obj, Obj>>();
                    l.Add(new KeyValuePair<Obj, Obj>(kv.Value, other));
                }
            }
            foreach (var kv in job.ObjsB)
                if (!IsVehicle(kv.Value.Prefab) && !job.ObjsA.ContainsKey(kv.Key)) Bucket(r.Removed, kv.Value);
            job.ObjsA = job.ObjsB = null;
            _last = r;
            _lastAt = DateTime.UtcNow;
            SendData(job.Requester, job.A, job.B, ms, r, "");
            CompanionPlugin.SrvAudit(job.Requester, "WorldDiff", $"{job.A} vs {job.B}: +{Count(r.Added)} -{Count(r.Removed)} ~{CountPairs(r.Changed)}");
        }

        private static void Bucket(Dictionary<Vector2s, List<Obj>> into, Obj o)
        {
            var z = ZoneSystem.GetZone(o.Pos);
            List<Obj> l;
            if (!into.TryGetValue(z, out l)) into[z] = l = new List<Obj>();
            l.Add(o);
        }

        private static int Count(Dictionary<Vector2s, List<Obj>> d) { var n = 0; foreach (var kv in d) n += kv.Value.Count; return n; }
        private static int CountPairs(Dictionary<Vector2s, List<KeyValuePair<Obj, Obj>>> d) { var n = 0; foreach (var kv in d) n += kv.Value.Count; return n; }

        // Two blobs that decode to the same multiset of (prefab, stack, quality) are the same contents - a save by a
        // newer game build rewrites the layout version without anything having moved.
        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameContents(byte[] a, byte[] b, List<InvCodec.Rec> sa, List<InvCodec.Rec> sb)
        {
            int va, da, vb, db;
            if (!InvCodec.Parse(a, out va, out da, sa) || !InvCodec.Parse(b, out vb, out db, sb)) return false;
            if (sa.Count != sb.Count) return false;
            var m = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in sa) { var k = r.PrefabHash + "/" + r.Quality; int c; m.TryGetValue(k, out c); m[k] = c + r.Stack; }
            foreach (var r in sb) { var k = r.PrefabHash + "/" + r.Quality; int c; m.TryGetValue(k, out c); m[k] = c - r.Stack; }
            foreach (var kv in m) if (kv.Value != 0) return false;
            return true;
        }

        private static string TopPrefabs(List<Obj> list)
        {
            if (list == null || list.Count == 0) return "";
            var counts = new Dictionary<int, int>();
            foreach (var o in list) { int c; counts.TryGetValue(o.Prefab, out c); counts[o.Prefab] = c + 1; }
            var top = new List<KeyValuePair<int, int>>(counts);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var parts = new List<string>();
            for (var k = 0; k < top.Count && k < 4; k++) parts.Add(Wave9.PrefabName(top[k].Key) + " x" + top[k].Value);
            if (top.Count > 4) parts.Add("+" + (top.Count - 4));
            return string.Join(", ", parts.ToArray());
        }

        private static void SendData(long to, string a, string b, long ms, Result r, string error)
        {
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(r != null ? ++_run : 0);
            pkg.Write(a ?? "");
            pkg.Write(b ?? "");
            pkg.Write(ms);
            pkg.Write(r != null ? Count(r.Added) : 0);
            pkg.Write(r != null ? Count(r.Removed) : 0);
            pkg.Write(r != null ? CountPairs(r.Changed) : 0);
            pkg.Write(error ?? "");
            if (r == null) { pkg.Write(0); Wave9.Reply(to, "AP_WdiffData", pkg); return; }

            var zones = new HashSet<Vector2s>();
            foreach (var z in r.Added.Keys) zones.Add(z);
            foreach (var z in r.Removed.Keys) zones.Add(z);
            foreach (var z in r.Changed.Keys) zones.Add(z);
            var rows = new List<Vector2s>(zones);
            rows.Sort((x, y) => Weight(r, y).CompareTo(Weight(r, x)));
            var online = Wave9Pids.OnlineByPid();
            var n = Math.Min(rows.Count, ZoneRowCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var z = rows[i];
                List<Obj> added, removed;
                List<KeyValuePair<Obj, Obj>> changed;
                r.Added.TryGetValue(z, out added);
                r.Removed.TryGetValue(z, out removed);
                r.Changed.TryGetValue(z, out changed);
                var creators = new Dictionary<long, int>();
                if (added != null) foreach (var o in added) { int c; creators.TryGetValue(o.Creator, out c); creators[o.Creator] = c + 1; }
                if (removed != null) foreach (var o in removed) { int c; creators.TryGetValue(o.Creator, out c); creators[o.Creator] = c + 1; }
                var top = new List<KeyValuePair<long, int>>(creators);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                var names = new List<string>();
                for (var k = 0; k < top.Count && k < 3; k++)
                {
                    var who = Wave9Pids.Lookup(top[k].Key, online);
                    names.Add(who.Name.Length > 0 ? who.Name : "#" + top[k].Key);
                }
                pkg.Write(ZoneSystem.GetZonePos(z));
                pkg.Write(added != null ? added.Count : 0);
                pkg.Write(removed != null ? removed.Count : 0);
                pkg.Write(changed != null ? changed.Count : 0);
                pkg.Write(string.Join(", ", names.ToArray()));
                pkg.Write(TopPrefabs(added));
                pkg.Write(TopPrefabs(removed));
            }
            Wave9.Reply(to, "AP_WdiffData", pkg);
        }

        private static int Weight(Result r, Vector2s z)
        {
            var n = 0;
            List<Obj> l;
            List<KeyValuePair<Obj, Obj>> c;
            if (r.Added.TryGetValue(z, out l)) n += l.Count;
            if (r.Removed.TryGetValue(z, out l)) n += l.Count;
            if (r.Changed.TryGetValue(z, out c)) n += c.Count * 5;
            return n;
        }

        // ==================== restore ====================

        private static void OnRestore(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvWdiffRestore")) return;
            Vector3 zonePos; int what, run;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                run = pkg.ReadInt();
                zonePos = pkg.ReadVector3();
                what = pkg.ReadInt();
            }
            catch (Exception) { return; }
            var r = _last;
            if (r == null) { Wave9.Tell(sender, "Run a comparison first."); return; }
            if (run != _run) { Wave9.Tell(sender, "Another comparison has replaced the one you are looking at - compare again."); return; }
            if ((DateTime.UtcNow - _lastAt).TotalMinutes > ResultMinutes)
            { Wave9.Tell(sender, $"That comparison is more than {ResultMinutes:0} minutes old and the world has moved on - compare again."); return; }
            if (r.A != Live) { Wave9.Tell(sender, "Restoring needs the live world as side A."); return; }
            var z = ZoneSystem.GetZone(zonePos);
            var man = ZDOMan.instance;
            if (man == null) return;
            var done = 0;
            var skipped = 0;
            var kept = 0;
            string label;
            switch (what)
            {
                case 1:
                    label = "recreated removed pieces";
                    List<Obj> removed;
                    if (r.Removed.TryGetValue(z, out removed))
                    {
                        var near = new List<ZDO>();
                        foreach (var o in removed)
                        {
                            if (done >= RestoreCap) break;
                            // Rebuilt since the comparison (or restored by another admin): never stack a duplicate.
                            Wave9.ZdosInRadius(o.Pos, 0.3f, near);
                            var exists = near.Exists(x => x.GetPrefab() == o.Prefab && (x.GetPosition() - o.Pos).sqrMagnitude < 0.04f);
                            near.Clear();
                            if (exists) { skipped++; continue; }
                            if (Recreate(man, o)) done++;
                        }
                        r.Removed.Remove(z);
                    }
                    break;
                case 2:
                    label = "restored container contents";
                    List<KeyValuePair<Obj, Obj>> changed;
                    if (r.Changed.TryGetValue(z, out changed))
                    {
                        foreach (var p in changed) if (RestoreItems(man, p.Key, p.Value)) done++;
                        r.Changed.Remove(z);
                    }
                    break;
                case 3:
                    label = "removed pieces added since the backup";
                    List<Obj> added;
                    if (r.Added.TryGetValue(z, out added))
                    {
                        foreach (var o in added)
                        {
                            if (done >= RestoreCap) break;
                            var zdo = man.GetZDO(o.LiveId);
                            if (zdo == null || zdo.GetPrefab() != o.Prefab || (zdo.GetPosition() - o.Pos).sqrMagnitude > 0.04f) { skipped++; continue; }
                            // A chest built since the backup may hold a player's things: keep it, say so.
                            var blob = InvCodec.ReadBlob(zdo);
                            if ((Wave9.KindOf(o.Prefab) & Wave9.KContainer) != 0 && blob != null && InvCodec.Parse(blob, out _, out _, ScratchRecs) && ScratchRecs.Count > 0)
                            { ScratchRecs.Clear(); kept++; continue; }
                            ScratchRecs.Clear();
                            if (Wave9.DestroyZdo(zdo)) done++;
                        }
                        r.Added.Remove(z);
                    }
                    break;
                default: return;
            }
            var extra = (skipped > 0 ? $"; {skipped} skipped (changed since the comparison)" : "") + (kept > 0 ? $"; {kept} container(s) holding items kept" : "");
            CompanionPlugin.SrvAudit(sender, "WorldDiffRestore", $"{label} in zone {z.x},{z.y} from {r.B}: {done} object(s){extra}");
            Wave9.Tell(sender, $"Backup diff: {label} - {done} object(s) in zone around {Wave9.F(zonePos.x)},{Wave9.F(zonePos.z)}{extra}.");
        }

        private static readonly List<InvCodec.Rec> ScratchRecs = new List<InvCodec.Rec>();

        internal static bool Recreate(ZDOMan man, Obj o)
        {
            try
            {
                var zdo = man.CreateNewZDO(o.Pos, o.Prefab);
                zdo.SetPrefab(o.Prefab);
                zdo.Persistent = true;   // player-built objects are always persistent
                zdo.Distant = (o.Flags & 0x200) != 0;
                zdo.Type = (ZDO.ObjectType)((o.Flags >> 10) & 3);
                zdo.SetRotation(Quaternion.Euler(o.Rot));
                if (o.Floats != null) foreach (var kv in o.Floats) zdo.Set(kv.Key, kv.Value);
                if (o.Vec3s != null) foreach (var kv in o.Vec3s) zdo.Set(kv.Key, kv.Value);
                if (o.Quats != null) foreach (var kv in o.Quats) zdo.Set(kv.Key, kv.Value);
                if (o.Ints != null) foreach (var kv in o.Ints) zdo.Set(kv.Key, kv.Value);
                if (o.Longs != null) foreach (var kv in o.Longs) zdo.Set(kv.Key, kv.Value);
                if (o.Strings != null) foreach (var kv in o.Strings) zdo.Set(kv.Key, kv.Value ?? "");
                if (o.Bytes != null) foreach (var kv in o.Bytes) zdo.Set(kv.Key, kv.Value);
                return true;
            }
            catch (Exception e) { Wave9.ThrottledLog("wdiff-recreate", $"Backup diff: recreating {Wave9.PrefabName(o.Prefab)} failed: {e.Message}"); return false; }
        }

        private static bool RestoreItems(ZDOMan man, Obj live, Obj backup)
        {
            try
            {
                var zdo = man.GetZDO(live.LiveId);
                if (zdo == null || zdo.GetInt(KeyInUse, 0) == 1) return false;   // never under a player's open chest
                // The chest module's own write discipline: claim, write, hand back to a CONNECTED owner (else
                // release), force the new contents out, and refresh the host's loaded copy on a listen server.
                Wave8Objects.WriteContainerBlob(zdo, backup.Items);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}

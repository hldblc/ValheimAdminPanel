using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace AdminPanelCompanion
{
    // ==================== Server-side persistence (the mod's first durable store) ====================
    // Everything before this was in-memory and died with the process (UndoHistory, join log). Feature
    // modules need durable per-world state: roles, temp-bans, warnings, ledgers, audit trail.
    //
    // Format: line-based "key=value" tables (net48 ships no JSON parser and the mod ships no deps — same
    // reasoning as the Loc system) plus append-only logs. All paths are ABSOLUTE (the 2026-07-22 locale
    // truncation came from relative [IO.File] paths). Writes are atomic: temp file then File.Replace.
    // Thread-safe via one gate: callers include the main thread, the save worker thread (backup postfix)
    // and webhook Tasks.
    internal static class FeatureStore
    {
        private static readonly object Gate = new object();
        private static string _dir;        // per-world data dir; null until a world is resolvable
        private static string _worldKey;   // world fileName the cache belongs to
        private static DateTime _nextDirRetry;      // back-off after a failed CreateDirectory
        private static bool _dirFailureLogged;      // one line per world, not one per call
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>();

        internal static bool Ready { get { lock (Gate) return ResolveDirLocked() != null; } }

        internal static string DataDir { get { lock (Gate) return ResolveDirLocked(); } }

        // World identity comes from the loaded World's fileName (already filesystem-safe). ZNet.m_world is
        // static in current assemblies but accessed reflectively with a property fallback so a game update
        // renaming it degrades to "store unavailable" instead of a crash.
        private static string ResolveDirLocked()
        {
            var world = CurrentWorldFileName();
            if (world == null) return null;   // no world loaded (yet): not ready, but the cache still stands
            if (world == _worldKey)
            {
                // Same world. A previous CreateDirectory failure leaves _dir null; that must NOT fall through
                // into the world-change branch below, which clears Tables — every in-memory edit made through
                // Table() would be dropped on the next call, and SaveTable would then persist nothing.
                // Retry the creation occasionally instead (a full/read-only volume can come back).
                if (_dir != null || DateTime.UtcNow < _nextDirRetry) return _dir;
                return CreateDirLocked(world);
            }
            // World changed (server restart into another world, or host loaded a different save): drop the
            // old world's cached tables so nothing leaks across worlds.
            Tables.Clear();
            LogBytes.Clear();
            _worldKey = world;
            _dirFailureLogged = false;
            return CreateDirLocked(world);
        }

        private static string CreateDirLocked(string world)
        {
            var candidate = Path.Combine(Paths.ConfigPath, "AdminPanelCompanion", world);
            try
            {
                Directory.CreateDirectory(candidate);
                _dir = candidate;
                _nextDirRetry = DateTime.MinValue;
            }
            catch (Exception e)
            {
                _dir = null;
                _nextDirRetry = DateTime.UtcNow.AddSeconds(30);
                if (!_dirFailureLogged)
                {
                    _dirFailureLogged = true;
                    CompanionPlugin.FeatureLog(
                        $"FeatureStore: cannot create {candidate} ({e.Message}) - nothing persists for this world.");
                }
            }
            return _dir;
        }

        private static string CurrentWorldFileName()
        {
            try
            {
                object w = AccessTools.Property(typeof(ZNet), "World")?.GetValue(null)
                           ?? AccessTools.Field(typeof(ZNet), "m_world")?.GetValue(null);
                if (w == null) return null;
                var name = AccessTools.Field(w.GetType(), "m_fileName")?.GetValue(w) as string;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception) { return null; }
        }

        // ---- key=value tables (cached; explicit SaveTable persists) ----

        internal static Dictionary<string, string> Table(string name)
        {
            lock (Gate)
            {
                if (Tables.TryGetValue(name, out var cached)) return cached;
                var t = new Dictionary<string, string>(StringComparer.Ordinal);
                var dir = ResolveDirLocked();
                if (dir != null)
                {
                    var path = Path.Combine(dir, name + ".txt");
                    try
                    {
                        if (File.Exists(path))
                            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                            {
                                if (line.Length == 0 || line[0] == '#') continue;
                                var eq = line.IndexOf('=');
                                if (eq <= 0) continue;
                                t[Unescape(line.Substring(0, eq))] = Unescape(line.Substring(eq + 1));
                            }
                    }
                    catch (Exception) { /* unreadable table = start empty; SaveTable rewrites it */ }
                }
                Tables[name] = t;
                return t;
            }
        }

        internal static void SaveTable(string name)
        {
            lock (Gate)
            {
                var dir = ResolveDirLocked();
                if (dir == null || !Tables.TryGetValue(name, out var t)) return;
                var path = Path.Combine(dir, name + ".txt");
                var tmp = path + ".tmp";
                try
                {
                    var sb = new StringBuilder();
                    foreach (var kv in t)
                        sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value)).Append("\r\n");
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (Exception)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                }
            }
        }

        // ---- append-only logs (audit, mod-log, chat history). Flushed per write: entries are rare
        // (admin actions), and durability across a crash is the whole point of an audit trail. ----

        // Nothing ever truncated these files, so a long-lived world grew audit.log/chat.log without bound
        // until the volume filled and every write here started failing silently. One previous generation is
        // kept as <log>.1.log; readers already tolerate a shorter or absent file.
        private const long MaxLogBytes = 8L * 1024 * 1024;
        private static readonly Dictionary<string, long> LogBytes =
            new Dictionary<string, long>(StringComparer.Ordinal);

        internal static void Append(string log, string line)
        {
            lock (Gate) AppendLocked(log, LogLine(line));
        }

        // Batch form for callers that buffer their lines (the audit trail): one open/append/close for the
        // whole batch instead of one per line, which matters when the writer sits in an RPC prefix.
        internal static void AppendBatch(string log, List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(LogLine(l));
            lock (Gate) AppendLocked(log, sb.ToString());
        }

        private static string LogLine(string line) =>
            (line ?? "").Replace("\r", " ").Replace("\n", " ") + "\r\n";

        private static void AppendLocked(string log, string payload)
        {
            var dir = ResolveDirLocked();
            if (dir == null) return;
            try
            {
                var path = Path.Combine(dir, log + ".log");
                var bytes = Encoding.UTF8.GetByteCount(payload);
                RotateLocked(log, dir, path, bytes);
                File.AppendAllText(path, payload, new UTF8Encoding(false));
                long cur;
                LogBytes.TryGetValue(log, out cur);
                LogBytes[log] = cur + bytes;
            }
            catch (Exception) { /* full disk / locked file must never take down an RPC handler */ }
        }

        // The size check runs off a cached byte count (seeded by one stat per log per world) so an append
        // never costs a file-metadata call, however hot the caller is.
        private static void RotateLocked(string log, string dir, string path, int adding)
        {
            long cur;
            if (!LogBytes.TryGetValue(log, out cur))
            {
                try { cur = File.Exists(path) ? new FileInfo(path).Length : 0L; }
                catch (Exception) { cur = 0L; }
                LogBytes[log] = cur;
            }
            if (cur + adding <= MaxLogBytes) return;
            try
            {
                var prev = Path.Combine(dir, log + ".1.log");
                if (File.Exists(prev)) File.Delete(prev);
                if (File.Exists(path)) File.Move(path, prev);
            }
            catch (Exception) { /* rotation is best-effort; the append still lands in the live file */ }
            LogBytes[log] = 0L;   // reset either way: a failing move must not be retried on every append
        }

        // Reading the whole file was affordable when logs were small, but audit.log/chat.log grow for the
        // life of the world and this runs on the main thread inside RPC handlers that the panel polls every
        // 20s. Cost must stay proportional to n, not to file size: seek to the end and walk backwards a
        // chunk at a time until enough newlines are in hand (or the byte budget is spent), then decode only
        // that tail. Ordering is unchanged - oldest first, newest last.
        private const int TailChunkBytes = 64 * 1024;
        private const long TailBudgetBytes = 4L * 1024 * 1024;

        internal static List<string> Tail(string log, int n)
        {
            lock (Gate)
            {
                var res = new List<string>();
                var dir = ResolveDirLocked();
                if (dir == null || n <= 0) return res;
                try
                {
                    var path = Path.Combine(dir, log + ".log");
                    if (!File.Exists(path)) return res;
                    string text;
                    bool partialHead;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var pos = fs.Length;
                        var scanned = 0L;
                        var newlines = 0;
                        var chunks = new List<byte[]>();
                        // A 0x0A byte can never occur inside a multi-byte UTF-8 sequence, so counting bytes
                        // is safe even though a chunk boundary may split a character: all chunks are joined
                        // before decoding. n+1 newlines are needed because the first one delimits the head
                        // of the oldest wanted line.
                        while (pos > 0 && newlines <= n && scanned < TailBudgetBytes)
                        {
                            var take = (int)Math.Min(TailChunkBytes, pos);
                            pos -= take;
                            fs.Seek(pos, SeekOrigin.Begin);
                            var buf = new byte[take];
                            var got = 0;
                            while (got < take)
                            {
                                var r = fs.Read(buf, got, take - got);
                                if (r <= 0) break;
                                got += r;
                            }
                            if (got < take) { pos += take; break; }   // short read: keep what we already have
                            for (var i = 0; i < take; i++) if (buf[i] == (byte)'\n') newlines++;
                            chunks.Insert(0, buf);
                            scanned += take;
                        }
                        partialHead = pos > 0;
                        var total = 0;
                        foreach (var c in chunks) total += c.Length;
                        var all = new byte[total];
                        var off = 0;
                        foreach (var c in chunks) { Buffer.BlockCopy(c, 0, all, off, c.Length); off += c.Length; }
                        text = new UTF8Encoding(false).GetString(all);
                    }
                    var parts = text.Split('\n');
                    var first = partialHead ? 1 : 0;   // the head chunk starts mid-line unless we reached byte 0
                    var last = parts.Length - 1;
                    if (last >= first && parts[last].Length == 0) last--;   // logs end with a newline
                    for (var i = Math.Max(first, last - n + 1); i <= last; i++) res.Add(parts[i].TrimEnd('\r'));
                }
                catch (Exception) { }
                return res;
            }
        }

        // Same escaping family as the panel's ParseKv: %, =, and newlines survive round-trips.
        private static string Escape(string s) =>
            s.Replace("%", "%25").Replace("=", "%3D").Replace("\r", "%0D").Replace("\n", "%0A");

        private static string Unescape(string s) =>
            s.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%3D", "=").Replace("%25", "%");
    }
}

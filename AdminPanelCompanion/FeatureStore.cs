using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;

namespace AdminPanelCompanion
{
    // ==================== Server-side persistence (the mod's first durable store) ====================
    // Everything before this was in-memory and died with the process (UndoHistory, join log). Feature
    // modules need durable per-world state: roles, temp-bans, warnings, ledgers, audit trail.
    //
    // Format: line-based "key=value" tables (net48 ships no JSON parser and the mod ships no deps — same
    // reasoning as the Loc system) plus append-only logs. All paths are ABSOLUTE (the 2026-07-22 locale
    // truncation came from relative [IO.File] paths). Writes are atomic: temp file then File.Replace.
    // One gate serialises every table/log access. Callers are MAIN-THREAD ONLY today: the save-worker
    // postfixes only touch atomics, the webhook Tasks receive plain strings and byte arrays, and the backup
    // worker reaches only ScanChunkedSet, which is pure System.IO and takes no gate. The gate is insurance for
    // the future, not a licence to call the world helpers below from a worker — those are main-thread only by
    // contract (see that section).
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

        // World identity comes from the loaded World's on-disk name (already filesystem-safe), read through
        // the cached, property-first ZNet.World lookup further down. A game update that renames a member
        // degrades to "store unavailable" (reported once) instead of a crash.
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
            return WorldFileName(CurrentWorld());
        }

        // ==================== World identity and save layout (silent reflection, cached) ====================
        // MAIN THREAD ONLY. Every helper here reads ZNet.World, and the path helpers call World.GetSaveDirectory /
        // GetSavePaths / GetDBPath / GetMetaPath, which run through SaveSystem.GetWorldsSaveRootPath ->
        // Utils.GetSaveDataPath (SaveSystem.cs:907-910, Utils.cs:177-188): that consults
        // FileHelpers.CloudStorageSupportedAndEnabled (the platform save-data provider, FileHelpers.cs:74-83) and
        // Utils.persistantDataPath (Application.persistentDataPath cached at type init, Utils.cs:156). None of it
        // is documented thread-safe, so callers stay on the main thread; the backup worker receives resolved
        // strings, never a World.
        //
        // 1.0.12 shapes (decompile-1.0.12/valheim/World.cs): the on-disk name is World.m_worldName (:20; the
        // m_fileName of earlier builds is gone), m_name is the display name (:22), m_fileSource the
        // FileHelpers.FileSource (:54; Auto=1 Local=2 Cloud=4 Legacy=8, FileHelpers.cs:19-25). A world saved by
        // 1.0.12 is CHUNKED: IsChunkedSave() (:386) reports the layout the world was LOADED from (m_chunkedSave is
        // only set in LoadWorld :316, never after a save), GetSaveDirectory(m_fileSource) (:91) is
        // "<worlds root>/<m_worldName>/", GetSavePaths() (:126) lists that directory for a chunked world and the
        // legacy .db + .fwl pair otherwise, and GetDBPath() (:106, public) / GetMetaPath() (:116, private) are the
        // legacy pair paths.
        //
        // The world lookup is PROPERTY-FIRST: ZNet.World (public static, ZNet.cs:327) and then the static m_world
        // field it wraps (ZNet.cs:239). Plain Type.GetField/GetMethod are used ON PURPOSE: AccessTools logs a
        // HarmonyX warning on every miss and CurrentWorld() runs on every store access — on the GTX server that
        // was ~30 warning lines per second and a 213 MB log in a day. Every member is probed ONCE (per World
        // type); a miss is cached and reported with ONE FeatureLog line naming the member and the game version,
        // and the helper then answers null/false for the rest of the process. A miss is a miss: no fallbacks.
        private static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static PropertyInfo _znetWorldProp;
        private static FieldInfo _znetWorldField;
        private static bool _znetProbed;
        private static Type _worldType;
        private static FieldInfo _worldNameField;          // m_worldName
        private static FieldInfo _worldDisplayNameField;   // m_name
        private static FieldInfo _worldFileSourceField;    // m_fileSource
        private static MethodInfo _worldIsChunkedMethod;   // bool IsChunkedSave()
        private static MethodInfo _worldSaveDirMethod;     // string GetSaveDirectory(FileHelpers.FileSource)
        private static MethodInfo _worldSavePathsMethod;   // List<string> GetSavePaths()
        private static MethodInfo _worldDbPathMethod;      // string GetDBPath()
        private static MethodInfo _worldMetaPathMethod;    // string GetMetaPath()

        private static string GameVersion()
        {
            try { return global::Version.GetVersionString(false); }
            catch (Exception) { return "?"; }
        }

        private static void ReportMisses(List<string> missing)
        {
            if (missing == null) return;
            foreach (var m in missing)
                CompanionPlugin.FeatureLog(
                    $"FeatureStore: {m} not found on this game build (Valheim {GameVersion()}); every feature that needs it is degraded for this process.");
        }

        /// <summary>The loaded World (ZNet.World), or null when none is loaded or the lookup is unavailable.</summary>
        internal static object CurrentWorld()
        {
            try
            {
                List<string> missing = null;
                lock (Gate)
                {
                    if (!_znetProbed)
                    {
                        var prop = typeof(ZNet).GetProperty("World", AnyStatic);
                        var field = typeof(ZNet).GetField("m_world", AnyStatic);
                        if (prop == null && field == null) (missing = new List<string>()).Add("ZNet.World / ZNet.m_world");
                        _znetWorldProp = prop;
                        _znetWorldField = field;
                        _znetProbed = true;   // published LAST, under the gate: readers never see the flag before the handles
                    }
                }
                ReportMisses(missing);   // outside the gate: logging must not run under the store lock
                return _znetWorldProp?.GetValue(null, null) ?? _znetWorldField?.GetValue(null);
            }
            catch (Exception) { return null; }
        }

        private static void ProbeWorldType(Type t)
        {
            List<string> missing = null;
            lock (Gate)
            {
                if (_worldType == t) return;
                var name = t.GetField("m_worldName", AnyInstance);
                var display = t.GetField("m_name", AnyInstance);
                var source = t.GetField("m_fileSource", AnyInstance);
                var chunked = t.GetMethod("IsChunkedSave", AnyInstance, null, Type.EmptyTypes, null);
                var saveDir = source != null
                    ? t.GetMethod("GetSaveDirectory", AnyInstance, null, new[] { source.FieldType }, null)
                    : null;
                var savePaths = t.GetMethod("GetSavePaths", AnyInstance, null, Type.EmptyTypes, null);
                var db = t.GetMethod("GetDBPath", AnyInstance, null, Type.EmptyTypes, null);
                var meta = t.GetMethod("GetMetaPath", AnyInstance, null, Type.EmptyTypes, null);
                if (name == null) (missing ?? (missing = new List<string>())).Add("World.m_worldName");
                if (display == null) (missing ?? (missing = new List<string>())).Add("World.m_name");
                if (source == null) (missing ?? (missing = new List<string>())).Add("World.m_fileSource");
                if (chunked == null) (missing ?? (missing = new List<string>())).Add("World.IsChunkedSave()");
                if (saveDir == null) (missing ?? (missing = new List<string>())).Add("World.GetSaveDirectory(FileSource)");
                if (savePaths == null) (missing ?? (missing = new List<string>())).Add("World.GetSavePaths()");
                if (db == null) (missing ?? (missing = new List<string>())).Add("World.GetDBPath()");
                if (meta == null) (missing ?? (missing = new List<string>())).Add("World.GetMetaPath()");
                _worldNameField = name;
                _worldDisplayNameField = display;
                _worldFileSourceField = source;
                _worldIsChunkedMethod = chunked;
                _worldSaveDirMethod = saveDir;
                _worldSavePathsMethod = savePaths;
                _worldDbPathMethod = db;
                _worldMetaPathMethod = meta;
                _worldType = t;   // published LAST: the handles above are visible before the guard
            }
            ReportMisses(missing);
        }

        /// <summary>The world's on-disk name (World.m_worldName: directory / db stem), or null when no world is loaded.</summary>
        internal static string WorldFileName(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                var name = _worldNameField?.GetValue(w) as string;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The world's display name (World.m_name), falling back to the on-disk name; null when no world.</summary>
        internal static string WorldDisplayName(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                var n = _worldDisplayNameField?.GetValue(w) as string;
                return string.IsNullOrEmpty(n) ? WorldFileName(w) : n;
            }
            catch (Exception) { return WorldFileName(w); }
        }

        /// <summary>World.m_fileSource by NAME ("Local", "Cloud", "Legacy", "Auto"); null when unreadable.</summary>
        internal static string WorldFileSource(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                var v = _worldFileSourceField?.GetValue(w);
                return v?.ToString();
            }
            catch (Exception) { return null; }
        }

        /// <summary>World.IsChunkedSave(): the layout the world was LOADED from. False when unreadable.</summary>
        internal static bool WorldIsChunked(object w)
        {
            if (w == null) return false;
            try
            {
                ProbeWorldType(w.GetType());
                return _worldIsChunkedMethod != null && _worldIsChunkedMethod.Invoke(w, null) is bool b && b;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// World.GetSaveDirectory(m_fileSource) as an absolute path without a trailing separator: the chunked
        /// save directory "<worlds root>/<name>" (also where a legacy world is migrated to on its first save).
        /// Null when unreadable or when the source is a cloud path.
        /// </summary>
        internal static string WorldSaveDirectory(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                if (_worldSaveDirMethod == null || _worldFileSourceField == null) return null;
                var raw = _worldSaveDirMethod.Invoke(w, new[] { _worldFileSourceField.GetValue(w) }) as string;
                return FullDirectoryPath(raw);
            }
            catch (Exception) { return null; }
        }

        /// <summary>World.GetSavePaths() verbatim (chunked: the directory; legacy: .db then .fwl); null when unreadable.</summary>
        internal static List<string> WorldSavePaths(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                if (_worldSavePathsMethod == null) return null;
                var res = new List<string>();
                if (_worldSavePathsMethod.Invoke(w, null) is System.Collections.IEnumerable list)
                    foreach (var p in list) if (p is string s && s.Length > 0) res.Add(s);
                return res;
            }
            catch (Exception) { return null; }
        }

        /// <summary>World.GetDBPath(): the legacy "<worlds root>/<name>.db", absolute; null when unreadable.</summary>
        internal static string WorldLegacyDbPath(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                return FullFilePath(_worldDbPathMethod?.Invoke(w, null) as string);
            }
            catch (Exception) { return null; }
        }

        /// <summary>World.GetMetaPath(): the legacy "<worlds root>/<name>.fwl", absolute; null when unreadable.</summary>
        internal static string WorldLegacyMetaPath(object w)
        {
            if (w == null) return null;
            try
            {
                ProbeWorldType(w.GetType());
                return FullFilePath(_worldMetaPathMethod?.Invoke(w, null) as string);
            }
            catch (Exception) { return null; }
        }

        // Engine paths come back with forward slashes and a trailing "/" (persistentDataPath style); a cloud
        // source yields a relative "/worlds/..." that GetFullPath would root on the current drive - reject it.
        private static string FullDirectoryPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.TrimEnd('/', '\\');
            if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed)) return null;
            if (trimmed[0] == '/' || trimmed[0] == '\\')
            {
                // A drive-less root: on Windows that is the cloud-relative shape, on Linux a real absolute path.
                if (Path.DirectorySeparatorChar == '\\') return null;
            }
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string FullFilePath(string raw)
        {
            if (string.IsNullOrEmpty(raw) || !Path.IsPathRooted(raw)) return null;
            if ((raw[0] == '/' || raw[0] == '\\') && Path.DirectorySeparatorChar == '\\') return null;
            return Path.GetFullPath(raw);
        }

        // ==================== World save set: the files a backup must copy and a restore must place ====================
        //
        // Which files make a loadable chunked snapshot, from the engine's own writer and reader (decompile-1.0.12):
        //  * ZNet.SaveWorldThread (ZNet.cs:1801-1912) writes, into World.GetSaveDirectory, _main.N.db2 (:1833,
        //    net time + zones + events), _main.N.fwl2 (World.SaveWorldFWLData :1850 -> World.cs:218, written to
        //    GetSaveFWLPath :96-101; the world meta), the chunk index _main.N.chunks (ZDOMan.SaveChunks :1836 -> ChunkSaveMapping.Save,
        //    ChunkSaveMapping.cs:168-204) and — LAST, only on success — _main.N.ok (:1877-1879). It then deletes the
        //    previous generation's four _main.(N-1).* files (:1880-1883) and the superseded chunk versions (:1884).
        //    N is SaveSystem's save number (BeginSave/EndSave, SaveSystem.cs:115-137).
        //  * Only DIRTY chunks are rewritten (ZDOMan.SaveChunks iterates m_saveData.m_objectsByChunk, ZDOMan.cs:296-330),
        //    each as "<hh>_<ll>__<size>_<version>.chunk" (ChunkSaveMapping.GetChunkFilename :121-145) under a bumped
        //    version, so the live directory holds the current _main.N quartet plus chunk files of many vintages.
        //  * The loader (ZNet.LoadWorld ZNet.cs:1950-1995, ZDOMan.LoadChunks ZDOMan.cs:467-584) opens _main.N.db2 and
        //    then every chunk named by _main.N.chunks; any other .chunk file in the directory is deleted as an
        //    orphan (:551-554). SaveCollection.KeepOnlyNewest (SaveCollection.cs:162-231) recognises a generation
        //    only when all FOUR _main.N files exist and deletes anything else named _main.* as an orphan.
        //  * The engine's own backup and restore copy the WHOLE directory filtered to {.fwl2 .db2 .chunks .ok .chunk}
        //    (SaveSystem.CopyDirectory :371-379 -> FileHelpers.CopyDirectory FileHelpers.cs:257-330; extensions
        //    SaveSystem.s_saveFileExtensions :70).
        // A set is therefore the newest COMPLETE _main.N quartet plus every .chunk file in the directory — the
        // engine's own semantics; a chunk file of an older vintage is harmless because the loader prunes it. The
        // index is parsed only to VERIFY that every chunk it names is present (a set that fails that is not
        // loadable and is never reported as complete).

        /// <summary>Result of scanning one chunked save directory. Pure System.IO — safe on any thread.</summary>
        internal sealed class ChunkedScan
        {
            public int Generation = -1;                  // newest complete _main.N generation, -1 when none
            public List<string> Files = new List<string>();   // absolute paths: the quartet + every .chunk file
            public long Bytes;                           // sum of Files
            public int ChunkFiles;                       // how many of Files are .chunk files
            public int IndexedChunks = -1;               // chunks named by _main.N.chunks, -1 when the index was not parseable
            public List<string> MissingIndexed = new List<string>();   // named by the index but absent on disk
            public bool Verified => IndexedChunks >= 0;  // the index parsed, so MissingIndexed is meaningful
            // "Could not verify" is NOT complete: an unparseable index (future format) must never pass as loadable.
            public bool Complete => Generation >= 0 && IndexedChunks >= 0 && MissingIndexed.Count == 0;
        }

        private static readonly string[] MainExtensions = { ".fwl2", ".db2", ".chunks", ".ok" };

        /// <summary>
        /// Scan a chunked save directory for its newest complete generation and the files that make its loadable
        /// set. Pure System.IO (no Unity, no ZNet, no store): callable from the backup worker and at plugin Awake.
        /// A missing directory yields an empty scan (Generation -1).
        /// </summary>
        internal static ChunkedScan ScanChunkedSet(string directory)
        {
            var scan = new ChunkedScan();
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return scan;
                var byGeneration = new Dictionary<int, HashSet<string>>();
                var chunks = new List<string>();
                foreach (var path in Directory.GetFiles(directory))
                {
                    var file = Path.GetFileName(path);
                    if (file.EndsWith(".chunk", StringComparison.Ordinal)) { chunks.Add(path); continue; }
                    if (!file.StartsWith("_main.", StringComparison.Ordinal)) continue;
                    var rest = file.Substring(6);
                    var dot = rest.IndexOf('.');
                    if (dot <= 0) continue;
                    int n;
                    if (!int.TryParse(rest.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                    var ext = rest.Substring(dot);
                    if (Array.IndexOf(MainExtensions, ext) < 0) continue;
                    HashSet<string> set;
                    if (!byGeneration.TryGetValue(n, out set)) byGeneration[n] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(ext);
                }
                var best = -1;
                foreach (var kv in byGeneration)
                    if (kv.Value.Count == MainExtensions.Length && kv.Key > best) best = kv.Key;
                if (best < 0) return scan;

                scan.Generation = best;
                foreach (var ext in MainExtensions) scan.Files.Add(Path.Combine(directory, "_main." + best + ext));
                chunks.Sort(StringComparer.Ordinal);
                scan.Files.AddRange(chunks);
                scan.ChunkFiles = chunks.Count;
                foreach (var f in scan.Files)
                {
                    try { scan.Bytes += new FileInfo(f).Length; } catch (Exception) { }
                }

                List<string> indexed = null;
                try { indexed = IndexedChunkFiles(Path.Combine(directory, "_main." + best + ".chunks")); }
                catch (Exception) { indexed = null; }
                if (indexed != null)
                {
                    scan.IndexedChunks = indexed.Count;
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var c in chunks) present.Add(Path.GetFileName(c));
                    foreach (var name in indexed)
                        if (!present.Contains(name)) scan.MissingIndexed.Add(name);
                }
            }
            catch (Exception) { }
            return scan;
        }

        // _main.N.chunks layout (ChunkSaveMapping.Save, ChunkSaveMapping.cs:168-204; written raw through ZPackage's
        // BinaryWriter, little-endian, ZPackage.cs:98-136): short format(41), int totalZDOs, int entries, then per
        // entry ushort chunk, byte size, uint version, int numZDOs. The chunk's file name is
        // "<chunk>>8 as x2>_<chunk&0xFF as x2>__<size>_<version>.chunk" (GetChunkFilename :121-145).
        // Returns null when the file does not have exactly that shape: the format is engine-private, so an
        // unrecognised layout means "cannot verify", never a guess.
        private static List<string> IndexedChunkFiles(string indexPath)
        {
            using (var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 10) return null;
                if (br.ReadInt16() != 41) return null;   // format version: the engine writes 41 (ChunkSaveMapping.cs:187); anything else cannot be verified
                br.ReadInt32();                 // total ZDOs
                var entries = br.ReadInt32();
                if (entries < 0 || fs.Length != 10L + 11L * entries) return null;
                var res = new List<string>(entries);
                for (var i = 0; i < entries; i++)
                {
                    var chunk = br.ReadUInt16();
                    var size = br.ReadByte();
                    var version = br.ReadUInt32();
                    br.ReadInt32();             // ZDOs in this chunk
                    res.Add((chunk >> 8).ToString("x2", CultureInfo.InvariantCulture) + "_" +
                            (chunk & 0xFF).ToString("x2", CultureInfo.InvariantCulture) + "__" +
                            size.ToString(CultureInfo.InvariantCulture) + "_" +
                            version.ToString(CultureInfo.InvariantCulture) + ".chunk");
                }
                return res;
            }
        }

        /// <summary>The loaded world's save set as it is ON DISK right now (see ResolveSaveSet).</summary>
        internal sealed class WorldSaveSet
        {
            public string WorldName;      // World.m_worldName
            public string Source;         // FileHelpers.FileSource name: "Local", "Cloud", "Legacy", "Auto"
            public bool IsCloud;          // a cloud source has no local files to copy
            public bool Chunked;          // layout on disk: World.IsChunkedSave() OR a complete _main generation exists
            public string SaveDirectory;  // "<worlds root>/<name>" — the chunked home (absolute, no trailing separator)
            public string WorldsRoot;     // the directory holding SaveDirectory and the legacy pair
            public string LegacyDb;       // "<worlds root>/<name>.db"  (World.GetDBPath)
            public string LegacyFwl;      // "<worlds root>/<name>.fwl" (World.GetMetaPath)
            public int Generation = -1;   // chunked: the newest complete _main.N generation
            public List<string> Files = new List<string>();   // absolute paths of the loadable set
            public long Bytes;            // sum of Files
            public bool Complete;         // chunked: a complete generation whose indexed chunks all exist; legacy: both files
            public int ChunkFiles;        // chunked: .chunk files in the set
            public List<string> EnginePaths;   // World.GetSavePaths() verbatim, for diagnostics (may be null)
        }

        /// <summary>
        /// MAIN THREAD ONLY (see the world helpers). Resolves the loaded world's save set from the World's own
        /// path helpers AND the disk: a legacy world that 1.0.12 migrated during this session still reports
        /// IsChunkedSave()==false (World.cs:316), while its pair has been renamed to "<name>_backup_<stamp>.*"
        /// (SaveSystem.CheckMove :623-658 -> MoveToBackup :735) and its chunked directory exists — the directory
        /// wins. Null when no world is loaded or its paths are unreadable on this build.
        /// </summary>
        internal static WorldSaveSet ResolveSaveSet(object w) => ResolveSaveSet(w, true);

        /// <summary>ResolveSaveSet for the currently loaded world (main thread only).</summary>
        internal static WorldSaveSet ResolveSaveSet() => ResolveSaveSet(CurrentWorld(), true);

        /// <summary>
        /// Layout only, for callers that need the world's name, source, root and directories but not its file
        /// list (the panel's 20-second backup poll must not stat every chunk file of a live world): Files, Bytes
        /// and Generation stay empty and Complete is false. Main thread only.
        /// </summary>
        internal static WorldSaveSet ResolveSaveLayout() => ResolveSaveSet(CurrentWorld(), false);

        internal static WorldSaveSet ResolveSaveSet(object w, bool scanFiles)
        {
            try
            {
                var name = WorldFileName(w);
                if (name == null) return null;
                var db = WorldLegacyDbPath(w);
                var fwl = WorldLegacyMetaPath(w);
                var dir = WorldSaveDirectory(w);
                var source = WorldFileSource(w) ?? "";
                var set = new WorldSaveSet
                {
                    WorldName = name,
                    Source = source,
                    IsCloud = source.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0,
                    SaveDirectory = dir,
                    LegacyDb = db,
                    LegacyFwl = fwl,
                    EnginePaths = WorldSavePaths(w),
                };
                if (set.IsCloud) return set;                 // no local files: nothing further is meaningful
                if (dir == null && db == null) return null;  // neither path helper works on this build
                set.WorldsRoot = Path.GetDirectoryName(dir ?? db);

                if (!scanFiles)
                {
                    set.Chunked = WorldIsChunked(w) || HasMainGeneration(dir);
                    return set;
                }

                var scan = dir != null ? ScanChunkedSet(dir) : new ChunkedScan();
                set.Chunked = WorldIsChunked(w) || scan.Generation >= 0;
                if (set.Chunked)
                {
                    set.Generation = scan.Generation;
                    set.Files = scan.Files;
                    set.Bytes = scan.Bytes;
                    set.ChunkFiles = scan.ChunkFiles;
                    set.Complete = scan.Complete;
                    return set;
                }

                if (db != null && File.Exists(db)) { set.Files.Add(db); set.Bytes += SafeLength(db); }
                if (fwl != null && File.Exists(fwl)) { set.Files.Add(fwl); set.Bytes += SafeLength(fwl); }
                set.Complete = set.Files.Count == 2;
                return set;
            }
            catch (Exception) { return null; }
        }

        // One directory read, no per-file stats: "does this directory hold any finished generation".
        private static bool HasMainGeneration(string dir)
        {
            try { return dir != null && Directory.Exists(dir) && Directory.GetFiles(dir, "_main.*.ok").Length > 0; }
            catch (Exception) { return false; }
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch (Exception) { return 0L; }
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

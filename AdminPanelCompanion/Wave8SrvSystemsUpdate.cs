using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #24 Companion self-update (server) ====================
    // Every stale-companion failure so far came from a manual upload step. This closes the loop in three
    // explicit, admin-clicked steps (nothing here ever runs on its own, and the whole feature is OFF unless
    // EnableSelfUpdate is set):
    //
    //   AP_SrvUpdateCheck  GET https://api.github.com/repos/<SelfUpdateRepo>/releases/latest on a Task,
    //                      parse tag_name + the asset named exactly AdminPanelCompanion.dll with a small
    //                      hand-written scanner (net48 here has no JSON library; the panel's update banner
    //                      regex-matches the same API), compare numerically with PluginVersion.
    //   AP_SrvUpdateStage  download that asset to <plugin dir>\AdminPanelCompanion.dll.staged, verify the
    //                      size and — when GitHub provides one — the SHA-256 digest; otherwise fall back to
    //                      the MZ/PE header plus the assembly NAME read with AssemblyName.GetAssemblyName.
    //                      (Both csproj files pin <Version>1.0.0</Version>, so the ASSEMBLY version is
    //                      always 1.0.0 and says nothing about the plugin version; only the name is checked.)
    //                      A .staged.txt sidecar records version|sha256|size|url.
    //   AP_SrvUpdateApply  File.Replace(staged, live, live.bak). Windows refuses to replace a loaded DLL;
    //                      then a rename dance is tried (a mapped image may be renamed even when it cannot
    //                      be overwritten), and if that fails too the staged file is kept and applied again
    //                      from Init() at the next start — before anything else in this group. On Linux the
    //                      replace succeeds while running and the new build loads at the next restart.
    //
    // HTTP follows the Wave1SrvAuditRpc.PostModLog / Wave34Core contract: Task.Run, TLS 1.2 OR'd in once,
    // a User-Agent header, no Unity/ZNet API off the main thread, results handed back through fields the
    // Tick reads under one gate.
    internal static class Wave8SystemsUpdate
    {
        private const int Ver = 1;
        private const string AssetName = "AdminPanelCompanion.dll";
        private const string DefaultRepo = "hldblc/ValheimAdminPanel";
        private const string StagedSuffix = ".staged";
        private const string InfoSuffix = ".staged.txt";
        private const string TmpSuffix = ".staged.tmp";
        private const string BackupSuffix = ".bak";
        private const int HttpTimeoutMs = 15000;
        private const long MaxDownloadBytes = 32L * 1024 * 1024;
        private const int MaxJsonChars = 4 * 1024 * 1024;
        private const int MaxMessageLen = 200;

        // ---- wire status (AP_UpdateState) ----
        internal const int StIdle = 0;
        internal const int StDisabled = 1;
        internal const int StChecking = 2;
        internal const int StUpToDate = 3;
        internal const int StAvailable = 4;
        internal const int StDownloading = 5;
        internal const int StStaged = 6;
        internal const int StApplied = 7;        // replaced on disk; loads at the next restart
        internal const int StError = 8;
        internal const int StStagedLocked = 9;   // the OS refused to replace the loaded DLL; retried at next start

        private static ConfigEntry<bool> _enableCfg;
        private static ConfigEntry<string> _repoCfg;
        private static bool Enabled => _enableCfg != null && _enableCfg.Value;

        // ---- state (main thread) ----
        private static int _status = StIdle;
        private static string _latest = "";
        private static string _stagedVersion = "";
        private static string _message = "";
        private static string _assetUrl = "";
        private static long _assetSize;
        private static string _assetDigest = "";
        private static bool _busy;
        private static long _requester;   // admin to answer when the worker finishes

        // ---- worker -> main handoff (everything below the gate is plain data) ----
        private static readonly object Gate = new object();
        private static bool _resDone;
        private static int _resKind;      // 1 = check, 2 = stage
        private static bool _resOk;
        private static string _resError;
        private static string _resLatest;
        private static string _resUrl;
        private static long _resSize;
        private static string _resDigest;
        private static string _resSha;
        private static long _resBytes;
        private static int _tlsPrepared;
        private static bool _inited;

        private static readonly Regex RepoShape = new Regex(@"^[A-Za-z0-9_.\-]{1,64}/[A-Za-z0-9_.\-]{1,100}$", RegexOptions.CultureInvariant);

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableCfg = cfg.Bind("Features", "EnableSelfUpdate", false,
                    "Let the owner check GitHub for a newer AdminPanelCompanion.dll, download it next to the running one (.staged) and swap it in from the panel. OFF by default: it contacts github.com and writes into the plugins folder. Nothing is ever applied without an admin click.");
                _repoCfg = cfg.Bind("Features", "SelfUpdateRepo", DefaultRepo,
                    "GitHub repository (owner/name) whose latest release ships an asset named exactly AdminPanelCompanion.dll.");
            }

            CompanionPlugin.RegisterAuditedRpc("AP_SrvUpdateCheck", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUpdateStage", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUpdateApply", null);

            // A staged build from the previous session: apply it now, BEFORE anything else in this group.
            try { RetryStagedAtStartup(); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Self-update: startup retry failed: {e.Message}"); }
        }

        internal static void Tick()
        {
            if (!_inited || !_resDone) return;
            int kind; bool ok; string error, latest, url, digest, sha; long size, bytes;
            lock (Gate)
            {
                if (!_resDone) return;
                _resDone = false;
                kind = _resKind; ok = _resOk; error = _resError; latest = _resLatest; url = _resUrl;
                digest = _resDigest; sha = _resSha; size = _resSize; bytes = _resBytes;
            }
            _busy = false;
            if (kind == 1) FinishCheck(ok, error, latest, url, size, digest);
            else if (kind == 2) FinishStage(ok, error, sha, bytes);
            if (_requester != 0L) SendState(_requester);
        }

        // ==================== paths ====================

        private static string LivePath()
        {
            try
            {
                var info = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Info : null;
                if (info != null && !string.IsNullOrEmpty(info.Location)) return info.Location;
            }
            catch (Exception) { }
            try { return typeof(CompanionPlugin).Assembly.Location; }
            catch (Exception) { return null; }
        }

        private static string Repo()
        {
            var r = _repoCfg != null ? (_repoCfg.Value ?? "").Trim() : DefaultRepo;
            return r.Length == 0 ? DefaultRepo : r;
        }

        private static string Current => CompanionPlugin.PluginVersion ?? "0.0";

        // "v2.5.0" / "2.5" -> Version. System.Version needs at least two components.
        private static bool TryParseVersion(string s, out System.Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else break;   // "2.5.0-beta" -> "2.5.0"
            }
            var core = sb.ToString().Trim('.');
            if (core.Length == 0) return false;
            if (core.IndexOf('.') < 0) core += ".0";
            try { v = new System.Version(core); return true; }
            catch (Exception) { return false; }
        }

        private static void SetMessage(string text) => _message = Wave8Systems.Clamp(text ?? "", MaxMessageLen);

        // ==================== startup retry ====================

        private static void RetryStagedAtStartup()
        {
            var live = LivePath();
            if (string.IsNullOrEmpty(live)) return;
            var staged = live + StagedSuffix;
            if (!File.Exists(staged)) return;

            var stagedVer = ReadInfoVersion(live + InfoSuffix);
            _stagedVersion = stagedVer;
            System.Version sv, cv;   // the game ships its own global Version class
            if (TryParseVersion(stagedVer, out sv) && TryParseVersion(Current, out cv) && sv <= cv)
            {
                // The staged build is already running (or older): it was applied last time, or is stale.
                try { File.Delete(staged); } catch (Exception) { }
                try { File.Delete(live + InfoSuffix); } catch (Exception) { }
                _stagedVersion = "";
                CompanionPlugin.FeatureLog($"Self-update: removed stale staged build {stagedVer} (running {Current}).");
                return;
            }

            string how, err;
            if (TryApply(live, staged, live + BackupSuffix, out how, out err))
            {
                _status = StApplied;
                SetMessage($"Staged build {stagedVer} was applied on disk at startup ({how}); it loads at the next restart. This session still runs {Current}.");
                CompanionPlugin.FeatureLog("Self-update: " + _message);
                try { File.Delete(live + InfoSuffix); } catch (Exception) { }
                _stagedVersion = "";
            }
            else
            {
                _status = StStagedLocked;
                SetMessage($"Staged build {stagedVer} could not replace the loaded DLL at startup ({err}). Stop the server and copy {AssetName}{StagedSuffix} over {AssetName} by hand.");
                CompanionPlugin.FeatureLog("Self-update: " + _message);
            }
        }

        private static string ReadInfoVersion(string infoPath)
        {
            try
            {
                if (!File.Exists(infoPath)) return "";
                foreach (var line in File.ReadAllLines(infoPath, Encoding.UTF8))
                {
                    if (line.StartsWith("version=", StringComparison.Ordinal))
                        return Wave8Systems.Clamp(line.Substring(8).Trim(), 32);
                }
            }
            catch (Exception) { }
            return "";
        }

        // ==================== apply ====================

        // 1) File.Replace (atomic where the OS allows it), 2) rename dance, 3) give up and say why.
        private static bool TryApply(string live, string staged, string bak, out string how, out string error)
        {
            how = ""; error = "";
            try { if (File.Exists(bak)) File.Delete(bak); }
            catch (Exception) { /* an undeletable old backup only matters if Replace needs the name */ }

            try
            {
                File.Replace(staged, live, bak, true);
                how = "File.Replace";
                return true;
            }
            catch (Exception e) { error = e.Message; }

            // Windows will not overwrite a mapped image but often lets it be renamed; try that before giving up.
            var movedLive = false;
            try
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(live, bak);
                movedLive = true;
                File.Move(staged, live);
                how = "rename";
                error = "";
                return true;
            }
            catch (Exception e2)
            {
                error = error + " / rename: " + e2.Message;
                if (movedLive)
                {
                    // Put the live file back so the plugin folder is never left without a DLL.
                    try { if (!File.Exists(live)) File.Move(bak, live); }
                    catch (Exception) { }
                }
                return false;
            }
        }

        // ==================== RPC handlers ====================

        private static bool OwnerGate(long sender, string action)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            return CompanionPlugin.SenderCanFeature(sender, action);
        }

        internal static void OnStateReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvUpdateStateReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvUpdateCheck")) return;
            SendState(sender);
        }

        internal static void OnCheck(long sender)
        {
            if (!OwnerGate(sender,"AP_SrvUpdateCheck")) return;
            CompanionPlugin.SrvAudit(sender, "UPDATE-CHECK", $"repo={Repo()} current={Current}");
            if (!Enabled) { _status = StDisabled; SetMessage("Self-update is disabled: set Features.EnableSelfUpdate = true in the companion config."); SendState(sender); return; }
            if (_busy) { CompanionPlugin.NotifySender(sender, "Self-update: a check or download is still running."); SendState(sender); return; }
            var repo = Repo();
            if (!RepoShape.IsMatch(repo)) { _status = StError; SetMessage($"SelfUpdateRepo '{repo}' is not owner/name."); SendState(sender); return; }
            StartCheck(sender, repo);
            SendState(sender);
        }

        internal static void OnStage(long sender)
        {
            if (!OwnerGate(sender,"AP_SrvUpdateStage")) return;
            CompanionPlugin.SrvAudit(sender, "UPDATE-STAGE", $"latest={_latest} url={_assetUrl} size={_assetSize}");
            if (!Enabled) { _status = StDisabled; SetMessage("Self-update is disabled: set Features.EnableSelfUpdate = true in the companion config."); SendState(sender); return; }
            if (_busy) { CompanionPlugin.NotifySender(sender, "Self-update: a check or download is still running."); SendState(sender); return; }
            if (string.IsNullOrEmpty(_assetUrl) || string.IsNullOrEmpty(_latest)) { SetMessage("Run Check first: no release asset is known yet."); SendState(sender); return; }
            System.Version lv, cv;
            if (!TryParseVersion(_latest, out lv) || !TryParseVersion(Current, out cv) || lv <= cv)
            { _status = StUpToDate; SetMessage($"Nothing to stage: {Current} is already the latest ({_latest})."); SendState(sender); return; }
            var live = LivePath();
            if (string.IsNullOrEmpty(live)) { _status = StError; SetMessage("Cannot locate the running AdminPanelCompanion.dll."); SendState(sender); return; }
            StartStage(sender, live);
            SendState(sender);
        }

        internal static void OnApply(long sender)
        {
            if (!OwnerGate(sender,"AP_SrvUpdateApply")) return;
            if (!Enabled) { _status = StDisabled; SetMessage("Self-update is disabled: set Features.EnableSelfUpdate = true in the companion config."); SendState(sender); return; }
            if (_busy) { CompanionPlugin.NotifySender(sender, "Self-update: a check or download is still running."); SendState(sender); return; }
            var live = LivePath();
            var staged = live != null ? live + StagedSuffix : null;
            if (staged == null || !File.Exists(staged)) { SetMessage("Nothing is staged. Run Check, then Download & stage."); SendState(sender); return; }
            var stagedVer = ReadInfoVersion(live + InfoSuffix);
            string how, err;
            var ok = TryApply(live, staged, live + BackupSuffix, out how, out err);
            CompanionPlugin.SrvAudit(sender, "UPDATE-APPLY", $"staged={stagedVer} result={(ok ? how : "locked")} error={Wave8Systems.Clamp(err, 120)}");
            if (ok)
            {
                _status = StApplied;
                SetMessage($"{AssetName} {stagedVer} is in place ({how}); the previous build is {AssetName}{BackupSuffix}. Restart the server to load it - use the Scheduled restart in Server Tools.");
                try { File.Delete(live + InfoSuffix); } catch (Exception) { }
                _stagedVersion = "";
                Wave1AuditRpc.PostModLog($"UPDATE {Wave1AuditRpc.AdminLabel(sender)} applied companion {stagedVer} (restart pending)");
            }
            else
            {
                _status = StStagedLocked;
                SetMessage($"Staged - the OS refused to replace the loaded DLL ({Wave8Systems.Clamp(err, 90)}). It is retried automatically at the next server start; if that fails too, copy the .staged file over the DLL while the server is stopped.");
            }
            CompanionPlugin.FeatureLog("Self-update: " + _message);
            SendState(sender);
        }

        // ==================== worker: check ====================

        private static void StartCheck(long requester, string repo)
        {
            _busy = true;
            _status = StChecking;
            _requester = requester;
            SetMessage("Checking GitHub for the latest release...");
            var url = "https://api.github.com/repos/" + repo + "/releases/latest";
            var ua = "AdminPanelCompanion/" + Current;
            PrepareTls();
            try
            {
                Task.Run(() =>
                {
                    string json = null, err = null;
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Method = "GET";
                        req.Timeout = HttpTimeoutMs;
                        req.ReadWriteTimeout = HttpTimeoutMs;
                        req.UserAgent = ua;
                        req.Accept = "application/vnd.github+json";
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var s = resp.GetResponseStream())
                        using (var r = new StreamReader(s, Encoding.UTF8))
                            json = ReadCapped(r, MaxJsonChars);
                    }
                    catch (WebException we) { err = DescribeWebError(we); }
                    catch (Exception e) { err = e.Message; }

                    string latest = "", assetUrl = "", digest = "";
                    long size = 0L;
                    if (err == null && !ParseRelease(json, out latest, out assetUrl, out size, out digest))
                        err = "the release response had no tag_name or no asset named " + AssetName;

                    lock (Gate)
                    {
                        _resKind = 1; _resOk = err == null; _resError = err;
                        _resLatest = latest; _resUrl = assetUrl; _resSize = size; _resDigest = digest;
                        _resDone = true;
                    }
                });
            }
            catch (Exception e)
            {
                lock (Gate) { _resKind = 1; _resOk = false; _resError = e.Message; _resDone = true; }
            }
        }

        private static void FinishCheck(bool ok, string error, string latest, string url, long size, string digest)
        {
            if (!ok)
            {
                _status = StError;
                SetMessage("Check failed: " + error);
                CompanionPlugin.FeatureLog("Self-update: " + _message);
                return;
            }
            _latest = Wave8Systems.Clamp(latest, 32);
            _assetUrl = url ?? "";
            _assetSize = size;
            _assetDigest = digest ?? "";
            System.Version lv, cv;
            var newer = TryParseVersion(_latest, out lv) && TryParseVersion(Current, out cv) && lv > cv;
            _status = newer ? StAvailable : StUpToDate;
            SetMessage(newer
                ? $"Update available: {_latest} (running {Current}), {(_assetSize > 0 ? _assetSize / 1024 + " KB" : "size unknown")}{(_assetDigest.Length > 0 ? ", digest provided" : ", no digest")}."
                : $"Up to date: running {Current}, latest release is {_latest}.");
            CompanionPlugin.FeatureLog("Self-update: " + _message);
        }

        // ==================== worker: stage ====================

        private static void StartStage(long requester, string live)
        {
            _busy = true;
            _status = StDownloading;
            _requester = requester;
            SetMessage($"Downloading {AssetName} {_latest}...");
            var url = _assetUrl;
            var expectSize = _assetSize;
            var expectDigest = DigestHex(_assetDigest);
            var version = _latest;
            var tmp = live + TmpSuffix;
            var staged = live + StagedSuffix;
            var info = live + InfoSuffix;
            var ua = "AdminPanelCompanion/" + Current;
            PrepareTls();
            try
            {
                Task.Run(() =>
                {
                    string err = null, sha = "";
                    long total = 0L;
                    try
                    {
                        if (!UrlAllowed(url)) throw new InvalidOperationException("the asset URL is not an https GitHub URL");
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Method = "GET";
                        req.Timeout = HttpTimeoutMs;
                        req.ReadWriteTimeout = HttpTimeoutMs;
                        req.UserAgent = ua;
                        req.AllowAutoRedirect = true;   // browser_download_url redirects to the object store
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            if (resp.ContentLength > MaxDownloadBytes) throw new InvalidOperationException("asset larger than the 32 MB cap");
                            using (var s = resp.GetResponseStream())
                            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            using (var hasher = SHA256.Create())
                            {
                                var buf = new byte[64 * 1024];
                                int n;
                                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                                {
                                    total += n;
                                    if (total > MaxDownloadBytes) throw new InvalidOperationException("asset larger than the 32 MB cap");
                                    fs.Write(buf, 0, n);
                                    hasher.TransformBlock(buf, 0, n, null, 0);
                                }
                                hasher.TransformFinalBlock(buf, 0, 0);
                                sha = ToHex(hasher.Hash);
                            }
                        }

                        // ---- verification (file reads only; nothing here touches Unity) ----
                        if (total == 0L) throw new InvalidOperationException("empty download");
                        if (expectSize > 0L && total != expectSize) throw new InvalidOperationException($"size mismatch: got {total} bytes, GitHub says {expectSize}");
                        if (expectDigest.Length > 0)
                        {
                            if (!string.Equals(sha, expectDigest, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("SHA-256 mismatch against the digest GitHub published");
                        }
                        if (!LooksLikePe(tmp)) throw new InvalidOperationException("the file is not a Windows PE image (no MZ/PE header)");
                        string nameErr;
                        var nameOk = AssemblyNameIs(tmp, "AdminPanelCompanion", out nameErr);
                        if (nameOk == false) throw new InvalidOperationException("the file is not the AdminPanelCompanion assembly");
                        if (nameOk == null && expectDigest.Length == 0)
                            throw new InvalidOperationException("cannot verify the download: GitHub published no digest and the assembly name could not be read (" + nameErr + ")");

                        if (File.Exists(staged)) File.Delete(staged);
                        File.Move(tmp, staged);
                        File.WriteAllText(info,
                            "version=" + version + "\r\nsha256=" + sha + "\r\nsize=" + total.ToString(CultureInfo.InvariantCulture) +
                            "\r\nurl=" + url + "\r\nstaged=" + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\r\n",
                            new UTF8Encoding(false));
                    }
                    catch (WebException we) { err = DescribeWebError(we); }
                    catch (Exception e) { err = e.Message; }
                    if (err != null)
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                    }
                    lock (Gate)
                    {
                        _resKind = 2; _resOk = err == null; _resError = err; _resSha = sha; _resBytes = total;
                        _resDone = true;
                    }
                });
            }
            catch (Exception e)
            {
                lock (Gate) { _resKind = 2; _resOk = false; _resError = e.Message; _resDone = true; }
            }
        }

        private static void FinishStage(bool ok, string error, string sha, long bytes)
        {
            if (!ok)
            {
                _status = StError;
                SetMessage("Download failed: " + error);
                CompanionPlugin.FeatureLog("Self-update: " + _message);
                return;
            }
            _stagedVersion = _latest;
            _status = StStaged;
            SetMessage($"Staged {AssetName} {_latest} ({bytes / 1024} KB, sha256 {Wave8Systems.Clamp(sha, 12)}...). Click Apply to swap it in, then restart the server.");
            CompanionPlugin.FeatureLog("Self-update: " + _message);
        }

        // ==================== reply ====================

        // AP_UpdateState (v1): int ver, int status, string current, string latest, string staged, string message,
        // bool enabled, string repo, long assetSize, bool busy.
        private static void SendState(long sender)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(Enabled ? _status : StDisabled);
            pkg.Write(Current);
            pkg.Write(_latest ?? "");
            pkg.Write(_stagedVersion ?? "");
            pkg.Write(_message ?? "");
            pkg.Write(Enabled);
            pkg.Write(Wave8Systems.Clamp(Repo(), 120));
            pkg.Write(_assetSize);
            pkg.Write(_busy);
            CompanionPlugin.ReplyTo(sender, "AP_UpdateState", pkg);
        }

        // ==================== helpers (worker-safe: no Unity/ZNet) ====================

        private static void PrepareTls()
        {
            if (Interlocked.Exchange(ref _tlsPrepared, 1) != 0) return;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
        }

        private static bool UrlAllowed(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                return host == "github.com" || host.EndsWith(".github.com") || host.EndsWith(".githubusercontent.com");
            }
            catch (Exception) { return false; }
        }

        private static string ReadCapped(StreamReader r, int max)
        {
            var sb = new StringBuilder();
            var buf = new char[16 * 1024];
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                sb.Append(buf, 0, n);
                if (sb.Length > max) throw new InvalidOperationException("response larger than the cap");
            }
            return sb.ToString();
        }

        private static string DescribeWebError(WebException we)
        {
            try
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    var code = (int)resp.StatusCode;
                    resp.Close();
                    if (code == 403) return "HTTP 403 (GitHub API rate limit or access denied)";
                    if (code == 404) return "HTTP 404 (repository or release not found)";
                    return "HTTP " + code;
                }
            }
            catch (Exception) { }
            return we.Status + ": " + we.Message;
        }

        // "sha256:<hex>" -> "<hex>" ("" when absent or in another algorithm).
        private static string DigestHex(string digest)
        {
            if (string.IsNullOrEmpty(digest)) return "";
            var d = digest.Trim();
            if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) d = d.Substring(7);
            else return "";
            return d.Length == 64 ? d.ToLowerInvariant() : "";
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null) return "";
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool LooksLikePe(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 0x40 + 4) return false;
                    var head = new byte[0x40];
                    if (fs.Read(head, 0, head.Length) != head.Length) return false;
                    if (head[0] != (byte)'M' || head[1] != (byte)'Z') return false;
                    var peOffset = BitConverter.ToInt32(head, 0x3C);
                    if (peOffset <= 0 || peOffset + 4 > fs.Length) return false;
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    var sig = new byte[4];
                    if (fs.Read(sig, 0, 4) != 4) return false;
                    return sig[0] == (byte)'P' && sig[1] == (byte)'E' && sig[2] == 0 && sig[3] == 0;
                }
            }
            catch (Exception) { return false; }
        }

        // true/false = the assembly name could be read and does/does not match; null = could not be read.
        private static bool? AssemblyNameIs(string path, string expected, out string error)
        {
            error = "";
            try
            {
                var an = AssemblyName.GetAssemblyName(path);
                if (an == null || string.IsNullOrEmpty(an.Name)) { error = "no assembly name"; return null; }
                return string.Equals(an.Name, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        // ==================== the hand-written release scanner ====================

        // Finds tag_name at the top level and, inside the "assets" array, the object whose "name" is the
        // companion DLL; from that object browser_download_url, size and digest (when GitHub provides one).
        internal static bool ParseRelease(string json, out string tag, out string url, out long size, out string digest)
        {
            tag = ""; url = ""; size = 0L; digest = "";
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var len = json.Length;
                tag = JsonString(json, "tag_name", 0, len) ?? "";
                var assets = JsonKeyValueStart(json, "assets", 0, len);
                if (assets < 0 || tag.Length == 0) return false;
                var i = SkipWs(json, assets, len);
                if (i >= len || json[i] != '[') return false;
                i++;
                while (i < len)
                {
                    i = SkipWs(json, i, len);
                    if (i >= len) break;
                    if (json[i] == ']') break;
                    if (json[i] == ',') { i++; continue; }
                    var end = SkipValue(json, i, len);
                    if (end <= i) break;
                    if (json[i] == '{')
                    {
                        var name = JsonString(json, "name", i, end);
                        if (string.Equals(name, AssetName, StringComparison.Ordinal))
                        {
                            url = JsonString(json, "browser_download_url", i, end) ?? "";
                            size = JsonNumber(json, "size", i, end);
                            digest = JsonString(json, "digest", i, end) ?? "";
                            return url.Length > 0;
                        }
                    }
                    i = end;
                }
                return false;
            }
            catch (Exception) { return false; }
        }

        private static int SkipWs(string s, int i, int to)
        {
            while (i < to && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        // Index just past the colon of `"key":` for the first occurrence inside [from, to), or -1.
        private static int JsonKeyValueStart(string json, string key, int from, int to)
        {
            var needle = "\"" + key + "\"";
            var i = json.IndexOf(needle, from, StringComparison.Ordinal);
            while (i >= 0 && i < to)
            {
                var j = SkipWs(json, i + needle.Length, to);
                if (j < to && json[j] == ':') return j + 1;
                i = json.IndexOf(needle, i + 1, StringComparison.Ordinal);
            }
            return -1;
        }

        private static string JsonString(string json, string key, int from, int to)
        {
            var v = JsonKeyValueStart(json, key, from, to);
            if (v < 0) return null;
            v = SkipWs(json, v, to);
            if (v >= to || json[v] != '"') return null;
            var sb = new StringBuilder();
            var i = v + 1;
            while (i < to)
            {
                var c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < to)
                {
                    var e = json[i + 1];
                    switch (e)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'u':
                            if (i + 5 < to)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                { sb.Append((char)code); i += 6; continue; }
                            }
                            i += 2; continue;
                        default: sb.Append(e); i += 2; continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return null;   // unterminated
        }

        private static long JsonNumber(string json, string key, int from, int to)
        {
            var v = JsonKeyValueStart(json, key, from, to);
            if (v < 0) return 0L;
            v = SkipWs(json, v, to);
            var start = v;
            while (v < to && (char.IsDigit(json[v]) || json[v] == '-')) v++;
            long n;
            return long.TryParse(json.Substring(start, v - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0L;
        }

        // Index just past the JSON value that starts at i (string / object / array / number / literal),
        // honouring escapes inside strings. Returns i on malformed input so callers can stop.
        private static int SkipValue(string s, int i, int to)
        {
            if (i >= to) return i;
            var c = s[i];
            if (c == '"')
            {
                i++;
                while (i < to)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') return i + 1;
                    i++;
                }
                return to;
            }
            if (c == '{' || c == '[')
            {
                var open = c;
                var close = c == '{' ? '}' : ']';
                var depth = 0;
                while (i < to)
                {
                    var d = s[i];
                    if (d == '"') { i = SkipValue(s, i, to); continue; }
                    if (d == open) depth++;
                    else if (d == close)
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                    i++;
                }
                return to;
            }
            while (i < to && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            return i;
        }
    }
}

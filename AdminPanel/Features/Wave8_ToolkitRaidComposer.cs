using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — #22 custom raid composer (client side) ====================
    // Up to five waves of (creature prefab, count, level), seconds between waves, a radius, a banner
    // and an optional duration after which leftovers are removed. The server owns every timer and every
    // spawned object (AP_SrvRaidStart / AP_SrvRaidStop / AP_SrvRaidStateReq -> AP_RaidState); the panel
    // only composes, fires and watches. Presets live in the client config (RaidPresets) so an owner's
    // "arena draugr" recipe survives a relog.
    //
    // IMGUI: five wave rows are ALWAYS drawn (an empty prefab means "no wave"), so the composer's control
    // count never depends on typed text. The creature picker and the preset list are pinned per frame.
    public partial class AdminPanelPlugin
    {
        private const int RaidcWaves = 5;
        private const int RaidcMaxPerWave = 30;
        private const int RaidcMaxTotal = 100;
        private const int RaidcMaxLevel = 10;          // the server clamps to its RaidMaxLevel (default 3)
        private const int RaidcMinInterval = 5, RaidcMaxInterval = 120;
        private const float RaidcMinRadius = 5f, RaidcMaxRadius = 60f;
        private const int RaidcMaxDuration = 3600;
        private const int RaidcMaxBanner = 60;
        private const int RaidcMaxPresets = 20;
        private const int RaidcMaxPresetName = 24;
        private const int RaidcPickMax = 6;

        private ConfigEntry<bool> _raidcSectionCfg;
        private ConfigEntry<string> _raidcPresetsCfg;

        private sealed class RaidcState
        {
            public bool Active, Spawning;
            public int WavesDone, WaveCount, Alive, SpawnedTotal, SecondsLeft;
            public float ToNext;
            public string Banner = "", Starter = "", Note = "";
        }

        private sealed class RaidcPreset
        {
            public string Name = "";
            public string[] Prefab = new string[RaidcWaves];
            public int[] Count = new int[RaidcWaves];
            public int[] Level = new int[RaidcWaves];
            public int Interval = 30, Duration;
            public float Radius = 15f;
            public string Banner = "";
        }

        // ---- composer live state ----
        private readonly string[] _raidcPrefab = new string[RaidcWaves];
        private readonly string[] _raidcCount = new string[RaidcWaves];
        private readonly string[] _raidcLevel = new string[RaidcWaves];
        private int _raidcSel;                       // wave row the picker fills
        private string _raidcPick = "";
        private string _raidcInterval = "30", _raidcRadius = "15", _raidcDuration = "0", _raidcBanner = "";
        private bool _raidcTyped;
        private string _raidcX = "", _raidcZ = "";
        private string _raidcPresetName = "";
        private RaidcState _raidcState;              // null = no reply yet
        private float _raidcNextStateReq, _raidcNextAction;
        private float _raidcFirstReqAt;              // first poll of this session (no-answer detection)

        private const float RaidcNoAnswerSeconds = 10f;
        private string _raidcPresetsRaw;             // config text the parsed list was built from
        private List<RaidcPreset> _raidcPresets;

        // ---- Layout snapshots ----
        private RaidcState _raidcStateLayout;
        private List<CreatureEntry> _raidcMatchesLayout;
        private string _raidcPickSeen;                 // filter text the matches above were built from
        private List<RaidcPreset> _raidcPresetsLayout;
        private bool _raidcReachableLayout;
        private bool _raidcNoAnswerLayout;
        private int _raidcSelLayout;

        // ---- lifecycle ----

        internal void RaidcInit()
        {
            _raidcSectionCfg = Config.Bind("Features", "ShowRaidComposerSection", true,
                "Show the Raid Composer section in the Tools tab (compose waves of creatures and fire them at a position through the companion).");
            _raidcPresetsCfg = Config.Bind("Features", "RaidPresets", "",
                "Saved raid presets (managed from the panel; one preset per ';', fields per '|', waves as prefab:count:level).");
            for (var i = 0; i < RaidcWaves; i++) RaidcClearWave(i);
        }

        internal bool RaidcSectionEnabled() => _raidcSectionCfg == null || _raidcSectionCfg.Value;

        // Composer text stays (it is the admin's draft, not server truth); everything that describes the
        // server just left is cleared.
        internal void RaidcReset()
        {
            _raidcState = null;
            _raidcStateLayout = null;
            _raidcNextStateReq = 0f;
            _raidcNextAction = 0f;
            _raidcFirstReqAt = 0f;
            _raidcMatchesLayout = null;
            _raidcPickSeen = null;
            _raidcReachableLayout = false;
            _raidcNoAnswerLayout = false;
            _raidcPick = "";
        }

        private void RaidcClearWave(int i)
        {
            _raidcPrefab[i] = "";
            _raidcCount[i] = "5";
            _raidcLevel[i] = "1";
        }

        // ---- reply ----

        private static void RaidcOnState(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseRaidState(self, pkg);
        }

        // Wire: int ver=1 | bool active | bool spawning | int wavesDone | int waveCount | int alive |
        //       int spawnedTotal | float secondsToNext | int secondsLeft | string banner | string starter | string note
        internal static void ParseRaidState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var st = new RaidcState
                {
                    Active = pkg.ReadBool(),
                    Spawning = pkg.ReadBool(),
                    WavesDone = pkg.ReadInt(),
                    WaveCount = pkg.ReadInt(),
                    Alive = pkg.ReadInt(),
                    SpawnedTotal = pkg.ReadInt(),
                    ToNext = pkg.ReadSingle(),
                    SecondsLeft = pkg.ReadInt(),
                    Banner = pkg.ReadString() ?? "",
                    Starter = pkg.ReadString() ?? "",
                    Note = pkg.ReadString() ?? "",
                };
                self._raidcState = st;
            }
            catch (Exception) { }
        }

        // ---- requests ----

        // Polled every 3 s while the card is open (Layout pass only). Not audited server-side for that reason.
        private void RaidcPollState()
        {
            if (!_raidcReachableLayout) return;
            if (Time.time < _raidcNextStateReq) return;
            _raidcNextStateReq = Time.time + 3f;
            if (_raidcFirstReqAt <= 0f) _raidcFirstReqAt = Time.time;
            SrvRpc("AP_SrvRaidStateReq");
        }

        private void RaidcFire()
        {
            if (!TkRequireReachable()) return;
            var lp = LocalPlayer;
            if (lp == null) { Message(Loc.T("raidc.msg_no_player")); return; }
            if (Time.time < _raidcNextAction) return;
            _raidcNextAction = Time.time + 2f;

            var prefabs = new List<string>();
            var counts = new List<int>();
            var levels = new List<int>();
            var total = 0;
            for (var i = 0; i < RaidcWaves; i++)
            {
                var p = (_raidcPrefab[i] ?? "").Trim();
                if (p.Length == 0) continue;
                var c = Mathf.Clamp(TkInt(_raidcCount[i], 1), 1, RaidcMaxPerWave);
                var l = Mathf.Clamp(TkInt(_raidcLevel[i], 1), 1, RaidcMaxLevel);
                _raidcCount[i] = TkI(c);
                _raidcLevel[i] = TkI(l);
                if (total + c > RaidcMaxTotal) { Message(Loc.T("raidc.msg_too_many", RaidcMaxTotal)); return; }
                total += c;
                prefabs.Add(p);
                counts.Add(c);
                levels.Add(l);
            }
            if (prefabs.Count == 0) { Message(Loc.T("raidc.msg_no_waves")); return; }

            Vector3 origin;
            bool hasY;
            if (_raidcTyped)
            {
                var x = TkFloat(_raidcX, float.NaN);
                var z = TkFloat(_raidcZ, float.NaN);
                if (float.IsNaN(x) || float.IsNaN(z)) { Message(Loc.T("raidc.msg_bad_coords")); return; }
                origin = new Vector3(x, 0f, z);
                hasY = false;   // the server resolves the ground height
            }
            else
            {
                origin = lp.transform.position;
                hasY = true;
            }

            var interval = Mathf.Clamp(TkInt(_raidcInterval, 30), RaidcMinInterval, RaidcMaxInterval);
            var radius = Mathf.Clamp(TkFloat(_raidcRadius, 15f), RaidcMinRadius, RaidcMaxRadius);
            var duration = Mathf.Clamp(TkInt(_raidcDuration, 0), 0, RaidcMaxDuration);
            var banner = (_raidcBanner ?? "").Trim();
            if (banner.Length > RaidcMaxBanner) banner = banner.Substring(0, RaidcMaxBanner);
            _raidcInterval = TkI(interval);
            _raidcRadius = TkF(radius);
            _raidcDuration = TkI(duration);

            var pkg = new ZPackage();
            pkg.Write(1);                       // payload version
            pkg.Write(origin);
            pkg.Write(hasY);
            pkg.Write(interval);
            pkg.Write(radius);
            pkg.Write(duration);
            pkg.Write(banner);
            pkg.Write(prefabs.Count);
            for (var i = 0; i < prefabs.Count; i++)
            {
                pkg.Write(prefabs[i]);
                pkg.Write(counts[i]);
                pkg.Write(levels[i]);
            }
            SrvRpc("AP_SrvRaidStart", pkg);
            _raidcNextStateReq = 0f;            // fresh state on the next Layout pass
            Message(Loc.T("raidc.msg_fired", prefabs.Count, total));
        }

        private void RaidcStop()
        {
            if (!TkRequireReachable()) return;
            SrvRpc("AP_SrvRaidStop");
            _raidcNextStateReq = 0f;
        }

        // ---- presets (client config) ----
        // Config text: presets joined by ';'; a preset is name|interval|radius|duration|banner|waves;
        // waves are five prefab:count:level triples joined by ','. Every free-text field is escaped so
        // a banner may contain any of the separators.

        private static string RaidcEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("%", "%25").Replace("|", "%7C").Replace(";", "%3B").Replace(",", "%2C").Replace(":", "%3A");
        }

        private static string RaidcUnesc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("%3A", ":").Replace("%2C", ",").Replace("%3B", ";").Replace("%7C", "|").Replace("%25", "%");
        }

        private static List<RaidcPreset> RaidcParsePresets(string raw)
        {
            var res = new List<RaidcPreset>();
            if (string.IsNullOrEmpty(raw)) return res;
            foreach (var chunk in raw.Split(';'))
            {
                if (chunk.Length == 0) continue;
                var f = chunk.Split('|');
                if (f.Length < 6) continue;
                var p = new RaidcPreset
                {
                    Name = RaidcUnesc(f[0]).Trim(),
                    Interval = Mathf.Clamp(TkInt(f[1], 30), RaidcMinInterval, RaidcMaxInterval),
                    Radius = Mathf.Clamp(TkFloat(f[2], 15f), RaidcMinRadius, RaidcMaxRadius),
                    Duration = Mathf.Clamp(TkInt(f[3], 0), 0, RaidcMaxDuration),
                    Banner = RaidcUnesc(f[4]),
                };
                if (p.Name.Length == 0) continue;
                var waves = f[5].Split(',');
                for (var i = 0; i < RaidcWaves; i++)
                {
                    p.Prefab[i] = "";
                    p.Count[i] = 5;
                    p.Level[i] = 1;
                    if (i >= waves.Length) continue;
                    var w = waves[i].Split(':');
                    if (w.Length < 3) continue;
                    p.Prefab[i] = RaidcUnesc(w[0]);
                    p.Count[i] = Mathf.Clamp(TkInt(w[1], 5), 1, RaidcMaxPerWave);
                    p.Level[i] = Mathf.Clamp(TkInt(w[2], 1), 1, RaidcMaxLevel);
                }
                res.Add(p);
                if (res.Count >= RaidcMaxPresets) break;
            }
            return res;
        }

        private static string RaidcSerialize(List<RaidcPreset> list)
        {
            var sb = new StringBuilder();
            foreach (var p in list)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(RaidcEsc(p.Name)).Append('|')
                  .Append(TkI(p.Interval)).Append('|')
                  .Append(TkF1(p.Radius)).Append('|')
                  .Append(TkI(p.Duration)).Append('|')
                  .Append(RaidcEsc(p.Banner)).Append('|');
                for (var i = 0; i < RaidcWaves; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(RaidcEsc(p.Prefab[i])).Append(':').Append(TkI(p.Count[i])).Append(':').Append(TkI(p.Level[i]));
                }
            }
            return sb.ToString();
        }

        // Parsed once per distinct config text (the setter below is the only writer, but the file can
        // be edited by hand while the game runs).
        private List<RaidcPreset> RaidcPresetList()
        {
            var raw = _raidcPresetsCfg != null ? (_raidcPresetsCfg.Value ?? "") : "";
            if (_raidcPresets == null || !string.Equals(raw, _raidcPresetsRaw, StringComparison.Ordinal))
            {
                _raidcPresets = RaidcParsePresets(raw);
                _raidcPresetsRaw = raw;
            }
            return _raidcPresets;
        }

        private void RaidcStorePresets(List<RaidcPreset> list)
        {
            if (_raidcPresetsCfg == null) return;
            var text = RaidcSerialize(list);
            _raidcPresetsCfg.Value = text;    // BepInEx persists on set
            _raidcPresets = list;
            _raidcPresetsRaw = text;
        }

        private void RaidcSavePreset()
        {
            var name = (_raidcPresetName ?? "").Trim();
            if (name.Length == 0) { Message(Loc.T("raidc.msg_preset_name")); return; }
            if (name.Length > RaidcMaxPresetName) name = name.Substring(0, RaidcMaxPresetName);
            var list = new List<RaidcPreset>(RaidcPresetList());
            var p = new RaidcPreset
            {
                Name = name,
                Interval = Mathf.Clamp(TkInt(_raidcInterval, 30), RaidcMinInterval, RaidcMaxInterval),
                Radius = Mathf.Clamp(TkFloat(_raidcRadius, 15f), RaidcMinRadius, RaidcMaxRadius),
                Duration = Mathf.Clamp(TkInt(_raidcDuration, 0), 0, RaidcMaxDuration),
                Banner = (_raidcBanner ?? "").Trim(),
            };
            if (p.Banner.Length > RaidcMaxBanner) p.Banner = p.Banner.Substring(0, RaidcMaxBanner);
            for (var i = 0; i < RaidcWaves; i++)
            {
                p.Prefab[i] = (_raidcPrefab[i] ?? "").Trim();
                p.Count[i] = Mathf.Clamp(TkInt(_raidcCount[i], 5), 1, RaidcMaxPerWave);
                p.Level[i] = Mathf.Clamp(TkInt(_raidcLevel[i], 1), 1, RaidcMaxLevel);
            }
            var idx = list.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) list[idx] = p;
            else if (list.Count >= RaidcMaxPresets) { Message(Loc.T("raidc.msg_presets_full", RaidcMaxPresets)); return; }
            else list.Add(p);
            RaidcStorePresets(list);
            Message(Loc.T("raidc.msg_preset_saved", name));
        }

        private void RaidcLoadPreset(RaidcPreset p)
        {
            if (p == null) return;
            for (var i = 0; i < RaidcWaves; i++)
            {
                _raidcPrefab[i] = p.Prefab[i] ?? "";
                _raidcCount[i] = TkI(p.Count[i]);
                _raidcLevel[i] = TkI(p.Level[i]);
            }
            _raidcInterval = TkI(p.Interval);
            _raidcRadius = TkF1(p.Radius);
            _raidcDuration = TkI(p.Duration);
            _raidcBanner = p.Banner ?? "";
            _raidcPresetName = p.Name;
            Message(Loc.T("raidc.msg_preset_loaded", p.Name));
        }

        private void RaidcDeletePreset(string name)
        {
            var list = new List<RaidcPreset>(RaidcPresetList());
            var n = list.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return;
            RaidcStorePresets(list);
            Message(Loc.T("raidc.msg_preset_deleted", name));
        }

        // ---- creature picker ----

        private List<CreatureEntry> RaidcMatches(string filter)
        {
            var f = (filter ?? "").Trim();
            if (f.Length < 2 || _creatureIndex == null) return null;
            List<CreatureEntry> res = null;
            foreach (var e in _creatureIndex)
            {
                if (e == null) continue;
                if ((e.Display ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (e.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                (res ?? (res = new List<CreatureEntry>(RaidcPickMax))).Add(e);
                if (res.Count >= RaidcPickMax) break;
            }
            return res;
        }

        // ---- draw ----

        private string RaidcStateText(RaidcState st)
        {
            if (st == null)
            {
                if (!_raidcReachableLayout) return Loc.T("raidc.not_connected");
                return Loc.T(_raidcNoAnswerLayout ? "raidc.state_no_answer" : "raidc.state_unknown");
            }
            if (!st.Active) return Loc.T("raidc.state_idle");
            if (st.Spawning)
                return Loc.T("raidc.state_spawning", st.WavesDone, st.WaveCount, st.Alive, TkF(Mathf.Max(0f, st.ToNext)), st.Starter);
            if (st.SecondsLeft >= 0)
                return Loc.T("raidc.state_timed", st.SpawnedTotal, st.Alive, st.SecondsLeft, st.Starter);
            return Loc.T("raidc.state_done", st.SpawnedTotal, st.Alive, st.Starter);
        }

        internal void DrawRaidComposerSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _raidcReachableLayout = TkReachable();
                RaidcPollState();
                _raidcStateLayout = _raidcState;
                _raidcNoAnswerLayout = _raidcState == null && _raidcFirstReqAt > 0f && Time.time - _raidcFirstReqAt > RaidcNoAnswerSeconds;
                // Only re-walk the creature index when the filter text changed (see ItemForge).
                if (!string.Equals(_raidcPick, _raidcPickSeen, StringComparison.Ordinal))
                {
                    _raidcPickSeen = _raidcPick;
                    _raidcMatchesLayout = RaidcMatches(_raidcPick);
                }
                _raidcPresetsLayout = RaidcPresetList();
                _raidcSelLayout = Mathf.Clamp(_raidcSel, 0, RaidcWaves - 1);
            }

            BeginCard(Loc.T("raidc.section"));
            GUILayout.Label(Loc.T("raidc.hint"), _hintStyle);

            // Server state: always exactly two labels (status + note), text swaps only.
            var st = _raidcStateLayout;
            GUILayout.Label(RaidcStateText(st), st != null && st.Active ? _headerStyle : _dimLabelStyle);
            GUILayout.Label(st != null && st.Note.Length > 0 ? st.Note : "", _dimLabelStyle);

            DrawSection(Loc.T("raidc.waves_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("raidc.col_wave"), _headerStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T("raidc.col_prefab"), _headerStyle, GUILayout.Width(200));
            GUILayout.Label(Loc.T("raidc.col_count"), _headerStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("raidc.col_level"), _headerStyle, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            for (var i = 0; i < RaidcWaves; i++)
            {
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                // Radio-style: act only on an off->on FLIP, or the already-selected row (drawn later in
                // the loop) would overwrite a click on an earlier one.
                var wasOn = _raidcSelLayout == i;
                var on = GUILayout.Toggle(wasOn, Loc.T("raidc.wave_n", i + 1), _toggleStyle, GUILayout.Width(70));
                if (on && !wasOn) _raidcSel = i;
                _raidcPrefab[i] = GUILayout.TextField(_raidcPrefab[i] ?? "", 64, _textFieldStyle, GUILayout.Width(200));
                _raidcCount[i] = GUILayout.TextField(_raidcCount[i] ?? "", 3, _textFieldStyle, GUILayout.Width(60));
                _raidcLevel[i] = GUILayout.TextField(_raidcLevel[i] ?? "", 2, _textFieldStyle, GUILayout.Width(60));
                if (GUILayout.Button(Loc.T("raidc.clear_wave"), _buttonStyle, GUILayout.MinWidth(60))) RaidcClearWave(i);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("raidc.pick"), _labelStyle, GUILayout.MinWidth(90));
            _raidcPick = GUILayout.TextField(_raidcPick ?? "", 40, _textFieldStyle, GUILayout.MinWidth(200));
            GUILayout.Label(Loc.T("raidc.pick_target", _raidcSelLayout + 1), _dimLabelStyle);
            GUILayout.EndHorizontal();
            var matches = _raidcMatchesLayout;
            if (matches != null && matches.Count > 0)
            {
                GUILayout.BeginHorizontal();
                for (var i = 0; i < matches.Count; i++)
                    if (GUILayout.Button(matches[i].Display ?? matches[i].Name, _buttonStyle, GUILayout.MinWidth(90)))
                        _raidcPrefab[_raidcSelLayout] = matches[i].Name;
                GUILayout.EndHorizontal();
            }

            DrawSection(Loc.T("raidc.settings_title"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("raidc.interval"), _labelStyle, GUILayout.MinWidth(120));
            _raidcInterval = GUILayout.TextField(_raidcInterval ?? "", 3, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("raidc.radius"), _labelStyle, GUILayout.MinWidth(90));
            _raidcRadius = GUILayout.TextField(_raidcRadius ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("raidc.duration"), _labelStyle, GUILayout.MinWidth(130));
            _raidcDuration = GUILayout.TextField(_raidcDuration ?? "", 4, _textFieldStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("raidc.banner"), _labelStyle, GUILayout.MinWidth(120));
            _raidcBanner = GUILayout.TextField(_raidcBanner ?? "", RaidcMaxBanner, _textFieldStyle, GUILayout.MinWidth(260));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _raidcTyped = GUILayout.Toggle(_raidcTyped, Loc.T("raidc.at_typed"), _toggleStyle, GUILayout.MinWidth(160));
            GUILayout.Label(Loc.T("raidc.axis_x"), _labelStyle, GUILayout.Width(20));
            _raidcX = GUILayout.TextField(_raidcX ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T("raidc.axis_z"), _labelStyle, GUILayout.Width(20));
            _raidcZ = GUILayout.TextField(_raidcZ ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T(_raidcTyped ? "raidc.at_typed_hint" : "raidc.at_me_hint"), _dimLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("raidc.fire"), _buttonStyle, GUILayout.MinWidth(130))) RaidcFire();
            if (ConfirmButton("raidcStop", Loc.T("raidc.stop"), GUILayout.MinWidth(130))) RaidcStop();
            if (GUILayout.Button(Loc.T("raidc.refresh"), _buttonStyle, GUILayout.MinWidth(90))) _raidcNextStateReq = 0f;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("raidc.fire_hint"), _hintStyle);
            EndCard();

            // ---- presets ----
            BeginCard(Loc.T("raidc.presets_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("raidc.preset_name"), _labelStyle, GUILayout.MinWidth(90));
            _raidcPresetName = GUILayout.TextField(_raidcPresetName ?? "", RaidcMaxPresetName, _textFieldStyle, GUILayout.MinWidth(160));
            if (GUILayout.Button(Loc.T("raidc.save"), _buttonStyle, GUILayout.MinWidth(100))) RaidcSavePreset();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var presets = _raidcPresetsLayout;
            if (presets == null || presets.Count == 0)
            {
                GUILayout.Label(Loc.T("raidc.presets_none"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < presets.Count; i++)
                {
                    var p = presets[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(p.Name, _cellStyle, GUILayout.Width(200));
                    GUILayout.Label(Loc.T("raidc.preset_summary", RaidcPresetWaves(p), p.Interval), _dimCellStyle, GUILayout.MinWidth(120));
                    if (GUILayout.Button(Loc.T("raidc.load"), _buttonStyle, GUILayout.MinWidth(70))) RaidcLoadPreset(p);
                    if (GUILayout.Button(Loc.T("raidc.delete"), _buttonStyle, GUILayout.MinWidth(70))) RaidcDeletePreset(p.Name);
                    GUILayout.EndHorizontal();
                }
            }
            EndCard();
        }

        private static int RaidcPresetWaves(RaidcPreset p)
        {
            var n = 0;
            for (var i = 0; i < RaidcWaves; i++) if (!string.IsNullOrEmpty(p.Prefab[i])) n++;
            return n;
        }
    }
}

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 7 — modder SDK + global dry-run contract (client) ====================
    // Two things live in this file:
    //
    //  1. AdminPanelApi — the PUBLIC, stable extension surface. Another BepInEx plugin references
    //     AdminPanel.dll and calls AdminPanelApi.RegisterSection(...) to put its own admin UI inside this
    //     panel's Extras tab, without forking the panel. Everything a third party hands us (a draw
    //     delegate, an enabled predicate, a command runner) is treated as hostile code: it runs inside a
    //     try/catch, its throws are counted, and a section that throws three Layout passes in a row is
    //     switched off instead of taking the panel down with it.
    //
    //  2. The client half of the global dry-run contract. DryRunMode is a panel-wide "simulate" switch.
    //     It is ADVISORY: it can only stop actions that opt in by calling SdkGuardDestructive() before
    //     they send, and it asks the companion to remember the same flag for this admin
    //     (AP_SrvDryRunSet) so server modules that opt in can honour it too. It is NOT a universal
    //     interception of every RPC and this file never claims otherwise — see the hint text and the
    //     server-side comment in Wave7SrvSdk.cs.
    //
    // Member prefix: "Sdk". Locale prefix: "sdk.".
    //
    // IMGUI law: the registry can be mutated at ANY time by a third-party plugin (its own Awake, its own
    // Update, in principle its own OnGUI). Nothing in the draw path reads the live registry — the
    // diagnostics list is flattened into immutable row structs on the Layout pass, and new registrations
    // are queued and only handed to RegisterFeatureSection from SdkTick (LateUpdate), never mid-OnGUI,
    // because RegisterFeatureSection mutates the very list DrawFeaturesTab enumerates.

    /// <summary>
    /// Public extension API for the Advanced Admin Panel (client side).
    ///
    /// <para>Stability contract: every member of this class is versioned by <see cref="ApiVersion"/>.
    /// Members are added, never removed or re-signatured, so a plugin compiled against v1 keeps working.
    /// Guard your calls with <see cref="IsAvailable"/> — the panel may not be installed, and every method
    /// here is a safe no-op that returns false/null rather than throwing when it is not.</para>
    ///
    /// <para>Typical use, from your own plugin's Awake (running BEFORE the panel's Awake is fine —
    /// registrations that arrive early are queued and flushed once the panel starts):</para>
    /// <code>
    /// AdminPanelApi.RegisterSection("MyMod", "My Mod", DrawMyAdminUi, () => MyConfig.Enabled.Value);
    /// AdminPanelApi.RegisterCommand("mymod", "mymod &lt;on|off&gt;", MyCommand);
    /// </code>
    /// </summary>
    public static class AdminPanelApi
    {
        /// <summary>Extension API revision. Bumped only when members are ADDED; existing members never change.</summary>
        public const int ApiVersion = 1;

        // Caps exist so a buggy (or hostile) third party cannot fill the chip row or the command registry.
        internal const int MaxSections = 24;
        internal const int MaxCommands = 64;
        internal const int MaxIdLength = 32;
        internal const int MaxTitleLength = 40;
        internal const int MaxVerbLength = 24;
        internal const int MaxUsageLength = 120;
        internal const int MaxConsecutiveThrows = 3;

        // One gate for the whole registry: callers may be on the main thread (the normal case) or, if a
        // third party is careless, a worker thread. Every read below copies out under the lock.
        internal static readonly object Gate = new object();
        internal static readonly List<SdkSection> AllSections = new List<SdkSection>();
        internal static readonly List<SdkSection> PendingSections = new List<SdkSection>();
        internal static readonly List<SdkCommand> AllCommands = new List<SdkCommand>();

        /// <summary>
        /// True once the panel plugin exists. Registrations made while this is false are still accepted
        /// and applied when the panel starts — check it only when you need to know for other reasons.
        /// </summary>
        public static bool IsAvailable => AdminPanelPlugin.Instance != null;

        /// <summary>The panel's plugin version, e.g. "2.4.0". Never null.</summary>
        public static string PanelVersion => AdminPanelPlugin.PluginVersion;

        /// <summary>
        /// Add a section (one chip) to the panel's Extras tab.
        /// </summary>
        /// <param name="id">
        /// Stable, non-localized identifier, max 32 chars. Used as the registry key and, prefixed with
        /// "Ext:", as the panel's internal section id — so a third-party id can never collide with a
        /// built-in one. Registering the same id twice returns false; the first registration wins.
        /// </param>
        /// <param name="title">
        /// The chip label. NOT localized by the panel — it is YOUR string and is passed through verbatim
        /// (localize it yourself before calling if you ship translations). Max 40 chars; empty falls back
        /// to <paramref name="id"/>.
        /// </param>
        /// <param name="draw">
        /// Your IMGUI drawing code, called once per pass while your chip is selected, from inside the
        /// panel's window. It runs inside the panel's try/catch: an exception is logged with your id and
        /// counted, and after 3 consecutive Layout-pass throws your section is disabled until an admin
        /// re-enables it from the Extensions section. You must obey the same IMGUI rule the panel does —
        /// identical control count on the Layout and Repaint passes of one frame — and you must leave no
        /// unbalanced GUILayout Begin/End pairs. Use the panel's look by drawing plain GUILayout controls.
        /// </param>
        /// <param name="enabled">
        /// Optional visibility predicate, re-checked on every Layout pass; return false to hide the chip
        /// (e.g. your own config toggle). A throwing predicate counts as a throw, exactly like draw.
        /// Null means always visible.
        /// </param>
        /// <returns>True when the section was accepted (or queued for a panel that has not started yet).</returns>
        public static bool RegisterSection(string id, string title, Action draw, Func<bool> enabled = null)
        {
            if (draw == null || string.IsNullOrEmpty(id)) return false;
            id = id.Trim();
            if (id.Length == 0 || id.Length > MaxIdLength) return false;
            title = string.IsNullOrEmpty(title) ? id : title.Trim();
            if (title.Length == 0) title = id;
            if (title.Length > MaxTitleLength) title = title.Substring(0, MaxTitleLength);
            // '\n' in a chip label would grow the tab row unpredictably; strip control characters.
            title = title.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            lock (Gate)
            {
                if (AllSections.Count >= MaxSections)
                {
                    AdminPanelPlugin.SdkWarn($"AdminPanelApi: section '{id}' refused, the {MaxSections}-section limit is reached.");
                    return false;
                }
                foreach (var s in AllSections)
                    if (string.Equals(s.Id, id, StringComparison.Ordinal)) return false;
                var reg = new SdkSection { Id = id, Title = title, Draw = draw, Enabled = enabled };
                AllSections.Add(reg);
                PendingSections.Add(reg);
            }
            // Deliberately NOT flushed here: the panel hands sections to its Extras tab from LateUpdate,
            // because doing it inline could mutate the section list while OnGUI is enumerating it.
            return true;
        }

        /// <summary>
        /// Register a command verb. The panel keeps the registry here; a command palette (when present in
        /// this build) bridges the verbs to its input line. Registering always succeeds independently of
        /// whether a palette exists, so your mod can call this unconditionally.
        /// </summary>
        /// <param name="verb">
        /// Single word, no spaces, max 24 chars. Lower-cased and trimmed. Duplicates are refused.
        /// </param>
        /// <param name="usage">One-line usage/help text shown to the admin. NOT localized by the panel.</param>
        /// <param name="run">
        /// Receives the argument tokens (never null, may be empty) and returns true when it handled the
        /// call. It runs inside a try/catch at the call site; a throw is logged and treated as false.
        /// </param>
        /// <returns>True when the verb was accepted.</returns>
        public static bool RegisterCommand(string verb, string usage, Func<string[], bool> run)
        {
            if (run == null || string.IsNullOrEmpty(verb)) return false;
            verb = verb.Trim().ToLowerInvariant();
            if (verb.Length == 0 || verb.Length > MaxVerbLength || verb.IndexOf(' ') >= 0) return false;
            usage = usage == null ? "" : usage.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (usage.Length > MaxUsageLength) usage = usage.Substring(0, MaxUsageLength);

            lock (Gate)
            {
                if (AllCommands.Count >= MaxCommands)
                {
                    AdminPanelPlugin.SdkWarn($"AdminPanelApi: command '{verb}' refused, the {MaxCommands}-command limit is reached.");
                    return false;
                }
                foreach (var c in AllCommands)
                    if (string.Equals(c.Verb, verb, StringComparison.Ordinal)) return false;
                AllCommands.Add(new SdkCommand { Verb = verb, Usage = usage, Run = run });
            }
            return true;
        }

        /// <summary>
        /// Every registered command, as an immutable snapshot taken under the registry lock. This is the
        /// bridge a command palette consumes; enumerating it never observes a half-written registry and
        /// never throws if another plugin registers while you iterate.
        /// </summary>
        public static IEnumerable<(string verb, string usage, Func<string[], bool> run)> Commands
        {
            get
            {
                var copy = new List<(string, string, Func<string[], bool>)>();
                lock (Gate)
                    foreach (var c in AllCommands)
                        copy.Add((c.Verb, c.Usage, c.Run));
                return copy;
            }
        }

        /// <summary>
        /// Show a short message in the panel's usual message channel (top-left game message + log).
        /// No-op when the panel is not loaded or the local player does not exist yet.
        /// </summary>
        public static void Toast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var self = AdminPanelPlugin.Instance;
            if (self == null) return;
            try { self.SdkToast(message); }
            catch (Exception) { /* a toast must never propagate into the caller */ }
        }

        // ---- internal snapshot helpers (panel-side only) ----

        internal static List<SdkSection> TakePending()
        {
            lock (Gate)
            {
                if (PendingSections.Count == 0) return null;
                var list = new List<SdkSection>(PendingSections);
                PendingSections.Clear();
                return list;
            }
        }

        internal static List<SdkSection> SnapshotSections()
        {
            lock (Gate) return new List<SdkSection>(AllSections);
        }

        internal static List<SdkCommand> SnapshotCommands()
        {
            lock (Gate) return new List<SdkCommand>(AllCommands);
        }

        internal static SdkSection FindSection(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Gate)
                foreach (var s in AllSections)
                    if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
            return null;
        }
    }

    /// <summary>One third-party section registration. Internal: third parties only ever see the API above.</summary>
    internal sealed class SdkSection
    {
        internal string Id;
        internal string Title;
        internal Action Draw;
        internal Func<bool> Enabled;
        internal int Throws;          // CONSECUTIVE Layout-pass throws; a clean Layout pass resets it
        internal int TotalThrows;     // lifetime count, for the diagnostics list
        internal bool Disabled;       // auto-off after MaxConsecutiveThrows, or manually re-enabled
        internal bool ActiveLayout;   // Layout-pinned copy of !Disabled — the draw wrapper gates on THIS
        internal bool Registered;     // already handed to RegisterFeatureSection
        internal string LastError;
    }

    /// <summary>One third-party command verb.</summary>
    internal sealed class SdkCommand
    {
        internal string Verb;
        internal string Usage;
        internal Func<string[], bool> Run;
        internal bool Bridged;        // already handed to the command registrar (see SdkBridgeCommands)
    }

    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _sdkSectionCfg;
        private ConfigEntry<bool> _sdkThirdPartyCfg;
        private ConfigEntry<bool> _sdkDryRunCfg;
        private bool _sdkInited;

        // ---- dry-run session state ----
        private bool _sdkDrySynced;      // pushed this world session already?
        private float _sdkNextSync;

        // ---- diagnostics rows (immutable per-frame snapshots; the draw path reads ONLY these) ----
        private struct SdkRow
        {
            public string Id;
            public string Title;
            public int State;     // 0 active, 1 hidden by the caller's predicate, 2 disabled after errors
            public int Throws;    // lifetime throw count
            public string Err;
        }

        private struct SdkCmdRow
        {
            public string Verb;
            public string Usage;
        }

        // How many registered commands the bridge has already looked at (see SdkBridgeCommands). Process-
        // wide like the registry itself, so SdkReset must not clear it.
        private int _sdkBridgedSeen;

        private List<SdkRow> _sdkRowsLayout;
        private List<SdkCmdRow> _sdkCmdRowsLayout;
        private Vector2 _sdkScroll;

        // ==================== lifecycle ====================

        // Config binds + the first flush of anything a plugin registered before we existed.
        internal void SdkInit()
        {
            if (_sdkInited) return;
            _sdkInited = true;

            _sdkSectionCfg = Config.Bind("Features", "ShowExtensionsSection", true,
                "Show the Extensions section in the Extras tab (dry-run switch + the list of sections/commands other mods registered). Client-side UI only - it changes nothing on its own.");
            _sdkThirdPartyCfg = Config.Bind("Features", "AllowThirdPartySections", true,
                "Let other BepInEx plugins add their own sections to the Extras tab through AdminPanelApi. Inert unless another mod actually calls the API; turn it off to hide every third-party section without uninstalling anything.");
            _sdkDryRunCfg = Config.Bind("Features", "DryRunMode", false,
                "Simulate instead of act: panel features that opt in report what they WOULD do and send nothing. OFF by default. Advisory only - it is honored by the features that call the dry-run guard, not by every action in the mod, and never by other mods.");

            SdkFlushPending();
        }

        internal bool SdkSectionEnabled() => _sdkSectionCfg == null || _sdkSectionCfg.Value;

        // Per-world state only. The registry itself is per-PROCESS: a third-party mod registers once at
        // load, so clearing it on logout would silently lose every extension section for the rest of the
        // session. Dry-run is a client preference (config-backed) and likewise survives; only the
        // "the server knows about it" flag is per-world.
        internal void SdkReset()
        {
            _sdkDrySynced = false;
            _sdkNextSync = 0f;
            _sdkRowsLayout = null;
            _sdkCmdRowsLayout = null;
            _sdkScroll = Vector2.zero;
        }

        // Runs from LateUpdate (never inside OnGUI), which is the only safe place to mutate the Extras
        // tab's section list, and the place the one-shot dry-run push to the server happens.
        internal void SdkTick()
        {
            SdkFlushPending();
            if (!_sdkInited) return;

            if (ZNet.instance == null) { _sdkDrySynced = false; return; }
            if (_sdkDrySynced) return;
            // Only admins who actually switched dry-run on need the server to know; pushing "off" on every
            // world join would add an audited RPC per login for every admin and tell the server nothing it
            // does not already assume (the server-side default is off).
            if (!SdkDryRun) { _sdkDrySynced = true; return; }
            if (Time.time < _sdkNextSync) return;
            _sdkNextSync = Time.time + 5f;
            SdkPushDryRun(false);
        }

        // ==================== third-party section plumbing ====================

        // Hands every queued registration to the Extras tab. The panel's own id namespace is kept clean by
        // the "Ext:" prefix, and the chip label is the caller's raw title (see SdkChipKey).
        internal void SdkFlushPending()
        {
            var pending = AdminPanelApi.TakePending();
            if (pending == null) return;
            foreach (var reg in pending)
            {
                if (reg == null || reg.Registered) continue;
                reg.Registered = true;
                var captured = reg;   // never capture the loop variable
                try
                {
                    RegisterFeatureSection(new FeatureSection
                    {
                        Id = "Ext:" + captured.Id,
                        LocKey = SdkChipKey(captured.Title),
                        Draw = () => SdkRunSection(captured),
                        Enabled = () => SdkSectionVisible(captured),
                    });
                    Logger.LogInfo($"AdminPanelApi: section '{captured.Id}' registered by another mod.");
                }
                catch (Exception e)
                {
                    captured.Disabled = true;
                    captured.LastError = e.Message;
                    Logger.LogWarning($"AdminPanelApi: section '{captured.Id}' could not be added: {e.Message}");
                }
            }
        }

        // ==================== third-party command plumbing ====================

        // Hands every not-yet-bridged verb to a registrar the CALLER supplies. The command runner that
        // actually executes verbs lives in a sibling wave file which this file must not reference (and
        // which cannot reference the SDK registry either), so the glue owns the connection and this method
        // only knows the delegate shape: (verb, usage, run).
        //
        // Two constraints shape it. It must be called from LateUpdate, never from the draw path: the
        // registrar appends to a list the palette enumerates during OnGUI, and growing that list mid-frame
        // would change the control count between the Layout and Repaint passes. And it must be safe to call
        // every tick, because a third-party plugin registers whenever it likes (its own Awake, or minutes
        // later) - Bridged is what stops a verb being pushed twice. Bridged is touched only here, on the
        // main thread, so it needs no lock of its own even though the registry itself has one.
        internal void SdkBridgeCommands(Action<string, string, Func<string[], bool>> register)
        {
            if (register == null) return;
            // Called every frame, so the common case must cost nothing: read the count under the lock and
            // skip the snapshot allocation entirely while no new verb has arrived. A registration that
            // lands between the two reads is picked up on the next tick (the count moves again), and
            // Bridged still guarantees no verb is pushed twice.
            int total;
            lock (AdminPanelApi.Gate) total = AdminPanelApi.AllCommands.Count;
            if (total == _sdkBridgedSeen) return;
            _sdkBridgedSeen = total;

            foreach (var c in AdminPanelApi.SnapshotCommands())
            {
                if (c == null || c.Bridged || c.Run == null || string.IsNullOrEmpty(c.Verb)) continue;
                c.Bridged = true;   // set before the call: a throwing registrar must not be retried forever
                try { register(c.Verb, c.Usage, c.Run); }
                catch (Exception e)
                {
                    Logger.LogWarning($"AdminPanelApi: command '{c.Verb}' could not be bridged: {e.Message}");
                }
            }
        }

        // FeatureSection.LocKey goes through Loc.T, which returns the key itself when it is not a known
        // key — that is exactly the pass-through we want for a caller-supplied title. The only way a title
        // could be mangled is by colliding with a real locale key (they all look like "prefix.name"), so
        // in that one case append a space: still not a key, renders identically to the eye.
        private static string SdkChipKey(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            return Loc.T(title) == title ? title : title + " ";
        }

        // Visibility predicate handed to the Extras tab. Evaluated on the Layout pass by DrawFeaturesTab,
        // so a section that turned itself off becomes invisible on the NEXT frame, never mid-frame.
        private bool SdkSectionVisible(SdkSection reg)
        {
            if (reg == null || reg.Disabled) return false;
            if (_sdkThirdPartyCfg != null && !_sdkThirdPartyCfg.Value) return false;
            if (reg.Enabled == null) return true;
            try { return reg.Enabled(); }
            catch (Exception e) { SdkNoteThrow(reg, e, "enabled predicate"); return false; }
        }

        // The guarded draw. Two separate protections:
        //  * ActiveLayout pins "is this section still on" on the Layout pass, so the disable that a throw
        //    triggers can never change the control count between Layout and Repaint of the same frame.
        //  * The caller's code runs inside one BeginVertical/EndVertical of ours with the End in a finally,
        //    which keeps OUR layout stack balanced for well-behaved callers. A caller that throws with its
        //    own groups still open tears the current frame (IMGUI cannot be unwound from outside); the
        //    next frames are clean because the section is on its way to being disabled.
        private void SdkRunSection(SdkSection reg)
        {
            if (reg == null) return;
            if (Event.current != null && Event.current.type == EventType.Layout)
                reg.ActiveLayout = !reg.Disabled;

            if (!reg.ActiveLayout)
            {
                GUILayout.Label(Loc.T("sdk.disabled_notice", reg.TotalThrows), _hintStyle);
                return;
            }

            var threw = false;
            GUILayout.BeginVertical();
            try { reg.Draw(); }
            catch (Exception e) { threw = true; SdkNoteThrow(reg, e, "draw"); }
            finally
            {
                try { GUILayout.EndVertical(); }
                catch (Exception) { /* the caller unbalanced the stack; next frame re-inits it */ }
            }

            // Counting only on Layout keeps "3 throws" meaning three FRAMES, not three passes, and keeps
            // the disable decision on the pass that is allowed to change what the next frame draws.
            if (!threw && Event.current != null && Event.current.type == EventType.Layout) reg.Throws = 0;
        }

        private void SdkNoteThrow(SdkSection reg, Exception e, string where)
        {
            if (reg == null) return;
            reg.LastError = e == null ? "?" : e.Message;
            reg.TotalThrows++;
            if (Event.current != null && Event.current.type != EventType.Layout) return;
            reg.Throws++;
            Logger.LogWarning($"AdminPanelApi: section '{reg.Id}' threw in its {where} ({reg.Throws}/{AdminPanelApi.MaxConsecutiveThrows}): {reg.LastError}");
            if (reg.Throws < AdminPanelApi.MaxConsecutiveThrows || reg.Disabled) return;
            reg.Disabled = true;
            Logger.LogWarning($"AdminPanelApi: section '{reg.Id}' disabled after {AdminPanelApi.MaxConsecutiveThrows} consecutive errors. Re-enable it in Extras > Extensions.");
            Message(Loc.T("sdk.msg_auto_disabled", reg.Id));
        }

        // Bridges for AdminPanelApi (it is a separate class and cannot reach private members).
        internal void SdkToast(string message) => Message(message);

        internal static void SdkWarn(string message)
        {
            var self = Instance;
            if (self != null) { self.Logger.LogWarning(message); return; }
            try { Debug.LogWarning("[AdminPanel] " + message); }
            catch (Exception) { }
        }

        // ==================== global dry-run contract (client half) ====================

        /// <summary>True when the admin switched the panel into simulate mode.</summary>
        internal bool SdkDryRun => _sdkDryRunCfg != null && _sdkDryRunCfg.Value;

        /// <summary>
        /// The opt-in guard a feature calls immediately before sending a destructive RPC:
        /// <c>if (!SdkGuardDestructive(Loc.T("x.action"))) return;</c>
        /// Returns TRUE when the caller should go ahead. Returns FALSE when dry-run is on — and in that
        /// case it has already shown the "simulated, nothing was sent" message, so the caller just returns.
        /// This is advisory by construction: a feature that never calls it is not affected by dry-run.
        /// </summary>
        internal bool SdkGuardDestructive(string actionName)
        {
            if (!SdkDryRun) return true;
            Message(Loc.T("sdk.dry_blocked", string.IsNullOrEmpty(actionName) ? "?" : actionName));
            return false;
        }

        // Tell the companion this admin is simulating, so server modules that opt in can honour it too.
        private void SdkPushDryRun(bool announce)
        {
            if (ZNet.instance == null)
            {
                if (announce) Message(Loc.T("common.not_connected_srv"));
                return;
            }
            SrvRpc("AP_SrvDryRunSet", SdkDryRun);
            _sdkDrySynced = true;
            if (announce) Message(Loc.T("sdk.msg_resync"));
        }

        // ==================== drawing ====================

        internal void DrawSdkSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _sdkRowsLayout = SdkBuildRows();
                _sdkCmdRowsLayout = SdkBuildCmdRows();
            }

            _sdkScroll = GUILayout.BeginScrollView(_sdkScroll, GUILayout.Height(ListView(150f)));

            SdkDrawDryRunCard();
            SdkDrawApiCard();
            SdkDrawSectionsCard();
            SdkDrawCommandsCard();

            GUILayout.EndScrollView();
        }

        private List<SdkRow> SdkBuildRows()
        {
            var rows = new List<SdkRow>();
            foreach (var s in AdminPanelApi.SnapshotSections())
            {
                if (s == null) continue;
                var state = 0;
                if (s.Disabled) state = 2;
                else if (!SdkSectionVisible(s)) state = 1;
                rows.Add(new SdkRow { Id = s.Id, Title = s.Title, State = state, Throws = s.TotalThrows, Err = s.LastError });
            }
            return rows;
        }

        private List<SdkCmdRow> SdkBuildCmdRows()
        {
            var rows = new List<SdkCmdRow>();
            foreach (var c in AdminPanelApi.SnapshotCommands())
                if (c != null) rows.Add(new SdkCmdRow { Verb = c.Verb, Usage = c.Usage });
            return rows;
        }

        private void SdkDrawDryRunCard()
        {
            BeginCard(Loc.T("sdk.dry_section"));
            GUILayout.BeginHorizontal();
            // A Toggle is one control whatever its value, so reading the live config here cannot change
            // the control count between passes.
            var on = GUILayout.Toggle(SdkDryRun, " " + Loc.T("sdk.dry_toggle"), _toggleStyle);
            if (_sdkDryRunCfg != null && on != _sdkDryRunCfg.Value)
            {
                _sdkDryRunCfg.Value = on;
                Message(Loc.T(on ? "sdk.msg_dry_on" : "sdk.msg_dry_off"));
                SdkPushDryRun(false);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("sdk.resync"), _buttonStyle, GUILayout.MinWidth(120)))
                SdkPushDryRun(true);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("sdk.dry_state"), _labelStyle, GUILayout.MinWidth(70));
            var prev = GUI.contentColor;
            GUI.contentColor = SdkDryRun ? new Color(1f, 0.78f, 0.35f) : prev;
            GUILayout.Label(Loc.T(SdkDryRun ? "sdk.dry_state_on" : "sdk.dry_state_off"), _headerStyle);
            GUI.contentColor = prev;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("sdk.dry_hint"), _proseStyle);
            EndCard();
        }

        private void SdkDrawApiCard()
        {
            var secs = _sdkRowsLayout != null ? _sdkRowsLayout.Count : 0;
            var cmds = _sdkCmdRowsLayout != null ? _sdkCmdRowsLayout.Count : 0;
            BeginCard(Loc.T("sdk.api_section"));
            GUILayout.Label(Loc.T("sdk.api_version", PluginVersion, AdminPanelApi.ApiVersion), _labelStyle);
            GUILayout.Label(Loc.T("sdk.api_counts", secs, cmds), _labelStyle);
            GUILayout.Label(Loc.T("sdk.api_hint"), _proseStyle);
            EndCard();
        }

        private void SdkDrawSectionsCard()
        {
            var rows = _sdkRowsLayout;
            BeginCard(Loc.T("sdk.sections_section"));
            if (rows == null || rows.Count == 0)
            {
                GUILayout.Label(Loc.T("sdk.sections_empty"), _hintStyle);
                EndCard();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("sdk.col_id"), _cellStyle, GUILayout.Width(120));
            GUILayout.Label(Loc.T("sdk.col_title"), _cellStyle, GUILayout.Width(150));
            GUILayout.Label(Loc.T("sdk.col_state"), _cellStyle, GUILayout.Width(110));
            GUILayout.Label(Loc.T("sdk.col_errors"), _cellStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(r.Id ?? "", _cellStyle, GUILayout.Width(120));
                GUILayout.Label(r.Title ?? "", _cellStyle, GUILayout.Width(150));
                var prev = GUI.contentColor;
                if (r.State == 2) GUI.contentColor = new Color(0.95f, 0.45f, 0.40f);
                else if (r.State == 1) GUI.contentColor = new Color(0.72f, 0.68f, 0.60f);
                GUILayout.Label(Loc.T(r.State == 2 ? "sdk.state_disabled" : r.State == 1 ? "sdk.state_hidden" : "sdk.state_active"),
                    _cellStyle, GUILayout.Width(110));
                GUI.contentColor = prev;
                GUILayout.Label(r.Throws.ToString(), _dimCellStyle, GUILayout.Width(60));
                GUILayout.FlexibleSpace();
                // One button on every row, label unchanged by state: the control count is identical for
                // active and disabled rows, and re-enabling an already-active section is a no-op.
                if (GUILayout.Button(Loc.T("sdk.reenable"), _buttonStyle, GUILayout.MinWidth(90)))
                    SdkReenable(r.Id);
                GUILayout.EndHorizontal();
                if (r.State == 2 && !string.IsNullOrEmpty(r.Err))
                    GUILayout.Label(r.Err, _hintStyle);
            }
            EndCard();
        }

        private void SdkDrawCommandsCard()
        {
            var rows = _sdkCmdRowsLayout;
            BeginCard(Loc.T("sdk.cmds_section"));
            if (rows == null || rows.Count == 0) GUILayout.Label(Loc.T("sdk.cmds_empty"), _hintStyle);
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Verb ?? "", _cellStyle, GUILayout.Width(140));
                    GUILayout.Label(string.IsNullOrEmpty(r.Usage) ? "-" : r.Usage, _dimCellStyle, GUILayout.Width(320));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label(Loc.T("sdk.cmds_hint"), _hintStyle);
            EndCard();
        }

        // Clears the throw counters so the section is drawn again on the next frame. The chip comes back
        // through the Extras tab's own Layout-pass rebuild of the enabled list.
        private void SdkReenable(string id)
        {
            var reg = AdminPanelApi.FindSection(id);
            if (reg == null) return;
            if (!reg.Disabled) { Message(Loc.T("sdk.msg_already_active", id)); return; }
            reg.Disabled = false;
            reg.Throws = 0;
            reg.LastError = null;
            Message(Loc.T("sdk.msg_reenabled", id));
        }
    }
}

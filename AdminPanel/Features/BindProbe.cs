using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;

namespace AdminPanel
{
    // ==================== Startup bind probe (client self-check) ====================
    // A Valheim update can rename or reshape an engine member without notice. A mod method whose body
    // references such a member still compiles against the old assembly and only fails at RUN time: Mono's
    // JIT throws MissingMethodException / MissingFieldException / TypeLoadException while compiling the
    // CALLER, the first time it is called — i.e. the first time an admin presses that button, as a silent
    // no-op or one red log line. RuntimeHelpers.PrepareMethod asks the JIT to compile a method right now, so
    // doing that for every method in this assembly at startup turns "some button is dead" into one log
    // summary at load plus a line on the Server tab. The companion runs the same probe server-side and ships
    // its result in AP_HealthData (see CompanionHealth in AdminPanelPlugin.cs); Summary here uses the same
    // probe/checked/failed/names fields so the two read alike.
    //
    // Threading: on Mono, PrepareMethod is NOT pure JIT work — after compiling a method the runtime runs the
    // declaring type's class constructor (mono_jit_compile_method_with_opt → mono_runtime_class_init_full),
    // so a background thread would execute every static initializer in this assembly off the Unity main
    // thread. This probe therefore stays on the main thread and is spread over frames by RunBudgeted
    // (driven by a coroutine in AdminPanelPlugin.StartBindProbe); RunNow is the synchronous fallback.
    // All state below is written and read on the main thread only; the coroutine steps in Unity's coroutine
    // phase, never inside OnGUI, so a frame's Layout and Repaint passes always see the same values.
    //
    // Not touched here: Unity objects, game singletons, the network. Only reflection and the JIT.
    internal static class BindProbe
    {
        private const int LogCap = 50;     // per-failure log lines after the summary
        private const int NamesCap = 8;    // "Type.Method" entries carried in Summary (contract: up to 8)

        public static bool Done { get; private set; }
        public static int Checked { get; private set; }
        public static int Failed { get; private set; }
        public static long ElapsedMs { get; private set; }   // CPU time spent probing (frames yielded in between excluded)

        private static readonly List<string> FailureNames = new List<string>();   // first NamesCap "Type.Method"
        private static string _summary = "probe=skipped;checked=0;failed=0;names=";
        private static bool _started;

        /// <summary>
        /// "probe=ok|failed|skipped;checked=N;failed=M;names=a.b,c.d" — the same fields, in the same order,
        /// as the tail of the companion's AP_HealthData payload. "probe=skipped" until the run completes.
        /// </summary>
        public static string Summary => _summary;

        /// <summary>The first failures as "Type.Method", at most <see cref="NamesCap"/> of them.</summary>
        public static IReadOnlyList<string> Names => FailureNames;

        /// <summary>
        /// Coroutine body: probes <paramref name="asm"/> on the main thread, yielding a frame whenever the
        /// current slice has used <paramref name="budgetMs"/> milliseconds. Idempotent per process.
        /// </summary>
        public static IEnumerator RunBudgeted(Assembly asm, ManualLogSource log, float budgetMs)
        {
            foreach (var _ in Work(asm, log, budgetMs)) yield return null;
        }

        /// <summary>Synchronous fallback: the whole probe in one go, on the calling thread.</summary>
        public static void RunNow(Assembly asm, ManualLogSource log)
        {
            foreach (var _ in Work(asm, log, double.PositiveInfinity)) { }
        }

        private static IEnumerable<object> Work(Assembly asm, ManualLogSource log, double budgetMs)
        {
            if (_started || asm == null) yield break;
            _started = true;

            var work = Stopwatch.StartNew();   // stopped across yields so ElapsedMs is probe time, not wall time
            var slice = Stopwatch.StartNew();
            var failures = new List<string>();  // full "Type.Method: ExceptionType: message" lines for the log
            int checkedCount = 0, failedCount = 0, ignored = 0;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                // Some types could not even be loaded (a base type or interface vanished). Probe the rest and
                // report each loader exception as a failure of its own.
                types = e.Types ?? new Type[0];
                if (e.LoaderExceptions != null)
                    foreach (var le in e.LoaderExceptions)
                    {
                        if (le == null) continue;
                        var bind = BindFailure(le) ?? le;
                        failedCount++;
                        Record(failures, "<type load>", bind);
                    }
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                if (t == null || t.ContainsGenericParameters) continue;   // open generics: nothing concrete to compile

                MethodBase[] members;
                try
                {
                    var methods = t.GetMethods(all);
                    var ctors = t.GetConstructors(all);   // Static flag included: the .cctor is probed too
                    members = new MethodBase[methods.Length + ctors.Length];
                    methods.CopyTo(members, 0);
                    ctors.CopyTo(members, methods.Length);
                }
                catch (Exception e)
                {
                    var bind = BindFailure(e);
                    if (bind != null) { failedCount++; Record(failures, t.Name + ".<members>", bind); }
                    else ignored++;
                    continue;
                }

                foreach (var m in members)
                {
                    if (!Probeable(m)) continue;
                    checkedCount++;
                    // Unity's Mono implements RuntimeHelpers.PrepareMethod as a NO-OP for binding (verified 2026-09-12 on the local 1.0.12 dedicated server: a method calling a member missing at runtime 'prepared' in 0.05 ms without error, while RuntimeMethodHandle.GetFunctionPointer() compiled it and threw MissingMethodException). GetFunctionPointer is the primitive that really JIT-compiles under Mono, so it is what the probe uses.
                    try { m.MethodHandle.GetFunctionPointer(); }
                    catch (Exception e)
                    {
                        var bind = BindFailure(e);
                        if (bind != null) { failedCount++; Record(failures, NameOf(m), bind); }
                        else ignored++;   // anything else is not a binding problem (or not ours to diagnose here)
                    }

                    if (slice.Elapsed.TotalMilliseconds >= budgetMs)
                    {
                        work.Stop();
                        yield return null;
                        work.Start();
                        slice.Restart();
                    }
                }
            }

            work.Stop();
            Publish(checkedCount, failedCount, failures, work.ElapsedMilliseconds);

            var summary = $"Bind probe: {checkedCount} methods checked, {failedCount} failed, {work.ElapsedMilliseconds} ms" +
                          (ignored > 0 ? $" ({ignored} non-binding exceptions ignored)" : "");
            if (failedCount > 0) log.LogWarning(summary); else log.LogInfo(summary);
            for (var i = 0; i < failures.Count && i < LogCap; i++) log.LogWarning("Bind probe failure: " + failures[i]);
            if (failures.Count > LogCap) log.LogWarning($"Bind probe: {failures.Count - LogCap} more failures not listed");
        }

        // Only IL bodies can be JIT-compiled: skip abstract members, open generics, runtime-provided bodies
        // (delegate Invoke/BeginInvoke/EndInvoke are MethodImplAttributes.Runtime), icalls and P/Invokes.
        private static bool Probeable(MethodBase m)
        {
            if (m == null || m.IsAbstract || m.ContainsGenericParameters) return false;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
            MethodImplAttributes impl;
            try { impl = m.GetMethodImplementationFlags(); }
            catch (Exception) { return false; }
            if ((impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) return false;
            return (impl & (MethodImplAttributes.InternalCall | MethodImplAttributes.Unmanaged)) == 0;
        }

        // The binding exception in the chain, or null when this failure is something else. The JIT's own
        // exceptions arrive directly; a static initializer that hit a missing member arrives wrapped in a
        // TypeInitializationException, hence the walk. EntryPointNotFoundException and DllNotFoundException
        // derive from TypeLoadException; MissingMethod/MissingField derive from MissingMemberException.
        private static Exception BindFailure(Exception e)
        {
            for (var depth = 0; e != null && depth < 8; e = e.InnerException, depth++)
                if (e is MissingMemberException || e is TypeLoadException || e is FileNotFoundException)
                    return e;
            return null;
        }

        private static string NameOf(MethodBase m)
        {
            var type = m.DeclaringType != null ? m.DeclaringType.Name : "?";
            return Sanitize(type + "." + m.Name);
        }

        // Summary is ';'/','-delimited, so a name must contain neither (compiler-generated names use <>|_ only).
        private static string Sanitize(string s) => s.Replace(';', '_').Replace(',', '_');

        private static void Record(List<string> failures, string name, Exception e)
        {
            var msg = (e.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            failures.Add($"{name}: {e.GetType().Name}: {msg}");
            if (FailureNames.Count < NamesCap) FailureNames.Add(name);
        }

        private static void Publish(int checkedCount, int failedCount, List<string> failures, long elapsedMs)
        {
            Checked = checkedCount;
            Failed = failedCount;
            ElapsedMs = elapsedMs;
            _summary = "probe=" + (failedCount > 0 ? "failed" : "ok") +
                       ";checked=" + checkedCount + ";failed=" + failedCount +
                       ";names=" + string.Join(",", FailureNames);
            Done = true;
        }
    }
}

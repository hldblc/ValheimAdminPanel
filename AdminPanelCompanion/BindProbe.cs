using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx.Logging;

namespace AdminPanelCompanion
{
    // ==================== Bind probe ====================
    // Why this exists: 2.5.0 shipped compiled against a stale assembly_valheim and ran dead on Valheim 1.0.12
    // for a day while logging "loaded". Every RPC registered fine — the handler BODIES referenced members that
    // had moved (World.m_fileName gone, ZRoutedRpc.Everybody turned const, FindSectorObjects/GetPortals/
    // Heightmap.Poke signatures), and Mono resolves member references when it JIT-compiles a method, not when
    // the assembly loads. So each handler threw MissingMethod/MissingField the first time the routed-RPC
    // dispatcher called it, before its own try/catch even existed, and nothing was logged.
    //
    // The probe forces that compile for every method in this assembly right after Awake via
    // RuntimeHelpers.PrepareMethod and reports what failed — once, in the BepInEx log and in the
    // AP_HealthData handshake payload (CompanionPlugin.HealthSummary). Nothing is executed and nothing is
    // disabled: a handler that fails to bind still fails exactly as it did before; the operator just learns
    // about it at startup instead of by a dead admin panel.
    //
    // Threading. Phase 1 (type initializers) runs on the MAIN thread inside Start(); phase 2 (the JIT loop)
    // runs on a background thread and publishes one immutable Result through a volatile field. The worker
    // touches no Unity object and never logs; CompanionPlugin polls Current from a coroutine and writes the
    // report from the main thread.
    //
    // Why phase 1 exists: on Mono, JIT-compiling a method also class-inits its declaring type
    // (mono_jit_compile_method_with_opt ends with mono_runtime_class_init_full), so PrepareMethod on a type
    // whose static constructor has not run yet RUNS it — on whichever thread called PrepareMethod. Running
    // RuntimeHelpers.RunClassConstructor for every type on the main thread first means the worker only ever
    // meets initialised classes, whatever Mono does. Every static initializer in this assembly is a hash
    // constant, a collection, DateTime.UtcNow or a System.Random (audited 2026-09): none reads game state or
    // needs the Unity thread, so running them at the end of Awake instead of at first use changes nothing.
    internal static class BindProbe
    {
        private const int LogCap = 50;      // failure lines kept for the log
        private const int PayloadCap = 8;   // failure names carried in the health payload
        private const int MessageCap = 200;

        internal sealed class Result
        {
            public readonly int Checked;          // methods handed to PrepareMethod
            public readonly int Failed;           // bind failures (uncapped count)
            public readonly long ElapsedMs;       // wall time from Start() to the worker's last method
            public readonly string[] Failures;    // "Type.Method: ExceptionType: message", in scan order, at most LogCap
            public readonly string[] FailedNames; // "Type.Method" for the first PayloadCap failures
            public readonly int Ignored;          // methods that threw something other than a bind failure
            public readonly string IgnoredSample; // the first of those, for one debug line
            public readonly string Aborted;       // non-null when the scan itself died; counts are then partial

            internal Result(int checkedCount, int failed, long elapsedMs, string[] failures, string[] failedNames,
                            int ignored, string ignoredSample, string aborted)
            {
                Checked = checkedCount; Failed = failed; ElapsedMs = elapsedMs;
                Failures = failures; FailedNames = failedNames;
                Ignored = ignored; IgnoredSample = ignoredSample; Aborted = aborted;
            }
        }

        private static int _started;
        private static volatile Result _result;

        // Null until the worker has finished. A volatile read of the reference is enough: the Result is
        // fully constructed before the worker publishes it and never mutated afterwards.
        internal static Result Current => _result;
        internal static bool Finished => _result != null;

        // Health-payload state: "skipped" until the worker finishes (or if it never ran), then ok/failed.
        internal static string State
        {
            get
            {
                var r = _result;
                return r == null ? "skipped" : (r.Failed > 0 ? "failed" : "ok");
            }
        }

        // Main thread, once. Later calls are no-ops: the probe answers "does this DLL bind to this game",
        // which cannot change while the process lives.
        internal static void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            var sw = Stopwatch.StartNew();
            var c = new Collector();
            Type[] types;
            HashSet<Type> initFailed;
            try
            {
                types = LoadTypes(typeof(BindProbe).Assembly, c);
                initFailed = RunTypeInitializers(types, c);
            }
            catch (Exception e)
            {
                c.Aborted = Describe(e);
                sw.Stop();
                _result = c.ToResult(sw.ElapsedMilliseconds);
                return;
            }
            try
            {
                var worker = new Thread(() => Worker(types, initFailed, c, sw))
                {
                    IsBackground = true,
                    Name = "AdminPanelCompanion.BindProbe",
                    Priority = ThreadPriority.BelowNormal,
                };
                worker.Start();
            }
            catch (Exception e)
            {
                c.Aborted = "worker thread did not start: " + Describe(e);
                sw.Stop();
                _result = c.ToResult(sw.ElapsedMilliseconds);
            }
        }

        // Main thread. Writes the report the coroutine in CompanionPlugin asks for once Finished is true.
        internal static void LogReport(ManualLogSource log, string gameVersion, string compiledFor, string plugin)
        {
            var r = _result;
            if (r == null || log == null) return;
            var summary = $"Bind probe: {r.Checked} methods checked in {r.ElapsedMs} ms, {r.Failed} failed to bind on Valheim {gameVersion}" + (r.Ignored > 0 ? $" ({r.Ignored} raised a non-binding exception while compiling; first: {r.IgnoredSample})" : "");
            if (r.Failed == 0) log.LogInfo(summary); else log.LogWarning(summary);
            foreach (var f in r.Failures) log.LogWarning("Bind probe: " + f);
            if (r.Failed > r.Failures.Length)
                log.LogWarning($"Bind probe: ... and {r.Failed - r.Failures.Length} more (first {r.Failures.Length} shown)");
            if (r.Failed > 0)
                log.LogWarning($"Bind probe: {plugin} was compiled for Valheim {compiledFor}; the members above do not exist or changed shape on Valheim {gameVersion}. Each affected handler throws when it is invoked while the rest of the companion keeps working. Rebuild the companion against this game build, or install a release built for it.");
            if (r.Ignored > 0)
                log.LogDebug($"Bind probe: {r.Ignored} method(s) raised a non-binding exception while compiling and were ignored (first: {r.IgnoredSample})");
            if (r.Aborted != null)
                log.LogWarning($"Bind probe: scan stopped early ({r.Aborted}); the counts above are partial");
        }

        // ---------- phase 1: type initializers (main thread) ----------

        private static Type[] LoadTypes(Assembly asm, Collector c)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                // Some types could not be loaded at all (a base class or interface that no longer exists).
                // Report each loader exception and probe whatever did load.
                if (e.LoaderExceptions != null)
                    foreach (var le in e.LoaderExceptions)
                        if (le != null) c.Fail("<type load>", FindBindFailure(le) ?? Describe(le));
                return e.Types ?? new Type[0];
            }
        }

        private static HashSet<Type> RunTypeInitializers(Type[] types, Collector c)
        {
            var failed = new HashSet<Type>();
            foreach (var t in types)
            {
                if (t == null || t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;
                try { RuntimeHelpers.RunClassConstructor(t.TypeHandle); }
                catch (Exception e)
                {
                    // A type whose initializer threw is dead for the rest of the process (every later touch
                    // rethrows TypeInitializationException), whatever the cause — always worth a line, and
                    // its methods are skipped below because probing them could only repeat this one.
                    failed.Add(t);
                    c.Fail(TypeLabel(t) + "..cctor", FindBindFailure(e) ?? Describe(Innermost(e)));
                }
            }
            return failed;
        }

        // ---------- phase 2: JIT every method (worker thread) ----------

        private static void Worker(Type[] types, HashSet<Type> initFailed, Collector c, Stopwatch sw)
        {
            try
            {
                foreach (var t in types)
                {
                    if (t == null || initFailed.Contains(t)) continue;
                    if (t.IsGenericTypeDefinition || t.ContainsGenericParameters) continue;   // no closed instantiation to compile
                    MethodBase[] members;
                    try { members = CollectMembers(t); }
                    catch (Exception e) { c.Ignore(TypeLabel(t), e); continue; }
                    foreach (var m in members) Probe(t, m, c);
                }
            }
            catch (Exception e) { c.Aborted = Describe(e); }
            finally
            {
                sw.Stop();
                try { _result = c.ToResult(sw.ElapsedMilliseconds); }
                catch (Exception) { _result = new Result(c.Checked, c.Failed, sw.ElapsedMilliseconds, new string[0], new string[0], c.Ignored, null, "result could not be assembled"); }
            }
        }

        private static MethodBase[] CollectMembers(Type t)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = t.GetMethods(flags);
            var ctors = t.GetConstructors(flags);
            var all = new MethodBase[methods.Length + ctors.Length];
            Array.Copy(methods, 0, all, 0, methods.Length);
            Array.Copy(ctors, 0, all, methods.Length, ctors.Length);
            return all;
        }

        private static void Probe(Type t, MethodBase m, Collector c)
        {
            if (m.IsAbstract) return;
            if (m.IsGenericMethodDefinition || m.ContainsGenericParameters) return;   // nothing concrete to compile
            var impl = m.GetMethodImplementationFlags();
            if ((impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) return;   // runtime-provided (delegate Invoke etc.)
            if ((impl & MethodImplAttributes.InternalCall) != 0) return;
            if ((impl & MethodImplAttributes.ManagedMask) == MethodImplAttributes.Unmanaged) return;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return;
            if (m is ConstructorInfo && m.IsStatic) return;   // type initializers already ran in phase 1

            c.Checked++;
            // Unity's Mono implements RuntimeHelpers.PrepareMethod as a NO-OP for binding (verified 2026-09-12 on the local 1.0.12 dedicated server: a method calling a member missing at runtime 'prepared' in 0.05 ms without error, while RuntimeMethodHandle.GetFunctionPointer() compiled it and threw MissingMethodException). GetFunctionPointer is the primitive that really JIT-compiles under Mono, so it is what the probe uses.
            try { m.MethodHandle.GetFunctionPointer(); }
            catch (Exception e)
            {
                var bind = FindBindFailure(e);
                if (bind != null) c.Fail(Label(t, m), bind);
                else c.Ignore(Label(t, m), e);
            }
        }

        // ---------- classification ----------

        // The exception kinds Mono's JIT (or the loader underneath it) raises when a member reference in the
        // method body no longer resolves against the running game. Walks the InnerException chain because the
        // JIT failure may arrive wrapped (TypeInitializationException, TargetInvocationException).
        //   MissingMemberException  covers MissingMethodException and MissingFieldException
        //   TypeLoadException       covers EntryPointNotFoundException and DllNotFoundException
        private static string FindBindFailure(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is MissingMemberException || x is TypeLoadException || x is FileNotFoundException ||
                    x is BadImageFormatException || x is TargetParameterCountException)
                    return Describe(x);
            }
            return null;
        }

        private static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static string Describe(Exception e) => e.GetType().Name + ": " + OneLine(e.Message);

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MessageCap ? s.Substring(0, MessageCap) + "..." : s;
        }

        // "Wave5SrvPortals.OnListReq", "CompanionPlugin+RpcRegistration.ZNetAwakePostfix", "Foo..ctor".
        private static string Label(Type t, MethodBase m) => TypeLabel(t) + "." + m.Name;

        private static string TypeLabel(Type t)
        {
            var n = t.FullName ?? t.Name;
            const string ns = "AdminPanelCompanion.";
            return n.StartsWith(ns, StringComparison.Ordinal) ? n.Substring(ns.Length) : n;
        }

        // Scratch state. Phase 1 fills it on the main thread, phase 2 on the worker; Thread.Start orders the
        // hand-over, and nobody else reads it until the worker turns it into the immutable Result.
        private sealed class Collector
        {
            public int Checked, Failed, Ignored;
            public string IgnoredSample, Aborted;
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> FailedNames = new List<string>();

            public void Fail(string label, string why)
            {
                Failed++;
                if (Failures.Count < LogCap) Failures.Add(label + ": " + why);
                if (FailedNames.Count < PayloadCap) FailedNames.Add(label);
            }

            public void Ignore(string label, Exception e)
            {
                Ignored++;
                if (IgnoredSample == null) IgnoredSample = label + ": " + Describe(e);
            }

            public Result ToResult(long elapsedMs) =>
                new Result(Checked, Failed, elapsedMs, Failures.ToArray(), FailedNames.ToArray(), Ignored, IgnoredSample, Aborted);
        }
    }
}

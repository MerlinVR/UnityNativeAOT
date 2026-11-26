using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Private.Scripting;
using Unity.Private.Scripting.Interop;

namespace AotRuntime
{
    /// <summary>
    /// AOT_ALLOC_TRACE=1: every few seconds the runtime log lists the engine-to-script calls under which the calling
    /// thread allocated the most. A stock CoreCLR player allocates next to nothing per frame, so whatever shows up
    /// here every frame is this runtime's doing, or code that leans on something NativeAOT does differently.
    /// </summary>
    public static class AllocTrace
    {
        const int Rows = 14;
        const int SecondsBetweenReports = 4;

        public static readonly bool On = Environment.GetEnvironmentVariable("AOT_ALLOC_TRACE") == "1";

        // Per method: calls, bytes including nested engine-to-script calls, bytes when it was the outermost call.
        static readonly Dictionary<MethodBase, long[]> s_ByMethod = new Dictionary<MethodBase, long[]>();
        static long s_LastReport = Stopwatch.GetTimestamp();
        [ThreadStatic] static int t_Depth;

        public static void Enter() => t_Depth++;

        public static void Leave(MethodBase method, long bytes)
        {
            int depth = --t_Depth;
            lock (s_ByMethod)
            {
                if (!s_ByMethod.TryGetValue(method, out long[] entry)) s_ByMethod[method] = entry = new long[3];
                entry[0]++;
                entry[1] += bytes;
                if (depth == 0) entry[2] += bytes;

                if (Stopwatch.GetTimestamp() - s_LastReport < SecondsBetweenReports * Stopwatch.Frequency) return;
                s_LastReport = Stopwatch.GetTimestamp();
                var rows = new List<KeyValuePair<MethodBase, long[]>>(s_ByMethod);
                rows.Sort((x, y) => y.Value[1].CompareTo(x.Value[1]));
                Log.Line("alloctrace: calls, bytes with nested calls, bytes as the outermost call, method (since the last report)");
                for (int i = 0; i < rows.Count && i < Rows; i++)
                    Log.Line($"alloctrace: {rows[i].Value[0],8} {rows[i].Value[1],12} {rows[i].Value[2],12}  {rows[i].Key.DeclaringType?.FullName}.{rows[i].Key.Name}");
                s_ByMethod.Clear();
            }
        }
    }

    /// <summary>
    /// AOT_SELFTEST=1: when the engine first invokes a script method, times the pieces of that path on a method that
    /// does nothing and writes nanoseconds per call to the runtime log.
    /// </summary>
    public static unsafe class InvokeTiming
    {
        const int Calls = 5_000_000;

        sealed class Target
        {
            public int Count;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Tick() => Count++;
        }

        static bool s_Done;
        static object s_Sink;

        public static void RunOnce(IntPtr klass)
        {
            if (s_Done) return;
            s_Done = true;
            if (Environment.GetEnvironmentVariable("AOT_SELFTEST") != "1") return;

            var target = new Target();
            MethodInfo tick = typeof(Target).GetMethod(nameof(Target.Tick));
            IntPtr method = tick.MethodHandle.Value;
            GCHandle handle = GCHandle.Alloc(target);
            // The engine's object representation: a GC handle with the handle bit set.
            IntPtr obj = GCHandle.ToIntPtr(handle) | 2;

            RuntimeInterop.runtime_invoke(method, klass, obj, IntPtr.Zero, IntPtr.Zero);

            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < Calls; i++) RuntimeInterop.runtime_invoke(method, klass, obj, IntPtr.Zero, IntPtr.Zero);
            Report("runtime_invoke, called from managed code", ref started);

            var native = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&RuntimeInterop.runtime_invoke_native;
            for (int i = 0; i < Calls; i++) native(method, klass, obj, IntPtr.Zero, IntPtr.Zero);
            Report("runtime_invoke_native, out to native and back in as the engine does", ref started);

            for (int i = 0; i < Calls; i++) RuntimeInterop.GetOrCreateCacheInfo(method, klass);
            Report("  of which the invoker lookup (a lock and a dictionary)", ref started);

            Func<IntPtr, IntPtr, object> invoke = RuntimeInterop.GetOrCreateCacheInfo(method, klass).InvokeDelegate;
            for (int i = 0; i < Calls; i++) invoke(obj, IntPtr.Zero);
            Report("  of which the invoker", ref started);

            for (int i = 0; i < Calls; i++) s_Sink = obj.ToManagedRepresentation();
            Report("  of which handle to object", ref started);

            var direct = Unsafe.As<Action<object>>(tick.CreateDelegate(typeof(Action<>).MakeGenericType(typeof(Target))));
            for (int i = 0; i < Calls; i++) direct(target);
            Report("an open delegate, for comparison", ref started);

            for (int i = 0; i < Calls; i++) tick.Invoke(target, BindingFlags.DoNotWrapExceptions, null, Array.Empty<object>(), null);
            Report("MethodInfo.Invoke, for comparison", ref started);

            Log.Line($"selftest: the target was reached {target.Count} times");
            handle.Free();
        }

        static void Report(string what, ref long started)
        {
            long now = Stopwatch.GetTimestamp();
            Log.Line($"selftest: {(now - started) * 1e9 / Stopwatch.Frequency / Calls,7:F1} ns  {what}");
            started = Stopwatch.GetTimestamp();
        }
    }
}

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

// Logs what the scripting runtime looks like from inside a built player, then quits.
public class Probe : MonoBehaviour
{
    void Start()
    {
        Debug.Log("PROBE runtime=" + RuntimeInformation.FrameworkDescription
            + " corelib=" + typeof(object).Assembly.FullName
            + " corelibLocation=" + typeof(object).Assembly.Location);
        Debug.Log("PROBE dynamicCodeSupported=" + RuntimeFeature.IsDynamicCodeSupported
            + " dynamicCodeCompiled=" + RuntimeFeature.IsDynamicCodeCompiled
            + " serverGC=" + GCSettings.IsServerGC
            + " latency=" + GCSettings.LatencyMode
            + " maxGen=" + GC.MaxGeneration);

        // Does the managed heap move objects? Allocate, churn, compact, compare addresses.
        var probe = new object[64];
        for (int i = 0; i < probe.Length; i++) probe[i] = new byte[1024];
        // Free the even slots to leave holes, then watch an odd (surviving) slot.
        var before = AddressOf(probe[33]);
        for (int i = 0; i < probe.Length; i += 2) probe[i] = null;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        var after = AddressOf(probe[33]);
        Debug.Log("PROBE objectMoved=" + (before != after) + " before=0x" + before.ToString("x") + " after=0x" + after.ToString("x"));

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-callbench") >= 0) CallBench();

        // Exercise, on the same object, quits the player when its sequence ends.
    }

    // -callbench: what one call from a script into the engine costs, per kind of call. Three passes, the last one
    // reported, so that a JIT has settled.
    const int BenchCalls = 2000000;
    static float s_Sink;

    static void CallBench()
    {
        Transform t = new GameObject("bench").transform;
        for (int pass = 0; pass < 3; pass++)
        {
            bool report = pass == 2;
            Time("empty loop", report, () => { for (int i = 0; i < BenchCalls; i++) s_Sink += i & 3; });
            Time("Time.frameCount", report, () => { int n = 0; for (int i = 0; i < BenchCalls; i++) n += UnityEngine.Time.frameCount; s_Sink = n; });
            Time("Quaternion.Euler", report, () => { Quaternion q = default; var e = new Vector3(10, 20, 30); for (int i = 0; i < BenchCalls; i++) q = Quaternion.Euler(e); s_Sink = q.x; });
            Time("Quaternion.LookRotation", report, () => { Quaternion q = default; var f = new Vector3(1, 2, 3); for (int i = 0; i < BenchCalls; i++) q = Quaternion.LookRotation(f, Vector3.up); s_Sink = q.x; });
            Time("Matrix4x4.TRS", report, () => { Matrix4x4 m = default; for (int i = 0; i < BenchCalls; i++) m = Matrix4x4.TRS(Vector3.one, Quaternion.identity, Vector3.one); s_Sink = m.m00; });
            Time("get localPosition", report, () => { Vector3 v = default; for (int i = 0; i < BenchCalls; i++) v = t.localPosition; s_Sink = v.x; });
            Time("set localPosition", report, () => { for (int i = 0; i < BenchCalls; i++) t.localPosition = new Vector3(i & 7, 1, 2); });
            Time("set localRotation", report, () => { var q = Quaternion.identity; for (int i = 0; i < BenchCalls; i++) t.localRotation = q; });
            Time("set localScale", report, () => { for (int i = 0; i < BenchCalls; i++) t.localScale = new Vector3(1 + (i & 3), 1, 1); });
            Time("childCount", report, () => { int n = 0; for (int i = 0; i < BenchCalls; i++) n += t.childCount; s_Sink = n; });
        }
    }

    static void Time(string what, bool report, Action body)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        body();
        if (report) Debug.Log("PROBE callbench " + (watch.Elapsed.TotalMilliseconds * 1e6 / BenchCalls).ToString("F1") + " ns  " + what);
    }

    static unsafe long AddressOf(object o)
    {
        TypedReference tr = __makeref(o);
        return (long)(**(IntPtr**)(&tr));
    }
}

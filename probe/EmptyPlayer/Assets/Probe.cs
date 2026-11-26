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

        // Exercise, on the same object, quits the player when its sequence ends.
    }

    static unsafe long AddressOf(object o)
    {
        TypedReference tr = __makeref(o);
        return (long)(**(IntPtr**)(&tr));
    }
}

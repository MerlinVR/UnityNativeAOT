using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace AotRuntime
{
    /// <summary>
    /// The liveness walk behind Resources.UnloadUnusedAssets and scene unloading. The engine asks which engine objects
    /// scripts can still reach: it hands over root objects (its live MonoBehaviours and the like), asks for the statics
    /// as well, and expects a callback with every reachable UnityEngine.Object. Anything not reported is destroyed.
    ///
    /// The contract was inferred from Mono's unity_liveness.c and from the arguments the 6000.7 engine passes; Unity's
    /// CoreCLR implementation is not public. NativeAOT has no heap-walk API, so the walk reads the GC's own layout
    /// descriptors (the GCDesc that precedes every MethodTable), which NativeAOT shares with CoreCLR.
    ///
    /// Not covered: [ThreadStatic] roots, and a real stop-the-world. The world is "stopped" with a no-GC region, which
    /// keeps objects from moving but lets other managed threads keep running.
    /// </summary>
    public static unsafe class Liveness
    {
        // Internal.Runtime.MethodTable: { uint flags; uint baseSize; MethodTable* relatedType; ... }
        const uint HasPointersFlag = 0x01000000;
        const uint HasComponentSizeFlag = 0x80000000;
        const uint KindMask = 0x00030000;       // zero for an ordinary class or struct, whose related type is its base
        const int BatchSize = 64;
        const long NoGcBudget = 32 * 1024 * 1024;

        struct State
        {
            public delegate* unmanaged[Cdecl]<nint*, int, void*, void> Callback;
            public void* UserData;
            public nint Filter;                 // MethodTable of the class to report
            public nint* Set;                   // visited objects, open addressing
            public int SetCapacity, SetCount;
            public nint* Stack;                 // objects found but not scanned yet
            public int StackCapacity, StackCount;
            public nint* Batch;
            public int BatchCount;
            public int Reported;
        }

        static bool s_noGc;
        static int s_passes, s_roots, s_states, s_visited, s_reported, s_fromStatics;

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_moduleGCStaticsSpines")]
        static extern ref IntPtr GCStaticsSpines(
            [UnsafeAccessorType("Internal.Runtime.CompilerHelpers.StartupCodeHelpers, System.Private.CoreLib")] object unused);

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_allocate_struct", CallConvs = new[] { typeof(CallConvCdecl) })]
        static State* Allocate(ClassHandle* filter, uint maxCount, delegate* unmanaged[Cdecl]<nint*, int, void*, void> callback, void* userData)
        {
            var state = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
            state->Callback = callback;
            state->UserData = userData;
            // The 6000.7 engine passes no filter and means its own object class.
            state->Filter = filter != null ? filter->MethodTable : typeof(UnityEngine.Object).TypeHandle.Value;
            state->SetCapacity = 1 << 12;
            state->Set = (nint*)NativeMemory.AllocZeroed((nuint)state->SetCapacity, (nuint)sizeof(nint));
            state->StackCapacity = 1 << 10;
            state->Stack = (nint*)NativeMemory.Alloc((nuint)state->StackCapacity, (nuint)sizeof(nint));
            state->Batch = (nint*)NativeMemory.Alloc(BatchSize, (nuint)sizeof(nint));
            return state;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_free_struct", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Free(State* state)
        {
            if (state == null) return;
            NativeMemory.Free(state->Set);
            NativeMemory.Free(state->Stack);
            NativeMemory.Free(state->Batch);
            NativeMemory.Free(state);
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_stop_world", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void StopWorld()
        {
            s_roots = s_states = s_visited = s_reported = s_fromStatics = 0;
            // The engine passes object addresses from here on, so nothing may move until start_world.
            try { s_noGc = GC.TryStartNoGCRegion(NoGcBudget); }
            catch (Exception e) { s_noGc = false; Log.Line($"liveness: no-GC region refused: {e.Message}"); }
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_start_world", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void StartWorld()
        {
            string region = "not entered";
            if (s_noGc)
            {
                s_noGc = false;
                // EndNoGCRegion throws when a collection happened anyway: addresses were not stable during the walk.
                if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) region = "ended early";
                else
                {
                    try { GC.EndNoGCRegion(); region = "held"; }
                    catch (InvalidOperationException e) { region = "did not hold: " + e.Message; }
                }
            }
            if (++s_passes <= 8 || region != "held")
                Log.Line($"liveness pass {s_passes}: {s_roots} roots in {s_states} states, {s_visited} objects visited ({s_fromStatics} from statics), {s_reported} reported, no-GC region {region}");
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_calculation_from_root", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void FromRoot(nint root, State* state)
        {
            if (state == null || root == 0) return;
            Interlocked.Increment(ref s_roots);
            Walk(state, root);
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_calculation_from_statics", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void FromStatics(State* state)
        {
            if (state == null) return;
            // NativeAOT keeps every type's GC statics in a holder object. All holders hang off one object[][].
            object spines = GCHandle.FromIntPtr(GCStaticsSpines(null)).Target;
            if (spines == null) return;
            int before = state->SetCount;
            Walk(state, *(nint*)Unsafe.AsPointer(ref spines));
            Interlocked.Add(ref s_fromStatics, state->SetCount - before);
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_liveness_finalize", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Finish(State* state)
        {
            if (state == null) return;
            Flush(state);
            Interlocked.Increment(ref s_states);
            Interlocked.Add(ref s_visited, state->SetCount);
            Interlocked.Add(ref s_reported, state->Reported);
        }

        static void Walk(State* state, nint root)
        {
            Visit(state, root);
            while (state->StackCount > 0)
            {
                nint o = state->Stack[--state->StackCount];
                nint mt = *(nint*)o;
                uint flags = *(uint*)mt;
                if (IsReported(mt, state->Filter))
                {
                    state->Batch[state->BatchCount++] = o;
                    if (state->BatchCount == BatchSize) Flush(state);
                }
                if ((flags & HasPointersFlag) == 0) continue;

                // Object size as the GC counts it: base size, plus the elements of an array.
                nint size = (nint)(*(uint*)(mt + 4));
                if ((flags & HasComponentSizeFlag) != 0) size += (nint)(flags & 0xFFFF) * (nint)(*(uint*)(o + sizeof(nint)));

                // GCDesc (gcdesc.h): the series count sits just below the MethodTable, the series below that. Each
                // series is { size, offset }; the stored size has the object's size subtracted from it.
                nint count = *(nint*)(mt - sizeof(nint));
                nint* series = (nint*)(mt - 3 * sizeof(nint));
                if (count >= 0)
                {
                    nint* last = (nint*)(mt - sizeof(nint) - count * 2 * sizeof(nint));
                    for (; series >= last; series -= 2)
                    {
                        nint* p = (nint*)(o + series[1]);
                        nint* stop = (nint*)((byte*)p + series[0] + size);
                        for (; p < stop; p++) Visit(state, *p);
                    }
                }
                else
                {
                    // An array of structs: the series describes one element as { pointer count, bytes to skip } pairs.
                    nint* p = (nint*)(o + series[1]);
                    byte* end = (byte*)o + size - sizeof(nint);
                    while ((byte*)p < end)
                    {
                        for (nint i = 0; i > count; i--)
                        {
                            uint* item = (uint*)(series + i);
                            nint* stop = p + item[0];
                            do { Visit(state, *p); p++; } while (p < stop);
                            p = (nint*)((byte*)stop + item[1]);
                        }
                    }
                }
            }
        }

        static bool IsReported(nint mt, nint filter)
        {
            for (; mt != 0; mt = *(nint*)(mt + 2 * sizeof(uint)))
            {
                if ((*(uint*)mt & KindMask) != 0) return false;
                if (mt == filter) return true;
            }
            return false;
        }

        static void Flush(State* state)
        {
            if (state->BatchCount == 0) return;
            state->Reported += state->BatchCount;
            state->Callback(state->Batch, state->BatchCount, state->UserData);
            state->BatchCount = 0;
        }

        static void Visit(State* state, nint o)
        {
            if (o == 0 || !Add(state, o)) return;
            if (state->StackCount == state->StackCapacity)
            {
                state->StackCapacity *= 2;
                state->Stack = (nint*)NativeMemory.Realloc(state->Stack, (nuint)state->StackCapacity * (nuint)sizeof(nint));
            }
            state->Stack[state->StackCount++] = o;
        }

        static bool Add(State* state, nint o)
        {
            if (state->SetCount * 2 >= state->SetCapacity) Grow(state);
            int mask = state->SetCapacity - 1;
            for (int i = Slot(o, mask); ; i = (i + 1) & mask)
            {
                nint entry = state->Set[i];
                if (entry == o) return false;
                if (entry != 0) continue;
                state->Set[i] = o;
                state->SetCount++;
                return true;
            }
        }

        static int Slot(nint o, int mask) => (int)(((ulong)o * 0x9E3779B97F4A7C15UL) >> 32) & mask;

        static void Grow(State* state)
        {
            nint* old = state->Set;
            int oldCapacity = state->SetCapacity;
            state->SetCapacity = oldCapacity * 2;
            state->Set = (nint*)NativeMemory.AllocZeroed((nuint)state->SetCapacity, (nuint)sizeof(nint));
            int mask = state->SetCapacity - 1;
            for (int i = 0; i < oldCapacity; i++)
            {
                nint o = old[i];
                if (o == 0) continue;
                int slot = Slot(o, mask);
                while (state->Set[slot] != 0) slot = (slot + 1) & mask;
                state->Set[slot] = o;
            }
            NativeMemory.Free(old);
        }
    }
}

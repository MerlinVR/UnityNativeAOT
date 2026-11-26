using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CoreClrStandIn
{
    // Signatures follow coreclrhost.h and Unity's fork (src/coreclr/vm/mono/mono_coreclr.cpp).
    internal static unsafe class Exports
    {
        // Slot order of Unity.Private.Scripting.InitializationCallbacks in 6000.7.0b10.
        static readonly string[] s_InteropStructs =
        {
            "NativeSupport", "ArrayInterop", "AssemblyInterop", "AssemblyLoadContextInterop", "ClassInterop",
            "CodeReloadInterop", "DebuggerInterop", "ExceptionInterop", "FieldInterop", "GCHandleInterop",
            "GCInterop", "ImageInterop", "MethodInterop", "ObjectInterop", "ObjectTransferInterop",
            "RuntimeInterop", "ScriptingUtilityInterop", "StackFrameInterop", "StackTraceInterop",
            "StringInterop", "TypeInterop",
        };

        static string Str(byte* p) => p == null ? "<null>" : Marshal.PtrToStringUTF8((IntPtr)p);

        [UnmanagedCallersOnly(EntryPoint = "coreclr_initialize", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Initialize(byte* exePath, byte* appDomainFriendlyName, int propertyCount, byte** keys, byte** values, void** hostHandle, uint* domainId)
        {
            Log.Line($"coreclr_initialize exe={Str(exePath)} domain={Str(appDomainFriendlyName)} properties={propertyCount}");
            Log.Line($"  stand-in runtime: {RuntimeInformation.FrameworkDescription}, dynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported}");
            for (int i = 0; i < propertyCount; i++)
            {
                string value = Str(values[i]);
                string[] parts = value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 3)
                    Log.Line($"  {Str(keys[i])} = [{parts.Length} entries] {parts[0]} ; ... ; {parts[^1]}");
                else
                    Log.Line($"  {Str(keys[i])} = {value}");
            }
            if (hostHandle != null) *hostHandle = (void*)0x1;
            if (domainId != null) *domainId = 1;
            return 0;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_initialize_domain", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void InitializeDomain(void* runtimeHost, uint rootDomainId)
            => Log.Line($"coreclr_initialize_domain host=0x{(nint)runtimeHost:x} domainId={rootDomainId}");

        [UnmanagedCallersOnly(EntryPoint = "unity_coreclr_create_delegate", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void* UnityCreateDelegate(byte* assemblyName, byte* typeName, byte* methodName)
        {
            Log.Line($"unity_coreclr_create_delegate {Str(assemblyName)} :: {Str(typeName)} :: {Str(methodName)}");
            if (Str(methodName) == "InitializationBootstrapper")
                return (delegate* unmanaged[Cdecl]<IntPtr, int, void>)&Bootstrapper;
            Log.Line("  -> no AOT-compiled entry point registered for this method, returning null");
            return null;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_create_delegate", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int CreateDelegate(void* hostHandle, uint domainId, byte* assemblyName, byte* typeName, byte* methodName, void** result)
        {
            Log.Line($"coreclr_create_delegate {Str(assemblyName)} :: {Str(typeName)} :: {Str(methodName)}");
            if (result != null) *result = null;
            return unchecked((int)0x80004001); // E_NOTIMPL
        }

        // Stands in for Unity.Private.Scripting.Initialization.InitializationBootstrapper.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Bootstrapper(IntPtr functionStructNative, int structSizeNative)
        {
            int slots = structSizeNative / IntPtr.Size;
            Log.Line($"InitializationBootstrapper called from native: InitializationCallbacks size={structSizeNative} ({slots} slots, expected {s_InteropStructs.Length})");
            var table = (delegate* unmanaged[Cdecl]<IntPtr, int, void>*)functionStructNative;
            for (int i = 0; i < slots && i < InitCallbacks.Count; i++)
                table[i] = InitCallbacks.Get(i);
        }

        internal static void OnInitCallback(int slot, IntPtr callbackStruct, int size)
        {
            string name = slot < s_InteropStructs.Length ? s_InteropStructs[slot] : "slot" + slot;
            Log.Line($"InitCallback_{name}: native handed a {size}-byte struct ({size / IntPtr.Size} pointer slots) at 0x{(nint)callbackStruct:x} for managed code to fill");

            // NativeSupportCallbackStruct flows the other way (native -> managed) and ends with
            // { sbyte** argv; int argc; int return_handles_from_api; }.
            if (slot == 0 && size >= 16)
            {
                int argc = *(int*)((byte*)callbackStruct + size - 8);
                int returnHandlesFromApi = *(int*)((byte*)callbackStruct + size - 4);
                Log.Line($"  NativeSupport: argc={argc} return_handles_from_api={returnHandlesFromApi}");
            }
        }
    }

    internal static class Log
    {
        static readonly object s_Lock = new object();
        static readonly string s_Path =
            Environment.GetEnvironmentVariable("CORECLR_STANDIN_LOG") ?? Path.Combine(Path.GetTempPath(), "coreclr-standin.log");

        internal static void Line(string text)
        {
            lock (s_Lock)
            {
                try { File.AppendAllText(s_Path, $"[tid {Environment.CurrentManagedThreadId}] {text}{Environment.NewLine}"); }
                catch (IOException) { }
            }
        }
    }
}

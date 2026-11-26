using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AotRuntime
{
    /// <summary>
    /// The hosting entry points UnityPlayer.dll calls on coreclr.dll. Signatures follow coreclrhost.h and Unity's fork
    /// (src/coreclr/vm/mono/mono_coreclr.cpp). There is no runtime to start: this library is the runtime.
    /// </summary>
    public static unsafe class Host
    {
        /// <summary>The player's Managed folder. The IL files there are only read for names and metadata tokens.</summary>
        public static string ManagedDir = "";

        [UnmanagedCallersOnly(EntryPoint = "coreclr_initialize", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Initialize(byte* exePath, byte* appDomainFriendlyName, int propertyCount, byte** keys, byte** values, void** hostHandle, uint* domainId)
        {
            Log.Line($"coreclr_initialize exe={Log.Str(exePath)} domain={Log.Str(appDomainFriendlyName)} runtime={RuntimeInformation.FrameworkDescription} dynamicCode={RuntimeFeature.IsDynamicCodeSupported}");
            for (int i = 0; i < propertyCount; i++)
            {
                if (Log.Str(keys[i]) == "APP_PATHS")
                    ManagedDir = Log.Str(values[i]).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)[0];
            }
            Log.Line($"  managed dir: {ManagedDir}; AOT-compiled assemblies: {AotAssemblies.Count}");
            try { Log.Line($"  process started {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms before this call"); }
            catch (Exception) { }
            Diagnostics.Install();
            if (hostHandle != null) *hostHandle = (void*)0x1;
            if (domainId != null) *domainId = 1;
            return 0;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_initialize_domain", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void InitializeDomain(void* runtimeHost, uint rootDomainId) => Log.Line("coreclr_initialize_domain");

        [UnmanagedCallersOnly(EntryPoint = "unity_coreclr_create_delegate", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void* UnityCreateDelegate(byte* assemblyName, byte* typeName, byte* methodName)
        {
            string type = Log.Str(typeName), method = Log.Str(methodName);
            Log.Line($"unity_coreclr_create_delegate {type} :: {method}");
            if (type == "Unity.Private.Scripting.Initialization" && method == "InitializationBootstrapper")
                return (delegate* unmanaged[Cdecl]<IntPtr, int, void>)&Unity.Private.Scripting.Initialization.InitializationBootstrapper;

            Log.Line("  -> no AOT entry point registered, returning null");
            return null;
        }

        [UnmanagedCallersOnly(EntryPoint = "coreclr_create_delegate", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int CreateDelegate(void* hostHandle, uint domainId, byte* assemblyName, byte* typeName, byte* methodName, void** result)
        {
            Log.Line($"coreclr_create_delegate {Log.Str(assemblyName)} :: {Log.Str(typeName)} :: {Log.Str(methodName)} -> E_NOTIMPL");
            if (result != null) *result = null;
            return unchecked((int)0x80004001);
        }
    }
}

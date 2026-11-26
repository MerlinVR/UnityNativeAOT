using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AotRuntime
{
    /// <summary>
    /// Marks a static method that replaces a method in one of Unity's assemblies. `asmdump detour` rewrites the target's
    /// body into a call to it. A replacement for an instance method takes the instance as its first parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class DetourAttribute : Attribute
    {
        public DetourAttribute(string assembly, string type, string method) { }
    }

    /// <summary>
    /// Marks a static method that replaces every call to <paramref name="callee"/> (written as Cecil prints it, for
    /// example "System.IntPtr System.RuntimeTypeHandle::get_Value()") made from inside one of Unity's assemblies.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RedirectCallsAttribute : Attribute
    {
        public RedirectCallsAttribute(string assembly, string callee) { }
    }

    public static class Asm
    {
        public const string Core = "UnityEngine.CoreModule";
        public const string ScriptingModule = "UnityEngine.ScriptingModule";
        public const string Scripting = "Unity.Private.Scripting";
        public const string Interop = "Unity.Private.Scripting.Interop.";
        public const string Loader = "Unity.Private.Scripting.AssemblyLoader.";
    }

    public static unsafe class Log
    {
        static readonly object s_Lock = new object();
        static StreamWriter s_Writer;

        public static void Line(string text)
        {
            lock (s_Lock)
            {
                try
                {
                    if (s_Writer == null)
                    {
                        string path = Environment.GetEnvironmentVariable("CORECLR_STANDIN_LOG") ?? Path.Combine(Path.GetTempPath(), "coreclr-standin.log");
                        s_Writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    s_Writer.WriteLine($"[tid {Environment.CurrentManagedThreadId}] {text}");
                }
                catch (IOException) { }
            }
        }

        public static string Str(byte* utf8) => utf8 == null ? "<null>" : Marshal.PtrToStringUTF8((IntPtr)utf8);
    }

    /// <summary>Makes every managed exception visible in the runtime log, including ones Unity's layer swallows.</summary>
    public static class Diagnostics
    {
        const int MaxReports = 200;
        const int MaxFrames = 14;

        [ThreadStatic] static bool t_Reporting;
        static int s_Count;

        public static void Install()
        {
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                if (t_Reporting) return;
                t_Reporting = true;
                try
                {
                    int n = System.Threading.Interlocked.Increment(ref s_Count);
                    if (n > MaxReports) return;
                    var text = new StringBuilder($"EXCEPTION #{n} {e.Exception.GetType().FullName}: {e.Exception.Message}");
                    // Skip the runtime's own dispatch frames at the top; they are the same for every exception.
                    string[] frames = new System.Diagnostics.StackTrace(1, false).ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                    int shown = 0;
                    foreach (string frame in frames)
                    {
                        if (frame.Contains("System.Runtime.EH.") || frame.Contains("OnFirstChanceException")) continue;
                        if (shown++ == MaxFrames) break;
                        text.Append(Environment.NewLine).Append("   ").Append(frame.TrimEnd());
                    }
                    Log.Line(text.ToString());
                }
                catch (Exception) { }
                finally { t_Reporting = false; }
            };
        }
    }

    public static unsafe class Native
    {
        /// <summary>A NUL-terminated UTF-8 copy that is never freed; for names the engine keeps pointers to.</summary>
        public static byte* AllocUtf8(string s)
        {
            if (s == null) return null;
            int n = Encoding.UTF8.GetByteCount(s);
            byte* p = (byte*)NativeMemory.Alloc((nuint)n + 1);
            fixed (char* c = s) Encoding.UTF8.GetBytes(c, s.Length, p, n);
            p[n] = 0;
            return p;
        }

        sealed class RawData { public byte Data; }

        /// <summary>The first byte after the MethodTable pointer of any object: a box's payload, an object's first field.</summary>
        public static ref byte Payload(object o) => ref Unsafe.As<RawData>(o).Data;

        public static object Box(Type valueType, IntPtr source)
        {
            object box = RuntimeHelpers.GetUninitializedObject(valueType);
            Unsafe.CopyBlockUnaligned(ref Payload(box), ref *(byte*)source, (uint)RuntimeHelpers.SizeOf(valueType.TypeHandle));
            return box;
        }

        public static void Unbox(object box, IntPtr destination)
        {
            Unsafe.CopyBlockUnaligned(ref *(byte*)destination, ref Payload(box), (uint)RuntimeHelpers.SizeOf(box.GetType().TypeHandle));
        }
    }
}

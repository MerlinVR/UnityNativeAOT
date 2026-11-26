using System;

namespace AotRuntime
{
    /// <summary>
    /// StackFrameInterop maps a code address to a method for native crash and profiler stacks. Unity binds it to the
    /// CoreCLR-private StackFrame.GetMethodFromNativeIP. NativeAOT has no public address-to-method lookup, so frames
    /// in managed code are reported as unknown.
    /// </summary>
    public static unsafe class StackFrames
    {
        [Detour(Asm.Scripting, Asm.Interop + "StackFrameInterop", ".cctor")]
        public static void StaticConstructor() { }

        [Detour(Asm.Scripting, Asm.Interop + "StackFrameInterop", "stackframe_from_ip")]
        public static bool FromIp(IntPtr ip, IntPtr methodName, IntPtr signature, IntPtr moduleName, IntPtr sourceFile, uint* lineNumber) => false;
    }
}

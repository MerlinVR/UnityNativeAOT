using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AotRuntime
{
    /// <summary>
    /// Method and field handles given to the engine are NativeAOT RuntimeMethodHandle / RuntimeFieldHandle values. Those
    /// point at MethodHandleInfo / FieldHandleInfo, which both begin with the exact declaring type, so a handle alone
    /// identifies its member even on a generic type. CoreCLR needs the declaring type passed alongside, which is why
    /// Unity's own helpers take a class the engine does not always have.
    /// </summary>
    public static unsafe class Handles
    {
        static RuntimeTypeHandle DeclaringType(IntPtr handle) => Unsafe.ReadUnaligned<RuntimeTypeHandle>((void*)handle);

        public static MethodBase Method(IntPtr handle)
            => handle == IntPtr.Zero ? null : MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr(handle), DeclaringType(handle));

        public static FieldInfo Field(IntPtr handle)
            => handle == IntPtr.Zero ? null : FieldInfo.GetFieldFromHandle(RuntimeFieldHandle.FromIntPtr(handle), DeclaringType(handle));

        [Detour(Asm.Scripting, "Unity.Private.Scripting.Extensions", "MethodInfoFromHandleIntPtr")]
        public static MethodBase MethodInfoFromHandleIntPtr(IntPtr method, IntPtr declaringType)
            => Method(method) ?? throw new ArgumentException("Method handle is null");

        [Detour(Asm.Scripting, "Unity.Private.Scripting.Extensions", "FieldInfoFromHandleIntPtr")]
        public static FieldInfo FieldInfoFromHandleIntPtr(IntPtr field, IntPtr declaringType)
            => Field(field) ?? throw new ArgumentException("Field handle is null");

        // ImageInterop resolves a handle without its declaring type through CoreCLR-private RuntimeMethodHandleInternal.
        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", "GetFieldFromHandle")]
        public static FieldInfo GetFieldFromHandle(RuntimeFieldHandle handle) => Field(handle.Value);

        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", "GetMethodFromHandle")]
        public static MethodBase GetMethodFromHandle(RuntimeMethodHandle handle) => Method(handle.Value);
    }
}

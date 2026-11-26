using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AotRuntime
{
    /// <summary>
    /// What the engine holds as a "class". Unity's fork hands out MethodTable pointers and adds a user-data field to
    /// MethodTable, which the engine then reads directly at klass + mono_class_get_userdata_offset(). NativeAOT's
    /// MethodTable has no such field, so a class handle here is a pointer to one of these instead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClassHandle
    {
        public IntPtr MethodTable;  // 0   NativeAOT MethodTable*, zero when the type has none
        public IntPtr UserData;     // 8   read directly by the engine
        public IntPtr Type;         // 16  GCHandle to the System.Type
        public byte* Name;          // 24  lazily filled, never freed
        public byte* Namespace;     // 32

        public const int UserDataOffset = 8;
    }

    public static unsafe class ClassHandles
    {
        static readonly Dictionary<Type, IntPtr> s_ByType = new Dictionary<Type, IntPtr>();

        public static IntPtr Get(Type type)
        {
            if (type == null) return IntPtr.Zero;
            lock (s_ByType)
            {
                if (s_ByType.TryGetValue(type, out IntPtr existing)) return existing;
                var handle = (ClassHandle*)NativeMemory.AllocZeroed((nuint)sizeof(ClassHandle));
                try { handle->MethodTable = type.TypeHandle.Value; } catch (Exception) { /* metadata-only type */ }
                handle->Type = GCHandle.ToIntPtr(GCHandle.Alloc(type));
                s_ByType.Add(type, (IntPtr)handle);
                return (IntPtr)handle;
            }
        }

        public static Type ToType(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            return (Type)GCHandle.FromIntPtr(((ClassHandle*)handle)->Type).Target;
        }

        // ---- Replacements for Unity.Private.Scripting.Extensions, where every handle conversion goes through ----

        [Detour(Asm.Scripting, "Unity.Private.Scripting.Extensions", "TypeHandleIntPtr")]
        public static IntPtr TypeHandleIntPtr(Type type) => Get(type);

        [Detour(Asm.Scripting, "Unity.Private.Scripting.Extensions", "TypeFromHandleIntPtr")]
        public static Type TypeFromHandleIntPtr(IntPtr handle)
            => ToType(handle) ?? throw new ArgumentException("Type handle is null");

        [Detour(Asm.Scripting, "Unity.Private.Scripting.Extensions", "TypeHandleFromHandleIntPtr")]
        public static RuntimeTypeHandle TypeHandleFromHandleIntPtr(IntPtr handle)
            => handle == IntPtr.Zero ? default : ToType(handle).TypeHandle;

        // ---- The engine modules skip Extensions and pass RuntimeTypeHandle.Value straight to native code ----

        [RedirectCalls(Asm.Core, "System.IntPtr System.RuntimeTypeHandle::get_Value()")]
        [RedirectCalls(Asm.ScriptingModule, "System.IntPtr System.RuntimeTypeHandle::get_Value()")]
        public static IntPtr TypeHandleValue(ref RuntimeTypeHandle handle)
            => handle.Value == IntPtr.Zero ? IntPtr.Zero : Get(Type.GetTypeFromHandle(handle));

        [Detour(Asm.ScriptingModule, "UnityEngine.Bindings.SystemReflectionMarshalling", "UnmarshalRuntimeTypeHandle")]
        public static RuntimeTypeHandle UnmarshalRuntimeTypeHandle(IntPtr handle) => TypeHandleFromHandleIntPtr(handle);

        [Detour(Asm.Core, "UnityEngine.Serialization.SerializationBackendManagedCommands", "UnmarshalSystemType")]
        public static Type UnmarshalSystemType(IntPtr handle) => ToType(handle);

        // ---- The native side of the same contract: exports the engine calls with a class, method or field ----

        [UnmanagedCallersOnly(EntryPoint = "mono_class_get_userdata_offset", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int GetUserDataOffset() => ClassHandle.UserDataOffset;

        [UnmanagedCallersOnly(EntryPoint = "mono_class_set_userdata", CallConvs = new[] { typeof(CallConvCdecl) })]
        static void SetUserData(ClassHandle* klass, IntPtr userData) => klass->UserData = userData;

        [UnmanagedCallersOnly(EntryPoint = "mono_class_get_name", CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte* GetName(ClassHandle* klass)
        {
            if (klass->Name == null) klass->Name = Native.AllocUtf8(ToType((IntPtr)klass).Name);
            return klass->Name;
        }

        [UnmanagedCallersOnly(EntryPoint = "mono_class_get_namespace", CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte* GetNamespace(ClassHandle* klass)
        {
            if (klass->Namespace == null)
            {
                // Like metadata, a nested type has an empty namespace of its own.
                Type type = ToType((IntPtr)klass);
                klass->Namespace = Native.AllocUtf8(type.IsNested ? "" : type.Namespace ?? "");
            }
            return klass->Namespace;
        }

        [UnmanagedCallersOnly(EntryPoint = "mono_class_get_type_token", CallConvs = new[] { typeof(CallConvCdecl) })]
        static uint GetTypeToken(ClassHandle* klass)
        {
            try { return Tokens.TokenOf(ToType((IntPtr)klass)); }
            catch (Exception e) { Log.Line($"mono_class_get_type_token failed: {e}"); return 0; }
        }

        /// <summary>The fork returns instance field bytes plus the MethodTable pointer.</summary>
        [UnmanagedCallersOnly(EntryPoint = "mono_class_instance_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int GetInstanceSize(ClassHandle* klass)
        {
            Type type = ToType((IntPtr)klass);
            if (type.IsValueType) return RuntimeHelpers.SizeOf(type.TypeHandle) + sizeof(void*);
            // MethodTable: { uint flags; uint baseSize; ... } and base size counts the object header too.
            return klass->MethodTable == IntPtr.Zero ? 0 : (int)((uint*)klass->MethodTable)[1] - sizeof(void*);
        }

        static int ElementSize(Type element) => element.IsValueType ? RuntimeHelpers.SizeOf(element.TypeHandle) : sizeof(void*);

        /// <summary>The size one element of this class takes in an array of it.</summary>
        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_class_array_element_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int ClassArrayElementSize(ClassHandle* klass) => ElementSize(ToType((IntPtr)klass));

        [UnmanagedCallersOnly(EntryPoint = "coreclr_unity_array_element_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int ArrayElementSize(ClassHandle* arrayClass) => ElementSize(ToType((IntPtr)arrayClass).GetElementType());

        [UnmanagedCallersOnly(EntryPoint = "mono_unity_class_has_failure", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int HasFailure(ClassHandle* klass) => 0;

        [UnmanagedCallersOnly(EntryPoint = "mono_method_get_class", CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr MethodGetClass(IntPtr method)
        {
            try { return Get(Handles.Method(method)?.DeclaringType); }
            catch (Exception e) { Log.Line($"mono_method_get_class failed: {e}"); return IntPtr.Zero; }
        }

        /// <summary>In the fork a signature is the method itself.</summary>
        [UnmanagedCallersOnly(EntryPoint = "mono_method_signature", CallConvs = new[] { typeof(CallConvCdecl) })]
        static IntPtr MethodSignature(IntPtr method) => method;

        [UnmanagedCallersOnly(EntryPoint = "mono_field_get_name", CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte* FieldGetName(IntPtr field)
        {
            try { return FieldNames.Get(field); }
            catch (Exception e) { Log.Line($"mono_field_get_name failed: {e}"); return null; }
        }

        [UnmanagedCallersOnly(EntryPoint = "mono_field_get_offset", CallConvs = new[] { typeof(CallConvCdecl) })]
        static int FieldGetOffset(IntPtr field)
        {
            try
            {
                return FieldOffsets.Of(Handles.Field(field));
            }
            catch (Exception e) { Log.Line($"mono_field_get_offset failed: {e}"); return 0; }
        }
    }

    static unsafe class FieldNames
    {
        static readonly Dictionary<IntPtr, IntPtr> s_Names = new Dictionary<IntPtr, IntPtr>();

        public static byte* Get(IntPtr field)
        {
            lock (s_Names)
            {
                if (!s_Names.TryGetValue(field, out IntPtr name))
                    s_Names[field] = name = (IntPtr)Native.AllocUtf8(Handles.Field(field).Name);
                return (byte*)name;
            }
        }
    }

    /// <summary>
    /// Instance field offsets, counted from the object pointer as the fork does (so the first field is at 8).
    /// NativeAOT has no public API for this; its reflection keeps the offset on the internal RuntimeFieldInfo.
    /// </summary>
    static class FieldOffsets
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Offset")]
        static extern int GetOffset([UnsafeAccessorType("System.Reflection.Runtime.FieldInfos.RuntimeFieldInfo, System.Private.CoreLib")] object field);

        public static int Of(FieldInfo field) => field.IsStatic ? 0 : GetOffset(field) + IntPtr.Size;
    }
}

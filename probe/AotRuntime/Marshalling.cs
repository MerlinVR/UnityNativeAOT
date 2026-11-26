using System;
using System.Reflection;
using UnityEngine.Bindings;

namespace AotRuntime
{
    /// <summary>
    /// Managed wrappers of native objects (AsyncOperation, AnimationCurve, ...) keep the native pointer in their first
    /// field. Unity emits three DynamicMethods per wrapper type to read it, write it and default-construct the wrapper.
    /// Reflection does the same here; a real pipeline would generate these accessors at build time.
    /// </summary>
    public static class Marshalling
    {
        [Detour(Asm.ScriptingModule, "UnityEngine.Bindings.IntPtrObjectMarshalling", "CreateAccess")]
        public static IntPtrObjectMarshalling.IntPtrObjectAccess CreateAccess(Type type)
        {
            FieldInfo first = IntPtrObjectMarshalling.GetFirstField(type);
            if (first == null || first.FieldType != typeof(IntPtr))
                throw new InvalidOperationException("IntPtrObjectAccess only be used on types that have a first field of IntPtr " + type.FullName + " does not");

            ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var access = default(IntPtrObjectMarshalling.IntPtrObjectAccess);
            access.SetIntPtr = (obj, pointer) => first.SetValue(obj, pointer);
            access.GetIntPtr = obj => (IntPtr)first.GetValue(obj);
            if (constructor != null)
            {
                access.CreateDefault = (out IntPtr pointer) =>
                {
                    object created = constructor.Invoke(null);
                    pointer = (IntPtr)first.GetValue(created);
                    return created;
                };
            }
            return access;
        }
    }
}

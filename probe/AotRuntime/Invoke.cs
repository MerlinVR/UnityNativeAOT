using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Unity.Private.Scripting;
using Unity.Private.Scripting.Interop;

namespace AotRuntime
{
    /// <summary>
    /// runtime_invoke. Unity emits one DynamicMethod per target that unpacks the engine's void** argument array and
    /// calls the method. Without a JIT the same unpacking is done here and the call goes through reflection.
    /// The argument conventions below mirror RuntimeInterop.LoadParameters.
    /// </summary>
    public static unsafe class Invoke
    {
        [Detour(Asm.Scripting, Asm.Interop + "RuntimeInterop", "GenerateInvokeMethod")]
        public static RuntimeInterop.CacheInfo GenerateInvokeMethod(IntPtr method, IntPtr klass)
        {
            MethodBase target = Handles.Method(method);
            if (target == null)
            {
                // The engine invokes callbacks of its built-in modules without checking that the lookup found them.
                // It always does in a stock player; here it means the module's assembly was not compiled in.
                Log.Line($"runtime_invoke of a null method (class {ClassHandles.ToType(klass)?.FullName ?? "null"}); ignored");
                return new RuntimeInterop.CacheInfo((obj, arguments) => null);
            }
            return new RuntimeInterop.CacheInfo(new Invoker(target).Call);
        }

        sealed class Invoker
        {
            readonly MethodBase _method;
            readonly ParameterInfo[] _parameters;
            readonly Type _thisValueType;
            readonly Type _returnEnumUnderlying;

            public Invoker(MethodBase method)
            {
                _method = method;
                _parameters = method.GetParameters();
                if (!method.IsStatic && method.DeclaringType.IsValueType) _thisValueType = method.DeclaringType;
                if (method is MethodInfo info && info.ReturnType.IsEnum) _returnEnumUnderlying = Enum.GetUnderlyingType(info.ReturnType);
            }

            public object Call(IntPtr obj, IntPtr arguments)
            {
                object target = null;
                if (!_method.IsStatic)
                    target = _thisValueType != null ? Native.Box(_thisValueType, obj) : obj.ToManagedRepresentation();

                var slots = (IntPtr*)arguments;
                object[] args = _parameters.Length == 0 ? Array.Empty<object>() : new object[_parameters.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    Type type = _parameters[i].ParameterType;
                    if (!type.IsByRef)
                    {
                        // Values arrive as a pointer to the value, objects as the engine's object representation.
                        args[i] = type.IsValueType ? Native.Box(type, slots[i])
                                : type.IsPointer || type.IsFunctionPointer ? Pointer.Box((void*)*(IntPtr*)slots[i], type)
                                : slots[i].ToManagedRepresentation();
                        continue;
                    }
                    Type element = type.GetElementType();
                    if (element.IsValueType) args[i] = Native.Box(element, slots[i]);
                    else if (!_parameters[i].IsOut) args[i] = (*(IntPtr*)slots[i]).ToManagedRepresentation();
                }

                object result = _method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);

                for (int i = 0; i < args.Length; i++)
                {
                    Type type = _parameters[i].ParameterType;
                    if (!type.IsByRef) continue;
                    if (type.GetElementType().IsValueType) { if (args[i] != null) Native.Unbox(args[i], slots[i]); }
                    else *(IntPtr*)slots[i] = args[i].ToNativeRepresentation();
                }
                if (_thisValueType != null) Native.Unbox(target, obj);

                if (result != null && _returnEnumUnderlying != null) result = Convert.ChangeType(result, _returnEnumUnderlying);
                return result;
            }
        }

        [Detour(Asm.Scripting, "Unity.Private.Scripting.NativeSupport", "SizeOfType")]
        public static int SizeOfType(Type type) => type == null ? 0 : RuntimeHelpers.SizeOf(type.TypeHandle);
    }
}

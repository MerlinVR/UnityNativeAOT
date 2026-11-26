using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AotRuntime
{
    /// <summary>
    /// Marshal.SizeOf(Type) and Marshal.OffsetOf(Type, string) need layout data that ILC only generates for the
    /// structs it can see being marshalled. Unity asks for types it holds in a variable (Graphics.RenderMeshInstanced
    /// asks for the layout of its instance struct), and ILC cannot see those. Where the data is missing the managed
    /// layout is used, which is the same thing for the blittable structs Unity passes to the engine.
    /// </summary>
    public static class MarshalGaps
    {
        static readonly Dictionary<Type, int> s_Size = new Dictionary<Type, int>();
        static readonly Dictionary<(Type, string), IntPtr> s_Offset = new Dictionary<(Type, string), IntPtr>();

        [RedirectCalls("*", "System.Int32 System.Runtime.InteropServices.Marshal::SizeOf(System.Type)")]
        public static int SizeOf(Type type)
        {
            lock (s_Size)
            {
                if (s_Size.TryGetValue(type, out int size)) return size;
                try { size = Marshal.SizeOf(type); }
                catch (NotSupportedException) { size = RuntimeHelpers.SizeOf(type.TypeHandle); }
                s_Size[type] = size;
                return size;
            }
        }

        [RedirectCalls("*", "System.IntPtr System.Runtime.InteropServices.Marshal::OffsetOf(System.Type,System.String)")]
        public static IntPtr OffsetOf(Type type, string fieldName)
        {
            lock (s_Offset)
            {
                if (s_Offset.TryGetValue((type, fieldName), out IntPtr offset)) return offset;
                try { offset = Marshal.OffsetOf(type, fieldName); }
                catch (NotSupportedException)
                {
                    FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new ArgumentException($"Field '{fieldName}' is not a field of {type}.", nameof(fieldName));
                    // FieldOffsets counts from the object pointer; a struct's own layout starts after the type pointer.
                    offset = (IntPtr)(FieldOffsets.Of(field) - IntPtr.Size);
                }
                s_Offset[(type, fieldName)] = offset;
                return offset;
            }
        }
    }

    /// <summary>Parts of reflection that NativeAOT does not carry, rebuilt from the parts it does.</summary>
    public static class ReflectionGaps
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Dictionary<MemberInfo, int> s_Order = new Dictionary<MemberInfo, int>();

        /// <summary>
        /// NativeAOT keeps no metadata tokens, and MemberInfo.MetadataToken throws. Unity's code reads tokens to put
        /// members in declaration order: URP's volume framework sorts the fields of every volume component by token.
        /// A number that sorts the same way stands in for it: the depth of the declaring type, then the member's place
        /// among the members that type declares.
        /// </summary>
        [RedirectCalls("*", "System.Int32 System.Reflection.MemberInfo::get_MetadataToken()")]
        public static int MetadataToken(MemberInfo member)
        {
            lock (s_Order)
            {
                if (s_Order.TryGetValue(member, out int order)) return order;

                Type declaring = member.DeclaringType;
                int depth = 0, index = 0;
                if (declaring != null)
                {
                    for (Type t = declaring.BaseType; t != null; t = t.BaseType) depth++;
                    MemberInfo[] declared = declaring.GetMembers(Declared);
                    index = Array.IndexOf(declared, member);
                    if (index < 0) index = declared.Length;
                }
                order = (depth << 20) | (index + 1);
                s_Order[member] = order;
                return order;
            }
        }

        static readonly ConditionalWeakTable<Delegate, MethodInfo> s_MethodOfDelegate = new ConditionalWeakTable<Delegate, MethodInfo>();
        static readonly Dictionary<MethodInfo, MethodInfo> s_OneMethodInfo = new Dictionary<MethodInfo, MethodInfo>();

        /// <summary>
        /// Delegate.Method. CoreCLR, Mono and IL2CPP hand out the same MethodInfo object for a method every time.
        /// NativeAOT builds a new one on each call: equal, but not the same object. The render graph hashes a pass
        /// by RuntimeHelpers.GetHashCode(renderFunc.Method), so without this its hash changes every frame, the graph
        /// is compiled again every frame, and a cache keyed by that hash grows for as long as the player runs.
        /// </summary>
        [RedirectCalls("*", "System.Reflection.MethodInfo System.Delegate::get_Method()")]
        public static MethodInfo DelegateMethod(Delegate del)
        {
            if (del == null) throw new NullReferenceException();
            if (s_MethodOfDelegate.TryGetValue(del, out MethodInfo method)) return method;

            method = del.Method;
            if (method == null) return null;
            lock (s_OneMethodInfo)
            {
                if (s_OneMethodInfo.TryGetValue(method, out MethodInfo first)) method = first;
                else s_OneMethodInfo[method] = method;
            }
            s_MethodOfDelegate.AddOrUpdate(del, method);
            return method;
        }
    }
}

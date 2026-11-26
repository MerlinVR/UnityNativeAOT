using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Unity.Private.Scripting;
using Unity.Private.Scripting.AssemblyLoader;
using Unity.Private.Scripting.Interop;

namespace AotRuntime
{
    /// <summary>Every assembly is compiled into this library, so "loading" one means finding it by name.</summary>
    public static class AotAssemblies
    {
        static Dictionary<string, Assembly> s_ByName;
        static int s_Skipped;

        static Dictionary<string, Assembly> ByName
        {
            get
            {
                if (s_ByName == null)
                {
                    var map = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                        map[assembly.GetName().Name] = assembly;
                    s_ByName = map;
                }
                return s_ByName;
            }
        }

        public static int Count => ByName.Count;

        public static Assembly Find(string simpleName) => ByName.TryGetValue(simpleName, out Assembly assembly) ? assembly : null;

        // Unity loads from a path, into the default context with LoadFromAssemblyPath or into a collectible one from a
        // stream. Neither exists here; the path only tells us which compiled-in assembly is meant.
        [Detour(Asm.Scripting, Asm.Loader + "UnityAssemblyLoadContextWrapper", "ForceLoadIntoAlcFromPath")]
        public static Assembly ForceLoadIntoAlcFromPath(UnityAssemblyLoadContextWrapper self, string assemblyPath)
        {
            Assembly assembly = Find(Path.GetFileNameWithoutExtension(assemblyPath));
            if (assembly == null)
            {
                // The Managed folder still holds every IL assembly of the stock build; only the rooted ones exist here.
                if (++s_Skipped == 1) Log.Line($"not compiled in, skipped (first of many): {assemblyPath}");
                return null;
            }
            AssemblyLoadContextInterop.AssemblyInteropCache.CacheAssemblyAndGetAssemblyHandle(assembly, assemblyPath);
            return assembly;
        }

        [Detour(Asm.Scripting, Asm.Loader + "UnityAssemblyLoadContextWrapper", "LoadFromPath")]
        public static Assembly LoadFromPath(UnityAssemblyLoadContextWrapper self, string assemblyPath)
        {
            var name = new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath));
            Assembly assembly = self.GetOrLoadFromAssemblyPath(assemblyPath, name)
                ?? throw new FileNotFoundException("Assembly is not compiled into this player", assemblyPath);
            self.AssemblyResolver.RegisterExternallyLoadedAssembly(self.TargetAlc, assembly.GetName(), assemblyPath);
            return assembly;
        }

        /// <summary>
        /// Unity's fork adds AssemblyLoadContext.s_ResolveAssemblyLocationOverride so Assembly.Location can report the
        /// path an assembly was loaded from as a stream. Stock CoreLib has no such field.
        /// </summary>
        [Detour(Asm.Scripting, Asm.Loader + "UnityAssemblyLoadContextWrapper", "SetupAssemblyLocationResolution")]
        public static void SetupAssemblyLocationResolution() { }

        [Detour(Asm.Scripting, Asm.Loader + "AssemblyInteropCache", "GetAssemblyTypeDefTableRowCount")]
        public static int GetAssemblyTypeDefTableRowCount(Assembly assembly, string assemblyFilename) => Tokens.TypeDefCount(assembly);
    }

    /// <summary>
    /// TypeDef tokens. NativeAOT keeps no ECMA-335 tokens, so they are read from the IL file the assembly was compiled
    /// from and matched to types by name. A build pipeline would emit this table instead of shipping the IL.
    /// </summary>
    public static class Tokens
    {
        // Names come from the IL file once per assembly; a type is only looked up when the engine asks for its row.
        sealed class Table
        {
            public string[] NameByRow = Array.Empty<string>();
            public Type[] TypeByRow = Array.Empty<Type>();
            public readonly Dictionary<string, int> RowByName = new Dictionary<string, int>();
            public readonly Dictionary<Type, uint> TokenByType = new Dictionary<Type, uint>();
        }

        static readonly Dictionary<Assembly, Table> s_Tables = new Dictionary<Assembly, Table>();
        static readonly Dictionary<Assembly, int> s_Counts = new Dictionary<Assembly, int>();

        static string IlPath(Assembly assembly) => Path.Combine(Host.ManagedDir, assembly.GetName().Name + ".dll");

        static Table For(Assembly assembly)
        {
            if (!s_Tables.TryGetValue(assembly, out Table table))
                s_Tables[assembly] = table = Build(assembly);
            return table;
        }

        static Table Build(Assembly assembly)
        {
            var table = new Table();
            string path = IlPath(assembly);
            if (!File.Exists(path))
            {
                Log.Line($"tokens: no IL file for {assembly.GetName().Name}");
                return table;
            }
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader md = pe.GetMetadataReader();
            int count = md.GetTableRowCount(TableIndex.TypeDef);
            table.NameByRow = new string[count + 1];
            table.TypeByRow = new Type[count + 1];
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                int row = MetadataTokens.GetRowNumber(handle);
                string name = FullName(md, handle);
                table.NameByRow[row] = name;
                table.RowByName[name] = row;
            }
            return table;
        }

        static string FullName(MetadataReader md, TypeDefinitionHandle handle)
        {
            TypeDefinition def = md.GetTypeDefinition(handle);
            string name = md.GetString(def.Name);
            TypeDefinitionHandle declaring = def.GetDeclaringType();
            if (!declaring.IsNil) return FullName(md, declaring) + "+" + name;
            string ns = md.GetString(def.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <summary>Asked once for every assembly as it loads, so it reads the count and nothing else.</summary>
        public static int TypeDefCount(Assembly assembly)
        {
            lock (s_Tables)
            {
                if (s_Counts.TryGetValue(assembly, out int count)) return count;
                string path = IlPath(assembly);
                if (File.Exists(path))
                {
                    using FileStream stream = File.OpenRead(path);
                    using var pe = new PEReader(stream);
                    count = pe.GetMetadataReader().GetTableRowCount(TableIndex.TypeDef);
                }
                else Log.Line($"tokens: no IL file for {assembly.GetName().Name}");
                s_Counts[assembly] = count;
                return count;
            }
        }

        public static uint TokenOf(Type type)
        {
            const uint typeDef = 0x02000000;
            if (type.HasElementType || type.IsGenericParameter) return typeDef;
            if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
            lock (s_Tables)
            {
                Table table = For(type.Assembly);
                if (table.TokenByType.TryGetValue(type, out uint token)) return token;
                token = type.FullName != null && table.RowByName.TryGetValue(type.FullName, out int row) ? typeDef | (uint)row : typeDef;
                table.TokenByType[type] = token;
                return token;
            }
        }

        public static Type TypeFromToken(Assembly assembly, uint token)
        {
            uint row = token & 0x00FFFFFF;
            if ((token >> 24) != 0x02) return null;
            lock (s_Tables)
            {
                Table table = For(assembly);
                if (row >= table.NameByRow.Length || table.NameByRow[row] == null) return null;
                Type type = table.TypeByRow[row];
                if (type == null)
                {
                    try { type = assembly.GetType(table.NameByRow[row], throwOnError: false); } catch (Exception) { }
                    table.TypeByRow[row] = type;
                }
                return type;
            }
        }

        // ---- ImageInterop: its static constructor binds to CoreCLR-private RuntimeFieldHandleInternal/RuntimeMethodHandleInternal ----

        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", ".cctor")]
        public static void ImageInteropStaticConstructor() { }

        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", "image_get_class_from_token_checked")]
        public static IntPtr ClassFromToken(IntPtr image, uint token)
            => ClassHandles.Get(TypeFromToken(image.AssemblyFromGCHandleIntPtr(), token));

        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", "image_get_method_from_token_checked")]
        public static IntPtr MethodFromToken(IntPtr image, uint token, IntPtr outDeclaringType)
            => throw new NotSupportedException($"method token 0x{token:x8}: member tokens are not available in an AOT-compiled player");

        [Detour(Asm.Scripting, Asm.Interop + "ImageInterop", "image_get_field_from_token_checked")]
        public static IntPtr FieldFromToken(IntPtr image, uint token, IntPtr declaringKlass, IntPtr retklass)
            => throw new NotSupportedException($"field token 0x{token:x8}: member tokens are not available in an AOT-compiled player");
    }
}

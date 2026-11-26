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
        sealed class Table
        {
            public Type[] ByRow = Array.Empty<Type>();
            public readonly Dictionary<Type, int> Rows = new Dictionary<Type, int>();
            public int TypeDefCount;
        }

        static readonly Dictionary<Assembly, Table> s_Tables = new Dictionary<Assembly, Table>();

        static Table For(Assembly assembly)
        {
            lock (s_Tables)
            {
                if (!s_Tables.TryGetValue(assembly, out Table table))
                    s_Tables[assembly] = table = Build(assembly);
                return table;
            }
        }

        static Table Build(Assembly assembly)
        {
            var table = new Table();
            string path = Path.Combine(Host.ManagedDir, assembly.GetName().Name + ".dll");
            if (!File.Exists(path))
            {
                Log.Line($"tokens: no IL file for {assembly.GetName().Name}");
                return table;
            }
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader md = pe.GetMetadataReader();
            table.TypeDefCount = md.GetTableRowCount(TableIndex.TypeDef);
            table.ByRow = new Type[table.TypeDefCount + 1];
            int missing = 0;
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                Type type = null;
                try { type = assembly.GetType(FullName(md, handle), throwOnError: false); } catch (Exception) { }
                if (type == null) { missing++; continue; }
                int row = MetadataTokens.GetRowNumber(handle);
                table.ByRow[row] = type;
                table.Rows[type] = row;
            }
            Log.Line($"tokens: {assembly.GetName().Name} has {table.TypeDefCount} TypeDefs, {missing} without a compiled type");
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

        public static int TypeDefCount(Assembly assembly) => For(assembly).TypeDefCount;

        public static uint TokenOf(Type type)
        {
            const uint typeDef = 0x02000000;
            if (type.HasElementType || type.IsGenericParameter) return typeDef;
            if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
            return For(type.Assembly).Rows.TryGetValue(type, out int row) ? typeDef | (uint)row : typeDef;
        }

        public static Type TypeFromToken(Assembly assembly, uint token)
        {
            Table table = For(assembly);
            uint row = token & 0x00FFFFFF;
            return (token >> 24) == 0x02 && row < table.ByRow.Length ? table.ByRow[row] : null;
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

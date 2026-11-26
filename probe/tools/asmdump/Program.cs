using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline .NET assembly inspection helper.
//   asmdump stats <dll>...                       per-assembly interop statistics
//   asmdump refs <dll>                           assembly + module references
//   asmdump types <dll> [regex]                  list types
//   asmdump members <dll> <typeRegex>            list fields/methods with interop attributes
//   asmdump il <dll> <typeRegex> <methodRegex>   dump IL
//   asmdump cs <dll> <typeFullName>...           decompile types to C#
//   asmdump csm <dll> <typeFullName> [regex]     decompile a type's methods one at a time
//   asmdump find <dll> <regex>                   find methods whose IL references a member/string matching regex
//   asmdump calli <dll>...                       histogram of calli calling conventions
//   asmdump publicize <in> <out> <type> <method>...   make one type and some of its methods public
//   asmdump publicize-all <outDir> <dll>...      write all-public copies to compile against
//   asmdump detour <shims.dll> <inDir> <outDir>  redirect methods to [Detour] shims

if (args.Length < 2) { Console.Error.WriteLine("usage: asmdump <cmd> <dll> ..."); return 1; }
var cmd = args[0];

static ModuleDefinition Load(string path)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
    return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred });
}

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
{
    foreach (var t in m.Types)
    {
        yield return t;
        foreach (var n in Nested(t)) yield return n;
    }
}

static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
{
    foreach (var n in t.NestedTypes)
    {
        yield return n;
        foreach (var nn in Nested(n)) yield return nn;
    }
}

static string Attrs(ICustomAttributeProvider p)
{
    if (!p.HasCustomAttributes) return "";
    var sb = new StringBuilder();
    foreach (var a in p.CustomAttributes)
    {
        var name = a.AttributeType.Name;
        if (name.EndsWith("Attribute")) name = name[..^9];
        if (name is "CompilerGenerated" or "DebuggerHidden" or "NullableContext" or "Nullable" or "IsReadOnly" or "DebuggerStepThrough" or "AsyncStateMachine" or "IteratorStateMachine" or "DebuggerBrowsable" or "DebuggerNonUserCode") continue;
        sb.Append('[').Append(name);
        try
        {
            var parts = new List<string>();
            foreach (var ca in a.ConstructorArguments) parts.Add(Fmt(ca));
            foreach (var na in a.Properties) parts.Add(na.Name + "=" + Fmt(na.Argument));
            foreach (var na in a.Fields) parts.Add(na.Name + "=" + Fmt(na.Argument));
            if (parts.Count > 0) sb.Append('(').Append(string.Join(", ", parts)).Append(')');
        }
        catch { sb.Append("(?)"); }
        sb.Append("] ");
    }
    return sb.ToString();
}

static string Fmt(CustomAttributeArgument a)
{
    if (a.Value is CustomAttributeArgument[] arr) return "{" + string.Join(",", arr.Select(Fmt)) + "}";
    if (a.Value is string s) return "\"" + s + "\"";
    if (a.Value is TypeReference tr) return "typeof(" + tr.Name + ")";
    return a.Value?.ToString() ?? "null";
}

static string Sig(MethodDefinition m)
{
    var sb = new StringBuilder();
    if (m.IsStatic) sb.Append("static ");
    if (m.IsPInvokeImpl) sb.Append("pinvoke ");
    if (m.IsInternalCall) sb.Append("icall ");
    if (m.IsAbstract) sb.Append("abstract ");
    sb.Append(m.ReturnType.FullName).Append(' ').Append(m.Name).Append('(');
    sb.Append(string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name)));
    sb.Append(')');
    if (m.IsPInvokeImpl && m.PInvokeInfo != null) sb.Append("  => ").Append(m.PInvokeInfo.Module.Name).Append('!').Append(m.PInvokeInfo.EntryPoint);
    return sb.ToString();
}

switch (cmd)
{
    case "stats":
    {
        foreach (var path in args.Skip(1))
        {
            ModuleDefinition mod;
            try { mod = Load(path); } catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path)}: not managed ({e.GetType().Name})"); continue; }
            int types = 0, methods = 0, icalls = 0, pinvokes = 0, uco = 0, calli = 0, ldftn = 0, withBody = 0, fnptrFields = 0;
            var pinvokeModules = new Dictionary<string, int>();
            foreach (var t in AllTypes(mod))
            {
                types++;
                foreach (var f in t.Fields) if (f.FieldType is Mono.Cecil.FunctionPointerType) fnptrFields++;
                foreach (var m in t.Methods)
                {
                    methods++;
                    if (m.IsInternalCall) icalls++;
                    if (m.IsPInvokeImpl)
                    {
                        pinvokes++;
                        var mn = m.PInvokeInfo?.Module?.Name ?? "?";
                        pinvokeModules[mn] = pinvokeModules.GetValueOrDefault(mn) + 1;
                    }
                    if (m.HasCustomAttributes && m.CustomAttributes.Any(a => a.AttributeType.Name == "UnmanagedCallersOnlyAttribute")) uco++;
                    if (m.HasBody)
                    {
                        withBody++;
                        try
                        {
                            foreach (var ins in m.Body.Instructions)
                            {
                                if (ins.OpCode == OpCodes.Calli) calli++;
                                else if (ins.OpCode == OpCodes.Ldftn) ldftn++;
                            }
                        }
                        catch { }
                    }
                }
            }
            Console.WriteLine($"{Path.GetFileName(path)}: types={types} methods={methods} icall={icalls} pinvoke={pinvokes} UnmanagedCallersOnly={uco} calli={calli} ldftn={ldftn} fnptrFields={fnptrFields}");
            foreach (var kv in pinvokeModules.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"    pinvoke module {kv.Key}: {kv.Value}");
        }
        break;
    }
    case "publicize":
    {
        // asmdump publicize <in.dll> <out.dll> <TypeFullName> <MethodName>...
        // Makes one type and the named methods public in a copy, leaving everything else untouched.
        var mod = Load(args[1]);
        var type = AllTypes(mod).First(t => t.FullName == args[3]);
        if (type.IsNested) type.IsNestedPublic = true; else type.IsPublic = true;
        foreach (var name in args.Skip(4))
        {
            var method = type.Methods.Single(m => m.Name == name);
            method.IsPublic = true;
            Console.WriteLine($"made public: {type.FullName}::{method.Name}");
        }
        mod.Write(args[2]);
        break;
    }
    case "publicize-all":
    {
        // asmdump publicize-all <outDir> <dll>...
        // Writes copies in which every type, method and field is public. They are compile-time references only:
        // ILC does not check accessibility, so code compiled against them runs against the untouched originals.
        var outDir = args[1];
        Directory.CreateDirectory(outDir);
        foreach (var path in args.Skip(2))
        {
            var outPath = Path.Combine(outDir, Path.GetFileName(path));
            if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(path)) continue;
            var mod = Load(path);
            foreach (var t in AllTypes(mod))
            {
                // Unity embeds internal polyfills of BCL types (UnmanagedCallersOnlyAttribute, ...). Made public they
                // would collide with the real ones in every file that uses them.
                var top = t; while (top.IsNested) top = top.DeclaringType;
                if (top.Namespace.StartsWith("System") || top.Namespace.StartsWith("Microsoft")) continue;
                if (t.IsNested) t.IsNestedPublic = true; else t.IsPublic = true;
                // An event and its backing field share a name; leave those fields alone so C# can still bind the event.
                var eventNames = new HashSet<string>(t.Events.Select(e => e.Name));
                foreach (var m in t.Methods) m.IsPublic = true;
                foreach (var f in t.Fields) if (!eventNames.Contains(f.Name)) f.IsPublic = true;
            }
            mod.Write(outPath);
            Console.WriteLine("publicized " + Path.GetFileName(path));
        }
        break;
    }
    case "detour":
    {
        // asmdump detour <shims.dll> <inDir> <outDir>
        // For every static method in shims.dll marked [Detour("Assembly", "Type.FullName", "Method")], replaces the body of
        // that method in <inDir>\Assembly.dll with a call to the shim and writes the result to <outDir>. A shim for an
        // instance method takes the instance as its first parameter. Overloads are picked by the shim's parameter types.
        // [RedirectCalls("Assembly", "<callee full name>")] instead rewrites every call to that callee inside Assembly,
        // or with "*" inside every Unity*.dll and Assembly-CSharp*.dll of <inDir>.
        var shims = Load(args[1]);
        var inDir = args[2];
        var outDir = args[3];
        Directory.CreateDirectory(outDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.dll")) File.Delete(stale);

        var targets = new Dictionary<string, ModuleDefinition>();
        int count = 0, failed = 0, redirected = 0;

        // References a shim using the target module's own type references, so no self-reference is imported.
        MethodReference ShimRef(ModuleDefinition mod, MethodDefinition shim, TypeReference returnType, IEnumerable<TypeReference> parameters)
        {
            var shimAsm = mod.AssemblyReferences.FirstOrDefault(r => r.Name == shims.Assembly.Name.Name);
            if (shimAsm == null) mod.AssemblyReferences.Add(shimAsm = new Mono.Cecil.AssemblyNameReference(shims.Assembly.Name.Name, shims.Assembly.Name.Version));
            TypeReference TypeRef(TypeDefinition t) => t.IsNested
                ? new TypeReference("", t.Name, mod, null) { DeclaringType = TypeRef(t.DeclaringType) }
                : new TypeReference(t.Namespace, t.Name, mod, shimAsm);
            var call = new MethodReference(shim.Name, returnType, TypeRef(shim.DeclaringType)) { HasThis = false };
            foreach (var p in parameters) call.Parameters.Add(new ParameterDefinition(p));
            return call;
        }

        foreach (var shimType in AllTypes(shims))
        foreach (var shim in shimType.Methods)
        {
            foreach (var attr in shim.CustomAttributes.Where(a => a.AttributeType.Name == "RedirectCallsAttribute"))
            {
                var asmName = (string)attr.ConstructorArguments[0].Value;
                var callee = (string)attr.ConstructorArguments[1].Value;
                // "*" is every assembly of Unity's and of the game's that makes the call; none of them has to.
                bool everywhere = asmName == "*";
                var names = everywhere
                    ? Directory.GetFiles(inDir, "*.dll").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => n.StartsWith("Unity") || n.StartsWith("Assembly-CSharp")).OrderBy(n => n).ToList()
                    : new List<string> { asmName };
                int sites = 0;
                foreach (var name in names)
                {
                    bool kept = targets.TryGetValue(name, out var mod);
                    if (!kept)
                    {
                        var p = Path.Combine(inDir, name + ".dll");
                        if (!File.Exists(p)) { failed++; Console.WriteLine($"REDIRECT FAILED {shim.Name}: assembly {name} not found"); continue; }
                        mod = Load(p);
                    }
                    int before = sites;
                    foreach (var method in AllTypes(mod).SelectMany(t => t.Methods).Where(m => m.HasBody))
                    foreach (var ins in method.Body.Instructions)
                    {
                        if ((ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) || ins.Operand is not MethodReference called || called.FullName != callee) continue;
                        var parameters = new List<TypeReference>();
                        if (called.HasThis)
                            parameters.Add(shim.Parameters[0].ParameterType.IsByReference ? new Mono.Cecil.ByReferenceType(called.DeclaringType) : called.DeclaringType);
                        parameters.AddRange(called.Parameters.Select(p => p.ParameterType));
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = ShimRef(mod, shim, called.ReturnType, parameters);
                        sites++;
                    }
                    // An assembly that was only searched is not written out.
                    if (!kept && (sites > before || !everywhere)) targets[name] = mod;
                }
                if (sites == 0 && !everywhere) { failed++; Console.WriteLine($"REDIRECT FAILED {shim.Name}: no call to '{callee}' in {asmName}"); }
                redirected += sites;
            }

            foreach (var attr in shim.CustomAttributes.Where(a => a.AttributeType.Name == "DetourAttribute"))
            {
                var asmName = (string)attr.ConstructorArguments[0].Value;
                var typeName = (string)attr.ConstructorArguments[1].Value;
                var methodName = (string)attr.ConstructorArguments[2].Value;
                string Fail(string why) { failed++; return $"DETOUR FAILED {shimType.FullName}::{shim.Name} -> [{asmName}] {typeName}::{methodName}: {why}"; }

                if (!shim.IsStatic || shim.HasGenericParameters || shimType.HasGenericParameters) { Console.WriteLine(Fail("shim must be a non-generic static method")); continue; }
                if (!targets.TryGetValue(asmName, out var mod))
                {
                    var p = Path.Combine(inDir, asmName + ".dll");
                    if (!File.Exists(p)) { Console.WriteLine(Fail("assembly not found")); continue; }
                    targets[asmName] = mod = Load(p);
                }
                var type = AllTypes(mod).FirstOrDefault(t => t.FullName == typeName);
                if (type == null) { Console.WriteLine(Fail("type not found")); continue; }
                if (type.HasGenericParameters) { Console.WriteLine(Fail("generic declaring types are not supported")); continue; }

                var shimParams = shim.Parameters.Select(p => p.ParameterType.FullName).ToArray();
                string[] Expected(MethodDefinition m)
                {
                    var list = new List<string>();
                    if (!m.IsStatic) list.Add(type.IsValueType ? type.FullName + "&" : type.FullName);
                    list.AddRange(m.Parameters.Select(p => p.ParameterType.FullName));
                    return list.ToArray();
                }
                var named = type.Methods.Where(m => m.Name == methodName && m.HasBody && !m.HasGenericParameters).ToList();
                var match = named.Where(m => Expected(m).SequenceEqual(shimParams) && m.ReturnType.FullName == shim.ReturnType.FullName).ToList();
                if (match.Count != 1)
                {
                    Console.WriteLine(Fail($"{match.Count} overloads match shim ({shim.ReturnType.FullName})({string.Join(", ", shimParams)}); candidates:"));
                    foreach (var m in named) Console.WriteLine($"    ({m.ReturnType.FullName})({string.Join(", ", Expected(m))})");
                    continue;
                }
                var target = match[0];

                var callParams = new List<TypeReference>();
                if (!target.IsStatic) callParams.Add(type.IsValueType ? new Mono.Cecil.ByReferenceType(type) : type);
                callParams.AddRange(target.Parameters.Select(p => p.ParameterType));
                var call = ShimRef(mod, shim, target.ReturnType, callParams);

                var body = target.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                var il = body.GetILProcessor();
                for (int i = 0; i < call.Parameters.Count; i++) il.Emit(OpCodes.Ldarg, i);
                il.Emit(OpCodes.Call, call);
                il.Emit(OpCodes.Ret);
                if (target.DebugInformation != null) { target.DebugInformation.SequencePoints.Clear(); target.DebugInformation.Scope = null; }
                count++;
            }
        }
        foreach (var kv in targets) kv.Value.Write(Path.Combine(outDir, kv.Key + ".dll"));
        Console.WriteLine($"detoured {count} methods and redirected {redirected} call sites in {targets.Count} assemblies: {string.Join(", ", targets.Keys)}");
        if (failed > 0) return 1;
        break;
    }
    case "csm":
    {
        // asmdump csm <dll> <TypeFullName> [methodRegex]   decompile methods one at a time, surviving ones that fail
        var path = args[1];
        var pe = new PEFile(path);
        var resolver = new UniversalAssemblyResolver(path, false, pe.Reader.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false, ShowXmlDocumentation = false };
        var dec = new CSharpDecompiler(path, resolver, settings);
        var def = dec.TypeSystem.FindType(new FullTypeName(args[2])).GetDefinition();
        if (def == null) { Console.Error.WriteLine("type not found: " + args[2]); return 1; }
        var rx = args.Length > 3 ? new Regex(args[3]) : null;
        foreach (var m in def.Methods)
        {
            if (rx != null && !rx.IsMatch(m.Name)) continue;
            try { Console.WriteLine(dec.DecompileAsString(m.MetadataToken)); }
            catch (Exception e) { Console.WriteLine($"// failed to decompile {m.Name}: {e.GetType().Name}"); }
        }
        break;
    }
    case "calli":
    {
        // histogram of calli call-site calling conventions per assembly
        foreach (var path in args.Skip(1))
        {
            var mod = Load(path);
            var hist = new Dictionary<string, int>();
            foreach (var t in AllTypes(mod))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                try
                {
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Calli || ins.Operand is not CallSite cs) continue;
                        var key = cs.CallingConvention + (cs.HasThis ? "+hasthis" : "");
                        var mods = cs.ReturnType is IModifierType mt ? " modopt(" + mt.ModifierType.Name + ")" : "";
                        hist[key + mods] = hist.GetValueOrDefault(key + mods) + 1;
                    }
                }
                catch { }
            }
            Console.WriteLine(Path.GetFileName(path) + ": " + string.Join(", ", hist.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)));
        }
        break;
    }
    case "refs":
    {
        var mod = Load(args[1]);
        Console.WriteLine("assembly: " + mod.Assembly?.FullName);
        foreach (var a in mod.Assembly.CustomAttributes.Where(a => a.AttributeType.Name is "TargetFrameworkAttribute" or "InternalsVisibleToAttribute" or "AssemblyMetadataAttribute"))
            Console.WriteLine("  attr " + a.AttributeType.Name + "(" + string.Join(", ", a.ConstructorArguments.Select(Fmt)) + ")");
        foreach (var r in mod.AssemblyReferences) Console.WriteLine("  ref " + r.FullName);
        foreach (var r in mod.ModuleReferences) Console.WriteLine("  moduleref " + r.Name);
        break;
    }
    case "types":
    {
        var mod = Load(args[1]);
        var rx = args.Length > 2 ? new Regex(args[2]) : null;
        foreach (var t in AllTypes(mod))
        {
            if (rx != null && !rx.IsMatch(t.FullName)) continue;
            Console.WriteLine($"{t.FullName}  (methods={t.Methods.Count}, fields={t.Fields.Count}) {Attrs(t)}");
        }
        break;
    }
    case "members":
    {
        var mod = Load(args[1]);
        var rx = new Regex(args[2]);
        foreach (var t in AllTypes(mod))
        {
            if (!rx.IsMatch(t.FullName)) continue;
            Console.WriteLine($"## {t.FullName} : {t.BaseType?.FullName} {Attrs(t)}");
            foreach (var f in t.Fields) Console.WriteLine($"   F {(f.IsStatic ? "static " : "")}{f.FieldType.FullName} {f.Name} {Attrs(f)}");
            foreach (var m in t.Methods) Console.WriteLine($"   M {Attrs(m)}{Sig(m)}");
        }
        break;
    }
    case "il":
    {
        var mod = Load(args[1]);
        var trx = new Regex(args[2]);
        var mrx = new Regex(args[3]);
        foreach (var t in AllTypes(mod))
        {
            if (!trx.IsMatch(t.FullName)) continue;
            foreach (var m in t.Methods)
            {
                if (!mrx.IsMatch(m.Name)) continue;
                Console.WriteLine($"### {t.FullName}::{Attrs(m)}{Sig(m)}");
                if (!m.HasBody) { Console.WriteLine("    (no body)"); continue; }
                foreach (var ins in m.Body.Instructions) Console.WriteLine("    " + ins);
            }
        }
        break;
    }
    case "find":
    {
        var mod = Load(args[1]);
        var rx = new Regex(args[2]);
        foreach (var t in AllTypes(mod))
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            try
            {
                foreach (var ins in m.Body.Instructions)
                {
                    var op = ins.Operand switch { MemberReference mr => mr.FullName, string s => "\"" + s + "\"", CallSite cs => "calli " + cs.FullName, _ => null };
                    if (op != null && rx.IsMatch(op)) Console.WriteLine($"{t.FullName}::{m.Name}  ->  {ins.OpCode} {op}");
                }
            }
            catch { }
        }
        break;
    }
    case "cs":
    {
        var path = args[1];
        var pe = new PEFile(path);
        var resolver = new UniversalAssemblyResolver(path, false, pe.Reader.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false, ShowXmlDocumentation = false };
        var dec = new CSharpDecompiler(path, resolver, settings);
        foreach (var name in args.Skip(2))
        {
            try { Console.WriteLine(dec.DecompileTypeAsString(new FullTypeName(name))); }
            catch (Exception e) { Console.WriteLine($"// failed to decompile {name}: {e.GetType().Name}: {e.Message}"); }
        }
        break;
    }
    default:
        Console.Error.WriteLine("unknown command " + cmd);
        return 1;
}
return 0;

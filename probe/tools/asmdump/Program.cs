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
//   asmdump find <dll> <regex>                   find methods whose IL references a member/string matching regex

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

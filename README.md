# Unity + NativeAOT

An investigation into running Unity player scripts as .NET NativeAOT code instead of IL2CPP or JIT-compiled CoreCLR.
Everything here was measured against Unity 6000.7.0b10 on Windows x64 with ILCompiler 10.0.1. It relies on private Unity
interfaces that can change in any release, so treat it as a spike, not a product.

## Findings so far

Every `UnityPlayer.dll` variant binds its scripting runtime by name at run time. What it expects differs a lot:

| | IL2CPP player | CoreCLR player |
|---|---|---|
| Runtime binary | `GameAssembly.dll` | `CoreCLR\native\coreclr.dll` (Unity's fork of .NET 10) |
| Native functions bound by name | about 245 `il2cpp_*` | about 35 (`coreclr_initialize`, `unity_coreclr_create_delegate`, `coreclr_unity_*`, some `mono_*`) |
| Rest of the embedding API | none, all native | 175 function pointers that `Unity.Private.Scripting.dll` fills in |
| Engine calls in `UnityEngine.CoreModule` | 4,084 `InternalCall` methods | none, `calli` through per-module function-pointer tables |
| Objects crossing the boundary | raw pointers | tagged GC handles |
| GC | Boehm, objects do not move | compacting, objects move |
| Class library | `mscorlib 4.0.0.0` (Mono) | `System.Private.CoreLib 10.0` |

- **NativeAOT with libil2cpp** is not a linking problem. The two are complete runtimes with different object layouts,
  type metadata, GC and exception models. It would need a new compiler backend, or a NativeAOT library that
  impersonates all of `GameAssembly.dll` for an engine that holds raw object pointers.
- **NativeAOT behind the CoreCLR engine build** works for the test scene. A NativeAOT library named `coreclr.dll`,
  holding Unity's managed layer and the game's scripts, is accepted by the stock player with no JIT present. A scene
  that exercises serialization, component and object lifetime calls, coroutines, messages, exceptions and asset
  unloading prints the same output as on stock CoreCLR, except where it asks for a generic instantiation that only
  exists at run time. Getting there meant replacing the parts of the embedding layer that need a JIT or CoreCLR
  internals (see [Stage 3](#stage-3-how-it-works)). It is desktop only, because Unity ships CoreCLR engine builds
  only for Windows, macOS and Linux.

## Layout

| Path | What it is |
|---|---|
| `probe/EmptyPlayer` | Unity project with one scene. `Probe.cs` logs runtime facts; `Exercise.cs` runs one check per line over the scripting features a game leans on and quits. `Builder.cs` writes the scene and builds a player per scripting backend. |
| `probe/CoreClrStandIn` | Stage 1. A NativeAOT library that exports Unity's `coreclr.dll` surface as logging stubs, to record what the engine calls. |
| `probe/AotRuntime` | Stage 3. The replacement runtime pieces: the `coreclr.dll` exports and the methods that stand in for JIT- or CoreCLR-only code in Unity's assemblies. |
| `probe/AotScripting` | The NativeAOT project that compiles `AotRuntime`, Unity's assemblies and the game's assemblies into one `coreclr.dll`. It has no code of its own. |
| `probe/build-aot.ps1` | Runs the whole stage 3 pipeline and the player, then prints the interesting parts of both logs. |
| `probe/tools/asmdump` | Assembly inspection and patching helper built on Mono.Cecil and ICSharpCode.Decompiler. |
| `probe/tools/binstrings.py` | Extracts strings matching a regex from a native binary. |

Player builds (`probe/builds`), logs and copies of Unity's assemblies are not committed.

## Reproducing

Paths below assume the editor is at `D:\Programs\Unity\Hub\Editor\6000.7.0b10` with the Windows IL2CPP and CoreCLR
modules installed.

```powershell
# 1. Build the players (CoreCLR, IL2CPP) into probe\builds\<name>
$unity = "D:\Programs\Unity\Hub\Editor\6000.7.0b10\Editor\Unity.exe"
& $unity -batchmode -quit -nographics -projectPath probe\EmptyPlayer -executeMethod Builder.BuildCoreCLR -logFile probe\build-coreclr.log
& $unity -batchmode -quit -nographics -projectPath probe\EmptyPlayer -executeMethod Builder.BuildIL2CPP -logFile probe\build-il2cpp.log

# 2. Stage 1: swap in the logging stand-in for coreclr.dll (and delete the JIT)
Copy-Item probe\builds\coreclr probe\builds\coreclr-standin -Recurse
dotnet publish probe\CoreClrStandIn -c Release
Copy-Item probe\CoreClrStandIn\bin\Release\net10.0\win-x64\publish\coreclr.dll probe\builds\coreclr-standin\CoreCLR\native\ -Force
Remove-Item probe\builds\coreclr-standin\CoreCLR\native\clrjit.dll, probe\builds\coreclr-standin\CoreCLR\native\mscordac*.dll, probe\builds\coreclr-standin\CoreCLR\native\mscordbi.dll
$env:CORECLR_STANDIN_LOG = "$PWD\probe\standin.log"
probe\builds\coreclr-standin\Probe.exe -batchmode -nographics -logFile probe\player-standin.log

# 3. Stage 3: compile everything with NativeAOT, stage it as coreclr.dll in probe\builds\coreclr-aot, run the player
dotnet build probe\tools\asmdump -c Release -o probe\tools\asmdump\out
probe\build-aot.ps1            # eight rooted assemblies, about 10 s. Also -Optimize, -Symbols, -Roots "A;B;C", -NoRun, -SkipBuild
probe\build-aot.ps1 -AllRoots  # every assembly the stock player lists, about 90 s

# 4. Compare with stock CoreCLR: the "PROBE x." lines of the two logs should match
probe\builds\coreclr\Probe.exe -batchmode -nographics -logFile probe\player-coreclr.log
Compare-Object (Select-String '^PROBE x\.' probe\player-coreclr.log).Line (Select-String '^PROBE x\.' probe\player-aot.log).Line
```

## Stage 3: how it works

`build-aot.ps1` does five things:

1. `asmdump publicize-all` writes copies of Unity's assemblies with every member public, so `AotRuntime` can be
   compiled against Unity's internals as ordinary C#.
2. `AotRuntime` is compiled. Its methods carry `[Detour(assembly, type, method)]` or `[RedirectCalls(assembly, callee)]`.
3. `asmdump detour` rewrites the real Unity assemblies: a detoured method's body becomes a call to its replacement,
   and a redirected call site calls the replacement instead of the original callee.
4. ILC compiles `AotRuntime`, the patched assemblies and the untouched ones into one library. The `coreclr.dll` exports
   come from `AotRuntime` through `UnmanagedEntryPointsAssembly`.
5. The library replaces `CoreCLR\native\coreclr.dll` in a copy of the CoreCLR player with the JIT deleted.

What had to be replaced, and with what:

| Unity's CoreCLR code | Why it cannot run | Replacement |
|---|---|---|
| A class is a `MethodTable*`, and the fork adds a user-data field to `MethodTable` that the engine reads at a fixed offset | NativeAOT's `MethodTable` has no such field | A class is a pointer to a small native `ClassHandle` struct (`ClassHandles.cs`). The conversions are three methods of `Unity.Private.Scripting.Extensions` plus 14 `RuntimeTypeHandle.Value` call sites in the engine modules |
| `runtime_invoke` emits a `DynamicMethod` per target | No JIT | A reflection invoker that unpacks the engine's argument array the same way (`Invoke.cs`) |
| Assemblies are loaded from the `Managed` folder into load contexts | Nothing can be loaded | Lookup by name among the compiled-in assemblies (`Assemblies.cs`) |
| TypeDef tokens come from the loaded image | NativeAOT keeps no tokens | Read from the IL files with `System.Reflection.Metadata` and matched by name |
| Member handles are resolved through `RuntimeMethodHandleInternal` and friends | CoreCLR-private types | NativeAOT handles carry their declaring type (`Handles.cs`) |
| Field offsets come from the fork's `mono_field_get_offset` | No public API | `RuntimeFieldInfo.Offset` in NativeAOT's reflection, reached with `UnsafeAccessor` |
| `IntPtrObjectMarshalling` emits `DynamicMethod`s to reach the native pointer inside wrappers such as `AsyncOperation` | No JIT | Reflection on the same field (`Marshalling.cs`) |
| The liveness walk (`coreclr_unity_liveness_*`) that tells the engine which of its objects scripts still reach | Implemented inside the fork's runtime; NativeAOT has no heap-walk API | A walk over the GC's own layout descriptors, from the engine's roots and from NativeAOT's static holders, inside a no-GC region (`Liveness.cs`) |
| `StackFrame.GetMethodFromNativeIP`, `AssemblyLoadContext.s_ResolveAssemblyLocationOverride` | Exist only in Unity's fork | Managed frames are reported as unknown; the override is skipped |

Known gaps:

- 27 of the native exports are still logging stubs. The test scene reaches two of them (`coreclr_unity_profiler_register`,
  `coreclr_unity_set_on_fatal_error`); the rest cover shutdown, the profiler, GC mode switches, thread attach and a
  few `mono_*` lookups.
- The liveness walk does not see `[ThreadStatic]` roots, and it stops nothing: a no-GC region keeps objects from
  moving, but other managed threads keep running. Its contract was inferred from Mono's `unity_liveness.c` and from
  the arguments the engine passes.
- A generic instantiation over a value type that is first named at run time has no code (`MakeGenericType`,
  reflection-driven serializers). This is the usual ahead-of-time limit and IL2CPP has it too.
- Everything reached by name must be rooted. By default only eight assemblies are, and the engine then logs a failed
  lookup for each class it wants from the others; `-AllRoots` compiles in all of them.
- Token lookups for methods and fields throw, and stack frames in managed code are not symbolised for the engine.
- `runtime_invoke` and the wrapper accessors go through reflection, which is slow. A build pipeline would generate
  them, as IL2CPP does.
- Not exercised: rendering (the player ran with `-batchmode -nographics`), packages, Burst, the job system from
  scripts, domain reload, and anything beyond the test scene.

When the player dies without a message, the fail-fast text and managed stack are in the Windows Application event
log under the `.NET Runtime` source.

## Results recorded on 2026-11-25

- CoreCLR player: `.NET 10.0.10-dev`, JIT on, three GC generations, an object's address changed across a compacting GC.
- IL2CPP player: `mscorlib 4.0.0.0`, no dynamic code, one generation, the object did not move.
- Stage 1: with the JIT deleted, the stock player called `coreclr_initialize`, asked for
  `Unity.Private.Scripting.Initialization.InitializationBootstrapper`, and handed over all 21 interop tables with the
  expected sizes, then crashed on the unfilled tables.
- Stage 2: ILC compiled every engine module plus `Assembly-CSharp`, fully rooted, into one 105.6 MB library in about
  25 minutes with 0 errors and 250 distinct trim/AOT warnings. At run time Unity's own bootstrapper initialised 11 of the
  21 tables, then `ImageInterop`'s static constructor threw because it reflects on the CoreCLR-private type
  `System.RuntimeFieldHandleInternal`.
- Stage 3: with 21 methods detoured and 14 call sites redirected in three Unity assemblies, the stock CoreCLR player
  (`-batchmode -nographics`) loaded the scene, ran `Probe.Start()` and exited with code 0. The script reported
  `.NET 10.0.1`, no dynamic code, an empty `Assembly.Location` for the core library and an object that moved across
  a compacting GC. No managed exception was thrown during the run. Eight assemblies were rooted (30 compiled in),
  unoptimised and without symbols: ILC and the linker took about 10 seconds and produced a 22.9 MB `coreclr.dll`.
  The engine logged 122 lookup failures for classes of modules that were not compiled in.
- Stage 3 with `-AllRoots`: all 92 assemblies the stock player lists are rooted (129 compiled in). ILC and the linker
  took about 90 seconds unoptimised and produced a 217 MB `coreclr.dll`. No lookup failed.
- The `Exercise` scene on stock CoreCLR and on the NativeAOT runtime (`-AllRoots`, four runs): 30 `PROBE x.` lines
  each, exit code 0 both, and one line differs. `makegeneric.computed` prints `Queue`1` on CoreCLR and fails with
  `NotSupportedException` on NativeAOT. The lines that match cover fields written by the scene loader, an object
  reference, `GetComponent`, `AddComponent`, `Instantiate`, `Destroy`, `SendMessage`, `Invoke`, two coroutines,
  `JsonUtility`, reflection, a second thread, an exception escaping `Update`, and `Resources.UnloadUnusedAssets`
  keeping the textures a field and a static hold while destroying an unreferenced one.
- Before `Liveness.cs` the liveness exports were stubs, and `UnloadUnusedAssets` destroyed all three textures.
- One liveness pass in that scene: 5 roots, 70,816 objects visited (70,793 of them from statics), 10 reported, and
  the no-GC region held.
- Optimised, with the default eight roots: 18.4 MB, about 15 seconds, same output.

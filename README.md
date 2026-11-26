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
- **NativeAOT behind the CoreCLR engine build** works for the test scene and for a URP demo, where it computes the
  same results as CoreCLR and IL2CPP at close to CoreCLR's speed (see [Demo and benchmark](#demo-and-benchmark)).
  A NativeAOT library named `coreclr.dll`,
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
| `demo/UrpDemo` | URP project with six workloads to look at and to measure (see [Demo and benchmark](#demo-and-benchmark)). `DemoBuilder.cs` writes the scene, the pipeline assets and the materials, and builds a player per scripting backend. |
| `demo/build-players.ps1` | Builds the demo's Mono, IL2CPP and CoreCLR players, and with `-Aot` the NativeAOT one. |
| `demo/run-benchmark.ps1` | Runs the benchmark in every player and prints the comparison. |

Player builds (`probe/builds`, `demo/builds`), logs, benchmark results and copies of Unity's assemblies are not
committed.

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
| `runtime_invoke` emits a `DynamicMethod` per target | No JIT | An open delegate for instance methods without parameters or a result (`Update` and the like); for the rest a reflection invoker that unpacks the engine's argument array the same way (`Invoke.cs`) |
| Assemblies are loaded from the `Managed` folder into load contexts | Nothing can be loaded | Lookup by name among the compiled-in assemblies (`Assemblies.cs`) |
| TypeDef tokens come from the loaded image | NativeAOT keeps no tokens | Read from the IL files with `System.Reflection.Metadata` and matched by name, one type at a time as the engine asks |
| `MemberInfo.MetadataToken`, which URP sorts a volume component's parameters by | NativeAOT keeps no tokens and the property throws | A number that orders members the way reflection lists them: depth of the declaring type, then position among its members (`Reflection.cs`). The call sites are redirected in every Unity assembly |
| `Marshal.SizeOf(Type)` and `Marshal.OffsetOf(Type, string)` on a type named only at run time, as in `Graphics.RenderMeshInstanced<T>` | ILC generates marshalling data only for structs it sees being marshalled | Fall back to the managed size and field offset, which are the same thing for the blittable structs the engine accepts (`Reflection.cs`) |
| `Delegate.Method` returning the same `MethodInfo` object every time. The render graph hashes a pass by the identity of its render function's `MethodInfo` | NativeAOT builds a new, equal `MethodInfo` on every call | One `MethodInfo` per method, remembered per delegate (`Reflection.cs`). Without it the graph's hash changed every frame and the graph was compiled again each frame |
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
- `runtime_invoke` of anything with parameters or a result, and the wrapper accessors, go through reflection, which
  boxes: the engine's own per-frame callbacks (input, physics events, the render loop) allocate a few kilobytes a
  frame this way, where a stock CoreCLR player allocates nothing. A build pipeline would generate these calls, as
  IL2CPP does.
- Reflection objects other than types are not unique in NativeAOT. `Delegate.Method` is patched because URP depends
  on it; code that compares `MethodInfo`, `FieldInfo` or `PropertyInfo` objects by reference, or keys a cache by
  their identity, will misbehave the same way without failing.
- Not exercised: Burst, the job system from scripts, domain reload, input, audio, physics, UI Toolkit, and anything
  else the test scene and the demo do not touch.

When the player dies without a message, the fail-fast text and managed stack are in the Windows Application event
log under the `.NET Runtime` source.

The runtime writes its log to the file named by `CORECLR_STANDIN_LOG`, each line stamped with the milliseconds since
the engine's first call. Every managed exception is listed there, including the ones Unity catches. Three switches
help when a number looks wrong:

- `AOT_ALLOC_TRACE=1` lists, every few seconds, the engine-to-script calls under which the most bytes were allocated.
  It is how the render graph recompiling every frame was found.
- `AOT_SELFTEST=1` times the pieces of an engine-to-script call on a method that does nothing.
- `Probe.exe -callbench` (the probe project, any backend) times calls from a script into the engine.

`build-aot.ps1` works on any CoreCLR player: `-Builds` names the folder that holds the `coreclr` build and receives
`coreclr-aot`, `-Exe` the player's name, `-PlayerArgs` what to run it with.

## Demo and benchmark

`demo/UrpDemo` is a URP 17.7 project that plays six workloads, each with something to look at. It has two jobs: to
put the NativeAOT runtime under a real render pipeline for longer than a test scene does, and to compare the scripting
backends on identical scripts.

| Section | On screen | What it leans on |
|---|---|---|
| `boids` | 10,000 boids flocking inside a sphere and scattering from a predator | Struct math and array loops in managed code, no engine calls in the step |
| `tree` | A fractal of 97,656 cubes, every level turning inside its parent | Quaternion and matrix math, two engine calls per part |
| `julia` | An animated Julia set, 768x432, computed into a texture | One tight loop over doubles on one thread |
| `julia-mt` | The same picture with `Parallel.For` over the rows | The thread pool and delegates |
| `swarm` | 8,000 GameObjects, each with an `Update` that moves its own transform | Engine-to-script and script-to-engine calls |
| `fireworks` | Shells, sparks and trails, one object per particle | Allocation, garbage collection, virtual calls, LINQ |

Boids, tree and fireworks draw with `Graphics.RenderMeshInstanced` and a colour per instance, the Julia sets upload
a texture every frame, and the swarm uses ordinary renderers. The camera renders in HDR through bloom, tonemapping
and a vignette. The shaders are hand-written HLSL, and `DemoBuilder.cs` creates every asset, so the project needs
nothing from the registry: URP and its dependencies ship inside the editor.

Every section advances by 1/60 s per frame from a fixed seed and takes its sines and cosines from a polynomial
rather than the C runtime, so a run ends in the same state at any frame rate. That state is hashed. Two players that
print the same checksum computed the same thing, which makes the benchmark a correctness test as well.

```powershell
demo\build-players.ps1 -Aot    # Mono, IL2CPP and CoreCLR players, about a minute each, then the NativeAOT one, about 15 minutes
demo\run-benchmark.ps1         # three interleaved runs per player; prints the tables and saves demo\results\summary.md
demo\run-benchmark.ps1 -Backends coreclr,coreclr-aot -Sections swarm -Scale 4 -Runs 1
demo\builds\coreclr-aot\UrpDemo.exe    # the slideshow: 1-6 pick a section, space holds it, U lifts the 60 fps limit, Esc quits
```

`UrpDemo.exe -benchmark` plays each section for `-frames` frames after `-warmup`, logs one `DEMO result` line per
section and exits with 0, or with 2 if a section threw. The other options are listed at the top of `Demo.cs`.
The runner launches every player once before measuring, because the first launch of a newly built player is slow
for reasons outside it (see below). Do not run ILC while the editor is building a player: ILC reads the CoreCLR
player's assemblies, and the editor's build then fails on a locked file.

### Benchmark recorded on 2026-11-25

Ryzen 9 5950X (32 logical processors), RTX 3090, Direct3D 11, Windows 10, a 1280x720 window with vSync off. 600
measured frames per section after 120 warm-up frames, the median of three interleaved runs. NativeAOT is the
optimised runtime with all 111 assemblies of the player compiled in: a 134.7 MB `coreclr.dll`, 22 methods detoured
and 73 call sites redirected in 12 Unity assemblies. All twelve runs completed all six sections and exited with
code 0.

Script time per frame in milliseconds, from the start of the section's step to `LateUpdate`, so every `Update` the
engine calls in between is included. In brackets, the ratio to CoreCLR.

| Section | Mono | IL2CPP | CoreCLR | NativeAOT |
|---|---:|---:|---:|---:|
| boids | 24.30 (4.07x) | 6.87 (1.15x) | 5.97 | 6.15 (1.03x) |
| tree | 21.61 (2.79x) | 9.13 (1.18x) | 7.75 | 8.48 (1.09x) |
| julia | 16.82 (2.86x) | 5.97 (1.02x) | 5.88 | 6.01 (1.02x) |
| julia-mt | 1.38 (2.45x) | 0.72 (1.27x) | 0.56 | 0.58 (1.03x) |
| swarm | 3.12 (1.22x) | 2.11 (0.82x) | 2.56 | 3.35 (1.31x) |
| fireworks | 9.60 (3.31x) | 5.79 (2.00x) | 2.90 | 3.33 (1.15x) |

Whole frame with rendering, median, in milliseconds:

| Section | Mono | IL2CPP | CoreCLR | NativeAOT |
|---|---:|---:|---:|---:|
| boids | 26.35 | 7.74 | 6.32 | 6.76 |
| tree | 21.88 | 9.56 | 8.04 | 8.72 |
| julia | 14.39 | 5.93 | 6.18 | 6.04 |
| julia-mt | 1.77 | 1.05 | 0.98 | 0.98 |
| swarm | 5.49 | 4.61 | 4.99 | 5.83 |
| fireworks | 10.48 | 6.73 | 3.04 | 3.68 |

Cold code, garbage collection and start-up:

| | Mono | IL2CPP | CoreCLR | NativeAOT |
|---|---:|---:|---:|---:|
| First step of boids, ms | 10.89 | 3.62 | 7.31 | 3.14 |
| First step of julia, ms | 5.90 | 1.64 | 6.91 | 1.23 |
| First step of fireworks, ms | 7.84 | 0.78 | 8.81 | 0.20 |
| Collections during fireworks | 518 | 514 | 52 | 48 |
| Fireworks frame, 99th percentile, ms | 13.6 | 9.6 | 16.0 | 16.5 |
| Fireworks frame, worst, ms | 17.4 | 11.3 | 24.6 | 25.6 |
| MB allocated by the main thread in fireworks | not counted | not counted | 841.0 | 842.0 |
| MB allocated by the main thread in boids, tree, julia, swarm | not counted | not counted | 0.0 | 1.1 |
| Launch to first frame, s | 0.64 | 0.43 | 0.83 | 0.71 |

What it shows:

- **Same results.** IL2CPP, CoreCLR and NativeAOT ended all six sections in the same state in every run. A soak run
  of 5,000 frames per section on CoreCLR and NativeAOT (30,000 frames each) also ended in the same state, with a flat
  heap and exit code 0. Mono matches in the two sections that compute in `double` and ends elsewhere in the four that
  compute in `float`, which points at how Mono evaluates single-precision arithmetic rather than at the demo.
- **Numeric code.** NativeAOT is within 2 to 9 percent of CoreCLR and level with or ahead of IL2CPP. Mono is 2.5 to
  4 times slower.
- **Allocation.** CoreCLR runs fireworks twice as fast as IL2CPP and three times as fast as Mono, with a tenth of
  the collections (its collector is generational) but longer worst frames. NativeAOT uses the same collector. Its
  15 percent over CoreCLR is about what the JIT's dynamic profile-guided optimisation is worth here: stock CoreCLR
  started with `DOTNET_TieredPGO=0` measured 3.1 to 3.25 ms in that section against 2.67 with it.
- **Calls across the engine boundary.** IL2CPP wins swarm, and NativeAOT is 31 percent behind CoreCLR. That is not
  the price of a call: an engine-to-script call costs 22 to 28 ns in this runtime (`AOT_SELFTEST=1`), and
  script-to-engine calls cost the same as on CoreCLR (table below). It depends on how many agents there are. Run on
  its own with 1,000, 8,000 and 32,000 agents, the section takes CoreCLR 0.42, 2.5 and 10.4 ms, NativeAOT 0.35,
  3.1 to 3.9 and 12.9 ms, and IL2CPP 0.24, 2.2 and 9.1 ms. NativeAOT is ahead of CoreCLR while the swarm is small
  and behind once it is large, which looks like memory rather than code, and is not explained yet. Turning PGO off
  in CoreCLR does not reproduce it.
- **Cold code.** The first step of a section costs the JIT backends several milliseconds that the ahead-of-time ones
  do not pay.

Nanoseconds per call from a script into the engine (`Probe.exe -callbench`, one run, the NativeAOT runtime optimised
with the default roots):

| Call | CoreCLR | NativeAOT |
|---|---:|---:|
| `Time.frameCount` | 5.5 | 4.5 |
| `Quaternion.Euler` | 19.4 | 20.4 |
| `Quaternion.LookRotation` | 27.4 | 27.5 |
| `Matrix4x4.TRS` | 19.3 | 13.3 |
| `transform.localPosition` get | 6.1 | 7.5 |
| `transform.localPosition` set | 16.2 | 14.1 |
| `transform.localRotation` set | 15.4 | 11.2 |
| `transform.localScale` set | 21.6 | 18.9 |

### What the demo found in the NativeAOT runtime

The probe scene passed long before URP would start. In the order they turned up:

1. `MemberInfo.MetadataToken` throws on NativeAOT. URP sorts a volume component's parameters by it, so the volume
   stack failed to build and pipeline creation failed again on every frame. Shimmed.
2. `Marshal.SizeOf(Type)` has no data for a struct ILC never saw being marshalled, so `Graphics.RenderMeshInstanced`
   threw on its instance type. Shimmed.
3. `Delegate.Method` returns a new `MethodInfo` object on every call. Nothing failed: the render graph, which hashes
   a pass by the identity of that object, simply saw a new graph every frame and compiled it again, allocating 20 KB
   a frame. Read from URP's code and not measured: `DelegateHashCodeUtils` also adds a dictionary entry for every
   method hash it has not seen, so that dictionary would have grown for as long as the player ran. Shimmed; the
   main thread now allocates about 1.8 KB a frame, spread over the engine's per-frame callbacks.
4. NativeAOT links a LINQ built for size unless told otherwise. With `UseSizeOptimizedLinq=false` the fireworks
   section went from 1.37 to 1.15 times CoreCLR's time.
5. The first launch of a newly built runtime spent 3 to 5 seconds before the engine made its first call into it.
   Later launches of the same file take 30 ms. That looks like a virus scan of a new 134 MB library, which was not
   verified.

Seven managed exceptions are thrown and caught while the first instanced batch is set up, four inside the
`Marshal` shim and three `ArgumentException`s that Unity expects and catches. Nothing else is thrown in a run. The
slideshow with its IMGUI overlay runs on NativeAOT as it does on the others; its keys were not pressed in that test.

The demo does not touch physics, audio, animation, UI Toolkit, the input system package, Burst or jobs, and it has
one scene. Mono and IL2CPP have no per-thread allocation counter, and their collection counts are whole-heap
collections, so the garbage collection rows compare unlike things across those two pairs of backends.

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
- Optimised with `-AllRoots`: 119.8 MB, about 16 minutes in ILC and the linker, same output.
- That optimised build with graphics on (`Probe.exe -screen-fullscreen 0 -screen-width 480 -screen-height 270`,
  Direct3D 12): same output, exit code 0. The scene holds only the default camera and light, so this covers device
  creation and the frame loop, not rendering features.

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
- **NativeAOT behind the CoreCLR engine build** is plausible. A NativeAOT library named `coreclr.dll` is accepted by the
  stock player, and ILC compiles every Unity engine assembly with no errors. What remains is replacing the parts of
  the embedding layer that need a JIT or CoreCLR internals. It is desktop only, because Unity ships CoreCLR engine
  builds only for Windows, macOS and Linux.

## Layout

| Path | What it is |
|---|---|
| `probe/EmptyPlayer` | Unity project with one scene and one `MonoBehaviour` (`Probe.cs`) that logs runtime facts and quits. `Builder.cs` builds it per scripting backend. |
| `probe/CoreClrStandIn` | Stage 1. A NativeAOT library that exports Unity's `coreclr.dll` surface as logging stubs, to record what the engine calls. |
| `probe/AotScripting` | Stage 2. The same exports, with Unity's managed layer and the game's assemblies compiled in by NativeAOT. |
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
```

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

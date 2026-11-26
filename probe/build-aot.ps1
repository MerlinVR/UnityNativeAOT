# Builds the NativeAOT replacement for coreclr.dll, puts it in a copy of the CoreCLR player, and runs that player.
#
#   .\build-aot.ps1                 build, stage, run
#   .\build-aot.ps1 -SkipBuild      run the staged player again
#   .\build-aot.ps1 -NoRun          build and stage only
#   .\build-aot.ps1 -AllRoots       compile in every assembly the stock player lists (slower, much larger)
#
# Needs probe\builds\coreclr (Builder.BuildCoreCLR) and probe\tools\asmdump\out\asmdump.exe.
param(
    [switch]$SkipBuild,
    [switch]$NoRun,
    [switch]$Optimize,
    [switch]$Symbols,
    [string]$Roots = "",
    [switch]$AllRoots,
    [int]$TimeoutSec = 60,
    [int]$Tail = 30
)

$probe = $PSScriptRoot
$managed = "$probe\builds\coreclr\Managed"
$tool = "$probe\tools\asmdump\out\asmdump.exe"
$publicized = "$probe\builds\publicized"
$patched = "$probe\builds\patched"
$player = "$probe\builds\coreclr-aot"
$defaultRoots = "Unity.Private.Scripting;Unity.Scripting;UnityEngine.ScriptingModule;UnityEngine.CoreModule;UnityEngine.ManagedKernelModule;UnityEngine.AnimationModule;UnityEngine;Assembly-CSharp"
if ($AllRoots) {
    $stock = (Get-Content "$probe\builds\coreclr\Probe_Data\ScriptingAssemblies.json" -Raw | ConvertFrom-Json).names
    $Roots = (@("Unity.Private.Scripting") + ($stock | ForEach-Object { $_ -replace '\.dll$', '' })) -join ';'
}
elseif ($Roots -eq "") { $Roots = $defaultRoots }

function Step($name) { Write-Output "== $name ($((Get-Date).ToString('HH:mm:ss')))" }

if (-not $SkipBuild) {
    Step "publicize reference copies"
    $inputs = @(Get-ChildItem "$managed\Unity*.dll", "$managed\Assembly-CSharp*.dll" | ForEach-Object { $_.FullName })
    & $tool publicize-all $publicized @inputs | Measure-Object | ForEach-Object { "  $($_.Count) assemblies rewritten" }
    if ($LASTEXITCODE -ne 0) { Write-Output "publicize failed"; exit 1 }

    Step "compile AotRuntime"
    $out = dotnet build "$probe\AotRuntime\AotRuntime.csproj" -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $out | Select-String -Pattern "error CS" | ForEach-Object { [regex]::Replace($_.Line, '^.*[\\/]|\s*\[[^\]]*proj\]\s*$', '') } | Sort-Object -Unique |
            Select-Object -First 25 | ForEach-Object { if ($_.Length -gt 240) { $_.Substring(0, 240) } else { $_ } }
        exit 1
    }

    Step "detour Unity's assemblies"
    & $tool detour "$probe\AotRuntime\bin\Release\net10.0\AotRuntime.dll" $managed $patched
    if ($LASTEXITCODE -ne 0) { exit 1 }

    Step "ILC compile and link"
    "<Project><PropertyGroup><AotRoots>$Roots</AotRoots></PropertyGroup></Project>" | Set-Content "$probe\builds\aot-roots.props" -Encoding ascii
    $props = @()
    if ($Optimize) { $props += "-p:AotOptimize=true" }
    if ($Symbols) { $props += "-p:AotSymbols=true" }
    dotnet publish "$probe\AotScripting\AotScripting.csproj" -c Release -nologo -v m @props > "$probe\aot-publish.log"
    $ok = $LASTEXITCODE -eq 0
    $log = Get-Content "$probe\aot-publish.log"
    $log | Select-String -Pattern "\berror\b|will always throw" | ForEach-Object { ($_.Line -replace '\s*\[[A-Z]:\\.*?\]\s*$', '').Trim() } | Sort-Object -Unique |
        Select-Object -First 25 | ForEach-Object { if ($_.Length -gt 300) { $_.Substring(0, 300) } else { $_ } }
    if (-not $ok) { Write-Output "publish failed, see aot-publish.log"; exit 1 }

    Step "stage player"
    if (-not (Test-Path $player)) {
        Copy-Item "$probe\builds\coreclr" $player -Recurse
        Remove-Item "$player\CoreCLR\native\clrjit.dll", "$player\CoreCLR\native\mscordaccore.dll", "$player\CoreCLR\native\mscordbi.dll" -ErrorAction SilentlyContinue
    }
    else {
        # Pick up a rebuilt Unity player: its scene data and IL assemblies, not the runtime that is replaced here.
        foreach ($dir in "Probe_Data", "Managed") {
            robocopy "$probe\builds\coreclr\$dir" "$player\$dir" /MIR /NJH /NJS /NDL /NFL /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { Write-Output "copying $dir into the staged player failed"; exit 1 }
        }
        $global:LASTEXITCODE = 0   # robocopy reports "files copied" as 1
    }
    $native = Get-Item "$probe\AotScripting\bin\Release\net10.0\win-x64\publish\AotScripting.dll"
    Copy-Item $native.FullName "$player\CoreCLR\native\coreclr.dll" -Force
    "  coreclr.dll = {0:N1} MB" -f ($native.Length / 1MB)

    # The engine loads what ScriptingAssemblies.json lists. Keep the entries that were compiled in full.
    $json = Get-Content "$probe\builds\coreclr\Probe_Data\ScriptingAssemblies.json" -Raw | ConvertFrom-Json
    $rooted = $Roots -split ';'
    $names = @(); $types = @()
    for ($i = 0; $i -lt $json.names.Count; $i++) {
        if ($rooted -contains ($json.names[$i] -replace '\.dll$', '')) { $names += $json.names[$i]; $types += $json.types[$i] }
    }
    [pscustomobject]@{ names = $names; types = $types } | ConvertTo-Json -Compress | Set-Content "$player\Probe_Data\ScriptingAssemblies.json" -Encoding ascii
    "  ScriptingAssemblies.json: $($names -join ', ')"
}

if ($NoRun) { exit 0 }

Step "run player"
$standinLog = "$probe\aot-standin.log"
$playerLog = "$probe\player-aot.log"
Remove-Item $standinLog, $playerLog -ErrorAction SilentlyContinue
$env:CORECLR_STANDIN_LOG = $standinLog
$p = Start-Process -FilePath "$player\Probe.exe" -ArgumentList @("-batchmode", "-nographics", "-logFile", "`"$playerLog`"") -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); "player timed out after $TimeoutSec s and was killed" }
else { "player exit code: 0x{0:x}" -f $p.ExitCode }
# Only this player's crash handler: an open editor has one of its own.
Get-Process UnityCrashHandler64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$player\*" } | Stop-Process -Force -Confirm:$false

function Short($line) { if ($line.Length -gt 260) { $line.Substring(0, 260) } else { $line } }

"-- runtime log, last $Tail lines"
if (Test-Path $standinLog) { Get-Content $standinLog | Select-Object -Last $Tail | ForEach-Object { Short $_ } }
"-- player log: PROBE lines, exceptions and the native crash stack"
if (Test-Path $playerLog) {
    $lines = Get-Content $playerLog
    # One line per class or method the engine wanted from an assembly that was not compiled in.
    $lookups = @($lines | Where-Object { $_ -match '^Unable to find (type|method) ' })
    if ($lookups.Count -gt 0) {
        $modules = $lookups | ForEach-Object { if ($_ -match '\[([^\]]+)\.dll\]') { $Matches[1] } } | Sort-Object -Unique
        "$($lookups.Count) failed lookups in: $($modules -join ', ')"
    }
    $lines | Where-Object { $_ -match '^PROBE|Exception|^\s+at |NotSupported|Unable to (?!find (type|method) )|Could not|Failed|Fatal|Assert|Crash!!!|^0x[0-9A-F]{16} \((?!ntdll|KERNEL|USER32|ucrtbase)' } |
        Select-Object -First 45 | ForEach-Object { Short $_ }
}

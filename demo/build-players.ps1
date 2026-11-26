# Builds the demo's players with the Unity editor in batch mode, one per scripting backend, into demo\builds.
#
#   .\build-players.ps1                       Mono, IL2CPP and CoreCLR
#   .\build-players.ps1 -Backends coreclr     one of them
#   .\build-players.ps1 -Aot                  then the NativeAOT player from the CoreCLR one (optimised, every assembly)
#
# Each editor run logs to demo\build-<backend>.log.
param(
    [string[]]$Backends = @("coreclr", "mono", "il2cpp"),
    [string]$Unity = "D:\Programs\Unity\Hub\Editor\6000.7.0b10\Editor\Unity.exe",
    [switch]$Aot
)

$demo = $PSScriptRoot
$methods = @{ coreclr = "DemoBuilder.BuildCoreCLR"; mono = "DemoBuilder.BuildMono"; il2cpp = "DemoBuilder.BuildIL2CPP" }
$failed = $false

foreach ($backend in $Backends) {
    if (-not $methods.ContainsKey($backend)) { Write-Output "unknown backend '$backend'"; $failed = $true; continue }
    $log = "$demo\build-$backend.log"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Unity -PassThru -Wait -ArgumentList @(
        "-batchmode", "-quit", "-nographics", "-projectPath", "`"$demo\UrpDemo`"", "-executeMethod", $methods[$backend], "-logFile", "`"$log`"")
    $line = Select-String -Path $log -Pattern "^DEMO BUILD" | Select-Object -Last 1
    $summary = "no result line, see $log"
    if ($line) { $summary = $line.Line }
    Write-Output ("{0,-8} exit={1} {2,4:F0}s  {3}" -f $backend, $process.ExitCode, $watch.Elapsed.TotalSeconds, $summary)
    if ($process.ExitCode -ne 0) {
        $failed = $true
        Select-String -Path $log -Pattern "error CS|Shader error|: error |Exception:" | Select-Object -First 15 | ForEach-Object { "    " + $_.Line.Trim() }
    }
}

if ($Aot -and -not $failed) {
    & "$demo\..\probe\build-aot.ps1" -Builds "$demo\builds" -Exe UrpDemo -AllRoots -Optimize -NoRun
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

if ($failed) { exit 1 }

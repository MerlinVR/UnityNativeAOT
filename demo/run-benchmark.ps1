# Runs the demo's benchmark in every player build found under demo\builds and compares the scripting backends.
#
#   .\run-benchmark.ps1                          every backend, three runs each
#   .\run-benchmark.ps1 -Backends coreclr,coreclr-aot -Runs 1
#   .\run-benchmark.ps1 -Sections boids,julia -Frames 300
#   .\run-benchmark.ps1 -Screenshots             also save one picture per section and backend
#   .\run-benchmark.ps1 -Batch                   no window and no rendering: script time only
#
# Each run writes results\<backend>-<n>.json and .log. The summary is printed and saved as results\summary.md.
# Runs are interleaved (one of each backend, then the next round) so that drift in the machine's state is shared.
# Each player is launched once, unmeasured, before the runs (-NoPrime skips that).
param(
    [string[]]$Backends = @("mono", "il2cpp", "coreclr", "coreclr-aot"),
    [int]$Runs = 3,
    [int]$Frames = 600,
    [int]$Warmup = 120,
    [double]$Scale = 1,
    [string]$Sections = "",
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$TimeoutSec = 300,
    [switch]$Screenshots,
    [switch]$Batch,
    [switch]$NoPrime,
    [string]$Baseline = "coreclr"
)

$demo = $PSScriptRoot
$results = "$demo\results"
New-Item -ItemType Directory -Force $results | Out-Null
$invariant = [Globalization.CultureInfo]::InvariantCulture

$present = @($Backends | Where-Object { Test-Path "$demo\builds\$_\UrpDemo.exe" })
$missing = @($Backends | Where-Object { $present -notcontains $_ })
if ($missing.Count -gt 0) { Write-Output "no build for: $($missing -join ', ')" }
if ($present.Count -eq 0) { Write-Output "nothing to run; build the players first (see README)"; exit 1 }

function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function Num($value, $format) {
    if ($null -eq $value) { return "-" }
    return ([double]$value).ToString($format, $invariant)
}

# ---- prime ----

# The first launch of a newly built player is slow for reasons that have nothing to do with it: the file cache is cold
# and antivirus scans the new binaries (seconds, for a NativeAOT library of 100 MB and more). One short unmeasured
# launch per player takes that out of the measured runs.
if (-not $NoPrime) {
    foreach ($backend in $present) {
        $process = Start-Process -FilePath "$demo\builds\$backend\UrpDemo.exe" -PassThru -ArgumentList @(
            "-batchmode", "-nographics", "-benchmark", "-frames", "1", "-warmup", "1", "-sections", "julia", "-logFile", "`"$results\prime-$backend.log`"")
        if (-not $process.WaitForExit($TimeoutSec * 1000)) { $process.Kill() }
    }
}

# ---- run ----

$records = @()
for ($n = 1; $n -le $Runs; $n++) {
    foreach ($backend in $present) {
        $json = "$results\$backend-$n.json"
        $log = "$results\$backend-$n.log"
        Remove-Item $json, $log -ErrorAction SilentlyContinue

        $arguments = @("-benchmark", "-results", "`"$json`"", "-logFile", "`"$log`"", "-label", $backend,
            "-frames", $Frames, "-warmup", $Warmup, "-scale", $Scale.ToString($invariant))
        if ($Sections -ne "") { $arguments += @("-sections", $Sections) }
        if ($Batch) { $arguments += @("-batchmode", "-nographics") }
        else { $arguments += @("-screen-fullscreen", "0", "-screen-width", $Width, "-screen-height", $Height) }
        if ($Screenshots -and $n -eq 1 -and -not $Batch) { $arguments += @("-screenshots", "`"$results\screenshots\$backend`"") }

        # Read only by the NativeAOT stand-in for coreclr.dll.
        $env:CORECLR_STANDIN_LOG = "$results\$backend-$n.runtime.log"
        Remove-Item $env:CORECLR_STANDIN_LOG -ErrorAction SilentlyContinue

        $arguments += @("-launched", [DateTime]::UtcNow.ToFileTimeUtc())
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath "$demo\builds\$backend\UrpDemo.exe" -ArgumentList $arguments -PassThru
        $exit = "timeout"
        if ($process.WaitForExit($TimeoutSec * 1000)) { $exit = "0x{0:x}" -f $process.ExitCode } else { $process.Kill() }
        $wall = $watch.Elapsed.TotalSeconds
        Get-Process UnityCrashHandler64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$demo\builds\$backend\*" } | Stop-Process -Force -Confirm:$false

        $data = $null
        if (Test-Path $json) { $data = Get-Content $json -Raw | ConvertFrom-Json }
        $sectionCount = 0
        if ($data) { $sectionCount = @($data.sections | Where-Object { $_.ok }).Count }
        Write-Output ("run {0} {1,-12} exit={2,-8} wall={3,6:F1}s sections ok={4}" -f $n, $backend, $exit, $wall, $sectionCount)
        $records += [pscustomobject]@{ Backend = $backend; Run = $n; Exit = $exit; Wall = $wall; Data = $data }
    }
}

# ---- summarise ----

$names = @()
foreach ($run in $records) { if ($run.Data) { foreach ($s in $run.Data.sections) { if ($names -notcontains $s.name) { $names += $s.name } } } }

# The value of one field of one section for one backend: the median over its runs.
function Cell($backend, $section, $field) {
    $values = @()
    foreach ($run in $records) {
        if ($run.Backend -ne $backend -or -not $run.Data) { continue }
        $s = $run.Data.sections | Where-Object { $_.name -eq $section -and $_.ok } | Select-Object -First 1
        if ($s) { $values += [double]$s.$field }
    }
    return (Median $values)
}

function Table($title, $field, $format, [switch]$Ratio, [string[]]$Blank = @()) {
    $lines = @("", "### $title", "")
    $lines += "| section | " + ($present -join " | ") + " |"
    $lines += "|---|" + (($present | ForEach-Object { "---:" }) -join "|") + "|"
    foreach ($name in $names) {
        $base = $null
        if ($Ratio -and $present -contains $Baseline) { $base = Cell $Baseline $name $field }
        $cells = foreach ($backend in $present) {
            if ($Blank -contains $backend) { "-"; continue }
            $value = Cell $backend $name $field
            $text = Num $value $format
            if ($null -ne $value -and $null -ne $base -and $base -gt 0 -and $backend -ne $Baseline) { $text += " (" + (Num ($value / $base) "F2") + "x)" }
            $text
        }
        $lines += "| $name | " + ($cells -join " | ") + " |"
    }
    return $lines
}

$first = $records | Where-Object { $_.Data } | Select-Object -First 1
$summary = @("## Benchmark summary", "")
if ($first) {
    $mode = "window ${Width}x${Height}"
    if ($Batch) { $mode = "batch mode, no rendering" }
    $summary += "Unity $($first.Data.unity), $($first.Data.processors) logical processors, $mode, $Frames measured frames after $Warmup warm-up frames, scale $Scale, median of $Runs runs. Times in milliseconds; in brackets, the ratio to $Baseline."
}

$summary += Table "Script time per frame, average (start of the step to LateUpdate)" "scriptAvg" "F3" -Ratio
$summary += Table "Simulation step alone, average" "stepAvg" "F3" -Ratio
$summary += Table "Whole frame, median" "frameMedian" "F3" -Ratio
$summary += Table "Whole frame, 99th percentile" "frameP99" "F3" -Ratio
$summary += Table "Whole frame, worst" "frameMax" "F2"
$summary += Table "First step of the section (cold code)" "stepFirst" "F2"
$summary += Table "Garbage collections during the measured frames (youngest generation where there are generations)" "gen0" "F0"

# Mono and IL2CPP have no per-thread allocation counter and report zero.
$uncounted = @($present | Where-Object {
    $backend = $_
    @($records | Where-Object { $_.Backend -eq $backend -and $_.Data -and @("Mono", "IL2CPP") -contains $_.Data.backend }).Count -gt 0
})
$summary += Table "Megabytes allocated by the main thread during the measured frames (not counted by Mono and IL2CPP)" "allocatedMb" "F1" -Blank $uncounted

# Checksums: the same on every run of a backend, or the backend is not deterministic.
$summary += @("", "### State after the last frame", "", "A letter per distinct checksum: backends with the same letter ended in exactly the same state.", "")
$summary += "| section | " + ($present -join " | ") + " |"
$summary += "|---|" + (($present | ForEach-Object { ":---:" }) -join "|") + "|"
foreach ($name in $names) {
    $seen = @()
    $cells = foreach ($backend in $present) {
        $sums = @()
        foreach ($run in $records) {
            if ($run.Backend -ne $backend -or -not $run.Data) { continue }
            $s = $run.Data.sections | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if ($s -and $s.ok) { $sums += $s.checksum } elseif ($s) { $sums += "failed" }
        }
        $distinct = @($sums | Sort-Object -Unique)
        if ($distinct.Count -eq 0) { "-" }
        elseif ($distinct -contains "failed") { "failed" }
        elseif ($distinct.Count -gt 1) { "varies" }
        else {
            if ($seen -notcontains $distinct[0]) { $seen += $distinct[0] }
            [string][char](65 + [Array]::IndexOf($seen, $distinct[0]))
        }
    }
    $summary += "| $name | " + ($cells -join " | ") + " |"
}

$summary += @("", "### Process", "", "| backend | runtime | launch to first frame (s) | whole run (s) | exit codes |", "|---|---|---:|---:|---|")
foreach ($backend in $present) {
    $mine = @($records | Where-Object { $_.Backend -eq $backend })
    $withData = @($mine | Where-Object { $_.Data })
    $runtime = "-"
    if ($withData.Count -gt 0) { $runtime = "$($withData[0].Data.backend), $($withData[0].Data.runtime)" }
    $startup = Median @($withData | ForEach-Object { [double]$_.Data.startupSeconds })
    $wall = Median @($mine | ForEach-Object { $_.Wall })
    $summary += "| $backend | $runtime | $(Num $startup 'F2') | $(Num $wall 'F1') | $(($mine | ForEach-Object { $_.Exit }) -join ' ') |"
}

$failures = @()
foreach ($run in $records) {
    if (-not $run.Data) { $failures += "$($run.Backend) run $($run.Run): no results (exit $($run.Exit))"; continue }
    foreach ($s in $run.Data.sections) { if (-not $s.ok) { $failures += "$($run.Backend) run $($run.Run), $($s.name): $($s.error)" } }
}
if ($failures.Count -gt 0) { $summary += @("", "### Failures", "") + ($failures | ForEach-Object { "- $_" }) }

$summary | Set-Content "$results\summary.md" -Encoding utf8
$summary

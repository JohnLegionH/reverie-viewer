# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# s1.ps1 - S-1 native smoke stage. Exports host\Viewer.Godot (release) with the portable
# Godot 4.7.2 .NET editor, guards the export's joltc and checks it carries no Linden data, then runs the EXPORTED build three times:
#     --rendering-driver vulkan   must PASS    (exit 0, verdict PASS, driver vulkan)
#     --rendering-driver d3d12    must PASS    (exit 0, verdict PASS, driver d3d12)
#     --rendering-driver opengl3  must REFUSE  (exit 3, verdict REFUSED, the R-1 message)
# plus a run without --s1-out (must exit 2 writing nothing) and a check that nothing lands beside the executable.
# Verdicts are read from tools\test-results\s1-<driver>.json, which S-1 writes there because this script passes
# "-- --s1-out=<tools\test-results> --s1-tag=<driver>" (ADR-019). JUnit XML: tools\test-results\S1.junit.xml.
# If the editor is not at -GodotExe, the stage is a real SKIP (reason in the XML), exit 0.
# Every Godot process goes through tools\godot.ps1 (APPDATA sandboxed, no lingering build processes).
# A window opens briefly for each run; that is expected.
#     powershell -NoProfile -File <repo>\tools\s1.ps1
# -ExportOnly: just the shipping export and the guards that read it (about 30 s instead of 5 to 6 minutes); not an S-1 run (see the parameter).
# A fresh checkout needs its packages restored first (dotnet restore, or the build/test step that does it): the export fails without them.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$GodotExe = "",   # default: the portable editor in \tools\godot\4.7.2-mono\ on this drive (set below)
    [string]$DiagDir = "",   # passed to every godot.ps1 run; absent, godot.ps1's default stands
    [string]$BuildFolder = "",   # check this folder as the export (no import, no export, no development export); absent: the repo's own build folder
    # Makes the shipping export and runs the checks that read the export folder (joltc, avatar data folder, no Linden data), then stops:
    # no development export and none of the runs of the exported viewer. It is for a step that only needs a current export to draw
    # with (tools\render.ps1). It is NOT the S-1 stage: it prints no "S-1 summary" and writes S1-export-only.junit.xml, so
    # S1.junit.xml is only ever written by a whole S-1 run. Absent: the whole stage, as before.
    [switch]$ExportOnly
)

$ErrorActionPreference = "Stop"
# stopping this script, however it is stopped, ends everything it started (job-guard.ps1, beside this script)
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
# the avatar data folder: NV_CHARACTER_DIR when it is set, else the copy of it kept in the repository
if (-not $env:NV_CHARACTER_DIR) { $env:NV_CHARACTER_DIR = Join-Path $repo "vendor\second-life-character" }
if (-not $GodotExe) { $GodotExe = Join-Path ([System.IO.Path]::GetPathRoot($PSScriptRoot)) "tools\godot\4.7.2-mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" }
$project = Join-Path $repo "host\Viewer.Godot"
$build = Join-Path $project "build"
$existing = [bool]$BuildFolder   # an export already made (the shipping folder): checked as it is, not exported again
if ($existing) { $build = [IO.Path]::GetFullPath($BuildFolder).TrimEnd([char]92); $env:NV_EXPORT_BUILD = $build }   # the .NET scans read the same folder
$results = Join-Path $PSScriptRoot "test-results"
$godot = Join-Path $PSScriptRoot "godot.ps1"
# The longest a start of the exported viewer may take before it is cut off. The checks below are about what a start logs and how it ends,
# not how fast it is, so a start beside other builds gets as long as it needs; a finished start ends its wait at once.
$startupBudgetSeconds = 300
$diagArgs = if ($DiagDir) { @("-DiagDir", $DiagDir) } else { @() }
$guard = Join-Path $PSScriptRoot "assert-joltc.ps1"
New-Item -ItemType Directory -Force -Path $results, $build | Out-Null
# the export's own restore (Godot runs dotnet publish, which inherits this environment through godot.ps1) is
# in NuGet locked mode, also when this stage runs on its own: a lock-file mismatch fails the export.
$env:RestoreLockedMode = "true"

$cases = New-Object System.Collections.Generic.List[object]
function Add-Case([string]$name, [string]$status, [string]$detail, [double]$seconds) {
    $cases.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail; Seconds = $seconds })
    Write-Output ("  {0,-7} S1.{1}  {2}" -f $status, $name, $detail)
}
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }
function Write-JUnit([string]$fileName = $(if ($ExportOnly) { "S1-export-only.junit.xml" } else { "S1.junit.xml" })) {
    $fail = @($cases | Where-Object Status -eq "FAIL").Count
    $skip = @($cases | Where-Object Status -eq "SKIP").Count
    $total = ($cases | Measure-Object Seconds -Sum).Sum
    $xml = New-Object System.Text.StringBuilder
    [void]$xml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$xml.AppendLine("<testsuites><testsuite name=""S1"" tests=""$($cases.Count)"" failures=""$fail"" skipped=""$skip"" errors=""0"" time=""$total"">")
    foreach ($c in $cases) {
        [void]$xml.Append("<testcase classname=""S1"" name=""$(Esc $c.Name)"" time=""$($c.Seconds)"">")
        if ($c.Status -eq "FAIL") { [void]$xml.Append("<failure message=""$(Esc $c.Detail)"" />") }
        elseif ($c.Status -eq "SKIP") { [void]$xml.Append("<skipped message=""$(Esc $c.Detail)"" />") }
        else { [void]$xml.Append("<system-out>$(Esc $c.Detail)</system-out>") }
        [void]$xml.AppendLine("</testcase>")
    }
    [void]$xml.AppendLine("</testsuite></testsuites>")
    Set-Content -LiteralPath (Join-Path $results $fileName) -Value $xml.ToString() -Encoding utf8
    Write-Output ("{4}: total {0}, passed {1}, failed {2}, skipped {3}" -f $cases.Count, @($cases | Where-Object Status -eq "PASS").Count, $fail, $skip, $(if ($ExportOnly) { "S-1 export-only (NOT an S-1 run) summary" } else { "S-1 summary" }))
}

Write-Output "S-1 stage (exported build)"
if (-not (Test-Path -LiteralPath $GodotExe)) {
    Add-Case "all" "SKIP" "Godot 4.7.2 .NET editor not found at $GodotExe (portable install, see tools\godot.ps1); S-1 not run" 0
    Write-JUnit
    exit 0
}

if (-not $existing) {
# 0. Import first (4.7 CLI docs: --import "waits for any resources to be imported, and then quits"), so a fresh
#    checkout's first scan is not part of the export.
$t0 = Get-Date
& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $GodotExe -TimeoutSeconds 300 -LogFile (Join-Path $results "s1-import.log") `
    -ArgLine "--headless --path `"$project`" --import" | Out-Null
$importExit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
if ($importExit -ne 0) {
    Add-Case "import" "FAIL" "import exit=$importExit; log: $(Join-Path $results 's1-import.log')" $secs
    Write-JUnit
    exit 1
}
Add-Case "import" "PASS" "import ok" $secs

# 1. Export (release). Godot exits 0 even when the .NET part of an export fails, so the managed output is
#    checked for freshness too.
#    NO RETRY: any hang fails the stage loudly. was root-caused: with the Roslyn compiler server on,
#    the export's publish started VBCSCompiler, which outlived the publish as an orphan while the editor never
#    exited (process tree captured before the kill). tools\godot.ps1 now sets UseSharedCompilation=false for every
#    Godot process; fresh-clone first exports went from hanging to 0 hangs (report). If this stage ever times
#    out again, godot.ps1 leaves hang-tree-<n>.txt snapshots in tools\test-results\.
$t0 = Get-Date
$exportStart = $t0.AddSeconds(-2)
& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $GodotExe -TimeoutSeconds 300 -LogFile (Join-Path $results "s1-export.log") `
    -ArgLine "--headless --path `"$project`" --export-release windows-desktop `"$build\Reverie.exe`"" | Out-Null
$exportExit = $LASTEXITCODE
$outputs = @("Reverie.exe", "Reverie.console.exe", "Reverie.pck", "data_Viewer.Godot_windows_x86_64\Viewer.Godot.dll") | ForEach-Object { Join-Path $build $_ }
$stale = @($outputs | Where-Object { -not (Test-Path -LiteralPath $_) -or (Get-Item -LiteralPath $_).LastWriteTime -lt $exportStart })
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
if ($exportExit -ne 0 -or $stale.Count -gt 0) {
    Add-Case "export" "FAIL" "export exit=$exportExit$(if ($exportExit -eq 124) { ' (HANG: see hang-tree-*.txt)' }); missing or stale: $($stale -join ', '); log: $(Join-Path $results 's1-export.log')" $secs
    Write-JUnit
    exit 1
}
} else {
    $t0 = Get-Date
    $secs = 0
}
$size = (Get-ChildItem -LiteralPath $build -Recurse -File | Where-Object { $_.FullName -notlike "$build\appdata\*" -and $_.FullName -notlike "$build\dev\*" -and $_.Name -notlike "s1-result*" } | Measure-Object Length -Sum).Sum
$rc = Get-Content -Raw -LiteralPath (Join-Path $build "data_Viewer.Godot_windows_x86_64\Viewer.Godot.runtimeconfig.json") | ConvertFrom-Json
$selfContained = [bool]($rc.runtimeOptions.includedFrameworks)
Add-Case "export" "PASS" ("export ok; self-contained={0}; size={1:N1} MiB" -f $selfContained, ($size / 1MB)) $secs

# 2. joltc guard on the WHOLE export folder.
$t0 = Get-Date
$g = & powershell -NoProfile -ExecutionPolicy Bypass -File $guard -Path $build
$gExit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
Add-Case "joltc_guard" ($(if ($gExit -eq 0) { "PASS" } else { "FAIL" })) (($g | Select-Object -Last 1) -join "") $secs

# 2a. The avatar data folder: the official viewer's character folder that the tests read (NV_CHARACTER_DIR), copied
#     unchanged beside the executable as character\, with its notice (ThirdPartyLicenses\character-NOTICE.txt) as
#     character\NOTICE.txt. Pass: every file's SHA-256 in the copy equals the source's, and the copy holds nothing else.
#     With -BuildFolder nothing is copied; the folder already there is checked the same way.
function Sync-CharacterFolder([string]$target, [bool]$copy) {
    $src = $env:NV_CHARACTER_DIR
    if (-not $src -or -not (Test-Path -LiteralPath $src -PathType Container)) { return "NV_CHARACTER_DIR is unset or not a folder: no avatar data folder to ship" }
    if (@(Get-ChildItem -LiteralPath $src -Directory -Force).Count -gt 0) { return "the avatar data source folder has subfolders; only a flat folder is shipped" }
    $dest = Join-Path $target "character"
    $notice = Join-Path $repo "ThirdPartyLicenses\character-NOTICE.txt"
    $srcFiles = @(Get-ChildItem -LiteralPath $src -File -Force)
    if ($copy) {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        foreach ($f in $srcFiles) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $dest $f.Name) -Force }
        Copy-Item -LiteralPath $notice -Destination (Join-Path $dest "NOTICE.txt") -Force
    }
    if (-not (Test-Path -LiteralPath $dest -PathType Container)) { return "no character folder in $target" }
    $differs = @($srcFiles | Where-Object {
        $c = Join-Path $dest $_.Name
        -not (Test-Path -LiteralPath $c) -or (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $c -Algorithm SHA256).Hash
    } | ForEach-Object { $_.Name })
    $extra = @(Get-ChildItem -LiteralPath $dest -Force | Where-Object { $_.Name -ne "NOTICE.txt" -and -not (Test-Path -LiteralPath (Join-Path $src $_.Name)) } | ForEach-Object { $_.Name })
    if (-not (Test-Path -LiteralPath (Join-Path $dest "NOTICE.txt"))) { return "character\NOTICE.txt is missing in $target" }
    if ($differs.Count -gt 0 -or $extra.Count -gt 0) { return "character folder in $target differs from the source: differing $($differs -join ', '); extra $($extra -join ', ')" }
    return ""
}
$t0 = Get-Date
$charProblem = Sync-CharacterFolder $build (-not $existing)
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$charCount = @(Get-ChildItem -LiteralPath (Join-Path $build "character") -File -Force -ErrorAction SilentlyContinue).Count
Add-Case "character_folder" ($(if (-not $charProblem) { "PASS" } else { "FAIL" })) ($(if (-not $charProblem) { "character\ holds the $($charCount - 1) source files, SHA-256 equal, and NOTICE.txt" } else { $charProblem })) $secs

# 2b. No Linden viewer data (LibreMetaverse contentFiles) anywhere in the export folder, apart from the avatar data folder
#     shipped on purpose (2a; the development export's copy too).
$t0 = Get-Date
$lg = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "assert-no-linden.ps1") -Path $build -Allow ((Join-Path $build "character") + ";" + (Join-Path $build "dev\character"))
$lgExit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
Add-Case "linden_guard" ($(if ($lgExit -eq 0) { "PASS" } else { "FAIL" })) (($lg | Select-Object -Last 1) -join "") $secs

# -ExportOnly: the shipping export and the guards that read it are done; no development export and no runs of the viewer.
if ($ExportOnly) {
    Write-JUnit
    exit $(if (@($cases | Where-Object Status -eq "FAIL").Count -gt 0) { 1 } else { 0 })
}

# 2c. The DEVELOPMENT export (preset "windows-dev", export-debug: the automation API compiled in, off unless
#     --automation), into build\dev\; then the assembly scan: the SHIPPING export has no automation type, enable string or
#     script (the negative control: the development export does, so the scan can see it).
if (-not $existing) {
$t0 = Get-Date
$devBuild = Join-Path $build "dev"
New-Item -ItemType Directory -Force -Path $devBuild | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $GodotExe -TimeoutSeconds 300 -LogFile (Join-Path $results "s1-export-dev.log") `
    -ArgLine "--headless --path `"$project`" --export-debug windows-dev `"$devBuild\Reverie.exe`"" | Out-Null
$devExit = $LASTEXITCODE
$devDll = Join-Path $devBuild "data_Viewer.Godot_windows_x86_64\Viewer.Godot.dll"
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
if ($devExit -ne 0 -or -not (Test-Path -LiteralPath $devDll) -or (Get-Item -LiteralPath $devDll).LastWriteTime -lt $t0.AddSeconds(-2)) {
    Add-Case "export_dev" "FAIL" "development export exit=$devExit; log: $(Join-Path $results 's1-export-dev.log')" $secs
} else {
    Add-Case "export_dev" "PASS" "development export ok (build\dev\)" $secs
}
$devCharProblem = Sync-CharacterFolder $devBuild $true
Add-Case "character_folder_dev" ($(if (-not $devCharProblem) { "PASS" } else { "FAIL" })) ($(if (-not $devCharProblem) { "build\dev\character\ equals the source, with NOTICE.txt" } else { $devCharProblem })) 0
} else {
    $devBuild = Join-Path $build "dev"
    Add-Case "export_dev" "PASS" "existing build folder: the development export in it is used as it is" 0
}
$t0 = Get-Date
$env:NV_EXPORT_SCAN = "1"
$scan = & dotnet test (Join-Path $repo "core\Viewer.Core.Tests\Viewer.Core.Tests.csproj") -nodeReuse:false -p:UseSharedCompilation=false `
    --filter "FullyQualifiedName~ExportScanTests" --logger "junit;LogFilePath=$results\ExportScan.junit.xml;MethodFormat=Class;FailureBodyFormat=Verbose" 2>&1
$scanExit = $LASTEXITCODE
$env:NV_EXPORT_SCAN = $null
$scanLine = ($scan | Select-String -Pattern "Passed!|Failed!" | Select-Object -Last 1)
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$scanPassed = $scanExit -eq 0 -and "$scanLine" -match "Passed:\s+2," -and "$scanLine" -match "Skipped:\s+0,"
Add-Case "automation_absent_from_shipping" ($(if ($scanPassed) { "PASS" } else { "FAIL" })) ("shipping export: no automation type/string/script; development export: found (negative control). " + "$scanLine".Trim()) $secs

# 2d. The shipping export can log in without anything added to it: the libraries the login path uses are byte-identical in
#     both exports, and the viewer's own core and shell assemblies hold the same types, members and method bodies
#     (core\Viewer.Core.Tests\ExportLoginProofTests.cs, with a negative control). The development export is the one the
#     automation run (tools\automation.ps1) logs in with against a test login service.
$t0 = Get-Date
$env:NV_EXPORT_SCAN = "1"
$same = & dotnet test (Join-Path $repo "core\Viewer.Core.Tests\Viewer.Core.Tests.csproj") -nodeReuse:false -p:UseSharedCompilation=false `
    --filter "FullyQualifiedName~ExportLoginProofTests" --logger "junit;LogFilePath=$results\ExportLoginProof.junit.xml;MethodFormat=Class;FailureBodyFormat=Verbose" 2>&1
$sameExit = $LASTEXITCODE
$env:NV_EXPORT_SCAN = $null
$sameLine = ($same | Select-String -Pattern "Passed!|Failed!" | Select-Object -Last 1)
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$samePassed = $sameExit -eq 0 -and "$sameLine" -match "Passed:\s+4," -and "$sameLine" -match "Skipped:\s+0,"
Add-Case "login_assemblies_identical" ($(if ($samePassed) { "PASS" } else { "FAIL" })) ("shipping and development exports: login libraries byte-identical, core and shell assemblies identical in content (negative control: the host assembly differs). " + "$sameLine".Trim()) $secs

# 3. The runs of the exported executable. S-1 writes its JSON ONLY to the directory passed after "--"
#    (ADR-019): tools\test-results\s1-<driver>.json. Nothing may be written under build\ (apart from the APPDATA
#    sandbox) from here on; checked after the runs.
$exe = Join-Path $build "Reverie.console.exe"
# Inventory of build\ (outside the APPDATA sandbox) before the runs: path -> size + write time.
function Get-BuildInventory {
    $inv = @{}
    Get-ChildItem -LiteralPath $build -Recurse -File -Force | Where-Object { $_.FullName -notlike "$build\appdata\*" } |
        ForEach-Object { $inv[$_.FullName] = "$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
    return $inv
}
$inventoryBefore = Get-BuildInventory

# 3a. S-1 requested (an --s1- argument) but no --s1-out: error, exit 2, nothing written anywhere. (without any
#     --s1- argument the build starts the viewer itself, the login screen, so the request must be explicit.)
$t0 = Get-Date
$out = & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $exe -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds $startupBudgetSeconds `
    -LogFile (Join-Path $results "s1-missing-out.log") -ArgLine "--rendering-driver vulkan -- --s1-tag=missing"
$exit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$newJson = @(Get-ChildItem -LiteralPath $results -Filter "s1-*.json" | Where-Object { $_.LastWriteTime -ge $t0.AddSeconds(-1) })
if ($exit -eq 2 -and ($out -join "`n") -match "no result directory" -and $newJson.Count -eq 0) {
    Add-Case "missing_out_arg" "PASS" "exit=2, error printed, nothing written" $secs
} else {
    Add-Case "missing_out_arg" "FAIL" "exit=$exit (expected 2); new json: $($newJson.Name -join ', ')" $secs
}

foreach ($run in @(
        @{ Driver = "vulkan"; Exit = 0; Verdict = "PASS" },
        @{ Driver = "d3d12"; Exit = 0; Verdict = "PASS" },
        @{ Driver = "opengl3"; Exit = 3; Verdict = "REFUSED" })) {
    $t0 = Get-Date
    $runStart = $t0.AddSeconds(-1)
    $resultFile = Join-Path $results "s1-$($run.Driver).json"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $exe -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds $startupBudgetSeconds `
        -LogFile (Join-Path $results "s1-$($run.Driver).log") -ArgLine "--rendering-driver $($run.Driver) -- `"--s1-out=$results`" --s1-tag=$($run.Driver)"
    $exit = $LASTEXITCODE
    $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    $problems = @()
    if (-not (Test-Path -LiteralPath $resultFile) -or (Get-Item -LiteralPath $resultFile).LastWriteTime -lt $runStart) {
        $problems += "no fresh $resultFile"
        $j = $null
    } else {
        $j = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json
    }
    if ($exit -ne $run.Exit) { $problems += "exit $exit, expected $($run.Exit)" }
    if ($j) {
        if ($j.verdict -ne $run.Verdict) { $problems += "verdict $($j.verdict), expected $($run.Verdict) ($($j.failures -join ' | '))" }
        if ($j.rendering.driver -ne $run.Driver) { $problems += "active driver $($j.rendering.driver), requested $($run.Driver)" }
        if ($run.Verdict -eq "REFUSED" -and ($j.refusal_message -notmatch "requires Vulkan or Direct3D 12")) { $problems += "refusal message missing" }
        if ($run.Verdict -eq "PASS") {
            if (-not $j.physics_lifecycle.pass) { $problems += "physics lifecycle: $($j.physics_lifecycle.verdict)" }
            if (@($j.joltc_loaded).Count -ne 1 -or $j.joltc_loaded[0].sha256 -ne "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5") { $problems += "loaded joltc is not exactly one patched copy" }
            if ($j.godot_physics.physics_3d_engine_setting -ne "Dummy" -or $j.godot_physics.space_create_valid) { $problems += "Godot physics is not Dummy" }
            # the floor (Vulkan 1.3 / D3D12 FL 12_0), the engine-log route (ADR-019), the login screen's layout and
            # accessible names in English and pseudo-localised (ADR-020).
            if ($j.floor.verdict -ne "Approved") { $problems += "floor verdict $($j.floor.verdict) (api $($j.floor.api_version))" }
            if (-not $j.engine_log.pass) { $problems += "engine log route: leaked=$($j.engine_log.leaked) events=$($j.engine_log.events -join ',')" }
            if (-not $j.ui.pass) { $problems += "login screen: en $(@($j.ui.en.problems).Count) / pseudo $(@($j.ui.pseudo.problems).Count) problems" }
        }
    }
    if ($problems.Count -eq 0) {
        $d = if ($j.verdict -eq "PASS") {
            "driver=$($j.rendering.driver) adapter=$($j.rendering.adapter_name) api=$($j.rendering.api_version) floor=$($j.floor.verdict)($($j.floor.parsed)) ui=en+pseudo ok engine_log=redacted; threads $($j.physics_lifecycle.threads_before)->$($j.physics_lifecycle.threads_after); private MiB $($j.physics_lifecycle.private_mib_before)->$($j.physics_lifecycle.private_mib_after); joltc $($j.joltc_loaded[0].path)"
        } else { "exit=$exit REFUSED: $($j.refusal_message)" }
        Add-Case $run.Driver "PASS" $d $secs
    } else {
        Add-Case $run.Driver "FAIL" ($problems -join "; ") $secs
    }
}

# 3b. The shipping export does not echo engine stdout (the "Godot Engine v4.7..." banner, engine prints),
#     while our redacted log lines (written by ConsoleLogSink to stderr) are still there; the development export keeps the
#     engine echo. Evidence: the vulkan run's output above, and one development S-1 run.
$shipLog = @(Get-Content -LiteralPath (Join-Path $results "s1-vulkan.log"))
$t0 = Get-Date
$devOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe (Join-Path $devBuild "Reverie.console.exe") -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds $startupBudgetSeconds `
    -LogFile (Join-Path $results "s1-dev-vulkan.log") -ArgLine "--rendering-driver vulkan -- `"--s1-out=$results`" --s1-tag=dev-vulkan"
$devRunExit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$shipBanner = @($shipLog | Select-String -SimpleMatch "Godot Engine v").Count
# Engine stdout = GD.Print (S-1's "S1 rendering..." / "S1 VERDICT..." lines). The raw STDERR echo of engine errors is the
# measured residual (disable_stderr would also silence the custom logger, ADR-019): counted and reported, not failed.
$stdoutPattern = "^S1 (rendering driver=|VERDICT |Viewer\.Core )"
$shipPrints = @($shipLog | Select-String -Pattern $stdoutPattern).Count
$shipStderrEcho = @($shipLog | Select-String -Pattern "^(ERROR|WARNING): |^S1 engine-log probe").Count
$shipOurs = @($shipLog | Select-String -Pattern "startup\.graphics").Count
$devBanner = @($devOut | Select-String -SimpleMatch "Godot Engine v").Count
$devPrints = @($devOut | Select-String -Pattern $stdoutPattern).Count
$devOurs = @($devOut | Select-String -Pattern "startup\.graphics").Count
$n47 = "shipping: banner=$shipBanner engine stdout prints=$shipPrints our redacted lines=$shipOurs (residual raw stderr echo lines: $shipStderrEcho); development (exit $devRunExit): banner=$devBanner engine prints=$devPrints ours=$devOurs"
Add-Case "n47_shipping_quiet" ($(if ($shipBanner -eq 0 -and $shipPrints -eq 0 -and $shipOurs -ge 1 -and $devRunExit -eq 0 -and $devBanner -ge 1 -and $devPrints -ge 1 -and $devOurs -ge 1) { "PASS" } else { "FAIL" })) $n47 $secs

# 4. ADR-019: nothing written beside the executable (or anywhere under build\ outside the APPDATA sandbox) by the runs.
$inventoryAfter = Get-BuildInventory
$written = @($inventoryAfter.Keys | Where-Object { -not $inventoryBefore.ContainsKey($_) -or $inventoryBefore[$_] -ne $inventoryAfter[$_] })
if ($written.Count -eq 0) {
    Add-Case "nothing_beside_exe" "PASS" "no file under build\ (outside appdata\) added or changed by the S-1 runs ($($inventoryAfter.Count) files compared)" 0
} else {
    Add-Case "nothing_beside_exe" "FAIL" "added or changed under build\ by the runs: $($written -join ', ')" 0
}

# 5. Test runs stay out of the shipping build. A test window's no-focus setting lives in an override.cfg in the run's
#    own launch folder (tools\test-launch.ps1), never in the build's folder; and a normal start that does find such a file
#    beside its executable ignores the setting instead of never taking focus.
$strays = @(@($build, $devBuild) | ForEach-Object { Join-Path $_ "override.cfg" } | Where-Object { Test-Path -LiteralPath $_ })
Add-Case "no_test_settings_in_build" ($(if ($strays.Count -eq 0) { "PASS" } else { "FAIL" })) ($(if ($strays.Count -eq 0) { "no override.cfg in build\ or build\dev\ after the S-1 runs" } else { "left in the build: $($strays -join ', ')" })) 0

. (Join-Path $PSScriptRoot "test-launch.ps1")
$t0 = Get-Date
$diagRoot = if ($DiagDir) { $DiagDir } else { $results }
$planted = New-TestLaunchFolder $build $diagRoot "offscreen"   # a build folder with an override.cfg beside its executable, as a stray one would be (opening minimised, so it stays off the monitors)
$plantedOut = ""; $plantedExit = -1; $controlOut = ""; $controlExit = -1
if ($planted) {
    $stray = Join-Path $results "stray-override"
    $plantedOut = (& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe (Join-Path $planted "Reverie.console.exe") -AppDataDir (Join-Path $stray "appdata-planted") -TimeoutSeconds $startupBudgetSeconds `
        -LogFile (Join-Path $results "s1-stray-override.log") -ArgLine "--rendering-driver vulkan --minimized --quit-after 60") -join "`n"
    $plantedExit = $LASTEXITCODE
    $controlOut = (& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $exe -AppDataDir (Join-Path $stray "appdata-control") -TimeoutSeconds $startupBudgetSeconds `
        -LogFile (Join-Path $results "s1-stray-control.log") -ArgLine "--rendering-driver vulkan --minimized --quit-after 60") -join "`n"
    $controlExit = $LASTEXITCODE
}
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$ignored = $plantedOut -match "startup\.no-focus-setting-ignored"
$controlQuiet = $controlOut -notmatch "startup\.no-focus-setting-ignored"
if (-not $planted) {
    Add-Case "shipping_start_ignores_stray_override" "SKIP" "no launch folder could be made (hard links need the build and the diagnostics folder on one volume)" $secs
} elseif ($ignored -and $controlQuiet -and $plantedExit -eq 0 -and $controlExit -eq 0) {
    Add-Case "shipping_start_ignores_stray_override" "PASS" "a normal start beside a no-focus override.cfg ignored it (exit 0, logged); the same build without one logged nothing (exit 0)" $secs
} else {
    Add-Case "shipping_start_ignores_stray_override" "FAIL" "with the stray file: ignored=$ignored exit=$plantedExit; without: quiet=$controlQuiet exit=$controlExit" $secs
}

# 5b. Where the engine's caches go. A plain start of the shipping export (caches on) writes its shader cache and pipeline cache only
#     under %APPDATA%\<product> (the project's custom user folder; 4.7.2 has no setting for the cache path itself), nothing under
#     %APPDATA%\Godot and nothing under %LOCALAPPDATA%. A start from a test launch folder (its override.cfg turns both caches off)
#     writes no cache file at all. APPDATA and LOCALAPPDATA are sandboxes under the diagnostics folder, on this drive.
$t0 = Get-Date
$cacheRoot = Join-Path $diagRoot ("cache-location-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
function Get-CacheFiles([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.FullName -match '\\shader_cache\\' -or $_.Name -match '^pipelines\..*\.cache$' })
}
$shipRoam = Join-Path $cacheRoot "ship-roaming"; $shipLocal = Join-Path $cacheRoot "ship-local"
$testRoam = Join-Path $cacheRoot "test-roaming"; $testLocal = Join-Path $cacheRoot "test-local"
$cacheShip = (& powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $exe -AppDataDir $shipRoam -LocalAppDataDir $shipLocal -TimeoutSeconds $startupBudgetSeconds `
    -LogFile (Join-Path $results "s1-cache-ship.log") -ArgLine "--rendering-driver vulkan --minimized --quit-after 60") -join "`n"
$cacheShipExit = $LASTEXITCODE
$cacheTestExit = -1
if ($planted) {
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe (Join-Path $planted "Reverie.console.exe") -AppDataDir $testRoam -LocalAppDataDir $testLocal -TimeoutSeconds $startupBudgetSeconds `
        -LogFile (Join-Path $results "s1-cache-test.log") -ArgLine "--rendering-driver vulkan --minimized --quit-after 60"
    $cacheTestExit = $LASTEXITCODE
}
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$productRoam = Join-Path $shipRoam "ReverieViewer"
$shipFiles = @(Get-CacheFiles $shipRoam) + @(Get-CacheFiles $shipLocal)
$shipIn = @($shipFiles | Where-Object { $_.FullName.StartsWith($productRoam + "\", [System.StringComparison]::OrdinalIgnoreCase) })
$shipElsewhere = @($shipFiles | Where-Object { -not $_.FullName.StartsWith($productRoam + "\", [System.StringComparison]::OrdinalIgnoreCase) })
$testFiles = @(Get-CacheFiles $testRoam) + @(Get-CacheFiles $testLocal)
if (-not $planted) {
    Add-Case "cache_location" "SKIP" "no launch folder could be made (hard links need the build and the diagnostics folder on one volume)" $secs
} elseif ($cacheShipExit -eq 0 -and $cacheTestExit -eq 0 -and $shipIn.Count -gt 0 -and $shipElsewhere.Count -eq 0 -and $testFiles.Count -eq 0) {
    Add-Case "cache_location" "PASS" "shipping start: $($shipIn.Count) cache files, all under APPDATA\ReverieViewer, none elsewhere; test launch folder start: $($testFiles.Count) cache files" $secs
} else {
    $where = @($shipElsewhere | Select-Object -First 3 | ForEach-Object { $_.FullName.Substring($cacheRoot.Length) }) -join ", "
    Add-Case "cache_location" "FAIL" "shipping start exit=${cacheShipExit}: cache files under APPDATA\ReverieViewer $($shipIn.Count), elsewhere $($shipElsewhere.Count) (e.g. $where); test launch folder start exit=${cacheTestExit}: cache files $($testFiles.Count); folders under $cacheRoot" $secs
}

# 6. The shipping export reaches its login screen with the grid address filled in from --grid, captured from its window
#    (tools\login-proof.ps1; no login is attempted, nothing is typed).
foreach ($driver in @("vulkan", "d3d12")) {
    $t0 = Get-Date
    $proofOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "login-proof.ps1") @diagArgs -Exe $exe -AppDataDir (Join-Path $build "appdata") -OutDir (Join-Path $results "login-proof") -Driver $driver
    $proofExit = $LASTEXITCODE
    $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    Add-Case "login_screen_$driver" ($(if ($proofExit -eq 0) { "PASS" } else { "FAIL" })) (($proofOut | Select-Object -Last 1) -join "") $secs
}

# 7. A login from the development export against a test login service on loopback: the pasted address (grid-info address
#    with a stray backtick) is tidied and resolved, the login is posted to the service, and the service's own refusal is shown
#    (tools\automation.ps1, case login_test_service, which runs with the other automation checks).
$t0 = Get-Date
$autoOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "automation.ps1")
$autoExit = $LASTEXITCODE
$secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$loginLine = @($autoOut | Where-Object { $_ -match "Automation\.login_test_service" }) | Select-Object -First 1
Add-Case "dev_login_against_test_service" ($(if ($autoExit -eq 0 -and "$loginLine" -match "^\s+PASS") { "PASS" } else { "FAIL" })) ("automation exit $autoExit; " + "$loginLine".Trim()) $secs

Write-JUnit
if (@($cases | Where-Object Status -eq "FAIL").Count -gt 0) { exit 1 }
exit 0

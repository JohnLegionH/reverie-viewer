# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# run-laptop.ps1 - the run script of the portable hardware check kit (built by build-kit.ps1, started by RUN-LAPTOP.cmd).
#
# It answers "does the viewer run acceptably on this PC" from recorded data: no login, no network, no credentials.
#   1. records the machine (CPU, memory, graphics adapter and driver, display, Windows version, power);
#   2. checks the hardware floor (Vulkan 1.3 or Direct3D 12 feature level 12_0) by running the exported build's S-1
#      stage on each backend; a backend that fails is reported and skipped, never a crash of the kit;
#   3. on each available backend, in rounds, runs the measuring scenes through tools\godot.ps1 -MeasureRun:
#        region replays (two captures), busy views (2,000 and 6,000 objects, merging on), a standing crowd of 25 realistic
#        avatars, and a cold arrival of 25 realistic avatars;
#   4. writes one zip next to RUN-LAPTOP.cmd with the machine record, every run's result file and log, a summary table and
#      any crash dump.
#
# It installs nothing and changes no setting. Everything it writes is inside the kit folder (work\): the viewer's user data,
# its pipeline cache, the temporary files and the results. APPDATA, LOCALAPPDATA, TEMP and TMP are pointed into work\ for
# every process it starts. The graphics driver keeps its own shader cache where the driver chooses (the kit cannot move it).
# Windows PowerShell 5.1 compatible.
# =====================================================================================
[CmdletBinding()]
param(
    [int]$Rounds = 3,
    # A new round starts only if the run so far plus the last round's length fits in this many minutes. At least one round is always run.
    [int]$BudgetMinutes = 40,
    [string[]]$Backends = @("vulkan", "d3d12"),
    # Names of the scenes to run (default: all). For a repeat of one scene.
    [string[]]$OnlyScenes = @(),
    [switch]$NoZip
)

$ErrorActionPreference = "Continue"
# powershell -File passes "a,b" as one string: split lists
$Backends = @($Backends | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$OnlyScenes = @($OnlyScenes | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$kit = Split-Path -Parent $PSScriptRoot
$work = Join-Path $kit "work"
$tmp = Join-Path $work "tmp"
$appdata = Join-Path $work "appdata"
$localapp = Join-Path $work "localappdata"
$diag = Join-Path $work "diag"
$results = Join-Path $work "results"
$exeDir = Join-Path $kit "viewer"
$exe = Join-Path $exeDir "Reverie.console.exe"
$godot = Join-Path $kit "tools\godot.ps1"
$data = Join-Path $kit "data"
$startTime = Get-Date

function Say([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date).ToString("HH:mm:ss"), $m) }

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "The viewer is not in this folder ($exe). If you opened the zip without extracting it, extract it first and run RUN-LAPTOP.cmd from the extracted folder."
    exit 2
}
New-Item -ItemType Directory -Force -Path $work, $tmp, $appdata, $localapp, $diag, $results | Out-Null
# everything the viewer or its helpers write goes under work\
$env:TEMP = $tmp; $env:TMP = $tmp
$env:APPDATA = $appdata; $env:LOCALAPPDATA = $localapp
$env:DOTNET_CLI_HOME = $tmp; $env:DOTNET_NOLOGO = "1"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# ---------------------------------------------------------------- 1. the machine
Say "1/4 Recording this machine"
$machine = [ordered]@{}
try {
    $cpu = @(Get-CimInstance Win32_Processor)
    $cs = Get-CimInstance Win32_ComputerSystem
    $os = Get-CimInstance Win32_OperatingSystem
    $machine.computer = $env:COMPUTERNAME
    $machine.date_utc = (Get-Date).ToUniversalTime().ToString("o")
    $machine.cpu = ($cpu | ForEach-Object { $_.Name.Trim() }) -join " + "
    $machine.cpu_cores = ($cpu | Measure-Object NumberOfCores -Sum).Sum
    $machine.cpu_threads = ($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    $machine.cpu_max_mhz = ($cpu | Select-Object -First 1).MaxClockSpeed
    $machine.ram_gib = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
    $machine.ram_free_gib_at_start = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    $machine.windows = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    $machine.model = "$($cs.Manufacturer) $($cs.Model)"
    $gpus = @()
    foreach ($v in @(Get-CimInstance Win32_VideoController)) {
        $gpus += [ordered]@{ name = $v.Name; driver_version = $v.DriverVersion; driver_date = "$($v.DriverDate)"; adapter_ram_bytes_wmi = $v.AdapterRAM
            current_resolution = "$($v.CurrentHorizontalResolution)x$($v.CurrentVerticalResolution)"; current_refresh_hz = $v.CurrentRefreshRate; status = $v.Status }
    }
    $machine.graphics = $gpus
    # video memory as the driver records it (64-bit, unlike WMI's AdapterRAM, which stops at 4 GiB)
    $vram = @()
    try {
        $cls = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
        foreach ($k in @(Get-ChildItem -LiteralPath $cls -ErrorAction Stop | Where-Object { $_.PSChildName -match '^\d{4}$' })) {
            $p = Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue
            if ($p -and $p.'HardwareInformation.qwMemorySize') { $vram += [ordered]@{ adapter = "$($p.DriverDesc)"; video_memory_mib = [math]::Round([double]$p.'HardwareInformation.qwMemorySize' / 1MB) } }
        }
    } catch { }
    $machine.video_memory_registry = $vram
    $battery = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $machine.battery_present = ($battery.Count -gt 0)
    if ($battery.Count -gt 0) { $machine.on_ac_power = ($battery[0].BatteryStatus -in 2, 3, 6, 7, 8, 9, 11); $machine.battery_status_code = $battery[0].BatteryStatus }
    try { $machine.power_plan = ((& powercfg /getactivescheme) -join " ").Trim() } catch { }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $machine.screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.Bounds.Width)x$($_.Bounds.Height)$(if ($_.Primary) { ' primary' })" })
    } catch { }
    $drive = Get-PSDrive -Name ((Split-Path -Qualifier $kit).TrimEnd(':')) -ErrorAction SilentlyContinue
    if ($drive) { $machine.kit_drive_free_gib = [math]::Round($drive.Free / 1GB, 1) }
    $machine.kit_folder_is_on = Split-Path -Qualifier $kit
} catch { $machine.error = $_.Exception.Message }
$machine | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results "machine.json") -Encoding utf8
Say ("    {0}; {1} cores / {2} threads; {3} GiB RAM; {4}" -f $machine.cpu, $machine.cpu_cores, $machine.cpu_threads, $machine.ram_gib, (($machine.graphics | ForEach-Object { "$($_.name) (driver $($_.driver_version), $($_.current_resolution) @ $($_.current_refresh_hz) Hz)" }) -join "; "))
if ($machine.battery_present -and $machine.on_ac_power -eq $false) { Say "    WARNING: the laptop is on battery. Plug it in and start again for comparable figures." }
if ($machine.kit_drive_free_gib -and $machine.kit_drive_free_gib -lt 4) { Say "    WARNING: less than 4 GiB free on the kit's drive." }

# ---------------------------------------------------------------- run helper
$runs = New-Object System.Collections.Generic.List[object]
function Invoke-Viewer([string]$backend, [string]$tag, [string]$outDir, [string]$extra, [int]$timeout) {
    $t0 = Get-Date
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $argLine = "--rendering-driver $backend -- --test-render `"--tr-out=$outDir`" --tr-tag=$tag $extra"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $godot -MeasureRun -DiagDir $diag -Exe $exe -AppDataDir $appdata -LocalAppDataDir $localapp `
        -TimeoutSeconds $timeout -LogFile (Join-Path $outDir "$tag.log") -ArgLine $argLine | Out-Null
    $code = $LASTEXITCODE
    $jf = Join-Path $outDir "$tag.json"
    $j = $null
    if ((Test-Path -LiteralPath $jf) -and (Get-Item -LiteralPath $jf).LastWriteTime -ge $t0.AddSeconds(-1)) {
        try { $j = Get-Content -Raw -LiteralPath $jf | ConvertFrom-Json } catch { }
    }
    return @{ Exit = $code; Json = $j; Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) }
}

# ---------------------------------------------------------------- 2/3. floor and S-1 per backend
Say "2/4 Hardware floor and S-1 smoke run, per graphics backend"
$available = New-Object System.Collections.Generic.List[string]
$floor = [ordered]@{}
foreach ($be in $Backends) {
    $s1dir = Join-Path $results "s1"
    New-Item -ItemType Directory -Force -Path $s1dir | Out-Null
    $t0 = Get-Date
    & powershell -NoProfile -ExecutionPolicy Bypass -File $godot -DiagDir $diag -Exe $exe -AppDataDir $appdata -LocalAppDataDir $localapp -TimeoutSeconds 180 `
        -LogFile (Join-Path $s1dir "s1-$be.log") -ArgLine "--rendering-driver $be -- `"--s1-out=$s1dir`" --s1-tag=$be" | Out-Null
    $code = $LASTEXITCODE
    $j = $null
    $jf = Join-Path $s1dir "s1-$be.json"
    if ((Test-Path -LiteralPath $jf) -and (Get-Item -LiteralPath $jf).LastWriteTime -ge $t0.AddSeconds(-1)) { try { $j = Get-Content -Raw -LiteralPath $jf | ConvertFrom-Json } catch { } }
    $ok = ($code -eq 0 -and $j -and $j.verdict -eq "PASS" -and $j.rendering.driver -eq $be -and $j.floor.verdict -eq "Approved")
    $entry = [ordered]@{ backend = $be; exit_code = $code; available = $ok; seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) }
    if ($j -and $j.rendering) { $entry.adapter = $j.rendering.adapter_name; $entry.api_version = $j.rendering.api_version }
    if ($j -and $j.floor) { $entry.floor = "$($j.floor.verdict) ($($j.floor.parsed))" }
    if ($j -and $j.verdict) { $entry.verdict = $j.verdict }
    if ($j -and $j.refusal_message) { $entry.refusal = "$($j.refusal_message)" }
    if ($j -and $j.failures) { $entry.failures = @($j.failures) }
    if ($ok) { $available.Add($be); Say ("    {0}: AVAILABLE - {1}, api {2}, floor {3}" -f $be, $entry.adapter, $entry.api_version, $entry.floor) }
    else {
        $why = if ($entry.refusal) { $entry.refusal } elseif ($entry.failures) { ($entry.failures -join " | ") } elseif ($code -eq 124) { "timed out" } else { "exit code $code, no passing result" }
        $entry.reason = $why
        Say ("    {0}: SKIPPED - this PC does not meet the floor for it, or it could not start ({1})" -f $be, $why)
    }
    $floor[$be] = $entry
}
$floor | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results "floor.json") -Encoding utf8
if ($available.Count -eq 0) {
    Say "No graphics backend met the floor. Nothing more to measure; the results zip records why."
}

# ---------------------------------------------------------------- 4. the measuring scenes
$captureV9 = Join-Path $data "captures\capture-region-a-v9.jsonl.gz"
$captureV5 = Join-Path $data "captures\capture-region-a-v5.jsonl.gz"
$assets = Join-Path $data "assets"
$character = Join-Path $data "character"
$bakeCache = Join-Path $data "bake-cache"
$zoo = Join-Path $kit "fixtures\materials\projector.matzoo.json"
$pathArgs = "--tr-seed=8 --tr-size=1920x1080 --tr-texture-server=partial --tr-texture-delay-ms=105 --tr-content-store --tr-budget=2048m --tr-path-seconds=30 --tr-return --tr-asset-store-mib=16 --tr-merge"
$scenes = @(
    @{ Name = "region-v9"; Kind = "path";   Timeout = 500; Args = "`"--tr-fixture=$captureV9`" `"--tr-assets=$assets`" $pathArgs" },
    @{ Name = "region-v5"; Kind = "path";   Timeout = 500; Args = "`"--tr-fixture=$captureV5`" `"--tr-assets=$assets`" $pathArgs" },
    @{ Name = "busy-2000"; Kind = "busy";   Timeout = 400; Args = "`"--tr-fixture=$zoo`" --tr-busy=objects:2000,glow:10,lights:40,projectors:8 --tr-busy-seconds=15 --tr-seed=8 --tr-size=1920x1080 --tr-merge" },
    @{ Name = "busy-6000"; Kind = "busy";   Timeout = 400; Args = "`"--tr-fixture=$zoo`" --tr-busy=objects:6000,glow:10,lights:40,projectors:8 --tr-busy-seconds=15 --tr-seed=8 --tr-size=1920x1080 --tr-merge" },
    @{ Name = "crowd-25-standing"; Kind = "crowd"; Timeout = 400; Args = "--tr-avatars=crowd:25,real --tr-crowd-seconds=6 `"--tr-character=$character`" `"--tr-assets=$assets`" `"--tr-bake-cache=$bakeCache`" --tr-seed=8 --tr-size=1920x1080" },
    @{ Name = "arrive-25-cold"; Kind = "arrive"; Timeout = 400; Args = "--tr-avatars=arrive:25 --tr-arrive-seconds=1 `"--tr-character=$character`" `"--tr-assets=$assets`" `"--tr-bake-cache=$bakeCache`" --tr-seed=8 --tr-size=1920x1080" }
)

# the first existing key (full path, dotted) among the patterns, in order
function Pick($flat, [string[]]$patterns) {
    foreach ($p in $patterns) {
        foreach ($k in $flat.Keys) { if ($k -match $p) { return $flat[$k] } }
    }
    return $null
}
function Get-Flat($o, [string]$prefix, $into) {
    if ($null -eq $o) { return }
    if ($o -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $o.PSObject.Properties) { Get-Flat $p.Value ($prefix + $p.Name + ".") $into }
    } elseif ($o -is [System.Array]) { return }
    else { $into[$prefix.TrimEnd('.')] = $o }
}
function Summarise-Run($scene, [string]$backend, [int]$round, $r) {
    $flat = [ordered]@{}
    if ($r.Json) { Get-Flat $r.Json "" $flat }
    $row = [ordered]@{ scene = $scene.Name; backend = $backend; round = $round; exit = $r.Exit; verdict = $(if ($r.Json) { $r.Json.verdict } else { "NO RESULT" }); seconds = $r.Seconds }
    $fp = switch ($scene.Kind) {
        "path"   { "^path\.frame_ms\." }
        "busy"   { "^busy\.frame_ms\." }
        "crowd"  { "^avatars_crowd\.frame_ms\." }
        "arrive" { "^avatars_crowd\.arrival\.frame_ms\." }
    }
    $row.frame_p50_ms = Pick $flat @(($fp + "p50$"))
    $row.frame_p95_ms = Pick $flat @(($fp + "p95$"))
    $row.frame_max_ms = Pick $flat @(($fp + "max$"))
    $row.pacing_capped = Pick $flat @('^frame_pacing\.capped$')
    $row.pacing_enforced = Pick $flat @('^frame_pacing\.enforced$')
    $row.pacing_mode_fraction = Pick $flat @('^frame_pacing\.mode_fraction$')
    $row.peak_working_set_mib = Pick $flat @('process_memory\.peak_working_set_mib$', 'peak_working_set_mib$')
    $row.peak_private_mib = Pick $flat @('process_memory\.peak_private_mib$', 'peak_private_mib$')
    $row.first_image_face_p50_ms = Pick $flat @('first_image_per_face\.p50$')
    $row.first_image_90pct_ms = Pick $flat @('first_image_per_face\.at_90pct_of_in_view$')
    $row.video_mem_mib = Pick $flat @('video_mem_mib$', 'video_memory_mib$', 'video_mem.*mib$')
    $row.resident_textures_mib = Pick $flat @('path\.peak_resident_mib$')
    $row.arrival_time_to_finished_s = Pick $flat @('arrival\.time_to_finished_s$')
    $row.arrival_longest_slice_ms = Pick $flat @('arrival\.slice_max_ms$')
    $row.failures = if ($r.Json -and $r.Json.failures) { (@($r.Json.failures) -join " | ") } else { $null }
    return [pscustomobject]$row
}

if ($OnlyScenes.Count -gt 0) { $scenes = @($scenes | Where-Object { $OnlyScenes -contains $_.Name }) }
Say "3/4 Measuring scenes: $($available.Count) backend(s), up to $Rounds round(s), budget $BudgetMinutes min"
$roundsDone = 0
$roundSeconds = 0
$roundsDropped = $false
for ($round = 1; $round -le $Rounds -and $available.Count -gt 0; $round++) {
    $elapsedMin = ((Get-Date) - $startTime).TotalMinutes
    if ($round -gt 1 -and ($elapsedMin + $roundSeconds / 60) -gt $BudgetMinutes) {
        Say ("    Stopping after {0} round(s): another round would run past the {1}-minute budget. Rounds are dropped, never scenes." -f $roundsDone, $BudgetMinutes)
        $roundsDropped = $true
        break
    }
    $rt0 = Get-Date
    foreach ($be in $available) {
        foreach ($sc in $scenes) {
            $dir = Join-Path $results ("runs\" + $be + "\" + $sc.Name)
            $tag = "$($sc.Name)-$be-r$round"
            Say ("    round {0}/{1} {2} {3} ..." -f $round, $Rounds, $be, $sc.Name)
            $r = Invoke-Viewer $be $tag $dir $sc.Args $sc.Timeout
            $row = Summarise-Run $sc $be $round $r
            $runs.Add($row)
            Say ("        exit {0}, {1}, {2} s; frame p50/p95/max {3}/{4}/{5} ms{6}" -f $r.Exit, $row.verdict, $r.Seconds, $row.frame_p50_ms, $row.frame_p95_ms, $row.frame_max_ms,
                $(if ($row.failures) { "; " + $row.failures } else { "" }))
        }
    }
    $roundSeconds = ((Get-Date) - $rt0).TotalSeconds
    $roundsDone = $round
}

# ---------------------------------------------------------------- summary
function Median($v) {
    $a = @($v | Where-Object { $null -ne $_ -and "$_" -ne "" } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($a.Count -eq 0) { return $null }
    $m = [int][math]::Floor($a.Count / 2)
    if ($a.Count % 2 -eq 1) { return $a[$m] } else { return ($a[$m - 1] + $a[$m]) / 2 }
}
$runs | Export-Csv -LiteralPath (Join-Path $results "runs.csv") -NoTypeInformation -Encoding utf8
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Laptop check results - $($env:COMPUTERNAME) - $((Get-Date).ToString('yyyy-MM-dd HH:mm'))")
[void]$md.AppendLine("")
[void]$md.AppendLine("Machine: $($machine.cpu); $($machine.cpu_cores) cores; $($machine.ram_gib) GiB RAM; " + (($machine.graphics | ForEach-Object { "$($_.name) driver $($_.driver_version) $($_.current_resolution)@$($_.current_refresh_hz)Hz" }) -join "; "))
[void]$md.AppendLine("")
[void]$md.AppendLine("## Graphics floor / S-1")
foreach ($be in $floor.Keys) {
    $e = $floor[$be]
    if ($e.available) { [void]$md.AppendLine("- $be : AVAILABLE ($($e.adapter), api $($e.api_version), floor $($e.floor))") }
    else { [void]$md.AppendLine("- $be : SKIPPED ($($e.reason))") }
}
[void]$md.AppendLine("")
[void]$md.AppendLine("## Medians over the rounds ($roundsDone of $Rounds rounds run$(if ($roundsDropped) { '; rounds dropped for time' }))")
[void]$md.AppendLine("")
[void]$md.AppendLine("| backend | scene | runs ok | frame p50 ms | frame p95 ms | frame max ms | pacing capped | peak WS MiB | peak private MiB | first image 90% ms | video MiB | arrival s | seconds/run |")
[void]$md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
foreach ($be in $available) {
    foreach ($sc in $scenes) {
        $g = @($runs | Where-Object { $_.backend -eq $be -and $_.scene -eq $sc.Name })
        if ($g.Count -eq 0) { continue }
        $okN = @($g | Where-Object { $_.exit -eq 0 }).Count
        $f = { param($v) if ($null -eq $v) { "-" } else { ([math]::Round($v, 2)).ToString([System.Globalization.CultureInfo]::InvariantCulture) } }
        $capped = (@($g | Where-Object { $_.pacing_capped -eq $true }).Count)
        [void]$md.AppendLine("| $be | $($sc.Name) | $okN/$($g.Count) | $(& $f (Median ($g | ForEach-Object { $_.frame_p50_ms }))) | $(& $f (Median ($g | ForEach-Object { $_.frame_p95_ms }))) | $(& $f (Median ($g | ForEach-Object { $_.frame_max_ms }))) | $capped/$($g.Count) | $(& $f (Median ($g | ForEach-Object { $_.peak_working_set_mib }))) | $(& $f (Median ($g | ForEach-Object { $_.peak_private_mib }))) | $(& $f (Median ($g | ForEach-Object { $_.first_image_90pct_ms }))) | $(& $f (Median ($g | ForEach-Object { $_.video_mem_mib }))) | $(& $f (Median ($g | ForEach-Object { $_.arrival_time_to_finished_s }))) | $(& $f (Median ($g | ForEach-Object { $_.seconds }))) |")
    }
}
[void]$md.AppendLine("")
[void]$md.AppendLine("Runs that failed or were refused (exit not 0):")
$bad = @($runs | Where-Object { $_.exit -ne 0 })
if ($bad.Count -eq 0) { [void]$md.AppendLine("- none") } else { foreach ($b in $bad) { [void]$md.AppendLine("- $($b.backend) $($b.scene) round $($b.round): exit $($b.exit), $($b.verdict) $($b.failures)") } }
$total = [math]::Round(((Get-Date) - $startTime).TotalMinutes, 1)
[void]$md.AppendLine("")
[void]$md.AppendLine("Total time: $total minutes.")
Set-Content -LiteralPath (Join-Path $results "SUMMARY.md") -Value $md.ToString() -Encoding utf8

# ---------------------------------------------------------------- 5. the zip
Say "4/4 Writing the results zip"
if (-not $NoZip) {
    $zipName = "laptop-results-$($env:COMPUTERNAME)-$((Get-Date).ToString('yyyyMMdd-HHmm')).zip"
    $zipPath = Join-Path $kit $zipName
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $add = {
            param($file, $entry)
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
        foreach ($f in Get-ChildItem -LiteralPath $results -Recurse -File) {
            if ($f.Extension -eq ".png" -or $f.FullName -like "*-content-*") { continue }   # pictures and the per-run content stores stay on this machine
            & $add $f.FullName ("results\" + $f.FullName.Substring($results.Length + 1))
        }
        # the runner's own notes and any crash dump; the hard-linked launch folders are not part of the results
        foreach ($f in Get-ChildItem -LiteralPath $diag -File -ErrorAction SilentlyContinue) { & $add $f.FullName ("diag\" + $f.Name) }
        $crash = Join-Path $diag "crash"
        if (Test-Path -LiteralPath $crash) { foreach ($f in Get-ChildItem -LiteralPath $crash -Recurse -File) { & $add $f.FullName ("diag\crash\" + $f.FullName.Substring($crash.Length + 1)) } }
        $manifest = Join-Path $kit "KIT-MANIFEST.txt"
        if (Test-Path -LiteralPath $manifest) { & $add $manifest "KIT-MANIFEST.txt" }
    } finally { $zip.Dispose() }
    $mb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Say "    wrote $zipPath ($mb MiB)"
    Write-Host ""
    Write-Host "DONE: bring back $zipName"
} else {
    Write-Host ""
    Write-Host "DONE (no zip requested): results are in $results"
}
exit 0

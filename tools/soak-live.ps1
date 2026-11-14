# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# A long live run against a real grid session with the development export, for leaks over hours. One viewer process per backend; each cycle
# (about ten minutes): stand, turn the camera through a full circle, step through the five time-of-day choices, walk a short way and back,
# log out and log back in inside the same viewer process. Nothing is rezzed, edited or deleted. The viewer's own figures come from the
# automation API's sample command (process, collector after two full collections, engine objects, video memory, native heaps, engine log
# errors and the pipelines compiled so far); one row per cycle goes to cycles.csv, one row per phase to steps.csv.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\soak-live.ps1 -Out <new folder> -Plan vulkan:60,d3d12:40
#     [-SettleMinutes 15] [-CycleSeconds 600] [-Account viewer1] [-RegionServerLog <file>] [-WatchName "<first last>"] [-DryRun]
#
# Inputs, as the other live scripts take them: NV_TEST_ACCOUNTS (the accounts file), NV_GRID_INFO_URL, NV_LIVE_START (the start location),
# NV_OTHER_CLIENT_PATTERN. The credentials file is read by the viewer itself; this script never opens it. The login address is read from the
# grid's get_grid_info at run time and held in memory. Every Godot process goes through tools\godot.ps1.
# -WatchName names an avatar that must not be disturbed: while the region server log shows it online, the walk is replaced by standing.
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Plan = "vulkan:60,d3d12:40",
    [int]$SettleMinutes = 15,
    [int]$CycleSeconds = 600,
    [string]$Account = "viewer1",
    [string]$Exe = "",
    [string]$Accounts = $env:NV_TEST_ACCOUNTS,
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [string]$RegionServerLog = "",
    [string]$WatchName = "",
    [string]$DiagDir = "",
    [switch]$DryRun
)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $Accounts) { $Accounts = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

# ---------------- plan and preconditions ----------------
$legs = @()
foreach ($part in $Plan.Split(",")) {
    $bits = $part.Split(":")
    if ($bits.Count -ne 2 -or $bits[0] -notin @("vulkan", "d3d12") -or -not ($bits[1] -as [int]) -or [int]$bits[1] -le 0) { Write-Output "BLOCKED: -Plan must read backend:minutes[,backend:minutes] (vulkan or d3d12)"; exit 3 }
    $legs += [pscustomobject]@{ Backend = $bits[0]; Minutes = [int]$bits[1] }
}
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Out)) { $problems.Add("-Out must be an absolute path") } elseif (Test-Path -LiteralPath $Out) { $problems.Add("-Out exists (a fresh folder is required)") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export)") }
if (-not $Accounts -or -not (Test-Path -LiteralPath $Accounts -PathType Leaf)) { $problems.Add("the accounts file is not a file (NV_TEST_ACCOUNTS)") }
if (-not $Start) { $problems.Add("the start location is missing (NV_LIVE_START)") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the test account, or none") }
elseif ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    if (@(Find-OtherClients -Pattern $OtherClientPattern).Count -gt 0) { $problems.Add("another test client is running: the test account may be in use") }
}
if ($RegionServerLog -and -not (Test-Path -LiteralPath $RegionServerLog -PathType Leaf)) { $problems.Add("-RegionServerLog is not a file") }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

# whether the watched avatar is online, from the region server log: its last login or logout line decides
function Test-WatchedOnline {
    if (-not $RegionServerLog -or -not $WatchName) { return $false }
    $parts = $WatchName.Split(" ")
    $logIn = "Incoming client `"$($parts[0])`" `"$($parts[1])`""
    $logOut = "logout request for $WatchName"
    $lines = @(Select-String -LiteralPath $RegionServerLog -SimpleMatch -Pattern $logIn, $logOut -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) { return $false }
    return $lines[-1].Line.Contains($logIn)
}
$watchedAtStart = Test-WatchedOnline
Write-Output ("preconditions ok: plan {0}, account {1}; watched avatar online at start: {2}" -f $Plan, $Account, $watchedAtStart)
if ($watchedAtStart) { Write-Output "BLOCKED: the watched avatar is online"; exit 3 }
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

# ---------------- helpers ----------------
function Invoke-Api($v, [string]$command, $body = @{}) {
    $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 5 }
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $v.Port)
    $tcp.ReceiveTimeout = 120000
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($json)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$($v.Port)`r`nAuthorization: Bearer $($v.Token)`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $all = (New-Object System.IO.StreamReader($s)).ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Parse([string]$json) { try { return $json | ConvertFrom-Json } catch { return $null } }
function Wait-Until([scriptblock]$cond, [int]$seconds, [int]$pollMs = 1000) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds $pollMs }
    return $null
}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,8:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$cyclesCsv = Join-Path $Out "cycles.csv"; $stepsCsv = Join-Path $Out "steps.csv"
$script:headers = @{}
function Append-Row([string]$path, [System.Collections.Specialized.OrderedDictionary]$row) {
    $keys = @($row.Keys)
    if (-not $script:headers.ContainsKey($path)) { $script:headers[$path] = $keys -join ","; Add-Content -LiteralPath $path -Value $script:headers[$path] -Encoding utf8 }
    $cols = $script:headers[$path].Split(",")
    Add-Content -LiteralPath $path -Value (($cols | ForEach-Object { if ($row.Contains($_)) { [string]$row[$_] } else { "" } }) -join ",") -Encoding utf8
}
function Sample($v, [string]$backend, [int]$cycle, [string]$phase, [bool]$walkAllowed, [int]$loginFailures, [string]$csv) {
    $r = Invoke-Api $v "sample"
    $o = Parse $r.Body
    $row = [ordered]@{ backend = $backend; cycle = $cycle; phase = $phase; elapsed_s = [math]::Round($clock.Elapsed.TotalSeconds, 1); walk = [int]$walkAllowed; login_failures = $loginFailures; status = $r.Status }
    if ($o) { foreach ($p in $o.PSObject.Properties) { $row[$p.Name] = $p.Value } }
    Append-Row $csv $row
    return $o
}
function Login($v) {
    for ($try = 1; $try -le 3; $try++) {
        $null = Invoke-Api $v "session" @{ action = "login"; accounts_file = $Accounts; account = $Account; login_uri = $loginUri; start = $Start }
        $st = Wait-Until { $s = Parse (Invoke-Api $v "session" '{"action":"state"}').Body; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150 2000
        if ($st) { Start-Sleep -Seconds 10; return ($try - 1) }
        Note "login attempt $try did not reach the world"
        try { $null = Invoke-Api $v "session" '{"action":"logout"}' } catch { }
        Start-Sleep -Seconds 15
    }
    throw "the account did not reach the world in 3 attempts"
}
function Logout($v) {
    $r = Invoke-Api $v "session" '{"action":"logout"}'
    $ended = Wait-Until { $s = Parse (Invoke-Api $v "session" '{"action":"state"}').Body; if ($s -and $s.ended) { $s } } 60 2000
    if (-not $ended) { Note "logout: the session did not report ended" }
}
function Key($v, [string]$key, [bool]$down) { $null = Invoke-Api $v "keys" @{ key = $key; down = $down } }

Write-Output ""
Write-Output "SOAK-LIVE STARTING - no grid restarts, heavy builds or test runs, or other test clients until SOAK-LIVE DONE"
Write-Output ""
$exit = 0
foreach ($leg in $legs) {
    $be = $leg.Backend
    $sandbox = Join-Path $Out $be
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    $appdata = Join-Path $sandbox "appdata"; $local = Join-Path $sandbox "localappdata"; $vlog = Join-Path $sandbox "viewer.log"
    $diagDir = if ($DiagDir) { $DiagDir } else { $sandbox }
    $diagArgs = @("-DiagDir", ('"' + $diagDir + '"'))
    $timeout = [int](($leg.Minutes + 30) * 60)
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $godot + '"'),
        "-Exe", ('"' + $Exe + '"'), "-AppDataDir", ('"' + $appdata + '"'), "-LocalAppDataDir", ('"' + $local + '"'), "-TimeoutSeconds", "$timeout", "-HangSnapshotSeconds", "0") + $diagArgs +
        @("-LogFile", ('"' + $vlog + '"'), "-ArgLine", ('"--rendering-driver ' + $be + ' -- --automation"'))
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $argList
    Note "$be viewer started through godot.ps1 (pid $($p.Id)), $($leg.Minutes) minutes planned"
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = Wait-Until { @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 } 90 250
    if (-not $tf) { Note "$be : no automation token file"; $exit = 5; break }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $v = [pscustomobject]@{ Port = [int]$j.port; Token = [string]$j.token }
    $legClock = [System.Diagnostics.Stopwatch]::StartNew()
    $cycle = 0; $failures = 0; $walkAllowed = $true; $crashed = $false
    try {
        $failures += Login $v
        Note "$be in world"
        [void](Sample $v $be 0 "baseline" $walkAllowed $failures $cyclesCsv)
        while ($legClock.Elapsed.TotalMinutes -lt $leg.Minutes) {
            $cycle++
            $cycleClock = [System.Diagnostics.Stopwatch]::StartNew()
            if ($p.HasExited) { $crashed = $true; Note "$be viewer process has exited"; break }
            if (Test-WatchedOnline) { if ($walkAllowed) { Note "the watched avatar came online: standing and turning only from here" }; $walkAllowed = $false }
            Note "$be cycle $cycle"
            Start-Sleep -Seconds 60   # stand
            for ($y = 10; $y -le 360; $y += 10) { $null = Invoke-Api $v "orbit" @{ yaw_deg = $y }; Start-Sleep -Seconds 1 }
            [void](Sample $v $be $cycle "after-orbit" $walkAllowed $failures $stepsCsv)
            foreach ($choice in @("sunrise", "midday", "sunset", "midnight", "region")) { $null = Invoke-Api $v "sky" @{ choice = $choice }; Start-Sleep -Seconds 30 }
            [void](Sample $v $be $cycle "after-sky" $walkAllowed $failures $stepsCsv)
            if ($walkAllowed) { Key $v "W" $true; Start-Sleep -Seconds 6; Key $v "W" $false; Key $v "S" $true; Start-Sleep -Seconds 6; Key $v "S" $false; Start-Sleep -Seconds 4 }
            else { Start-Sleep -Seconds 16 }
            [void](Sample $v $be $cycle "after-walk" $walkAllowed $failures $stepsCsv)
            Logout $v
            Start-Sleep -Seconds 10
            $failures += Login $v
            Start-Sleep -Seconds 20
            $left = $CycleSeconds - $cycleClock.Elapsed.TotalSeconds
            if ($left -gt 0) { Start-Sleep -Seconds ([int]$left) }
            [void](Sample $v $be $cycle "cycle-end" $walkAllowed $failures $cyclesCsv)
        }
    } catch {
        Note "$be stopped by an error: $($_.Exception.Message)"
        $exit = 1
    } finally {
        try { Key $v "W" $false; Key $v "S" $false } catch { }
        try { Logout $v; Note "$be logged out" } catch { Note "$be logout failed: $($_.Exception.Message)"; $exit = 1 }
        try { $null = Invoke-Api $v "quit" } catch { }
        $p.WaitForExit(60000) | Out-Null
        Note "$be viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' }); cycles $cycle; login failures $failures; process gone early: $crashed"
        if ($crashed) { $exit = 1 }
    }
    if ($exit -ne 0) { break }
}
$loginUri = $null
Write-Output ""
Write-Output "SOAK-LIVE DONE - other work may run again (exit $exit)"
exit $exit

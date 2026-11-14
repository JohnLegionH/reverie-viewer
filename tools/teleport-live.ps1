# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# teleport-live.ps1 - the live check of the teleport progress view. Logs a test avatar in with the development export, sets the
# avatar's home to where it stands (the official Set Home to Here), then teleports and watches the teleport state every quarter
# second, with a picture during and after each one:
#   same region      - the progress view never shows; the camera sits behind the avatar; a walk key moves it
#   neighbour region - the progress view closes after the arrival; the same afterwards
#   home             - the same (only when the region accepted the home)
#   logout, login at home - the avatar is in the scene, the camera behind it, a walk key moves it
#   home again, from home - a teleport home inside the region closes at once
#   a region that does not exist - the progress closes and the failure notice shows
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\teleport-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#     [-NeighbourRuns <n>] [-SkipHome]
# For every teleport that lands in another region the report gives close_after_movement_s: the time from the agent's movement message
# (the arrival counter going up) until the progress view closes. The official arrival delay is 2 s.
#
# Settings come from the parameters or the environment as in env-live.ps1. -Start must name the region (a uri: start location), because
# a teleport inside the region is asked for by its name. Run it under a live-login claim and an avatar claim on the account.
# Output: teleport-report.json in the sandbox, pictures in -ShotsDir. Exit: 0 all passed, 1 a step failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [int]$NeighbourRuns = 1,       # how many teleports to a neighbouring region; more than one also returns to the start region and teleports inside it again
    [switch]$SkipHome              # leave out the set-home and home cases (an account whose parcels refuse a home)
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $ShotsDir -PathType Container)) { $problems.Add("-ShotsDir must be an existing folder") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start -or $Start -notmatch 'uri:([^&]+)') { $problems.Add("-Start (or NV_LIVE_START) must be a uri: start location naming the region") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
$regionName = [uri]::UnescapeDataString($Matches[1])

function Invoke-Raw([int]$port, [string]$command, [string]$token, [string]$body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nAuthorization: Bearer $token`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $r = New-Object System.IO.StreamReader($s)
        $all = $r.ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Parse([string]$json) { try { return $json | ConvertFrom-Json } catch { return $null } }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

$script:port = 0; $script:token = ""; $script:proc = $null
function Call([string]$command, $body = @{}) { Parse (Invoke-Raw $script:port $command $script:token ($body | ConvertTo-Json -Compress)).Body }
function Shot([string]$name) { [void](Call "screenshot" @{ dir = $ShotsDir; name = $name }); $script:report.shots += $name; Note "picture $name" }

# starts the viewer and logs in at $startLocation; returns the observe reply once the avatar is in the world and the terrain is complete
function Start-Session([string]$startLocation, [string]$sandboxName) {
    $dir = Join-Path $Sandbox $sandboxName
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$dir`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    Note "viewer started through godot.ps1, pid $($script:proc.Id)"
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $script:port = [int]$j.port; $script:token = [string]$j.token
    $login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $startLocation } | ConvertTo-Json -Compress)
    Note "login requested ($sandboxName): $($login.Status)"
    $stable = 0; $last = -1; $o = $null
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $o = Call "observe"
        if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
            if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
            $last = $o.world.objects
            if ($stable -ge 3) { return $o }
        }
    }
    return $null
}
function Stop-Session {
    $after = Call "session" @{ action = "logout" }
    Note "logout: ended=$($after.ended)"
    Start-Sleep -Seconds 3
    [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
    return [bool]$after.ended
}

# the camera sits behind the avatar and close, and a walk key moves the avatar; returns a record
function Check-Camera-And-Walk {
    $o = Call "observe"
    $cam = Call "orbit" @{ op = "state" }
    $r = [ordered]@{ camera_distance_m = $null; camera_behind = $false; walked_m = 0.0; walk_key_moves = $false }
    if ($o -and $o.self -and $o.self.drawn_soles -and $cam -and $cam.eye) {
        $soles = $o.self.drawn_soles; $eye = $cam.eye
        $dx = $eye[0] - $soles[0]; $dy = $eye[1] - $soles[1]; $dz = $eye[2] - $soles[2]
        $yaw = [double]$o.self.drawn_heading_deg * [math]::PI / 180
        $r.camera_distance_m = [math]::Round([math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz), 1)
        $r.camera_behind = (($dx * [math]::Cos($yaw) + $dy * [math]::Sin($yaw)) -lt 0) -and ($r.camera_distance_m -lt 20)
        $before = $o.self.region_position
        [void](Invoke-Raw $script:port "keys" $script:token '{"key":"W","down":true}')
        Start-Sleep -Seconds 3
        [void](Invoke-Raw $script:port "keys" $script:token '{"key":"W","down":false}')
        Start-Sleep -Milliseconds 800
        $o3 = Call "observe"
        $after = $o3.self.region_position
        if ($before -and $after) { $r.walked_m = [math]::Round([math]::Sqrt([math]::Pow($after[0] - $before[0], 2) + [math]::Pow($after[1] - $before[1], 2)), 1) }
        $r.walk_key_moves = ($r.walked_m -gt 1.0)
    }
    return $r
}

# starts a teleport with $send, then reads the teleport state every quarter second until it is over (or $maxSeconds pass); returns the trace and verdicts
function Watch-Teleport([string]$name, [scriptblock]$send, [bool]$expectFailure = $false, $expectProgressShown = $null, [double]$maxSeconds = 45) {
    $t0 = $clock.Elapsed.TotalSeconds
    $trace = New-Object System.Collections.Generic.List[string]
    $sentReply = & $send
    Note "$name sent: $(if ($sentReply) { ($sentReply | ConvertTo-Json -Compress) } else { 'no reply' })"
    $everShown = $false; $shotDuring = $false; $final = $null; $lastKey = ""; $overAt = $null; $idleSeen = $false
    $arrivedAt = $null; $closedAt = $null
    $arrivalsAtStart = [int](Call "teleport" @{ action = "status" }).arrivals
    while (($clock.Elapsed.TotalSeconds - $t0) -lt $maxSeconds) {
        $s = Call "teleport" @{ action = "status" }
        $t = [math]::Round($clock.Elapsed.TotalSeconds - $t0, 2)
        if ($s) {
            $shown = $s.stage -in @("Requested", "Active", "Arriving")
            if ($shown) { $everShown = $true }
            $key = "{0}|{1}|{2}|{3}" -f $s.stage, $s.progress, $s.arrivals, $s.failure
            if ($null -eq $arrivedAt -and $s.arrivals -gt $arrivalsAtStart -and $s.stage -ne "Idle") { $arrivedAt = $t }
            if ($null -ne $arrivedAt -and $null -eq $closedAt -and $s.stage -eq "Idle") { $closedAt = $t }
            if ($key -ne $lastKey) { $trace.Add(("t={0} stage={1} progress={2} arrivals={3} failure={4}" -f $t, $s.stage, $s.progress, $s.arrivals, $s.failure)); $lastKey = $key }
            if (-not $shotDuring -and $t -ge 0.7) { Shot ($name + "_during"); $shotDuring = $true }
            $final = $s
            if ($s.stage -eq "Failed") { if ($null -eq $overAt) { $overAt = $t }; break }
            if ($s.stage -eq "Idle") { $idleSeen = $true; if ($s.arrivals -gt 0 -or $t -ge 3) { if ($null -eq $overAt) { $overAt = $t }; break } }
        }
        if ($s -and $s.stage -eq "Arriving") { Start-Sleep -Milliseconds 40 } else { Start-Sleep -Milliseconds 250 }
    }
    Start-Sleep -Seconds 4   # the region's content settles
    Shot ($name + "_after")
    $final = Call "teleport" @{ action = "status" }
    $v = [ordered]@{
        name = $name; trace = $trace.ToArray(); over_after_s = $overAt; progress_ever_shown = $everShown
        final_stage = [string]$final.stage; failure = [string]$final.failure; arrivals = $final.arrivals
        close_after_movement_s = $(if ($null -ne $arrivedAt -and $null -ne $closedAt) { [math]::Round($closedAt - $arrivedAt, 2) } else { $null })
    }
    if ($expectFailure) { $v.pass = ($final.stage -eq "Failed") -and [bool]$final.failure }
    else {
        $closed = $final.stage -eq "Idle"
        $v.pass = $closed -and (($null -eq $expectProgressShown) -or ($everShown -eq $expectProgressShown))
        $cw = Check-Camera-And-Walk
        $v.camera_distance_m = $cw.camera_distance_m; $v.camera_behind = $cw.camera_behind; $v.walked_m = $cw.walked_m; $v.walk_key_moves = $cw.walk_key_moves
        $v.pass = $v.pass -and $cw.camera_behind -and $cw.walk_key_moves
    }
    Note ("{0}: {1}  trace: {2}" -f $name, $(if ($v.pass) { "PASS" } else { "FAIL" }), ($trace -join " ; "))
    $script:report.steps += $v
    return $v
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$report = [ordered]@{ account = $Account; home_set = $false; home_notices = @(); steps = @(); shots = @(); notes = @(); completed = $false }
Write-Output "LIVE TELEPORT RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"

$o = Start-Session $Start "run1"
if (-not $o) { Note "the world did not come up"; [void](Stop-Session); exit 5 }
Note "in world, settled"
Start-Sleep -Seconds 6

# Part 2: the official Set Home to Here (the region answers with a notice). A parcel may refuse it, so other spots in the region are tried.
$p = $o.self.region_position
$homeSpots = @(@([double]$p[0], [double]$p[1], [double]$p[2]), @(40.0, 40.0, [double]$p[2] + 5), @(128.0, 128.0, [double]$p[2] + 5), @(216.0, 40.0, [double]$p[2] + 5), @(40.0, 216.0, [double]$p[2] + 5), @(216.0, 216.0, [double]$p[2] + 5))
$spotIndex = 0
$kinds = @("HomeNotAllowed:skipped")
foreach ($spot in $(if ($SkipHome) { @() } else { $homeSpots })) {
    if ($spotIndex -gt 0) {
        [void](Call "teleport" @{ action = "region"; region = $regionName; x = $spot[0]; y = $spot[1]; z = $spot[2] })
        Start-Sleep -Seconds 6
    }
    $spotIndex++
    $sh = Call "teleport" @{ action = "set_home" }
    Start-Sleep -Seconds 4
    $st = Call "teleport" @{ action = "status" }
    $kinds = @($st.notices | ForEach-Object { "{0}:{1}" -f $_.kind, $_.key })
    Note ("set home at spot {0}: sent {1}; notices: {2}" -f $spotIndex, $sh.sent, ($kinds -join ","))
    $report.home_notices += ("spot{0}=" -f $spotIndex) + ($kinds -join ",")
    if (-not ($kinds -match "HomeNotAllowed")) { break }
}
$report.home_spot = $spotIndex
$homeAccepted = (-not $SkipHome) -and -not ($kinds -match "HomeNotAllowed")

# 1. same region: a spot 25 m from where the avatar stands, kept inside the region
$o = Call "observe"; $p = $o.self.region_position
$tx = [math]::Min(230.0, [math]::Max(25.0, [double]$p[0] + 25.0)); $ty = [math]::Min(230.0, [math]::Max(25.0, [double]$p[1]))
$sameRegion = Watch-Teleport "same_region" { Call "teleport" @{ action = "region"; region = $regionName; x = $tx; y = $ty; z = [double]$p[2] + 2.0 } } $false $false

# 2. a neighbour region: the map is opened so it holds the neighbours, then the first one found is chosen by grid offset
function Teleport-To-Neighbour([string]$stepName) {
    [void](Call "map" @{ action = "open" })
    [void](Call "map" @{ action = "centre" })
    $found = $false
    for ($i = 0; $i -lt 15 -and -not $found; $i++) {
        Start-Sleep -Seconds 2
        $m = Call "map" @{ action = "state" }
        if ($m.regions -gt 1) { $found = $true }
    }
    Note "map holds $($m.regions) regions"
    return Watch-Teleport $stepName {
        foreach ($off in @("1,0", "-1,0", "0,1", "0,-1", "1,1", "-1,-1", "1,-1", "-1,1", "any")) {
            $r = Call "map" @{ action = "neighbour"; name = $off }
            if ($r -and $r.found) { return $r }
        }
        return $null
    } $false $true
}
$neighbour = $null
for ($n = 1; $n -le [math]::Max(1, $NeighbourRuns); $n++) {
    $neighbour = Teleport-To-Neighbour $(if ($NeighbourRuns -le 1) { "neighbour_region" } else { "neighbour_region_$n" })
}
if ($NeighbourRuns -gt 1) {
    # back to the start region by its name, then a teleport inside it: the progress view must not show
    $back = Watch-Teleport "return_to_start_region" { Call "teleport" @{ action = "region"; region = $regionName; x = 128.0; y = 128.0; z = 60.0 } } $false $true 60
    $o = Call "observe"; $p = $o.self.region_position
    $tx = [math]::Min(230.0, [math]::Max(25.0, [double]$p[0] + 25.0)); $ty = [math]::Min(230.0, [math]::Max(25.0, [double]$p[1]))
    [void](Watch-Teleport "same_region_again" { Call "teleport" @{ action = "region"; region = $regionName; x = $tx; y = $ty; z = [double]$p[2] + 2.0 } } $false $false)
}

# 3. home (only when the region accepted the home above)
$homeStep = $null
$homeTried = Call "teleport" @{ action = "status" }
if ($homeAccepted) {
    $homeStep = Watch-Teleport "home" { Call "teleport" @{ action = "home" } } $false $null 60
    $report.home_set = [bool]$homeStep.pass
}

# 4. a region that does not exist
$missing = Watch-Teleport "missing_region" { Call "teleport" @{ action = "region"; region = "Nonexistent Region Zz9"; x = 128; y = 128; z = 30 } } $true $true 30

$ended1 = Stop-Session

# 5. log in at home, and teleport home again from home (inside the region)
if ($report.home_set) {
    $o2 = Start-Session "home" "run2"
    if ($o2) {
        Start-Sleep -Seconds 5
        $cw = Check-Camera-And-Walk
        $v = [ordered]@{ name = "login_at_home"; avatar_in_scene = [bool]$o2.self; camera_distance_m = $cw.camera_distance_m; camera_behind = $cw.camera_behind; walked_m = $cw.walked_m; walk_key_moves = $cw.walk_key_moves }
        $v.pass = $v.avatar_in_scene -and $cw.camera_behind -and $cw.walk_key_moves
        Shot "login_at_home"
        Note ("login_at_home: {0}" -f $(if ($v.pass) { "PASS" } else { "FAIL" }))
        $report.steps += $v
        [void](Watch-Teleport "home_from_home" { Call "teleport" @{ action = "home" } } $false $null 60)
        [void](Stop-Session)
    } else { $report.notes += "the login at home did not come up"; [void](Stop-Session) }
} else { $report.notes += "no valid home was set: the home cases were skipped" }

$report.completed = $true
$report.all_passed = (@($report.steps | Where-Object { -not $_.pass }).Count -eq 0)
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "teleport-report.json") -Encoding utf8
Write-Output "LIVE TELEPORT RUN DONE - other work may run again"
if ($report.all_passed) { exit 0 } else { exit 1 }

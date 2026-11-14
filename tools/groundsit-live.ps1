# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# groundsit-live.ps1 - the live ground-sit and body check. Logs a test avatar in with the development export, saves the numbers the viewer draws her body with, takes her to plain terrain found in its own terrain and object data (any region the session holds), sits her on the
# ground with the constrained ground-sit animation, reads where the seat is drawn against the ground (the report's drawn_seat), saves a picture from
# the side, and stands her up.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\groundsit-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# The avatar body is drawn only when a character folder is given (NV_CHARACTER_DIR or --character=); without one the run is BLOCKED, since every drawn_* reading would be empty.
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: groundsit-report.json and groundsit-report.txt in the sandbox, pictures in -ShotsDir.
# Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [string]$AgentId = "",
    [int]$StartSeconds = 60
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
. (Join-Path $PSScriptRoot "aim-lib.ps1")
Assert-ClaimGranted
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $ShotsDir -PathType Container)) { $problems.Add("-ShotsDir must be an existing folder") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $env:NV_CHARACTER_DIR -or -not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR -PathType Container)) { $problems.Add("NV_CHARACTER_DIR must name the character folder: without it no avatar body is drawn and the drawn_* readings are empty") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

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

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE GROUND-SIT RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started through godot.ps1, pid $($p.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token
$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$stable = 0; $last = -1; $o = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
}
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-48} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Cam([string]$op, [double]$value = 0) { Parse (Invoke-Raw $port "orbit" $token (@{ op = $op; value = $value } | ConvertTo-Json -Compress)).Body }
function Dist($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2) + [math]::Pow($a[2] - $b[2], 2)) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

function In([hashtable]$b) { Parse (Invoke-Raw $port "input" $token ($b | ConvertTo-Json -Compress)).Body }
function Key([string]$k, [bool]$down, [bool]$shift = $false, [bool]$alt = $false, [bool]$ctrl = $false) { In @{ kind = "key"; key = $k; down = $down; shift = $shift; alt = $alt; ctrl = $ctrl } }
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$alt = $false, [bool]$ctrl = $false, [bool]$shift = $false) {
    In @{ kind = "mouse"; button = $button; down = $down; x = $x; y = $y; alt = $alt; ctrl = $ctrl; shift = $shift }
}
function Pos() { $ob = Parse (Invoke-Raw $port "observe" $token).Body; return @($ob.self.region_position) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
function Turn([double]$from, [double]$to) { $d = $to - $from; while ($d -gt 180) { $d -= 360 }; while ($d -lt -180) { $d += 360 }; return $d }
function Drag([double]$x0, [double]$y0, [double]$dx, [double]$dy, [bool]$alt = $false, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](Mouse "none" $false $x0 $y0 $alt $ctrl $shift)
    [void](Mouse "left" $true $x0 $y0 $alt $ctrl $shift)
    $steps = 10
    for ($i = 1; $i -le $steps; $i++) { [void](Mouse "none" $false ($x0 + $dx * $i / $steps) ($y0 + $dy * $i / $steps) $alt $ctrl $shift); Start-Sleep -Milliseconds 60 }
    $s = In @{ kind = "state" }
    [void](Mouse "left" $false ($x0 + $dx) ($y0 + $dy) $alt $ctrl $shift)
    Start-Sleep -Milliseconds 300
    return $s
}


function Obs() { Parse (Invoke-Raw $port "observe" $token).Body }
function IState() { In @{ kind = "state" } }
function Interact([hashtable]$b) { Parse (Invoke-Raw $port "interact" $token ($b | ConvertTo-Json -Compress)).Body }
function Playing($ob) { @($ob.self.playing) -join "," }

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 5
    # keep the avatar on the ground and the keyboard with the world
    for ($k = 0; $k -lt 30; $k++) { $ob = Obs; if (-not $ob.self.flying) { break }; [void](Key "C" $true); Start-Sleep -Milliseconds 700 }
    [void](Key "C" $false); Start-Sleep -Seconds 2

    # ---- F. the numbers the viewer draws her body with (visual-parameter weights, head and face joints in the head frame, morph fingerprints): no picture, no fill check.
    #         The body must be drawn first; the dump is taken on a frame where the head's turn and tilt are under 5 degrees, twice, so the two can be compared ----
    $bodyUp = $false
    for ($k = 0; $k -lt 60 -and -not $bodyUp; $k++) { $ob = Obs; $bodyUp = [bool]($ob.self.avatar_body.built -and $ob.self.drawn_head); if (-not $bodyUp) { Start-Sleep -Seconds 1 } }
    $report.avatar_body = $ob.self.avatar_body
    Check "F the avatar body is drawn (the drawn_* readings are there)" $bodyUp ("avatar_body {0}" -f ($ob.self.avatar_body | ConvertTo-Json -Compress))
    $dumps = @()
    for ($try = 0; $try -lt 30 -and $dumps.Count -lt 2 -and $bodyUp; $try++) {
        $d = (Obs).self.shape_dump
        if ($d -and @($d.head_turn_tilt_roll_deg).Count -ge 2 -and [math]::Abs([double]$d.head_turn_tilt_roll_deg[0]) -lt 5 -and [math]::Abs([double]$d.head_turn_tilt_roll_deg[1]) -lt 5) { $dumps += ,$d; Start-Sleep -Seconds 2 }
        else { Start-Sleep -Milliseconds 800 }
    }
    if ($dumps.Count -gt 0) {
        $dumps[0] | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Sandbox "face-dump-live.json") -Encoding utf8
        $report.face_dump_file = "face-dump-live.json"; $report.face_dump_hover = $dumps[0].hover
        $report.face_dump_second_matches_first = ($dumps.Count -ge 2) -and (($dumps[0] | ConvertTo-Json -Depth 12 -Compress) -eq ($dumps[1] | ConvertTo-Json -Depth 12 -Compress))
    }
    Check "F face: the live dump is saved" ($dumps.Count -gt 0) ("{0} dump(s); weights {1}; two dumps equal: {2}" -f $dumps.Count, $(if ($dumps.Count) { $dumps[0].weight_count } else { "-" }), $report.face_dump_second_matches_first)

    # ---- 0. the floor she landed on (the login puts her on a built floor): sit on it. The official ground there is the foot plane the region sends with her,
    #         less the viewer's 5 cm allowance for a plane's error, so the seat is expected on the floor within that allowance (1 cm more is allowed for the measurement) ----
    $ob = Obs
    $floorSoles = [double]$ob.self.drawn_soles[2]
    $floorTerrain = if ($null -ne $ob.self.terrain_z) { [double]$ob.self.terrain_z } else { $null }
    $onFloor = ($null -ne $floorTerrain) -and ($floorSoles - $floorTerrain -gt 0.3)
    Note ("landing spot: soles z {0:N3}, terrain z {1}, on a built floor {2}" -f $floorSoles, $floorTerrain, $onFloor)
    $report.landing_floor = [ordered]@{ soles_z = $floorSoles; terrain_z = $floorTerrain; built_floor = $onFloor }
    if ($onFloor) {
        Shot "groundsit_floor_00_standing"
        [void](Interact @{ action = "sit_ground" })
        Start-Sleep -Seconds 7
        $ob = Obs; $is = Interact @{ action = "state" }
        $floorSeat = if ($ob.self.drawn_seat) { [double]$ob.self.drawn_seat[2] } else { $null }
        # the floor is the plane the region sends with her (upright normal: its offset is the floor height); her standing soles, taken from the stated position and body size, can sit above it
        $fp = $ob.self.foot_plane
        $floorRef = if ($fp -and [double]$fp[2] -gt 0.99) { [double]$fp[3] } else { $floorSoles }
        $floorGap = if ($null -ne $floorSeat) { $floorSeat - $floorRef } else { $null }
        Note ("floor sit: playing {0}; sitting {1}; seat z {2}; floor (the foot plane) z {3:N3}; gap {4}" -f (Playing $ob), $is.sitting, $floorSeat, $floorRef, $floorGap)
        $report.landing_floor.seat_z = $floorSeat; $report.landing_floor.gap_m = $floorGap; $report.landing_floor.playing = (Playing $ob); $report.landing_floor.limbs = $ob.self.drawn_limbs; $report.landing_floor.foot_plane = $ob.self.foot_plane; $report.landing_floor.hover = $report.face_dump_hover; $report.landing_floor.seat = $ob.self.drawn_seat
        $hd = [double]$ob.self.drawn_heading_deg
        [void](Parse (Invoke-Raw $port "orbit" $token (@{ yaw_deg = ($hd + 90); pitch_deg = 0; zoom = 2.5 } | ConvertTo-Json -Compress)).Body)
        Start-Sleep -Seconds 2
        Shot "groundsit_floor_01_seated_side"
        Check "0 floor sit: she plays the sit and Stand shows" (($ob.self.playing -contains "sit_ground") -and $is.sitting -eq $true) ("playing {0}; sitting {1}" -f (Playing $ob), $is.sitting)
        Check "0 floor sit: her seat is on the floor plane within 5 cm (plus 1 cm measuring)" ($null -ne $floorGap -and [math]::Abs($floorGap) -le 0.06) ("seat {0} m from the floor" -f $floorGap)
        [void](Interact @{ action = "stand" })
        for ($w = 0; $w -lt 25; $w++) { Start-Sleep -Seconds 1; $ob = Obs; $is = Interact @{ action = "state" }; if (($ob.self.playing -notcontains "sit_ground") -and $is.sitting -eq $false) { break } }; Note ("stood after {0} s" -f ($w + 1))
        $ob = Obs; $is = Interact @{ action = "state" }
        Check "0 floor sit: stand: she stands" (($ob.self.playing -notcontains "sit_ground") -and $is.sitting -eq $false) ("playing {0}; sitting {1}" -f (Playing $ob), $is.sitting)
    }

    # ---- 1. plain ground: land at least a metre above the water, level, with no object within 3 m, found in the viewer's own terrain and object data;
    #         she is taken there by a teleport inside the region (the viewer has no flight-to-a-point driver) and lets herself down ----
    $ob = Obs
    $found = $null
    $plainReply = Invoke-Raw $port "plain_ground" $token (@{ x = [double]$ob.self.region_position[0]; y = [double]$ob.self.region_position[1]; min_above_water = 1; clear_radius = 3; prefer = @(($env:NV_PLAIN_GROUND_PREFER -split ",") | Where-Object { $_ }) } | ConvertTo-Json -Compress)
    Note ("plain_ground answered {0}: {1}" -f $plainReply.Status, $(if ($plainReply.Body.Length -gt 400) { $plainReply.Body.Substring(0, 400) } else { $plainReply.Body }))
    $plain = Parse $plainReply.Body
    if ($plain -and $plain.best) { $found = @([double]$plain.best.x, [double]$plain.best.y, [double]$plain.best.z); $foundGrid = @([int]$plain.best.grid[0], [int]$plain.best.grid[1]) }
    $report.plain_ground = $plain
    foreach ($r in @($plain.regions | Where-Object { $_ })) { Note ("plain ground, region {0},{1}: places {2}, no terrain {3}, too low {4}, not level {5}, object near {6}, plain {7}" -f $r.grid[0], $r.grid[1], $r.places, $r.no_terrain, $r.too_low, $r.not_level, $r.object_near, $r.plain) }
    Note ("plain ground: {0} (water {1}, objects {2})" -f $(if ($found) { "region {0},{1} at {2:N1}, {3:N1}, land {4:N2}" -f $foundGrid[0], $foundGrid[1], $found[0], $found[1], $found[2] } else { "none found" }), $plain.water_height, $plain.objects)
    Check "1 plain ground was found" ($null -ne $found) "terrain at least 1 m above the water, level, no object within 3 m, in any region the session holds"
    $arrived = $false
    if ($found) {
        $tp = Parse (Invoke-Raw $port "teleport" $token (@{ action = "region"; grid_x = $foundGrid[0]; grid_y = $foundGrid[1]; x = $found[0]; y = $found[1]; z = ($found[2] + 1.5) } | ConvertTo-Json -Compress)).Body
        Note "teleport to the cell sent: $($tp | ConvertTo-Json -Compress -Depth 3)"
        Start-Sleep -Seconds 10
        for ($k = 0; $k -lt 60; $k++) { $ob = Obs; if ($ob.self -and -not $ob.self.flying -and [math]::Abs([double]$ob.self.region_position[0] - $found[0]) -lt 3 -and [math]::Abs([double]$ob.self.region_position[1] - $found[1]) -lt 3) { $arrived = $true; break }; Start-Sleep -Seconds 1 }
    }    $ob = Obs
    $there = $arrived -and $found -and [math]::Sqrt([math]::Pow([double]$ob.self.region_position[0] - $found[0], 2) + [math]::Pow([double]$ob.self.region_position[1] - $found[1], 2)) -lt 3
    Check "1 she is on that ground" ([bool]$there) ("at {0}, {1}" -f $ob.self.region_position[0], $ob.self.region_position[1])
    Start-Sleep -Seconds 2
    $ob = Obs
    $groundZ = if ($null -ne $ob.self.terrain_z) { [double]$ob.self.terrain_z } else { [double]$ob.self.drawn_soles[2] }
    $report.start = [ordered]@{ stated_z = $ob.self.region_position[2]; soles_z = $ob.self.drawn_soles[2]; terrain_z = $ob.self.terrain_z }
    $report.self_standing = $ob.self
    Shot "groundsit_00_standing"

    # ---- 2. sit on the ground: the seat is on it ----
    $chose = [bool](Interact @{ action = "sit_ground" }).sent
    Note "Sit Here sent: $chose"
    Start-Sleep -Seconds 7
    $ob = Obs; $is = Interact @{ action = "state" }
    $seatZ = if ($ob.self.drawn_seat) { [double]$ob.self.drawn_seat[2] } else { $null }
    $seatGround = if ($null -ne $ob.self.terrain_under_seat_z) { [double]$ob.self.terrain_under_seat_z } else { $groundZ }
    $gap = if ($null -ne $seatZ) { $seatZ - $seatGround } else { $null }
    Note ("ground sit: playing {0}; sitting {1}; seat z {2}; terrain z {3:N3}; gap {4}" -f (Playing $ob), $is.sitting, $seatZ, $seatGround, $gap)
    $report.self_seated = $ob.self
    $report.ground_sit = [ordered]@{ limbs = $ob.self.drawn_limbs; seat = $ob.self.drawn_seat; playing = (Playing $ob); sitting = $is.sitting; seat_z = $seatZ; terrain_z = $seatGround; standing_terrain_z = $groundZ; gap_m = $gap; head = $ob.self.drawn_head }
    # a picture from the side
    $hd = [double]$ob.self.drawn_heading_deg
    [void](Parse (Invoke-Raw $port "orbit" $token (@{ yaw_deg = ($hd + 90); pitch_deg = 0; zoom = 2.5 } | ConvertTo-Json -Compress)).Body)
    Start-Sleep -Seconds 2
    Shot "groundsit_01_seated_side"
    Check "2 ground sit: she plays the sit and Stand shows" (($ob.self.playing -contains "sit_ground") -and $is.sitting -eq $true) ("playing {0}; sitting {1}" -f (Playing $ob), $is.sitting)
    Check "2 ground sit: her seat is on the ground within 5 cm" ($null -ne $gap -and [math]::Abs($gap) -le 0.05) ("seat {0} m from the ground" -f $gap)

    # ---- 3. stand ----
    [void](Interact @{ action = "stand" })
    for ($w = 0; $w -lt 25; $w++) { Start-Sleep -Seconds 1; $ob = Obs; $is = Interact @{ action = "state" }; if (($ob.self.playing -notcontains "sit_ground") -and $is.sitting -eq $false) { break } }; Note ("stood after {0} s" -f ($w + 1))
    $ob = Obs; $is = Interact @{ action = "state" }
    Check "3 stand: she stands" (($ob.self.playing -notcontains "sit_ground") -and $is.sitting -eq $false) ("playing {0}; sitting {1}" -f (Playing $ob), $is.sitting)
    $report.completed = $true
}
$report.checks = $checks
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Sandbox "groundsit-report.json") -Encoding utf8
Write-Output "LIVE GROUND-SIT RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# steer-live.ps1 - left-drag steering through the engine's own input path (the automation input command), against the official viewer's
# figures: a drag on the own avatar turns it by -dx * 360 / view width degrees and pitches the agent by -dy * 360 / view width degrees
# with y up (lltoolfocus.cpp handleHover with mouse steering); a drag on open ground keeps the grabbed point under the pointer
# (lltoolpie.cpp steerCameraWithMouse). Also: a walk key levels the pitch again, Esc clears it.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\steer-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under the claims a login needs (an avatar claim on the account; the target's own claim). Output: steer-report.json in the
# sandbox, pictures in -ShotsDir. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [int]$StartSeconds = 60
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
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
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" -and $_.CommandLine -match [regex]::Escape($Sandbox) })
if ($viewers.Count -gt 0) { $problems.Add("a viewer for this sandbox is already running ($($viewers.Count))") }
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
Write-Output "LIVE STEER RUN STARTING - no other clients on account $Account until it is DONE"
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
Note "login requested: $($login.Status) $(if ($login.Status -ne 200) { $login.Body })"
$stable = 0; $last = -1; $o = $null
$deadline = if ($login.Status -eq 200) { (Get-Date).AddSeconds(150) } else { Get-Date }
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
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-56} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
$report = [ordered]@{ account = $Account; checks = $null; rows = $null; completed = $false }
$rows = New-Object System.Collections.Generic.List[object]

function In([hashtable]$b) { Parse (Invoke-Raw $port "input" $token ($b | ConvertTo-Json -Compress)).Body }
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down } }
# a movement key as a focused window's keyboard gives it (the automation keys command): a test window that has no focus ignores real keys
function Walk([string]$k, [bool]$down) { Parse (Invoke-Raw $port "keys" $token (@{ key = $k; down = $down } | ConvertTo-Json -Compress)).Body }
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y) { In @{ kind = "mouse"; button = $button; down = $down; x = $x; y = $y } }
function Pos() { $ob = Parse (Invoke-Raw $port "observe" $token).Body; return @($ob.self.region_position) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow([double]$a[0] - [double]$b[0], 2) + [math]::Pow([double]$a[1] - [double]$b[1], 2)) }
function Turn([double]$from, [double]$to) { $d = $to - $from; while ($d -gt 180) { $d -= 360 }; while ($d -lt -180) { $d += 360 }; return $d }
function Bearing($from, $to) { return [math]::Atan2([double]$to[1] - [double]$from[1], [double]$to[0] - [double]$from[0]) * 180 / [math]::PI }
function Elevation($from, $to) {
    $flat = [math]::Sqrt([math]::Pow([double]$to[0] - [double]$from[0], 2) + [math]::Pow([double]$to[1] - [double]$from[1], 2))
    return [math]::Atan2([double]$to[2] - [double]$from[2], $flat) * 180 / [math]::PI
}
function Snap() {
    $s = In @{ kind = "state" }
    return [ordered]@{ heading = [double]$s.heading_deg; pitch = [double]$s.avatar_pitch_deg; cam_yaw = (Bearing $s.eye $s.look); cam_pitch = (Elevation $s.eye $s.look)
        eye = $s.eye; look = $s.look; self_screen = $s.self_screen; view = $s.view }
}
function Row([string]$drag, $a, $b, [string]$what, [double]$official, [double]$measured) {
    $r = [ordered]@{ drag = $drag; heading_before = [math]::Round($a.heading, 2); heading_after = [math]::Round($b.heading, 2); pitch_before = [math]::Round($a.pitch, 2); pitch_after = [math]::Round($b.pitch, 2)
        cam_yaw_before = [math]::Round($a.cam_yaw, 2); cam_yaw_after = [math]::Round($b.cam_yaw, 2); cam_pitch_before = [math]::Round($a.cam_pitch, 2); cam_pitch_after = [math]::Round($b.cam_pitch, 2)
        quantity = $what; official = [math]::Round($official, 2); measured = [math]::Round($measured, 2); error = [math]::Round($measured - $official, 2) }
    $rows.Add($r)
    Check ("{0}: {1}" -f $drag, $what) ([math]::Abs($measured - $official) -le 2.0) ("official {0:N2}, measured {1:N2} deg" -f $official, $measured)
}
# a drag in 10 steps of pointer movement, the state read while the button is still held
function Drag([double]$x0, [double]$y0, [double]$dx, [double]$dy) {
    [void](Mouse "none" $false $x0 $y0)
    [void](Mouse "left" $true $x0 $y0)
    Start-Sleep -Milliseconds 150
    for ($i = 1; $i -le 10; $i++) { [void](Mouse "none" $false ($x0 + $dx * $i / 10) ($y0 + $dy * $i / 10)); Start-Sleep -Milliseconds 60 }
    $held = In @{ kind = "state" }
    [void](Mouse "left" $false ($x0 + $dx) ($y0 + $dy))
    Start-Sleep -Milliseconds 700
    return $held
}

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 3
    for ($k = 0; $k -lt 30; $k++) {
        $ob = Parse (Invoke-Raw $port "observe" $token).Body
        if (-not $ob.self.flying) { break }
        [void](Key "C" $true); Start-Sleep -Milliseconds 700
    }
    [void](Key "C" $false); Start-Sleep -Seconds 2
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800
    $s0 = Snap
    $w = [double]$s0.view[0]; $h = [double]$s0.view[1]
    Note ("view {0}x{1}, heading {2:N2}, avatar on screen {3}" -f $w, $h, $s0.heading, ($s0.self_screen -join ","))
    Shot "steer_01_start"
    # 3 ground between the camera and the avatar: the official viewer turns the other way there (lltoolpie.cpp startCameraSteering, mClockwise)
        $s4 = Snap; $me = Pos
    $near = $false; $pr = $null; $gx = 0; $gy = 0
    foreach ($fx in 0.5, 0.4, 0.6, 0.3, 0.7) {
        foreach ($gy0 in 530, 520, 510, 500) {
            $gx = $w * $fx; $gy = $gy0
            $pr = In @{ kind = "probe"; x = $gx; y = $gy }
            $near = $pr.probe_point -and (-not $pr.probe_own) -and ((Flat $s4.eye $pr.probe_point) -lt (Flat $s4.eye $me))
            if ($near) { break }
        }
        if ($near) { break }
    }
    Check "the ground pixel picks a point between the camera and the avatar" (($pr.probe_kind -ne "None") -and (-not $pr.probe_own) -and $near) ("{0} at {1}" -f $pr.probe_kind, ($pr.probe_point -join ","))
    $a = Snap; [void](Drag $gx $gy 200 0); $b = Snap
    Check "ground near, 200 px right turns the avatar right" ((Turn $a.heading $b.heading) -lt -2) ("{0:N2} deg" -f (Turn $a.heading $b.heading))

    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800
    # the keys walk before any drag (a baseline for the walk check below)
    [void](Invoke-Raw $port "typing" $token '{"action":"focus"}'); Start-Sleep -Milliseconds 300; [void](Invoke-Raw $port "typing" $token '{"action":"release"}'); Start-Sleep -Milliseconds 300
    $q0 = Pos; [void](Walk "UP" $true); Start-Sleep -Milliseconds 1000; [void](Walk "UP" $false); Start-Sleep -Milliseconds 600; $q1 = Pos
    $f0 = (Parse (Invoke-Raw $port "observe" $token).Body).self.focused
    Note ("diag: before any drag the Up key walked {0:N2} m; input focused {1}" -f (Flat $q0 $q1), $f0)
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800

    # 1 the own avatar: a pixel that picks it (its centre, then up the body)
    function FindSelf() {
        $now = Snap
        foreach ($up in 60, 40, 20, 0, 80, 100, -20, -40, -60, -80, 120, 140, 160) {
            if (-not $now.self_screen) { return $null }
            $px = [double]$now.self_screen[0]; $py = [double]$now.self_screen[1] - $up
            $pr = In @{ kind = "probe"; x = $px; y = $py }
            if ($pr.probe_own -and $py -gt 20 -and $py -lt $h - 130) { return @($px, $py) }
        }
        Write-Host ("no pixel picks the own avatar; its centre is at {0}" -f ($now.self_screen -join ","))
        return $null
    }
    $press = FindSelf
    Check "a pixel on the own avatar picks it" ($null -ne $press) ("at {0}" -f ($press -join ","))
    if ($press) {
        # 1a 200 px right: the avatar turns right by 200 * 360 / width, the camera with it
        $a = Snap; $held = Drag $press[0] $press[1] 200 0; $b = Snap
        Note ("diag: after the first drag input focused {0}" -f (Parse (Invoke-Raw $port "observe" $token).Body).self.focused)
        Check "own-avatar drag runs the camera tool with steering" ([bool]$held.camera_tool_steering) ("camera_tool {0}, steering {1}" -f $held.camera_tool, $held.camera_tool_steering)
        Row "own avatar, 200 px right" $a $b "heading change" (-200 * 360 / $w) (Turn $a.heading $b.heading)
        Row "own avatar, 200 px right" $a $b "camera yaw change" (-200 * 360 / $w) (Turn $a.cam_yaw $b.cam_yaw)
        Shot "steer_02_avatar_right"
        # 1b 200 px left: back again
        $a = Snap; [void](Drag $press[0] $press[1] -200 0); $b = Snap
        Row "own avatar, 200 px left" $a $b "heading change" (200 * 360 / $w) (Turn $a.heading $b.heading)
        # each press on the own avatar resets the agent's axes first (setFocusOnAvatar with reset_axes), so the pitch after is the drag's alone
        $press = FindSelf
        if ($press) {
            $a = Snap; [void](Drag $press[0] $press[1] 0 50); $b = Snap
            Row "own avatar, 50 px down" $a $b "agent pitch after" (50 * 360 / $w) $b.pitch
        } else { Check "own avatar, 50 px down" $false "no pixel on the avatar" }
        Check "own avatar, 50 px down: the camera rises and looks further down" (([double]$b.eye[2] -gt [double]$a.eye[2] + 0.1) -and ($b.cam_pitch -lt $a.cam_pitch - 2)) ("eye z {0:N2} -> {1:N2}, camera pitch {2:N2} -> {3:N2}" -f $a.eye[2], $b.eye[2], $a.cam_pitch, $b.cam_pitch)
        [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800
        $press = FindSelf
        # 1c 100 px up (window y up is a positive dy): the agent pitches up by 100 * 360 / width, the camera goes down and looks up;
        # then 50 px down. (The drags stay above the bottom bar: an injected pointer over a panel gives the panel its movement, which a
        # captured pointer held in place does not.)
        $a = Snap; [void](Drag $press[0] $press[1] 0 -100); $b = Snap
        Row "own avatar, 100 px up" $a $b "agent pitch change" (-100 * 360 / $w) ($b.pitch - $a.pitch)
        Row "own avatar, 100 px up" $a $b "heading change" 0 (Turn $a.heading $b.heading)
        Check "own avatar, 100 px up: the camera goes down and looks further up" (([double]$b.eye[2] -lt [double]$a.eye[2] - 0.2) -and ($b.cam_pitch -gt $a.cam_pitch + 2)) ("eye z {0:N2} -> {1:N2}, camera pitch {2:N2} -> {3:N2}" -f $a.eye[2], $b.eye[2], $a.cam_pitch, $b.cam_pitch)
        Shot "steer_03_avatar_up"
        # 1d a walk key held levels the pitch (resetView with the button up); Esc clears it
        [void](Invoke-Raw $port "typing" $token '{"action":"focus"}'); Start-Sleep -Milliseconds 300; [void](Invoke-Raw $port "typing" $token '{"action":"release"}'); Start-Sleep -Milliseconds 300
        $before = (Snap).pitch; $p0 = Pos
        [void](Walk "UP" $true); Start-Sleep -Milliseconds 700; $hold = In @{ kind = "state" }; Start-Sleep -Milliseconds 800; [void](Walk "UP" $false); Start-Sleep -Milliseconds 500
        Note ("diag walk: keys_held={0} owner={1} camera_tool={2} steer_pressed={3}" -f ($hold.keys_held -join ","), $hold.focus_owner, $hold.camera_tool, $hold.steer_pressed)
        $walked = (Snap).pitch; $p1 = Pos
        Check "walking levels the pitch" (([math]::Abs($before) -gt 3) -and ([math]::Abs($walked) -lt [math]::Abs($before) * 0.25)) ("pitch {0:N2} -> {1:N2} deg, walked {2:N2} m" -f $before, $walked, (Flat $p0 $p1))
        $press = FindSelf
        if ($press) { [void](Drag $press[0] $press[1] 0 -80) }; $mid = Snap
        [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800
        $esc = (Snap).pitch
        Check "Esc clears the pitch" (([math]::Abs($mid.pitch) -gt 3) -and ([math]::Abs($esc) -lt 0.01)) ("pitch {0:N2} -> {1:N2} deg" -f $mid.pitch, $esc)
    }

    # 2 open ground beyond the avatar: the grabbed point stays under the pointer (seen from the camera, the grabbed point and the point
    # under the pointer have the same bearing after the drag), and a drag to the right turns the avatar left
    $s2 = Snap; $me = Pos
    $gx = $w * 0.2; $gy = [double]$s2.self_screen[1] - 20
    $pr = In @{ kind = "probe"; x = $gx; y = $gy }
    $grab = $pr.probe_point
    $beyond = $grab -and ((Flat $s2.eye $grab) -gt (Flat $s2.eye $me) + 0.5)
    Check "the ground pixel picks a point beyond the avatar" (($pr.probe_kind -ne "None") -and (-not $pr.probe_own) -and $beyond) ("{0} at {1}" -f $pr.probe_kind, ($grab -join ","))
    $a = Snap; $held = Drag $gx $gy 200 0; $b = Snap
    Check "a ground drag steers (not the camera tool)" (($held.steering -eq $true) -and (-not $held.camera_tool)) ("steering {0}, camera_tool {1}" -f $held.steering, $held.camera_tool)
    $under = In @{ kind = "probe"; x = ($gx + 200); y = $gy }
    $offBy = Turn (Bearing $b.eye $grab) (Bearing $b.eye $under.probe_point)
    Row "ground beyond, 200 px right" $a $b "grabbed point's bearing from the camera vs the pointer's" 0 $offBy
    Check "ground beyond, 200 px right turns the avatar left" ((Turn $a.heading $b.heading) -gt 2) ("{0:N2} deg" -f (Turn $a.heading $b.heading))
    Shot "steer_04_ground_beyond_right"
    $report.completed = $true
}
$report.checks = $checks
$report.rows = $rows
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "steer-report.json") -Encoding utf8
Write-Output "LIVE STEER RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

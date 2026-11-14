# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# camera-live.ps1 - the live camera test. Logs a test avatar in with the development export and drives the camera through the
# viewer's automation API with the operations the mouse and keys use (orbit around and over, pan, wheel, reset, mouse-look), takes a
# picture after each, then walks with the camera following and measures the camera's step from one drawn frame to the next.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\camera-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: camera-report.json and camera-report.txt in the sandbox, pictures in -ShotsDir.
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
Write-Output "LIVE CAMERA RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
$report = [ordered]@{ account = $Account; checks = $null; walk = $null; completed = $false }

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 5
    $d0 = Cam "state"
    Shot "cam_01_default"
    Check "default view is behind the avatar" ($d0.focus_on_avatar -eq $true -and $d0.mode -eq "ThirdPerson") "focus_on_avatar=$($d0.focus_on_avatar) mode=$($d0.mode) distance=$($d0.distance)"

    $u = Cam "unlock"; Start-Sleep -Milliseconds 400
    Check "unlocking leaves the camera where it is" (($u.focus_on_avatar -eq $false) -and (Dist $u.eye $d0.eye) -lt 0.3) ("moved {0:N3} m" -f (Dist $u.eye $d0.eye))
    $a = Cam "orbit_around_deg" 90; Start-Sleep -Milliseconds 400; $a = Cam "state"; Shot "cam_02_orbit_around_90"
    Check "orbit around turns the camera about the avatar" ((Dist $a.eye $d0.eye) -gt 2.0 -and [math]::Abs($a.focus_distance - $u.focus_distance) -lt 0.05) ("eye moved {0:N2} m, distance {1:N2} -> {2:N2}" -f (Dist $a.eye $d0.eye), $u.focus_distance, $a.focus_distance)
    $v = Cam "orbit_over_deg" 30; Start-Sleep -Milliseconds 400; $v = Cam "state"; Shot "cam_03_orbit_over_30"
    Check "orbit over lifts the camera at the same distance" (($v.eye[2] - $a.eye[2]) -gt 0.5 -and [math]::Abs($v.focus_distance - $a.focus_distance) -lt 0.05) ("up {0:N2} m, distance {1:N2}" -f ($v.eye[2] - $a.eye[2]), $v.focus_distance)
    $pn = Cam "pan_left_m" 2; Start-Sleep -Milliseconds 400; $pn = Cam "pan_up_m" 1; Start-Sleep -Milliseconds 400; $pn = Cam "state"; Shot "cam_04_pan"
    Check "pan moves the view and keeps the distance" ((Dist $pn.look $v.look) -gt 2.0 -and [math]::Abs($pn.focus_distance - $v.focus_distance) -lt 0.05) ("focus moved {0:N2} m" -f (Dist $pn.look $v.look))
    $w = Cam "wheel" -3; Start-Sleep -Milliseconds 600; $w = Cam "state"; Shot "cam_05_zoom_in"
    Check "the wheel in brings the camera closer" ($w.focus_distance -lt $pn.focus_distance * 0.75) ("{0:N2} -> {1:N2} m" -f $pn.focus_distance, $w.focus_distance)
    $w2 = Cam "wheel" 6; Start-Sleep -Milliseconds 600; $w2 = Cam "state"; Shot "cam_06_zoom_out"
    Check "the wheel out takes it back" ($w2.focus_distance -gt $w.focus_distance * 1.5) ("{0:N2} -> {1:N2} m" -f $w.focus_distance, $w2.focus_distance)
    $r = Cam "reset"; Start-Sleep -Milliseconds 800; $r = Cam "state"; Shot "cam_07_reset"
    Check "Esc puts the default view back" (($r.focus_on_avatar -eq $true) -and [math]::Abs($r.zoom - 1) -lt 0.001 -and (Dist $r.eye $d0.eye) -lt 0.3) ("eye differs {0:N3} m, zoom {1}" -f (Dist $r.eye $d0.eye), $r.zoom)

    $m = Parse (Invoke-Raw $port "mouselook" $token '{"action":"enter"}').Body
    Start-Sleep -Milliseconds 600
    for ($i = 0; $i -lt 10; $i++) { [void](Invoke-Raw $port "mouselook" $token '{"action":"look","dx":12,"dy":0}'); Start-Sleep -Milliseconds 50 }
    $m2 = Parse (Invoke-Raw $port "mouselook" $token '{"action":"state"}').Body
    Shot "cam_08_mouselook"
    Check "mouse-look is entered and the mouse turns the avatar" (($m2.active -eq $true) -and [math]::Abs([double]$m2.heading_deg - [double]$m.heading_deg) -gt 5) ("heading {0} -> {1} degrees" -f $m.heading_deg, $m2.heading_deg)
    [void](Invoke-Raw $port "mouselook" $token '{"action":"leave"}'); Start-Sleep -Milliseconds 1200
    $l = Cam "state"; Shot "cam_09_back_in_third_person"
    Check "leaving mouse-look is third person behind the avatar" (($l.mode -eq "ThirdPerson") -and ($l.focus_on_avatar -eq $true)) "mode=$($l.mode)"

    # walk with the camera following: the camera's step per drawn frame. On the ground first (an avatar that logged in flying lands).
    for ($k = 0; $k -lt 30; $k++) {
        $ob = Parse (Invoke-Raw $port "observe" $token).Body
        if (-not $ob.self.flying) { break }
        [void](Invoke-Raw $port "keys" $token '{"key":"C","down":true,"control":false}'); Start-Sleep -Milliseconds 700
    }
    [void](Invoke-Raw $port "keys" $token '{"key":"C","down":false,"control":false}'); Start-Sleep -Seconds 2
    $ob = Parse (Invoke-Raw $port "observe" $token).Body
    Check "the avatar is on the ground before the walk" (-not [bool]$ob.self.flying) ("flying=" + $ob.self.flying)
    [void](Invoke-Raw $port "motion" $token '{"action":"start"}'); [void](Invoke-Raw $port "motion" $token '{"action":"take"}')
    [void](Invoke-Raw $port "keys" $token '{"key":"W","down":true,"control":false}')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()   # no picture during the measured walk: a picture stalls a frame
    while ($sw.Elapsed.TotalSeconds -lt 6) { Start-Sleep -Milliseconds 100 }
    $ob2 = Parse (Invoke-Raw $port "observe" $token).Body
    [void](Invoke-Raw $port "keys" $token '{"key":"W","down":false,"control":false}')
    $takeRaw = (Invoke-Raw $port "motion" $token '{"action":"take"}').Body
    Set-Content -LiteralPath (Join-Path $Sandbox "walk-motion.json") -Value $takeRaw -Encoding utf8
    $take = Parse $takeRaw
    [void](Invoke-Raw $port "motion" $token '{"action":"stop"}')
    # a second short walk for the picture
    Start-Sleep -Milliseconds 800
    [void](Invoke-Raw $port "keys" $token '{"key":"W","down":true,"control":false}'); Start-Sleep -Seconds 3; Shot "cam_10_walking"; [void](Invoke-Raw $port "keys" $token '{"key":"W","down":false,"control":false}')
    # self samples: [at, id, self, dx, dy, dz, sx, sy, sz, vx, vy, vz, updates, cx, cy, cz]
    $self = @($take.samples | Where-Object { $_[2] -eq $true -and $_.Count -ge 16 })
    function Stats($v) {
        $s = @($v | Sort-Object); $n = $s.Count; $m = ($s | Measure-Object -Average).Average
        $sd = [math]::Sqrt((($s | ForEach-Object { ($_ - $m) * ($_ - $m) }) | Measure-Object -Average).Average)
        [ordered]@{ n = $n; mean = [math]::Round($m, 2); sd = [math]::Round($sd, 2); median = [math]::Round($s[[int]($n / 2)], 2); p95 = [math]::Round($s[[int]($n * 0.95)], 2); max = [math]::Round($s[-1], 2) }
    }
    $dts = New-Object System.Collections.Generic.List[double]; $cam = New-Object System.Collections.Generic.List[double]; $av = New-Object System.Collections.Generic.List[double]
    $camSpeed = New-Object System.Collections.Generic.List[double]; $avSpeed = New-Object System.Collections.Generic.List[double]
    for ($i = 1; $i -lt $self.Count; $i++) {
        if ([double]$self[$i][0] - [double]$self[0][0] -lt 1.5) { continue }
        $dt = [double]$self[$i][0] - [double]$self[$i - 1][0]
        $c = [math]::Sqrt([math]::Pow($self[$i][13] - $self[$i - 1][13], 2) + [math]::Pow($self[$i][14] - $self[$i - 1][14], 2) + [math]::Pow($self[$i][15] - $self[$i - 1][15], 2))
        $a = [math]::Sqrt([math]::Pow($self[$i][3] - $self[$i - 1][3], 2) + [math]::Pow($self[$i][4] - $self[$i - 1][4], 2) + [math]::Pow($self[$i][5] - $self[$i - 1][5], 2))
        $dts.Add($dt * 1000); $cam.Add($c * 1000); $av.Add($a * 1000)
        if ($dt -gt 0.001) { $camSpeed.Add($c / $dt); $avSpeed.Add($a / $dt) }
    }
    if ($cam.Count -gt 10) {
        $walk = [ordered]@{
            animations = @($ob2.self.playing); flying = [bool]$ob2.self.flying; frames = $cam.Count; frames_over_50ms = @($dts | Where-Object { $_ -gt 50 }).Count
            frame_ms = (Stats $dts); camera_step_mm = (Stats $cam); avatar_step_mm = (Stats $av); camera_speed_ms = (Stats $camSpeed); avatar_speed_ms = (Stats $avSpeed)
        }
        $report.walk = $walk
        Note ("walk: " + ($walk | ConvertTo-Json -Compress -Depth 4))
        Check "the walk was recorded on the ground" (-not $walk.flying) ("animations " + ($walk.animations -join "+"))
    } else { Check "walking: camera samples recorded" $false "only $($cam.Count) samples" }
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "camera-report.json") -Encoding utf8
Write-Output "LIVE CAMERA RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

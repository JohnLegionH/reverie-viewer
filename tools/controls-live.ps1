# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# controls-live.ps1 - the live controls test. Logs a test avatar in with the development export and sends keys and mouse events through the
# engine's own input path (the automation input command): the arrow keys, a left-drag on the avatar, Alt, Ctrl+Alt and Ctrl+Alt+Shift drags and Esc,
# and reads back what the avatar and the camera did.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\camera-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: controls-report.json and controls-report.txt in the sandbox, pictures in -ShotsDir.
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
Write-Output "LIVE CONTROLS RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 5
    # on the ground first (an avatar that logged in flying lands)
    for ($k = 0; $k -lt 30; $k++) {
        $ob = Parse (Invoke-Raw $port "observe" $token).Body
        if (-not $ob.self.flying) { break }
        [void](Key "C" $true); Start-Sleep -Milliseconds 700
    }
    [void](Key "C" $false); Start-Sleep -Seconds 2
    [void](Invoke-Raw $port "typing" $token '{"action":"focus"}'); Start-Sleep -Milliseconds 400; [void](Invoke-Raw $port "typing" $token '{"action":"release"}'); Start-Sleep -Milliseconds 300   # the window has the keyboard, as it has when a person uses it
    $st = In @{ kind = "state" }
    $cx =[double]$st.view[0] / 2; $cy = [double]$st.view[1] / 2
    Note ("view {0}x{1}, keyboard owner {2}, heading {3}" -f $st.view[0], $st.view[1], $st.focus_owner, $st.heading_deg)
    Shot "ctl_01_start"

    # 1 the arrow keys
    $p0 = Pos; [void](Key "UP" $true); Start-Sleep -Milliseconds 1800; $held = In @{ kind = "state" }; [void](Key "UP" $false); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "Up arrow moves the avatar forward" ((Flat $p0 $p1) -gt 1.0 -and ($held.keys_held -contains "UP")) ("moved {0:N2} m, held {1}" -f (Flat $p0 $p1), ($held.keys_held -join ","))
    $h0 = [double](In @{ kind = "state" }).heading_deg
    [void](Key "LEFT" $true); Start-Sleep -Milliseconds 700; [void](Key "LEFT" $false); Start-Sleep -Milliseconds 400
    $h1 = [double](In @{ kind = "state" }).heading_deg
    Check "Left arrow turns the avatar left" ((Turn $h0 $h1) -gt 15) ("heading {0:N1} -> {1:N1} ({2:N1} deg)" -f $h0, $h1, (Turn $h0 $h1))
    [void](Key "RIGHT" $true); Start-Sleep -Milliseconds 700; [void](Key "RIGHT" $false); Start-Sleep -Milliseconds 400
    $h2 = [double](In @{ kind = "state" }).heading_deg
    Check "Right arrow turns the avatar right" ((Turn $h1 $h2) -lt -15) ("heading {0:N1} -> {1:N1} ({2:N1} deg)" -f $h1, $h2, (Turn $h1 $h2))
    $p0 = Pos; [void](Key "DOWN" $true); Start-Sleep -Milliseconds 1500; [void](Key "DOWN" $false); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "Down arrow moves the avatar back" ((Flat $p0 $p1) -gt 0.8) ("moved {0:N2} m" -f (Flat $p0 $p1))
    $p0 = Pos; [void](Key "W" $true); Start-Sleep -Milliseconds 1500; [void](Key "W" $false); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "W moves the avatar forward" ((Flat $p0 $p1) -gt 0.8) ("moved {0:N2} m" -f (Flat $p0 $p1))
    $p0 = Pos; [void](Key "A" $true $true); Start-Sleep -Milliseconds 700; $mid = In @{ kind = "state" }; $ty = Parse (Invoke-Raw $port "typing" $token ('{"action":"state"}')).Body; $ob = Parse (Invoke-Raw $port "observe" $token).Body; Note ("diag shift+A: keys_held={0} controls={1} text_entry={2} movement_allowed={3} focused={4}" -f ($mid.keys_held -join ","), $ob.self.controls, $ty.text_entry, $ty.movement_allowed, $ob.self.focused); Start-Sleep -Milliseconds 800; [void](Key "A" $false $true); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "Shift+A slides the avatar sideways" ((Flat $p0 $p1) -gt 0.6) ("moved {0:N2} m" -f (Flat $p0 $p1))

    # 1b a button that has the keyboard does not take the arrow keys
    $fb = In @{ kind = "focus_button" }; Start-Sleep -Milliseconds 300
    $p0 = Pos; [void](Key "UP" $true); Start-Sleep -Milliseconds 700; $mid = In @{ kind = "state" }; $ty = Parse (Invoke-Raw $port "typing" $token '{"action":"state"}').Body; $ob = Parse (Invoke-Raw $port "observe" $token).Body; Note ("diag button: keys_held={0} controls={1} text_entry={2} movement_allowed={3} focused={4} owner={5}" -f ($mid.keys_held -join ","), $ob.self.controls, $ty.text_entry, $ty.movement_allowed, $ob.self.focused, $mid.focus_owner); Start-Sleep -Milliseconds 800; [void](Key "UP" $false); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "Up arrow moves the avatar while a button has the keyboard" (($null -ne $fb.focus_owner) -and (Flat $p0 $p1) -gt 0.8) ("owner {0}, moved {1:N2} m" -f $fb.focus_owner, (Flat $p0 $p1))

    # 2 the keyboard goes back to the world when the world is clicked
    [void](Invoke-Raw $port "typing" $token '{"action":"focus"}'); Start-Sleep -Milliseconds 300
    $withChat = In @{ kind = "state" }
    [void](Mouse "left" $true $cx ($cy + 250)); [void](Mouse "left" $false $cx ($cy + 250)); Start-Sleep -Milliseconds 300
    $after = In @{ kind = "state" }
    Check "a click in the world takes the keyboard away from a panel" (($null -ne $withChat.focus_owner) -and ($null -eq $after.focus_owner)) ("owner {0} -> {1}" -f $withChat.focus_owner, $after.focus_owner)
    $p0 = Pos; [void](Key "UP" $true); Start-Sleep -Milliseconds 1500; [void](Key "UP" $false); Start-Sleep -Milliseconds 800; $p1 = Pos
    Check "Up arrow moves the avatar after the click" ((Flat $p0 $p1) -gt 0.8) ("moved {0:N2} m" -f (Flat $p0 $p1))

    # 3 a left-drag on the avatar (the middle of the view) and on the ground
    $before = [double](In @{ kind = "state" }).heading_deg
    $s = Drag $cx $cy 160 0
    $afterDrag = [double](In @{ kind = "state" }).heading_deg
    Check "a left-drag on the avatar turns it" ($s.steering -eq $true -and [math]::Abs((Turn $before $afterDrag)) -gt 2) ("heading {0:N1} -> {1:N1} ({2:N1} deg, steering {3})" -f $before, $afterDrag, (Turn $before $afterDrag), $s.steering)
    $dirRight = Turn $before $afterDrag
    Shot "ctl_02_after_avatar_drag"
    $before = $afterDrag
    $s = Drag $cx ($cy + 300) -160 0
    $afterGround = [double](In @{ kind = "state" }).heading_deg
    Check "a left-drag on the ground turns the avatar" ($s.steering -eq $true -and [math]::Abs((Turn $before $afterGround)) -gt 2) ("heading {0:N1} -> {1:N1} ({2:N1} deg)" -f $before, $afterGround, (Turn $before $afterGround))
    $before = $afterGround
    [void](Mouse "left" $true $cx ($cy + 300)); [void](Mouse "none" $false ($cx + 2) ($cy + 300)); [void](Mouse "left" $false ($cx + 2) ($cy + 300)); Start-Sleep -Milliseconds 300
    $tiny = [double](In @{ kind = "state" }).heading_deg
    Check "a click with a tiny movement does not turn the avatar" ([math]::Abs((Turn $before $tiny)) -lt 0.5) ("{0:N2} deg" -f (Turn $before $tiny))
    Note ("direction: a drag to the right turned the avatar by {0:N1} degrees (positive is left)" -f $dirRight)

    # 4 the camera tools
    $r = Cam "reset"; Start-Sleep -Milliseconds 600; $c0 = Cam "state"; $hh = [double](In @{ kind = "state" }).heading_deg
    $gy = $cy + 300
    $s = Drag $cx $gy 200 0 $true
    $c1 = Cam "state"; $hAlt = [double](In @{ kind = "state" }).heading_deg
    Check "an Alt-drag moves the camera without turning the avatar" ((Dist $c1.eye $c0.eye) -gt 0.5 -and [math]::Abs((Turn $hh $hAlt)) -lt 0.5) ("eye moved {0:N2} m, heading changed {1:N2} deg, tool {2}" -f (Dist $c1.eye $c0.eye), (Turn $hh $hAlt), $s.camera_tool)
    Shot "ctl_03_alt_drag"
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 700; $c1 = Cam "state"; $s = Drag $cx $gy 0 -120 $true $true
    $c2 = Cam "state"
    Check "a Ctrl+Alt-drag orbits the camera" ((Dist $c2.eye $c1.eye) -gt 0.3) ("eye moved {0:N2} m, distance {1:N2} -> {2:N2}" -f (Dist $c2.eye $c1.eye), $c1.focus_distance, $c2.focus_distance)
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 700; $c2 = Cam "state"; $s = Drag $cx $gy 150 0 $true $true $true
    $c3 = Cam "state"
    Check "a Ctrl+Alt+Shift-drag pans the camera" ((Dist $c3.look $c2.look) -gt 0.3 -and [math]::Abs($c3.focus_distance - $c2.focus_distance) -lt 0.2) ("focus moved {0:N2} m" -f (Dist $c3.look $c2.look))
    Shot "ctl_04_pan"
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 900
    $c4 = Cam "state"
    Check "Esc puts the default view back" ($c4.focus_on_avatar -eq $true -and [math]::Abs($c4.zoom - 1) -lt 0.001) ("focus_on_avatar {0}, zoom {1}" -f $c4.focus_on_avatar, $c4.zoom)
    [void](Key "LEFT" $true $false $true); Start-Sleep -Milliseconds 700; $ck = In @{ kind = "state" }; [void](Key "LEFT" $false $false $true); Start-Sleep -Milliseconds 300
    $c5 = Cam "state"
    Check "Alt+Left spins the camera and the arrow keys do not move the avatar" ((Dist $c5.eye $c4.eye) -gt 0.3 -and ($ck.camera_keys_held -contains "SpinAroundCw") -and -not ($ck.keys_held -contains "LEFT")) ("eye moved {0:N2} m, camera keys {1}" -f (Dist $c5.eye $c4.eye), ($ck.camera_keys_held -join ","))
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 600

    # 5 right button: the menu opens, nothing turns
    $hr = [double](In @{ kind = "state" }).heading_deg
    [void](Mouse "right" $true $cx $cy); [void](Mouse "none" $false ($cx + 80) $cy); [void](Mouse "right" $false ($cx + 80) $cy); Start-Sleep -Milliseconds 300
    $hr2 = [double](In @{ kind = "state" }).heading_deg
    Check "a right-drag turns nothing" ([math]::Abs((Turn $hr $hr2)) -lt 0.5) ("{0:N2} deg" -f (Turn $hr $hr2))
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "controls-report.json") -Encoding utf8
Write-Output "LIVE CONTROLS RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

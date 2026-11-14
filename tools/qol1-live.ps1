# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# qol1-live.ps1 - the live test of the camera constraints setting, the keys that reset the camera, and a refused login. Logs a test avatar in
# with the development export and sends keys and mouse events through the engine's own input path (the automation input command): the turn keys
# and A, D, W, S after an Alt-drag, then Alt-drags with the Disable camera constraints setting off and on, reading how far the camera goes from
# the avatar; then a second run that asks for a start location that does not exist and reads the login screen's status text.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\qol1-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: qol1-report.json and qol1-report.txt in the sandbox, pictures in -ShotsDir.
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
Write-Output "LIVE QOL1 RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
    for ($k = 0; $k -lt 30; $k++) {
        $ob = Parse (Invoke-Raw $port "observe" $token).Body
        if (-not $ob.self.flying) { break }
        [void](Key "C" $true); Start-Sleep -Milliseconds 700
    }
    [void](Key "C" $false); Start-Sleep -Seconds 2
    [void](Invoke-Raw $port "typing" $token '{"action":"focus"}'); Start-Sleep -Milliseconds 400; [void](Invoke-Raw $port "typing" $token '{"action":"release"}'); Start-Sleep -Milliseconds 300
    $st = In @{ kind = "state" }
    $vw = [double]$st.view[0]; $vh = [double]$st.view[1]
    $cx = $vw / 2; $cy = $vh / 2
    Shot "qol1_00_start"
    function SetPref([string]$key, $value) { $r = Invoke-Raw $port "prefs" $token (@{ action = "set"; key = $key; value = $value } | ConvertTo-Json -Compress); Start-Sleep -Milliseconds 800; return $r.Status }
    function Unlocked() { [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 700; [void](Drag $cx ($cy + 40) 0 -150 $true); $c = Cam "state"; return (-not $c.focus_on_avatar) }
    function InForce() { $s = Parse (Invoke-Raw $port "prefs" $token '{"action":"state"}').Body; return $s.in_force.disable_camera_constraints }

    # ---- PART 2: the turn keys reset the camera onto the avatar ----
    foreach ($k in @("LEFT", "RIGHT", "A", "D", "W", "S")) {
        $was = Unlocked
        $h0 = [double](In @{ kind = "state" }).heading_deg
        [void](Key $k $true); Start-Sleep -Milliseconds 500; [void](Key $k $false); Start-Sleep -Milliseconds 600
        $c = Cam "state"; $h1 = [double](In @{ kind = "state" }).heading_deg
        Check "P2 key $k : the camera is back on the avatar" ($was -and $c.focus_on_avatar -eq $true) ("unlocked before {0}, focus_on_avatar after {1}, heading {2:N1} -> {3:N1}" -f $was, $c.focus_on_avatar, $h0, $h1)
    }
    # a held turn key keeps the view on the avatar: an Alt-drag during the hold is undone at once
    [void](Key "LEFT" $true); Start-Sleep -Milliseconds 300
    [void](Drag $cx ($cy + 40) 0 -150 $true)
    $held = Cam "state"
    [void](Key "LEFT" $false); Start-Sleep -Milliseconds 500
    Check "P2 a held turn key keeps the view on the avatar during an Alt-drag" ($held.focus_on_avatar -eq $true) ("focus_on_avatar {0}" -f $held.focus_on_avatar)
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 700
    Shot "qol1_01_after_keys"

    # ---- PART 1: Disable camera constraints ----
    # The wheel multiplies the camera's distance by 1.19 per click (cameraOrbitIn), so a few clicks reach any distance the setting allows.
    $avatarAt = Pos
    $script:outside = $false
    function FarFromAvatar([string]$what) {
        $c = Cam "state"
        $d = Dist $c.eye @($avatarAt[0], $avatarAt[1], $avatarAt[2])
        if ($c.eye[0] -lt 0 -or $c.eye[0] -ge 256 -or $c.eye[1] -lt 0 -or $c.eye[1] -ge 256) { $script:outside = $true }
        Note ("  {0}: eye {1:N1},{2:N1},{3:N1}, {4:N1} m from the avatar, zoom {5:N2}" -f $what, $c.eye[0], $c.eye[1], $c.eye[2], $d, $c.zoom) | Out-Host
        return $d
    }
    function Wheel([double]$clicks, [int]$times) { for ($i = 0; $i -lt $times; $i++) { [void](Cam "wheel" $clicks); Start-Sleep -Milliseconds 700 } }
    function Back() { [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 500; [void](Cam "reset"); Start-Sleep -Milliseconds 1200 }

    $inForce0 = InForce
    Check "P1 the setting is off at the start" ($inForce0 -eq $false) ("in force: $inForce0")
    Back; Wheel 10 4; $offRear = FarFromAvatar "off, rear view, wheel out 40 clicks"
    Shot "qol1_02_off_rear"
    Check "P1 off: the rear view stops at its zoom limit (8 times, about 25 m)" ($offRear -le 50 -and $offRear -gt 10) ("{0:N1} m from the avatar" -f $offRear)
    Back; $was = Unlocked; Wheel 10 4; $offFree = FarFromAvatar "off, unlocked view, wheel out 40 clicks"
    Shot "qol1_03_off_unlocked"
    Check "P1 off: the unlocked view stops at the draw distance (128 m)" ($was -and $offFree -le 129.0 -and $offFree -gt 90) ("{0:N1} m from the avatar" -f $offFree)

    Back
    [void](SetPref "disable_camera_constraints" $true)
    $inForce1 = InForce
    Check "P1 the setting is in force after turning it on" ($inForce1 -eq $true) ("in force: $inForce1")
    $script:outside = $false
    Wheel 10 3; $onRear = FarFromAvatar "on, rear view, wheel out 30 clicks"
    Shot "qol1_04_on_rear"
    Check "P1 on: the rear view goes far past the draw distance and the zoom limit" ($onRear -gt 200) ("{0:N1} m from the avatar (off: {1:N1})" -f $onRear, $offRear)
    Check "P1 on: the camera leaves the region the avatar is in" ($script:outside) ("eye outside 0..256 : $($script:outside)")
    Back
    $script:outside = $false
    $was2 = Unlocked; Wheel 10 3; $onFree = FarFromAvatar "on, unlocked view, wheel out 30 clicks"
    Shot "qol1_05_on_unlocked"
    Check "P1 on: the unlocked view goes past the draw distance" ($was2 -and $onFree -gt 200) ("{0:N1} m from the avatar (off: {1:N1})" -f $onFree, $offFree)
    [void](Key "W" $true); Start-Sleep -Milliseconds 400; [void](Key "W" $false); Start-Sleep -Milliseconds 600
    $back = Cam "state"
    Check "P1 on: a movement key still brings the camera back to the avatar" ($back.focus_on_avatar -eq $true) ("focus_on_avatar {0}" -f $back.focus_on_avatar)
    [void](SetPref "disable_camera_constraints" $false)
    Back
    Shot "qol1_99_end"
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

# ---- PART 3: a start location that does not exist: the grid's own message is shown ----
Write-Output "second run: a start location that does not exist"
$appdata2 = Join-Path $Sandbox "appdata2"; $local2 = Join-Path $Sandbox "localappdata2"; $log2 = Join-Path $Sandbox "run2.log"
$p2 = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata2`"", "-LocalAppDataDir", "`"$local2`"", "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log2`"", "-ArgLine", "`"$argLine`"")
$tokDir2 = Join-Path $local2 "ReverieViewer\Automation"
$tf2 = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf2; $i++) { Start-Sleep -Milliseconds 250; $tf2 = @(Get-ChildItem -LiteralPath $tokDir2 -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if ($tf2) {
    Start-Sleep -Milliseconds 300
    $j2 = Get-Content -Raw -LiteralPath $tf2.FullName | ConvertFrom-Json
    $port = [int]$j2.port; $token = [string]$j2.token
    $loginUri2 = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login
    $lg2 = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri2; start = "uri:NoSuchRegionQolOne&128&128&25" } | ConvertTo-Json -Compress)
    $loginUri2 = $null
    $s2 = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        $s2 = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body
        if ($s2 -and -not $s2.busy -and $s2.status -match "failed") { break }
    }
    Note ("status shown: [" + ($s2.status -replace "`n", " | ") + "]")
    $text = [string]$s2.status
    $lines = $text -split "`n"
    Check "P3 the login screen shows the header and the grid's own message" ($text.StartsWith("Login failed.") -and $lines.Count -ge 2 -and $lines[1].Trim().Length -gt 10) ("lines: " + $lines.Count)
    Check "P3 the reason code is not shown as text" ($text -notmatch "(?i)\bkey\b|code=") ("status: " + ($text -replace "`n", " | "))
    $report.status_text = $text
    $dir = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body
    Shot "qol1_04_login_failed"
    [void](Invoke-Raw $port "quit" $token)
    $p2.WaitForExit(60000) | Out-Null
    Note "second viewer exit: $(if ($p2.HasExited) { $p2.ExitCode } else { 'still running' })"
} else { Check "P3 the second viewer started" $false "no automation token file" }

$report.checks = $checks
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "qol1-report.json") -Encoding utf8
Write-Output "LIVE QOL1 RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# cam2-live.ps1 - the live camera and arrow-key test. Logs a test avatar in with the development export and sends keys and mouse events through the
# engine's own input path (the automation input command): the arrow keys, a left-drag on the avatar, Alt, Ctrl+Alt and Ctrl+Alt+Shift drags and Esc,
# and reads back what the avatar and the camera did.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\camera-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: cam2-report.json and cam2-report.txt in the sandbox, pictures in -ShotsDir.
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
Write-Output "LIVE CAM2 RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
    Note ("view {0}x{1}, window {4}, keyboard owner {2}, heading {3}" -f $vw, $vh, $st.focus_owner, $st.heading_deg, ($st.window -join "x"))
    Shot "cam2_00_start"

    function Click([double]$x, [double]$y) { [void](Mouse "none" $false $x $y); [void](Mouse "left" $true $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $x $y); Start-Sleep -Milliseconds 500 }
    function Owner() { $s = In @{ kind = "state" }; "$($s.focus_owner) hovered=$($s.hovered) mouse=$($s.mouse_at -join ",")" }
    function Controls() { (In @{ kind = "buttons" }).buttons }
    function ArrowCheck([string]$label) {
        $own = Owner
        $p0 = Pos; [void](Key "UP" $true); Start-Sleep -Milliseconds 1600; $held = In @{ kind = "state" }; [void](Key "UP" $false); Start-Sleep -Milliseconds 700; $p1 = Pos
        Check "$label : Up arrow walks" ((Flat $p0 $p1) -gt 1.0) ("owner before {0}, moved {1:N2} m, held {2}" -f $own, (Flat $p0 $p1), ($held.keys_held -join ","))
        $h0 = [double](In @{ kind = "state" }).heading_deg
        [void](Key "LEFT" $true); Start-Sleep -Milliseconds 700; [void](Key "LEFT" $false); Start-Sleep -Milliseconds 400
        $h1 = [double](In @{ kind = "state" }).heading_deg
        Check "$label : Left arrow turns" ((Turn $h0 $h1) -gt 15) ("{0:N1} deg" -f (Turn $h0 $h1))
        [void](Key "RIGHT" $true); Start-Sleep -Milliseconds 700; [void](Key "RIGHT" $false); Start-Sleep -Milliseconds 400
        $h2 = [double](In @{ kind = "state" }).heading_deg
        Check "$label : Right arrow turns" ((Turn $h1 $h2) -lt -15) ("{0:N1} deg" -f (Turn $h1 $h2))
        $p0 = Pos; [void](Key "DOWN" $true); Start-Sleep -Milliseconds 1200; [void](Key "DOWN" $false); Start-Sleep -Milliseconds 600; $p1 = Pos
        Check "$label : Down arrow walks back" ((Flat $p0 $p1) -gt 0.6) ("moved {0:N2} m" -f (Flat $p0 $p1))
    }

    # ---- PART 2: the arrow keys after real use of the screen ----
    ArrowCheck "P2 fresh"
    $all = @(Controls)
    foreach ($c in $all) { Note ("  control {0} {1} '{2}' at {3:N0},{4:N0} {5:N0}x{6:N0} focus={7}" -f $c.type, $c.path, $c.text, $c.x, $c.y, $c.w, $c.h, $c.focus) }
    $buttons = @($all | Where-Object { $_.type -match "Button" -and $_.path -notmatch "(?i)close|resize|menu" } )
    $toolbar = @($buttons | Where-Object { $_.path -match "toolbar_(inventory|people|map|outfits)" })
    if ($toolbar.Count -lt 1) { $toolbar = $buttons }
    $chat = @($all | Where-Object { $_.path -match "chat_bar.*input" }) | Select-Object -First 1
    Note ("toolbar candidates {0}; chat field: {1}" -f $toolbar.Count, $(if ($chat) { $chat.path } else { "none" }))

    # V1: click a toolbar button (opens its window), look, close it, then the arrows
    $tb = $toolbar | Select-Object -First 1
    if ($tb) {
        Click $tb.x $tb.y; Start-Sleep -Milliseconds 700
        $after = @(Controls); Note ("V1 after the first click: owner {0}, controls {1} (was {2})" -f (Owner), $after.Count, $all.Count)
        Shot "cam2_01_toolbar_window_open"
        $new = @($after | Where-Object { $_.path -match "(?i)close" -or $_.text -match "^[xX]$" }) | Select-Object -First 1
        if ($new) { Note ("V1 closing with {0}" -f $new.path); Click $new.x $new.y } else { Click $tb.x $tb.y }
        Start-Sleep -Milliseconds 600
        Note ("V1 after closing: owner {0}, controls {1}" -f (Owner), @(Controls).Count)
        ArrowCheck "P2 V1 toolbar button, window opened and closed"
    }
    # V2: a second toolbar button (its window left open), then the chat field, then the world
    if ($toolbar.Count -ge 2) { $tb2 = $toolbar[1]; Click $tb2.x $tb2.y; Start-Sleep -Milliseconds 600; Note ("V2 second toolbar button: owner {0}" -f (Owner)); Shot "cam2_02_second_window" }
    if ($chat) {
        Click $chat.x $chat.y; Note ("V2 chat field clicked: owner {0}" -f (Owner)); Shot "cam2_03_chat_focused"
        Click $cx ($cy + 250); Note ("V2 world clicked: owner {0}" -f (Owner))
        ArrowCheck "P2 V2 chat field clicked, then the world"
    }
    # V3: the chat field, then a toolbar button, then the arrows
    if ($chat -and $tb) {
        Click $chat.x $chat.y; Click $tb.x $tb.y; Start-Sleep -Milliseconds 500; Note ("V3 chat then toolbar button: owner {0}" -f (Owner))
        ArrowCheck "P2 V3 chat field, then a toolbar button"
        Click $tb.x $tb.y; Start-Sleep -Milliseconds 400
    }
    # V4: the chat field, then Esc
    if ($chat) {
        Click $chat.x $chat.y; [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 400; Note ("V4 chat then Esc: owner {0}" -f (Owner))
        ArrowCheck "P2 V4 chat field, then Esc"
    }
    Shot "cam2_04_after_part2"

    # ---- PART 1: the Alt camera ----
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 800
    $c0 = Cam "state"; $origin = @($c0.eye[0], $c0.eye[1], $c0.eye[2]); $avatarAt = Pos
    Note ("camera start: eye {0:N1},{1:N1},{2:N1} land {3}" -f $c0.eye[0], $c0.eye[1], $c0.eye[2], $c0.land_z)
    $script:minAbove = 1e9; $script:maxFar = 0.0; $script:trackLog = ""
    function Track([string]$what) {
        $c = Cam "state"
        $above = [double]$c.eye[2] - [double]$c.land_z
        $far = [math]::Sqrt([math]::Pow($c.eye[0] - $origin[0], 2) + [math]::Pow($c.eye[1] - $origin[1], 2))
        if ($above -lt $script:minAbove) { $script:minAbove = $above }
        if ($far -gt $script:maxFar) { $script:maxFar = $far }
        $script:trackLog += ("  {0}: eye {1:N1},{2:N1},{3:N1} land {4:N2} above {5:N2} focus_distance {6:N2} moved {7:N1} m from where it began, {8:N1} m from the avatar" -f $what, $c.eye[0], $c.eye[1], $c.eye[2], $c.land_z, $above, $c.focus_distance, $far, [math]::Sqrt([math]::Pow($c.eye[0] - $avatarAt[0], 2) + [math]::Pow($c.eye[1] - $avatarAt[1], 2))) + "`n"
        return $c
    }
    # one Alt-click on the ground near the horizon (a far point) and an Alt-drag with the pointer pulled up
    function Hop([int]$n) {
        $gy = [math]::Min([double](Cam "state").horizon_y + 14, $vh - 120)
        [void](Drag $cx $gy 0 -150 $true)
        Start-Sleep -Milliseconds 400
        [void](Track "hop $n")
        Shot ("cam2_hop_{0:00}" -f $n)
    }
    Note "Alt camera at the default draw distance"
    for ($n = 1; $n -le 6; $n++) { Hop $n }
    $far128 = $script:maxFar
    $dd = Invoke-Raw $port "prefs" $token (@{ action = "set"; key = "draw_distance"; value = 512 } | ConvertTo-Json -Compress); Start-Sleep -Seconds 6
    Note "draw distance set to 512: $($dd.Status)"
    for ($n = 5; $n -le 30; $n++) { Hop $n; if ($script:maxFar -gt 215) { break } }
    Check "Alt-click and pull: the camera crosses the region from one spot" ($script:maxFar -gt 150) ("farthest {0:N1} m from the standing spot (128 m limit run: {1:N1})" -f $script:maxFar, $far128)
    Check "the camera never went under the land" ($script:minAbove -ge 0.45) ("lowest {0:N2} m above the land" -f $script:minAbove)
    # a tilt and a turn around one focus: one gesture, the focus is picked at the press and kept
    $gy2 = $cy + 40
    [void](Mouse "none" $false $cx $gy2 $true $true)
    [void](Mouse "left" $true $cx $gy2 $true $true)
    $ta = Cam "state"
    for ($i = 1; $i -le 10; $i++) { [void](Mouse "none" $false $cx ($gy2 - 30 * $i) $true $true); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 200
    $tb = Track "tilt, pointer pulled up 300 px"
    for ($i = 1; $i -le 20; $i++) { [void](Mouse "none" $false $cx (($gy2 - 300) + 30 * $i) $true $true); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 200
    $tc = Track "tilt, pointer pushed down 300 px past the start"
    [void](Mouse "left" $false $cx ($gy2 + 300) $true $true)
    Check "Ctrl+Alt-drag tilts about the focus (the same distance when the ground does not lift the camera)" ([math]::Abs($tc.focus_distance - $ta.focus_distance) -lt 0.1 -and (Dist $ta.eye $tc.eye) -gt 0.2) ("distance {0:N2} / {1:N2} / {2:N2}, eye moved {3:N2} / {4:N2}" -f $ta.focus_distance, $tb.focus_distance, $tc.focus_distance, (Dist $ta.eye $tb.eye), (Dist $ta.eye $tc.eye))
    [void](Drag ($cx - 300) ($cy + 40) 600 0 $true $true)
    [void](Track "orbit")
    Check "the camera is still above the land after the tilt and the orbit" ($script:minAbove -ge 0.45) ("lowest {0:N2} m above the land" -f $script:minAbove)
    [void](Key "ESC" $true); [void](Key "ESC" $false); Start-Sleep -Milliseconds 900
    $r = Cam "state"
    Check "Esc returns the camera to the avatar" ($r.focus_on_avatar -eq $true) ("focus_on_avatar {0}" -f $r.focus_on_avatar)
    [void](Drag $cx ($cy + 40) 0 -150 $true)
    $u = Cam "state"; $wasOff = -not $u.focus_on_avatar
    [void](Key "W" $true); Start-Sleep -Milliseconds 500; [void](Key "W" $false); Start-Sleep -Milliseconds 500
    $w = Cam "state"
    Check "a movement key returns the camera to the avatar" ($wasOff -and $w.focus_on_avatar -eq $true) ("unlocked before: {0}, after the key: focus_on_avatar {1}" -f $wasOff, $w.focus_on_avatar)
    Note ("camera track:`n" + $script:trackLog)
    Shot "cam2_99_end"
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "cam2-report.json") -Encoding utf8
Write-Output "LIVE CAM2 RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

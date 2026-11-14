# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# cam4-live.ps1 - the live stand, ground-sit, name-tag and arrow-key camera test. Logs a test avatar in with the development export and sends keys and mouse events through the
# engine's own input path (the automation input command): the arrow keys, a left-drag on the avatar, Alt, Ctrl+Alt and Ctrl+Alt+Shift drags and Esc,
# and reads back what the avatar and the camera did.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\cam4-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: cam4-report.json and cam4-report.txt in the sandbox, pictures in -ShotsDir.
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
Write-Output "LIVE CAM4 RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
    $ob = Obs
    $px = [double]$ob.self.region_position[0]; $py = [double]$ob.self.region_position[1]; $pz = [double]$ob.self.region_position[2]
    $hd = [double]$ob.self.drawn_heading_deg * [math]::PI / 180
    $groundZ = [double]$ob.self.drawn_soles[2]
    Note ("standing at {0:N1},{1:N1},{2:N2}; drawn soles z {3:N2}; heading {4:N0} deg" -f $px, $py, $pz, $groundZ, ($hd * 180 / [math]::PI))
    $report.start = [ordered]@{ stated_z = $pz; soles_z = ($groundZ + $zoff) }
    Shot "cam4_00_start"

    # ---- 1. sit on the ground ----
    # the land menu's Sit Here only shows within 3 m of land, and her landing spot may be a floor; the automation sends the same command
    $chose = [bool](Interact @{ action = "sit_ground" }).sent
    Note "Sit Here sent: $chose"
    Start-Sleep -Seconds 5
    $ob = Obs; $is = Interact @{ action = "state" }
    $headRise = if ($ob.self.drawn_head) { [double]$ob.self.drawn_head[2] - $groundZ } else { -1 }
    $solesRise = [double]$ob.self.drawn_soles[2] - $groundZ
    Note ("ground sit: playing {0}; sitting {1}; stand button {2}; stated z {3:N2}; drawn soles z {4:N2}; head {5:N2} m above the standing soles" -f (Playing $ob), $is.sitting, $is.stand_button, $ob.self.region_position[2], $ob.self.drawn_soles[2], $headRise)
    $report.ground_sit = [ordered]@{ playing = (Playing $ob); sitting = $is.sitting; stand_button = $is.stand_button; stated_z = $ob.self.region_position[2]; soles_z = $ob.self.drawn_soles[2]; head_above_standing_soles = $headRise; seat = $ob.self.seat }
    Shot "cam4_01_ground_sit"
    Check "1 ground sit: she sits on the ground, not above it" (($ob.self.playing -contains "sit_ground") -and $headRise -gt 0 -and $headRise -lt 1.35) ("head {0:N2} m above the standing soles (standing head is about 1.7)" -f $headRise)
    Check "1 ground sit: the Stand button shows" ($is.sitting -eq $true -and $is.stand_button -eq $true) ("sitting {0}, button {1}" -f $is.sitting, $is.stand_button)

    # ---- 2. stand ----
    [void](Interact @{ action = "stand" })
    Start-Sleep -Seconds 6
    $ob = Obs; $is = Interact @{ action = "state" }
    Check "2 stand: she stands" (($ob.self.playing -notcontains "sit_ground") -and $is.sitting -eq $false -and $is.stand_button -eq $false) ("playing {0}; sitting {1}; button {2}" -f (Playing $ob), $is.sitting, $is.stand_button)
    $report.stand_from_ground = [ordered]@{ playing = (Playing $ob); sitting = $is.sitting }

    # ---- 3. sit on a prim near the landing point ----
    $ob = Obs
    $px = [double]$ob.self.region_position[0]; $py = [double]$ob.self.region_position[1]; $pz = [double]$ob.self.region_position[2]
    $hd = [double]$ob.self.drawn_heading_deg * [math]::PI / 180
    $sat = $false; $tried = 0
    foreach ($radius in 3, 5, 7, 9) {
        foreach ($deg in 0, -12, 12, -25, 25, -40, 40) {
            if ($sat) { break }
            $a = $hd + $deg * [math]::PI / 180
            $tx = $px + $radius * [math]::Cos($a); $ty = $py + $radius * [math]::Sin($a)
            $r = Interact @{ action = "rightclick"; x = $tx; y = $ty; z = ($groundZ + 0.4) }
            $tried++
            if ($r.pick.kind -eq "Object" -and (@($r.entries | Where-Object { $_.Id -eq "sit" -and $_.Enabled }).Count -gt 0)) {
                [void](Interact @{ action = "choose"; entry = "sit" })
                $sat = $true
            } else { [void](Interact @{ action = "choose"; entry = "" }) }
        }
    }
    Note "prim search: $tried points tried, sat $sat"
    if ($sat) {
        Start-Sleep -Seconds 6
        $ob = Obs; $is = Interact @{ action = "state" }
        $nt = Parse (Invoke-Raw $port "nametags" $token).Body
        $tag = @($nt.tags) | Select-Object -First 1
        $headZ = if ($ob.self.drawn_head) { [double]$ob.self.drawn_head[2] } else { $null }
        $above = if ($tag -and $null -ne $headZ) { [double]$tag.anchor[2] - $headZ } else { $null }
        Note ("prim sit: sitting {0}; stand button {1}; seat {2}; head z {3}; tag anchor z {4}; tag above head {5}" -f $is.sitting, $is.stand_button, $ob.self.seat, $headZ, $(if ($tag) { $tag.anchor[2] } else { "none" }), $above)
        $report.prim_sit = [ordered]@{ sitting = $is.sitting; stand_button = $is.stand_button; seat = $ob.self.seat; head_z = $headZ; tag_above_head = $above; shown = $nt.shown }
        Shot "cam4_03_prim_sit"
        Check "3 prim sit: she sits and Stand shows" ($is.sitting -eq $true -and $is.stand_button -eq $true -and [int]$ob.self.seat -ne 0) ("sitting {0}, button {1}, seat {2}" -f $is.sitting, $is.stand_button, $ob.self.seat)
        Check "3 prim sit: the name tag sits just above her head" ($null -ne $above -and $above -gt 0 -and $above -lt 0.6) ("tag anchor {0} m above the head joint" -f $above)
        # ---- 4. stand ----
        [void](Interact @{ action = "stand" })
        Start-Sleep -Seconds 6
        $ob = Obs; $is = Interact @{ action = "state" }
        Check "4 stand: she stands" ($is.sitting -eq $false -and [int]$ob.self.seat -eq 0) ("sitting {0}, seat {1}" -f $is.sitting, $ob.self.seat)
    } else { Check "3 prim sit: a prim to sit on was found" $false "no object with a Sit entry within 9 m in front of her" }

    # ---- 5. Alt camera away, then each arrow ----
    $st = IState
    $vw = [double]$st.view[0]; $vh = [double]$st.view[1]; $cx = $vw / 2; $cy = $vh / 2
    $first = $true
    foreach ($arrow in "LEFT", "RIGHT", "UP", "DOWN") {
        [void](Drag ($cx - 250) ($cy + 40) 500 -120 $true)
        $away = Cam "state"
        $wasOff = -not $away.focus_on_avatar
        if ($first) { Shot "cam4_05_camera_away" }
        $h0 = [double](IState).heading_deg
        [void](Key $arrow $true); Start-Sleep -Milliseconds 350
        $held = IState
        $viaCtl = (Obs).self.controls
        [void](Key $arrow $false); Start-Sleep -Milliseconds 2500
        $back = Cam "state"
        $ob = Obs
        Note ("  {0}: keys held while down [{1}], controls {2}, keyboard owner {3}, heading {4:N1} -> {5:N1}" -f $arrow, ($held.keys_held -join ","), $viaCtl, $held.focus_owner, $h0, [double](IState).heading_deg)
        $ex = [double]$back.eye[0] - [double]$ob.self.region_position[0]; $ey = [double]$back.eye[1] - [double]$ob.self.region_position[1]
        $flat = [math]::Sqrt($ex * $ex + $ey * $ey)
        $h = [double]$ob.self.drawn_heading_deg * [math]::PI / 180
        $dot = if ($flat -gt 0.01) { (-[math]::Cos($h) * $ex - [math]::Sin($h) * $ey) / $flat } else { 0 }
        if ($first) { Shot "cam4_05_after_arrow"; $first = $false }
        Check "5 $arrow snaps the camera back behind her" ($wasOff -and $back.focus_on_avatar -eq $true -and $dot -gt 0.9 -and $flat -gt 2 -and $flat -lt 4.5) ("away: focus off {0}; after: focus on {1}, {2:N2} m behind-ness {3:N2}, flat distance {4:N2}" -f $wasOff, $back.focus_on_avatar, $back.focus_distance, $dot, $flat)
    }

    # ---- 6. fly, then stop flying ----
    [void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false); Start-Sleep -Milliseconds 400
    [void](Key "PGUP" $true); Start-Sleep -Milliseconds 1800; [void](Key "PGUP" $false); Start-Sleep -Seconds 2
    $ob = Obs
    $airZ = [double]$ob.self.region_position[2]
    Check "6 fly: she is flying and in the air" ($ob.self.flying -eq $true -and $airZ -gt ($pz + 2)) ("flying {0}, z {1:N1} (ground {2:N1})" -f $ob.self.flying, $airZ, $pz)
    [void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false)
    $landed = $false
    for ($k = 0; $k -lt 20; $k++) { Start-Sleep -Seconds 1; $ob = Obs; if (-not $ob.self.flying -and [double]$ob.self.region_position[2] -lt ($pz + 1.2)) { $landed = $true; break } }
    Check "6 stop flying: she lands" $landed ("flying {0}, z {1:N1} (ground {2:N1})" -f $ob.self.flying, $ob.self.region_position[2], $pz)
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "cam4-report.json") -Encoding utf8
Write-Output "LIVE CAM4 RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

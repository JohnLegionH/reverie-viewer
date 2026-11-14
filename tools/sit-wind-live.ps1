# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# sit-wind-live.ps1 - the live check of sitting on a prim and of the wind. Starts the development export, logs a test avatar in,
# sits it on the nearest prim near the start that offers Sit (nothing is rezzed), and reads through the automation interface
# (world_sounds seat) where its pelvis is drawn against the top of the prim under it, with its own body height and the region's;
# then stands, flies up and forward, and reads the wind (world_sounds wind) while hovering and at rising speeds; checks the avatar.sit log
# line; slides sideways with Mono off and on (an off-centre wind on both sides alike with Mono); stops flying and reads the wind while
# falling (silent: the official viewer mutes the wind unless flying). Then it logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sit-wind-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: sit-wind-report.json in the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked,
# 5 the viewer or the login did not come up.
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
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($script:port -and $script:token) { try { [void](Invoke-Raw $script:port "session" $script:token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $script:port "quit" $script:token) } catch { } }
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
function Note([string]$what) { [Console]::Out.WriteLine(("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what)) }   # not part of a function's result
function Obs() { Parse (Invoke-Raw $script:port "observe" $script:token).Body }
function In([hashtable]$b) { Parse (Invoke-Raw $script:port "input" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false } }
function Interact([hashtable]$b) { Parse (Invoke-Raw $script:port "interact" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
function SelfState() {
    $ob = Obs; $is = Interact @{ action = "state" }
    return [ordered]@{ position = @($ob.self.region_position); flying = $ob.self.flying; controls = $ob.self.controls; seat = $ob.self.seat
        playing = (@($ob.self.playing) -join ","); sitting = $is.sitting; stand_button = $is.stand_button; stop_flying_button = $is.stop_flying_button }
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-52} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

function Start-Viewer([string]$tag, [string]$where) {
    $appdata = Join-Path $Sandbox "appdata-$tag"; $local = Join-Path $Sandbox "localappdata-$tag"; $log = Join-Path $Sandbox "run-$tag.log"
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "400", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    Note "viewer ($tag) started through godot.ps1, pid $($script:proc.Id)"
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { Note "no automation token file: stopping"; return $false }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $script:port = [int]$j.port; $script:token = [string]$j.token
    $login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $where } | ConvertTo-Json -Compress)
    Note "login ($tag, start $(if ($where -eq 'last') { 'last' } else { 'the usual start' })) requested: $($login.Status)"
    $stable = 0; $last = -1; $o = $null
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $o = Obs
        if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
            if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
            $last = $o.world.objects
            if ($stable -ge 3) { return $true }
        }
    }
    return [bool]($o -and $o.self)
}
function Stop-Viewer([string]$tag) {
    $logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
    $after = Parse $logout.Body
    Note "logout ($tag): $($logout.Status) ended=$($after.ended)"
    [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    Note "viewer ($tag) exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
    $script:port = $null; $script:token = $null
    return [bool]$after.ended
}
function Walk([double]$seconds) {
    $from = @((Obs).self.region_position)
    [void](Key "UP" $true); Start-Sleep -Milliseconds ([int]($seconds * 1000)); $mid = SelfState; [void](Key "UP" $false); Start-Sleep -Seconds 2
    $to = @((Obs).self.region_position)
    return [ordered]@{ from = $from; to = $to; moved = (Flat $from $to); while_walking = $mid }
}

function World([hashtable]$b) { Parse (Invoke-Raw $script:port "world_sounds" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }; return $false }

if (-not (Start-Viewer "sit" $Start)) { exit 5 }
Start-Sleep -Seconds 4

# ---- 1. sit on the nearest prim that offers Sit ----
$sat = $false
$found = @(@((Interact @{ action = "scan"; distance = 64 }).objects) | Sort-Object -Property { [double]$_.distance })
foreach ($obj in ($found | Select-Object -First 12)) {
    [void](Interact @{ action = "aim"; x = [double]$obj.x; y = [double]$obj.y; z = [double]$obj.z; distance = 6 }); Start-Sleep -Milliseconds 1500
    $r = Interact @{ action = "rightclick"; id = $obj.id }
    if ($r.pick.kind -eq "Object" -and (@($r.entries | Where-Object { $_.Id -eq "sit" -and $_.Enabled }).Count -gt 0)) {
        [void](Interact @{ action = "choose"; entry = "sit" })
        $sat = Wait-Until { (Interact @{ action = "state" }).sitting -eq $true } 8
        if ($sat) { $report.sat_on = $obj.id; break }
        [void](Interact @{ action = "stand" }); Start-Sleep -Seconds 2
    } else { [void](Interact @{ action = "choose"; entry = "" }) }
}
Check "1 sat on a prim near the start" $sat ("tried {0} objects" -f [math]::Min(12, $found.Count))
if ($sat) {
    Start-Sleep -Seconds 4
    $seat = World @{ action = "seat" }
    $report.seated = $seat
    Shot "seated"
    Note ("seated: pelvis {0:N3}, seat top {1}, pelvis over top {2}, own height {3:N3}, region height {4:N3}, pelvis to foot {5:N3}, hover {6:N3}" -f $seat.pelvis_z, $seat.seat_top, $seat.pelvis_over_top, $seat.body_height, $seat.region_height, $seat.pelvis_to_foot, $seat.hover)
    # sitting on a prim with a sit target the pelvis rests on or just above the top (the official viewer: the root at the region's place,
    # the sit pose's own pelvis drop after that); never a leg's length above it
    $over = $seat.pelvis_over_top
    Check "2 the pelvis sits on the prim, not a leg's length above it" ($null -ne $over -and [double]$over -lt 0.5 -and [double]$over -gt -0.3) ("pelvis over the top {0} m" -f $over)
    # the avatar.sit line is written once per sit, with every field
    $keys = @("sit_target", "sent_x", "sent_y", "sent_z", "seat_x", "seat_y", "seat_z", "seat_yaw_deg", "prim_top", "root_over_top", "pelvis_over_top",
        "knee_over_top", "sole_over_top", "thigh_deg", "body_height", "animation", "animation_kind", "animation_priority")
    $line = $seat.line
    $missing = @($keys | Where-Object { -not $line -or -not ($line.PSObject.Properties.Name -contains $_) })
    $logLines = @(Get-ChildItem -LiteralPath $Sandbox -Recurse -Filter "viewer.log" -ErrorAction SilentlyContinue | ForEach-Object { Select-String -LiteralPath $_.FullName -Pattern "avatar.sit " -SimpleMatch })
    $report.sit_log_lines = @($logLines | ForEach-Object { $_.Line })
    Check "2b one avatar.sit line, every field" ($seat.sit_logged -eq $true -and $missing.Count -eq 0 -and $logLines.Count -eq 1) ("logged {0}, log lines {1}, missing [{2}]" -f $seat.sit_logged, $logLines.Count, ($missing -join ","))
    [void](Interact @{ action = "stand" }); Start-Sleep -Seconds 3
}

# ---- 2. fly: the wind rises with speed ----
$calm = World @{ action = "wind" }
$report.wind_standing = $calm
[void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false); Start-Sleep -Milliseconds 400
[void](Key "PGUP" $true); Start-Sleep -Milliseconds 1500; [void](Key "PGUP" $false); Start-Sleep -Seconds 3
$hover = World @{ action = "wind" }
$report.wind_hovering = $hover
[void](Key "UP" $true); Start-Sleep -Milliseconds 700
$early = World @{ action = "wind" }
Start-Sleep -Milliseconds 2500
$fast = World @{ action = "wind" }
[void](Key "UP" $false); Start-Sleep -Seconds 3
$after = World @{ action = "wind" }
$report.wind_flying = [ordered]@{ early = $early; fast = $fast; stopped = $after }
Note ("wind: standing max {0:N3} gain {1:N3}; hovering max {2:N3} gain {3:N3} felt {4:N2}; flying early gain {5:N3} felt {6:N2}; flying fast gain {7:N3} felt {8:N2} freq {9:N1}; stopped gain {10:N3}; region wind {11}" -f $calm.max_gain, $calm.gain, $hover.max_gain, $hover.gain, $hover.felt_speed, $early.gain, $early.felt_speed, $fast.gain, $fast.felt_speed, $fast.frequency, $after.gain, $fast.region_wind)
Check "3 no wind is heard standing" ([double]$calm.max_gain -eq 0) ("max gain {0}" -f $calm.max_gain)
Check "4 the wind fades in while flying" ($hover.flying -eq $true -and [double]$hover.max_gain -gt 0) ("flying {0}, max gain {1}" -f $hover.flying, $hover.max_gain)
Check "5 the wind gets louder flying fast than hovering" ([double]$fast.felt_speed -gt [double]$hover.felt_speed + 3 -and [double]$fast.target_gain -gt [double]$hover.target_gain) ("felt {0:N2} -> {1:N2} m/s, target gain {2:N3} -> {3:N3}" -f $hover.felt_speed, $fast.felt_speed, $hover.target_gain, $fast.target_gain)

# ---- 3. Mono: an off-centre wind (flying sideways) on both sides alike ----
function Prefs([hashtable]$b) { Parse (Invoke-Raw $script:port "prefs" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Slide([bool]$down) { In @{ kind = "key"; key = "RIGHT"; down = $down; shift = $true; alt = $false; ctrl = $false } }
[void](Key "PGUP" $true); Start-Sleep -Milliseconds 5000; [void](Key "PGUP" $false); Start-Sleep -Seconds 1
[void](Prefs @{ action = "set"; key = "audio_mono"; value = $false }); Start-Sleep -Milliseconds 500
[void](Slide $true); Start-Sleep -Milliseconds 2500; $stereo = World @{ action = "wind" }; [void](Slide $false); Start-Sleep -Seconds 2
[void](Prefs @{ action = "set"; key = "audio_mono"; value = $true }); Start-Sleep -Milliseconds 500
[void](Slide $true); Start-Sleep -Milliseconds 2500; $mono = World @{ action = "wind" }; [void](Slide $false); Start-Sleep -Seconds 1
$sounds = World @{ action = "state" }
$report.mono = [ordered]@{ stereo = $stereo; mono = $mono; world_panning = $sounds.panning }
Note ("mono: stereo pan {0:N3} left {1:N4} right {2:N4}; mono pan {3:N3} left {4:N4} right {5:N4}; world voices panning {6}" -f $stereo.pan_right, $stereo.left_rms, $stereo.right_rms, $mono.pan_right, $mono.left_rms, $mono.right_rms, $sounds.panning)
$offCentre = [math]::Abs([double]$mono.pan_right - 0.5) -gt 0.1
$equal = [double]$mono.left_rms -gt 0 -and [math]::Abs([double]$mono.left_rms - [double]$mono.right_rms) -le 1e-6
Check "6 Mono on: an off-centre wind is equal left and right, sounds in the world unpanned" ($mono.mono -eq $true -and $offCentre -and $equal -and [double]$sounds.panning -eq 0) ("pan {0:N3}, left {1:N4}, right {2:N4}, world panning {3}" -f $mono.pan_right, $mono.left_rms, $mono.right_rms, $sounds.panning)
[void](Prefs @{ action = "set"; key = "audio_mono"; value = $false })

# ---- 4. falling: strong wind felt, but the official viewer mutes the wind unless flying ----
[void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false)
Start-Sleep -Milliseconds 600; $fall1 = World @{ action = "wind" }
Start-Sleep -Milliseconds 1900; $fall2 = World @{ action = "wind" }
$report.wind_falling = [ordered]@{ early = $fall1; later = $fall2 }
Note ("falling: early flying {0} felt {1:N2} max {2:N3}; later felt {3:N2} max {4:N3} gain {5:N3}" -f $fall1.flying, $fall1.felt_speed, $fall1.max_gain, $fall2.felt_speed, $fall2.max_gain, $fall2.gain)
Check "7 falling is silent: the loudest fades to 0 when not flying" ($fall1.flying -eq $false -and [double]$fall1.max_gain -lt [double]$fast.max_gain -and [double]$fall2.max_gain -eq 0) ("felt {0:N2} m/s, max {1:N3} -> {2:N3}" -f [math]::Max([double]$fall1.felt_speed, [double]$fall2.felt_speed), $fall1.max_gain, $fall2.max_gain)
Start-Sleep -Seconds 6

$report.logged_out = Stop-Viewer "sit"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "sit-wind-report.json") -Encoding utf8
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0

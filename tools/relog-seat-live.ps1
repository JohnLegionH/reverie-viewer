# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# relog-seat-live.ps1 - the live check for an avatar that logged out on a prim. Logs a test avatar in with the development
# export, sits it on a prim near the start, logs out there, logs it back in at its last location and tries to walk off
# (and, if the viewer offers Stand, presses Stand first). Reads back where the region put the avatar, what the viewer thinks
# it is doing (flying, sitting, the buttons offered), the control bits it sends and whether the avatar moved.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\relog-seat-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: relog-report.json in the sandbox, pictures in -ShotsDir.
# Exit: 0 she walked off, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [ValidateSet("seat", "air")][string]$Mode = "seat",   # seat: log out sitting on a prim; air: log out flying a few metres up
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

# ---- 1. sit on a prim near the start (or fly up in the air) and log out there ----
if (-not (Start-Viewer "sit" $Start)) { exit 5 }
Start-Sleep -Seconds 4
if ($Mode -eq "air") {
    $o0 = Obs
    [void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false); Start-Sleep -Milliseconds 400
    [void](Key "PGUP" $true); Start-Sleep -Milliseconds 1500; [void](Key "PGUP" $false); Start-Sleep -Seconds 2
    $report.in_air_before_logout = SelfState
    $up = [double]$report.in_air_before_logout.position[2] - [double]$o0.self.region_position[2]
    Check "1 she flies in the air before logging out" ($report.in_air_before_logout.flying -eq $true -and $up -gt 2) ("flying {0}, {1:N1} m up" -f $report.in_air_before_logout.flying, $up)
    $report.logged_out_1 = Stop-Viewer "sit"
    $sat = $true
} else {
$ob = Obs
$regionName = (($Start -replace '^uri:', '') -split '&')[0]
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 700 }; return $false }
function Hop([double]$x, [double]$y, [double]$z) {
    [void](Invoke-Raw $script:port "teleport" $script:token (@{ action = "region"; region = $regionName; x = $x; y = $y; z = $z } | ConvertTo-Json -Compress))
    $ok = Wait-Until { $o2 = Obs; $o2.self -and (Flat @($o2.self.region_position) @($x, $y)) -lt 20 -and $o2.stage -eq "InWorld" } 45
    Start-Sleep -Seconds 5
    return $ok
}
# the objects near the start, nearest first; for each, stand beside it, open its menu and sit if Sit is offered
$found = @(@((Interact @{ action = "scan"; distance = 96 }).objects) | Sort-Object -Property { [double]$_.distance })
Note "objects in reach of the start: $($found.Count)"
$sat = $false; $tried = 0
foreach ($obj in $found) {
    if ($sat -or $tried -ge 10) { break }
    $tried++
    if ([double]$obj.distance -gt 8) { [void](Hop ([double]$obj.x - 3) ([double]$obj.y) ([double]$obj.z + 1)) }
    [void](Interact @{ action = "aim"; x = [double]$obj.x; y = [double]$obj.y; z = [double]$obj.z; distance = 6 }); Start-Sleep -Milliseconds 1500
    $r = Interact @{ action = "rightclick"; id = $obj.id }
    Start-Sleep -Milliseconds 1500
    if ($r.pick.kind -eq "Object" -and (@($r.entries | Where-Object { $_.Id -eq "sit" -and $_.Enabled }).Count -gt 0)) {
        [void](Interact @{ action = "choose"; entry = "sit" })
        $sat = Wait-Until { (Interact @{ action = "state" }).sitting -eq $true } 8
        Note ("object {0} at ({1:N0}, {2:N0}, {3:N0}): Sit offered, sitting {4}" -f $tried, $obj.x, $obj.y, $obj.z, $sat)
        if (-not $sat) { [void](Interact @{ action = "stand" }); Start-Sleep -Seconds 2 }
    } else { [void](Interact @{ action = "choose"; entry = "" }) }
}
# no object with contents offered Sit: try points around the start at knee and waist height (furniture, walls, floors of other prims)
if (-not $sat) {
    if ($found.Count -gt 0) { [void](Hop ([double]$ob.self.region_position[0]) ([double]$ob.self.region_position[1]) ([double]$ob.self.region_position[2] + 1)) }
    $o3 = Obs
    $px = [double]$o3.self.region_position[0]; $py = [double]$o3.self.region_position[1]; $pz = [double]$o3.self.region_position[2]
    foreach ($radius in 2, 4, 6, 9, 12, 16) {
        foreach ($dz in 0.0, 0.6, -0.6) {
            foreach ($deg in 0, 45, 90, 135, 180, 225, 270, 315) {
                if ($sat) { break }
                $a = $deg * [math]::PI / 180
                $r = Interact @{ action = "rightclick"; x = ($px + $radius * [math]::Cos($a)); y = ($py + $radius * [math]::Sin($a)); z = ($pz + $dz) }
                $tried++
                if ($r.pick.kind -eq "Object" -and (@($r.entries | Where-Object { $_.Id -eq "sit" -and $_.Enabled }).Count -gt 0)) {
                    [void](Interact @{ action = "choose"; entry = "sit" })
                    $sat = Wait-Until { (Interact @{ action = "state" }).sitting -eq $true } 8
                    if (-not $sat) { [void](Interact @{ action = "stand" }); Start-Sleep -Seconds 2 }
                } else { [void](Interact @{ action = "choose"; entry = "" }) }
            }
        }
    }
}
Note "prim search: $tried tried, sat $sat"
$report.seated_before_logout = SelfState
Shot "relog_1_seated"
Check "1 she sits on a prim before logging out" $sat ("sitting {0}, seat {1}, at {2}" -f $report.seated_before_logout.sitting, $report.seated_before_logout.seat, ($report.seated_before_logout.position -join ","))
$report.logged_out_1 = Stop-Viewer "sit"
}
if (-not $sat) { $report.checks = $checks; $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "relog-report.json") -Encoding utf8; exit 1 }

# ---- 2. log back in at the last location and walk off ----
if (-not (Start-Viewer "relog" "last")) { exit 5 }
Start-Sleep -Seconds 3
$report.arrived = SelfState
Note ("arrived: at {0}; flying {1}; sitting {2}; seat {3}; stand button {4}; stop flying button {5}; controls {6}; playing {7}" -f ($report.arrived.position -join ","), $report.arrived.flying, $report.arrived.sitting, $report.arrived.seat, $report.arrived.stand_button, $report.arrived.stop_flying_button, $report.arrived.controls, $report.arrived.playing)
Shot "relog_2_arrived"
Check "2 the viewer does not think she flies" ($report.arrived.flying -eq $false) ("flying {0}, controls {1}" -f $report.arrived.flying, $report.arrived.controls)
$walk = Walk 3
$report.walk_1 = $walk
Note ("walk 1: moved {0:N2} m; while walking: flying {1}, controls {2}, playing {3}" -f $walk.moved, $walk.while_walking.flying, $walk.while_walking.controls, $walk.while_walking.playing)
$walked = $walk.moved -gt 1.0
if (-not $walked) {
    $st = SelfState
    $report.before_stand = $st
    if ($st.stand_button) {
        [void](Interact @{ action = "stand" }); Start-Sleep -Seconds 3
        $walk2 = Walk 3
        $report.walk_after_stand = $walk2
        Note ("walk after Stand: moved {0:N2} m" -f $walk2.moved)
        $walked = $walk2.moved -gt 1.0
    }
}
Shot "relog_3_after_walk"
Check "3 she walks off (directly, or after Stand where Stand is offered)" $walked ("first walk {0:N2} m" -f $walk.moved)
$report.completed = $true
$report.logged_out_2 = Stop-Viewer "relog"
$report.checks = $checks
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "relog-report.json") -Encoding utf8
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

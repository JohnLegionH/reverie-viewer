# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# sync-live.ps1 - the live check of the sky pick, the camera's farthest zoom and a jump. Logs a test avatar in with the development
# export, picks Midday (Legacy) through the Environment menu's command and takes a picture, zooms the rear view out as far as it goes and
# records the distance, zooms an unlocked view out as far as it goes, jumps once with the jump key and records the height reached
# and whether the finish-animation bit went to the region, walks 10 m, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sync-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in camera-live.ps1. Run it under a live-login claim and an avatar claim on the
# account. Output: sync-report.json in the sandbox, pictures in -ShotsDir. Exit: 0 done, 3 blocked, 5 the viewer or the login did not come up.
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
Write-Output "LIVE SYNC RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)); Note "picture $name" }
function Cam([string]$op, [double]$value = 0) { Parse (Invoke-Raw $port "orbit" $token (@{ op = $op; value = $value } | ConvertTo-Json -Compress)).Body }
function Pos { $ob = Parse (Invoke-Raw $port "observe" $token).Body; if ($ob -and $ob.self -and $ob.self.region_position) { return @([double]$ob.self.region_position[0], [double]$ob.self.region_position[1], [double]$ob.self.region_position[2]) }; return $null }
$report = [ordered]@{ account = $Account; sky = $null; rear_zoom_limit_m = $null; unlocked_zoom_limit_m = $null; jump = $null; walk_10m = $null; completed = $false }

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 5
    # 1 the sky
    $r = Parse (Invoke-Raw $port "sky" $token '{"choice":"legacy noon"}').Body
    $report.sky = [ordered]@{ in_force = $r.choice; failed = [bool]$r.failed; why = $r.why }
    Note "sky: in force $($r.choice), failed $([bool]$r.failed)"
    Start-Sleep -Seconds 6
    Shot "sync_midday_legacy"
    # 2 the rear view zoomed out as far as it goes
    [void](Cam "reset"); Start-Sleep -Milliseconds 800
    $prev = -1.0
    for ($k = 0; $k -lt 12; $k++) { [void](Cam "wheel" 20); Start-Sleep -Milliseconds 700; $s = Cam "state"; if ([math]::Abs($s.distance - $prev) -lt 0.001) { break }; $prev = $s.distance }
    $s = Cam "state"
    $report.rear_zoom_limit_m = [ordered]@{ distance = [double]$s.distance; focus_distance = [double]$s.focus_distance; zoom = [double]$s.zoom }
    Note ("rear view farthest: distance {0:N3} m, zoom {1}" -f $s.distance, $s.zoom)
    Shot "sync_zoom_rear_farthest"
    # 3 an unlocked view zoomed out as far as it goes
    [void](Cam "reset"); Start-Sleep -Milliseconds 800; [void](Cam "unlock"); Start-Sleep -Milliseconds 500
    $prev = -1.0
    for ($k = 0; $k -lt 12; $k++) { [void](Cam "wheel" 20); Start-Sleep -Milliseconds 700; $s = Cam "state"; if ([math]::Abs($s.focus_distance - $prev) -lt 0.001) { break }; $prev = $s.focus_distance }
    $s = Cam "state"
    $report.unlocked_zoom_limit_m = [ordered]@{ focus_distance = [double]$s.focus_distance }
    Note ("unlocked view farthest: focus distance {0:N3} m" -f $s.focus_distance)
    Shot "sync_zoom_unlocked_farthest"
    [void](Cam "reset"); Start-Sleep -Milliseconds 800
    # on the ground first
    for ($k = 0; $k -lt 30; $k++) {
        $ob = Parse (Invoke-Raw $port "observe" $token).Body
        if (-not $ob.self.flying) { break }
        [void](Invoke-Raw $port "keys" $token '{"key":"C","down":true,"control":false}'); Start-Sleep -Milliseconds 700
    }
    [void](Invoke-Raw $port "keys" $token '{"key":"C","down":false,"control":false}'); Start-Sleep -Seconds 3
    # 4 one jump
    $z0 = (Pos)[2]; $zmax = $z0
    [void](Invoke-Raw $port "motion" $token '{"action":"start"}'); [void](Invoke-Raw $port "motion" $token '{"action":"take"}')
    [void](Invoke-Raw $port "keys" $token '{"key":"E","down":true,"control":false}'); Start-Sleep -Milliseconds 150
    [void](Invoke-Raw $port "keys" $token '{"key":"E","down":false,"control":false}')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 4) { $q = Pos; if ($q -and $q[2] -gt $zmax) { $zmax = $q[2] }; Start-Sleep -Milliseconds 60 }
    $take = Parse (Invoke-Raw $port "motion" $token '{"action":"take"}').Body
    [void](Invoke-Raw $port "motion" $token '{"action":"stop"}')
    $bits = @(); $finish = 0
    foreach ($e in @($take.sent)) { if ("$($e[1])" -eq "controls") { $c = [uint32]$e[2]; if (($c -band 0x8000) -ne 0) { $finish++ } } }
    $report.jump = [ordered]@{ start_z = $z0; peak_z = $zmax; height_m = [math]::Round($zmax - $z0, 3); updates_sent = @($take.sent).Count; updates_with_finish_animation = $finish }
    Note ("jump: height {0:N3} m, updates sent {1}, with the finish-animation bit {2}" -f ($zmax - $z0), @($take.sent).Count, $finish)
    Start-Sleep -Seconds 2
    # 5 walk 10 m
    $a = Pos
    [void](Invoke-Raw $port "keys" $token '{"key":"W","down":true,"control":false}')
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $moved = 0.0
    while ($sw.Elapsed.TotalSeconds -lt 20 -and $moved -lt 10.0) { Start-Sleep -Milliseconds 100; $b = Pos; if ($b) { $moved = [math]::Sqrt([math]::Pow($b[0] - $a[0], 2) + [math]::Pow($b[1] - $a[1], 2)) } }
    [void](Invoke-Raw $port "keys" $token '{"key":"W","down":false,"control":false}'); Start-Sleep -Seconds 1
    $report.walk_10m = [ordered]@{ metres = [math]::Round($moved, 2); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
    Note ("walk: {0:N2} m in {1:N1} s" -f $moved, $sw.Elapsed.TotalSeconds)
    $report.completed = $true
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "sync-report.json") -Encoding utf8
Write-Output "LIVE SYNC RUN DONE - other work may run again"
exit 0

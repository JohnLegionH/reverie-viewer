# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# login-start-live.ps1 - logs a test avatar in to a start location with the development export and checks what a person sees on arrival:
# the avatar is in the scene, the camera sits behind it close by (not high above the land), no control holds the keyboard focus (world_has_keyboard is only reported:
# it is false while the test window is not the foreground window), and
# a walk key moves the avatar. The checks run as soon as the avatar is in the scene, with the time it took.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\login-start-live.ps1 -Sandbox <new folder> -Start <last|home|uri:...>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Exe <development export>] [-ArriveWithin 5]
#     [-Teleport <region name>,<x>,<y>,<z>] [-SetHome] [-CharacterDir <folder>]
#
# -Teleport: after the checks, teleports to that place (the next run's "last location"), waits for arrival, and logs out.
# Run it under a live-login claim and an avatar claim on the account. Exit: 0 passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$Start,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Exe = "",
    [string]$CharacterDir = $env:NV_CHARACTER_DIR,
    [double]$ArriveWithin = 5,
    [string]$Teleport = "",
    [switch]$SetHome,
    [int]$StartSeconds = 60
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if ($CharacterDir) { $env:NV_CHARACTER_DIR = $CharacterDir }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
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
$logDir = Join-Path $Sandbox "viewer-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$env:NV_LOG_DIR = $logDir
Write-Output "LIVE LOGIN RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0",
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
Note "login requested ($Start): $($login.Status)"

# 1. the world
$o = $null; $inWorldAt = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline -and $null -eq $inWorldAt) {
    Start-Sleep -Milliseconds 500
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld") { $inWorldAt = $clock.Elapsed.TotalSeconds }
}
$results = [ordered]@{}
$ok = $false
if ($null -ne $inWorldAt) {
    Note "in world"
    # 2. the avatar is in the scene: how long after the world, up to ArriveWithin seconds
    $selfAt = $null
    while ($null -eq $selfAt -and ($clock.Elapsed.TotalSeconds - $inWorldAt) -lt $ArriveWithin) {
        $o = Parse (Invoke-Raw $port "observe" $token).Body
        if ($o -and $o.self) { $selfAt = $clock.Elapsed.TotalSeconds } else { Start-Sleep -Milliseconds 250 }
    }
    $results.avatar_in_scene = ($null -ne $selfAt)
    $results.avatar_in_scene_after_s = if ($null -ne $selfAt) { [math]::Round($selfAt - $inWorldAt, 1) } else { $null }
    if ($null -eq $selfAt) { Note ("NO AVATAR in the scene within $ArriveWithin s; self_missing: " + ($o.self_missing | ConvertTo-Json -Compress)) }
    # 3. the camera is behind the avatar and close
    $cam = Parse (Invoke-Raw $port "orbit" $token '{"op":"state"}').Body
    if ($null -ne $selfAt -and $cam -and $o.self.drawn_soles) {
        $soles = $o.self.drawn_soles; $eye = $cam.eye
        $dx = $eye[0] - $soles[0]; $dy = $eye[1] - $soles[1]; $dz = $eye[2] - $soles[2]
        $dist = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
        $yaw = [double]$o.self.drawn_heading_deg * [math]::PI / 180
        $behind = ($dx * [math]::Cos($yaw) + $dy * [math]::Sin($yaw)) -lt 0
        $results.camera_distance_m = [math]::Round($dist, 1)
        $results.camera_behind = $behind
        $results.camera_close = ($dist -lt 20)
        Note ("camera {0:N1} m from the avatar, behind: {1}, focus on avatar: {2}" -f $dist, $behind, $cam.focus_on_avatar)
    } else { $results.camera_close = $false; $results.camera_behind = $false }
    # 4. the keyboard: nothing holds focus, and a walk key moves the avatar
    $results.world_has_keyboard = [bool]$o.self.focused
    $typing = Parse (Invoke-Raw $port "typing" $token (@{ action = "state" } | ConvertTo-Json -Compress)).Body
    $results.focus_owner = if ($typing) { [string]$typing.focus_owner } else { "unknown" }
    if ($null -ne $selfAt) {
        $before = $o.self.region_position
        [void](Invoke-Raw $port "keys" $token '{"key":"W","down":true}')
        Start-Sleep -Seconds 3
        [void](Invoke-Raw $port "keys" $token '{"key":"W","down":false}')
        Start-Sleep -Milliseconds 800
        $o3 = Parse (Invoke-Raw $port "observe" $token).Body
        $after = $o3.self.region_position
        $moved = if ($before -and $after) { [math]::Sqrt([math]::Pow($after[0] - $before[0], 2) + [math]::Pow($after[1] - $before[1], 2)) } else { 0 }
        $results.walked_m = [math]::Round($moved, 1)
        $results.walk_key_moves = ($moved -gt 1.0)
        Note ("walk key moved the avatar {0:N1} m" -f $moved)
    } else { $results.walk_key_moves = $false }
    [void](Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = "arrival" } | ConvertTo-Json -Compress))
    if ($SetHome) { $sh = Invoke-Raw $port "teleport" $token (@{ action = "set_home" } | ConvertTo-Json -Compress); Note "set home requested: $($sh.Status) $($sh.Body)"; Start-Sleep -Seconds 4 }
    if ($Teleport) {
        $t = $Teleport.Split(",")
        $tp = Invoke-Raw $port "teleport" $token (@{ action = "region"; region = $t[0]; x = [double]$t[1]; y = [double]$t[2]; z = [double]$t[3] } | ConvertTo-Json -Compress)
        Note "teleport requested: $($tp.Status)"
        Start-Sleep -Seconds 25
    }
    $ok = $results.avatar_in_scene -and $results.camera_close -and $results.camera_behind -and $results.walk_key_moves -and ($results.focus_owner -eq "")
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
Note "logout: $($logout.Status)"
Start-Sleep -Seconds 3
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note ("RESULT {0}: {1}" -f $(if ($ok) { "PASS" } else { "FAIL" }), ($results | ConvertTo-Json -Compress))
if ($ok) { exit 0 } else { exit 1 }

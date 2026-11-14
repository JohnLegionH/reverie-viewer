# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# avatar-dump-live.ps1 - the live check of the own-avatar dump. Logs a test avatar in with the development export and waits for the dump the viewer
# writes next to its log (viewer.avatar.json) once the avatar is drawn, then checks that every field of it is filled: the shape dump, the appearance
# the region sent, the rigged meshes worn and the joints they move, the character folder with its hash, the window, the 3D view and the camera.
# It takes no picture and changes nothing in the world.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\avatar-dump-live.ps1 -Sandbox <new folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar claim on the account. The
# body is drawn from NV_CHARACTER_DIR when it is set, else from the character folder the export carries; the report names the folder the viewer used. Output: avatar-dump-report.json in the sandbox, and the dump itself is left where
# the viewer wrote it (reported). Exit: 0 every field filled, 1 a field is empty, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [int]$DumpSeconds = 90
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
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$shippedCharacter = Join-Path (Split-Path -Parent $Exe) "character"
if ($env:NV_CHARACTER_DIR) {
    if (-not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR -PathType Container)) { $problems.Add("NV_CHARACTER_DIR does not name a folder") }
} elseif (-not (Test-Path -LiteralPath $shippedCharacter -PathType Container)) {
    $problems.Add("no character folder: NV_CHARACTER_DIR is unset and the export carries no character folder (without one no body is drawn)")
}
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
Write-Output "LIVE AVATAR DUMP RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

# the dump is written by the viewer a moment after the avatar is drawn; read the file where it lands, until every field is filled or the time is up
$dumpPath = $null; $dump = $null; $empty = @("not written yet")
function Is-Filled($v) { return ($null -ne $v) -and -not (($v -is [string]) -and ($v.Length -eq 0)) }
function Test-Dump($d) {
    $missing = New-Object System.Collections.Generic.List[string]
    $f = @{
        "shape_dump.weights_hash"            = $d.shape_dump.weights_hash
        "shape_dump.weight_count"            = $(if ($d.shape_dump.weight_count -gt 0) { $d.shape_dump.weight_count } else { $null })
        "shape_dump.morph_surface_hashes"    = $(if (@($d.shape_dump.morph_surface_hashes).Count -gt 0) { "set" } else { $null })
        "shape_dump.joints_in_head_frame_mm" = $(if ($d.shape_dump.joints_in_head_frame_mm) { "set" } else { $null })
        "shape_dump.head_turn_tilt_roll_deg" = $(if (@($d.shape_dump.head_turn_tilt_roll_deg).Count -eq 3) { "set" } else { $null })
        "appearance.received"                = $(if ($d.appearance.received -eq $true) { "true" } else { $null })
        "appearance.visual_params"           = $(if ($d.appearance.visual_params -gt 0) { $d.appearance.visual_params } else { $null })
        "worn.rigged_objects"                = $(if ($null -ne $d.worn -and $null -ne $d.worn.rigged_objects) { "set" } else { $null })
        "worn.joint_overrides"               = $(if ($null -ne $d.worn -and $null -ne $d.worn.joint_overrides) { "set" } else { $null })
        "character_folder.given"             = $(if ($d.character_folder.given -eq $true) { "true" } else { $null })
        "character_folder.hash_sha256"       = $(if ($d.character_folder.hash_sha256 -and "$($d.character_folder.hash_sha256)".Length -eq 64) { "set" } else { $null })
        "character_folder.files"             = $(if ($d.character_folder.files -gt 0) { $d.character_folder.files } else { $null })
        "window.size"                        = $(if ($d.window.size[0] -gt 0 -and $d.window.size[1] -gt 0) { "set" } else { $null })
        "window.content_scale_factor"        = $(if ($d.window.content_scale_factor -gt 0) { "set" } else { $null })
        "view_3d.size"                       = $(if ($d.view_3d.size[0] -gt 0 -and $d.view_3d.size[1] -gt 0) { "set" } else { $null })
        "view_3d.aspect"                     = $(if ($d.view_3d.aspect -gt 0) { "set" } else { $null })
        "camera.projection_aspect"           = $(if ($d.camera.projection_aspect -gt 0) { "set" } else { $null })
        "camera.vertical_fov_degrees"        = $(if ($d.camera.vertical_fov_degrees -gt 0) { "set" } else { $null })
    }
    foreach ($k in ($f.Keys | Sort-Object)) { if (-not (Is-Filled $f[$k])) { $missing.Add($k) } }
    return $missing
}
$deadline = (Get-Date).AddSeconds($StartSeconds + $DumpSeconds + 90)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (-not $dumpPath) { $candidate = Join-Path $local "ReverieViewer\Logs\viewer.avatar.json"; if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dumpPath = $candidate } }
    if ($dumpPath) {
        $d = $null
        try { $d = Get-Content -Raw -LiteralPath $dumpPath | ConvertFrom-Json } catch { }
        if ($d) { $dump = $d; $empty = @(Test-Dump $d); if ($empty.Count -eq 0) { break } }
    }
}
$filled = $dump -and $empty.Count -eq 0
Note "dump file: $(if ($dumpPath) { $dumpPath } else { 'not written' }); empty fields: $(if ($empty.Count) { $empty -join ', ' } else { 'none' })"
# keep a copy of the dump with the report (the sandbox is moved aside later; the viewer's own file stays where it was written)
$report = [ordered]@{
    account = $Account; dump_path = $dumpPath; every_field_filled = [bool]$filled; empty_fields = $empty
    worn_rigged_objects = $dump.worn.rigged_objects; worn_overridden_joints = @($dump.worn.joint_overrides.Joints).Count
    character_files = $dump.character_folder.files; character_source = $dump.character_folder.source; character_path = $dump.character_folder.path
    character_hash_sha256 = $dump.character_folder.hash_sha256; window = $dump.window.size; view_3d = $dump.view_3d.size
    projection_aspect = $dump.camera.projection_aspect; vertical_fov_degrees = $dump.camera.vertical_fov_degrees
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Sandbox "avatar-dump-report.json") -Encoding utf8
Write-Output "LIVE AVATAR DUMP RUN DONE - other work may run again"
if (-not $filled) { exit 1 }
exit 0

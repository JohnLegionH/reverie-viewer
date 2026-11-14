# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# face-mask-live.ps1 - the live half of the face outline comparison. Logs a test avatar in with the development export, aims the camera at her
# own head from the front (the aim command: head point, -Distance metres, level, the -FovDeg vertical field), and takes -Count rounds of three
# pictures a few seconds apart: the outline of her body alone with the parts her worn objects hide (body_mask with hidden_too: flat white on black, nothing else drawn), the outline of only the parts she shows, the normal picture from the
# same camera, and the shape dump the body is drawn with. Each outline's reply (camera, drawn head joint, picture size) is saved beside it, so the
# render harness's face-shape case can put its camera at the same place from the same head joint (NV_FACE_MASK_LIVE). Changes nothing in the
# world: only the camera moves.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\face-mask-live.ps1 -Sandbox <new folder> -AgentId <the account's agent id>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#     [-Resolution 1600x1000] [-Distance 1.0] [-FovDeg 60] [-Count 3]
#
# Settings come from the parameters, the environment or the local live.env file (live-env.ps1). Run it under a live-login claim and an avatar claim on the account.
# Output in the sandbox: mask-<n>.png and mask-<n>.json, visible-<n>.png and visible-<n>.json, shot-<n>.png, dump-<n>.json, aim.json and face-mask-report.json.
# Exit: 0 every round taken, 1 a round failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$AgentId,
    [string]$Credentials = "",
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = "",
    [string]$Start = "",
    [string]$Exe = "",
    [string]$Resolution = "1600x1000",
    [double]$Distance = 1.0,
    [double]$FovDeg = 60,
    [int]$Count = 3,
    [int]$StartSeconds = 60
)
$ErrorActionPreference = "Stop"
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
. (Join-Path $PSScriptRoot "claim-guard.ps1")
. (Join-Path $PSScriptRoot "aim-lib.ps1")
Assert-ClaimGranted
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $env:NV_CHARACTER_DIR -or -not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR -PathType Container)) { $problems.Add("NV_CHARACTER_DIR must name the character folder: without it no body is drawn") }
if ($Resolution -notmatch '^\d+x\d+$') { $problems.Add("-Resolution must be WIDTHxHEIGHT") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

function Invoke-Raw([int]$port, [string]$command, [string]$token, [string]$body = "{}") { Invoke-AimRaw $port $token $command $body }
function Parse([string]$json) { try { return $json | ConvertFrom-Json } catch { return $null } }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE FACE OUTLINE RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan --resolution $Resolution -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "420", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started through godot.ps1, pid $($p.Id)"
$port = $null; $token = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token '{"action":"logout"}'); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
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
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self -and $o.self.shape_dump) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
}
$report = [ordered]@{ account = $Account; resolution = $Resolution; distance = $Distance; fov_deg = $FovDeg; rounds = @(); completed = $false }
$ok = $true
if (-not ($o -and $o.stage -eq "InWorld" -and $o.self.shape_dump)) {
    Note "not in world with a drawn body: stopping"; $ok = $false
} else {
    $aim = Send-Aim $port $token @{ target = "avatar"; id = $AgentId; point = "head"; distance = $Distance; around_deg = 0; above_deg = 0; fov_deg = $FovDeg; min_fill = 0.05 }
    $st = Wait-AimApplied $port $token 15
    $st | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Sandbox "aim.json") -Encoding utf8
    Note ("aimed: {0}, applied: {1}" -f $(if ($st) { $st.aimed } else { "-" }), $(if ($st) { $st.engine.applied } else { "-" }))
    if (-not ($st -and $st.aimed -and $st.engine.applied)) { $ok = $false }
    for ($n = 1; $ok -and $n -le $Count; $n++) {
        Start-Sleep -Seconds 3
        $m = Invoke-Raw $port "body_mask" $token (@{ dir = $Sandbox; name = "mask-$n"; hidden_too = $true } | ConvertTo-Json -Compress)
        $v = Invoke-Raw $port "body_mask" $token (@{ dir = $Sandbox; name = "visible-$n" } | ConvertTo-Json -Compress)
        $v.Body | Set-Content -LiteralPath (Join-Path $Sandbox "visible-$n.json") -Encoding utf8
        $m.Body | Set-Content -LiteralPath (Join-Path $Sandbox "mask-$n.json") -Encoding utf8
        $s = Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = "shot-$n" } | ConvertTo-Json -Compress)
        $d = (Parse (Invoke-Raw $port "observe" $token).Body).self.shape_dump
        $d | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Sandbox "dump-$n.json") -Encoding utf8
        $round = [ordered]@{ n = $n; mask_status = $m.Status; shot_status = $s.Status; head_turn_tilt_roll = $d.head_turn_tilt_roll_deg; neck_turn_tilt_roll = $d.neck_turn_tilt_roll_deg }
        $report.rounds += $round
        Note ("round {0}: mask {1}, picture {2}, head turn/tilt/roll {3}" -f $n, $m.Status, $s.Status, (($d.head_turn_tilt_roll_deg | ForEach-Object { "{0:N2}" -f $_ }) -join "/"))
        if ($m.Status -ne 200 -or $s.Status -ne 200) { $ok = $false }
    }
    Stop-Aim $port $token
}
[void](Invoke-Raw $port "session" $token '{"action":"logout"}')
$ended = $false
for ($i = 0; $i -lt 40 -and -not $ended; $i++) { Start-Sleep -Milliseconds 500; $ss = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body; if ($ss -and ($ss.ended -or $ss.stage -eq "LoggedOut")) { $ended = $true } }
try { [void](Invoke-Raw $port "quit" $token) } catch { }
$p.WaitForExit(60000) | Out-Null
$report.logged_out = $ended
$report.viewer_exited = $p.HasExited
$report.completed = $ok
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "face-mask-report.json") -Encoding utf8
Note ("DONE - completed {0}, logged out {1}, viewer exited {2}" -f $ok, $ended, $p.HasExited)
if ($ok) { exit 0 } else { exit 1 }

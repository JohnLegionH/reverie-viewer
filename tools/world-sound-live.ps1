# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# world-sound-live.ps1 - the live check of sounds in the world. Starts the development export, logs a test avatar in, walks it forward
# for 4 seconds with its movement key, and checks through the automation interface (world_sounds) that footsteps were played at the
# official rate (a step at each foot plant) from the grid's own land step; then it stands on a flat prim near by (a teleport inside the
# region) and walks again, and checks the steps play that prim's material step, from the grid or our own step where the grid has none.
# It records what the grid answers for the land step and the stone step (status, type, size, first bytes). It also lists any object sound playing within hearing (an object's own
# loop or a one-shot from the region), with where it plays and at what gain; none in reach is reported, not failed. Nothing is
# rezzed or changed. Then it logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\world-sound-live.ps1 -Sandbox <new folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: world-sound-report.json in the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked,
# 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90
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
function World([hashtable]$b) { Parse (Invoke-Raw $port "world_sounds" $token ($b | ConvertTo-Json -Compress)).Body }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
Write-Output "LIVE WORLD SOUND RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-56} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false; object_sounds = @() }

$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$o = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld" -and $o.self) { break }
    $o = $null
}
if ($o) {
    Note "in world"
    # the own avatar must be drawn (its feet are read from the drawn skeleton) before walking
    $state = $null
    $until = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $until) { $state = World @{ action = "state" }; if ($state.in_world) { break }; Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 8
    # what the grid answers for the official land step and the stone step, through the viewer's own asset path
    $landStep = "e8af4a28-aa83-4310-a7c4-c047e15ea0df"; $stoneStep = "25a863e8-dc42-4e8a-a357-e76422ace9b5"
    foreach ($sid in @($landStep, $stoneStep)) {
        $pr = $null
        for ($k = 0; $k -lt 40; $k++) { $pr = World @{ action = "probe"; sound = $sid }; if (-not $pr.pending) { break }; Start-Sleep -Milliseconds 250 }
        Note ("probe {0}: status {1}, {2}, {3} bytes, starts {4}" -f $sid, $pr.status, $pr.content_type, $pr.size, $pr.first_bytes)
        $report["probe_$sid"] = $pr
    }
    # a walk of 4 s: the official rule makes a step at each foot plant, two a walk cycle (about 0.93 s), so 6 or more
    function Walk([string]$label) {
        [void](World @{ action = "clear" })
        $before = [int](World @{ action = "state" }).started
        [void](Invoke-Raw $port "keys" $token (@{ key = "W"; down = $true } | ConvertTo-Json -Compress))
        Start-Sleep -Seconds 4
        $mid = World @{ action = "state" }
        [void](Invoke-Raw $port "keys" $token (@{ key = "W"; down = $false } | ConvertTo-Json -Compress))
        Start-Sleep -Seconds 2
        $after = World @{ action = "state" }
        (World @{ action = "feet" }) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Sandbox "feet-$label.json") -Encoding utf8
        $steps = @($after.recent | Where-Object { $_.origin -eq "Step" })
        $playingSteps = @($mid.sources | Where-Object { $_.origin -eq "Step" -and $_.state -eq "Playing" })
        return @{ after = $after; steps = $steps; accepted = @($steps | Where-Object { $_.outcome -eq "accepted" }); fallback = @($steps | Where-Object { $_.outcome -eq "fallback" });
            started = [int]$after.started - $before; playing = $playingSteps.Count; sounds = @($steps | ForEach-Object { $_.sound } | Select-Object -Unique) }
    }
    # a first walk, not checked: an account may start on a raised prim and walk off it; the checked walk is on the land below
    [void](Walk "settle")
    Start-Sleep -Seconds 3
    $land = Walk "land"
    $notOnGrid = @($land.steps | Where-Object { $_.outcome -eq "not on grid" })
    Check "1 a 4 s walk on land plays footsteps at the official rate" ($land.accepted.Count -ge 6 -and $land.started -ge 6) ("steps accepted {0}, voices started {1}, playing mid-walk {2}, step sounds {3}" -f $land.accepted.Count, $land.started, $land.playing, ($land.sounds -join ","))
    Check "1b the land steps play the grid's own land step" ($notOnGrid.Count -eq 0 -and $land.fallback.Count -eq 0 -and $land.sounds -contains $landStep) ("not on grid {0}, fallback {1}" -f $notOnGrid.Count, $land.fallback.Count)
    $report.land_walk = @{ accepted = $land.accepted.Count; started = $land.started; playing = $land.playing; sounds = $land.sounds }
    # a walk on an object: a flat prim near by (a floor or platform), stood on by a teleport inside the region
    $spot = World @{ action = "stand_on_object" }
    $report.object_spot = $spot
    if ($spot.found -and $spot.sent) {
        Note ("standing on an object: material {0} at ({1:N1}, {2:N1}, {3:N1}), {4:N1} x {5:N1} m" -f $spot.material, $spot.x, $spot.y, $spot.z, $spot.size_x, $spot.size_y)
        Start-Sleep -Seconds 8
        $obj = Walk "object"
        Check "2 a 4 s walk on an object plays footsteps at the official rate" ($obj.accepted.Count -ge 6 -and $obj.started -ge 6) ("steps accepted {0}, voices started {1}, playing mid-walk {2}, step sounds {3}" -f $obj.accepted.Count, $obj.started, $obj.playing, ($obj.sounds -join ","))
        Check "2b the object steps play the material's step, from the grid or our fallback" (($obj.sounds.Count -ge 1) -and ($obj.sounds -notcontains $landStep) -and @($obj.steps | Where-Object { $_.outcome -eq "not on grid" }).Count -eq 0) ("fallback {0}, not on grid {1}" -f $obj.fallback.Count, @($obj.steps | Where-Object { $_.outcome -eq "not on grid" }).Count)
        $report.object_walk = @{ accepted = $obj.accepted.Count; fallback = $obj.fallback.Count; started = $obj.started; playing = $obj.playing; sounds = $obj.sounds; material = $spot.material }
        $after = $obj.after
    }
    else { Note "2 walk on an object: no flat prim of 1.5 m or more within 100 m (reported, not a failure)"; $after = $land.after }
    $objects = @($after.sources | Where-Object { $_.origin -in @("Attached", "Trigger") })
    $playing = @($objects | Where-Object { $_.state -eq "Playing" })
    $report.object_sounds = @($objects | ForEach-Object { [ordered]@{ origin = $_.origin; state = $_.state; gain = $_.gain; loop = $_.loop; why = $_.why; x = $_.x; y = $_.y; z = $_.z } })
    if ($objects.Count -eq 0) { Note "2 object sounds: none in reach (reported, not a failure)" }
    else { Check "2 an object's sound in reach plays" ($playing.Count -ge 1) ("{0} object sources, {1} playing" -f $objects.Count, $playing.Count) }
    $report.completed = $true
}
else { Note "not in the world within 150 s" }
$report.checks = $checks
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$afterLogout = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($afterLogout.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$afterLogout.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "world-sound-report.json") -Encoding utf8
Write-Output "LIVE WORLD SOUND RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

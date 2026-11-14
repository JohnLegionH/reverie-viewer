# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# sound-live.ps1 - the live check of a grid's own interface sound. Starts the development export with the Click sound set to the
# grid's (a fresh settings file), logs a test avatar in, and checks through the automation interface that the grid's click sound was
# fetched, decoded and played: before login it plays our file; in the world the page's status leaves "fetching", and a play of
# Click is played from the grid's Ogg Vorbis asset (the engine decoded it) and reaches the engine's player. Then it logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sound-live.ps1 -Sandbox <new folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: sound-report.json in the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer
# or the login did not come up.
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
function Sounds([hashtable]$b) { Parse (Invoke-Raw $port "ui_sounds" $token ($b | ConvertTo-Json -Compress)).Body }
function ClickState() { @((Sounds @{ action = "state" }).slots | Where-Object { $_.slot -eq "Click" })[0] }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
Set-Content -LiteralPath (Join-Path $appdata "ReverieViewer\settings.json") -Value '{"ui_sound_click_source": "grid"}' -Encoding utf8
Write-Output "LIVE SOUND RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

# before login: the grid's source falls back to our file
$ready = $null
for ($i = 0; $i -lt 60 -and -not $ready; $i++) { $ready = Sounds @{ action = "state" }; if (-not $ready.slots) { $ready = $null; Start-Sleep -Milliseconds 500 } }
[void](Sounds @{ action = "clear" })
$pre = Sounds @{ action = "play"; slot = "Click" }
Check "1 login screen: Click set to the grid's plays our file" ((ClickState).source -eq "grid" -and $pre.outcome -eq "PlayedOurs") ("source {0}, outcome {1}" -f (ClickState).source, $pre.outcome)

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
    # the preload asks for the grid's click as soon as the session is in the world; wait for its answer (status leaves fetching)
    $status = $null
    $until = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $until) { $c = ClickState; if ($c.status -ne "Fetching" -and (Sounds @{ action = "log" }).in_world) { $status = $c.status; break }; Start-Sleep -Milliseconds 500 }
    Check "2 in the world: the grid's click was fetched (not 'not on this grid')" ($status -eq "Fine") ("status {0}" -f $status)
    $before = (Sounds @{ action = "log" }).started
    [void](Sounds @{ action = "clear" })
    $played = Sounds @{ action = "play"; slot = "Click" }
    $after = Sounds @{ action = "log" }
    Check "3 Click plays the grid's sound, decoded by the engine" ($played.outcome -eq "PlayedGrid" -and ([int]$after.started - [int]$before) -eq 1) ("outcome {0}, engine plays {1}" -f $played.outcome, ([int]$after.started - [int]$before))
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "sound-report.json") -Encoding utf8
Write-Output "LIVE SOUND RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0

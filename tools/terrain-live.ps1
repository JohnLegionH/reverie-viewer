# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# terrain-live.ps1 - the live check of the ground. One command logs a test avatar in at a start location with the development export and
# --neighbours=on, waits until the terrain and the neighbours' terrain are drawn, and records through the viewer's automation API:
#   - the regions in the scene (grid cells, sizes, offsets, terrain patches, chunks) and each region's terrain texturing (where each detail layer
#     came from, its pixel size, the repeat and the pattern offset),
#   - for each yaw in -Yaws: a close picture (the follow camera at its closest, about eye height) and a far one (its farthest, about 25 m),
#     both at midday.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\terrain-live.ps1 -Sandbox <new folder> -Start <start location>
#     [-Yaws "0,90,180,270"] [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-OtherClientPattern <regex or none>] [-Exe <development export>]
#
# Settings come from the parameters or, when not given, from NV_TEST_ACCOUNTS, NV_LIVE_ACCOUNT, NV_GRID_INFO_URL, NV_OTHER_CLIENT_PATTERN (live-env.ps1).
# Run it under a live-login claim and an avatar claim on the account. Output goes to the sandbox folder (terrain-report.json, pictures, run.log).
# Exit: 0 done, 3 blocked (preconditions), 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$Start,
    [string]$Yaws = "0,90,180,270",
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [int]$SettleSeconds = 25
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token '{"action":"logout"}'); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export): $Exe") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the account, or none") }
elseif ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)
    if ($agent.Count -gt 0) { $problems.Add("another test client is running ($($agent.Count) process(es)): the test account may be in use") }
}
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
Write-Output "preconditions ok: account $Account"

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
function Observe { Parse (Invoke-Raw $port "observe" $token).Body }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output ""
Write-Output "LIVE TERRAIN RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation --neighbours=on"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
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
$report = [ordered]@{ account = $Account; yaws = $Yaws; completed = $false; shots = @() }

$stable = 0; $last = -1; $o = $null; $ended = $false
$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Observe
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
    $st = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body
    if ($st -and $st.ended) { Note "session ended during settle"; $ended = $true; break }
}
if (-not $ended -and $o -and $o.self) {
    Note "in world, settled (objects $last); waiting $SettleSeconds s for the neighbours and the detail textures"
    Start-Sleep -Seconds $SettleSeconds
    [void](Invoke-Raw $port "sky" $token '{"choice":"midday"}')
    Start-Sleep -Seconds 2
    $report.self_region_position = $o.self.region_position
    $report.neighbours = Parse (Invoke-Raw $port "neighbours" $token).Body | Select-Object in_world, enabled, stacks, water_plan_regions, anchor_has_water, @{ n = "regions"; e = { @($_.regions | ForEach-Object { [ordered]@{ grid = $_.grid; size = $_.size; current = $_.current; origin_offset = $_.origin_offset; patches = $_.terrain_patches; chunks = $_.chunks; drawn = $_.drawn; water_height = $_.water_height } }) } }
    $report.terrain = Parse (Invoke-Raw $port "terrain" $token).Body
    Note ("terrain: " + ($report.terrain | ConvertTo-Json -Compress -Depth 6))
    foreach ($yaw in ($Yaws -split ',' | Where-Object { $_ })) {
        foreach ($view in @(@{ name = "close"; zoom = 0.25; pitch = 4 }, @{ name = "far"; zoom = 8; pitch = 12 })) {
            [void](Invoke-Raw $port "orbit" $token (@{ yaw_deg = [double]$yaw; pitch_deg = $view.pitch; zoom = $view.zoom } | ConvertTo-Json -Compress))
            Start-Sleep -Seconds 2
            $name = "terrain-yaw$yaw-$($view.name)"
            $shot = Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = $name } | ConvertTo-Json -Compress)
            $report.shots += [ordered]@{ name = $name; status = $shot.Status }
            Note "screenshot $name : $($shot.Status)"
        }
    }
    $report.completed = $true
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "terrain-report.json") -Encoding UTF8
# logout, then quit
try { [void](Invoke-Raw $port "session" $token '{"action":"logout"}'); Start-Sleep -Seconds 4; [void](Invoke-Raw $port "quit" $token) } catch { }
$null = $p.WaitForExit(30000)
Write-Output "LIVE TERRAIN RUN DONE - other work may run again"
exit 0

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# objects-live.ps1 - the live check of objects that move or spin between the region's updates. Logs a test avatar in with the
# development export, optionally teleports to a region, and asks the viewer (the moving_probe automation command) every
# -IntervalMs for -Seconds which objects it carries: each as the region last stated it and as it is drawn now. It only looks:
# nothing in the region is touched.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\objects-live.ps1 -Sandbox <new folder> [-Region <name>] [-Seconds 5] [-IntervalMs 100]
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under a live-login claim and an avatar claim on the account. Output in the sandbox: samples.jsonl (every reply), table.tsv
# (one row per sample and object) and objects-report.json. Exit: 0 done, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Region = "",
    [double]$X = 128,
    [double]$Y = 128,
    [double]$Z = 40,
    [double]$Seconds = 5,
    [int]$IntervalMs = 100,
    [int]$SettleSeconds = 20,
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
Write-Output "LIVE OBJECTS RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

$report = [ordered]@{ account = $Account; teleported = $false; samples = 0; objects = @(); completed = $false; region_objects = $null; stated_moving = $null; carried = $null; before_teleport = $null }
function Self-Pos { $ob = Parse (Invoke-Raw $port "observe" $token).Body; if ($ob -and $ob.self -and $ob.self.region_position) { return @([double]$ob.self.region_position[0], [double]$ob.self.region_position[1], [double]$ob.self.region_position[2]) } return $null }
function F($v) { if ($null -eq $v) { return "" } return (@($v) | ForEach-Object { ([double]$_).ToString("F4", [System.Globalization.CultureInfo]::InvariantCulture) }) -join "," }

$inWorld = [bool]($o -and $o.self)
if ($inWorld) {
    Note "in world, settled (objects $last)"
    if ($Region) {
        $here = Parse (Invoke-Raw $port "moving_probe" $token '{"max":40}').Body   # the arrival region, before leaving it
        if ($here) { $report.before_teleport = [ordered]@{ region_objects = $here.region_objects; stated_moving = $here.stated_moving; carried = $here.carried } }
        Note ("arrival region: objects {0}; stated as moving or spinning {1}; carried {2}" -f $here.region_objects, $here.stated_moving, $here.carried)
        $r = Parse (Invoke-Raw $port "teleport" $token (@{ action = "region"; region = $Region; x = $X; y = $Y; z = $Z } | ConvertTo-Json -Compress)).Body
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Seconds 2
            $at = Self-Pos
            if ($at -and [math]::Sqrt([math]::Pow($at[0] - $X, 2) + [math]::Pow($at[1] - $Y, 2)) -lt 8) { $report.teleported = $true; break }
        }
        Note ("teleport: {0} (sent {1})" -f $(if ($report.teleported) { "arrived" } else { "did not arrive" }), $r.sent)
    }
    Start-Sleep -Seconds $SettleSeconds   # the region's objects arrive
    $samplesFile = Join-Path $Sandbox "samples.jsonl"
    $rows = New-Object System.Collections.Generic.List[string]
    $rows.Add("sample`tviewer_t`tdilation`tlocal`troot`tstated_pos`tdrawn_pos`tstated_rot`tdrawn_rot`tvelocity`tangular")
    $seen = @{}
    $first = Parse (Invoke-Raw $port "moving_probe" $token '{"max":40}').Body
    if ($first) { $report.region_objects = $first.region_objects; $report.stated_moving = $first.stated_moving; $report.carried = $first.carried }
    Note ("region objects {0}; stated as moving or spinning {1}; carried {2}" -f $first.region_objects, $first.stated_moving, $first.carried)
    $count = [int][math]::Ceiling($Seconds * 1000 / $IntervalMs) + 1
    $tick = [System.Diagnostics.Stopwatch]::StartNew()
    for ($n = 0; $n -lt $count; $n++) {
        $due = $n * $IntervalMs
        $wait = $due - $tick.ElapsedMilliseconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
        $reply = Invoke-Raw $port "moving_probe" $token '{"max":40}'
        Add-Content -LiteralPath $samplesFile -Value $reply.Body -Encoding utf8
        $probe = Parse $reply.Body
        if (-not $probe) { continue }
        $report.samples++
        foreach ($ob in @($probe.objects)) {
            $seen[[string]$ob.local] = $true
            $rows.Add(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}`t{8}`t{9}`t{10}" -f $n, ([double]$probe.t).ToString("F3", [System.Globalization.CultureInfo]::InvariantCulture),
                ([double]$probe.time_dilation).ToString("F4", [System.Globalization.CultureInfo]::InvariantCulture), $ob.local, $ob.root, (F $ob.stated), (F $ob.drawn), (F $ob.stated_rot), (F $ob.drawn_rot), (F $ob.velocity), (F $ob.angular)))
        }
    }
    $rows | Set-Content -LiteralPath (Join-Path $Sandbox "table.tsv") -Encoding utf8
    $report.objects = @($seen.Keys)
    $report.completed = $true
    Note ("sampled {0} times; {1} carried objects seen" -f $report.samples, $seen.Count)
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "objects-report.json") -Encoding utf8
Write-Output "LIVE OBJECTS RUN DONE - other work may run again"
if (-not $inWorld) { exit 5 }
exit 0

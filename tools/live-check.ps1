# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# one READ-ONLY live login with the development export, camera only.
# The same timeline every time: settle (in world, terrain complete, object count stable), 10 s, the camera through 360
# degrees and back in 10-degree steps 1 s apart, a 60 s hold at the start pose, then (with -Probe) the direct-capability
# probe, a final observe, a screenshot, the layers report, logout and quit.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\live-check.ps1 -Mode partial -Region <region>
#     -Sandbox <new folder> -Credentials <accounts file> -Account <key> -GridInfoUrl <grid's get_grid_info URL>
#     [-Exe <development export>] [-Probe] [-LoginNumber n] [-RegionServerLog <file> -AvatarName "<first last>"] [-DryRun]
#
# Nothing secret or grid-specific lives here: the credentials file is read by the viewer itself at login (this script never
# opens it), the login URI is read from the grid's get_grid_info at run time, and the region, sandbox and log paths are
# parameters. -OtherClientPattern (default: NV_OTHER_CLIENT_PATTERN) is a regex over process executables (tools\other-clients.ps1) matching any
# other client that may be logged in with the test account, "none" when there is none; it is required, so an account is
# never used by two clients at once. Every Godot process goes through tools\godot.ps1 (sandboxed APPDATA / LOCALAPPDATA inside the sandbox, a
# timeout that kills the whole tree). -DryRun checks every input and precondition and logs nothing in.
# The observe answers are kept raw (samples.jsonl): no answer is re-parsed where a field it does not need could break
# Windows PowerShell 5.1's ConvertFrom-Json (crash on an empty JSON key in the layers report).
param(
    [Parameter(Mandatory)][ValidateSet("partial", "whole")][string]$Mode,
    [Parameter(Mandatory)][string]$Region,
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$GridInfoUrl,
    [string]$Account = "tester1",
    [string]$Exe = "",   # default: the development export, host\Viewer.Godot\build\dev\Reverie.console.exe (set below)
    [switch]$Probe,
    [int]$LoginNumber = 1,
    [int]$StartSeconds = 40,   # how long to wait for the viewer to write its automation token file (a cold start can take longer)
    [string]$RegionServerLog,
    [string]$AvatarName,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [switch]$DryRun
)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

# ---------------- inputs and preconditions (also the whole of -DryRun) ----------------
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export): $Exe") }
if (-not (Test-Path -LiteralPath $godot -PathType Leaf)) { $problems.Add("tools\godot.ps1 not found") }
if ($Region -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("-Region must be a plain region name") }
if ($RegionServerLog -and -not (Test-Path -LiteralPath $RegionServerLog -PathType Leaf)) { $problems.Add("-RegionServerLog is not a file") }
if ($RegionServerLog -and -not $AvatarName) { $problems.Add("-RegionServerLog needs -AvatarName (the logout line names the avatar)") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the test account, or none") }
elseif ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)   # by executable, never by command-line text
    if ($agent.Count -gt 0) { $problems.Add("another test client is running ($($agent.Count) process(es)): the test account may be in use") }
}
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer process is already running ($($viewers.Count)): one account in one session at a time") }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
$heavy = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @("MSBuild.exe", "testhost.exe", "vstest.console.exe") -or ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match " (build|test|publish)\b") })
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
Write-Output ("preconditions ok: mode {0}, region {1}, account key {2}; heavy build/test processes running now: {3}" -f $Mode, $Region, $Account, $heavy.Count)
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

# ---------------- helpers ----------------
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
function Stamp { [math]::Round(((Get-Date) - $script:t0).TotalSeconds, 1) }
$events = New-Object System.Collections.Generic.List[object]
function Note([string]$what) { $events.Add([pscustomobject]@{ t = (Stamp); utc = (Get-Date).ToUniversalTime().ToString("HH:mm:ss"); what = $what }); Write-Output ("{0,7} {1}" -f (Stamp), $what) }
function LogoutLines { if ($RegionServerLog) { @(Select-String -LiteralPath $RegionServerLog -SimpleMatch -Pattern "Got a logout request for $AvatarName").Count } else { 0 } }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$samplesFile = Join-Path $Sandbox "samples.jsonl"
$logoutsBefore = LogoutLines

Write-Output ""
Write-Output "LIVE LOGIN $LoginNumber STARTING - no grid restarts, heavy builds or test runs, or other test clients until LIVE LOGIN $LoginNumber DONE"
Write-Output ""
$script:t0 = Get-Date
$userArgs = "--automation" + $(if ($Mode -eq "whole") { " --texture-fetch=whole" } else { "" })
$argLine = "--rendering-driver vulkan -- $userArgs"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started ($Mode) through godot.ps1, pid $($p.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token

function Observe([string]$phase) {
    $body = (Invoke-Raw $port "observe" $token).Body
    Add-Content -LiteralPath $samplesFile -Value ('{"t":' + (Stamp) + ',"phase":"' + $phase + '","o":' + $body + '}') -Encoding utf8
    return Parse $body
}

$start = "uri:" + $Region + "&128&128&25"
$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
# settle: in world, terrain complete, objects stable for 3 polls (2 s apart), at most 150 s
$stable = 0; $last = -1; $inWorld = $null; $ended = $false
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Observe "settle"
    if ($o -and $o.stage -eq "InWorld" -and -not $inWorld) { $inWorld = Stamp; Note "in world" }
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
    $st = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body
    if ($st -and $st.ended) { Note "session ended during settle: $($st.end_reason) / $($st.status)"; $ended = $true; break }
}
Note "settled (objects $last, stable x$stable)"
$atDesiredAt = $null; $backAt = $null; $probes = @()
if (-not $ended) {
    Start-Sleep -Seconds 10   # a fixed extra settle, so every login starts the camera at the same point after arrival
    [void](Observe "pre-camera")
    $cam = $null
    for ($y = 10; $y -le 360; $y += 10) { $cam = Invoke-Raw $port "camera" $token (@{ yaw_deg = $y } | ConvertTo-Json -Compress); Start-Sleep -Seconds 1; [void](Observe "turn") }
    for ($y = 350; $y -ge 0; $y -= 10) { $cam = Invoke-Raw $port "camera" $token (@{ yaw_deg = $y } | ConvertTo-Json -Compress); Start-Sleep -Seconds 1; [void](Observe "back") }
    Note "camera turned 360 and back (last reply $($cam.Status))"
    $backAt = Stamp
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        $o = Observe "hold"
        $r = if ($o) { $o.presentation.prims.residency } else { $null }
        if (-not $atDesiredAt -and $r -and $r.visible -gt 0 -and $r.visible_at_desired -eq $r.visible -and $r.loading -eq 0) { $atDesiredAt = Stamp }
    }
    Note ("hold done; view at desired levels {0} s after the camera returned" -f $(if ($atDesiredAt) { [math]::Round($atDesiredAt - $backAt, 1) } else { "never" }))
    if ($Probe) {
        # the direct-capability comparison: the same textures, a plain bounded client of the viewer's own (not the protocol
        # library's), first chunks at concurrency 1 then 8 (the official viewer's), then whole files at 8
        foreach ($pr in @(@{ concurrency = 1; to = 599 }, @{ concurrency = 8; to = 599 }, @{ concurrency = 8 })) {
            $a = Invoke-Raw $port "texture_probe" $token ($pr | ConvertTo-Json -Compress)
            $probes += [pscustomobject]@{ request = $pr; status = $a.Status; answer = (Parse $a.Body) }
            Note "texture_probe concurrency $($pr.concurrency) $(if ($pr.to) { 'first chunk' } else { 'whole' }): $($a.Status)"
        }
        $probes | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Sandbox "probe.json") -Encoding utf8
    }
    [void](Observe "final")
    $shot = Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = "final-$Mode" } | ConvertTo-Json -Compress)
    $layers = Invoke-Raw $port "layers" $token
    Set-Content -LiteralPath (Join-Path $Sandbox "layers.json") -Value $layers.Body -Encoding utf8   # kept raw, never re-parsed here
    Note "layers: $($layers.Status)"
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended) reason=$($after.end_reason)"
$q = Invoke-Raw $port "quit" $token
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$confirmed = $false
if ($RegionServerLog) {
    for ($i = 0; $i -lt 30 -and -not $confirmed; $i++) { if ((LogoutLines) -gt $logoutsBefore) { $confirmed = $true } else { Start-Sleep -Seconds 1 } }
    Note "logout in the region server log: $(if ($confirmed) { 'confirmed' } else { 'NOT confirmed' })"
}
[pscustomobject]@{ mode = $Mode; region = $Region; in_world_s = $inWorld; back_at_s = $backAt; at_desired_s = $atDesiredAt;
    at_desired_after_back_s = $(if ($atDesiredAt) { [math]::Round($atDesiredAt - $backAt, 1) } else { $null }); logout_confirmed = $confirmed;
    viewer_exited = $p.HasExited; heavy_processes_at_start = $heavy.Count; events = $events;
    token_in_log = ((Get-Content -Raw -LiteralPath $log) -match [regex]::Escape($token)) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "summary.json") -Encoding utf8
Write-Output ""
if ($confirmed) { Write-Output "LIVE LOGIN $LoginNumber DONE - other work may run again" }
elseif (-not $RegionServerLog) { Write-Output "LIVE LOGIN $LoginNumber ENDED - logout not yet confirmed (no -RegionServerLog): check the region server log before other work runs" }
else { Write-Output "LIVE LOGIN $LoginNumber ENDED - logout NOT confirmed in the region server log: check before other work runs" }

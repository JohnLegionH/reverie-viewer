# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# =====================================================================================
# lod-live.ps1 - the live level-of-detail check. Logs a test avatar in with the development export and asks the viewer (the mesh_probe automation
# command) which mesh objects it has and what each is drawn at.
#   -Scan:    writes scan.json, the mesh objects of the region nearest its middle (position, scale, asset, level drawn, levels the asset has).
#   -Target:  "x,y,z" of one object; the avatar is teleported to each of -Distances (metres from the object, along -BearingDeg), the viewer settles,
#             and the object is probed: the level drawn and wanted, the camera's distance, and each face texture's loaded and wanted level.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\lod-live.ps1 -Sandbox <new folder> [-Scan | -Target x,y,z] [-Distances 10,30,60]
#     [-BearingDeg 0] [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under a live-login claim and an avatar claim on the account. Output: lod-report.json in the sandbox. Exit: 0 done, 3 blocked, 5 the viewer
# or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [switch]$Scan,
    [string]$AuditAt = "",     # "x,y;x,y": the audit is taken after the avatar is teleported to each point
    [switch]$Audit,            # presented_audit: the region's objects against the presented ones, the reason for each that is not (audit.json)
    [string]$NamePattern = "", # the objects near the region's middle are asked for their names; the first whose name matches becomes -Target (names.json holds them all)
    [string]$Target = "",
    [double[]]$Distances = @(10, 30, 60),
    [double]$BearingDeg = 0,
    [double]$ScanRadius = 300,
    [string]$Region = "",
    [double]$ScanCentreX = 128,
    [double]$ScanCentreY = 128,
    [double]$RegionSize = 256,
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
Write-Output "LIVE LOD RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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


$report = [ordered]@{ account = $Account; scan = $null; distances = @(); notes = @(); name_matches = @(); completed = $false }
function Self-Pos { $ob = Parse (Invoke-Raw $port "observe" $token).Body; if ($ob -and $ob.self -and $ob.self.region_position) { return @([double]$ob.self.region_position[0], [double]$ob.self.region_position[1], [double]$ob.self.region_position[2]) } return $null }
$regionName = if ($Region) { $Region } elseif ($Start -match 'uri:([^&]+)') { [uri]::UnescapeDataString($Matches[1]) } else { "" }
function Go([double]$x, [double]$y, [double]$z) {
    $r = Parse (Invoke-Raw $port "teleport" $token (@{ action = "region"; region = $regionName; x = $x; y = $y; z = $z } | ConvertTo-Json -Compress)).Body
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $p = Self-Pos
        if ($p -and [math]::Sqrt([math]::Pow($p[0] - $x, 2) + [math]::Pow($p[1] - $y, 2)) -lt 8) { $ok = $true; break }
    }
    Note ("teleport to {0:N0},{1:N0},{2:N0}: {3} (sent {4})" -f $x, $y, $z, $(if ($ok) { "arrived" } else { "did not arrive" }), $r.sent)
    if ($ok) { Start-Sleep -Seconds 15 }   # the region's objects and textures settle
    return $ok
}

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 8
    if ($Region) { [void](Go ($ScanCentreX) ($ScanCentreY) 60) }   # to the region asked for, before anything is probed
    if ($Scan) {
        $probe = Parse (Invoke-Raw $port "mesh_probe" $token (@{ x = $ScanCentreX; y = $ScanCentreY; z = 25; radius = $ScanRadius; max = 400 } | ConvertTo-Json -Compress)).Body
        $report.scan = $probe
        $probe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "scan.json") -Encoding utf8
        Note ("scan: {0} mesh objects" -f @($probe.objects).Count)
    }
    if ($Audit) {
        $spots = if ($AuditAt) { @($AuditAt -split ';' | ForEach-Object { , @($_.Split(',') | ForEach-Object { [double]$_ }) }) } else { @($null) }
        $n = 0
        foreach ($spot in $spots) {
            $n++
            if ($spot) { [void](Go $spot[0] $spot[1] ([double]60)) }
            Start-Sleep -Seconds 40   # the region's meshes and textures arrive
            $auditReply = Parse (Invoke-Raw $port "presented_audit" $token (@{ sample = 12 } | ConvertTo-Json -Compress)).Body
            $auditReply | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox ("audit-{0}.json" -f $n)) -Encoding utf8
            Write-Output ("audit {0} at {1}: {2} objects, {3} presented, {4} not" -f $n, $(if ($spot) { $spot -join ',' } else { 'the arrival point' }), $auditReply.objects, $auditReply.presented, $auditReply.not_presented)
            foreach ($g in @($auditReply.by_reason)) { Write-Output ("    {0} x {1}" -f $g.count, $g.reason) }
        }
    }
    if ($NamePattern) {
        $names = $null
        for ($round = 0; $round -lt 3; $round++) {
            $names = Parse (Invoke-Raw $port "mesh_probe" $token (@{ x = $ScanCentreX; y = $ScanCentreY; z = 25; radius = $ScanRadius; max = 400; names = 1 } | ConvertTo-Json -Compress)).Body
            Start-Sleep -Seconds 6
        }
        $names | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "names.json") -Encoding utf8
        $named = @($names.objects | Where-Object { $_.name })
        Note ("names: {0} of {1} mesh objects named" -f $named.Count, @($names.objects).Count)
        $hits = @($named | Where-Object { $_.name -match $NamePattern })
        foreach ($h in $hits) { Note ("  match: '{0}' at {1:N1},{2:N1},{3:N1} scale {4}" -f $h.name, $h.x, $h.y, $h.z, ($h.scale -join 'x')) }
        if ($hits.Count -gt 0 -and -not $Target) { $Target = "{0},{1},{2}" -f $hits[0].x, $hits[0].y, $hits[0].z }
        $report.name_matches = $hits
    }
    if ($Target) {
        $t = @($Target.Split(',') | ForEach-Object { [double]$_ })
        foreach ($d in $Distances) {
            $px = [math]::Min($RegionSize - 6, [math]::Max(6, $t[0] + $d * [math]::Cos($BearingDeg * [math]::PI / 180)))
            $py = [math]::Min($RegionSize - 6, [math]::Max(6, $t[1] + $d * [math]::Sin($BearingDeg * [math]::PI / 180)))
            if (Go $px $py ($t[2] + 3.0)) {
                $probe = Parse (Invoke-Raw $port "mesh_probe" $token (@{ x = $t[0]; y = $t[1]; z = $t[2]; radius = 1.5; max = 4 } | ConvertTo-Json -Compress)).Body
                $report.distances += [ordered]@{ asked_metres = $d; probe = $probe }
                Note ("at {0} m: {1}" -f $d, (($probe.objects | ForEach-Object { "lod drawn $($_.drawn_lod) wanted $($_.wanted_lod) distance $($_.distance)" }) -join "; "))
            } else { $report.notes += "did not reach the point $d m from the object" }
        }
    }
    $report.completed = $true
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "lod-report.json") -Encoding utf8
Write-Output "LIVE LOD RUN DONE - other work may run again"

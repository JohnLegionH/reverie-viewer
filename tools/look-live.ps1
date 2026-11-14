# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# look-live.ps1 - the live look check. Logs a test avatar in with the development export, asks the viewer where the water meets the land,
# where its trees and larger textured objects are (the look_probe automation command), stands at a shore, out at sea, near and far from a
# tree and near and far from a textured object, takes a picture at each and records each texture's loaded level against the level the
# size on screen asks for.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\look-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in camera-live.ps1. Run it under a live-login claim and an avatar claim on the
# account. Output: look-report.json in the sandbox, pictures in -ShotsDir. Exit: 0 done, 3 blocked, 5 the viewer or the login did not come up.
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
Write-Output "LIVE LOOK RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

$report = [ordered]@{ account = $Account; water_map = $null; probe = $null; shots = @(); texture_levels = @(); notes = @(); completed = $false }
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)); $script:report.shots += $name; Note "picture $name" }
function Cam([string]$op, [double]$value = 0) { Parse (Invoke-Raw $port "orbit" $token (@{ op = $op; value = $value } | ConvertTo-Json -Compress)).Body }
function Self-Pos { $ob = Parse (Invoke-Raw $port "observe" $token).Body; if ($ob -and $ob.self -and $ob.self.region_position) { return @([double]$ob.self.region_position[0], [double]$ob.self.region_position[1], [double]$ob.self.region_position[2]) } return $null }
function Bearing($dx, $dy) { return [math]::Atan2($dy, $dx) * 180 / [math]::PI }
function Wrap([double]$a) { while ($a -gt 180) { $a -= 360 }; while ($a -lt -180) { $a += 360 }; return $a }
$regionName = if ($Start -match 'uri:([^&]+)') { [uri]::UnescapeDataString($Matches[1]) } else { "" }

function Go([double]$x, [double]$y, [double]$z) {
    $r = Parse (Invoke-Raw $port "teleport" $token (@{ action = "region"; region = $regionName; x = $x; y = $y; z = $z } | ConvertTo-Json -Compress)).Body
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $p = Self-Pos
        if ($p -and [math]::Sqrt([math]::Pow($p[0] - $x, 2) + [math]::Pow($p[1] - $y, 2)) -lt 8) { $ok = $true; break }
    }
    Note ("teleport to {0:N0},{1:N0},{2:N0}: {3} (sent {4})" -f $x, $y, $z, $(if ($ok) { "arrived" } else { "did not arrive" }), $r.sent)
    if ($ok) { Start-Sleep -Seconds 12 }   # the region's objects and textures settle
    return $ok
}
function Face([double]$tx, [double]$ty, [double]$pitchDeg) {
    [void](Cam "reset"); Start-Sleep -Milliseconds 700
    [void](Cam "unlock"); Start-Sleep -Milliseconds 400
    $s = Cam "state"
    $want = Bearing ($tx - $s.eye[0]) ($ty - $s.eye[1]); $have = Bearing ($s.look[0] - $s.eye[0]) ($s.look[1] - $s.eye[1])
    $turn = Wrap ($want - $have)
    [void](Cam "orbit_around_deg" $turn); Start-Sleep -Milliseconds 500
    $s2 = Cam "state"
    $err = [math]::Abs((Wrap ((Bearing ($tx - $s2.eye[0]) ($ty - $s2.eye[1])) - (Bearing ($s2.look[0] - $s2.eye[0]) ($s2.look[1] - $s2.eye[1])))))
    if ($err -gt 10) { [void](Cam "orbit_around_deg" (-2 * $turn)); Start-Sleep -Milliseconds 500 }
    if ($pitchDeg -ne 0) { [void](Cam "orbit_over_deg" $pitchDeg); Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 3
}
function Levels([string]$where) {
    $lp = Parse (Invoke-Raw $port "look_probe" $token).Body
    if (-not $lp -or -not $lp.textures) { return }
    $rows = @($lp.textures)
    $over = @($rows | Where-Object { $_.loaded -gt $_.asked })    # coarser than the rule asks
    $under = @($rows | Where-Object { $_.loaded -lt $_.asked })   # finer than the rule asks
    $script:report.texture_levels += [ordered]@{ where = $where; textures = $rows.Count; at_asked_level = @($rows | Where-Object { $_.loaded -eq $_.asked }).Count; coarser_than_asked = $over.Count; finer_than_asked = $under.Count; pending = @($rows | Where-Object { $_.pending -ge 0 }).Count; rows = $rows | Select-Object -First 25 }
    Note ("textures at {0}: {1} resident, {2} at the asked level, {3} coarser, {4} finer" -f $where, $rows.Count, @($rows | Where-Object { $_.loaded -eq $_.asked }).Count, $over.Count, $under.Count)
}

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 8
    [void](Invoke-Raw $port "sky" $token (@{ choice = "midday" } | ConvertTo-Json -Compress))   # daylight for the pictures
    Start-Sleep -Seconds 3
    $self0 = Self-Pos
    $lp = Parse (Invoke-Raw $port "look_probe" $token).Body
    $report.probe = $lp
    if (-not $lp) { $report.notes += "look_probe gave no answer" }
    else {
        Note ("probe: water {0} m, {1} trees, {2} textured objects, {3} shore points, sea {4}" -f $lp.region.water_height, @($lp.trees).Count, @($lp.builds).Count, @($lp.shores).Count, $(if ($lp.sea) { "yes" } else { "none" }))
        Levels "the start, before moving"
        $regionSize = [double]$lp.region.size
        Note ("water map: " + $lp.water_map)
        $report.water_map = $lp.water_map
        # the water: at a shore (looking down at it, then out over it), then from a second shore at the water's edge looking out to the horizon
        if (@($lp.shores).Count -gt 0 -and $lp.sea) {
            $sh = @($lp.shores)[0]
            if (Go $sh.x $sh.y ($sh.z + 1.0)) {
                Face $lp.sea.x $lp.sea.y 12; Shot "look_water_shore_near"
                Face $lp.sea.x $lp.sea.y 0; Shot "look_water_shore_far"
            } else { $report.notes += "did not reach the shore point" }
            $sh2 = @($lp.shores)[[math]::Min(1, @($lp.shores).Count - 1)]
            if (Go $sh2.x $sh2.y ($sh2.z + 1.0)) {
                Face $lp.sea.x $lp.sea.y 6; Shot "look_water_sea"
            } else { $report.notes += "did not reach the second shore point" }
        } else { $report.notes += "no shore point or sea point in this region" }
        # the trees: near, and far from outside the canopy (about 50 m away, where no other tree is within 15 m)
        if (@($lp.trees).Count -gt 0 -and $self0) {
            $t = @($lp.trees | Sort-Object { [math]::Pow($_.x - $self0[0], 2) + [math]::Pow($_.y - $self0[1], 2) })[0]
            $dx = $self0[0] - $t.x; $dy = $self0[1] - $t.y; $len = [math]::Max(1, [math]::Sqrt($dx * $dx + $dy * $dy)); $ux = $dx / $len; $uy = $dy / $len
            $px = [math]::Min($regionSize - 6, [math]::Max(6, $t.x + $ux * 14)); $py = [math]::Min($regionSize - 6, [math]::Max(6, $t.y + $uy * 14))
            if (Go $px $py ($t.z + 2.0)) { Face $t.x $t.y 5; Shot "look_tree_near"; Levels "tree near" } else { $report.notes += "did not reach the tree near point" }
            $far = $null
            for ($ang = 0; $ang -lt 360 -and -not $far; $ang += 30) {
                $cx = $t.x + 50 * [math]::Cos($ang * [math]::PI / 180); $cy = $t.y + 50 * [math]::Sin($ang * [math]::PI / 180)
                if ($cx -lt 6 -or $cy -lt 6 -or $cx -gt ($regionSize - 6) -or $cy -gt ($regionSize - 6)) { continue }
                $clear = @($lp.trees | Where-Object { [math]::Sqrt([math]::Pow($_.x - $cx, 2) + [math]::Pow($_.y - $cy, 2)) -lt 15 }).Count -eq 0
                if ($clear) { $far = @($cx, $cy) }
            }
            if ($far) {
                if (Go $far[0] $far[1] ($t.z + 6.0)) { Face $t.x $t.y 3; Shot "look_tree_far"; Levels "tree far" } else { $report.notes += "did not reach the tree far point" }
            } else { $report.notes += "no clear spot about 50 m from the tree" }
        } else { $report.notes += "no tree in this region" }
        # a textured object: near and far
        if (@($lp.builds).Count -gt 0 -and $self0) {
            $b = @($lp.builds)[0]
            $big = [math]::Max($b.scale[0], [math]::Max($b.scale[1], $b.scale[2]))
            $dx = $self0[0] - $b.x; $dy = $self0[1] - $b.y; $len = [math]::Max(1, [math]::Sqrt($dx * $dx + $dy * $dy)); $ux = $dx / $len; $uy = $dy / $len
            foreach ($d in @(@("near", ($big * 1.2 + 6)), @("far", ($big * 1.2 + 70)))) {
                $px = [math]::Min(250, [math]::Max(6, $b.x + $ux * $d[1])); $py = [math]::Min(250, [math]::Max(6, $b.y + $uy * $d[1]))
                if (Go $px $py ($b.z + 2.0)) { Face $b.x $b.y 3; Shot ("look_build_" + $d[0]); Levels ("textured object " + $d[0]) } else { $report.notes += "did not reach the object $($d[0]) point" }
            }
        } else { $report.notes += "no textured object in this region" }
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
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "look-report.json") -Encoding utf8
Write-Output "LIVE LOOK RUN DONE - other work may run again"

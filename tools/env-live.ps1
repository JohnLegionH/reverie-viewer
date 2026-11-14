# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# env-live.ps1 - the live check of the water and the World > Environment menu. Logs a test avatar in with the development export,
# stands at a shore with the camera on the water (not the overview camera), takes two pictures one second apart and compares the water
# pixels (they must differ: the ripples move), then picks each built-in sky through the menu's own command and takes a picture of it,
# and finally Use Shared Environment. For every built-in sky it records whether the grid served the settings asset.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\env-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in sky-live.ps1. Run it under a live-login claim and an avatar claim on the
# account. Output: env-report.json in the sandbox, pictures in -ShotsDir. Exit: 0 done, 3 blocked, 5 the viewer or the login did not come up.
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

# the share of pixels in the water band of two pictures (the right-hand part of the picture just below the horizon, where the camera looks out
# over the sea from the shore) whose colour differs by more than a small amount in any channel
Add-Type -AssemblyName System.Drawing
function Compare-Water([string]$a, [string]$b) {
    $ia = New-Object System.Drawing.Bitmap($a); $ib = New-Object System.Drawing.Bitmap($b)
    try {
        if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) { return -1.0 }
        $diff = 0; $total = 0
        for ($y = [int]($ia.Height * 0.36); $y -lt [int]($ia.Height * 0.48); $y += 2) {
            for ($x = [int]($ia.Width * 0.6); $x -lt [int]($ia.Width * 0.99); $x += 2) {
                $pa = $ia.GetPixel($x, $y); $pb = $ib.GetPixel($x, $y)
                $total++
                if ([math]::Abs($pa.R - $pb.R) -gt 3 -or [math]::Abs($pa.G - $pb.G) -gt 3 -or [math]::Abs($pa.B - $pb.B) -gt 3) { $diff++ }
            }
        }
        return [math]::Round(100.0 * $diff / [math]::Max(1, $total), 3)
    } finally { $ia.Dispose(); $ib.Dispose() }
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE ENVIRONMENT RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

$report = [ordered]@{ account = $Account; water_map = $null; water_changed_percent = $null; water_pass = $false; skies = @(); shots = @(); notes = @(); completed = $false }
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
function Pick-Sky([string]$choice) {
    $r = Parse (Invoke-Raw $port "sky" $token (@{ choice = $choice } | ConvertTo-Json -Compress)).Body
    $script:report.skies += [ordered]@{ requested = $choice; in_force = $r.choice; failed = [bool]$r.failed; why = $r.why }
    Note ("sky '{0}': in force {1}, failed {2} {3}" -f $choice, $r.choice, [bool]$r.failed, $r.why)
    Start-Sleep -Seconds 4
}

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 8
    $lp = Parse (Invoke-Raw $port "look_probe" $token).Body
    $report.water_map = $lp.water_map
    $sh = @($lp.shores)[0]
    if ($sh -and $lp.sea -and (Go $sh.x $sh.y ($sh.z + 1.0))) {
        # the camera on the water: from the shore, looking out to the open water, a little downward
        Face $lp.sea.x $lp.sea.y -8
        Pick-Sky "legacy noon"   # a lit sky, so the ripples show
        Start-Sleep -Seconds 4
        Shot "water_a"; Start-Sleep -Seconds 1; Shot "water_b"
        $pct = Compare-Water (Join-Path $ShotsDir "water_a.png") (Join-Path $ShotsDir "water_b.png")
        $report.water_changed_percent = $pct
        $report.water_pass = ($pct -gt 1.0)
        Note "water pixels that changed between the two pictures: $pct %"
        # every built-in sky through the menu's own command; the grid either serves its settings asset or not
        Shot "sky_region_before"
        foreach ($c in @("sunrise", "midday", "legacy noon", "sunset", "midnight")) {
            Pick-Sky $c
            Shot ("sky_" + ($c -replace ' ', '_'))
        }
        Pick-Sky "legacy noon"
        Shot "sky_legacy_noon_again"
        Pick-Sky "region"
        Shot "sky_region_after"
    } else { $report.notes += "did not reach a shore point" }
    $report.completed = $true
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "env-report.json") -Encoding utf8
Write-Output "LIVE ENVIRONMENT RUN DONE - other work may run again"

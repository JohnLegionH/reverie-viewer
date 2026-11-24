# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# pivot-live.ps1 - the live check of the movable pivot point. Starts the development export, logs a test avatar in, makes one 1 x 1 x 4 m
# box, and through the automation interface and the handles' own pointer path:
#   1 custom   the pivot is Custom, 0 / 0 / 0 % of the box: its point is the box's minimum corner, worked out here from the region's own
#              position, size and turn
#   2 ring     the z ring turns the box 90 degrees, ten times; each time the minimum corner, read back from the region, is where it was
#              (within 1 mm)
#   3 control  with the pivot back at Centre the same ring turns the box about its middle: the middle stays and the minimum corner moves
#              about a metre, ten times
#   4 root     Root is accepted and its point is the box's own place (a lone box is its own root)
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\pivot-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]   (accounts file, grid address and account come from live.env and the avatar claim)
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: pivot-report.json in the sandbox.
# Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:CLAIM_AVATAR) { $env:CLAIM_AVATAR } elseif ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [int]$Runs = 10
)
# the accounts file, the grid address and the account come from the run's settings (live.env) and the avatar claim, never from an argument
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($script:port -and $script:token) { try { [void](Invoke-Raw $script:port "session" $script:token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $script:port "quit" $script:token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $ShotsDir -PathType Container)) { $problems.Add("-ShotsDir must be an existing folder") }
if (-not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
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
function Note([string]$what) { [Console]::Out.WriteLine(("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what)) }
function Api([string]$command, [hashtable]$b) { Parse (Invoke-Raw $script:port $command $script:token ($b | ConvertTo-Json -Compress)).Body }
function Obs() { Parse (Invoke-Raw $script:port "observe" $script:token).Body }
function In([hashtable]$b) { Api "input" $b }
function Interact([hashtable]$b) { Api "interact" $b }
function Build([hashtable]$b) { Api "build" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-56} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

# ---- the viewer and the login ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
Note "viewer started through godot.ps1, pid $($script:proc.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$script:port = [int]$j.port; $script:token = [string]$j.token
$login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
Note "login requested: $($login.Status)"
$inWorld = Wait-Until { $o = Obs; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } 150
if (-not $inWorld) { Note "not in the world: stopping"; [void](Invoke-Raw $script:port "quit" $script:token); exit 5 }
Start-Sleep -Seconds 4

function Edit([hashtable]$b) { Api "edit" $b }
function OwnObj([uint32]$local) { @(@((Build @{ action = "state" }).own_objects) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Primary([uint32]$local) { (Edit @{ action = "select"; text = "$local" }).primary }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }
# the box's minimum corner in the region, from the region's own place, size and turn about z (the box is only ever turned about z here)
function Corner($o, [double]$zDegrees) {
    $t = $zDegrees * [math]::PI / 180; $c = [math]::Cos($t); $s = [math]::Sin($t)
    $lx = -[double]$o.sx / 2; $ly = -[double]$o.sy / 2; $lz = -[double]$o.sz / 2
    @(([double]$o.x + $lx * $c - $ly * $s), ([double]$o.y + $lx * $s + $ly * $c), ([double]$o.z + $lz))
}
function Dist($a, $b) { [math]::Sqrt([math]::Pow([double]$a[0] - [double]$b[0], 2) + [math]::Pow([double]$a[1] - [double]$b[1], 2) + [math]::Pow([double]$a[2] - [double]$b[2], 2)) }
function Turn-Z([double]$from, [double]$by) { $r = $from + $by; while ($r -gt 180.0001) { $r -= 360 }; while ($r -le -179.9999) { $r += 360 }; $r }
function Same-Turn($a, $b) { $d = [math]::Abs([double]$a - [double]$b) % 360; ($d -le 0.3) -or ($d -ge 359.7) }

# ---- one 1 x 1 x 4 m box in front of the avatar, upright and unturned ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 3.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 7 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z })
$box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $box) { Note "the box was not made: stopping"; [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$box.local)
$A = [uint32]$box.local
NoFocus
$st = Edit @{ action = "state" }
if (-not $st.build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Press "3" $true
[void](EditUntil { param($s) $s.tool -eq "edit" })
[void](Edit @{ action = "select"; text = "$A" })
$px = [double]$box.x; $py = [double]$box.y; $pz = [double]$me[2] + 2.1
[void](Edit @{ action = "size"; x = 1; y = 1; z = 4 })
[void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
[void](Edit @{ action = "position"; x = $px; y = $py; z = $pz })
$ready = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sx 1 0.001) -and (Near $o.sy 1 0.001) -and (Near $o.sz 4 0.001) -and (Near $o.z $pz 0.01) -and (Near (Primary $A).rotation[2] 0 0.2)) { $o } } 20
Check "0 a 1 x 1 x 4 m upright box stands in the region" ([bool]$ready) ("{0}" -f ($ready | ConvertTo-Json -Compress))
if (-not $ready) { $tb = Invoke-TakeBack -Seconds 30; [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
[void](Edit @{ action = "snap"; text = "off" })
[void](Edit @{ action = "manip"; text = "rotate" })

# ---- 1. Custom, 0 / 0 / 0 %: the pivot is the minimum corner ----
$o0 = OwnObj $A
$z0 = [double](Primary $A).rotation[2]
$c0 = Corner $o0 $z0
$pv = Edit @{ action = "pivot"; text = "custom:percent"; x = 0; y = 0; z = 0 }
$state = Edit @{ action = "state" }
$pt = @($state.pivot.point)
Check "1 Custom 0 / 0 / 0 % puts the pivot on the box's minimum corner" ($state.pivot.mode -eq "custom" -and $state.pivot.unit -eq "percent" -and $pt.Count -eq 3 -and (Dist $pt $c0) -le 0.001) ("mode {0} unit {1}; pivot {2}; corner from the region {3}" -f $state.pivot.mode, $state.pivot.unit, ($pt -join ","), ($c0 -join ","))
Shot "pivot-custom-corner"
Note ("pivot call answered {0}; state pivot {1}" -f ($pv.done -join ","), ($state.pivot | ConvertTo-Json -Compress))
$again = Edit @{ action = "state" }
Note ("state again: {0}; selection {1}" -f ($again.pivot | ConvertTo-Json -Compress), (@($again.selection) -join ","))
if ($state.pivot.mode -ne "custom") { $tb = Invoke-TakeBack -Seconds 30; [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token); $report.checks = $checks; $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "pivot-report.json") -Encoding UTF8; exit 1 }

# ---- 2. ten turns of 90 degrees by the z ring: the corner stays ----
$kept = 0; $worst = 0.0; $rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    $oBefore = OwnObj $A
    $zBefore = [double](Primary $A).rotation[2]
    $h = Edit @{ action = "handle"; text = "ring:z"; x = 90; y = 1; z = 0 }
    $want = Turn-Z $zBefore 90
    Note ("run {0} handle answered {1}" -f $i, ($h | ConvertTo-Json -Compress -Depth 4))
    $turned = Wait-Until { $p = Primary $A; if ($p -and (Same-Turn $p.rotation[2] $want)) { $p } } 8
    $oAfter = OwnObj $A
    $zAfter = [double](Primary $A).rotation[2]
    $d = if ($turned) { Dist (Corner $oAfter $zAfter) $c0 } else { 99 }
    if ($d -gt $worst) { $worst = $d }
    if ($turned -and $d -le 0.001) { $kept++ }
    $rows += [ordered]@{ run = $i; turned = [bool]$turned; z = $zAfter; corner_shift_m = $d; handle = $h.done }
}
$report.ring_runs = $rows
Check "2 ten 90-degree ring turns leave the minimum corner where it was (1 mm)" ($kept -eq $Runs) ("{0} of {1} within 1 mm; worst {2:N5} m" -f $kept, $Runs, $worst)

# ---- 3. control: Centre turns the box about its middle, so the corner moves ----
[void](Edit @{ action = "pivot"; text = "centre" })
$stC = Edit @{ action = "state" }
$moved = 0; $middleKept = 0; $rowsC = @()
for ($i = 1; $i -le $Runs; $i++) {
    $oBefore = OwnObj $A
    $zBefore = [double](Primary $A).rotation[2]
    $cBefore = Corner $oBefore $zBefore
    $h = Edit @{ action = "handle"; text = "ring:z"; x = 90; y = 1; z = 0 }
    $want = Turn-Z $zBefore 90
    Note ("run {0} handle answered {1}" -f $i, ($h | ConvertTo-Json -Compress -Depth 4))
    $turned = Wait-Until { $p = Primary $A; if ($p -and (Same-Turn $p.rotation[2] $want)) { $p } } 8
    $oAfter = OwnObj $A
    $zAfter = [double](Primary $A).rotation[2]
    $shift = if ($turned) { Dist (Corner $oAfter $zAfter) $cBefore } else { 0 }
    $mid = Dist @($oAfter.x, $oAfter.y, $oAfter.z) @($oBefore.x, $oBefore.y, $oBefore.z)
    if ($turned -and $shift -ge 0.9) { $moved++ }
    if ($turned -and $mid -le 0.001) { $middleKept++ }
    $rowsC += [ordered]@{ run = $i; turned = [bool]$turned; corner_shift_m = $shift; middle_shift_m = $mid }
}
$report.control_runs = $rowsC
Check "3 control: with Centre the same turns move the corner about a metre and keep the middle" ($stC.pivot.mode -eq "centre" -and $moved -eq $Runs -and $middleKept -eq $Runs) ("pivot {0}; corner moved {1} of {2}; middle kept {3} of {2}" -f $stC.pivot.mode, $moved, $Runs, $middleKept)

# ---- 4. Root: a lone box is its own root ----
$rt = Edit @{ action = "pivot"; text = "root" }
$stR = Edit @{ action = "state" }
$o1 = OwnObj $A
Check "4 Root puts the pivot on the box's own place (a lone box is its own root)" ($stR.pivot.mode -eq "root" -and (Dist @($stR.pivot.point) @($o1.x, $o1.y, $o1.z)) -le 0.001) ("mode {0}; pivot {1}; box {2},{3},{4}" -f $stR.pivot.mode, (@($stR.pivot.point) -join ","), $o1.x, $o1.y, $o1.z)
[void](Edit @{ action = "pivot"; text = "centre" })

# ---- 5. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "5 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)

$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "pivot-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

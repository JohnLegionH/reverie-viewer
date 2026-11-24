# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# snap-live.ps1 - the live check of Snap to objects. Starts the development export, logs a test avatar in, makes two 1 m boxes, and with
# grid snap left on (0.5 m, the viewer's own setting):
#   0 the tick is off when build mode opens
#   1 on       the tick is clicked with the pointer; ten times, box A is clicked with the pointer and its Move X arrow dragged towards box B,
#              let go 2 cm short of flush. B's near face is placed so that A's flush place is 0.11 to 0.21 m off every grid line. While
#              the button is held the viewer reports a guide of four lines; after the release A, read back from the region, is flush with
#              B within 1 mm and has moved along x only
#   2 control  the tick clicked off: the same ten drags land A on a grid line, off flush by more than 0.1 m, with no guide
#   3 alt      the tick on again, one drag with Alt held while the pointer moves: no guide, A on a grid line
# Every press, move and release goes through the automation's input command (the engine's input path, as the operating system's events
# arrive). Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\snap-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]   (accounts file, grid address and account come from live.env and the avatar claim)
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: snap-report.json in the sandbox.
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
function Edit([hashtable]$b) { Api "edit" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-60} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }
function Finish([int]$code) {
    $tb = Invoke-TakeBack -Seconds 40
    $report.take_back = $tb
    [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    $report.checks = $checks
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "snap-report.json") -Encoding UTF8
    exit $code
}

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

function OwnObj([uint32]$local) { @(@((Build @{ action = "state" }).own_objects) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }
# the pointer, always through the engine's input path
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$shift = $false) { In @{ kind = "mouse"; button = $button; down = $down; shift = $shift; ctrl = $false; alt = $false; x = $x; y = $y } }
function MoveTo([double]$x, [double]$y) { Mouse "none" $false $x $y }
function ClickAt([double]$x, [double]$y, [bool]$shift = $false) { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $x $y $shift); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $x $y $shift); Start-Sleep -Milliseconds 300 }
function ScreenOf($id) { $h = Interact @{ action = "hover"; id = [string]$id }; if ($h -and $null -ne $h.screen_x) { @([double]$h.screen_x, [double]$h.screen_y) } else { $null } }
# a control of the Build window: the first call scrolls it into view, the second reads where it stands once the scroll is drawn
function CtlPoint([string]$name) { [void](Edit @{ action = "control"; text = $name }); Start-Sleep -Milliseconds 300; $d = (Edit @{ action = "control"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function Mouse2([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$alt) { In @{ kind = "mouse"; button = $button; down = $down; shift = $false; ctrl = $false; alt = $alt; x = $x; y = $y } }
function State() { Edit @{ action = "state" } }
function OffGrid([double]$v) { [math]::Abs($v - [math]::Round($v * 2) / 2) }

# ---- two 1 m boxes in front of the avatar, along x ----
$me = @((Obs).self.region_position)
$baseX = [math]::Floor([double]$me[0]) - 1.0; $y = [double]$me[1] - 7.0; $z = [double]$me[2] + 2.1
$ids = @(); $locals = @()
for ($k = 0; $k -lt 2; $k++) {
    [void](Interact @{ action = "aim"; x = ($baseX + 3 * $k); y = $y; z = ([double]$me[2] - 0.9); distance = 7 }); Start-Sleep -Milliseconds 1200
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    [void](Build @{ action = "create"; x = ($baseX + 3 * $k); y = $y; z = ([double]$me[2] - 0.9) })
    $box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
    if (-not $box) { Note "box $k was not made: stopping"; Finish 1 }
    Add-Rezzed ([uint32]$box.local)
    $locals += [uint32]$box.local; $ids += [string]$box.id
}
$A = $locals[0]; $B = $locals[1]
NoFocus
if (-not (State).build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Press "3" $true
[void](EditUntil { param($s) $s.tool -eq "edit" })
foreach ($k in 0, 1) {
    [void](Edit @{ action = "select"; text = "$($locals[$k])" })
    [void](Edit @{ action = "size"; x = 1; y = 1; z = 1 })
    [void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
    [void](Edit @{ action = "position"; x = ($baseX + 3 * $k); y = ($y + 0.3 * $k); z = $z })
}
$ready = Wait-Until { $a = OwnObj $A; $b = OwnObj $B; if ($a -and $b -and (Near $a.sx 1 0.001) -and (Near $b.sx 1 0.001) -and (Near $a.z $z 0.01) -and (Near $b.x ($baseX + 3) 0.01)) { $true } } 20
Check "0a two upright 1 m boxes stand in the region" ([bool]$ready) (@((OwnObj $A), (OwnObj $B)) | ConvertTo-Json -Compress)
if (-not $ready) { Finish 1 }
$s0 = State
Check "0b Snap to objects is off when build mode opens, and grid snap is on" ($s0.snap_objects -eq $false -and $s0.snap -eq $true -and $s0.grid.mode -eq "world") ("snap_objects {0}; snap {1}; grid {2} {3}" -f $s0.snap_objects, $s0.snap, $s0.grid.mode, $s0.grid.unit)

# one drag of A's X arrow towards B, let go 2 cm short of flush; what was seen while the button was held, and where the region has A after
function Run-Drag([int]$run, [bool]$alt, [string]$shot) {
    $frac = 0.11 + 0.025 * ($run % 5)
    $x0 = $baseX + 0.04 * ($run % 3)
    $flush = $baseX + 1.5 + $frac          # where A's middle stands flush: this far off a grid line
    $bx = $flush + 1.0                     # B's near face is half a metre before its middle, A's half a metre past its own
    [void](Edit @{ action = "select"; text = "$B" }); [void](Edit @{ action = "position"; x = $bx; y = ($y + 0.3); z = $z })
    [void](Edit @{ action = "select"; text = "$A" }); [void](Edit @{ action = "position"; x = $x0; y = $y; z = $z })
    $placed = Wait-Until { $a = OwnObj $A; $b = OwnObj $B; if ($a -and $b -and (Near $a.x $x0 0.002) -and (Near $b.x $bx 0.002)) { $true } } 12
    if (-not $placed) { return [ordered]@{ run = $run; error = "not placed" } }
    [void](Edit @{ action = "select"; text = "" })
    [void](Interact @{ action = "aim"; x = ($baseX + 1.3); y = $y; z = $z; distance = 6 }); Start-Sleep -Milliseconds 1200
    $a0 = OwnObj $A; $b0 = OwnObj $B
    # the pointer picks A
    $p = ScreenOf $ids[0]
    if (-not $p) { return [ordered]@{ run = $run; error = "A is not on screen" } }
    ClickAt $p[0] $p[1] $false
    $sel = EditUntil { param($s) @($s.selection).Count -eq 1 -and [uint32]@($s.selection)[0] -eq $A }
    if (@($sel.selection).Count -ne 1 -or [uint32]@($sel.selection)[0] -ne $A) { return [ordered]@{ run = $run; error = "the pointer did not pick A"; selection = @($sel.selection) } }
    Start-Sleep -Milliseconds 500
    $want = [double]$b0.x - 1.0
    $dx = $want - 0.02 - [double]$a0.x
    $pts = (Edit @{ action = "handle_points"; text = "axis:x+"; x = $dx; y = 0; z = 0 }).done
    if (-not $pts -or -not $pts.from -or $pts.hit -ne "axis") { return [ordered]@{ run = $run; error = "no arrow"; points = $pts } }
    $pa = $pts.from; $pb = $pts.to
    $pixels = [math]::Sqrt([math]::Pow($pb[0] - $pa[0], 2) + [math]::Pow($pb[1] - $pa[1], 2))
    [void](MoveTo $pa[0] $pa[1]); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true $pa[0] $pa[1])
    for ($i = 1; $i -le 8; $i++) { $t = $i / 8.0; [void](Mouse2 "none" $false ($pa[0] + ($pb[0] - $pa[0]) * $t) ($pa[1] + ($pb[1] - $pa[1]) * $t) $alt); Start-Sleep -Milliseconds 150 }
    [void](Mouse2 "none" $false $pb[0] $pb[1] $alt)
    Start-Sleep -Milliseconds 700
    $held = State
    if ($shot) { Shot $shot }
    [void](Mouse "left" $false $pb[0] $pb[1])
    $moved = Wait-Until { $a = OwnObj $A; if ($a -and [math]::Abs([double]$a.x - [double]$a0.x) -gt 0.5) { $true } } 10
    Start-Sleep -Milliseconds 1500
    $a1 = OwnObj $A; $b1 = OwnObj $B; $after = State
    [ordered]@{ run = $run; dragging_held = [bool]$held.dragging; guide_lines_held = [int]$held.snap_lines; guide_lines_after = [int]$after.snap_lines
        moved = [bool]$moved; start_x = [double]$a0.x; flush_x = ([double]$b1.x - 1.0); end_x = [double]$a1.x
        off_flush_m = [math]::Abs([double]$a1.x - ([double]$b1.x - 1.0)); flush_off_grid_m = (OffGrid ([double]$b1.x - 1.0)); end_off_grid_m = (OffGrid ([double]$a1.x))
        let_go_short_px = [math]::Round(0.02 * $pixels / [math]::Abs($dx), 2)
        only_x = [bool]((Near $a1.y $a0.y 0.001) -and (Near $a1.z $a0.z 0.001) -and (Near $b1.x $b0.x 0.001)) }
}
function Say($r) { Note ($r | ConvertTo-Json -Compress -Depth 4) }

# ---- 1. the tick on, by the pointer ----
$clicked = ClickCtl "snap_objects"
$s1 = EditUntil { param($s) $s.snap_objects -eq $true }
Check "1a the pointer ticks Snap to objects" ($clicked -and $s1.snap_objects -eq $true) ("snap_objects {0}" -f $s1.snap_objects)
$good = 0; $worst = 0.0; $rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    $r = Run-Drag $i $false $(if ($i -eq 1) { "snap-held-guide" } else { "" })
    Say $r; $rows += $r
    if (-not $r.error -and $r.dragging_held -and $r.guide_lines_held -eq 4 -and $r.guide_lines_after -eq 0 -and $r.moved -and $r.only_x -and $r.flush_off_grid_m -ge 0.1 -and $r.off_flush_m -le 0.001) { $good++ }
    if (-not $r.error -and $r.off_flush_m -gt $worst) { $worst = $r.off_flush_m }
}
$report.on_runs = $rows
Check "1b tick on: a pointer drag leaves A flush with B within 1 mm" ($good -eq $Runs) ("{0} of {1}; worst {2:N5} m" -f $good, $Runs, $worst)

# ---- 2. control: the tick off, the same drags ----
$clicked = ClickCtl "snap_objects"
$s2 = EditUntil { param($s) $s.snap_objects -eq $false }
$control = 0; $least = 99.0; $rowsC = @()
for ($i = 1; $i -le $Runs; $i++) {
    $r = Run-Drag $i $false $(if ($i -eq 1) { "snap-held-off" } else { "" })
    Say $r; $rowsC += $r
    if (-not $r.error -and $r.dragging_held -and $r.guide_lines_held -eq 0 -and $r.moved -and $r.only_x -and $r.off_flush_m -gt 0.1 -and $r.end_off_grid_m -le 0.001) { $control++ }
    if (-not $r.error -and $r.off_flush_m -lt $least) { $least = $r.off_flush_m }
}
$report.off_runs = $rowsC
Check "2 control, tick off: the same drags land on a grid line, off flush" ($clicked -and $s2.snap_objects -eq $false -and $control -eq $Runs) ("{0} of {1}; nearest to flush {2:N5} m" -f $control, $Runs, $least)

# ---- 3. Alt lets go ----
[void](ClickCtl "snap_objects")
$s3 = EditUntil { param($s) $s.snap_objects -eq $true }
$r = Run-Drag 1 $true ""
Say $r
$report.alt_run = $r
Check "3 tick on with Alt held: no guide, A lands on a grid line" ($s3.snap_objects -eq $true -and -not $r.error -and $r.guide_lines_held -eq 0 -and $r.moved -and $r.off_flush_m -gt 0.1 -and $r.end_off_grid_m -le 0.001) ("guide lines {0}; off flush {1:N5} m" -f $r.guide_lines_held, $r.off_flush_m)

# ---- 4. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "4 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "snap-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# align-live.ps1 - the live check of Align in the Build window's Arrange section. Starts the development export, logs a test avatar in,
# makes three boxes of different sizes (1, 2 and 3 m) in a row along y, and ten times:
#   - puts the boxes at different places along x by number (set-up only: the staggered start),
#   - picks all three with the real pointer (a click on the first, Shift-clicks on the other two), each press through the engine's input path,
#   - presses the real Max button of the Arrange section with the pointer (axis X, target Selection box),
#   - reads every box back from the region: the high edge on x (place + half the size) is the same for all three within 1 mm, and y, z and
#     the sizes did not change.
# The control reads the same boxes before the press: their high edges differ by at least 0.5 m, so a pass cannot be an unmoved start.
# A second control selects one box alone: the buttons are off and a press moves nothing.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\align-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]   (accounts file, grid address and account come from live.env and the avatar claim)
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: align-report.json in the sandbox.
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
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "align-report.json") -Encoding UTF8
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
function HighX($o) { [double]$o.x + [double]$o.sx / 2 }

# ---- three boxes of 1, 2 and 3 m in a row along y in front of the avatar ----
$me = @((Obs).self.region_position)
$sizes = @(1.0, 2.0, 3.0)
$ids = @(); $locals = @()
$baseX = [double]$me[0] + 6.0; $baseY = [double]$me[1] - 5.0; $z = [double]$me[2] + 2.1
for ($k = 0; $k -lt 3; $k++) {
    [void](Interact @{ action = "aim"; x = ($baseX); y = ($baseY + 5 * $k); z = ([double]$me[2] - 0.9); distance = 7 }); Start-Sleep -Milliseconds 1200
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    [void](Build @{ action = "create"; x = $baseX; y = ($baseY + 5 * $k); z = ([double]$me[2] - 0.9) })
    $box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
    if (-not $box) { Note "box $k was not made: stopping"; Finish 1 }
    Add-Rezzed ([uint32]$box.local)
    $locals += [uint32]$box.local; $ids += [string]$box.id
}
NoFocus
$st = Edit @{ action = "state" }
if (-not $st.build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Press "3" $true
[void](EditUntil { param($s) $s.tool -eq "edit" })
[void](Edit @{ action = "snap"; text = "off" })
for ($k = 0; $k -lt 3; $k++) {
    [void](Edit @{ action = "select"; text = "$($locals[$k])" })
    [void](Edit @{ action = "size"; x = $sizes[$k]; y = $sizes[$k]; z = $sizes[$k] })
    [void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
    [void](Edit @{ action = "position"; x = $baseX; y = ($baseY + 5 * $k); z = $z })
}
$ready = Wait-Until { $r = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] }); if (@($r | Where-Object { $_ }).Count -eq 3 -and (Near $r[0].sx 1 0.001) -and (Near $r[1].sx 2 0.001) -and (Near $r[2].sx 3 0.001) -and (Near $r[2].z $z 0.01)) { $r } } 20
Check "0 three upright boxes of 1, 2 and 3 m stand in a row" ([bool]$ready) (($ready | ConvertTo-Json -Compress -Depth 4))
if (-not $ready) { Finish 1 }
[void](Interact @{ action = "aim"; x = $baseX; y = ($baseY + 5); z = $z; distance = 16 }); Start-Sleep -Milliseconds 1500
if (-not (ClickCtl "section_arrange")) { Check "0b the Arrange section header is on screen" $false "not shown"; Finish 1 }
$opened = EditUntil { param($s) $true }

# ---- 1. ten runs: pick all three by pointer, press Max, read the region ----
$good = 0; $worstSpread = 0.0; $rows = @(); $moved = 0
for ($run = 1; $run -le $Runs; $run++) {
    # a different stagger along x each run (set up by number)
    $offs = @(((($run * 7) % 9) - 4), ((($run * 5 + 3) % 9) - 4), ((($run * 3 + 1) % 9) - 4))
    for ($k = 0; $k -lt 3; $k++) {
        [void](Edit @{ action = "select"; text = "$($locals[$k])" })
        [void](Edit @{ action = "position"; x = ($baseX + $offs[$k]); y = ($baseY + 5 * $k); z = $z })
    }
    $placed = Wait-Until { $r = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] }); if (@($r | Where-Object { $_ }).Count -eq 3 -and (Near $r[0].x ($baseX + $offs[0]) 0.01) -and (Near $r[1].x ($baseX + $offs[1]) 0.01) -and (Near $r[2].x ($baseX + $offs[2]) 0.01)) { $r } } 12
    if (-not $placed) { $rows += [ordered]@{ run = $run; error = "stagger not placed" }; continue }
    $b4 = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] })
    $edgesB = @($b4 | ForEach-Object { HighX $_ })
    $spreadB = ($edgesB | Measure-Object -Maximum).Maximum - ($edgesB | Measure-Object -Minimum).Minimum
    # the real pointer picks the boxes: a click on the first, Shift-clicks on the other two
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 300
    $pts = @(0, 1, 2 | ForEach-Object { ScreenOf $ids[$_] })
    if (@($pts | Where-Object { $_ }).Count -ne 3) { $rows += [ordered]@{ run = $run; error = "a box is not on screen"; points = $pts }; continue }
    ClickAt $pts[0][0] $pts[0][1] $false
    ClickAt $pts[1][0] $pts[1][1] $true
    ClickAt $pts[2][0] $pts[2][1] $true
    $sel = EditUntil { param($s) @($s.selection).Count -ge 3 }
    $picked = @($sel.selection | ForEach-Object { [uint32]$_ } | Sort-Object)
    $allPicked = ($picked.Count -eq 3) -and ($picked -join ",") -eq (($locals | Sort-Object) -join ",")
    if (-not $allPicked) { $rows += [ordered]@{ run = $run; error = "the pointer did not pick all three"; selection = $picked }; continue }
    $arr = $sel.arrange
    # the real Max button
    $pressed = ClickCtl "align_max"
    $aligned = Wait-Until { $r = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] }); $e = @($r | ForEach-Object { HighX $_ }); if (@($r | Where-Object { $_ }).Count -eq 3 -and (($e | Measure-Object -Maximum).Maximum - ($e | Measure-Object -Minimum).Minimum) -le 0.0005) { $r } } 10
    $af = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] })
    $edgesA = @($af | ForEach-Object { HighX $_ })
    $spreadA = ($edgesA | Measure-Object -Maximum).Maximum - ($edgesA | Measure-Object -Minimum).Minimum
    if ($spreadA -gt $worstSpread) { $worstSpread = $spreadA }
    $onlyX = $true
    for ($k = 0; $k -lt 3; $k++) { if (-not ((Near $af[$k].y $b4[$k].y 0.001) -and (Near $af[$k].z $b4[$k].z 0.001) -and (Near $af[$k].sx $b4[$k].sx 0.001))) { $onlyX = $false } }
    # the box furthest along x was already at the edge: the target is the old selection box's high edge
    $want = ($edgesB | Measure-Object -Maximum).Maximum
    $atTarget = Near ($edgesA | Measure-Object -Maximum).Maximum $want 0.001
    $ok = $pressed -and [bool]$aligned -and $spreadA -le 0.001 -and $onlyX -and $atTarget -and $spreadB -ge 0.5
    if ($ok) { $good++ }
    $rows += [ordered]@{ run = $run; spread_before_m = $spreadB; spread_after_m = $spreadA; only_x_changed = $onlyX; at_selection_box_edge = $atTarget; linksets = $arr.linksets; refusal = $arr.refusal; high_edges_after = $edgesA }
    Note ("run {0}: spread {1:N3} -> {2:N5} m; only x {3}; at the old box edge {4}" -f $run, $spreadB, $spreadA, $onlyX, $atTarget)
}
$report.runs = $rows
Shot "align-after"
Check "1 pointer picks three boxes, Max X lines every high edge up within 1 mm, changing only x" ($good -eq $Runs) ("{0} of {1} runs; worst spread {2:N5} m" -f $good, $Runs, $worstSpread)

# ---- 2. control: one box alone, the buttons are off and a press moves nothing ----
[void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 300
$p0 = ScreenOf $ids[0]
if ($p0) { ClickAt $p0[0] $p0[1] $false }
$one = EditUntil { param($s) @($s.selection).Count -eq 1 }
$beforeOne = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] })
[void](ClickCtl "align_max"); Start-Sleep -Milliseconds 1500
$afterOne = @(0, 1, 2 | ForEach-Object { OwnObj $locals[$_] })
$still = $true
for ($k = 0; $k -lt 3; $k++) { if (-not ((Near $afterOne[$k].x $beforeOne[$k].x 0.001) -and (Near $afterOne[$k].y $beforeOne[$k].y 0.001) -and (Near $afterOne[$k].z $beforeOne[$k].z 0.001))) { $still = $false } }
Check "2 control: with one box picked Align is refused and nothing moves" (@($one.selection).Count -eq 1 -and $one.arrange.refusal -eq "toofew" -and $still) ("selection {0}; refusal {1}; nothing moved {2}" -f @($one.selection).Count, $one.arrange.refusal, $still)

# ---- 3. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "3 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "align-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

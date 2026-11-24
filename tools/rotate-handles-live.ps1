# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# rotate-handles-live.ps1 - the live check that the build handles stand on the new box on the first frame after a Rotate drag of several
# objects is let go. Starts the development export, logs a test avatar in, makes three boxes of different sizes (not linked), and ten times:
#   - puts the boxes into a different place and turn by number (set-up only), picks all three with the real pointer (a click on the first,
#     Shift-clicks on the others) and turns on Rotate,
#   - drags the z ring with the real pointer (press on the ring, move in steps, release), every event through the engine's input path,
#   - reads where the viewer drew the handles on the first frame drawn after the release (the viewer's own record of that frame) and, once
#     the region has answered, works out the middle of the box that holds the three turned boxes from the region's own places, sizes and turns.
# A run passes when the first frame drawn after the release is the release's own frame or the next one and the handles stood within 2 cm of
# that box's middle. The control reads where the handles stood before the drag: that place is 5 cm or more from the new box's middle, so
# handles left on the old box would fail the same test.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\rotate-handles-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: rotate-handles-report.json in the sandbox.
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
# The viewer this script started (and only that tree) is stopped when the script ends before the normal logout and quit
. (Join-Path $PSScriptRoot "own-processes.ps1")
function Stop-OwnViewer { if ($script:proc -and -not $script:proc.HasExited) { [void](Stop-OwnedTree $script:proc.Id) } }
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($script:port -and $script:token) { try { [void](Invoke-Raw $script:port "session" $script:token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $script:port "quit" $script:token) } catch { } }
    Stop-OwnViewer
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
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "rotate-handles-report.json") -Encoding UTF8
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
if (-not $tf) { Note "no automation token file: stopping"; Stop-OwnViewer; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$script:port = [int]$j.port; $script:token = [string]$j.token
$login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
Note "login requested: $($login.Status)"
$inWorld = Wait-Until { $o = Obs; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } 150
if (-not $inWorld) { Note "not in the world: stopping"; [void](Invoke-Raw $script:port "quit" $script:token); $script:proc.WaitForExit(30000) | Out-Null; Stop-OwnViewer; exit 5 }
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


# ---- three boxes of different sizes, apart from each other ----
$me = @((Obs).self.region_position)
$locals = @(); $ids = @()
$baseX = [double]$me[0] + 10.0; $baseY = [double]$me[1] + 6.0; $z = [double]$me[2] + 2.1
for ($k = 0; $k -lt 3; $k++) {
    [void](Interact @{ action = "aim"; x = ($baseX); y = ($baseY + 3 * $k); z = ([double]$me[2] - 0.9); distance = 7 }); Start-Sleep -Milliseconds 1200
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    [void](Build @{ action = "create"; x = $baseX; y = ($baseY + 3 * $k); z = ([double]$me[2] - 0.9) })
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
$sizes = @(@(1.0, 1.0, 1.0), @(3.0, 1.0, 1.0), @(1.0, 2.0, 1.0))
function SetUp([int]$run) {
    # a different place and flat turn each run, by number
    $spread = @(0.0, 2.5, 5.0)
    for ($k = 0; $k -lt 3; $k++) {
        [void](Edit @{ action = "select"; text = "$($locals[$k])" })
        [void](Edit @{ action = "size"; x = $sizes[$k][0]; y = $sizes[$k][1]; z = $sizes[$k][2] })
        [void](Edit @{ action = "rotation"; x = 0; y = 0; z = (15 * $run * ($k + 1)) % 360 })
        [void](Edit @{ action = "position"; x = ($baseX + 0.3 * $run + 1.6 * $k); y = ($baseY + $spread[$k]); z = $z })
    }
}
# the middle of the region-aligned box that holds the turned boxes, from the region's own places, sizes and turns
function Extents-Middle($objs) {
    $min = @(1e9, 1e9, 1e9); $max = @(-1e9, -1e9, -1e9)
    foreach ($o in $objs) {
        $qx = [double]$o.rotation[0]; $qy = [double]$o.rotation[1]; $qz = [double]$o.rotation[2]; $qw = [double]$o.rotation[3]
        $n = [math]::Sqrt($qx * $qx + $qy * $qy + $qz * $qz + $qw * $qw); $qx /= $n; $qy /= $n; $qz /= $n; $qw /= $n
        $m = @(
            @((1 - 2 * ($qy * $qy + $qz * $qz)), (2 * ($qx * $qy - $qz * $qw)), (2 * ($qx * $qz + $qy * $qw))),
            @((2 * ($qx * $qy + $qz * $qw)), (1 - 2 * ($qx * $qx + $qz * $qz)), (2 * ($qy * $qz - $qx * $qw))),
            @((2 * ($qx * $qz - $qy * $qw)), (2 * ($qy * $qz + $qx * $qw)), (1 - 2 * ($qx * $qx + $qy * $qy))))
        $s = @([double]$o.sx, [double]$o.sy, [double]$o.sz); $p = @([double]$o.x, [double]$o.y, [double]$o.z)
        for ($a = 0; $a -lt 3; $a++) {
            $h = [math]::Abs($m[$a][0]) * $s[0] / 2 + [math]::Abs($m[$a][1]) * $s[1] / 2 + [math]::Abs($m[$a][2]) * $s[2] / 2
            if ($p[$a] - $h -lt $min[$a]) { $min[$a] = $p[$a] - $h }
            if ($p[$a] + $h -gt $max[$a]) { $max[$a] = $p[$a] + $h }
        }
    }
    @((($min[0] + $max[0]) / 2), (($min[1] + $max[1]) / 2), (($min[2] + $max[2]) / 2))
}
function Dist3($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2) + [math]::Pow($a[2] - $b[2], 2)) }
function Three() { @(0..2 | ForEach-Object { OwnObj $locals[$_] }) }

SetUp 0
$ready = Wait-Until { $r = Three; if (@($r | Where-Object { $_ }).Count -eq 3 -and (Near $r[2].sy 2 0.001) -and (Near $r[2].z $z 0.01)) { $r } } 20
Check "0 three boxes of different sizes stand apart" ([bool]$ready) (($ready | ForEach-Object { "{0}:{1:N2},{2:N2}" -f $_.local, $_.x, $_.y }) -join " ")
if (-not $ready) { Finish 1 }
[void](Interact @{ action = "aim"; x = ($baseX + 2.5); y = ($baseY + 2.5); z = $z; distance = 26 }); Start-Sleep -Milliseconds 1500
if (-not (Wait-Until { (Edit @{ action = "state" }).build_mode } 5)) { Finish 1 }
[void](Edit @{ action = "manip"; text = "rotate" })

# ---- 1. ten drags of the z ring with the real pointer ----
$good = 0; $rows = @(); $worstCm = 0.0; $worstFrames = 0
for ($run = 1; $run -le $Runs; $run++) {
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 400
    SetUp $run
    $here = Wait-Until { $r = Three; if (@($r | Where-Object { $_ }).Count -eq 3 -and (Near $r[0].x ($baseX + 0.3 * $run) 0.01) -and (Near $r[2].y ($baseY + 5.0) 0.01)) { $r } } 15
    if (-not $here) { $rows += [ordered]@{ run = $run; error = "the boxes did not reach their places" }; continue }
    Start-Sleep -Milliseconds 700
    # the three picked with the pointer: a click on the first, Shift-clicks on the others
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 400
    $picked = $true
    for ($k = 0; $k -lt 3; $k++) {
        $pt = ScreenOf $ids[$k]
        if (-not $pt) { $picked = $false; break }
        ClickAt $pt[0] $pt[1] ($k -gt 0)
    }
    $sel = EditUntil { param($s) @($s.selection).Count -ge 3 }
    if (-not $picked -or @($sel.selection).Count -lt 3) { Shot ("rotate-miss-run{0}" -f $run); $rows += [ordered]@{ run = $run; error = "the pointer did not pick all three"; selection = @($sel.selection) }; continue }
    [void](Edit @{ action = "manip"; text = "rotate" })
    $pre = EditUntil { param($s) $null -ne $s.handles_centre }
    $oldCentre = @($pre.handles_centre)
    $pts = (Edit @{ action = "handle_points"; text = "ring:z"; x = 40; y = 1; z = 0 }).done
    if (-not $pts -or -not $pts.from) { $rows += [ordered]@{ run = $run; error = "no ring points" }; continue }
    $pa = $pts.from; $pb = $pts.to
    [void](MoveTo $pa[0] $pa[1]); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true $pa[0] $pa[1])
    for ($i = 1; $i -le 6; $i++) { $t = $i / 6.0; [void](MoveTo ($pa[0] + ($pb[0] - $pa[0]) * $t) ($pa[1] + ($pb[1] - $pa[1]) * $t)); Start-Sleep -Milliseconds 150 }
    $held = Edit @{ action = "state" }
    [void](Mouse "left" $false $pb[0] $pb[1])
    $after = EditUntil { param($s) $s.release -and $null -ne $s.release.first_frame }
    # the region's answer: the three boxes' turns changed, then the box's middle from their places
    $answered = Wait-Until { $r = Three; if (@($r | Where-Object { $_ }).Count -eq 3 -and -not (Near ([double]$r[0].rotation[2]) ([double]$here[0].rotation[2]) 0.01)) { $r } } 12
    Start-Sleep -Milliseconds 800
    $now = Three
    $want = Extents-Middle $now
    $first = if ($after.release.first_centre) { @($after.release.first_centre) } else { $null }
    $frames = if ($null -ne $after.release.first_frame) { [int64]$after.release.first_frame - [int64]$after.release.frame } else { 99 }
    $cm = if ($first) { 100 * (Dist3 $first $want) } else { 1e9 }
    $oldCm = 100 * (Dist3 $oldCentre $want)
    if ($cm -gt $worstCm -and $cm -lt 1e8) { $worstCm = $cm }
    if ($frames -gt $worstFrames -and $frames -lt 90) { $worstFrames = $frames }
    $ok = [bool]$held.dragging -and [bool]$answered -and $frames -le 1 -and $cm -le 2.0 -and $oldCm -ge 5.0
    if ($ok) { $good++ }
    $rows += [ordered]@{ run = $run; dragging_held = $held.dragging; frames_after_release = $frames; handles_to_new_middle_cm = $cm; old_handles_to_new_middle_cm = $oldCm; answered = [bool]$answered }
    Note ("run {0}: first frame after release +{1}; handles {2:N2} cm from the new box's middle; the old place was {3:N1} cm from it" -f $run, $frames, $cm, $oldCm)
    if ($run -in 2, 7) { Shot ("rotate-handles-run{0}" -f $run) }
}
$report.runs = $rows
Check "1 ring drag by pointer, three boxes: handles on the new box's middle on the first frame after release" ($good -eq $Runs) ("{0} of {1} runs; worst {2:N2} cm; most frames after release {3}" -f $good, $Runs, $worstCm, $worstFrames)

# ---- 2. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "2 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "rotate-handles-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

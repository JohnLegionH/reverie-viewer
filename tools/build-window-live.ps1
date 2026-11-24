# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-window-live.ps1 - the live check of four things in the Build window, every click, drag and release a real pointer event through the
# engine's input path:
#   A  Keep Tool selected: unticked, one ground click places a box and the tool hands over to Edit (the control); ticked, each of three
#      ground clicks places another box and Create stays; after a restart of the viewer the tick is still on.
#   B  Drag selection: while the button is held the viewer draws a rectangle from the press to the pointer; it is gone after the release;
#      a press and release in one spot draws none. The count line names the selected linksets and parts with sections open and closed.
#   C  A minimized Build window: dragging its bar leaves it minimized (the control); one click on the bar restores the size and place from
#      before it was minimized, whether or not the bar was moved.
#   D  Distribute, ten times on five boxes skewed in both X and Y (X and Y orders differ): only boxes between the two outermost on the
#      chosen axis move, only along that axis, and they end evenly spaced.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\build-window-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: build-window-report.json in the sandbox.
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
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-70} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

# ---- the viewer and the login (twice: the second run proves the tick is remembered) ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"
$tokDir = Join-Path $local "ReverieViewer\Automation"
function Start-Viewer([string]$tag) {
    $old = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $log = Join-Path $Sandbox "$tag.log"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "420", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
    Note "viewer started through godot.ps1, pid $($script:proc.Id)"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue | Where-Object { $old -notcontains $_.Name }) | Select-Object -First 1 }
    if (-not $tf) { Note "no automation token file: stopping"; Stop-OwnViewer; exit 5 }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $script:port = [int]$j.port; $script:token = [string]$j.token
    $login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
    Note "login requested: $($login.Status)"
    $inWorld = Wait-Until { $o = Obs; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } 150
    if (-not $inWorld) { Note "not in the world: stopping"; [void](Invoke-Raw $script:port "quit" $script:token); $script:proc.WaitForExit(30000) | Out-Null; Stop-OwnViewer; exit 5 }
    Start-Sleep -Seconds 4
}
function Stop-Viewer() {
    $logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
    Note "logout: $($logout.Status)"
    Start-Sleep -Seconds 3
    [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
    if (-not $script:proc.HasExited) { Stop-OwnViewer }
}

function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
function Press([string]$k, [bool]$ctrl = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $false; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $false; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }
# the pointer, always through the engine's input path
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$shift = $false) { In @{ kind = "mouse"; button = $button; down = $down; shift = $shift; ctrl = $false; alt = $false; x = $x; y = $y } }
function MoveTo([double]$x, [double]$y) { Mouse "none" $false $x $y }
function ClickAt([double]$x, [double]$y, [bool]$shift = $false) { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $x $y $shift); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $x $y $shift); Start-Sleep -Milliseconds 350 }
function DragPress($a) { [void](MoveTo $a[0] $a[1]); Start-Sleep -Milliseconds 150; [void](Mouse "left" $true $a[0] $a[1]) }
# a move with the button held: the engine's motion event carries the held button
function DragTo($a, $b) { for ($i = 1; $i -le 6; $i++) { $t = $i / 6.0; [void](Mouse "none" $false ($a[0] + ($b[0] - $a[0]) * $t) ($a[1] + ($b[1] - $a[1]) * $t)); Start-Sleep -Milliseconds 100 } }
function DragRelease($b) { [void](Mouse "left" $false $b[0] $b[1]); Start-Sleep -Milliseconds 400 }
function ScreenOf($id) { $h = Interact @{ action = "hover"; id = [string]$id }; if ($h -and $null -ne $h.screen_x) { @([double]$h.screen_x, [double]$h.screen_y) } else { $null } }
function CtlPoint([string]$name) { [void](Edit @{ action = "control"; text = $name }); Start-Sleep -Milliseconds 300; $d = (Edit @{ action = "control"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function Ui([string]$name) { Api "ui" @{ action = "find"; name = $name } }
function Window() { $w = Ui "floater_build"; @($w.rect | ForEach-Object { [double]$_ }) }
function Fmt($r) { ($r | ForEach-Object { "{0:N0}" -f $_ }) -join "," }
function Own() { @((Build @{ action = "state" }).own_objects) }
function Counts() { $u = Ui "selection_counts"; if ($u) { "$($u.text)|$($u.reach)" } else { "" } }
function CountLine([int]$n) { if ($n -eq 1) { "Selected: 1 linkset, 1 part" } else { "Selected: $n linksets, $n parts" } }
function GroundPoint([double]$x, [double]$y) { $d = (Edit @{ action = "screen"; x = $x; y = $y; z = $groundZ }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
# one real click on the ground; answers the new boxes it made (taken back at the end) and the tool afterwards
function GroundClick([double]$x, [double]$y) {
    $before = @(Own | ForEach-Object { $_.id })
    $p = GroundPoint $x $y
    if (-not $p) { return @{ made = @(); tool = "none" } }
    ClickAt $p[0] $p[1]
    [void](Wait-Until { @(Own | Where-Object { $before -notcontains $_.id }).Count -ge 1 } 12)
    Start-Sleep -Milliseconds 900
    $new = @(Own | Where-Object { $before -notcontains $_.id })
    foreach ($n in $new) { Add-Rezzed ([uint32]$n.local); $script:boxes.Add($n) }
    @{ made = $new; tool = (Edit @{ action = "state" }).tool }
}

Start-Viewer "run1"
$script:boxes = New-Object System.Collections.Generic.List[object]
# the region is the run's own test region: anything of ours an earlier run left in it is taken back first
Start-Sleep -Seconds 3
foreach ($o in (Own)) { Add-Rezzed ([uint32]$o.local) }
if ($script:rezzed.Count -gt 0) { $left = Invoke-TakeBack -Seconds 40; Note "left from an earlier run, taken back: $(@($left.rezzed).Count), remaining $(@($left.remaining).Count)"; $script:rezzed.Clear() }
$me = @((Obs).self.region_position)
$groundZ = [double]$me[2] - 0.9
$baseX = [double]$me[0] + 9.0; $baseY = [double]$me[1] + 0.0
[void](Interact @{ action = "aim"; x = $baseX; y = ($baseY - 3.0); z = $groundZ; distance = 14 }); Start-Sleep -Seconds 2
NoFocus
if (-not (Edit @{ action = "state" }).build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Start-Sleep -Seconds 1
# the Build window is dragged to the right, clear of the ground the clicks use
$bar = (Edit @{ action = "floater_title"; text = "build" }).done
$from = @([double]$bar[0], [double]$bar[1]); $to = @(($from[0] + 3000), $from[1])
DragPress $from; DragTo $from $to; DragRelease $to
$win = Window
Note ("Build window at " + (Fmt $win))

# ---- A. Keep Tool selected ----
Press "4" $true
[void](EditUntil { param($s) $s.tool -eq "create" })
$r0 = GroundClick $baseX ($baseY - 3.0)
Check "A0 control: unticked, one ground click places one box and hands over to Edit" (@($r0.made).Count -eq 1 -and $r0.tool -eq "edit") ("made {0}, tool {1}" -f @($r0.made).Count, $r0.tool)
Press "4" $true
[void](EditUntil { param($s) $s.tool -eq "create" })
Shot "create-tab-unticked"
$ticked = ClickCtl "keep_tool"
Start-Sleep -Milliseconds 400
Shot "create-tab-ticked"
$good = 0; $detail = @()
foreach ($dy in @(-1.5, 0.0, 1.5)) {
    $r = GroundClick $baseX ($baseY + $dy)
    if (@($r.made).Count -eq 1 -and $r.tool -eq "create") { $good++ }
    $detail += ("made {0} tool {1}" -f @($r.made).Count, $r.tool)
}
Check "A1 ticked by pointer: each of three ground clicks places another box, Create stays" ($ticked -and $good -eq 3) ($detail -join "; ")
$r = GroundClick $baseX ($baseY + 3.0)
Check "A2 a fourth ground click places a fifth box" (@($r.made).Count -eq 1 -and $script:boxes.Count -eq 5) ("boxes made in all: {0}" -f $script:boxes.Count)

# ---- B. drag selection and the count line ----
$ids = @($script:boxes | ForEach-Object { [string]$_.id }); $locals = @($script:boxes | ForEach-Object { [uint32]$_.local })
Press "3" $true
[void](EditUntil { param($s) $s.tool -eq "edit" })
[void](Edit @{ action = "snap"; text = "off" })
[void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 600
Check "B0 nothing selected: the count line says so" ((Counts) -eq ((CountLine 0).Replace("linkset,", "linkset,") + "|visible")) (Counts)
$pts = @($ids | ForEach-Object { ,(ScreenOf $_) })
$xs = @($pts | ForEach-Object { $_[0] }); $ys = @($pts | ForEach-Object { $_[1] })
$a = @((($xs | Measure-Object -Minimum).Minimum - 70), (($ys | Measure-Object -Minimum).Minimum - 70))
$b = @((($xs | Measure-Object -Maximum).Maximum + 70), (($ys | Measure-Object -Maximum).Maximum + 70))
$b = @([math]::Min($b[0], $win[0] - 8), $b[1])   # both corners on the world, left of the Build window
# control: a press and release on one spot of empty ground draws no rectangle
DragPress $a; Start-Sleep -Milliseconds 300
$still = Edit @{ action = "state" }
DragRelease $a
Check "B1 control: a press that does not move draws no rectangle" ($null -eq $still.drag_box) ("drag_box {0}" -f ($still.drag_box -join ","))
$drags = 0; $rows = @()
for ($run = 1; $run -le 3; $run++) {
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 500
    $p = $a; $q = $b
    if ($run -eq 2) { $p = $b; $q = $a }   # dragged up and to the left
    DragPress $p; DragTo $p $q; Start-Sleep -Milliseconds 400
    $held = Edit @{ action = "state" }
    if ($run -eq 1) { Shot "drag-box-held" }
    DragRelease $q
    $after = Edit @{ action = "state" }
    $box = @($held.drag_box)
    $shape = $box.Count -eq 4 -and (Near $box[0] ([math]::Min($p[0], $q[0])) 2) -and (Near $box[1] ([math]::Min($p[1], $q[1])) 2) -and (Near $box[2] ([math]::Abs($q[0] - $p[0])) 2) -and (Near $box[3] ([math]::Abs($q[1] - $p[1])) 2)
    $n = @($after.selection).Count
    $ok = $shape -and [int]$held.drag_box_draws -gt 0 -and $null -eq $after.drag_box -and $n -eq 5
    if ($ok) { $drags++ }
    $rows += ("run {0}: drawn [{1}], after release [{2}], selected {3}" -f $run, (Fmt $box), ($after.drag_box -join ","), $n)
}
Check "B2 real drag: rectangle drawn from press to pointer while held, gone on release, 5 selected" ($drags -eq 3) ($rows -join "; ")
Start-Sleep -Milliseconds 600
$want = (CountLine 5) + "|visible"
$seen = @("start: " + (Counts)); $allSame = (Counts) -eq $want
$toggled = 0
foreach ($section in @("placement", "arrange", "shape", "object")) {
    foreach ($pass in 1..2) {
        $wasShown = [bool](Ui "${section}_page").shown
        if (ClickCtl "section_$section") { if ([bool](Ui "${section}_page").shown -ne $wasShown) { $toggled++ } }
        $c = Counts; if ($c -ne $want) { $allSame = $false; $seen += "$section click $pass : $c" }
    }
}
Shot "counts-five-selected"
Check "B3 the count line stays in view through eight section clicks (each section opened and closed)" ($allSame -and $toggled -eq 8) (($seen -join "; ") + "; clicks $toggled")
Press "4" $true; Start-Sleep -Milliseconds 700
$c = Counts
Check "B4 the count line shows with the Create tool too" ($c -eq $want) $c
Press "3" $true; Start-Sleep -Milliseconds 500
# the Build window closed by the program (not by Esc) in the middle of a drag takes the rectangle with it
DragPress $a; DragTo $a $b; Start-Sleep -Milliseconds 300
$mid = Edit @{ action = "state" }
[void](Api "ui_sounds" @{ action = "close_window"; name = "build" }); Start-Sleep -Milliseconds 700
$closed = Edit @{ action = "state" }
DragRelease $b
Check "B5 closing the Build window mid-drag removes the rectangle" ($null -ne $mid.drag_box -and -not $closed.window_open -and $null -eq $closed.drag_box) ("held [{0}], window open {1}, after [{2}]" -f ($mid.drag_box -join ","), $closed.window_open, ($closed.drag_box -join ","))
if (-not (Edit @{ action = "state" }).build_mode) { Press "B" $true; [void](EditUntil { param($s) $s.build_mode }) }
Press "3" $true; Start-Sleep -Milliseconds 700
[void](Edit @{ action = "select"; text = ($locals -join ",") }); Start-Sleep -Milliseconds 700
$win = Window

# ---- C. minimize and one-click restore ----
$good = 0; $rows = @()
for ($run = 1; $run -le 3; $run++) {
    $before = Window
    $min = Ui "minimize"
    ClickAt ([double]$min.point[0]) ([double]$min.point[1]); Start-Sleep -Milliseconds 400
    $small = Window
    $moved = $small
    if ($run -ne 2) {
        # control: a drag of the bar moves it and leaves it minimized
        $bar = (Edit @{ action = "floater_title"; text = "build" }).done
        $p = @([double]$bar[0], [double]$bar[1]); $q = @(($p[0] - 220), ($p[1] + 60 + 20 * $run))
        DragPress $p; DragTo $p $q; DragRelease $q
        $moved = Window
    }
    if ($run -eq 1) { Shot "build-minimized" }
    $bar = (Edit @{ action = "floater_title"; text = "build" }).done
    ClickAt ([double]$bar[0]) ([double]$bar[1]); Start-Sleep -Milliseconds 600
    $back = Window
    if ($run -eq 1) { Shot "build-restored" }
    $c = Counts
    $same = (Near $back[0] $before[0] 1.5) -and (Near $back[1] $before[1] 1.5) -and (Near $back[2] $before[2] 1.5) -and (Near $back[3] $before[3] 1.5)
    $dragOk = $run -eq 2 -or ($moved[3] -lt 60 -and -not (Near $moved[0] $small[0] 20))
    $ok = $small[3] -lt 60 -and $dragOk -and $same -and $c -eq $want
    if ($ok) { $good++ }
    $rows += ("run {0}: before [{1}] minimized [{2}] bar after drag [{3}] after one click [{4}]" -f $run, (Fmt $before), (Fmt $small), (Fmt $moved), (Fmt $back))
}
Check "C one click on the minimized bar restores size and place (3 of 3; a drag keeps it minimized)" ($good -eq 3) ($rows -join "; ")

# ---- D. Distribute on rows skewed in X and Y, ten times ----
function Five() { $all = Own; @($locals | ForEach-Object { $l = $_; $all | Where-Object { [uint32]$_.local -eq $l } | Select-Object -First 1 }) }
$offX = @(-3.5, -2.7, -0.2, 0.9, 3.5)      # uneven steps along X
$offY = @(-4.0, -3.1, -0.6, 2.9, 4.0)      # uneven steps along Y
$orderY = @(2, 4, 0, 3, 1)                 # the box k takes this Y step: the outermost on Y are not the outermost on X
$z = $groundZ + 1.4
[void](ClickCtl "section_arrange")
# the view turns so the five boxes stand clear of the Build window
[void](Interact @{ action = "aim"; x = $baseX; y = ($baseY - 3.5); z = $groundZ; distance = 16 }); Start-Sleep -Seconds 2
NoFocus
$good = 0; $rows = @(); $axisNow = "x"
for ($run = 1; $run -le 10; $run++) {
    $axis = $(if ($run -le 5) { "x" } else { "y" })
    $mode = $(if ($run % 2 -eq 1) { "centres" } else { "gaps" })
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 300
    $wantAt = @()
    for ($k = 0; $k -lt 5; $k++) {
        $i = ($k + $run) % 5
        $wx = $baseX + $offX[$i] + 0.04 * $run; $wy = $baseY + $offY[$orderY[$i]] - 0.03 * $run
        $wantAt += ,@($wx, $wy)
        [void](Edit @{ action = "select"; text = "$($locals[$k])" })
        [void](Edit @{ action = "position"; x = $wx; y = $wy; z = $z })
        Start-Sleep -Milliseconds 150
    }
    [void](Edit @{ action = "select"; text = "" })
    $wasAt = Wait-Until { $f = Five; $all = $true; for ($k = 0; $k -lt 5; $k++) { if (-not ((Near $f[$k].x $wantAt[$k][0] 0.01) -and (Near $f[$k].y $wantAt[$k][1] 0.01))) { $all = $false } }; if ($all) { $f } } 15
    if (-not $wasAt) { $rows += "run $run : the boxes did not reach their places"; continue }
    Start-Sleep -Milliseconds 800
    # all five picked with the pointer: a click on the first, Shift-clicks on the others
    for ($k = 0; $k -lt 5; $k++) { $pt = ScreenOf $ids[$k]; if ($pt) { ClickAt $pt[0] $pt[1] ($k -gt 0) } }
    $sel = EditUntil { param($s) @($s.selection).Count -eq 5 }
    if (@($sel.selection).Count -ne 5) { Shot "distribute-miss-run$run"; $rows += "run $run : the pointer picked $(@($sel.selection).Count) of 5"; continue }
    if ($axis -ne $axisNow) {
        [void](ClickCtl "distribute_axis"); Start-Sleep -Milliseconds 400
        $item = (Edit @{ action = "control"; text = "popup:distribute_axis:1" }).done
        if ($item) { ClickAt ([double]$item[0]) ([double]$item[1]); $axisNow = $axis } else { $rows += "run $run : the axis list did not open"; continue }
    }
    if ($run -eq 1 -or $run -eq 6) { Shot "distribute-run$run-before" }
    if (-not (ClickCtl "distribute_$mode")) { $rows += "run $run : no $mode button"; continue }
    Start-Sleep -Milliseconds 2500
    $isAt = Five
    if ($run -eq 1 -or $run -eq 6) { Shot "distribute-run$run-after" }
    $other = $(if ($axis -eq "x") { "y" } else { "x" })
    $byAxis = @(0..4 | Sort-Object { [double]$wasAt[$_].$axis })
    $outer = @($byAxis[0], $byAxis[4]); $inner = @($byAxis[1..3])
    $outerStill = @($outer | Where-Object { (Near $isAt[$_].x $wasAt[$_].x 0.005) -and (Near $isAt[$_].y $wasAt[$_].y 0.005) -and (Near $isAt[$_].z $wasAt[$_].z 0.005) }).Count -eq 2
    $onlyAxis = @(0..4 | Where-Object { (Near $isAt[$_].$other $wasAt[$_].$other 0.005) -and (Near $isAt[$_].z $wasAt[$_].z 0.005) }).Count -eq 5
    $first = [double]$wasAt[$byAxis[0]].$axis; $step = ([double]$wasAt[$byAxis[4]].$axis - $first) / 4.0
    $even = $true; for ($n = 0; $n -le 4; $n++) { if (-not (Near $isAt[$byAxis[$n]].$axis ($first + $step * $n) 0.01)) { $even = $false } }
    $movedBoxes = @(0..4 | Where-Object { -not ((Near $isAt[$_].x $wasAt[$_].x 0.02) -and (Near $isAt[$_].y $wasAt[$_].y 0.02)) })
    $onlyInner = @($movedBoxes | Where-Object { $inner -notcontains $_ }).Count -eq 0
    $otherEnds = @(0..4 | Sort-Object { [double]$wasAt[$_].$other }); $otherDiffer = ($outer -notcontains $otherEnds[0]) -or ($outer -notcontains $otherEnds[4])
    $ok = $outerStill -and $onlyAxis -and $even -and $onlyInner -and $movedBoxes.Count -ge 1 -and $otherDiffer
    if ($ok) { $good++ }
    $row = "run {0} {1} {2}: outermost still {3}, moved {4} (all between the outermost {5}), only along the axis {6}, even {7}, other-axis ends differ {8}" -f $run, $axis, $mode, $outerStill, $movedBoxes.Count, $onlyInner, $onlyAxis, $even, $otherDiffer
    Note $row
    $rows += $row
}
$report.distribute = $rows
Check "D Distribute by real mouse on rows skewed in X and Y: only the boxes between the outermost move" ($good -eq 10) ("{0} of 10; {1}" -f $good, (($rows | Where-Object { $_ -notmatch "moved \d" }) -join "; "))

# ---- the tick is remembered over a restart ----
NoFocus; Press "B" $true; Start-Sleep -Milliseconds 800
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "E1 everything the first run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
Stop-Viewer
Start-Sleep -Seconds 12
Start-Viewer "run2"
$script:rezzed.Clear(); $script:boxes.Clear()
$me = @((Obs).self.region_position)
$groundZ = [double]$me[2] - 0.9; $baseX = [double]$me[0] + 9.0; $baseY = [double]$me[1]
[void](Interact @{ action = "aim"; x = $baseX; y = $baseY; z = $groundZ; distance = 14 }); Start-Sleep -Seconds 2
NoFocus
if (-not (Edit @{ action = "state" }).build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Press "4" $true
[void](EditUntil { param($s) $s.tool -eq "create" })
Start-Sleep -Seconds 1
Shot "create-tab-after-restart"
$one = GroundClick $baseX ($baseY - 1.5); $two = GroundClick $baseX ($baseY + 1.5)
Check "E2 after a restart the tick is still on: two ground clicks place two boxes, Create stays" (@($one.made).Count -eq 1 -and @($two.made).Count -eq 1 -and $two.tool -eq "create") ("made {0} and {1}, tool {2}" -f @($one.made).Count, @($two.made).Count, $two.tool)
NoFocus; Press "B" $true; Start-Sleep -Milliseconds 800
$tb = Invoke-TakeBack -Seconds 40
$report.take_back_2 = $tb
Check "E3 everything the second run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
Stop-Viewer
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-window-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# edit-menu-live.ps1 - the live check of Edit in the right-click menu of an object. Starts the development export, logs a test avatar in,
# makes a box, and ten times, with the Build window closed:
#   - right-clicks the box with the pointer (through the engine's input path) and reads the menu: Edit and Delete are listed,
#   - presses the Edit entry with the pointer: build mode opens with the Edit tool, the Build window is shown, the box is the selection
#     and its handles are drawn,
#   - drags the Move arrow with the pointer (press, moves, release) and reads the box back from the region: it moved along x only.
# Controls: before Edit nothing is selected and no handles are drawn; a right-click on the ground and on the own avatar lists neither Edit nor
# Delete. A last run chooses Delete in the menu and reads the region: the box is gone.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\edit-menu-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]   (accounts file, grid address and account come from live.env and the avatar claim)
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: edit-menu-report.json in the sandbox.
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
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "edit-menu-report.json") -Encoding UTF8
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

function ClickRight([double]$x, [double]$y) { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "right" $true $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "right" $false $x $y); Start-Sleep -Milliseconds 300 }
function CloseMenu() { $s = Interact @{ action = "state" }; if ($s.menu_open) { Press "ESC"; [void](Wait-Until { $t = Interact @{ action = "state" }; if (-not $t.menu_open) { $t } } 5) } }
function LeaveBuild() {
    NoFocus
    $s = Edit @{ action = "state" }
    if ($s.build_mode) { Press "ESC" }
    EditUntil { param($t) -not $t.build_mode -and -not $t.window_open }
}
# a handle dragged by hand: press on it, move in steps while the button stays down, read the object while it is held and after the release
function HandDrag([string]$what, [double]$x, [double]$y, [double]$z, [scriptblock]$read) {
    $pts = (Edit @{ action = "handle_points"; text = $what; x = $x; y = $y; z = $z }).done
    if (-not $pts -or -not $pts.from) { return @{ error = "no handle points: $($pts | ConvertTo-Json -Compress)" } }
    $pa = $pts.from; $pb = $pts.to
    $before = & $read
    [void](MoveTo $pa[0] $pa[1]); Start-Sleep -Milliseconds 150
    $press = Mouse "left" $true $pa[0] $pa[1]
    for ($i = 1; $i -le 6; $i++) { $t = $i / 6.0; [void](MoveTo ($pa[0] + ($pb[0] - $pa[0]) * $t) ($pa[1] + ($pb[1] - $pa[1]) * $t)); Start-Sleep -Milliseconds 150 }
    $held = Mouse "none" $false $pb[0] $pb[1]
    Start-Sleep -Milliseconds 1500
    $during = & $read
    $dragging = (Edit @{ action = "state" }).dragging
    [void](Mouse "left" $false $pb[0] $pb[1]); Start-Sleep -Milliseconds 1500
    $after = & $read
    @{ points = $pts; before = $before; during = $during; after = $after; steering = [bool]$held.steering; dragging_held = [bool]$dragging; hit = $pts.hit }
}

# ---- a box in front of the avatar, the Build window closed ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z })
$box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $box) { Check "0 a box is made" $false "none"; Finish 1 }
Add-Rezzed ([uint32]$box.local); $A = [uint32]$box.local
$home3 = @([double]$box.x, [double]$box.y, [double]$box.z)
[void](LeaveBuild)
$e0 = Edit @{ action = "state" }
Check "0 a box is made and the Build window is closed" ([bool]$box -and -not $e0.build_mode -and -not $e0.window_open) ("box {0}; build mode {1}; window {2}" -f $A, $e0.build_mode, $e0.window_open)

$runs = @()
for ($n = 1; $n -le $Runs; $n++) {
    $r = [ordered]@{ run = $n; ok = $false }
    # put the box back where it started (set-up only: by number) and the camera on it, with the Build window closed
    [void](LeaveBuild)
    $cur = OwnObj $A
    if (-not (Near $cur.x $home3[0] 0.05)) {
        [void](Edit @{ action = "select"; text = "$A" })
        [void](Edit @{ action = "position"; x = $home3[0]; y = $home3[1]; z = $home3[2] })
        [void](Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x $home3[0] 0.05)) { $o } } 15)
        [void](LeaveBuild)
    }
    [void](Interact @{ action = "aim"; x = $home3[0]; y = $home3[1]; z = $home3[2]; distance = 8 }); Start-Sleep -Milliseconds 1200
    NoFocus
    $pre = Edit @{ action = "state" }
    $sp = ScreenOf $box.id
    if (-not $sp) { $r.error = "box not on screen"; $runs += $r; continue }
    ClickRight $sp[0] $sp[1]
    $open = Wait-Until { $s = Interact @{ action = "state" }; if ($s.menu_open) { $s } } 5
    $r.menu = (@($open.entries) -join ","); $r.pre_build_mode = [bool]$pre.build_mode; $r.pre_selection = @($pre.selection).Count; $r.pre_handles = [bool]$pre.handles_drawn
    $entry = Api "ui_sounds" @{ action = "find"; name = "entry_edit" }
    if (-not ($open -and $entry.found)) { $r.error = "no Edit entry in the menu [$($r.menu)]"; CloseMenu; $runs += $r; continue }
    ClickAt ([double]$entry.x) ([double]$entry.y)
    $ed = EditUntil { param($s) $s.build_mode -and $s.tool -eq "edit" -and @($s.selection) -contains $A -and $s.handles_drawn }
    $m = Interact @{ action = "state" }
    $r.build_mode = [bool]$ed.build_mode; $r.tool = $ed.tool; $r.window = [bool]$ed.window_open; $r.selection = (@($ed.selection) -join ","); $r.handles = [bool]$ed.handles_drawn; $r.menu_open_after = [bool]$m.menu_open
    # a handle dragged with the pointer: the box follows along x
    [void](Edit @{ action = "manip"; text = "move" })
    $readPos = { $o = OwnObj $A; if ($o) { @([double]$o.x, [double]$o.y, [double]$o.z) } }
    $mv = HandDrag "axis:x+" 2 0 0 $readPos
    if ($mv.error) { $r.error = $mv.error; $runs += $r; continue }
    $r.x_before = $mv.before[0]; $r.x_held = $mv.during[0]; $r.x_after = $mv.after[0]
    $r.moved = $mv.after[0] - $mv.before[0]
    $r.only_x = (Near $mv.after[1] $mv.before[1] 0.05) -and (Near $mv.after[2] $mv.before[2] 0.05)
    $r.ok = ($r.menu -match "edit") -and ($r.menu -match "delete") -and $r.build_mode -and $r.tool -eq "edit" -and $r.window -and $r.handles -and (@($ed.selection) -contains $A) `
        -and -not $r.menu_open_after -and -not $r.pre_build_mode -and $r.pre_selection -eq 0 -and -not $r.pre_handles -and ($r.moved -gt 1) -and $r.only_x -and -not $mv.steering
    Note ("run {0}: menu [{1}]; build mode {2}, tool {3}, window {4}, selection {5}, handles {6}; x {7:N3} -> {8:N3} (moved {9:N3}), only x {10}" -f $n, $r.menu, $r.build_mode, $r.tool, $r.window, $r.selection, $r.handles, $r.x_before, $r.x_after, $r.moved, $r.only_x)
    $runs += $r
}
$report.runs = $runs
$good = @($runs | Where-Object { $_.ok }).Count
Check "1 right-click Edit, then a handle drag moves the box (Build window closed at the start)" ($good -eq $Runs) ("{0} of {1} runs" -f $good, $Runs)

# ---- controls: before Edit nothing is selected and no handles are drawn; the ground and the own avatar offer neither entry ----
$ctl = $runs | Where-Object { $_.ok } | Select-Object -First 1
Check "2 control: before Edit the Build window was closed, nothing selected, no handles" ([bool]$ctl -and -not $ctl.pre_build_mode -and $ctl.pre_selection -eq 0 -and -not $ctl.pre_handles) ("pre build mode {0}, selection {1}, handles {2}" -f $ctl.pre_build_mode, $ctl.pre_selection, $ctl.pre_handles)
[void](LeaveBuild)
$me2 = @((Obs).self.region_position)
$g = Interact @{ action = "hover"; x = [double]$me2[0] + 2.0; y = [double]$me2[1] + 3.0; z = [double]$me2[2] - 1.0 }
$landOk = $false; $landDetail = "ground point not on screen: not exercised"
if ($g -and $null -ne $g.screen_x) {
    ClickRight ([double]$g.screen_x) ([double]$g.screen_y)
    $ls = Wait-Until { $s = Interact @{ action = "state" }; if ($s.menu_open) { $s } } 5
    $landDetail = "menu {0} [{1}]" -f $ls.menu, (@($ls.entries) -join ",")
    $landOk = [bool]$ls -and $ls.menu -eq "Land" -and -not (@($ls.entries) -contains "edit") -and -not (@($ls.entries) -contains "delete")
    CloseMenu
}
Check "3 control: a right-click on the ground lists neither Edit nor Delete" $landOk $landDetail
$self = Interact @{ action = "hover"; id = [string](Obs).self.id }
$selfOk = $false; $selfDetail = "own avatar not on screen: not exercised"
if ($self -and $null -ne $self.screen_x) {
    ClickRight ([double]$self.screen_x) ([double]$self.screen_y)
    $ss = Wait-Until { $s = Interact @{ action = "state" }; if ($s.menu_open) { $s } } 5
    $selfDetail = "menu {0} [{1}]" -f $ss.menu, (@($ss.entries) -join ",")
    $selfOk = [bool]$ss -and -not (@($ss.entries) -contains "edit") -and -not (@($ss.entries) -contains "delete")
    CloseMenu
}
Check "4 control: a right-click on the own avatar lists neither Edit nor Delete" $selfOk $selfDetail

# ---- Delete from the same menu, Build window closed: the box leaves the region ----
[void](LeaveBuild)
[void](Interact @{ action = "aim"; x = $home3[0]; y = $home3[1]; z = $home3[2]; distance = 8 }); Start-Sleep -Milliseconds 1200
NoFocus
$sp = ScreenOf $box.id
$deleted = $false; $delDetail = "box not on screen"
if ($sp -and (OwnObj $A)) {
    ClickRight $sp[0] $sp[1]
    $dm = Wait-Until { $s = Interact @{ action = "state" }; if ($s.menu_open) { $s } } 5
    $entry = Api "ui_sounds" @{ action = "find"; name = "entry_delete" }
    if ($dm -and $entry.found) {
        ClickAt ([double]$entry.x) ([double]$entry.y)
        $gone = Wait-Until { if (-not (OwnObj $A)) { $true } } 20
        $deleted = [bool]$gone
        $delDetail = "menu [{0}]; box {1} still in the region: {2}" -f (@($dm.entries) -join ","), $A, [bool](OwnObj $A)
    } else { $delDetail = "no Delete entry in [{0}]" -f (@($dm.entries) -join ","); CloseMenu }
}
Check "5 Delete in the menu removes the box from the region" $deleted $delDetail

$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
Finish $(if ($failed -eq 0) { 0 } else { 1 })

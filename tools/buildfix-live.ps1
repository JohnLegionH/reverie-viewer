# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# buildfix-live.ps1 - Create handing back to Edit, Keep Tool selected, the right-click menu's ways of closing and Select Face's marker,
# through the engine's input path, as a hand drives them: every pointer press, move and release and every key goes through the
# automation's input command (Input.ParseInputEvent, as the operating system's events arrive); the automation only says where things are
# on screen and reads back what the viewer holds. In order:
#   - Ctrl+B and a click on the ground: the box is made, the tool becomes Edit with the Move handles and the box is selected;
#   - Ctrl+4, Keep Tool selected ticked by a click, a second click on the ground: a second box, and the create tool stays;
#   - the right-click menu on a box closed by a left click on the ground (which does nothing else), by Esc, by a right-click on the ground
#     (which opens the ground's menu) and by choosing an entry; in build mode, a click outside leaves the edit selection as it was;
#   - Select Face chosen by a click, a face of each box clicked (the second with Shift): two faces on two objects, no whole-object outline,
#     and a picture of the marker.
# Takes back everything it rezzed, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\buildfix-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: buildfix-report.json in the sandbox. Exit: 0 all checks
# passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$GridInfoUrl,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = "",
    [switch]$TakeBackEarlier,   # also take back the account's objects already on the region (left by an earlier run that stopped)
    [int]$StartSeconds = 60
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    # a check that stops part way still takes back what it rezzed before it logs out
    if ($script:port -and $script:token -and (Get-Command Invoke-TakeBack -ErrorAction SilentlyContinue)) { try { [void](Invoke-TakeBack -Seconds 20) } catch { } }
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
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false } }
function Interact([hashtable]$b) { Api "interact" $b }
function Build([hashtable]$b) { Api "build" $b }
function World([hashtable]$b) { Api "world_sounds" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-56} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

# ---- the viewer and the login ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
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
function OwnPrim([uint32]$local) { @(@((Build @{ action = "state" }).own_prims) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Primary([uint32]$local) { (Edit @{ action = "select"; text = "$local" }).primary }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
# every key this check presses goes through the engine's key input, as a person's key does (never by calling the action)
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }


# ---- helpers: the pointer and the keys, always through the engine's input path ----
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$shift = $false) { In @{ kind = "mouse"; button = $button; down = $down; shift = $shift; ctrl = $false; alt = $false; x = $x; y = $y } }
function MoveTo([double]$x, [double]$y) { Mouse "none" $false $x $y }
function ClickAt([double]$x, [double]$y, [string]$button = "left") { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse $button $true $x $y); Start-Sleep -Milliseconds 120; [void](Mouse $button $false $x $y); Start-Sleep -Milliseconds 200 }
function ScreenOf($id) { $h = Interact @{ action = "hover"; id = [string]$id }; if ($h -and $null -ne $h.screen_x) { @([double]$h.screen_x, [double]$h.screen_y) } else { $null } }
function Rot([uint32]$local) { [double](Primary $local).rotation[2] }
function AngleDiff([double]$a, [double]$b) { $d = $a - $b; while ($d -gt 180) { $d -= 360 }; while ($d -le -180) { $d += 360 }; $d }
# a handle dragged by hand: press on it, move in steps while the button stays down, read the object while it is still held, then let go
function HandDrag([string]$what, [double]$x, [double]$y, [double]$z, [scriptblock]$read) {
    $pts = (Edit @{ action = "handle_points"; text = $what; x = $x; y = $y; z = $z }).done
    if (-not $pts -or -not $pts.from) { return @{ error = "no handle points: $($pts | ConvertTo-Json -Compress)" } }
    $pa = $pts.from; $pb = $pts.to   # not $a: PowerShell names ignore case, and $A is the box
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
    # the pointer moves on with the button up: nothing may follow it
    for ($i = 1; $i -le 4; $i++) { [void](MoveTo ($pb[0] + 25 * $i) ($pb[1] + 15 * $i)); Start-Sleep -Milliseconds 150 }
    Start-Sleep -Milliseconds 1500
    $later = & $read
    @{ points = $pts; before = $before; during = $during; after = $after; later = $later; steering = [bool]$held.steering; dragging_held = [bool]$dragging; hit = $pts.hit }
}


function Ui([string]$name) { $d = (Edit @{ action = "ui_point"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
# a control of the Build window: the first call scrolls its tab so it is in view, the second reads where it stands once the scroll is drawn
function CtlPoint([string]$name) { [void](Edit @{ action = "control"; text = $name }); Start-Sleep -Milliseconds 300; $d = (Edit @{ action = "control"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
function ClickUi([string]$name) { $p = Ui $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
# text typed into the field that has the keyboard: every character a key press
function TypeText([string]$text) { foreach ($ch in $text.ToCharArray()) { Press ([string]$ch).ToUpperInvariant(); Start-Sleep -Milliseconds 40 } }
# a field clicked, its old text removed with Backspace, the new text typed, Enter
function SetField([scriptblock]$point, [string]$text) {
    $p = & $point
    if (-not $p) { return $false }
    ClickAt $p[0] $p[1]; Start-Sleep -Milliseconds 200
    Press "END"; for ($i = 0; $i -lt 48; $i++) { Press "BACKSPACE" }
    TypeText $text; Press "ENTER"; Start-Sleep -Milliseconds 400
    $true
}
function State() { Edit @{ action = "state" } }
function Sel() { @((State).selection) }
function Feat() { (State).features }
function Contents() { @((State).contents) }
function TopOf($o) { ScreenOf $o.id }
function IState() { Interact @{ action = "state" } }
function MenuOpen() { [bool](IState).menu_open }
function WaitMenu([bool]$open) { Wait-Until { if ((MenuOpen) -eq $open) { $true } } 5 }
function Ground([double]$dx, [double]$dy) { $p = (Edit @{ action = "screen"; x = $spot.x + $dx; y = $spot.y + $dy; z = $spot.z }).done; if ($p) { @([double]$p[0], [double]$p[1]) } else { $null } }

# ---- 1. Create hands back to Edit: Ctrl+B, a click on the ground; the tool becomes Edit with Move and the new box is selected ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
if ($TakeBackEarlier) { foreach ($o in @((Build @{ action = "state" }).own_objects)) { Add-Rezzed ([uint32]$o.local) } }
NoFocus
if ((State).build_mode) { Press "ESC"; Start-Sleep -Milliseconds 300 }
Press "B" $true; Start-Sleep -Milliseconds 400
$toolBefore = (State).tool
$gp = Ground 0 0
if ($gp) { ClickAt $gp[0] $gp[1] }
$boxA = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $boxA) { Check "1 Create makes a box" $false "none"; [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$boxA.local); $A = [uint32]$boxA.local
$handed = EditUntil { param($s) $s.tool -eq "edit" -and @($s.selection) -contains $A }
Shot "create-handed-back"
Check "1 Create hands back: the tool becomes Edit (Move) and the new box is selected" ($toolBefore -eq "create" -and $handed.tool -eq "edit" -and @($handed.selection).Count -eq 1 -and @($handed.selection) -contains $A -and $handed.manip -ne "rotate" -and $handed.manip -ne "scale") ("tool before {0}, after {1}, manip {2}; selection [{3}], box {4}" -f $toolBefore, $handed.tool, $handed.manip, (@($handed.selection) -join ","), $A)

# ---- 2. Keep Tool selected: ticked by a click, the create tool stays after a placement ----
Press "4" $true; Start-Sleep -Milliseconds 400
$kt = ClickCtl "keep_tool"; Start-Sleep -Milliseconds 400
$before2 = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
# a spot of ground on screen clear of the Build window (on the left) and of the first box
$pa0 = ScreenOf $boxA.id
$gp2 = $null
foreach ($d in @(@(3, 0), @(-3, 0), @(0, 3), @(0, -3), @(2.5, 2.5), @(-2.5, -2.5), @(2.5, -2.5), @(-2.5, 2.5))) {
    $p = Ground $d[0] $d[1]
    if ($p -and $p[0] -gt 560 -and $p[0] -lt 1100 -and $p[1] -gt 120 -and $p[1] -lt 500 -and (-not $pa0 -or (Flat $p $pa0) -gt 90)) { $gp2 = $p; break }
}
if ($gp2) { ClickAt $gp2[0] $gp2[1] } else { Note "no clear spot of ground on screen for the second box" }
$boxB = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before2 -notcontains $_.id }) | Select-Object -First 1 } 20
if ($boxB) { Add-Rezzed ([uint32]$boxB.local); $B = [uint32]$boxB.local }
Start-Sleep -Milliseconds 800
$kept = State
Check "2 with Keep Tool selected ticked the create tool stays after a placement" ([bool]$kt -and [bool]$boxB -and $kept.tool -eq "create" -and [bool](Build @{ action = "state" }).placing) ("tick clicked {0}; second box {1}; tool {2}; placing {3}" -f $kt, [bool]$boxB, $kept.tool, (Build @{ action = "state" }).placing)
[void](ClickCtl "keep_tool"); Start-Sleep -Milliseconds 300   # the tick cleared again, so the setting is as it was

NoFocus
Press "ESC"; Start-Sleep -Milliseconds 400   # build mode closes

# ---- 3. the right-click menu closes: a click outside (that does nothing else), Esc, another right-click, a chosen entry ----
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y + 1.5; z = $spot.z; distance = 9 }); Start-Sleep -Milliseconds 1500
$pa = ScreenOf $boxA.id
$empty = Ground 3 -3
$clicksBefore = [int](IState).unhandled_clicks
ClickAt $pa[0] $pa[1] "right"; $o1 = WaitMenu $true
$selBefore = [bool](IState).selected
ClickAt $empty[0] $empty[1]; $c1 = WaitMenu $false
$after1 = IState
Shot "menu-closed-by-click"
Check "3a a click in the world outside the right-click menu closes it and does nothing else" ([bool]$o1 -and [bool]$c1 -and [int]$after1.unhandled_clicks -eq $clicksBefore) ("opened {0}; closed {1}; clicks the world handled before {2}, after {3}; right-click selection before {4}, after {5}" -f [bool]$o1, [bool]$c1, $clicksBefore, $after1.unhandled_clicks, $selBefore, $after1.selected)
ClickAt $pa[0] $pa[1] "right"; $o2 = WaitMenu $true
Press "ESC"; $c2 = WaitMenu $false
Check "3b Esc closes the right-click menu" ([bool]$o2 -and [bool]$c2) ("opened {0}; closed {1}" -f [bool]$o2, [bool]$c2)
ClickAt $pa[0] $pa[1] "right"; $o3 = WaitMenu $true; $entriesObj = @((IState).entries) -join ","
ClickAt $empty[0] $empty[1] "right"; Start-Sleep -Milliseconds 600
$s3 = IState
Check "3c another right-click closes the menu and opens the menu of what it was made on" ([bool]$o3 -and [bool]$s3.menu_open -and (@($s3.entries) -join ",") -ne $entriesObj) ("object menu [{0}]; after a right-click on the ground: open {1}, [{2}]" -f $entriesObj, $s3.menu_open, (@($s3.entries) -join ","))
Press "ESC"; [void](WaitMenu $false)
ClickAt $pa[0] $pa[1] "right"; $o4 = WaitMenu $true
$entry = @((IState).entries) | Where-Object { $_ -eq "inspect" -or $_ -eq "object_profile" } | Select-Object -First 1
$ep = if ($entry) { Ui ("entry_" + $entry) } else { $null }
if ($ep) { ClickAt $ep[0] $ep[1] }
$c4 = WaitMenu $false
Check "3d choosing an entry closes the menu" ([bool]$o4 -and [bool]$ep -and [bool]$c4) ("opened {0}; entry {1} at {2}; closed {3}" -f [bool]$o4, $entry, (@($ep) -join ","), [bool]$c4)
NoFocus
Press "ESC"; Start-Sleep -Milliseconds 300
# in build mode (where the edit tools take the left button first) a click outside closes the menu and leaves the edit selection alone
Press "3" $true; Start-Sleep -Milliseconds 400
ClickAt $pa[0] $pa[1]; $selA = EditUntil { param($s) @($s.selection) -contains $A }
ClickAt $pa[0] $pa[1] "right"; $o5 = WaitMenu $true
ClickAt $empty[0] $empty[1]; $c5 = WaitMenu $false
$s5 = State
Check "3e in build mode a click outside closes the right-click menu and does not change the edit selection" ([bool]$o5 -and [bool]$c5 -and @($s5.selection) -contains $A) ("opened {0}; closed {1}; selection before [{2}], after [{3}]" -f [bool]$o5, [bool]$c5, (@($selA.selection) -join ","), (@($s5.selection) -join ","))

# ---- 4. Select Face: faces chosen on two boxes are marked, and those boxes get no whole-object outline ----
[void](ClickCtl "manip_face"); Start-Sleep -Milliseconds 400
$pA = ScreenOf $boxA.id; $pB = if ($boxB) { ScreenOf $boxB.id } else { $null }
if ($pA) { ClickAt $pA[0] $pA[1] }
if ($pB) { [void](MoveTo $pB[0] $pB[1]); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $pB[0] $pB[1] $true); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $pB[0] $pB[1] $true); Start-Sleep -Milliseconds 300 }
$fs = EditUntil { param($s) $s.select_faces -and @($s.faces).Count -ge 2 }
Start-Sleep -Milliseconds 600
$outline = [int](IState).outline_pieces
Shot "select-face-two-boxes"
$objs = @($fs.faces | ForEach-Object { $_.local } | Select-Object -Unique)
Check "4 Select Face: a face on each of two boxes chosen by clicks, no whole-object outline drawn (picture: select-face-two-boxes)" ($fs.select_faces -and @($fs.faces).Count -eq 2 -and $objs.Count -eq 2 -and $outline -eq 0) ("faces {0} on objects [{1}]; whole-object outline pieces {2}" -f @($fs.faces).Count, ($objs -join ","), $outline)
NoFocus
Press "ESC"; Start-Sleep -Milliseconds 300

# ---- 5. take back, log out ----
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "5 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "buildfix-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

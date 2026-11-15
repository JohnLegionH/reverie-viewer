# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# edit-input-live.ps1 - the edit tools through the engine's input path, as a hand drives them: every pointer press, move and release and
# every key goes through the automation's input command (Input.ParseInputEvent, as the operating system's events arrive). Makes a box,
# opens build mode (Ctrl+B, Ctrl+3), clicks it, then:
#   - drags the Move arrow, a Rotate ring and a Stretch face box, reading the object while the button is still held (it must follow the
#     pointer) and after the pointer moves on with the button up (it must stay where it was let go); the ring once with the pointer well
#     outside the rings (whole 5.625-degree steps) and once on the ring (a smooth turn);
#   - Grid Options (Ctrl+Shift+B), Units typed as 1 with Enter, the window closed with its close button, then Shift+X: the middle on a whole metre;
#   - moves, turns and resizes the box by numbers, then Ctrl+Z three times (each change undone in turn) and Ctrl+Y (the move again);
#   - right-clicks the box and clicks Sit Here in build mode and out of it: the avatar sits and the menu is closed.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\edit-input-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: edit-input-report.json in the sandbox. Exit: 0 all checks
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
    [int]$StartSeconds = 60
)
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

# ---- 1. a box, build mode, the Edit tool, the box clicked ----
$me = @((Obs).self.region_position)
# a spot clear of anything the region already holds (an earlier run may have left boxes near the arrival point)
$taken = @((Build @{ action = "state" }).own_objects)
$spot = $null
foreach ($dy in @(0, 4, -4, 8, -8)) {
    $c = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + $dy; z = [double]$me[2] - 0.9 }
    if (-not ($taken | Where-Object { [math]::Abs([double]$_.x - $c.x) -lt 3 -and [math]::Abs([double]$_.y - $c.y) -lt 3 })) { $spot = $c; break }
}
if (-not $spot) { $spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 12; z = [double]$me[2] - 0.9 } }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z })
$box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $box) { Check "1 a box is made" $false "none"; [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$box.local); $A = [uint32]$box.local
NoFocus
$st = Edit @{ action = "state" }
if (-not $st.build_mode) { Press "B" $true }
Press "3" $true
$e = EditUntil { param($s) $s.build_mode -and $s.tool -eq "edit" }
$sp = ScreenOf $box.id
if ($sp) { ClickAt $sp[0] $sp[1] }
$sel = EditUntil { param($s) @($s.selection) -contains $A }
Check "1 build mode, Ctrl+3, a click on the box selects it" ([bool]$e -and @($sel.selection) -contains $A) ("tool {0}; box {1}; selection {2}; clicked at {3}" -f $e.tool, $A, (@($sel.selection) -join ","), (@($sp) -join ","))
if (-not (@($sel.selection) -contains $A)) { [void](Edit @{ action = "select"; text = "$A" }) }   # so the later checks still run
# typed numbers make a thin floor turned 45 degrees); the typing itself is not what this check is about
[void](Edit @{ action = "size"; x = 2; y = 2; z = 0.2 })
[void](Edit @{ action = "rotation"; x = 0; y = 0; z = 45 })
[void](Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sx 2 0.01) -and (Near $o.sz 0.2 0.01)) { $o } } 15)
[void](Wait-Until { $p = Primary $A; if ($p -and (Near $p.rotation[2] 45 0.3)) { $p } } 15)
[void](Interact @{ action = "aim"; x = [double]$box.x; y = [double]$box.y; z = [double]$box.z; distance = 7 }); Start-Sleep -Milliseconds 1500

# ---- 2. Move: the red arrow follows the pointer while the button is held and stays where it is let go ----
[void](Edit @{ action = "manip"; text = "move" })
$readPos = { $o = OwnObj $A; if ($o) { @([double]$o.x, [double]$o.y, [double]$o.z) } }
$mv = HandDrag "axis:x+" 2 0 0 $readPos
$report.move = $mv
if ($mv.error) { Note $mv.error; $mv = @{ before = @(0, 0, 0); during = @(0, 0, 0); after = @(0, 0, 0); later = @(9, 9, 9); steering = $true; dragging_held = $false; hit = "none" } }
$movedDuring = $mv.during -and $mv.before -and ([math]::Abs($mv.during[0] - $mv.before[0]) -gt 0.4)
$stays = $mv.after -and $mv.later -and (Near $mv.after[0] $mv.later[0] 0.02) -and (Near $mv.after[1] $mv.later[1] 0.02)
$along = $mv.after -and (Near $mv.after[1] $mv.before[1] 0.05) -and (Near $mv.after[2] $mv.before[2] 0.05) -and ([math]::Abs($mv.after[0] - $mv.before[0]) -gt 1)
Check "2a Move arrow: the box follows the pointer while the button is held" ([bool]$movedDuring -and -not $mv.steering -and $mv.dragging_held) ("hit {0}; x before {1}, while held {2}; steering {3}; dragging {4}" -f $mv.hit, $mv.before[0], $mv.during[0], $mv.steering, $mv.dragging_held)
Check "2b Move arrow: it stays where the button is let go, along the arrow only" ([bool]$stays -and [bool]$along) ("after release {0}; pointer moved on {1}" -f (@($mv.after) -join ","), (@($mv.later) -join ","))

# ---- 3. Rotate: held drag turns; outside the snap ring the turn is in 5.625-degree steps, close to the ring it is smooth ----
[void](Edit @{ action = "manip"; text = "rotate" })
$readRot = { Rot $A }
$far = HandDrag "ring:z" 30 1 0 $readRot
$report.rotate_far = $far
if ($far.error) { Note $far.error; $far = @{ before = 0.0; during = 0.0; after = 0.0; later = 9.0 } }
$dFar = AngleDiff $far.after $far.before
$stepsFar = $dFar / 5.625
Check "3a Rotate ring: the box turns while the button is held and stays turned after" ([math]::Abs((AngleDiff $far.during $far.before)) -gt 2 -and (Near $far.after $far.later 0.05)) ("before {0}, held {1}, after {2}, pointer moved on {3}" -f $far.before, $far.during, $far.after, $far.later)
Check "3b Rotate ring, pointer well outside the rings: the turn is whole 5.625-degree steps" ([math]::Abs($dFar) -gt 2 -and (Near $stepsFar ([math]::Round($stepsFar)) 0.02)) ("turned {0:N3} degrees = {1:N3} steps" -f $dFar, $stepsFar)
$near = HandDrag "ring:z" 20 0 0 $readRot
$report.rotate_near = $near
if ($near.error) { Note $near.error; $near = @{ before = 0.0; during = 0.0; after = 0.0; later = 9.0 } }
$dNear = AngleDiff $near.after $near.before
$stepsNear = $dNear / 5.625
Check "3c Rotate ring, pointer on the ring: the turn is smooth (not whole steps)" ([math]::Abs($dNear - 20) -lt 2 -and -not (Near $stepsNear ([math]::Round($stepsNear)) 0.02)) ("turned {0:N3} degrees = {1:N3} steps" -f $dNear, $stepsNear)

# ---- 4. Stretch: the top face box follows while held and stays ----
[void](Edit @{ action = "manip"; text = "scale" })
$readSize = { $o = OwnObj $A; if ($o) { [double]$o.sz } }
$s0 = & $readSize
$st4 = HandDrag "face:z+" ($s0 + 0.6) 0 0 $readSize
$report.stretch = $st4
if ($st4.error) { Note $st4.error; $st4 = @{ before = 0.0; during = 0.0; after = 0.0; later = 9.0 } }
Check "4 Stretch face: the box grows while the button is held and keeps the size it was let go at" (($st4.during - $st4.before) -gt 0.2 -and (Near $st4.after $st4.later 0.005) -and ($st4.after - $st4.before) -gt 0.4) ("z size before {0}, held {1}, after {2}, pointer moved on {3}" -f $st4.before, $st4.during, $st4.after, $st4.later)

# ---- 5. a builder's steps: in Stretch the floor clicked, Grid Options (Ctrl+Shift+B), Units 1 and Enter, the window closed, Shift+X ----
$sp = ScreenOf $box.id
if ($sp) { ClickAt $sp[0] $sp[1] }
$selS = EditUntil { param($s) @($s.selection) -contains $A }
Press "B" $true $true
$go = EditUntil { param($s) $s.grid_window_open -and $s.grid_unit_field }
$typed = $false
if ($go.grid_unit_field) {
    ClickAt $go.grid_unit_field[0] $go.grid_unit_field[1]
    Press "A" $true
    Press "1"
    Press "ENTER"
    $typed = $true
}
$gu = EditUntil { param($s) [double]$s.grid.unit -eq 1 }
$gs = Edit @{ action = "state" }
if ($gs.grid_close) { ClickAt $gs.grid_close[0] $gs.grid_close[1] }
$closed = EditUntil { param($s) -not $s.grid_window_open }
$p0 = OwnObj $A
$focusBefore = $closed.focus
Press "X" $false $true
$snap = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x ([math]::Round([double]$o.x)) 0.01) -and (Near $o.y ([math]::Round([double]$o.y)) 0.01)) { $o } } 8
$p1 = OwnObj $A
$report.shift_x = @{ selected = @($selS.selection); unit = $gu.grid.unit; focus = $focusBefore; before = @($p0.x, $p0.y, $p0.z); after = @($p1.x, $p1.y, $p1.z); grid = $closed.grid }
$alreadyWhole = (Near $p0.x ([math]::Round([double]$p0.x)) 0.01) -and (Near $p0.y ([math]::Round([double]$p0.y)) 0.01)
Check "5 Shift+X after Grid Options Units 1 and the window closed: the floor's middle is on a whole metre" ([bool]$snap -and $typed -and [double]$gu.grid.unit -eq 1 -and -not $closed.grid_window_open) ("selected {0}; unit {1}; window closed {2}; keyboard with {3}; before {4:N3},{5:N3} (already whole: {6}); after {7:N3},{8:N3}" -f (@($selS.selection) -join ","), $gu.grid.unit, (-not $closed.grid_window_open), $focusBefore, [double]$p0.x, [double]$p0.y, $alreadyWhole, [double]$p1.x, [double]$p1.y)

# ---- 6. Undo and Redo: move, turn and resize by numbers, Ctrl+Z three times steps back through each, Ctrl+Y steps forward ----
$sp = ScreenOf $box.id
if ($sp) { ClickAt $sp[0] $sp[1] }
[void](EditUntil { param($s) @($s.selection) -contains $A })
Start-Sleep -Seconds 1
$u0 = OwnObj $A; $r0 = Rot $A
$tx = [double]$u0.x + 1.5
[void](Edit @{ action = "position"; x = $tx; y = [double]$u0.y; z = [double]$u0.z })
$u1 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x $tx 0.01)) { $o } } 15
Start-Sleep -Milliseconds 800
$rt = [double]$r0 + 30; if ($rt -gt 180) { $rt -= 360 }
[void](Edit @{ action = "rotation"; x = 0; y = 0; z = $rt })
[void](Wait-Until { if ([math]::Abs((AngleDiff (Rot $A) $rt)) -lt 0.3) { $true } } 15)
$r1 = Rot $A
Start-Sleep -Milliseconds 800
$sx = [double]$u1.sx + 1
[void](Edit @{ action = "size"; x = $sx; y = [double]$u1.sy; z = [double]$u1.sz })
$u2 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sx $sx 0.01)) { $o } } 15
Start-Sleep -Milliseconds 800
NoFocus
$eu = Edit @{ action = "state" }
Press "Z" $true
$z1 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sx $u1.sx 0.01)) { $o } } 8
Press "Z" $true
$z2 = Wait-Until { if ([math]::Abs((AngleDiff (Rot $A) $r0)) -lt 0.3) { OwnObj $A } } 8
Press "Z" $true
$z3 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x $u0.x 0.01)) { $o } } 8
Press "Y" $true
$y1 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x $tx 0.01)) { $o } } 8
$report.undo = @{ can_undo = $eu.can_undo; start = @($u0.x, $r0, $u0.sx); changed = @($tx, $r1, $sx); z1 = $z1; z2 = $z2; z3 = $z3; y1 = $y1 }
Check "6a Ctrl+Z undoes the resize" ([bool]$z1) ("Undo on {0}; size x {1} -> {2}, wanted {3}" -f $eu.can_undo, $sx, (OwnObj $A).sx, $u1.sx)
Check "6b Ctrl+Z again undoes the turn" ([bool]$z2) ("rotation {0} -> {1}, wanted {2}" -f $r1, (Rot $A), $r0)
Check "6c Ctrl+Z a third time undoes the move" ([bool]$z3) ("x {0} -> {1}, wanted {2}" -f $tx, (OwnObj $A).x, $u0.x)
Check "6d Ctrl+Y steps forward again (the move comes back)" ([bool]$z3 -and [bool]$y1) ("x now {0}, wanted {1}" -f (OwnObj $A).x, $tx)

# ---- 7. The right-click menu: Sit Here chosen with the pointer closes the menu; in build mode and out of it ----
function SitByMenu([string]$label) {
    $sp = ScreenOf $box.id
    if (-not $sp) { Check "$label" $false "box not on screen"; return }
    ClickAt $sp[0] $sp[1] "right"
    $open = Wait-Until { $s = Interact @{ action = "state" }; if ($s.menu_open) { $s } } 5
    $entry = Api "ui_sounds" @{ action = "find"; name = "entry_sit" }
    if ($entry.found) { ClickAt ([double]$entry.x) ([double]$entry.y) }
    $sat = Wait-Until { $s = Interact @{ action = "state" }; if ($s.sitting) { $s } } 10
    Start-Sleep -Milliseconds 800
    $after = Interact @{ action = "state" }
    $ed = Edit @{ action = "state" }
    Check $label ([bool]$open -and $entry.found -and [bool]$sat -and -not $after.menu_open) ("menu opened {0}; Sit Here found {1}; sitting {2}; menu open after {3}; build mode {4}, window {5}" -f [bool]$open, $entry.found, [bool]$sat, $after.menu_open, $ed.build_mode, $ed.window_open)
    [void](Interact @{ action = "stand" })
    [void](Wait-Until { $s = Interact @{ action = "state" }; if (-not $s.sitting) { $s } } 10)
    Start-Sleep -Seconds 1
}
NoFocus
SitByMenu "7a in build mode: right-click the floor, Sit Here: you sit and the menu closes"
NoFocus
$bm = Edit @{ action = "state" }
if ($bm.build_mode) { Press "ESC" }
[void](EditUntil { param($s) -not $s.build_mode })
SitByMenu "7b out of build mode: right-click the floor, Sit Here: you sit and the menu closes"

# ---- 8. take back, log out ----
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "8 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "edit-input-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

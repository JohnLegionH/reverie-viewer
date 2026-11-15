# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-texture-live.ps1 - the handles, the Texture tab and copy and paste through the engine's input path, as a hand drives them: every
# pointer press, move and release and every key goes through the automation's input command (Input.ParseInputEvent, as the operating
# system's events arrive); the automation only says where things are on screen. Makes a box, opens build mode (Ctrl+B, Ctrl+3), clicks it, then:
#   - drags the Move arrow and reads, while the button is held, the box and where the handles are drawn: the handles move with the box;
#   - drags the flat Rotate ring out to the side and up past the ring's horizon in steps: the turn grows in 5.625-degree steps, never stalling;
#   - makes two more boxes, chooses Select Face and the Texture tab, clicks one top face, chooses a texture in the picker, Shift-clicks the
#     other top face, sets Mapping to Planar, presses Align: the first face's offsets make the texture run on from the second;
#   - chooses a PBR material in the picker and removes it again, when the inventory or the Library holds one;
#   - on the Object tab presses Copy size on the first box and Paste size on another: it takes that size.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\build-texture-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: build-texture-report.json in the sandbox. Exit: 0 all checks
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
    [string]$MaterialAsset = "",
    [switch]$TakeBackEarlier,   # also take back the account's objects already on the region (left by an earlier run that stopped)   # a PBR material asset the grid holds, used when no material item is in the inventory
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
# boxes earlier runs of this check left on the test region are taken back with this run's own (by id, re-read at the end)
if ($TakeBackEarlier) { foreach ($o in @((Build @{ action = "state" }).own_objects)) { Add-Rezzed ([uint32]$o.local) }; Note ("earlier boxes to take back: {0}" -f $script:rezzed.Count) }
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


# ---- 2. Move: while the arrow is held the handles move with the box ----
[void](Edit @{ action = "manip"; text = "move" })
$readPosAndHandles = { $o = OwnObj $A; $h = (Edit @{ action = "state" }).handles_centre; if ($o -and $h) { @([double]$o.x, [double]$h[0]) } }
$mv = HandDrag "axis:x+" 2 0 0 $readPosAndHandles
$report.move = $mv
if ($mv.error) { Note $mv.error; $mv = @{ before = @(0, 0); during = @(0, 0); after = @(0, 0); later = @(9, 9); dragging_held = $false; hit = "none" } }
$boxMoved = [math]::Abs($mv.during[0] - $mv.before[0])
$handlesMoved = [math]::Abs($mv.during[1] - $mv.before[1])
Check "2a held Move arrow: the box follows the pointer" ($boxMoved -gt 0.4 -and $mv.dragging_held) ("hit {0}; box x before {1:N3}, while held {2:N3}" -f $mv.hit, $mv.before[0], $mv.during[0])
Check "2b held Move arrow: the handles move with the box" ($handlesMoved -gt 0.4 -and [math]::Abs($handlesMoved - $boxMoved) -lt 0.3) ("handles x before {0:N3}, while held {1:N3} (box moved {2:N3}, handles {3:N3})" -f $mv.before[1], $mv.during[1], $boxMoved, $handlesMoved)
Check "2c let go: the handles stand on the box" ([math]::Abs($mv.after[1] - $mv.after[0]) -lt 0.3) ("box x {0:N3}, handles x {1:N3}" -f $mv.after[0], $mv.after[1])

# ---- 3. Rotate: the flat ring dragged well out, past the horizon, keeps turning in 5.625-degree steps ----
[void](Edit @{ action = "manip"; text = "rotate" })
[void](Interact @{ action = "aim"; x = [double]$box.x; y = [double]$box.y; z = [double]$box.z + 0.3; distance = 6 }); Start-Sleep -Milliseconds 1500
$pts = (Edit @{ action = "handle_points"; text = "ring:z"; x = 10; y = 1; z = 0 }).done
$turns = New-Object System.Collections.Generic.List[double]
$stalled = $false
if ($pts -and $pts.from -and $pts.centre) {
    $r0 = Rot $A
    $pa = $pts.from; $c = $pts.centre
    [void](MoveTo $pa[0] $pa[1]); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true $pa[0] $pa[1])
    # out to 320 pixels from the ring's middle, then round it from below through the side to above, past the ring's horizon, in steps;
    # the turn is read while the button is held
    $rad = 320
    $path = @(-80, -60, -40, -20, 0, 20, 40, 60, 80) | ForEach-Object { $th = $_ * [math]::PI / 180; @(($c[0] + $rad * [math]::Cos($th)), ($c[1] - $rad * [math]::Sin($th))) }
    foreach ($q in $path) {
        [void](MoveTo $q[0] $q[1]); Start-Sleep -Milliseconds 700
        $turns.Add((AngleDiff (Rot $A) $r0))
    }
    $last = $path[$path.Count - 1]
    [void](Mouse "left" $false $last[0] $last[1]); Start-Sleep -Milliseconds 1200
    $diffs = @(); for ($i = 1; $i -lt $turns.Count; $i++) { $diffs += (AngleDiff $turns[$i] $turns[$i - 1]) }
    $oneWay = (@($diffs | Where-Object { $_ -lt -0.01 }).Count -eq 0) -or (@($diffs | Where-Object { $_ -gt 0.01 }).Count -eq 0)
    $moved = [math]::Abs(($diffs | Measure-Object -Sum).Sum)
    $stalled = -not $oneWay
    $whole = @($turns | Where-Object { [math]::Abs($_ / 5.625 - [math]::Round($_ / 5.625)) -gt 0.03 }).Count -eq 0
    Check "3 Rotate ring dragged round well out, past the horizon: the turn keeps going one way in 5.625-degree steps" ($oneWay -and $whole -and $moved -gt 60) ("turns while held: {0}; turned {1:N1} in all" -f (($turns | ForEach-Object { "{0:N2}" -f $_ }) -join ", "), $moved)
    $report.ring_far = @{ turns = $turns; centre = $c; from = $pa }
}
else { Check "3 Rotate ring dragged well out" $false "no ring points" }

# ---- 4. the Texture tab: Select Face, a face textured from inventory, Planar, two faces aligned ----
[void](Edit @{ action = "manip"; text = "move" })
$made = @()
[void](Interact @{ action = "aim"; x = $spot.x + 0.7; y = $spot.y + 3; z = $spot.z; distance = 7 }); Start-Sleep -Milliseconds 1500
foreach ($dx in @(0.0, 1.3)) {
    $known = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    $cr = Build @{ action = "create"; x = $spot.x + $dx; y = $spot.y + 3; z = $spot.z }
    Note ("create at +{0}: {1}" -f $dx, ($cr | ConvertTo-Json -Compress -Depth 3))
    $nb = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $known -notcontains $_.id }) | Select-Object -First 1 } 20
    if ($nb) { Add-Rezzed ([uint32]$nb.local); $made += $nb }
}
if ($made.Count -lt 2) { Check "4 two more boxes are made" $false ("made {0}" -f $made.Count) }
$B = [uint32]$made[0].local; $C = [uint32]$made[1].local
Note ("boxes for the Texture tab: {0} and {1}" -f $B, $C)
# making boxes chose Create and its window may have closed: the Build window open again (Ctrl+B), then the Edit tool (Ctrl+3)
$st = Edit @{ action = "state" }
if (-not $st.window_open) { if ($st.build_mode) { Press "B" $true; [void](EditUntil { param($s) -not $s.build_mode }) }; Press "B" $true; [void](EditUntil { param($s) $s.window_open }) }
Press "3" $true
$st = EditUntil { param($s) $s.tool -eq "edit" -and $s.window_open }
Note ("after making the boxes: build mode {0}, window open {1}, tool {2}" -f $st.build_mode, $st.window_open, $st.tool)
# both made 1 m wide and set side by side by number (the typing is not what this part checks)
foreach ($l in @($B, $C)) { [void](Edit @{ action = "select"; text = "$l" }); [void](Edit @{ action = "size"; x = 1; y = 1; z = 0.3 }) }
[void](Edit @{ action = "select"; text = "$C" }); [void](Edit @{ action = "position"; x = [double]$made[0].x + 1.37; y = [double]$made[0].y; z = [double]$made[0].z })
[void](Wait-Until { $o = OwnObj $C; if ($o -and (Near $o.sx 1 0.01) -and (Near $o.x ([double]$made[0].x + 1.37) 0.02)) { $o } } 15)
[void](Interact @{ action = "aim"; x = [double]$made[0].x + 0.7; y = [double]$made[0].y; z = [double]$made[0].z; distance = 5 }); Start-Sleep -Milliseconds 1500
$oB = OwnObj $B; $oC = OwnObj $C
function TopOf($o) { (Edit @{ action = "screen"; x = [double]$o.x; y = [double]$o.y; z = [double]$o.z + [double]$o.sz / 2 }).done }

$p = (Edit @{ action = "control"; text = "manip_face" }).done
if ($p) { ClickAt $p[0] $p[1] }
$t = (Edit @{ action = "control"; text = "tab:texture" }).done
if ($t) { ClickAt $t[0] $t[1] }
$sB = TopOf $oB
if ($sB) { ClickAt $sB[0] $sB[1] }
$st = Edit @{ action = "state" }
Note ("Select Face at {0}, Texture tab at {1}, top of the first box at {2}; tool {3}, select faces {4}, selection {5}" -f (@($p) -join ","), (@($t) -join ","), (@($sB) -join ","), $st.tool, $st.select_faces, (@($st.selection) -join ","))
$f1 = EditUntil { param($s) $s.select_faces -and @($s.faces).Count -eq 1 -and [uint32]$s.faces[0].local -eq $B }
Check "4a Select Face, Texture tab, a click on the box's top selects that face alone" ($f1.select_faces -and @($f1.faces).Count -eq 1 -and [uint32]$f1.faces[0].local -eq $B -and [int]$f1.faces[0].face -eq 0) ("faces {0}" -f (($f1.faces | ForEach-Object { "$($_.local):$($_.face)" }) -join ","))

# a texture from inventory or the Library, chosen in the picker with the pointer
$before = $f1.faces[0].settings.texture
$ch = (Edit @{ action = "control"; text = "texture_choose" }).done
if ($ch) { ClickAt $ch[0] $ch[1] }
$rows = Wait-Until { $n = (Edit @{ action = "picker" }).done; if ($n -and @($n).Count -gt 0) { $n } } 20
$pickedName = $null
if ($rows) {
    $row = (Edit @{ action = "picker_row"; text = "0" }).done
    if ($row) { ClickAt $row[0] $row[1]; $pickedName = @($rows)[0] }
    $use = (Edit @{ action = "control"; text = "pick_use" }).done
    if ($use) { ClickAt $use[0] $use[1] }
}
$f2 = EditUntil { param($s) @($s.faces).Count -eq 1 -and $s.faces[0].settings.texture -ne $before }
Check "4b a texture chosen from the picker is put on that face" ($pickedName -and $f2.faces[0].settings.texture -ne $before) ("picked '{0}' of {1}; face texture {2} -> {3}" -f $pickedName, @($rows).Count, $before, $f2.faces[0].settings.texture)
Shot "texture-face"

# Shift-click the other box's top, then Mapping: Planar for both, then Align
$sC = TopOf $oC
if ($sC) { [void](MoveTo $sC[0] $sC[1]); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $sC[0] $sC[1] $true); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $sC[0] $sC[1] $true); Start-Sleep -Milliseconds 300 }
$f3 = EditUntil { param($s) @($s.faces).Count -eq 2 }
Check "4c Shift-click the second box's top: both faces are selected, the second last" (@($f3.faces).Count -eq 2 -and [uint32]$f3.faces[1].local -eq $C) ("faces {0}" -f (($f3.faces | ForEach-Object { "$($_.local):$($_.face)" }) -join ","))
$m = (Edit @{ action = "control"; text = "mapping" }).done
if ($m) { ClickAt $m[0] $m[1]; Start-Sleep -Milliseconds 300; Press "DOWN"; Press "ENTER" }
$f4 = EditUntil { param($s) @($s.faces).Count -eq 2 -and $s.faces[0].settings.planar -and $s.faces[1].settings.planar }
Check "4d Mapping set to Planar with the pointer and keys: both faces planar" ([bool]($f4.faces[0].settings.planar -and $f4.faces[1].settings.planar)) ("planar {0}, {1}" -f $f4.faces[0].settings.planar, $f4.faces[1].settings.planar)
# Align: the faces line up with the last selected one
NoFocus
$al = (Edit @{ action = "control"; text = "align" }).done
$beforeAlign = (Edit @{ action = "state" }).faces
if ($al) { ClickAt $al[0] $al[1] }
$f5 = EditUntil { param($s) @($s.faces).Count -eq 2 -and ([double]$s.faces[0].settings.offset[0] -ne [double]$beforeAlign[0].settings.offset[0] -or [double]$s.faces[0].settings.offset[1] -ne [double]$beforeAlign[0].settings.offset[1]) }
if (@($f5.faces).Count -eq 2 -and @($beforeAlign).Count -eq 2) {
    $oB = OwnObj $B; $oC = OwnObj $C
    # the top faces' planar mapping: with the anchor's repeats and no turn, B's offset must be the anchor's less twice the distance (whole repeats aside)
    $repU = [double]$f5.faces[1].settings.repeats[0]; $repV = [double]$f5.faces[1].settings.repeats[1]
    $wantU = [double]$f5.faces[1].settings.offset[0] - 2 * $repU * ([double]$oC.x - [double]$oB.x)
    $wantV = [double]$f5.faces[1].settings.offset[1] - 2 * $repV * ([double]$oC.y - [double]$oB.y)
    $gotU = [double]$f5.faces[0].settings.offset[0]; $gotV = [double]$f5.faces[0].settings.offset[1]
    function Whole([double]$d) { [math]::Abs($d - [math]::Round($d)) -lt 0.01 }
    Check "4e Align: the first face's texture runs on from the last selected face" ((Whole ($gotU - $wantU)) -and (Whole ($gotV - $wantV)) -and -not ((Whole $wantU) -and (Near $gotU [double]$beforeAlign[0].settings.offset[0] 0.001))) ("B offset {0:N4},{1:N4}; wanted {2:N4},{3:N4} (mod 1); before {4},{5}" -f $gotU, $gotV, $wantU, $wantV, $beforeAlign[0].settings.offset[0], $beforeAlign[0].settings.offset[1])
}
else { Check "4e Align: the first face's texture runs on from the last selected face" $false ("faces {0}" -f @($f5.faces).Count) }
Shot "texture-aligned"

# ---- 5. PBR material: from the picker, if the inventory holds one ----
$mc = (Edit @{ action = "control"; text = "material_choose" }).done
if ($mc) { ClickAt $mc[0] $mc[1] }
$mrows = Wait-Until { $n = (Edit @{ action = "picker" }).done; if ($n -and @($n).Count -gt 0) { $n } } 15
if ($mrows) {
    $row = (Edit @{ action = "picker_row"; text = "0" }).done
    if ($row) { ClickAt $row[0] $row[1] }
    $use = (Edit @{ action = "control"; text = "pick_use" }).done
    if ($use) { ClickAt $use[0] $use[1] }
    $f6 = Wait-Until { $s = Edit @{ action = "state" }; if (@($s.faces).Count -eq 2 -and $s.faces[0].settings.material -ne "00000000-0000-0000-0000-000000000000") { $s } } 20
    Check "5a a PBR material chosen in the picker is put on both selected faces" ([bool]$f6) ("material {0}" -f $(if ($f6) { $f6.faces[0].settings.material } else { "none" }))
    $rm = (Edit @{ action = "control"; text = "material_remove" }).done
    if ($rm) { ClickAt $rm[0] $rm[1] }
    $f7 = Wait-Until { $s = Edit @{ action = "state" }; if (@($s.faces).Count -eq 2 -and $s.faces[0].settings.material -eq "00000000-0000-0000-0000-000000000000") { $s } } 20
    Check "5b Remove takes it off again" ([bool]$f7) ""
}
else {
    [void](In @{ kind = "key"; key = "ESCAPE"; down = $true; shift = $false; alt = $false; ctrl = $false }); [void](In @{ kind = "key"; key = "ESCAPE"; down = $false; shift = $false; alt = $false; ctrl = $false })
    $report.pbr = "no material item in the inventory or the Library: the material is put on by its asset id through the automation, not by hand"
    Note $report.pbr
    if ($MaterialAsset) {
        [void](Edit @{ action = "material"; text = $MaterialAsset })
        $f6 = Wait-Until { $s = Edit @{ action = "state" }; if (@($s.faces).Count -eq 2 -and [string]$s.faces[0].settings.material -eq $MaterialAsset -and [string]$s.faces[1].settings.material -eq $MaterialAsset) { $s } } 20
        Check "5a (by asset id, not by hand) the region takes a PBR material on both selected faces" ([bool]$f6) ("material {0}" -f $MaterialAsset)
        [void](Edit @{ action = "material" })
        $f7 = Wait-Until { $s = Edit @{ action = "state" }; if (@($s.faces).Count -eq 2 -and [string]$s.faces[0].settings.material -eq "00000000-0000-0000-0000-000000000000") { $s } } 20
        Check "5b (by asset id, not by hand) and takes it off again" ([bool]$f7) ""
    }
}

# ---- 6. copy a box's size and paste it onto another ----
$p = (Edit @{ action = "control"; text = "manip_move" }).done
if ($p) { ClickAt $p[0] $p[1] }
$t = (Edit @{ action = "control"; text = "tab:object" }).done
if ($t) { ClickAt $t[0] $t[1] }
$sp = ScreenOf $box.id
if ($sp) { ClickAt $sp[0] $sp[1] }
[void](EditUntil { param($s) @($s.selection) -contains $A -and -not $s.select_faces })
$sizeA = @((OwnObj $A).sx, (OwnObj $A).sy, (OwnObj $A).sz)
$cs = (Edit @{ action = "control"; text = "copy_size" }).done
if ($cs) { ClickAt $cs[0] $cs[1] }
$sC = TopOf (OwnObj $C)
if ($sC) { ClickAt $sC[0] $sC[1] }
[void](EditUntil { param($s) @($s.selection) -contains $C })
$ps = (Edit @{ action = "control"; text = "paste_size" }).done
if ($ps) { ClickAt $ps[0] $ps[1] }
$oC2 = Wait-Until { $o = OwnObj $C; if ($o -and (Near $o.sx $sizeA[0] 0.01) -and (Near $o.sy $sizeA[1] 0.01) -and (Near $o.sz $sizeA[2] 0.01)) { $o } } 15
Check "6 Copy size on one box, Paste size on another: it takes that size" ([bool]$oC2) ("box A {0}; the other now {1}" -f ($sizeA -join ","), $(if ($oC2) { "$($oC2.sx),$($oC2.sy),$($oC2.sz)" } else { $o = OwnObj $C; "$($o.sx),$($o.sy),$($o.sz)" }))
NoFocus
$bm = Edit @{ action = "state" }
if ($bm.build_mode) { Press "ESC" }

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
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-texture-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

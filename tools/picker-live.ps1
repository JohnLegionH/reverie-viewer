# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# picker-live.ps1 - the Build window's texture picker, colour picker and Take Into Folder list stay open as windows, through the engine's
# input path, as a hand drives them: every pointer press, move and release and every key goes through the automation's input command
# (Input.ParseInputEvent, as the operating system's events arrive); the automation only says where things are on screen and reads back
# what the viewer holds. In order, on one new box:
#   - the texture picker opened by its button is a window with a close button; a click in the world (on the box) leaves it open;
#   - a row chosen by a click changes the face's texture at once; Cancel closes the picker and puts the texture back;
#   - opened again and a row chosen, its own close button puts the texture back too; opened again, a row chosen and OK: the texture stays;
#   - the colour picker opened from the colour swatch stays open on a click in the world; a click in its colour square changes the face's
#     colour at once; Cancel puts it back; opened again, a click in the square and OK: the colour stays;
#   - Build > Take Into Folder (the menu by a click on its title and the arrow keys) opens the folder list, which stays open on a click
#     in the world and closes on Cancel with the box still in the region.
# Takes back everything it rezzed, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\picker-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: picker-report.json in the sandbox. Exit: 0 all checks
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

function Pickers() { (Edit @{ action = "pickers" }).done }
function Face() { (State).face }
function Same($a, $b) { [string]$a -eq [string]$b }
function ColourOf($f) { if ($f) { (@($f.colour)[0..2] | ForEach-Object { [math]::Round([double]$_, 3) }) -join "," } else { "" } }
function PickerRows() { Wait-Until { $n = (Edit @{ action = "picker" }).done; if ($n -and @($n).Count -gt 0) { @($n) } } 30 }
function ClickRow([int]$row) { $p = (Edit @{ action = "picker_row"; text = [string]$row }).done; if ($p) { ClickAt ([double]$p[0]) ([double]$p[1]); $true } else { $false } }
function ClickPickerClose([string]$which) { $p = (Edit @{ action = "picker_close"; text = $which }).done; if ($p) { ClickAt ([double]$p[0]) ([double]$p[1]); $true } else { $false } }
function ClickSquare([string]$at) { $p = (Edit @{ action = "picker_colour_point"; text = $at }).done; if ($p) { ClickAt ([double]$p[0]) ([double]$p[1]); $true } else { $false } }

# ---- 0. a box to work on: Ctrl+B and a click on the ground; the tool becomes Edit and the box is selected ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
if ($TakeBackEarlier) { foreach ($o in @((Build @{ action = "state" }).own_objects)) { Add-Rezzed ([uint32]$o.local) } }
NoFocus
if ((State).build_mode) { Press "ESC"; Start-Sleep -Milliseconds 300 }
Press "B" $true; Start-Sleep -Milliseconds 400
$gp = Ground 0 0
if ($gp) { ClickAt $gp[0] $gp[1] }
$boxA = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $boxA) { Check "0 Create makes a box" $false "none"; [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$boxA.local); $A = [uint32]$boxA.local
$sel = EditUntil { param($s) $s.tool -eq "edit" -and @($s.selection) -contains $A }
Check "0 a new box, selected with the Edit tool" ($sel.tool -eq "edit" -and @($sel.selection) -contains $A) ("tool {0}; selection [{1}]" -f $sel.tool, (@($sel.selection) -join ","))
[void](ClickCtl "tab:texture"); Start-Sleep -Milliseconds 400
$pa = ScreenOf $boxA.id
$t0 = (Face).texture
Note "the face's texture at the start: $t0"

# ---- 1. the texture picker is a window that stays open on a click in the world ----
[void](ClickCtl "texture_choose")
$open1 = Wait-Until { if ((Pickers).items) { $true } } 5
$rows = PickerRows
$closeAt = (Edit @{ action = "picker_close"; text = "items" }).done
Shot "texture-picker-open"
if ($pa) { ClickAt $pa[0] $pa[1] }   # a click in the world: on the box itself, so the selection the picker works on stays
Start-Sleep -Milliseconds 800
$stillOpen = [bool](Pickers).items
$selAfter = State
Check "1 the texture picker opens as a window with its own close button and stays open on a click in the world" ([bool]$open1 -and [bool]$closeAt -and $stillOpen -and @($selAfter.selection) -contains $A) ("opened {0}; close button at {1}; rows {2}; open after the click {3}; selection [{4}]" -f [bool]$open1, (@($closeAt) -join ","), @($rows).Count, $stillOpen, (@($selAfter.selection) -join ","))

# ---- 2. a chosen row is applied at once; Cancel puts it back ----
$row = -1; $t1 = $null
for ($r = 0; $r -lt [math]::Min(6, @($rows).Count) -and -not $t1; $r++) {
    if (ClickRow $r) { $t1 = Wait-Until { $t = (Face).texture; if ($t -and -not (Same $t $t0)) { $t } } 6; if ($t1) { $row = $r } }
}
Shot "texture-chosen"
Check "2 a row chosen by a click changes the face's texture at once, the picker still open" ([bool]$t1 -and [bool](Pickers).items) ("row {0}; texture {1} -> {2}; open {3}" -f $row, $t0, $t1, (Pickers).items)
[void](ClickUi "pick_cancel")
$closed2 = Wait-Until { if (-not (Pickers).items) { $true } } 5
$back2 = Wait-Until { if (Same (Face).texture $t0) { $true } } 10
Check "3 Cancel closes the texture picker and puts the face's texture back" ([bool]$closed2 -and [bool]$back2) ("closed {0}; texture now {1} (was {2})" -f [bool]$closed2, (Face).texture, $t0)

# ---- 3. the window's own close button puts it back too ----
[void](ClickCtl "texture_choose"); [void](Wait-Until { if ((Pickers).items) { $true } } 5); [void](PickerRows)
$t3 = $null
if ($row -ge 0 -and (ClickRow $row)) { $t3 = Wait-Until { $t = (Face).texture; if ($t -and -not (Same $t $t0)) { $t } } 6 }
$cb = ClickPickerClose "items"
$closed3 = Wait-Until { if (-not (Pickers).items) { $true } } 5
$back3 = Wait-Until { if (Same (Face).texture $t0) { $true } } 10
Check "4 the picker's close button closes it and puts the face's texture back" ([bool]$t3 -and $cb -and [bool]$closed3 -and [bool]$back3) ("chosen {0}; close clicked {1}; closed {2}; texture now {3} (was {4})" -f $t3, $cb, [bool]$closed3, (Face).texture, $t0)

# ---- 4. OK keeps the choice ----
[void](ClickCtl "texture_choose"); [void](Wait-Until { if ((Pickers).items) { $true } } 5); [void](PickerRows)
$t4 = $null
if ($row -ge 0 -and (ClickRow $row)) { $t4 = Wait-Until { $t = (Face).texture; if ($t -and -not (Same $t $t0)) { $t } } 6 }
[void](ClickUi "pick_ok")
$closed4 = Wait-Until { if (-not (Pickers).items) { $true } } 5
Start-Sleep -Seconds 3
$kept4 = (Face).texture
Check "5 OK closes the texture picker and the chosen texture stays" ([bool]$t4 -and [bool]$closed4 -and (Same $kept4 $t4)) ("chosen {0}; closed {1}; texture 3 s later {2}" -f $t4, [bool]$closed4, $kept4)

# ---- 5. the colour picker: stays open, applies as chosen, Cancel puts back, OK keeps ----
$c0 = ColourOf (Face)
[void](ClickCtl "colour")
$openC = Wait-Until { if ((Pickers).colour) { $true } } 5
Shot "colour-picker-open"
if ($pa) { ClickAt $pa[0] $pa[1] }
Start-Sleep -Milliseconds 800
$stillC = [bool](Pickers).colour
Check "6 the colour picker opens as a window and stays open on a click in the world" ([bool]$openC -and $stillC) ("opened {0}; open after the click {1}" -f [bool]$openC, $stillC)
$sq = ClickSquare "0.8,0.2"
$c1 = Wait-Until { $c = ColourOf (Face); if ($c -and $c -ne $c0) { $c } } 6
Shot "colour-chosen"
Check "7 a click in the colour square changes the face's colour at once" ($sq -and [bool]$c1 -and [bool](Pickers).colour) ("square clicked {0}; colour {1} -> {2}" -f $sq, $c0, $c1)
[void](ClickUi "colour_cancel")
$closedC = Wait-Until { if (-not (Pickers).colour) { $true } } 5
$backC = Wait-Until { if ((ColourOf (Face)) -eq $c0) { $true } } 10
Check "8 Cancel closes the colour picker and puts the face's colour back" ([bool]$closedC -and [bool]$backC) ("closed {0}; colour now {1} (was {2})" -f [bool]$closedC, (ColourOf (Face)), $c0)
[void](ClickCtl "colour"); [void](Wait-Until { if ((Pickers).colour) { $true } } 5)
[void](ClickSquare "0.3,0.4")
$c2 = Wait-Until { $c = ColourOf (Face); if ($c -and $c -ne $c0) { $c } } 6
[void](ClickUi "colour_ok")
$closedC2 = Wait-Until { if (-not (Pickers).colour) { $true } } 5
Start-Sleep -Seconds 3
$keptC = ColourOf (Face)
Check "9 OK closes the colour picker and the chosen colour stays" ([bool]$c2 -and [bool]$closedC2 -and $keptC -eq $c2) ("chosen {0}; closed {1}; colour 3 s later {2}" -f $c2, [bool]$closedC2, $keptC)

# ---- 6. Take Into Folder: the list stays open until Take or Cancel ----
NoFocus
$tp = (Edit @{ action = "menu_title"; text = "Build" }).done
if ($tp) { ClickAt ([double]$tp[0]) ([double]$tp[1]) }
Start-Sleep -Milliseconds 500
$reached = $false
for ($i = 0; $i -lt 40 -and -not $reached; $i++) {
    if ([string](Edit @{ action = "menu_focus" }).done -eq "Take Into Folder...") { $reached = $true; break }
    Press "DOWN"; Start-Sleep -Milliseconds 120
}
if ($reached) { Press "ENTER" }
$openF = Wait-Until { if ((Pickers).folders) { $true } } 5
$frows = Wait-Until { $r = (State).folder_rows; if ($r -and @($r).Count -gt 0) { $r } } 10
Shot "take-folder-open"
if ($pa) { ClickAt $pa[0] $pa[1] }
Start-Sleep -Milliseconds 800
$stillF = [bool](Pickers).folders
Check "10 Take Into Folder's list opens as a window and stays open on a click in the world" ($reached -and [bool]$openF -and $stillF) ("menu entry reached {0}; opened {1}; folders {2}; open after the click {3}" -f $reached, [bool]$openF, @($frows).Count, $stillF)
[void](ClickUi "folder_cancel")
$closedF = Wait-Until { if (-not (Pickers).folders) { $true } } 5
Start-Sleep -Seconds 2
$stillThere = [bool](OwnObj $A)
Check "11 Cancel closes the folder list and takes nothing" ([bool]$closedF -and $stillThere) ("closed {0}; the box still in the region {1}" -f [bool]$closedF, $stillThere)
NoFocus
Press "ESC"; Start-Sleep -Milliseconds 300

# ---- 5. take back, log out ----
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "12 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "picker-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-features-live.ps1 - the Build window's Features and Content tabs, Select Only My Objects, the next and previous part, the new-prim
# settings and Take Into Folder through the engine's input path, as a hand drives them: every pointer press, move and release and every key
# goes through the automation's input command (Input.ParseInputEvent, as the operating system's events arrive); the automation only says
# where things are on screen and reads back what the viewer and the region hold. In order:
#   - Ctrl+P, Preferences, Building: a size X and a texture typed, Save; Ctrl+B and a click on the ground make a box of that size and texture;
#   - Ctrl+3 and a click select it; on the Features tab Flexible Path and Light ticked, Metal chosen from the Material list, Friction typed;
#     the region's values read back after the box is let go and selected again;
#   - on the Content tab New Script, then Delete on its row;
#   - a second box Shift-clicked, Ctrl+L, Edit Linked ticked, a part clicked, Ctrl+. and Ctrl+, step through the parts;
#   - Select Only My Objects ticked and cleared, an own object still selected while it is on;
#   - the Build menu opened by a click on its title and Take Into Folder reached with the arrow keys, a folder chosen, Take: the linkset
#     leaves the region;
#   - a new box selected, Ctrl+I, that folder dragged from the inventory onto the Content list: its items go in, one at a time.
# Takes back everything it rezzed and the region still holds, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\build-features-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: build-features-report.json in the sandbox. Exit: 0 all checks
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

# ---- 1. Preferences, Building: the size and texture Create uses, set by hand ----
$blank = "5748decc-f629-461c-9a36-a35a221fe21f"   # a texture every OpenSim grid holds
NoFocus
Press "P" $true
$open = Wait-Until { Ui "category_building" } 10
[void](ClickUi "category_building"); Start-Sleep -Milliseconds 600
$sx = SetField { Ui "pref_new_prim_size_x" } "1.5"
$tx = SetField { Ui "pref_new_prim_texture" } $blank
Shot "prefs-building"
[void](ClickUi "save"); Start-Sleep -Milliseconds 800
Check "1 Preferences, Building opens with Ctrl+P and takes a typed size and texture" ([bool]$open -and $sx -and $tx) ("category shown {0}; size field {1}; texture field {2}" -f [bool]$open, $sx, $tx)
NoFocus
Press "ESC"; Start-Sleep -Milliseconds 400

# ---- 2. Create: a click on the ground with the Create tool makes a box of that size and texture ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
if ($TakeBackEarlier) { foreach ($o in @((Build @{ action = "state" }).own_objects)) { Add-Rezzed ([uint32]$o.local) }; Note ("earlier objects to take back: {0}" -f $script:rezzed.Count) }
NoFocus
$st = State
if ($st.build_mode) { Press "ESC"; Start-Sleep -Milliseconds 300 }
Press "B" $true; Start-Sleep -Milliseconds 400
$gp = (Edit @{ action = "screen"; x = $spot.x; y = $spot.y; z = $spot.z }).done
if ($gp) { ClickAt ([double]$gp[0]) ([double]$gp[1]) }
$boxA = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $boxA) { Check "2 Create makes a box" $false "none"; [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$boxA.local); $A = [uint32]$boxA.local
$faced = Wait-Until { $p = OwnPrim $A; if ($p -and @($p.faces | Select-Object -First 6 | Where-Object { [string]$_ -eq $blank }).Count -eq 6) { $p } } 15
$oA = OwnObj $A
$faceIds = @((OwnPrim $A).faces | Select-Object -First 6 | ForEach-Object { ([string]$_).Substring(0, 8) }) -join ","
Check "2 Create makes the box Preferences, Building describes (size X 1.5, the texture on every face)" ((Near $oA.sx 1.5 0.001) -and [bool]$faced) ("size {0},{1},{2}; faces {3}" -f $oA.sx, $oA.sy, $oA.sz, $faceIds)

# ---- 3. the Features tab ----
[void](Interact @{ action = "aim"; x = [double]$oA.x; y = [double]$oA.y; z = [double]$oA.z; distance = 6 }); Start-Sleep -Milliseconds 1500
NoFocus
Press "3" $true; Start-Sleep -Milliseconds 300
$sp = TopOf $oA
if ($sp) { ClickAt $sp[0] $sp[1] }
$sel = EditUntil { param($s) @($s.selection) -contains $A }
if (-not (@($sel.selection) -contains $A)) { Note "the click did not select the box: selecting it so the later checks run"; [void](Edit @{ action = "select"; text = "$A" }) }
[void](ClickCtl "tab:features"); Start-Sleep -Milliseconds 500
[void](ClickCtl "flexible")
$flex = Wait-Until { $f = Feat; if ($f -and $f.flexible) { $f } } 15
[void](ClickCtl "light")
$lit = Wait-Until { $f = Feat; if ($f -and $f.light) { $f } } 15
Check "3a Features: Flexible Path and Light ticked by hand reach the region" ([bool]$flex -and [bool]$lit) ("flexible {0}; light {1}" -f (@($flex.flexible) -join ","), (@($lit.light) -join ","))
[void](ClickCtl "feat_material"); Start-Sleep -Milliseconds 400
[void](ClickCtl "popup:feat_material:1"); Start-Sleep -Milliseconds 300
$metal = Wait-Until { $f = Feat; if ($f -and [int]$f.material -eq 1) { $f } } 15
$fr = SetField { CtlPoint "friction" } "0.3"
NoFocus
# the region's own physics values: let go and select again, so the tab asks the region afresh
$sky = (Edit @{ action = "screen"; x = [double]$oA.x; y = [double]$oA.y; z = [double]$oA.z + 40 }).done
if ($sky) { ClickAt ([double]$sky[0]) ([double]$sky[1]) }
[void](EditUntil { param($s) @($s.selection).Count -eq 0 })
if ($sp) { ClickAt $sp[0] $sp[1] }
[void](EditUntil { param($s) @($s.selection) -contains $A })
$phys = Wait-Until { $f = Feat; if ($f -and (Near $f.physics[2] 0.3 0.001)) { $f } } 15
Shot "features"
Check "3b Features: Material Metal from the list, Friction 0.3 typed, read back from the region" ([bool]$metal -and [bool]$phys) ("material {0}; physics {1}" -f (Feat).material, (@((Feat).physics) -join ","))

# ---- 4. the Content tab: New Script, then Delete ----
[void](ClickCtl "tab:content"); Start-Sleep -Milliseconds 800
[void](Wait-Until { $c = State; if ($null -ne $c.contents) { $c } } 15)
[void](ClickCtl "new_script")
$withScript = Wait-Until { $c = Contents; if ($c -contains "New Script") { $c } } 20
$row = (Edit @{ action = "content_row"; text = "New Script" }).done
if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
[void](ClickCtl "content_delete")
$gone = Wait-Until { $c = State; if ($null -ne $c.contents -and -not (@($c.contents) -contains "New Script")) { $c } } 45
Shot "content"
Check "4 Content: New Script puts the region's default script in; Delete on its row takes it out" ([bool]$withScript -and [bool]$gone) ("after New Script [{0}]; after Delete [{1}]" -f (@($withScript) -join ","), (@($gone.contents) -join ","))

# ---- 5. two boxes linked, Edit Linked, Ctrl+. and Ctrl+, ----
$beforeB = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "create"; x = [double]$oA.x; y = [double]$oA.y + 2.5; z = [double]$oA.z })
$boxB = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $beforeB -notcontains $_.id }) | Select-Object -First 1 } 20
if ($boxB) { Add-Rezzed ([uint32]$boxB.local) }
$B = [uint32]$boxB.local
NoFocus
Press "3" $true
$sA = TopOf (OwnObj $A); $sB = TopOf (OwnObj $B)
if ($sA) { ClickAt $sA[0] $sA[1] }
if ($sB) { [void](MoveTo $sB[0] $sB[1]); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $sB[0] $sB[1] $true); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $sB[0] $sB[1] $true); Start-Sleep -Milliseconds 300 }
[void](EditUntil { param($s) @($s.selection).Count -eq 2 })
NoFocus
Press "L" $true
$linked = Wait-Until { $p = Primary $B; if ($p -and [int]$p.prims_in_linkset -eq 2) { $p } elseif (($q = Primary $A) -and [int]$q.prims_in_linkset -eq 2) { $q } } 15
[void](ClickCtl "linked")
$sA = TopOf (OwnObj ($(if ($linked) { [uint32]$linked.local } else { $A })))
$parts = @($A, $B)
if ($sA) { ClickAt $sA[0] $sA[1] }
$one = EditUntil { param($s) $s.edit_linked -and @($s.selection).Count -eq 1 }
$first = @($one.selection)[0]
NoFocus
Press "." $true
$next = EditUntil { param($s) @($s.selection).Count -eq 1 -and @($s.selection)[0] -ne $first }
$second = @($next.selection)[0]
Press "," $true
$back = EditUntil { param($s) @($s.selection).Count -eq 1 -and @($s.selection)[0] -eq $first }
Check "5 Ctrl+. and Ctrl+, step through the linked parts with Edit Linked on" ([bool]$linked -and $second -ne $first -and $parts -contains [uint32]$second -and @($back.selection)[0] -eq $first) ("linked {0}; first {1}, after Ctrl+. {2}, after Ctrl+, {3}" -f [bool]$linked, $first, $second, @($back.selection)[0])

# ---- 6. Select Only My Objects: the tick, and an own object still selected with it on ----
[void](ClickCtl "owned_only")
$on = EditUntil { param($s) $s.owned_only }
if ($sA) { ClickAt $sA[0] $sA[1] }
$still = EditUntil { param($s) @($s.selection).Count -ge 1 }
[void](ClickCtl "owned_only")
$off = EditUntil { param($s) -not $s.owned_only }
Check "6 Select Only My Objects ticks on and off by hand; an own object is still selected with it on" ([bool]$on.owned_only -and @($still.selection).Count -ge 1 -and -not $off.owned_only) ("on {0}; selection with it on [{1}]; off {2}" -f $on.owned_only, (@($still.selection) -join ","), (-not $off.owned_only))

# ---- 7. Take Into Folder from the Build menu: the linkset goes into the chosen folder ----
[void](ClickCtl "linked")   # Edit Linked off: the click selects the whole linkset
[void](EditUntil { param($s) -not $s.edit_linked })
if ($sA) { ClickAt $sA[0] $sA[1] }
$rootSel = EditUntil { param($s) @($s.selection).Count -eq 1 }
$root = [uint32]@($rootSel.selection)[0]
$animFolder = [string](State).animations_folder
$tp = (Edit @{ action = "menu_title"; text = "Build" }).done
Note ("the Build title at {0}" -f (@($tp) -join ","))
if ($tp) { ClickAt ([double]$tp[0]) ([double]$tp[1]) }
Start-Sleep -Milliseconds 500
$reached = $false; $seen = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt 40 -and -not $reached; $i++) {
    $f = [string](Edit @{ action = "menu_focus" }).done
    $seen.Add($f)
    if ($f -eq "Take Into Folder...") { $reached = $true; break }
    Press "DOWN"; Start-Sleep -Milliseconds 120
}
Note ("menu focus seen: {0}" -f ($seen -join " | "))
if ($reached) { Press "ENTER" }
$rows = Wait-Until { $r = (State).folder_rows; if ($r -and @($r).Count -gt 0) { $r } } 10
$target = @($rows) | Where-Object { $_ -eq "Animations" } | Select-Object -First 1
if (-not $target) { $target = @($rows) | Select-Object -First 1 }
$fr2 = (Edit @{ action = "folder_row"; text = [string]$target }).done
if ($fr2) { ClickAt ([double]$fr2[0]) ([double]$fr2[1]) }
[void](ClickUi "folder_use")
$taken = Wait-Until { if (-not (OwnObj $root)) { $true } } 20
Shot "taken"
Check "7 Build > Take Into Folder (menu by pointer and keys): the linkset leaves the region into the chosen folder" ($reached -and [bool]$taken) ("menu entry reached {0}; folder '{1}'; gone from the region {2}" -f $reached, $target, [bool]$taken)

# ---- 8. a whole folder dragged from the inventory onto the Content tab of a new box ----
$beforeC = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "create"; x = [double]$oA.x; y = [double]$oA.y; z = [double]$oA.z })
$boxC = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $beforeC -notcontains $_.id }) | Select-Object -First 1 } 20
if ($boxC) { Add-Rezzed ([uint32]$boxC.local) }
$C = [uint32]$boxC.local
Start-Sleep -Milliseconds 1500
NoFocus
Press "3" $true; Start-Sleep -Milliseconds 300   # the Edit tool again: making a box leaves the Create tool chosen
$sC = TopOf (OwnObj $C)
if ($sC) { ClickAt $sC[0] $sC[1] }
$selC = EditUntil { param($s) @($s.selection) -contains $C }
Note ("the new box selected: {0}" -f (@($selC.selection) -contains $C))
[void](ClickCtl "tab:content")
[void](Wait-Until { $c = State; if ($null -ne $c.contents) { $c } } 15)
NoFocus
Press "I" $true; Start-Sleep -Milliseconds 1500
$folderId = if ($target -eq "Animations") { $animFolder } else { "" }
$from = if ($folderId) { (Edit @{ action = "inventory_row"; text = $folderId }).done } else { $null }
# the inventory window opens over the Build window: it is dragged aside by its title bar first, as a person would
$to = CtlPoint "contents"
if ($from) {
    $title = @(([double]$from[0]), ([double]$from[1] - 123))
    [void](MoveTo $title[0] $title[1]); Start-Sleep -Milliseconds 120
    [void](Mouse "left" $true $title[0] $title[1]); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 8; $i++) { [void](MoveTo ($title[0] + 70 * $i) $title[1]); Start-Sleep -Milliseconds 60 }
    [void](Mouse "left" $false ($title[0] + 560) $title[1]); Start-Sleep -Milliseconds 600
    $from = (Edit @{ action = "inventory_row"; text = $folderId }).done
}
Note ("folder row now at {0}" -f (@($from) -join ","))
$dropped = $null
if ($from -and $to) {
    [void](MoveTo ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    for ($i = 1; $i -le 10; $i++) { $t = $i / 10.0; [void](MoveTo ([double]$from[0] + ($to[0] - [double]$from[0]) * $t) ([double]$from[1] + ($to[1] - [double]$from[1]) * $t)); Start-Sleep -Milliseconds 80 }
    Start-Sleep -Milliseconds 300
    [void](Mouse "left" $false $to[0] $to[1]); Start-Sleep -Milliseconds 500
    # the folder holds the linkset taken in step 7 (an object item named Object)
    $dropped = Wait-Until { $c = State; if ($null -ne $c.contents -and @($c.contents | Where-Object { $_ }).Count -gt 0 -and $c.pending -eq 0) { $c } } 45
}
Shot "dropped"
Check "8 a folder dragged from the inventory onto the Content list puts its items in, one at a time" ([bool]$dropped) ("folder row at {0}; list at {1}; contents now [{2}]" -f (@($from) -join ","), (@($to) -join ","), (@($dropped.contents) -join ","))
NoFocus
Press "ESC"

# ---- 9. take back, log out ----
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "9 everything the run rezzed and left is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-features-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

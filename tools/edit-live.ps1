# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# edit-live.ps1 - the live check of the edit tools. Starts the development export, logs a test avatar in, makes two plain boxes
# (Build, Create), opens build mode (Ctrl+B: the Create tool and the Build window), selects a box with the Edit tool, moves, turns and
# sizes it by numbers, sets hollow and twist, makes the other box a 10 x 10 x 0.2 m floor by numbers, links the two (the floor, selected
# last, becomes the root) and unlinks them, Shift-drags the box's Move arrow (the copy stays behind), drags each kind of handle (an arrow,
# a plane handle, a ring with the 5.625-degree snap, a corner box and a face box), snaps the box to the grid (Shift+X), opens Grid Options,
# makes the box physical while it is selected (the region holds it still) and deselects it (it falls), closes build mode, and re-reads the
# region after each change. Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\edit-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#     
#
# Run it under an avatar claim on the account and a build claim (the kind the viewer guard accepts). Output: edit-report.json in
# the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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

# ---- 1. two plain boxes in front of the avatar ----
$me = @((Obs).self.region_position)
$spotA = @{ x = [double]$me[0] + 3.0; y = [double]$me[1] - 1.5; z = [double]$me[2] - 0.9 }
$spotB = @{ x = [double]$me[0] + 3.0; y = [double]$me[1] + 1.5; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spotA.x; y = [double]$me[1]; z = $spotA.z; distance = 7 }); Start-Sleep -Milliseconds 1500
$boxes = @()
foreach ($spot in @($spotA, $spotB)) {
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    [void](Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z })
    $b = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
    if ($b) { Add-Rezzed ([uint32]$b.local); $boxes += $b }
}
Check "1 two boxes are made" ($boxes.Count -eq 2) ("made {0}" -f $boxes.Count)
if ($boxes.Count -lt 2) { $tb = Invoke-TakeBack -Seconds 30; [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
$A = [uint32]$boxes[0].local; $B = [uint32]$boxes[1].local

# ---- 2. Build > Create left build mode on with the Create tool; Ctrl+B closes it and opens it again with Create and the window; Edit selects ----
$made = Edit @{ action = "state" }
$closed = Edit @{ action = "toggle" }
$t = Edit @{ action = "toggle" }
$report.toggle = @($made, $closed, $t)
Check "2a Create is build mode's Create tool; Build closes and reopens it" ($made.build_mode -and $made.tool -eq "create" -and -not $closed.build_mode -and -not $closed.window_open) ("after create: build {0} tool {1}; after Build: build {2} window {3}" -f $made.build_mode, $made.tool, $closed.build_mode, $closed.window_open)
$e = Edit @{ action = "tool"; text = "edit" }
$sel = Edit @{ action = "click"; x = [double]$boxes[0].x; y = [double]$boxes[0].y; z = [double]$boxes[0].z + 0.25 }
Check "2 Build opens with Create and its window; a click with Edit selects the box" ($t.build_mode -and $t.tool -eq "create" -and $t.window_open -and @($sel.selection).Count -eq 1 -and [uint32]$sel.selection[0] -eq $A) ("build {0} tool {1} window {2}; selection {3}" -f $t.build_mode, $t.tool, $t.window_open, (@($sel.selection) -join ","))

# ---- 3. move, rotate and size by numbers, each confirmed by the region ----
$ax = [double]$boxes[0].x + 0.5; $ay = [double]$boxes[0].y; $az = [double]$boxes[0].z + 0.5
[void](Edit @{ action = "position"; x = $ax; y = $ay; z = $az })
$moved = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x $ax 0.01) -and (Near $o.z $az 0.01)) { $o } } 15
Check "3 the position typed in is where the region puts it" ([bool]$moved) ("{0}" -f ($moved | ConvertTo-Json -Compress))
[void](Edit @{ action = "rotation"; x = 0; y = 0; z = 45 })
$rot = Wait-Until { $p = Primary $A; if ($p -and (Near $p.rotation[2] 45 0.2)) { $p } } 15
Check "4 the rotation typed in is the region's" ([bool]$rot) ("rotation {0}" -f (@($rot.rotation) -join ","))
[void](Edit @{ action = "size"; x = 1; y = 2; z = 0.5 })
$sized = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sx 1 0.001) -and (Near $o.sy 2 0.001) -and (Near $o.sz 0.5 0.001)) { $o } } 15
Check "5 the size typed in is the region's" ([bool]$sized) ("{0}" -f ($sized | ConvertTo-Json -Compress))

# ---- 4. hollow and twist ----
[void](Edit @{ action = "shape"; text = "hollow=50,twist_begin=0,twist_end=90" })
$shaped = Wait-Until { $p = Primary $A; if ($p -and (Near $p.hollow 0.5 0.001) -and (Near $p.twist_end 0.5 0.01)) { $p } } 15
Check "6 hollow 50 % and twist 90 degrees reach the region" ([bool]$shaped) ("hollow {0} twist {1}..{2}" -f $shaped.hollow, $shaped.twist_begin, $shaped.twist_end)

# ---- 5. a 10 x 10 x 0.2 m floor by numbers ----
$floorZ = [double]$boxes[1].z - 0.15
[void](Edit @{ action = "select"; text = "$B" })
[void](Edit @{ action = "size"; x = 10; y = 10; z = 0.2 })
[void](Edit @{ action = "position"; x = [double]$boxes[1].x + 3; y = [double]$boxes[1].y + 3; z = $floorZ })
$floor = Wait-Until { $o = OwnObj $B; if ($o -and (Near $o.sx 10 0.001) -and (Near $o.sy 10 0.001) -and (Near $o.sz 0.2 0.001) -and (Near $o.z $floorZ 0.01)) { $o } } 15
Check "7 a 10 x 10 x 0.2 m floor by numbers" ([bool]$floor) ("{0}" -f ($floor | ConvertTo-Json -Compress))

# ---- 6. link the two (the floor selected last becomes the root), then unlink ----
[void](Edit @{ action = "select"; text = "$A" })
[void](Edit @{ action = "select"; text = "$B"; expect = "shift" })
$lk = Edit @{ action = "link" }
$linked = Wait-Until { $p = OwnPrim $A; if ($p -and [uint32]$p.parent -eq $B) { $p } } 15
Check "8 Link: the floor, selected last, is the root" ([bool]$linked -and $lk.done -eq "sent") ("link {0}; box parent {1}" -f $lk.done, $linked.parent)
[void](Edit @{ action = "select"; text = "$B" })
$ul = Edit @{ action = "unlink" }
$unlinked = Wait-Until { $p = OwnPrim $A; $q = OwnPrim $B; if ($p -and $q -and [uint32]$p.parent -eq 0 -and [uint32]$q.parent -eq 0) { $p } } 15
Check "9 Unlink: both stand alone again" ([bool]$unlinked -and $ul.done) ("unlink {0}" -f $ul.done)

# ---- 7. Shift-drag of the Move tool's +x arrow: the copy stays behind, the box moves ----
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { [uint32]$_.local })
$origin = OwnObj $A
[void](Edit @{ action = "select"; text = "$A" })
[void](Interact @{ action = "aim"; x = [double]$origin.x + 1; y = [double]$origin.y; z = [double]$origin.z; distance = 7 }); Start-Sleep -Milliseconds 1500
[void](Edit @{ action = "tool"; text = "move" })
$dr = Edit @{ action = "handle"; text = "axis:x+"; x = 2; y = 0; z = 0; expect = "shift" }
$report.drag = $dr
$copy = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains [uint32]$_.local }) | Select-Object -First 1 } 15
if ($copy) { Add-Rezzed ([uint32]$copy.local) }
$after = Wait-Until { $o = OwnObj $A; if ($o -and -not (Near $o.x $origin.x 0.05)) { $o } } 15
$copyStays = $copy -and (Near $copy.x $origin.x 0.05) -and (Near $copy.y $origin.y 0.05)
Check "10 Shift-drag leaves a copy where the box was and moves the box" ([bool]$copyStays -and [bool]$after) ("copy {0}; box now {1}; handle {2}" -f ($copy | ConvertTo-Json -Compress), ($after | ConvertTo-Json -Compress), ($dr.done | ConvertTo-Json -Compress))

# ---- 7b. each kind of handle, re-read from the region ----
[void](Edit @{ action = "select"; text = "$A" })
[void](Edit @{ action = "snap"; text = "off" })
$o0 = OwnObj $A
$h = Edit @{ action = "handle"; text = "axis:y+"; x = 0; y = 1; z = 0 }
$ay1 = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.y ([double]$o0.y + 1) 0.05) -and (Near $o.x $o0.x 0.02) -and (Near $o.z $o0.z 0.02)) { $o } } 15
Check "13 the +y arrow moves the box one metre north and nowhere else" ([bool]$ay1) ("handle {0}; before {1}; after {2}" -f ($h.done | ConvertTo-Json -Compress), ($o0 | ConvertTo-Json -Compress), ((OwnObj $A) | ConvertTo-Json -Compress))
$o1 = OwnObj $A
$h = Edit @{ action = "handle"; text = "plane:z"; x = 1; y = -1; z = 0 }
$pl = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.x ([double]$o1.x + 1) 0.05) -and (Near $o.y ([double]$o1.y - 1) 0.05) -and (Near $o.z $o1.z 0.02)) { $o } } 15
Check "14 the flat plane handle moves the box across the ground only" ([bool]$pl) ("handle {0}; after {1}" -f ($h.done | ConvertTo-Json -Compress), ((OwnObj $A) | ConvertTo-Json -Compress))
[void](Edit @{ action = "snap"; text = "on" })
$r0 = (Primary $A).rotation[2]
$h = Edit @{ action = "handle"; text = "ring:z"; x = 30; y = 1; z = 0 }
$want = [double]$r0 + 28.125
if ($want -gt 180) { $want -= 360 }
$rg = Wait-Until { $p = Primary $A; if ($p -and (Near $p.rotation[2] $want 0.3)) { $p } } 15
Check "15 the z ring turns the box by 28.125 degrees: 30 snapped to 5.625 steps outside the snap ring" ([bool]$rg) ("handle {0}; z rotation {1} -> {2}, wanted {3}" -f ($h.done | ConvertTo-Json -Compress), $r0, (Primary $A).rotation[2], $want)
$s0 = OwnObj $A
$h = Edit @{ action = "handle"; text = "corner:7"; x = 1.5; y = 0; z = 0 }
$cr = Wait-Until { $o = OwnObj $A; if ($o -and (Near ([double]$o.sx / [double]$s0.sx) 1.5 0.05) -and (Near ([double]$o.sy / [double]$s0.sy) 1.5 0.05) -and (Near ([double]$o.sz / [double]$s0.sz) 1.5 0.05)) { $o } } 15
Check "16 a corner box stretches every side by the same ratio" ([bool]$cr) ("handle {0}; size {1},{2},{3} -> {4}" -f ($h.done | ConvertTo-Json -Compress), $s0.sx, $s0.sy, $s0.sz, ((OwnObj $A) | ConvertTo-Json -Compress))
$f0 = OwnObj $A
$newZ = [double]$f0.sz + 0.5
$h = Edit @{ action = "handle"; text = "face:z+"; x = $newZ; y = 0; z = 0 }
$fc = Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.sz $newZ 0.05) -and (Near $o.sx $f0.sx 0.01) -and (Near $o.z ([double]$f0.z + 0.25) 0.05)) { $o } } 15
Check "17 the top face box makes the box taller and its bottom stays put" ([bool]$fc) ("handle {0}; before {1}; after {2}" -f ($h.done | ConvertTo-Json -Compress), ($f0 | ConvertTo-Json -Compress), ((OwnObj $A) | ConvertTo-Json -Compress))
$x0 = OwnObj $A
$sx = Edit @{ action = "snap_xy" }
$snapped = Wait-Until { $o = OwnObj $A; if ($o -and (Near ([double]$o.x * 2) ([math]::Round([double]$o.x * 2)) 0.01) -and (Near ([double]$o.y * 2) ([math]::Round([double]$o.y * 2)) 0.01)) { $o } } 15
Check "18 Snap Object XY to Grid puts the box on the 0.5 m grid" ([bool]$snapped -and $sx.done) ("before {0},{1}; after {2}" -f $x0.x, $x0.y, ((OwnObj $A) | ConvertTo-Json -Compress))
$go = Edit @{ action = "grid_options" }
Check "19 Grid Options opens" ([bool]$go.done) ("open {0}" -f $go.done)

# ---- 7c. a physical object stays still while it is selected and moves again once it is deselected ----
$p0 = OwnObj $A
$lift = [double]$p0.z + 3
[void](Edit @{ action = "position"; x = [double]$p0.x; y = [double]$p0.y; z = $lift })
[void](Wait-Until { $o = OwnObj $A; if ($o -and (Near $o.z $lift 0.02)) { $o } } 15)
[void](Edit @{ action = "flags"; text = "physical" })
Start-Sleep -Seconds 4
$held = OwnObj $A
$stillHeld = $held -and (Near $held.z $lift 0.05)
[void](Edit @{ action = "select"; text = "" })
$fell = Wait-Until { $o = OwnObj $A; if ($o -and [double]$o.z -lt $lift - 1) { $o } } 15
Check "20 selected and physical the box hangs still; deselected it falls" ([bool]$stillHeld -and [bool]$fell) ("lifted to {0}; 4 s later {1}; after deselecting {2}" -f $lift, $held.z, $fell.z)

# ---- 8. Esc leaves build mode ----
$off = Edit @{ action = "toggle" }
Check "11 Build again closes build mode and its window" (-not $off.build_mode -and -not $off.window_open) ("build {0} window {1}" -f $off.build_mode, $off.window_open)
[void](Interact @{ action = "aim"; distance = 0 })

# ---- 9. everything the run rezzed back to the inventory, by id, proven by re-reading the region ----
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "12 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)

$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "edit-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# mirror-copy-live.ps1 - the live check of Mirror a copy in the Build window's Arrange section, with handed shapes. Starts the development
# export, logs a test avatar in, makes two boxes and links them into one linkset (a plain root and a part with a twist and a top shear,
# set by number), and ten times:
#   - gives the part a different twist and shear (set-up only), lets go of the selection,
#   - picks the linkset with the real pointer (a click on it), chooses the X axis and the plane "Selection centre" in the Mirror rows with
#     the pointer (open the list, click the item), reads the rules' answer for the copy's places (nothing sent), then presses the real
#     "Mirror a copy" button with the pointer,
#   - waits for the copy, then reads back: every part of the copy within 1 mm and 0.1 degree of the rules' answer; the copy part's shape
#     settings as the mirrored set (twist begin and end negated, shear X negated, shear Y and the taper kept; within the 0.01 step the
#     region keeps); the originals unmoved and their shape unchanged.
# The copy is taken back after each run. The control: with nothing picked the button is off and a press makes no object.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\mirror-copy-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: mirror-copy-report.json in the sandbox.
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
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "mirror-copy-report.json") -Encoding UTF8
    exit $code
}

# ---- the viewer and the login ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
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
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note ("not shown: " + $name + " reply " + ((Edit @{ action = "control"; text = $name }) | ConvertTo-Json -Compress -Depth 3)); $false } }
function HighX($o) { [double]$o.x + [double]$o.sx / 2 }


# ---- two boxes (1 and 1.5 m) 3 m apart, linked into one linkset: a plain root and a part that gets a twist and a top shear ----
$me = @((Obs).self.region_position)
$ids = @(); $locals = @()
$baseX = [double]$me[0] + 10.0; $baseY = [double]$me[1] + 6.0; $z = [double]$me[2] + 2.1
for ($k = 0; $k -lt 2; $k++) {
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
$sizes = @(1.0, 1.5)
for ($k = 0; $k -lt 2; $k++) {
    [void](Edit @{ action = "select"; text = "$($locals[$k])" })
    [void](Edit @{ action = "size"; x = $sizes[$k]; y = $sizes[$k]; z = $sizes[$k] })
    [void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
    [void](Edit @{ action = "position"; x = ($baseX + 3.0 * $k); y = ($baseY + 3); z = $z })
}
$ready = Wait-Until { $r = @(0..1 | ForEach-Object { OwnObj $locals[$_] }); if (@($r | Where-Object { $_ }).Count -eq 2 -and (Near $r[1].sx 1.5 0.001) -and (Near $r[1].z $z 0.01)) { $r } } 20
Check "0 two boxes of 1 and 1.5 m stand 3 m apart" ([bool]$ready) (($ready | ConvertTo-Json -Compress -Depth 4))
if (-not $ready) { Finish 1 }
# link them: the first box is the one picked last, so it becomes the root
[void](Edit @{ action = "select"; text = (($locals[1], $locals[0]) -join ",") })
[void](Edit @{ action = "link" })
$linkedOk = Wait-Until { $o = Build @{ action = "state" }; $parts = @($o.own_prims | Where-Object { $locals -contains [uint32]$_.local }); if ($parts.Count -eq 2 -and @($parts | Where-Object { [uint32]$_.parent -ne 0 }).Count -eq 1) { $parts } } 15
Check "0b the two boxes are one linkset (one root, one part)" ([bool]$linkedOk) (($linkedOk | ForEach-Object { "{0}->{1}" -f $_.local, $_.parent }) -join " ")
if (-not $linkedOk) { Finish 1 }
$root = [uint32](@($linkedOk | Where-Object { [uint32]$_.parent -eq 0 })[0].local)
$part = [uint32](@($linkedOk | Where-Object { [uint32]$_.parent -ne 0 })[0].local)
[void](Interact @{ action = "aim"; x = ($baseX + 1.5); y = ($baseY + 3); z = $z; distance = 24 }); Start-Sleep -Milliseconds 1500
if (-not (ClickCtl "section_arrange")) { Check "0c the Arrange section header is on screen" $false "not shown"; Finish 1 }
function World($list) { @($list | Sort-Object { [uint32]$_.local }) }
function AngleDeg($a, $b) {
    $d = [math]::Abs($a[0] * $b[0] + $a[1] * $b[1] + $a[2] * $b[2] + $a[3] * $b[3])
    $na = [math]::Sqrt($a[0] * $a[0] + $a[1] * $a[1] + $a[2] * $a[2] + $a[3] * $a[3])
    $nb = [math]::Sqrt($b[0] * $b[0] + $b[1] * $b[1] + $b[2] * $b[2] + $b[3] * $b[3])
    2 * [math]::Acos([math]::Min(1.0, $d / ($na * $nb))) * 180 / [math]::PI
}
function Compare-Poses($want, $have) {
    $w = World $want; $h = World $have
    if ($w.Count -ne $h.Count -or $w.Count -eq 0) { return [ordered]@{ ok = $false; maxMm = 1e9; maxDeg = 1e9 } }
    $mm = 0.0; $deg = 0.0
    for ($i = 0; $i -lt $w.Count; $i++) {
        $d = 1000 * [math]::Sqrt([math]::Pow($w[$i].x - $h[$i].x, 2) + [math]::Pow($w[$i].y - $h[$i].y, 2) + [math]::Pow($w[$i].z - $h[$i].z, 2))
        if ($d -gt $mm) { $mm = $d }
        $g = AngleDeg @($w[$i].rotation) @($h[$i].rotation)
        if ($g -gt $deg) { $deg = $g }
    }
    [ordered]@{ ok = ($mm -le 1.0 -and $deg -le 0.1); maxMm = $mm; maxDeg = $deg }
}
# the shape settings the Build window reports for the part picked with Edit linked parts
function PartShape([uint32]$local) {
    [void](Edit @{ action = "linked"; text = "on" })
    [void](Edit @{ action = "select"; text = "$local" })
    $s = EditUntil { param($e) $e.primary -and [uint32]$e.primary.local -eq $local }
    [void](Edit @{ action = "linked"; text = "off" })
    if ($s -and $s.primary -and [uint32]$s.primary.local -eq $local) { $s.primary } else { $null }
}
function ShapeNear($a, $b) { $t = 0.0101; (Near $a.twist_begin $b.twist_begin $t) -and (Near $a.twist_end $b.twist_end $t) -and (Near $a.shear_x $b.shear_x $t) -and (Near $a.shear_y $b.shear_y $t) -and (Near $a.scale_x $b.scale_x $t) -and (Near $a.scale_y $b.scale_y $t) }

# ---- 1. ten runs: pick the linkset by pointer, X and Selection centre by pointer, press Mirror a copy, read the copy back ----
$good = 0; $worstMm = 0.0; $worstDeg = 0.0; $rows = @(); $shapeGood = 0; $originalsGood = 0
for ($run = 1; $run -le $Runs; $run++) {
    # a different handed shape each run, by number (set-up only): twist in degrees, top shear as fractions
    $tb = @(10, 20, 30, 40, 50, 60, 70, 80, 90, 100)[$run - 1]; $te = [math]::Round($tb * 1.5)
    $sx = 0.05 * (1 + ($run % 5)); $sy = -0.03 * ($run % 4)
    [void](Edit @{ action = "linked"; text = "on" })
    [void](Edit @{ action = "select"; text = "$part" })
    [void](Edit @{ action = "shape"; text = "twist_begin=$tb,twist_end=$te,shear_x=$sx,shear_y=$sy,taper_x=0.2" })
    [void](Edit @{ action = "linked"; text = "off" })
    $orig = $null
    $settled = Wait-Until { $o = PartShape $part; if ($o -and (Near $o.shear_x $sx 0.0101) -and (Near $o.twist_end ($te / 180.0) 0.0101)) { $o } } 12
    $orig = $settled
    $now = $null
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 800
    [void](Interact @{ action = "aim"; x = ($baseX + 1.5); y = ($baseY + 3); z = $z; distance = 24 }); Start-Sleep -Milliseconds 800
    # the Build window covers the left of the view: click the object that stands furthest to the right (either picks the whole linkset)
    $pt = $null
    foreach ($id in $ids) { $q = ScreenOf $id; if ($q -and $q[0] -gt 520 -and ($null -eq $pt -or $q[0] -gt $pt[0])) { $pt = $q } }
    if (-not $pt) { $rows += [ordered]@{ run = $run; error = "the linkset is not on screen" }; continue }
    if ($run -in 3, 8) { Shot ("mirror-copy-pre-run{0}" -f $run) }
    ClickAt $pt[0] $pt[1] $false
    $sel = EditUntil { param($s) @($s.selection).Count -ge 1 }
    if (@($sel.selection).Count -lt 1) { Shot ("mirror-copy-miss-run{0}" -f $run); $rows += [ordered]@{ run = $run; error = "the pointer did not pick the linkset"; point = $pt }; continue }
    $opened = ClickCtl "mirror_axis"; Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "popup:mirror_axis:0"); Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "mirror_plane"); Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "popup:mirror_plane:0"); Start-Sleep -Milliseconds 400
    $before = (Edit @{ action = "mirror_world" }).done
    $plan = (Edit @{ action = "mirror_plan"; text = "x:centre" }).done
    $apart = Compare-Poses $plan $before
    $offered = (Edit @{ action = "state" }).arrange.mirror_copy
    $pressed = $opened -and (ClickCtl "mirror_copy")
    # the copy arrives selected and is mirrored once its parts are all there: wait until the selection holds two new parts standing where the rules say
    $copy = Wait-Until {
        $h = (Edit @{ action = "mirror_world" }).done
        if (@($h).Count -eq 2 -and @($h | Where-Object { $locals -contains [uint32]$_.local }).Count -eq 0) { $c = Compare-Poses $plan $h; if ($c.ok) { $h } }
    } 30
    $have = (Edit @{ action = "mirror_world" }).done
    foreach ($h in @($have)) { if ($locals -notcontains [uint32]$h.local) { Add-Rezzed ([uint32]$h.local) } }
    $cmp = Compare-Poses $plan $have
    if ($cmp.maxMm -gt $worstMm -and $cmp.maxMm -lt 1e8) { $worstMm = $cmp.maxMm }
    if ($cmp.maxDeg -gt $worstDeg -and $cmp.maxDeg -lt 1e8) { $worstDeg = $cmp.maxDeg }
    $copyPart = @($have | Where-Object { [uint32]$_.parent -ne 0 }) | Select-Object -First 1
    $copyRoot = @($have | Where-Object { [uint32]$_.parent -eq 0 }) | Select-Object -First 1
    # the copy part's shape reads back as the mirrored set
    $cs = $null; $shapeOk = $false
    if ($copyPart -and $orig) {
        $want = [pscustomobject]@{ twist_begin = -[double]$orig.twist_begin; twist_end = -[double]$orig.twist_end; shear_x = -[double]$orig.shear_x; shear_y = [double]$orig.shear_y; scale_x = [double]$orig.scale_x; scale_y = [double]$orig.scale_y }
        $cs = Wait-Until { $o = PartShape ([uint32]$copyPart.local); if ($o -and (ShapeNear $want $o)) { $o } } 15
        $shapeOk = [bool]$cs
        if (-not $cs) { $cs = PartShape ([uint32]$copyPart.local) }
    }
    if ($shapeOk) { $shapeGood++ }
    # the originals did not move and kept their shape
    [void](Edit @{ action = "select"; text = "$root" })
    $afterOrig = (Edit @{ action = "mirror_world" }).done
    $origPose = Compare-Poses $before $afterOrig
    $origShape = PartShape $part
    $origOk = $origPose.maxMm -le 1.0 -and $origPose.maxDeg -le 0.1 -and $origShape -and (ShapeNear $orig $origShape)
    if ($origOk) { $originalsGood++ }
    # control for this run: the shape the copy started with (the original's) is not the mirrored set, and the copy stood apart from the plan
    $differs = $orig -and $cs -and -not (ShapeNear $orig $cs)
    $moved = $apart.maxMm -ge 50
    $ok = $pressed -and $offered -and [bool]$copy -and $cmp.ok -and $shapeOk -and $origOk -and $differs -and $moved
    if ($ok) { $good++ }
    # take the copy back before the next run, so the next pick cannot land on it
    $copyIds = @($have | Where-Object { $locals -notcontains [uint32]$_.local } | ForEach-Object { [uint32]$_.local })
    if ($copyIds.Count -gt 0) {
        [void](Build @{ action = "take_back"; text = ($copyIds -join ",") })
        [void](Wait-Until { $own = Get-OwnLocals; if (@($copyIds | Where-Object { $own -contains $_ }).Count -eq 0) { $true } } 15)
    }
    $rows += [ordered]@{ run = $run; twist_deg = @($tb, $te); shear = @($sx, $sy); start_to_plan_mm = $apart.maxMm; copy_vs_plan_mm = $cmp.maxMm; copy_vs_plan_deg = $cmp.maxDeg; offered = $offered
        original_shape = $orig; copy_shape = $cs; shape_ok = $shapeOk; originals_ok = [bool]$origOk; ok = $ok }
    Note ("run {0}: twist {1}/{2}, shear {3:N2}/{4:N2}; copy vs plan {5:N3} mm, {6:N4} deg; shape {7}; originals {8}" -f $run, $tb, $te, $sx, $sy, $cmp.maxMm, $cmp.maxDeg, $shapeOk, [bool]$origOk)
}
$report.runs = $rows
Shot "mirror-copy-after"
Check "1 pointer picks the linkset, Mirror a copy on X: the copy's parts within 1 mm and 0.1 degree of the rules' answer, its shape the mirrored set, the originals untouched" ($good -eq $Runs) ("{0} of {1} runs; places worst {2:N3} mm, {3:N4} deg; shapes {4}; originals {5}" -f $good, $Runs, $worstMm, $worstDeg, $shapeGood, $originalsGood)

# ---- 2. control: nothing selected, the Mirror a copy button is off and a press makes no object ----
[void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 500
$countBefore = @(Get-OwnLocals).Count
[void](ClickCtl "mirror_copy"); Start-Sleep -Milliseconds 2500
$none = Edit @{ action = "state" }
$countAfter = @(Get-OwnLocals).Count
Check "2 control: with nothing picked Mirror a copy is off and a press makes no object" (-not $none.arrange.mirror_copy -and $countAfter -eq $countBefore) ("offered {0}; objects {1} -> {2}" -f $none.arrange.mirror_copy, $countBefore, $countAfter)

# ---- 3. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb2 = Invoke-TakeBack -Seconds 40
$report.take_back = $tb2
Check "3 everything the run rezzed is taken back (re-read)" ([bool]$tb2.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb2.rezzed).Count, @($tb2.sent).Count, @($tb2.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "mirror-copy-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

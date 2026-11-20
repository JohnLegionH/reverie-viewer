# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-tools-live.ps1 - the live check of the Create shapes, Duplicate, the click action, For sale and the land impact. Starts the
# development export, logs a test avatar in, and through the automation interface and the engine's key input:
#   1 shapes     Create makes one of each of the fifteen shapes (a volume's profile, path, cut and top size are read back from the region; a
#                tree and a patch of grass are made too)
#   2 duplicate  Ctrl+D copies the selected box half a metre east and north of itself
#   3 shift-drag a Shift-drag of the Move arrow leaves a copy where the box was
#   4 copy       Create with Copy selection places a copy of the selection at the click
#   5 click      a click action set in the Object section reaches the region; a left click on a Zoom object moves the camera
#   6 sale       For sale (type and price) reaches the region and is read back after a new selection
#   7 impact     the selection's land impact is shown from the region's GetObjectCost answer
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out. Return needs a second owner's object and is not run.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\build-tools-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: build-tools-report.json in the
# sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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

function Edit([hashtable]$b) { Api "edit" $b }
function Own() { @((Build @{ action = "state" }).own_objects) }
function OwnObj([uint32]$local) { @(Own | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function NoFocus() { [void](In @{ kind = "release_focus" }) }
function NewOwn([uint32[]]$before) { @(Own | Where-Object { $before -notcontains [uint32]$_.local }) }

$me = @((Obs).self.region_position)
$gz = [double]$me[2] - 0.9
[void](Interact @{ action = "aim"; x = [double]$me[0] + 4; y = [double]$me[1]; z = $gz; distance = 14 }); Start-Sleep -Milliseconds 1500
NoFocus
Press "B" $true
$null = Wait-Until { $s = Edit @{ action = "state" }; if ($s.build_mode) { $s } } 5

# ---- 1. the fifteen shapes ----
$shapes = @("Cube","Prism","Pyramid","Tetrahedron","Cylinder","Hemicylinder","Cone","Hemicone","Sphere","Hemisphere","Torus","Tube","Ring","Tree","Grass")
# profile, path, path scale x, y, shear x, path end, profile begin and end, as the region states them for each volume
$want = @{
    Cube = @(1,16,1,1,0,1,0,1); Prism = @(1,16,0,1,-0.5,1,0,1); Pyramid = @(1,16,0,0,0,1,0,1); Tetrahedron = @(3,16,0,0,0,1,0,1)
    Cylinder = @(0,16,1,1,0,1,0,1); Hemicylinder = @(0,16,1,1,0,1,0.25,0.75); Cone = @(0,16,0,0,0,1,0,1); Hemicone = @(0,16,0,0,0,1,0.25,0.75)
    Sphere = @(5,32,1,1,0,1,0,1); Hemisphere = @(5,32,1,1,0,0.5,0,1); Torus = @(0,32,1,0.25,0,1,0,1); Tube = @(1,32,1,0.25,0,1,0,1); Ring = @(3,32,1,0.25,0,1,0,1)
}
$i = 0
foreach ($name in $shapes) {
    $col = $i % 5; $row = [math]::Floor($i / 5); $i++
    $spot = @{ x = [double]$me[0] + 2.0 + 2.5 * $col; y = [double]$me[1] - 2.5 + 2.5 * $row; z = $gz }
    $before = @(Own | ForEach-Object { [uint32]$_.local })
    $r = Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z; expect = $name }
    $made = Wait-Until { @(NewOwn $before) | Select-Object -First 1 } 20
    if ($made) { Add-Rezzed ([uint32]$made.local) }
    if ($name -in @("Tree","Grass")) {
        Check "1 $name is made" ([bool]$made) ("placed {0}; {1}" -f $r.placed, ($made | ConvertTo-Json -Compress))
        continue
    }
    $w = $want[$name]
    $ok = $made -and (Near $made.profile $w[0] 0.01) -and (Near $made.path $w[1] 0.01) -and (Near $made.path_scale[0] $w[2] 0.011) -and (Near $made.path_scale[1] $w[3] 0.011) `
        -and (Near $made.shear_x $w[4] 0.011) -and (Near $made.path_end $w[5] 0.001) -and (Near $made.profile_cut[0] $w[6] 0.001) -and (Near $made.profile_cut[1] $w[7] 0.001)
    Check "1 $name is made with the official profile, path, cut and top size" ([bool]$ok) ("placed {0}; {1}" -f $r.placed, ($made | ConvertTo-Json -Compress))
    Start-Sleep -Milliseconds 600
}
$ours = @(Own)

# ---- 2. Ctrl+D ----
$cube = @($ours | Where-Object { [uint32]$_.local -eq $script:rezzed[0] }) | Select-Object -First 1
[void](Edit @{ action = "tool"; text = "edit" })
[void](Edit @{ action = "select"; text = "$($cube.local)" })
$o0 = OwnObj ([uint32]$cube.local)
$before = @(Own | ForEach-Object { [uint32]$_.local })
NoFocus
Press "D" $true
$dup = Wait-Until { @(NewOwn $before) | Select-Object -First 1 } 15
if ($dup) { Add-Rezzed ([uint32]$dup.local) }
$offsetOk = $dup -and (Near $dup.x ([double]$o0.x + 0.5) 0.05) -and (Near $dup.y ([double]$o0.y + 0.5) 0.05) -and (Near $dup.z $o0.z 0.05)
Check "2 Ctrl+D makes a copy half a metre east and north" ([bool]$offsetOk) ("box {0}; copy {1}" -f ($o0 | ConvertTo-Json -Compress), ($dup | ConvertTo-Json -Compress))
$selNow = Wait-Until { $s = Edit @{ action = "state" }; if (@($s.selection).Count -eq 1) { $s } } 6
Check "2b the copy is the selection" ($selNow -and $dup -and [uint32]$selNow.selection[0] -eq [uint32]$dup.local) ("selection {0}" -f (@($selNow.selection) -join ","))

# ---- 3. Shift-drag ----
[void](Edit @{ action = "select"; text = "$($cube.local)" })
[void](Interact @{ action = "aim"; x = [double]$o0.x + 1; y = [double]$o0.y; z = [double]$o0.z; distance = 7 }); Start-Sleep -Milliseconds 1500
NoFocus; Press "2" $true
$before = @(Own | ForEach-Object { [uint32]$_.local })
$dr = Edit @{ action = "handle"; text = "axis:x+"; x = 2; y = 0; z = 0; expect = "shift" }
$copy = Wait-Until { @(NewOwn $before) | Select-Object -First 1 } 15
if ($copy) { Add-Rezzed ([uint32]$copy.local) }
$moved = Wait-Until { $o = OwnObj ([uint32]$cube.local); if ($o -and -not (Near $o.x $o0.x 0.05)) { $o } } 15
Check "3 a Shift-drag leaves a copy where the box was and the box moves on" ([bool]($copy -and (Near $copy.x $o0.x 0.05) -and (Near $copy.y $o0.y 0.05) -and $moved)) ("copy {0}; box now {1}" -f ($copy | ConvertTo-Json -Compress), ($moved | ConvertTo-Json -Compress))

# ---- 4. Copy selection ----
[void](Edit @{ action = "select"; text = "$($cube.local)" })
[void](Edit @{ action = "tool"; text = "create" })
[void](Edit @{ action = "copy_selection"; text = "on" })
[void](Interact @{ action = "aim"; x = [double]$me[0] + 4; y = [double]$me[1]; z = $gz; distance = 14 }); Start-Sleep -Milliseconds 1500
$before = @(Own | ForEach-Object { [uint32]$_.local })
$spot = @{ x = [double]$me[0] + 6.0; y = [double]$me[1] + 4.0; z = $gz }
$r = Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z }
$cp = Wait-Until { @(NewOwn $before) | Select-Object -First 1 } 15
if ($cp) { Add-Rezzed ([uint32]$cp.local) }
Check "4 Create with Copy selection places a copy of the selection at the click" ([bool]($cp -and (Near $cp.x $spot.x 2.5) -and (Near $cp.y $spot.y 2.5))) ("placed {0}; copy {1}; click {2},{3}" -f $r.placed, ($cp | ConvertTo-Json -Compress), $spot.x, $spot.y)
[void](Edit @{ action = "copy_selection"; text = "off" })

# ---- 5. click action ----
[void](Edit @{ action = "tool"; text = "edit" })
[void](Edit @{ action = "select"; text = "$($cube.local)" })
$ca = Edit @{ action = "click_action"; text = "zoom" }
$z = Wait-Until { $o = OwnObj ([uint32]$cube.local); if ($o -and [int]$o.click -eq 7) { $o } } 15
Check "5 the Zoom click action reaches the region" ([bool]$z) ("sent {0}; click {1}" -f $ca.done.sent, $z.click)
$buy = Edit @{ action = "click_action"; text = "buy" }
Check "5b Buy is refused for an object not for sale" (-not $buy.done.sent -and $buy.done.notice -eq "edit.click.cant_buy") ("{0}" -f ($buy.done | ConvertTo-Json -Compress))
[void](Edit @{ action = "select"; text = "" })
NoFocus; Press "B" $true
$null = Wait-Until { $s = Edit @{ action = "state" }; if (-not $s.build_mode) { $s } } 5
[void](Api "orbit" @{ op = "reset" })
$cam0 = Api "orbit" @{ op = "state" }
[void](Interact @{ action = "click"; id = [string]$cube.id })
$cam1 = Wait-Until { $c = Api "orbit" @{ op = "state" }; if ($c -and -not $c.focus_on_avatar) { $c } } 6
Check "5c a left click on a Zoom object frames it with the camera" ([bool]$cam1) ("camera before {0}; after {1}" -f ($cam0 | ConvertTo-Json -Compress), ($cam1 | ConvertTo-Json -Compress))
[void](Api "orbit" @{ op = "reset" })
NoFocus; Press "B" $true
$null = Wait-Until { $s = Edit @{ action = "state" }; if ($s.build_mode) { $s } } 5
[void](Edit @{ action = "tool"; text = "edit" })

# ---- 6. For sale ----
[void](Edit @{ action = "select"; text = "$($cube.local)" })
$sold = Edit @{ action = "sale"; text = "contents:75" }
Start-Sleep -Seconds 2
[void](Edit @{ action = "select"; text = "" })
[void](Edit @{ action = "select"; text = "$($cube.local)" })
$state = Wait-Until { $s = Edit @{ action = "state" }; if ($s.sale -and $s.sale.for_sale -and [int]$s.sale.price -eq 75 -and $s.sale.type -eq "contents") { $s } } 10
Check "6 For sale: Contents at L$ 75 reaches the region and is read back after selecting again" ([bool]$state) ("sent {0}; sale {1}" -f $sold.done, (($state.sale) | ConvertTo-Json -Compress))
[void](Edit @{ action = "sale"; text = "off" })
Start-Sleep -Seconds 2
[void](Edit @{ action = "select"; text = "" })
[void](Edit @{ action = "select"; text = "$($cube.local)" })
$off = Wait-Until { $s = Edit @{ action = "state" }; if ($s.sale -and -not $s.sale.for_sale) { $s } } 10
Check "6b taking it off sale is read back" ([bool]$off) ("sale {0}" -f (($off.sale) | ConvertTo-Json -Compress))

# ---- 7. land impact ----
[void](Edit @{ action = "land_impact_refresh" })
$impact = Wait-Until { $s = Edit @{ action = "state" }; if ($s.land_impact -and [int]$s.land_impact.impact -ge 1) { $s } } 12
Check "7 the selection's land impact is shown from the region's answer" ([bool]$impact) ("land impact {0}" -f (($impact.land_impact) | ConvertTo-Json -Compress))
Shot "build-tools-selected"

# ---- 8. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "8 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)

$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-tools-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

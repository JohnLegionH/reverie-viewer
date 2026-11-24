# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# mirror-live.ps1 - the live check of Mirror in the Build window's Arrange section. Starts the development export, logs a test avatar in,
# makes three boxes of different sizes and links them into one linkset, and ten times:
#   - puts the linkset into a different uneven state by number (set-up only: the root's place and turn, each part's place and turn in the
#     root's frame, so the three parts are asymmetric and the parts are turned),
#   - picks the linkset with the real pointer (a click on it), chooses the X axis in the Mirror row with the pointer (open the list, click
#     X) and the plane "Selection centre" the same way, asks the viewer for the rules' answer (nothing sent), then presses the real Mirror
#     button with the pointer,
#   - reads every part back as the region reports it and compares with the rules' answer: each part's place within 1 mm and turn within
#     0.1 degree.
# The control reads the parts before the press: at least one part stands 5 cm or more from where the rules put it (so a pass cannot be an
# unchanged linkset). A second control selects nothing: the Mirror button is off and a press moves nothing.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\mirror-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region and a live-login claim. Output: mirror-report.json in the sandbox.
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
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "mirror-report.json") -Encoding UTF8
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
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function HighX($o) { [double]$o.x + [double]$o.sx / 2 }

# ---- three boxes (1, 1.5 and 2 m) side by side, linked into one linkset ----
$me = @((Obs).self.region_position)
$ids = @(); $locals = @()
$baseX = [double]$me[0] + 10.0; $baseY = [double]$me[1] + 6.0; $z = [double]$me[2] + 2.1
for ($k = 0; $k -lt 3; $k++) {
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
$sizes = @(1.0, 1.5, 2.0)
for ($k = 0; $k -lt 3; $k++) {
    [void](Edit @{ action = "select"; text = "$($locals[$k])" })
    [void](Edit @{ action = "size"; x = $sizes[$k]; y = $sizes[$k]; z = $sizes[$k] })
    [void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
    [void](Edit @{ action = "position"; x = $baseX; y = ($baseY + 3 * $k); z = $z })
}
$ready = Wait-Until { $r = @(0..2 | ForEach-Object { OwnObj $locals[$_] }); if (@($r | Where-Object { $_ }).Count -eq 3 -and (Near $r[2].sx 2 0.001) -and (Near $r[2].z $z 0.01)) { $r } } 20
Check "0 three boxes of 1, 1.5 and 2 m stand in a row" ([bool]$ready) (($ready | ConvertTo-Json -Compress -Depth 4))
if (-not $ready) { Finish 1 }
# link them: the first box is the one picked last, so it becomes the root
[void](Edit @{ action = "select"; text = (($locals[1], $locals[2], $locals[0]) -join ",") })
[void](Edit @{ action = "link" })
$linkedOk = Wait-Until { $o = Build @{ action = "state" }; $parts = @($o.own_prims | Where-Object { $locals -contains [uint32]$_.local }); if ($parts.Count -eq 3 -and @($parts | Where-Object { [uint32]$_.parent -ne 0 }).Count -eq 2) { $parts } } 20
Check "0b the three boxes are one linkset (one root, two parts)" ([bool]$linkedOk) (($linkedOk | ForEach-Object { "{0}->{1}" -f $_.local, $_.parent }) -join " ")
if (-not $linkedOk) { Finish 1 }
$root = [uint32](@($linkedOk | Where-Object { [uint32]$_.parent -eq 0 })[0].local)
$kids = @($locals | Where-Object { $_ -ne $root })
[void](Interact @{ action = "aim"; x = ($baseX + 1.0); y = ($baseY + 3); z = $z; distance = 24 }); Start-Sleep -Milliseconds 1500
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

# ---- 1. ten runs: pick the linkset by pointer, choose X and Selection centre, press Mirror, read every part back ----
$good = 0; $worstMm = 0.0; $worstDeg = 0.0; $rows = @()
for ($run = 1; $run -le $Runs; $run++) {
    # an uneven state by number: the root's place and turn, then each part's place and turn in the root's frame
    $rz = @(0, 25, -40, 70, 115, -150, 10, 55, -80, 135)[$run - 1]
    [void](Edit @{ action = "linked"; text = "off" })
    [void](Edit @{ action = "select"; text = "$root" })
    [void](Edit @{ action = "rotation"; x = (8 * $run % 30); y = (-6 * $run % 25); z = $rz })
    [void](Edit @{ action = "position"; x = ($baseX + 0.5 * ($run % 3)); y = ($baseY + 3); z = $z })
    [void](Edit @{ action = "linked"; text = "on" })
    $off = @(@(2.2, 0.4, 0.3), @(-1.7, 1.1, -0.6))
    for ($k = 0; $k -lt 2; $k++) {
        [void](Edit @{ action = "select"; text = "$($kids[$k])" })
        [void](Edit @{ action = "position"; x = ($off[$k][0] + 0.1 * $run); y = $off[$k][1]; z = $off[$k][2] })
        [void](Edit @{ action = "rotation"; x = (20 + 7 * $run); y = (-30 + 5 * $run); z = (45 - 11 * $run) })
    }
    [void](Edit @{ action = "linked"; text = "off" })
    [void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 800
    [void](Interact @{ action = "aim"; x = ($baseX + 1.0); y = ($baseY + 3); z = $z; distance = 24 }); Start-Sleep -Milliseconds 800
    # the Build window covers the left of the view: click the part that stands furthest to the right (any part picks the whole linkset)
    $pt = $null
    foreach ($id in $ids) { $q = ScreenOf $id; if ($q -and $q[0] -gt 520 -and ($null -eq $pt -or $q[0] -gt $pt[0])) { $pt = $q } }
    if (-not $pt) { $rows += [ordered]@{ run = $run; error = "the linkset is not on screen" }; continue }
    if ($run -in 3, 8) { Shot ("mirror-pre-run{0}" -f $run) }
    ClickAt $pt[0] $pt[1] $false
    $sel = EditUntil { param($s) @($s.selection).Count -ge 1 }
    $picked = @($sel.selection | ForEach-Object { [uint32]$_ })
    if ($picked.Count -lt 1) { Shot ("mirror-miss-run{0}" -f $run); $rows += [ordered]@{ run = $run; error = "the pointer did not pick the linkset"; point = $pt }; continue }
    # the axis (X, the first item) and the plane (Selection centre, the first item): open each list with the pointer, click its item with the pointer
    $opened = ClickCtl "mirror_axis"; Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "popup:mirror_axis:0"); Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "mirror_plane"); Start-Sleep -Milliseconds 400
    $opened = $opened -and (ClickCtl "popup:mirror_plane:0"); Start-Sleep -Milliseconds 400
    $now = (Edit @{ action = "mirror_world" }).done
    $plan = (Edit @{ action = "mirror_plan"; text = "x:centre" }).done
    $apart = Compare-Poses $plan $now
    $pressed = $opened -and (ClickCtl "mirror")
    $match = Wait-Until { $h = (Edit @{ action = "mirror_world" }).done; $c = Compare-Poses $plan $h; if ($c.ok) { $c } } 12
    $have = (Edit @{ action = "mirror_world" }).done
    $cmp = Compare-Poses $plan $have
    if ($cmp.maxMm -gt $worstMm) { $worstMm = $cmp.maxMm }
    if ($cmp.maxDeg -gt $worstDeg) { $worstDeg = $cmp.maxDeg }
    $moved = $apart.maxMm -ge 50
    $ok = $pressed -and [bool]$match -and $cmp.ok -and $moved -and @($plan).Count -eq 3 -and @($have).Count -eq 3
    if ($ok) { $good++ }
    $rows += [ordered]@{ run = $run; parts = @($plan).Count; start_to_plan_mm = $apart.maxMm; after_vs_plan_mm = $cmp.maxMm; after_vs_plan_deg = $cmp.maxDeg; linksets = $sel.arrange.linksets; refusal = $sel.arrange.mirror_refusal }
    Note ("run {0}: start-to-plan {1:N0} mm; after vs plan {2:N3} mm, {3:N4} deg" -f $run, $apart.maxMm, $cmp.maxMm, $cmp.maxDeg)
}
$report.runs = $rows
Shot "mirror-after"
Check "1 pointer picks the linkset, Mirror on X: every part within 1 mm and 0.1 degree of the rules' answer" ($good -eq $Runs) ("{0} of {1} runs; worst {2:N3} mm, {3:N4} deg" -f $good, $Runs, $worstMm, $worstDeg)

# ---- 2. control: nothing selected, the Mirror button is off and a press moves nothing ----
[void](Edit @{ action = "select"; text = "" }); Start-Sleep -Milliseconds 500
$beforeObjs = @(0..2 | ForEach-Object { OwnObj $locals[$_] })
[void](ClickCtl "mirror"); Start-Sleep -Milliseconds 1500
$none = Edit @{ action = "state" }
$afterObjs = @(0..2 | ForEach-Object { OwnObj $locals[$_] })
$still = $true
foreach ($b in @($beforeObjs | Where-Object { $_ })) {
    $a = @($afterObjs | Where-Object { $_ -and $_.local -eq $b.local })[0]
    if (-not $a -or -not ((Near $a.x $b.x 0.001) -and (Near $a.y $b.y 0.001) -and (Near $a.z $b.z 0.001))) { $still = $false }
}
Check "2 control: with nothing picked Mirror is refused and nothing moves" ($none.arrange.mirror_refusal -eq "toofew" -and $still) ("refusal {0}; nothing moved {1}" -f $none.arrange.mirror_refusal, $still)

# ---- 3. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "3 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "mirror-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

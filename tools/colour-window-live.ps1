# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# colour-window-live.ps1 - the Build window's colour picker window at the test window's size, driven as a hand drives it through the
# shared live driver (tools\live-driver.ps1): controls are found by name, scrolled into view or reported cut, and pressed through the
# engine's input path. On one new box:
#   1 the colour window opens from the Texture tab's colour swatch (pressed by name);
#   2 its OK and Cancel are found by name and are wholly in view as it opens, before any scrolling (no scroll area or window edge cuts them);
#   3 a click in the colour square changes every face's colour; Cancel (pressed by name) closes the window and puts each face's colour back;
#   4 opened again, a click in the square and OK (pressed by name): the window closes and every face keeps the new colour.
# Takes back the box, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\colour-window-live.ps1 -Sandbox <new folder> -Credentials <accounts file> -Account <alias>
#     -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: colour-window-report.json in the sandbox. Exit: 0 all checks
# passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up (or the export is not complete).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$GridInfoUrl,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
. (Join-Path $PSScriptRoot "live-driver.ps1")
function Build([hashtable]$b) { Invoke-LiveApi "build" $b }
function Edit([hashtable]$b) { Invoke-LiveApi "edit" $b }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($script:LdPort) { try { [void](Invoke-TakeBack -Seconds 20) } catch { } }
    Stop-LiveViewer
    break
}
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials is not a file") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Write-LiveNote ("{0,-62} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }
function Save-Report { $report.checks = $checks; $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "colour-window-report.json") -Encoding UTF8 }

if (-not (Start-LiveViewer -Sandbox $Sandbox -Exe $Exe)) { Write-LiveNote "the viewer did not come up: stopping"; Stop-LiveViewer; exit 5 }
$world = Enter-LiveWorld -Credentials $Credentials -Account $Account -LoginUri $loginUri -Start $Start
if (-not $world) { Write-LiveNote "not in the world: stopping"; Stop-LiveViewer; exit 5 }
Start-Sleep -Seconds 3

function State() { Edit @{ action = "state" } }
function Pickers() { (Edit @{ action = "pickers" }).done }
function FaceColours([uint32]$local) {
    $p = @(@((Build @{ action = "state" }).own_prims) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1
    if (-not $p) { return "" }
    (@($p.colours)[0..5] | ForEach-Object { (@($_) | ForEach-Object { [math]::Round([double]$_, 3) }) -join "," }) -join " | "
}
function ClickSquare([string]$at) { $p = (Edit @{ action = "picker_colour_point"; text = $at }).done; if ($p) { Invoke-LivePointer ([double]$p[0]) ([double]$p[1]); $true } else { $false } }
function ScreenPoint($x, $y, $z) { $p = (Edit @{ action = "screen"; x = $x; y = $y; z = $z }).done; if ($p) { @([double]$p[0], [double]$p[1]) } else { $null } }

# ---- 0. a box: Ctrl+B and a click on the ground; the tool becomes Edit and the box is selected; the Texture tab ----
$me = @($world.self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.9 }
[void](Invoke-LiveApi "interact" @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Invoke-LiveApi "input" @{ kind = "release_focus" })
if ((State).build_mode) { Invoke-LiveKey "ESC"; Start-Sleep -Milliseconds 300 }
Invoke-LiveKey "B" -Ctrl; Start-Sleep -Milliseconds 400
$gp = ScreenPoint $spot.x $spot.y $spot.z
if ($gp) { Invoke-LivePointer $gp[0] $gp[1] }
$box = Wait-LiveUntil { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $box) { Check "0 a new box" $false "none appeared"; Save-Report; Stop-LiveViewer; exit 1 }
Add-Rezzed ([uint32]$box.local); $A = [uint32]$box.local
$sel = Wait-LiveUntil { $s = State; if ($s.tool -eq "edit" -and @($s.selection) -contains $A) { $s } } 10
Check "0 a new box, selected with the Edit tool" ([bool]$sel) ("box {0}" -f $A)
[void](Edit @{ action = "control"; text = "tab:texture" }); Start-Sleep -Milliseconds 300
$tab = (Edit @{ action = "control"; text = "tab:texture" }).done
if ($tab) { Invoke-LivePointer ([double]$tab[0]) ([double]$tab[1]) }
Start-Sleep -Milliseconds 500
$c0 = Wait-LiveUntil { $c = FaceColours $A; if ($c) { $c } } 10
Write-LiveNote "each face's colour at the start: $c0"

# ---- 1-2. the colour window, and its OK and Cancel in view ----
$swatch = Invoke-LiveClick "colour"
$open1 = Wait-LiveUntil { if ((Pickers).colour) { $true } } 5
Check "1 the colour window opens from the swatch (pressed by name)" ($swatch.clicked -and [bool]$open1) $swatch.detail
# the verdict is read as the window opens, without scrolling to them first: buttons inside the scrolled picker would only be in
# view after a scroll, buttons pinned under it are in view at once
$ok = Find-LiveControl "colour_ok"; $cancel = Find-LiveControl "colour_cancel"
$report.ok_found = $ok; $report.cancel_found = $cancel
[void](Invoke-LiveApi "screenshot" @{ dir = $Sandbox; name = "colour-window-open" })
Check "2 OK and Cancel are found by name and wholly in view" ($ok.reach -eq "visible" -and $cancel.reach -eq "visible") ("OK {0} rect [{1}] shown [{2}]; Cancel {3} rect [{4}] shown [{5}]" -f $ok.reach, (@($ok.rect) -join ","), (@($ok.shown_rect) -join ","), $cancel.reach, (@($cancel.rect) -join ","), (@($cancel.shown_rect) -join ","))

# ---- 3. a colour chosen, then Cancel puts each face back ----
$sq = ClickSquare "0.8,0.2"
$c1 = Wait-LiveUntil { $c = FaceColours $A; if ($c -and $c -ne $c0) { $c } } 8
$press = Invoke-LiveClick "colour_cancel"
$closed = Wait-LiveUntil { if (-not (Pickers).colour) { $true } } 5
$back = Wait-LiveUntil { if ((FaceColours $A) -eq $c0) { $true } } 10
Check "3 a chosen colour changes every face; Cancel (by name) closes and puts each face back" ($sq -and [bool]$c1 -and $press.clicked -and [bool]$closed -and [bool]$back) ("chosen: {0}; {1}; closed {2}; faces now {3} (were {4})" -f $c1, $press.detail, [bool]$closed, (FaceColours $A), $c0)

# ---- 4. opened again, a colour chosen, OK keeps it ----
[void](Invoke-LiveClick "colour"); [void](Wait-LiveUntil { if ((Pickers).colour) { $true } } 5)
[void](ClickSquare "0.3,0.4")
$c2 = Wait-LiveUntil { $c = FaceColours $A; if ($c -and $c -ne $c0) { $c } } 8
$press2 = Invoke-LiveClick "colour_ok"
$closed2 = Wait-LiveUntil { if (-not (Pickers).colour) { $true } } 5
Start-Sleep -Seconds 3
$kept = FaceColours $A
Check "4 a chosen colour, then OK (by name): the window closes and every face keeps it" ([bool]$c2 -and $press2.clicked -and [bool]$closed2 -and $kept -eq $c2) ("chosen: {0}; {1}; closed {2}; faces 3 s later {3}" -f $c2, $press2.detail, [bool]$closed2, $kept)

# ---- take back, log out ----
[void](Invoke-LiveApi "input" @{ kind = "release_focus" }); Invoke-LiveKey "ESC"; Start-Sleep -Milliseconds 300
[void](Invoke-LiveApi "interact" @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "5 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
Stop-LiveViewer
$report.completed = $true
Save-Report
$failed = @($checks | Where-Object { -not $_.pass }).Count
Write-LiveNote ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })

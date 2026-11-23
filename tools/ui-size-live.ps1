# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# ui-size-live.ps1 - the interface size chosen in Preferences, on the login screen, by the engine's input path (no value is set by
# code): the development export starts with the automation on, the window is set to 1152x648, and every step below is a pointer or key
# event sent to the real window, as the operating system's events arrive. No grid, region or account is used.
#   1 open        the gear button opens Preferences; the Interface category shows its page
#   2 choose      each size (-Sizes) is chosen with the size control: the drop-down "scale_dropdown" (opened by a click, the entry taken by
#                 the keyboard); a build that still has the slider "scale" gets it dragged from its 100 % position to where the size lay
#                 on the slider as it stood when the press began (this is how the slider's jumping under the pointer showed)
#   3 layout      after each choice Preferences is still whole: the size control, Cancel and OK are wholly in view, and below the window
#                 width limit the footer drop-down "categories_dropdown" stands in for the category list (above it the list); when the
#                 layout switches, the keyboard focus is on the category control now showing
#   4 page        the footer drop-down switches page by keyboard and the page changes; the size drop-down is reachable again afterwards
#   5 cancel, ok  Cancel puts back the size in force when Preferences opened (the gear's place on the screen is the same again, and
#                 Preferences opened again reads the old size); OK keeps the size chosen
# Output: one PASS or FAIL line per check, and a summary. Exit 0 all passed; 1 a check failed; 3 the viewer could not be started.
#
#   powershell -NoProfile -File <repo>\tools\ui-size-live.ps1 -Sandbox <new evidence folder> [-Exe <development export>] [-Sizes 125,200]
# The caller holds a lane claim (tools\claim-guard.ps1).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",   # default: the development export, host\Viewer.Godot\build\dev\Reverie.console.exe
    [int[]]$Sizes = @(125, 150, 175, 100, 200),
    [int]$Width = 1152, [int]$Height = 648,
    [int]$WideWindow = 850   # the logical width below which Preferences uses the footer drop-down (PreferencesPanel.WideWindow)
)
$ErrorActionPreference = "Stop"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
. (Join-Path $PSScriptRoot "live-driver.ps1")
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$report = Join-Path $Sandbox "ui-size.txt"
$script:Fails = 0; $script:Checks = 0
function Say([string]$line) { Write-LiveNote $line; Add-Content -LiteralPath $report -Value $line -Encoding utf8 }
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:Checks++; if (-not $ok) { $script:Fails++ }
    Say ("{0}  {1}  {2}" -f ($(if ($ok) { "PASS" } else { "FAIL" })), $name, $detail)
}

Add-Type @"
using System; using System.Runtime.InteropServices;
public class UiSizeWin { [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rt,B; }
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hgt, uint f); }
"@
[void][UiSizeWin]::SetProcessDPIAware()

function Set-ViewerWindow {
    $p = Wait-LiveUntil { Get-Process -Name "Reverie.console", "Reverie" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 } 30
    if (-not $p) { return "no viewer window" }
    $h = $p.MainWindowHandle
    [void][UiSizeWin]::ShowWindow($h, 9)   # restore: a maximized window cannot be given a size
    Start-Sleep -Milliseconds 700
    $r = New-Object UiSizeWin+R; $c = New-Object UiSizeWin+R
    [void][UiSizeWin]::GetWindowRect($h, [ref]$r); [void][UiSizeWin]::GetClientRect($h, [ref]$c)
    $dx = ($r.Rt - $r.L) - ($c.Rt - $c.L); $dy = ($r.B - $r.T) - ($c.B - $c.T)
    [void][UiSizeWin]::SetWindowPos($h, [IntPtr]::Zero, 100, 60, $Width + $dx, $Height + $dy, 0x0044)
    Start-Sleep -Seconds 2
    [void][UiSizeWin]::GetClientRect($h, [ref]$c)
    return "client $($c.Rt - $c.L)x$($c.B - $c.T)"
}

function Shown([string]$name) { $f = Find-LiveControl $name; [bool]($f -and $f.found -and $f.shown) }
function Control-Text([string]$name) { $f = Find-LiveControl $name; if ($f -and $f.found -and $f.shown) { [string]$f.text } else { $null } }
function Control-Whole([string]$name) { $f = Show-LiveControl $name; [bool]($f -and $f.found -and $f.shown -and $f.reach -eq "visible") }
function Scale-Now {
    $t = Control-Text "scale_dropdown"; if (-not $t) { $t = Control-Text "scale_value" }
    if ($t -match '(\d+)') { [int]$Matches[1] } else { $null }
}
function Focus-Name { $o = Invoke-LiveApi "input" @{ kind = "state" }; if ($o) { [string]$o.focus_name } else { "" } }
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y) {
    [void](Invoke-LiveApi "input" @{ kind = "mouse"; button = $button; down = $down; shift = $false; ctrl = $false; alt = $false; x = $x; y = $y })
}
function Wide-Layout { [bool](Shown "category_interface") }
# a drop-down opened by a click and one entry taken by the keyboard: the list opens with no entry lit, so Down once per entry counted from
# the top, then Enter
function Pick-Entry([string]$name, [int]$index) {
    [void](Invoke-LiveClick $name); Start-Sleep -Milliseconds 400
    for ($i = 0; $i -le $index; $i++) { Invoke-LiveKey "DOWN"; Start-Sleep -Milliseconds 150 }
    Invoke-LiveKey "ENTER"; Start-Sleep -Milliseconds 800
}

# choose a size with the size control the build has; the answer says how it was done
function Choose-Size([int]$percent) {
    if (Shown "scale_dropdown") {
        [void](Show-LiveControl "scale_dropdown")
        Pick-Entry "scale_dropdown" ([array]::IndexOf(@(100, 125, 150, 175, 200), $percent))
        return "drop-down opened by a click, entry taken by the keyboard"
    }
    $s = Show-LiveControl "scale"
    if (-not ($s -and $s.found)) { return "no size control" }
    $x0 = [double]$s.rect[0]; $w = [double]$s.rect[2]; $y = [double]$s.rect[1] + [double]$s.rect[3] / 2
    $at = { param($v) $x0 + ($v - 75) / 325.0 * $w }   # a value's place on the slider as it stands when the press begins (75 to 400)
    $from = Scale-Now
    $start = & $at $from; $target = & $at $percent
    Mouse "none" $false $start $y; Start-Sleep -Milliseconds 100
    Mouse "left" $true $start $y; Start-Sleep -Milliseconds 100
    $trail = New-Object System.Collections.Generic.List[string]
    for ($i = 1; $i -le 12; $i++) {
        Mouse "none" $false ($start + ($target - $start) * $i / 12) $y; Start-Sleep -Milliseconds 150
        $trail.Add([string](Scale-Now))
    }
    Mouse "left" $false $target $y; Start-Sleep -Milliseconds 500
    return "slider dragged from $from % to the point where $percent % lay when the press began; value after each of 12 moves: $($trail -join ' ')"
}

function Open-Interface-Page {
    if (-not (Shown "cancel")) { [void](Invoke-LiveClick "preferences") }
    $up = Wait-LiveUntil { if (Shown "cancel") { $true } } 8
    if (-not $up) { return $false }
    if (Wide-Layout) { [void](Invoke-LiveClick "category_interface") }
    else {
        Pick-Entry "categories_dropdown" 5   # Interface is the sixth category
    }
    return [bool](Wait-LiveUntil { if ($null -ne (Scale-Now)) { $true } } 6)
}

Say "ui-size-live: exe $Exe, window ${Width}x${Height}, sizes $($Sizes -join ',')"
if (-not (Start-LiveViewer -Sandbox $Sandbox -Exe $Exe)) { Say "NOT STARTED: the viewer or its automation did not come up"; exit 3 }
try {
    Say ("window: " + (Set-ViewerWindow))
    $login = Wait-LiveUntil { if (Control-Whole "preferences") { $true } } 40
    Check "1 open: the login screen shows the Preferences gear" ([bool]$login) "gear named 'preferences'"
    if (-not $login) { throw "no login screen" }
    $gear0 = (Find-LiveControl "preferences").rect[0]
    Check "1 open: the gear opens Preferences and the Interface page shows its size control" (Open-Interface-Page) "size in force $(Scale-Now) %"
    $opened = Scale-Now
    $wasWide = Wide-Layout
    foreach ($size in $Sizes) {
        $how = Choose-Size $size
        $got = Scale-Now
        Check "2 choose ${size}: the size in force is $size %" ($got -eq $size) "got $got; $how"
        Start-Sleep -Milliseconds 600
        [void](Invoke-LiveApi "screenshot" @{ dir = [IO.Path]::GetFullPath($Sandbox); name = "prefs-$size" })   # the window as drawn, to look at
        $whole = @("cancel", "save") | Where-Object { -not (Control-Whole $_) }
        $sizeName = if (Shown "scale_dropdown") { "scale_dropdown" } else { "scale" }
        $sizeWhole = ((Find-LiveControl $sizeName).reach -eq "visible")   # as it stands, with no scrolling by the check
        Check "3 layout ${size}: Preferences stays on screen, the size control, Cancel and OK whole" ($whole.Count -eq 0 -and $sizeWhole) "not whole: $($whole -join ',') ; size control whole: $sizeWhole"
        $logical = [math]::Round($Width * 100.0 / $size)
        $wantList = $logical + 0.5 -ge $WideWindow
        $isList = Wide-Layout; $isDrop = Shown "categories_dropdown"
        Check "3 layout ${size}: logical width $logical, $(if ($wantList) { 'category list' } else { 'footer drop-down' }) stands in" ($isList -eq $wantList -and $isDrop -ne $wantList) "list shown $isList, drop-down shown $isDrop"
        if ($isList -ne $wasWide) {
            $focus = Focus-Name
            $wantFocus = if ($isList) { "category_interface" } else { "categories_dropdown" }
            Check "3 layout ${size}: the layout switched, the keyboard focus is on the category control now showing" ($focus -eq $wantFocus) "focus on '$focus', expected '$wantFocus'"
        }
        $wasWide = $isList
    }
    # 4: a page chosen through the footer drop-down (or the list), and the size control reachable again
    $last = $Sizes[-1]
    if (-not (Shown "categories_dropdown")) { Say "SKIP  4 page: the last size chosen leaves the category list showing, so the footer drop-down is not exercised" }
    if (Shown "categories_dropdown") {
        Pick-Entry "categories_dropdown" 2   # Sound
        $leftPage = -not ((Shown "scale_dropdown") -or (Shown "scale"))
        Check "4 page: a page chosen by the footer drop-down shows (the Interface page is gone)" $leftPage "drop-down now reads '$(Control-Text 'categories_dropdown')'"
        Check "4 page: Cancel and OK stay whole on that page" ((Control-Whole "cancel") -and (Control-Whole "save")) ""
        Pick-Entry "categories_dropdown" 5
        Check "4 page: back on the Interface page the size control is reachable" ($null -ne (Scale-Now)) "size $(Scale-Now) %"
    }
    # 5: Cancel puts the size back, OK keeps one
    [void](Choose-Size 175); Start-Sleep -Milliseconds 400
    Check "5 cancel: before Cancel the size in force is 175 % (not the one Preferences opened with)" ((Scale-Now) -eq 175 -and 175 -ne $opened) "reads $(Scale-Now) %, opened with $opened %"
    [void](Invoke-LiveClick "cancel"); Start-Sleep -Milliseconds 900
    $gear1 = (Find-LiveControl "preferences").rect[0]
    Check "5 cancel: the interface is back at the size in force when Preferences opened ($opened %)" ([math]::Abs($gear1 - $gear0) -lt 0.6) "gear at x $gear1, was $gear0 before any change"
    Check "5 cancel: Preferences opened again reads $opened %" ((Open-Interface-Page) -and (Scale-Now) -eq $opened) "reads $(Scale-Now) %"
    [void](Choose-Size 150); Start-Sleep -Milliseconds 400
    Check "5 ok: before OK the size in force is 150 %" ((Scale-Now) -eq 150) "reads $(Scale-Now) %"
    [void](Invoke-LiveClick "save"); Start-Sleep -Milliseconds 900
    $stillOpen = Shown "cancel"
    Say "ok: Preferences window after OK is $(if ($stillOpen) { 'still open' } else { 'closed' })"
    $ok = (Open-Interface-Page) -and (Scale-Now) -eq 150
    Check "5 ok: the size chosen stays after OK, and Preferences opened again reads 150 %" $ok "reads $(Scale-Now) %"
}
catch { Check "run" $false "$_" }
finally { Stop-LiveViewer; Say "summary: $script:Checks checks, $script:Fails failed" }
exit $(if ($script:Fails -gt 0) { 1 } else { 0 })

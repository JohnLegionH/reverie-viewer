# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# window-check.ps1 - the check that a test or measuring run's viewer window stays out of the way of the person at the machine.
#
# It opens a stand-in window of its own, small, in front, on a monitor, in place of the window somebody is working in, and starts
# real runs of the exported viewer through tools\godot.ps1 while it watches. For each run it checks, every 200 ms while the run goes:
#   - the foreground window is still the stand-in (the viewer never took focus);
#   - the viewer's window is never always-on-top;
# and once the viewer's window is up and settled:
#   - it is on the stand-in's monitor, about half that monitor's work area wide and high (a quarter of its area), in the bottom-right corner;
#   - it is below the stand-in in the stacking order, and below every other visible application window.
# Two runs are made per backend:
#   launch    a camera-path render run (the kind every render case is): the checks above.
#   measure   a busy-view measuring run (-MeasureRun, --tr-pacing-guard) whose window is minimised once, 3 s in: the viewer must bring it back
#             without focus and below the stand-in, the run must pass, and the result must name the frames it set aside
#             (frame_pacing.marked), with fewer than a tenth of them marked.
# A run that cannot be checked (the stand-in cannot be brought to the front, no viewer window appears) is a FAIL, never a pass.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\window-check.ps1 -OutDir <dir> [-Build <dir>] [-Backends vulkan,d3d12]
# Needs the exported build (tools\s1.ps1 makes it). Exit 0 when every check passes, 1 otherwise.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$Build = "",
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$DiagDir = "",
    [string[]]$Backends = @("vulkan", "d3d12"),
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
. (Join-Path $PSScriptRoot "own-processes.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Build) { $Build = Join-Path $repo "host\Viewer.Godot\build" }
if (-not $DiagDir) { $DiagDir = Join-Path $OutDir "diag" }
New-Item -ItemType Directory -Force -Path $OutDir, $DiagDir | Out-Null
$godot = Join-Path $PSScriptRoot "godot.ps1"
$exe = Join-Path $Build "Reverie.console.exe"
if (-not (Test-Path -LiteralPath $exe)) { Write-Output "FAIL  window-check: the exported build is missing: $exe"; exit 1 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NvWc
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder t, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder t, int n);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public const uint GwHwndNext = 2;
    public const int GwlExStyle = -20;
    public const int WsExTopmost = 0x8, WsExToolWindow = 0x80;

    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static string Class(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }
    public static uint PidOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }

    /// <summary>The visible top-level windows of the given processes.</summary>
    public static IntPtr[] WindowsOf(int[] pids)
    {
        var mine = new HashSet<uint>(); foreach (int id in pids) mine.Add((uint)id);
        var found = new List<IntPtr>();
        EnumWindows((h, l) => { if (mine.Contains(PidOf(h)) && IsWindowVisible(h)) found.Add(h); return true; }, IntPtr.Zero);
        return found.ToArray();
    }

    /// <summary>The top-level windows in stacking order, top first.</summary>
    public static IntPtr[] ZOrder()
    {
        var all = new List<IntPtr>();
        EnumWindows((h, l) => { all.Add(h); return true; }, IntPtr.Zero);   // EnumWindows walks top to bottom
        return all.ToArray();
    }

    /// <summary>Tries the usual ways of making a window the foreground window of a session that was not the last to get input.</summary>
    public static bool BringToFront(IntPtr h)
    {
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return true;
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // an Alt press and release lifts the foreground lock
        SetForegroundWindow(h);
        return GetForegroundWindow() == h;
    }
}
"@
[void][NvWc]::SetProcessDPIAware()

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Output "  ok    $what" } else { Write-Output "  FAIL  $what"; $failures.Add($what) }
}

# --- the stand-in window ------------------------------------------------------------------------------------------------
$screens = @([System.Windows.Forms.Screen]::AllScreens)
$screen = ($screens | Where-Object { -not $_.Primary } | Select-Object -First 1)
if (-not $screen) { $screen = $screens[0] }
$form = New-Object System.Windows.Forms.Form
$form.Text = "window-check stand-in"
$form.StartPosition = "Manual"
$form.Location = New-Object System.Drawing.Point ($screen.WorkingArea.X + 120), ($screen.WorkingArea.Y + 120)
$form.Size = New-Object System.Drawing.Size 640, 360
$form.ShowInTaskbar = $false
$form.Show()
for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
$standIn = $form.Handle
if (-not [NvWc]::BringToFront($standIn)) {
    $form.Close()
    Write-Output "FAIL  window-check: the stand-in window could not be brought to the front, so no run can be judged"
    exit 1
}
$monitor = [System.Windows.Forms.Screen]::FromHandle($standIn)
$work = $monitor.WorkingArea
Write-Output ("stand-in 0x{0:X} on monitor {1} (work area {2}x{3} at {4},{5})" -f [int64]$standIn, $monitor.DeviceName, $work.Width, $work.Height, $work.X, $work.Y)

function Pump([int]$ms) { $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } }

function Get-TreeIds([int]$rootId) {
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($rootId)
    $procs = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $procs) { if ([int]$c.ParentProcessId -eq $ids[$i]) { $ids.Add([int]$c.ProcessId) } } }
    return $ids.ToArray()
}

# One run through the launcher while the stand-in is in front; returns what was seen.
function Invoke-Watched([string]$name, [string]$argLine, [bool]$measuring, [int]$settleAfterMs, [scriptblock]$during) {
    $log = Join-Path $OutDir "$name.log"
    if (-not [NvWc]::BringToFront($standIn)) { Check $false "${name}: the stand-in could not be brought back to the front before the run" }   # the person may have switched windows since the last run
    Pump 300
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command powershell.exe).Source
    $measureArg = if ($measuring) { " -MeasureRun" } else { "" }
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$godot`" -DiagDir `"$DiagDir`" -Exe `"$exe`" -AppDataDir `"$(Join-Path $Build 'appdata')`" -TimeoutSeconds $TimeoutSeconds -LogFile `"$log`"$measureArg -ArgLine `"$($argLine -replace '"', '\"')`""
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $started = Get-Date
    $run = [System.Diagnostics.Process]::Start($psi)
    $null = $run.StandardOutput.ReadToEndAsync(); $null = $run.StandardError.ReadToEndAsync()
    $r = [ordered]@{ Foreground = 0; ForegroundOther = 0; ForegroundViewer = 0; ViewerFg = @(); Topmost = 0; Samples = 0; Window = $null; WindowSeenAt = $null; Pids = @(); Layout = $null; Exit = $null; Tops = @() }
    $viewerPids = @()
    $lastLayout = [DateTime]::MinValue; $lastTree = [DateTime]::MinValue
    while (-not $run.HasExited -and ((Get-Date) - $started).TotalSeconds -lt ($TimeoutSeconds + 30)) {
        Pump 200
        $r.Samples++
        # the tree of the run's processes, re-read about once a second (a window's owner is the viewer's own process)
        if (((Get-Date) - $lastTree).TotalMilliseconds -ge 1000) { $lastTree = Get-Date; $viewerPids = @(Get-TreeIds $run.Id) }
        $fg = [NvWc]::GetForegroundWindow()
        if ($fg -eq $standIn) { $r.Foreground++ }
        else {
            $r.ForegroundOther++
            if ($viewerPids -contains [int][NvWc]::PidOf($fg)) {   # the run's own window took the foreground
                $r.ForegroundViewer++
                $r.ViewerFg += ("t={0:N1}s {1} '{2}'" -f ((Get-Date) - $started).TotalSeconds, [NvWc]::Class($fg), [NvWc]::Text($fg))
            }
            if ($r.Tops.Count -lt 3) { $r.Tops += ("0x{0:X} {1}" -f [int64]$fg, [NvWc]::Text($fg)) }
        }
        if (-not $r.Window -and ((Get-Date) - $started).TotalSeconds -gt 2) {
            $w = @([NvWc]::WindowsOf($viewerPids) | Where-Object { [NvWc]::Class($_) -eq "Engine" -or [NvWc]::Text($_) -match "Viewer|Godot" } | Select-Object -First 1)
            if ($w.Count -gt 0) { $r.Window = $w[0]; $r.WindowSeenAt = (Get-Date) }
        }
        if ($r.Window) {
            $ex = [NvWc]::GetWindowLong($r.Window, [NvWc]::GwlExStyle)
            if (($ex -band [NvWc]::WsExTopmost) -ne 0) { $r.Topmost++ }
            # the layout is re-read about once a second once the window has settled, so the last reading is the window as the run ended
            if (((Get-Date) - $r.WindowSeenAt).TotalMilliseconds -ge $settleAfterMs -and ((Get-Date) - $lastLayout).TotalMilliseconds -ge 1000 -and -not [NvWc]::IsIconic($r.Window)) {
                $lastLayout = Get-Date
                $l = Get-Layout $r.Window
                if ($l) { $r.Layout = $l }
            }
            if ($during) { & $during $r }
        }
    }
    if (-not $run.HasExited) { Stop-OwnedTree $run.Id | Out-Null; $r.Exit = -1 } else { $r.Exit = $run.ExitCode }
    $r.Pids = $viewerPids
    return $r
}

function Get-Layout([IntPtr]$h) {
    $rect = New-Object NvWc+Rect
    if (-not [NvWc]::GetWindowRect($h, [ref]$rect) -or ($rect.Right - $rect.Left) -le 0) { return $null }
    $z = [NvWc]::ZOrder()
    $iStand = [array]::IndexOf($z, $standIn); $iView = [array]::IndexOf($z, $h)
    # visible, titled, non-minimised top-level windows of other applications that are below the viewer's window
    $below = @()
    if ($iView -ge 0) {
        for ($i = $iView + 1; $i -lt $z.Length; $i++) {
            $w = $z[$i]
            if (-not [NvWc]::IsWindowVisible($w) -or [NvWc]::IsIconic($w)) { continue }
            $title = [NvWc]::Text($w); $cls = [NvWc]::Class($w)
            if (-not $title -or $cls -in @("Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow")) { continue }
            if (([NvWc]::GetWindowLong($w, [NvWc]::GwlExStyle) -band [NvWc]::WsExToolWindow) -ne 0) { continue }
            $below += "$title [$cls]"
        }
    }
    return [pscustomobject]@{ Left = $rect.Left; Top = $rect.Top; Right = $rect.Right; Bottom = $rect.Bottom; Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top
        BelowStandIn = ($iView -gt $iStand -and $iStand -ge 0); ZIndexViewer = $iView; ZIndexStandIn = $iStand; BelowViewer = $below }
}

function Check-Run([string]$name, $r) {
    Check ($r.Samples -ge 5) "${name}: watched for $($r.Samples) samples"
    Check ($null -ne $r.Window) "${name}: the viewer's window was found"
    Check ($r.ForegroundViewer -eq 0) "${name}: no window of the run was ever the foreground window ($($r.ViewerFg -join ' | ') $($r.Samples) samples; the stand-in was in front in $($r.Foreground), another program's window in $($r.ForegroundOther - $r.ForegroundViewer): $($r.Tops -join '; '))"
    Check ($r.Topmost -eq 0) "${name}: the viewer's window was never always-on-top"
}

function Check-Layout([string]$name, $l) {
    if (-not $l) { Check $false "${name}: the window's layout was read once it had settled"; return }
    $tol = 24   # the window's invisible resize border
    $title = 80 # and its title bar (the engine is asked for the size of the drawing area; the frame comes on top)
    $halfW = [math]::Floor($work.Width / 2); $halfH = [math]::Floor($work.Height / 2)
    Check ($l.Width -ge $halfW - $tol -and $l.Width -le $halfW + $tol -and $l.Height -ge $halfH - $tol -and $l.Height -le $halfH + $title) "${name}: about a quarter of the monitor ($($l.Width)x$($l.Height) with its frame, of a $($work.Width)x$($work.Height) work area; half is ${halfW}x${halfH})"
    Check ([math]::Abs($l.Right - $work.Right) -le $tol -and [math]::Abs($l.Bottom - $work.Bottom) -le $tol) "${name}: in the bottom-right corner of the stand-in's monitor (right edge $($l.Right) of $($work.Right), bottom $($l.Bottom) of $($work.Bottom))"
    Check ($l.BelowStandIn) "${name}: below the stand-in in the stacking order (viewer at $($l.ZIndexViewer), stand-in at $($l.ZIndexStandIn))"
    Check ($l.BelowViewer.Count -eq 0) "${name}: below every other visible application window ($(@($l.BelowViewer).Count) are below it: $((@($l.BelowViewer) | Select-Object -First 3) -join '; '))"
}

$fixtureBusy = Join-Path $repo "fixtures\materials\projector.matzoo.json"
foreach ($be in $Backends) {
    Write-Output "== $be"
    # launch: a camera-path render run, the kind every render case is
    $tag = "wc-launch-$be"
    $r = Invoke-Watched $tag "--rendering-driver $be -- --test-render `"--tr-out=$OutDir`" --tr-tag=$tag --tr-fixture=synthetic-heavy --tr-path-seconds=10 --tr-seed=8 --tr-budget=2048m" $false 4000 $null
    Check-Run $tag $r
    Check-Layout $tag $r.Layout
    Check ($r.Exit -eq 0) "${tag}: the run passed (exit $($r.Exit))"

    # measure: a busy-view measuring run whose window is minimised once
    $tag = "wc-measure-$be"
    $json = Join-Path $OutDir "$tag.json"
    $afterRestore = $null
    $r = Invoke-Watched $tag "--rendering-driver $be -- --test-render `"--tr-out=$OutDir`" --tr-tag=$tag `"--tr-fixture=$fixtureBusy`" --tr-busy=objects:2000,glow:10,lights:20,projectors:4 --tr-busy-seconds=14 --tr-seed=8 --tr-size=1920x1080 --tr-minimize-at=3" $true 3000 $null
    Check-Run $tag $r
    Check-Layout $tag $r.Layout   # the last reading before the run ended, after the minimise and the restore
    Check ($r.Exit -eq 0) "${tag}: the run passed (exit $($r.Exit))"
    if (Test-Path -LiteralPath $json) {
        $j = Get-Content -Raw -LiteralPath $json | ConvertFrom-Json
        $k = $j.window_keeper
        Check ($k -and $k.minimized_seen -ge 1) "${tag}: the viewer found its window minimised and brought it back (seen $($k.minimized_seen) time(s))"
        $m = $j.busy.counts; $fp = $null
        $fp = $j.frame_pacing
        Check ($fp -and $fp.marked.frames -ge 1) "${tag}: frame_pacing names the frames it set aside ($($fp.marked.frames) of $($fp.marked.of))"
        Check ($fp -and $fp.marked.share -le 0.10 -and -not $fp.marked.not_drawing) "${tag}: fewer than a tenth of the frames were marked ($($fp.marked.share))"
        Check ($j.verdict -eq "PASS") "${tag}: verdict $($j.verdict)"
    } else { Check $false "${tag}: the run left a result" }
}

$form.Close()
if ($failures.Count -gt 0) { Write-Output "window-check: FAIL ($($failures.Count) checks)"; exit 1 }
Write-Output "window-check: PASS"
exit 0

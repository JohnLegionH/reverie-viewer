# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# window-record.ps1 - the check that no window a check opens is seen or takes the foreground.
#
# Runs beside the checks (the .NET suite, S-1, render runs, the stock smoke) and watches, without opening any window of its own:
# every -IntervalMs it notes the foreground window and every visible top-level window of the programs the checks started (the
# viewer and the engine, recognised by their path starting with one of -PathPrefix: the clone or worktree the checks run from).
# Each sample is one line of <Out> (tab-separated): the time, the foreground window's process, whether that is a run program,
# and each run window (process, shown or minimised, its rectangle, and ON when it meets a monitor).
#
# It stops when -StopFile appears or after -Seconds, prints a summary and exits:
#   0  no sample had a run window on a monitor and none had a run program in front;
#   1  at least one did (the summary names the first such samples);
#   2  it could not watch (bad arguments).
# A minimised window is on no monitor (a test window opens minimised and its launcher brings it back off every monitor).
#
# Usage: powershell -NoProfile -File <repo>\tools\window-record.ps1 -Out <file.tsv> -PathPrefix <folder>[,<folder>] [-StopFile <file>] [-Seconds 7200] [-IntervalMs 250]
# It starts no program, so it takes no claim of its own.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string[]]$PathPrefix,
    [string]$StopFile = "",
    [int]$Seconds = 7200,
    [int]$IntervalMs = 250,
    [int]$Samples = 0,   # stub (red)
    [switch]$Replay   # stub (red)
)
$ErrorActionPreference = "Stop"
$prefixes = @($PathPrefix | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' })
if ($prefixes.Count -eq 0 -or $Seconds -lt 1 -or $IntervalMs -lt 10) { Write-Output "window-record: give -PathPrefix, -Seconds of at least 1 and -IntervalMs of at least 10"; exit 2 }
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NvWr
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L, T, R, B; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect r, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    /// <summary>The visible top-level windows of the given processes, "pid:shown|minimised:L,T,R,B:ON|off" joined by spaces;
    /// <paramref name="onMonitor"/> is set when one that is not minimised meets a monitor.</summary>
    public static string Windows(int[] pids, out bool onMonitor)
    {
        var mine = new HashSet<uint>();
        foreach (int p in pids) mine.Add((uint)p);
        var sb = new StringBuilder();
        bool on = false;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (mine.Contains(pid) && IsWindowVisible(h))
            {
                bool iconic = IsIconic(h);
                Rect r; GetWindowRect(h, out r);
                bool meets = !iconic && MonitorFromRect(ref r, 0) != IntPtr.Zero;   // MONITOR_DEFAULTTONULL: null when it meets none
                if (meets) on = true;
                sb.Append(pid).Append(':').Append(iconic ? "minimised" : "shown").Append(':').Append(r.L).Append(',').Append(r.T).Append(',').Append(r.R).Append(',').Append(r.B).Append(':').Append(meets ? "ON" : "off").Append(' ');
            }
            return true;
        }, IntPtr.Zero);
        onMonitor = on;
        return sb.ToString().Trim();
    }
}
"@
[void][NvWr]::SetProcessDPIAware()   # physical pixels, as the engine's windows use
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
"utc`tfg_pid`tfg_name`tfg_is_run`trun_on_monitor`trun_windows" | Set-Content -LiteralPath $Out -Encoding utf8
$end = (Get-Date).AddSeconds($Seconds)
$samples = 0; $bad = New-Object System.Collections.Generic.List[string]; $runSeen = 0
while ((Get-Date) -lt $end) {
    if ($StopFile -and (Test-Path -LiteralPath $StopFile)) { break }
    $runs = @(Get-CimInstance Win32_Process -Property ProcessId, ExecutablePath -ErrorAction SilentlyContinue | Where-Object {
        $path = $_.ExecutablePath
        $path -and @($prefixes | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 -and ([IO.Path]::GetExtension($path) -eq ".exe")
    } | ForEach-Object { [int]$_.ProcessId })
    $fg = [NvWr]::GetForegroundWindow(); $fpid = 0; [void][NvWr]::GetWindowThreadProcessId($fg, [ref]$fpid)
    $fname = ""; try { $fname = (Get-Process -Id $fpid -ErrorAction Stop).ProcessName } catch { }
    $on = $false
    $wins = if ($runs.Count) { [NvWr]::Windows([int[]]$runs, [ref]$on) } else { "" }
    $fgRun = $runs -contains [int]$fpid
    if ($wins) { $runSeen++ }
    $line = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f (Get-Date).ToUniversalTime().ToString("HH:mm:ss.fff"), $fpid, $fname, $fgRun, $on, $wins
    Add-Content -LiteralPath $Out -Value $line -Encoding utf8
    $samples++
    if ($on -or $fgRun) { $bad.Add($line) }
    Start-Sleep -Milliseconds $IntervalMs
}
Write-Output ("window-record: {0} samples, {1} with a run window up, {2} with a run window on a monitor or in front" -f $samples, $runSeen, $bad.Count)
foreach ($b in ($bad | Select-Object -First 10)) { Write-Output "  $b" }
if ($bad.Count -gt 0) { exit 1 }
exit 0

# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# window-record.ps1 - the check that no window a check opens is seen or takes the foreground.
#
# Runs beside the checks (the .NET suite, S-1, render runs, the stock smoke) and watches, without opening any window of its own:
# every -IntervalMs it notes the foreground window and every visible top-level window of the programs the checks started (the
# viewer and the engine, recognised by their path starting with one of -PathPrefix: the clone or worktree the checks run from).
# Each sample is one line of <Out> (tab-separated): the time, the foreground window's process, whether that is a run program,
# each run window (process, shown or minimised, its rectangle, and ON when it meets a monitor), and whether Windows was locked.
#
# Windows locked: the lock screen (LockApp) or the sign-in screen (LogonUI) is in front, or the desktop that takes input is not the
# user's own (the secure desktop). While locked, Windows itself may lay windows that sit off every monitor over a whole monitor; such
# samples are counted apart and named in the summary, and do not fail the run.
#
# It stops when -StopFile appears, after -Samples samples (when given), or after -Seconds, prints a summary and exits:
#   0  no sample taken while Windows was unlocked had a run window on a monitor or a run program in front;
#   1  at least one did (the summary names the first such samples);
#   2  it could not watch (bad arguments).
# A minimised window is on no monitor (a test window opens minimised and its launcher brings it back off every monitor).
# -Replay reads an earlier recording at <Out> (with or without the locked column: without it, the program in front tells) and prints
# the same summary and exit code, watching nothing.
#
# Usage: powershell -NoProfile -File <repo>\tools\window-record.ps1 -Out <file.tsv> -PathPrefix <folder>[,<folder>] [-StopFile <file>] [-Seconds 7200] [-IntervalMs 250] [-Samples <n>]
#        powershell -NoProfile -File <repo>\tools\window-record.ps1 -Out <earlier file.tsv> -Replay
# It starts no program, so it takes no claim of its own.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string[]]$PathPrefix = @(),
    [string]$StopFile = "",
    [int]$Seconds = 7200,
    [int]$IntervalMs = 250,
    [int]$Samples = 0,   # stop after this many samples (0: no count limit)
    [switch]$Replay
)
$ErrorActionPreference = "Stop"
$lockScreens = @("LockApp", "LogonUI")

# one sample's verdict: in the clear and failing, taken while locked (counted apart), or clean
$script:sampleCount = 0; $script:runSeen = 0; $script:locked = 0
$bad = New-Object System.Collections.Generic.List[string]; $lockedBad = New-Object System.Collections.Generic.List[string]
function Count-Sample([string]$line, [bool]$on, [bool]$fgRun, [bool]$hasWindows, [bool]$isLocked) {
    $script:sampleCount++
    if ($hasWindows) { $script:runSeen++ }
    if ($isLocked) { $script:locked++ }
    if ($on -or $fgRun) { if ($isLocked) { $lockedBad.Add($line) } else { $bad.Add($line) } }
}
function Write-Summary {
    Write-Output ("window-record: {0} samples, {1} with a run window up, {2} with a run window on a monitor or in front, {3} taken while Windows was locked ({4} of them with a run window on a monitor or in front, not counted)" -f $script:sampleCount, $script:runSeen, $bad.Count, $script:locked, $lockedBad.Count)
    foreach ($b in ($bad | Select-Object -First 10)) { Write-Output "  $b" }
    foreach ($b in ($lockedBad | Select-Object -First 3)) { Write-Output "  while locked: $b" }
    if ($bad.Count -gt 0) { exit 1 }
    exit 0
}

if ($Replay) {
    if (-not (Test-Path -LiteralPath $Out -PathType Leaf)) { Write-Output "window-record: -Replay needs -Out to name an earlier recording"; exit 2 }
    $rows = @(Get-Content -LiteralPath $Out -Encoding utf8)
    $hasLockedColumn = $rows.Count -gt 0 -and ($rows[0].TrimStart([char]0xFEFF) -split "`t") -contains "locked"
    foreach ($row in ($rows | Select-Object -Skip 1)) {
        if (-not $row.Trim()) { continue }
        $c = $row -split "`t"
        $isLocked = if ($hasLockedColumn -and $c.Count -gt 6) { $c[6] -eq "True" } else { $lockScreens -contains $c[2] }
        Count-Sample $row ($c[4] -eq "True") ($c[3] -eq "True") ([bool]($c.Count -gt 5 -and $c[5].Trim())) $isLocked
    }
    Write-Summary
}

$prefixes = @($PathPrefix | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' })
if ($prefixes.Count -eq 0 -or $Seconds -lt 1 -or $IntervalMs -lt 10 -or $Samples -lt 0) { Write-Output "window-record: give -PathPrefix, -Seconds of at least 1, -IntervalMs of at least 10 and -Samples of 0 or more"; exit 2 }
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
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int length, out int needed);
    /// <summary>The name of the desktop that takes input now ("Default" for the user's own); empty when it cannot be opened, as the
    /// secure desktop of the lock and sign-in screens cannot be by a user's program.</summary>
    public static string InputDesktop()
    {
        IntPtr d = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
        if (d == IntPtr.Zero) return "";
        try { var sb = new StringBuilder(256); int needed; return GetUserObjectInformation(d, 2, sb, sb.Capacity * 2, out needed) ? sb.ToString() : ""; }   // UOI_NAME
        finally { CloseDesktop(d); }
    }
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
"utc`tfg_pid`tfg_name`tfg_is_run`trun_on_monitor`trun_windows`tlocked" | Set-Content -LiteralPath $Out -Encoding utf8
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
    if ($StopFile -and (Test-Path -LiteralPath $StopFile)) { break }
    $runs = @(Get-CimInstance Win32_Process -Property ProcessId, ExecutablePath -ErrorAction SilentlyContinue | Where-Object {
        $path = $_.ExecutablePath
        $path -and @($prefixes | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 -and ([IO.Path]::GetExtension($path) -eq ".exe")
    } | ForEach-Object { [int]$_.ProcessId })
    $fg = [NvWr]::GetForegroundWindow(); $fpid = 0; [void][NvWr]::GetWindowThreadProcessId($fg, [ref]$fpid)
    $fname = ""; try { $fname = (Get-Process -Id $fpid -ErrorAction Stop).ProcessName } catch { }
    $isLocked = ($lockScreens -contains $fname) -or ([NvWr]::InputDesktop() -ne "Default")
    $on = $false
    $wins = if ($runs.Count) { [NvWr]::Windows([int[]]$runs, [ref]$on) } else { "" }
    $fgRun = $runs -contains [int]$fpid
    $line = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}" -f (Get-Date).ToUniversalTime().ToString("HH:mm:ss.fff"), $fpid, $fname, $fgRun, $on, $wins, $isLocked
    Add-Content -LiteralPath $Out -Value $line -Encoding utf8
    Count-Sample $line $on $fgRun ([bool]$wins) $isLocked
    if ($Samples -gt 0 -and $script:sampleCount -ge $Samples) { break }
    Start-Sleep -Milliseconds $IntervalMs
}
Write-Summary

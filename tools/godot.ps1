# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# godot.ps1 - runs the PORTABLE Godot 4.7.2 .NET editor (or an exported build) so that nothing it does lands
# in the user's AppData, and nothing it starts outlives it. Every Godot process goes through here.
#
# - Self-contained mode (the _sc_ marker next to the editor, Godot 4.7 docs "Data paths") keeps EDITOR data in
#   <editor>\editor_data\. It does not cover the PROJECT's user:// folder, which Godot puts under %APPDATA%\Godot\
#   on Windows (measured: the editor created %APPDATA%\Godot\app_userdata\Viewer.Godot\objectdb_snapshots).
#   So every Godot process started here gets APPDATA pointed at a sandbox folder (-AppDataDir).
# - The editor runs its own dotnet build/publish: MSBuild node reuse, the MSBuild server AND shared compilation
#   (the Roslyn compiler server, VBCSCompiler) are off, so no build process lingers.
# root cause: with shared compilation on, a compile inside the export's `dotnet publish` (e.g.
#   Viewer.Core, whose csproj does not turn it off) started VBCSCompiler as a child of the build; it outlived the
#   publish as an orphan and the editor never exited after `savepack DONE`. Captured process tree: Godot with no
#   children, one orphaned VBCSCompiler.exe created 4 s into the export. UseSharedCompilation=false in the
#   environment reaches every project the publish compiles. -KeepSharedCompilation exists ONLY as the negative
#   control that reproduces the hang.
# - Waits for exit; a process that overruns is killed with its whole tree, never left running.
# - Diagnostics: output is read line by line with timestamps. If the process has produced no output
#   for -HangSnapshotSeconds, the process tree under it (and every build process started since it) is written to
#   <DiagDir>\hang-tree-<n>.txt; again just before a timeout kill; and if the process exits but its output pipes
#   stay open (a child still holds them).
# - On a timeout, before the kill: a full dump of the hung .NET process (the executable's GUI process under a console
#   wrapper, Reverie.console.exe -> Reverie.exe; the process itself when it has no such child), taken with dotnet-dump
#   into -DiagDir (dump-<n>-<pid>.dmp), so `dotnet-dump analyze` can show every thread's managed stack. The
#   dump's path (or why there is none) is written into the run's log and output. dotnet-dump is used where it already
#   is (on PATH, or the .NET global tools folder; -DotnetDump names another copy); nothing is installed, and without
#   it the run is killed as before with the reason logged. Runs that do not time out are unchanged. A run started with
#   -ExpectTimeout (it is meant to end by timeout) takes no dump.
# - A test run whose window stopped drawing (the harness sees the loop running and no frame drawn for 10 s) writes
#   <tag>.notdrawing.txt into its --tr-out folder and waits for this script to take the same dump and process-tree
#   snapshot, which it writes next to it as <tag>.notdrawing.dumped; the run then ends itself with exit code 5 (NOT_DRAWING)
#   instead of waiting for the timeout. The process-tree snapshot also lists each process's window (visible, minimised) and
#   which window is in front.
#
# Usage (editor):   powershell -NoProfile -File <repo>\tools\godot.ps1 -ArgLine "--headless --editor --path <project> --build-solutions --quit"
# Usage (exported): powershell -NoProfile -File <repo>\tools\godot.ps1 -Exe <build\Reverie.console.exe> -AppDataDir <dir> -ArgLine "..."
# -ArgLine is ONE string passed verbatim (powershell -File cannot pass an array); quote paths with spaces inside it.
# Exit code = the Godot process's exit code (124 on timeout, 127 if the executable is missing).
# Every run (crash capture on or off) also adds one line to <DiagDir>\run-exits.tsv: end time, exit code, seconds, pid,
# crash-watch on or off, crash files, arguments.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$Exe = "",          # default: the portable editor in \tools\godot\4.7.2-mono\ on this drive (set below)
    [string]$AppDataDir = "",   # default: that install's editor_data\sandbox-appdata (set below)
    [string]$ArgLine = "",
    [int]$TimeoutSeconds = 600,
    [string]$LogFile = "",
    [int]$HangSnapshotSeconds = 30,
    [string]$DiagDir = "",   # default tools\test-results (set below: $PSScriptRoot is empty in param defaults on PS 5.1)
    [switch]$KeepSharedCompilation,
    # when set, LOCALAPPDATA is pointed at this sandbox too (the automation token file goes to the per-user LOCAL
    # data directory; test runs must not write to the real one, ADR-019). Default: unchanged.
    [string]$LocalAppDataDir = "",
    # Places this run's window like a test run's (quarter size, bottom-right corner, no focus) although it is a plain start
    # of the viewer (no --s1- or --test-render argument): tools\login-proof.ps1 starts the login screen this way.
    [switch]$PlaceWindow,
    [string]$DotnetDump = "",   # default: dotnet-dump on PATH, else the .NET global tools folder
    [int]$DumpTimeoutSeconds = 300,
    # A test or measurement run (--test-render, --s1-*) opens its window at a quarter of the area of the monitor the person at
    # the machine is using (the one holding the foreground window), in that monitor's bottom-right corner, at the bottom of the
    # stacking order and without taking focus, so it stays off what that person is looking at (see the window block below).
    # -FullSizeWindow, or NV_TEST_WINDOW_FULL=1 in the environment (which reaches the scripts that call this one), keeps the usual
    # full-size window. NV_TEST_WINDOW_MONITOR=<n> picks another monitor by its position in the list of screens.
    [switch]$FullSizeWindow,
    # Marks a measuring run (its timings are the result): the pacing guard is switched on and vsync is off from the start. The
    # window is the same as for every other test run. render.ps1 marks its own measurement sections, and NV_TEST_WINDOW_MEASURE=1
    # in the environment (which reaches the scripts that call this one) marks any run.
    [switch]$MeasureRun,
    # Runs the program under crash-watch.ps1 (a small debugger of our own, see that file) so a native crash while it starts or
    # runs, a heap corruption included, leaves a dump and a note in <DiagDir>\crash. A run that does not crash leaves nothing there.
    # Runs of the test harness (--test-render, --s1-*) have it on by default (NvTools.CrashWatchPolicy, in
    # TestWindowPlacement.cs); -CrashDumps, or NV_CRASH_DUMPS=1 in the environment (which reaches the scripts that call this one),
    # turns it on for any other program too.
    [switch]$CrashDumps,
    # Switches crash-watch off for this run (see -CrashDumps). NV_CRASH_DUMPS=0 in the environment does the same for the scripts that
    # call this one. Wins over -CrashDumps.
    [switch]$NoCrashDumps,
    # Marks a run that is meant to end by timeout (a check that starts the program and lets the time run out). The timeout still
    # saves the process-tree snapshot and ends the run with 124, but takes no dump: there was no hang to look at.
    [switch]$ExpectTimeout,
    # A batch run (the viewer started with --tr-serve=<this folder>, tools\render-serve.ps1) plays many cases in one process. The viewer writes the case it is
    # running, when it started and its time limit into <ServeDir>\current, and <tag>.done when it has finished; a case still running past its own limit is
    # handled as a timeout: the snapshot and the dump, then the process is stopped (exit 124). -TimeoutSeconds is then only the limit of the whole batch.
    [string]$ServeDir = ""
)

$ErrorActionPreference = "Stop"
# stopping this script, however it is stopped, ends everything it started (job-guard.ps1, beside this script)
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "own-processes.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
if (-not $DiagDir) { $DiagDir = Join-Path $PSScriptRoot "test-results" }
# The portable Godot install lives in a fixed folder on the drive the repository is on.
$godotHome = Join-Path ([System.IO.Path]::GetPathRoot($PSScriptRoot)) "tools\godot\4.7.2-mono\Godot_v4.7.2-stable_mono_win64"
if (-not $Exe) { $Exe = Join-Path $godotHome "Godot_v4.7.2-stable_mono_win64_console.exe" }
if (-not $AppDataDir) { $AppDataDir = Join-Path $godotHome "editor_data\sandbox-appdata" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "GODOT: executable not found: $Exe"; exit 127 }
# An exported build's folder is only read: a sandbox the caller put inside it moves to the diagnostics folder (test-launch.ps1).
. (Join-Path $PSScriptRoot "test-launch.ps1")
$exeBase = [System.IO.Path]::GetFileNameWithoutExtension($Exe) -replace '([._])console$', ''
$exeDir = Split-Path -Parent $Exe
$exportedBuild = Test-Path -LiteralPath (Join-Path $exeDir "$exeBase.pck")
# an export whose data folder or host assembly is missing (a failed .NET publish) is never started: the .NET host would show a
# dialog on the desktop instead of running; the run counts as one whose executable is missing
if ($exportedBuild -and -not (Test-ExportComplete $exeDir)) { Write-Output "GODOT: incomplete export (no data_* folder with Viewer.Godot.dll beside $Exe): not started"; exit 127 }
if ($exportedBuild) { $AppDataDir = Get-RunAppDataDir $AppDataDir $exeDir $DiagDir }
New-Item -ItemType Directory -Force -Path $AppDataDir, $DiagDir | Out-Null

# --- test window: off every monitor, no focus -----------------------------------------------------------------------
# Runs of the test harness and starts of the exported viewer (the live checks' and the smoke's automation runs) that open
# a window are placed; the editor and headless runs are untouched. Every placed window opens beyond the right edge of all
# the monitors, at the usual drawing size, without focus and at the bottom of the stacking order, so nothing a check starts
# covers or interrupts the person at the machine. The engine keeps a new window at least a third on a monitor whatever
# position it is given, so such a window opens minimised (the launch folder's override.cfg, tools\test-launch.ps1), where it
# is on no monitor, and is brought back transparent and without being activated straight to its place off every monitor, by
# the launcher (below) or by the viewer (--nv-test-window-at=, host\Viewer.Godot\TestWindow.cs), whichever is first. A normal
# start asked to open minimised (--minimized: S-1's shipping-start checks) opens minimised and stays so (TestWindowKind.Quiet),
# with its place and size given on the command line so the viewer's remembered window does not bring it back onto a monitor. NV_TEST_WINDOW_ONSCREEN=1 puts it in the bottom-right corner of the
# monitor in use instead (a quarter of its area), to watch a run. Which window a run gets is TestWindowPlacement.KindFor's decision. The placement itself is tools\TestWindowPlacement.cs, which
# the unit tests link, so the numbers used here are the numbers tested. The engine is told the window's size and
# position (--resolution, --position), and the test harness is told the full size it keeps drawing at
# (--nv-test-window=), so it scales the picture down for display when the window is smaller than that, and sets the
# window to not take focus (host\Viewer.Godot\TestWindow.cs).
$fullWindow = $FullSizeWindow -or ($env:NV_TEST_WINDOW_FULL -eq "1")
$measuring = $MeasureRun -or ($env:NV_TEST_WINDOW_MEASURE -eq "1")
$onScreen = $env:NV_TEST_WINDOW_ONSCREEN -eq "1"
$kind = $null
$offX = 0; $offY = 0
$windowPlaced = $false
# The placement and crash-watch decisions live in one file, which the unit tests link too.
$policyLoaded = $false
if ($ArgLine -match '--test-render|--s1-' -or $PlaceWindow -or $exportedBuild) {
    try { Add-Type -Path (Join-Path $PSScriptRoot "TestWindowPlacement.cs"); $policyLoaded = $true } catch { Write-Output "GODOT: placement/crash-watch policy not loaded ($($_.Exception.Message))" }
}
if ($policyLoaded -and $ArgLine -notmatch '--headless') {
    try {
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class NvDpi { [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; } [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r); [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); }'
        [void][NvDpi]::SetProcessDPIAware()   # physical pixels, as the engine's window uses
        $kind = [NvTools.TestWindowPlacement]::KindFor($(if ($PlaceWindow) { "$ArgLine --s1-window" } else { $ArgLine }), [bool]$fullWindow, [bool]$exportedBuild, [bool]$onScreen)
    } catch {
        $kind = $null
        Write-Output "GODOT: test window not placed ($($_.Exception.Message)); opening at the usual size"
    }
}
if ($kind -and $kind -ne [NvTools.TestWindowKind]::Untouched) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $screens = @([System.Windows.Forms.Screen]::AllScreens)
        $areas = New-Object 'NvTools.PixelRect[]' $screens.Count
        $bounds = New-Object 'NvTools.PixelRect[]' $screens.Count
        $primary = 0
        for ($i = 0; $i -lt $screens.Count; $i++) {
            $wa = $screens[$i].WorkingArea
            $areas[$i] = New-Object NvTools.PixelRect $wa.X, $wa.Y, $wa.Width, $wa.Height
            $bo = $screens[$i].Bounds
            $bounds[$i] = New-Object NvTools.PixelRect $bo.X, $bo.Y, $bo.Width, $bo.Height
            if ($screens[$i].Primary) { $primary = $i }
        }
        # The monitor the person at the machine is using: the one holding the foreground window right now (the primary monitor when
        # there is none, or it is minimised). NV_TEST_WINDOW_MONITOR=<n> names another by its position in the list of screens.
        $monitor = $primary
        $fg = [NvDpi]::GetForegroundWindow()
        $fgRect = New-Object NvDpi+Rect
        if ($fg -ne [IntPtr]::Zero -and -not [NvDpi]::IsIconic($fg) -and [NvDpi]::GetWindowRect($fg, [ref]$fgRect)) {
            $monitor = [NvTools.TestWindowPlacement]::MonitorOf($bounds, (New-Object NvTools.PixelRect $fgRect.Left, $fgRect.Top, ($fgRect.Right - $fgRect.Left), ($fgRect.Bottom - $fgRect.Top)), $primary)
        }
        if ($env:NV_TEST_WINDOW_MONITOR -match '^\d+$') { $monitor = [int]$env:NV_TEST_WINDOW_MONITOR }
        if ($kind -eq [NvTools.TestWindowKind]::OffScreen -or $kind -eq [NvTools.TestWindowKind]::Quiet) {
            $win = [NvTools.TestWindowPlacement]::OffScreen($bounds, [NvTools.TestWindowPlacement]::BaseWidth, [NvTools.TestWindowPlacement]::BaseHeight)
            $offX = $win.X; $offY = $win.Y
        } else {
            $win = [NvTools.TestWindowPlacement]::Place($areas, $monitor)
        }
        $windowPlaced = $true
        # the window argument is the viewer's own, so it goes after the engine's "--" separator (a plain start may have none yet)
        $sep = if ($ArgLine -match '(^|\s)--(\s|$)') { "" } else { " --" }
        if ($kind -eq [NvTools.TestWindowKind]::Quiet) {
            $ArgLine = "$([NvTools.TestWindowPlacement]::EngineArguments($win, $bounds)) $ArgLine"   # a normal start: no harness argument
        } else {
            $ArgLine = "$([NvTools.TestWindowPlacement]::EngineArguments($win, $bounds)) $ArgLine$sep --nv-test-window=$([NvTools.TestWindowPlacement]::BaseWidth)x$([NvTools.TestWindowPlacement]::BaseHeight)"
            if ($kind -eq [NvTools.TestWindowKind]::OffScreen) { $ArgLine = "$ArgLine $([NvTools.TestWindowPlacement]::OffScreenArgument($win))" }
        }
        # The window is created before any viewer code runs, so the no-focus flag has to be in the project settings
        # the engine reads at start. An exported build reads override.cfg from its own folder, and the build's folder is
        # never written: the run starts from a launch folder of hard links to the build's files with the override.cfg in
        # it (tools	est-launch.ps1), so a run stopped from outside leaves nothing behind in the build.
        if ($exportedBuild -and (Test-Path -LiteralPath (Join-Path $exeDir "launch.complete"))) {
            Write-Output "GODOT: started from the launch folder it is in ($exeDir), with that folder's own override.cfg"
        } elseif ($exportedBuild) {
            $launch = New-TestLaunchFolder $exeDir $DiagDir ([NvTools.TestWindowPlacement]::LaunchKind($kind))
            if ($launch) {
                $Exe = Join-Path $launch (Split-Path -Leaf $Exe)
                Write-Output "GODOT: test launch folder $launch (the build folder is not written)"
            } else {
                Write-Output "GODOT: no launch folder could be made (hard links need the build and the diagnostics folder on one volume); the window opens as it is"
            }
        }
    } catch {
        # a window that cannot be placed is still a working test run: it opens as it always did
        Write-Output "GODOT: test window not placed ($($_.Exception.Message)); opening at the usual size"
    }
}

# A measuring run's frame times must describe the scene, not how long presents waited on the display or a driver limit.
# The harness checks the path's frames against the display's refresh interval and fails the run, with the reason, instead
# of reporting capped figures (core\Viewer.Core\Diagnostics\FramePacingGuard.cs).
if ($measuring -and $ArgLine -match '--test-render' -and $ArgLine -notmatch '--headless' -and $ArgLine -notmatch '--tr-pacing-guard') { $ArgLine = "$ArgLine --tr-pacing-guard" }
# Vsync is also off from the start: the swap chain is made with the engine's start-up setting, and switching the mode off
# afterwards did not lift the pacing on every machine (measured: ~620 frames a second at a ~7 ms 95th percentile with the
# switch at run time only, ~1500 frames a second at ~1.2 ms with --disable-vsync).
if ($measuring -and $ArgLine -match '--test-render' -and $ArgLine -notmatch '--headless' -and $ArgLine -notmatch '--disable-vsync' -and $ArgLine -notmatch '--tr-vsync=on') { $ArgLine = $ArgLine -replace ' -- --test-render', ' --disable-vsync -- --test-render' }

Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Text;
public sealed class GodotOutputPump
{
    private readonly StringBuilder _text = new StringBuilder();
    private readonly object _gate = new object();
    private System.IO.StreamWriter _file;
    private int _tailMax;
    public DateTime LastOutputUtc = DateTime.UtcNow;
    public volatile bool StdoutClosed, StderrClosed;
    public void Attach(Process p)
    {
        p.OutputDataReceived += (s, e) => On(e.Data, true);
        p.ErrorDataReceived += (s, e) => On(e.Data, false);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }
    // Every line goes to the file as it arrives; only the last tailChars characters (at least) stay in memory. A long run prints
    // hundreds of thousands of engine lines, which the launcher would otherwise hold until the run ends.
    public bool Streaming { get { return _file != null || _tailMax > 0; } }
    public void StreamTo(string path, int tailChars)
    {
        _file = new System.IO.StreamWriter(new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite), new UTF8Encoding(true));
        _file.AutoFlush = true;
        _tailMax = tailChars;
    }
    public void Append(string text) { lock (_gate) { if (_file != null) _file.Write(text); } }
    public void CloseFile() { lock (_gate) { if (_file != null) { _file.Dispose(); _file = null; } } }
    private void On(string line, bool stdout)
    {
        if (line == null) { if (stdout) StdoutClosed = true; else StderrClosed = true; return; }
        lock (_gate)
        {
            if (_file != null) _file.WriteLine(line);
            _text.AppendLine(line);
            if (_tailMax > 0 && _text.Length > 2 * _tailMax) _text.Remove(0, _text.Length - _tailMax);
            LastOutputUtc = DateTime.UtcNow;
        }
    }
    public string Text { get { lock (_gate) return _text.ToString(); } }
    public double IdleSeconds { get { lock (_gate) return (DateTime.UtcNow - LastOutputUtc).TotalSeconds; } }
}
"@

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = $ArgLine
$crashDir = Join-Path $DiagDir "crash"
$forcedOn = $CrashDumps -or ($env:NV_CRASH_DUMPS -eq "1")
$forcedOff = $NoCrashDumps -or ($env:NV_CRASH_DUMPS -eq "0")
$crashWatch = if ($policyLoaded) { [NvTools.CrashWatchPolicy]::Enabled($ArgLine, [bool]$forcedOn, [bool]$forcedOff, [bool]$measuring) } else { [bool]($forcedOn -and -not $forcedOff) }
if ($crashWatch) {
    $psi.FileName = (Get-Command powershell.exe).Source
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'crash-watch.ps1')`" -Exe `"$Exe`" -DumpDir `"$crashDir`" -Name crash"
    $psi.EnvironmentVariables["NV_CRASHWATCH_ARGS"] = $ArgLine
}
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.EnvironmentVariables["APPDATA"] = $AppDataDir
if ($LocalAppDataDir) { New-Item -ItemType Directory -Force -Path $LocalAppDataDir | Out-Null; $psi.EnvironmentVariables["LOCALAPPDATA"] = $LocalAppDataDir }
# Every viewer started here is a test or automation run: its session log goes to a folder of this run's own (NV_LOG_DIR, unless the caller set
# one), never to the user's log folder, where it would roll away the log of a session someone is using. A run given its own LOCALAPPDATA
# sandbox already has a log folder of its own there, which is also what keeps two viewers started together from rolling each other's log.
if (-not $env:NV_LOG_DIR -and -not $LocalAppDataDir) { $psi.EnvironmentVariables["NV_LOG_DIR"] = Join-Path $DiagDir "viewer-logs" }
$psi.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1"
$psi.EnvironmentVariables["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
$psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
if (-not $KeepSharedCompilation) { $psi.EnvironmentVariables["UseSharedCompilation"] = "false" }
# A test run that stops drawing asks this script for a dump (see the header): <tag>.notdrawing.txt in its --tr-out folder.
$notDrawingFile = $null
if ($ArgLine -match '--tr-out="?([^"\s][^"]*?)"?(\s|$)' ) {
    $trOut = $Matches[1]
    $trTag = if ($ArgLine -match '--tr-tag=(\S+)') { $Matches[1] } else { "render" }
    if ($trOut -match '^[A-Za-z]:[\\/]') {   # an absolute path (the harness refuses any other)
        $notDrawingFile = Join-Path $trOut "$trTag.notdrawing.txt"
        $psi.EnvironmentVariables["NV_DUMP_BY_LAUNCHER"] = "1"
    }
}
$notDrawingHandled = $false
$notDrawingLines = @()

# One line per run in <DiagDir>\run-exits.tsv: when it ended, its exit code, how long it ran, whether crash-watch was on and
# which crash files it left. Written on every way out (normal end, timeout), crash capture on or off, so a report or a later
# session reads the exit code from here instead of from a log that a crashed run may not have written.
function Write-RunExit([int]$code, [string[]]$crashFiles) {
    try {
        $file = Join-Path $DiagDir "run-exits.tsv"
        if (-not (Test-Path -LiteralPath $file)) { Add-Content -LiteralPath $file -Value "utc`texit`tseconds`tpid`tcrash_watch`tcrash_files`targs" -Encoding utf8 }
        $line = @([DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), $code, [math]::Round(((Get-Date) - $started).TotalSeconds, 1), $p.Id,
                  $(if ($crashWatch) { "on" } else { "off" }), ($crashFiles -join " "), ($ArgLine -replace '[\t\r\n]+', ' ')) -join "`t"
        Add-Content -LiteralPath $file -Value $line -Encoding utf8
    } catch { Write-Output "GODOT: could not record the exit code ($($_.Exception.Message))" }
}

$started = Get-Date
$pump = New-Object GodotOutputPump
# a measuring run with a log file writes the log as the output arrives (see GodotOutputPump.StreamTo)
if ($LogFile -and $measuring) { $pump.StreamTo($LogFile, 4MB) }
$p = [System.Diagnostics.Process]::Start($psi)
$pump.Attach($p)

function Save-Tree([string]$reason) {
    $all = @(Get-CimInstance Win32_Process)
    $byParent = @{}
    foreach ($x in $all) { $k = [int]$x.ParentProcessId; if (-not $byParent.ContainsKey($k)) { $byParent[$k] = @() }; $byParent[$k] += $x }
    $lines = New-Object System.Collections.Generic.List[string]
    function Walk($procId, $depth) {
        foreach ($c in @($byParent[[int]$procId])) {
            if (-not $c) { continue }
            $lines.Add(("{0}{1} pid={2} ppid={3} created={4:HH:mm:ss} cmd={5}" -f ("  " * $depth), $c.Name, $c.ProcessId, $c.ParentProcessId, $c.CreationDate, $c.CommandLine))
            Walk $c.ProcessId ($depth + 1)
        }
    }
    $root = $all | Where-Object ProcessId -eq $p.Id
    $lines.Add("reason: $reason")
    $lines.Add("godot: pid=$($p.Id) alive=$(-not $p.HasExited) started=$($started.ToString('HH:mm:ss')) idle_output_s=$([math]::Round($pump.IdleSeconds,1)) stdout_closed=$($pump.StdoutClosed) stderr_closed=$($pump.StderrClosed) args: $ArgLine")
    $lines.Add("--- tree under godot")
    if ($root) { $lines.Add(("{0} pid={1} ppid={2} created={3:HH:mm:ss} cmd={4}" -f $root.Name, $root.ProcessId, $root.ParentProcessId, $root.CreationDate, $root.CommandLine)); Walk $p.Id 1 } else { $lines.Add("(godot process gone)") }
    $lines.Add("--- windows of those processes (visible, minimised) and the window in front")
    try {
        Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public static class NvWin { [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder t, int n); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); }' -ErrorAction SilentlyContinue
        $queue2 = New-Object System.Collections.Generic.Queue[int]; $seen = @{}; $queue2.Enqueue($p.Id)
        while ($queue2.Count -gt 0) {
            $id = $queue2.Dequeue(); if ($seen.ContainsKey($id)) { continue }; $seen[$id] = $true
            foreach ($c in @($byParent[$id])) { if ($c) { $queue2.Enqueue([int]$c.ProcessId) } }
            try {
                $pr = [System.Diagnostics.Process]::GetProcessById($id)
                $h = $pr.MainWindowHandle
                if ($h -ne [IntPtr]::Zero) { $lines.Add(("pid={0} window=0x{1:X} visible={2} minimised={3} title={4}" -f $id, [int64]$h, [NvWin]::IsWindowVisible($h), [NvWin]::IsIconic($h), $pr.MainWindowTitle)) }
            } catch { }
        }
        $fg = [NvWin]::GetForegroundWindow(); $sb = New-Object System.Text.StringBuilder 256
        [void][NvWin]::GetWindowText($fg, $sb, 256); $fpid = 0; [void][NvWin]::GetWindowThreadProcessId($fg, [ref]$fpid)
        $lines.Add("foreground: window=0x$([int64]$fg | ForEach-Object { $_.ToString('X') }) pid=$fpid title=$($sb.ToString())")
    } catch { $lines.Add("windows: not listed ($($_.Exception.Message))") }
    $lines.Add("--- build/compiler processes created since godot started (orphans lose their parent link)")
    foreach ($x in $all | Where-Object { $_.Name -match '^(dotnet|VBCSCompiler|MSBuild|csc)\.exe$' -and $_.CreationDate -ge $started.AddSeconds(-1) }) {
        $lines.Add(("{0} pid={1} ppid={2} created={3:HH:mm:ss} cmd={4}" -f $x.Name, $x.ProcessId, $x.ParentProcessId, $x.CreationDate, $x.CommandLine))
    }
    $n = 1
    while (Test-Path -LiteralPath (Join-Path $DiagDir "hang-tree-$n.txt")) { $n++ }
    $f = Join-Path $DiagDir "hang-tree-$n.txt"
    Set-Content -LiteralPath $f -Value $lines -Encoding utf8
    Write-Output "GODOT: diagnostic snapshot ($reason) -> $f"
}

# A full dump of every hung .NET process under the godot process (see the header); returns the lines to log.
function Save-Dump {
    $lines = New-Object System.Collections.Generic.List[string]
    $tool = $DotnetDump
    if (-not $tool) { $c = Get-Command dotnet-dump -ErrorAction SilentlyContinue; if ($c) { $tool = $c.Source } }
    if (-not $tool -and $env:USERPROFILE) { $tool = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-dump.exe" }
    if (-not $tool -or -not (Test-Path -LiteralPath $tool)) { $lines.Add("GODOT: no dump: dotnet-dump not found"); return $lines }
    $all = @(Get-CimInstance Win32_Process)
    $base = [System.IO.Path]::GetFileNameWithoutExtension($Exe) -replace '([._])console$', ''
    $tree = New-Object System.Collections.Generic.List[object]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($p.Id)
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        foreach ($c in $all | Where-Object { [int]$_.ParentProcessId -eq $id }) { $tree.Add($c); $queue.Enqueue([int]$c.ProcessId) }
    }
    $targets = @($tree | Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) -eq $base })
    if ($targets.Count -eq 0) { $targets = @($all | Where-Object ProcessId -eq $p.Id) }
    if ($targets.Count -eq 0) { $lines.Add("GODOT: no dump: the process is gone"); return $lines }
    foreach ($t in $targets) {
        $n = 1
        while (Test-Path -LiteralPath (Join-Path $DiagDir "dump-$n-$($t.ProcessId).dmp")) { $n++ }
        $dump = Join-Path $DiagDir "dump-$n-$($t.ProcessId).dmp"
        $dpsi = New-Object System.Diagnostics.ProcessStartInfo
        $dpsi.FileName = $tool
        $dpsi.Arguments = "collect -p $($t.ProcessId) --type Full -o `"$dump`""
        $dpsi.UseShellExecute = $false
        $dpsi.RedirectStandardOutput = $true
        $dpsi.RedirectStandardError = $true
        $d0 = Get-Date
        $dp = [System.Diagnostics.Process]::Start($dpsi)
        $dout = $dp.StandardOutput.ReadToEndAsync(); $derr = $dp.StandardError.ReadToEndAsync()
        if (-not $dp.WaitForExit($DumpTimeoutSeconds * 1000)) {
            Stop-OwnedTree $dp.Id | Out-Null
            $lines.Add("GODOT: no dump of $($t.Name) pid=$($t.ProcessId): dotnet-dump did not finish in $DumpTimeoutSeconds s (stopped)")
            continue
        }
        $secs = [math]::Round(((Get-Date) - $d0).TotalSeconds, 1)
        if ($dp.ExitCode -eq 0 -and (Test-Path -LiteralPath $dump)) {
            $lines.Add(("GODOT: dump of {0} pid={1} ({2:N0} MiB, {3} s) -> {4}" -f $t.Name, $t.ProcessId, ((Get-Item -LiteralPath $dump).Length / 1MB), $secs, $dump))
        } else {
            $msg = (($dout.Result + " " + $derr.Result) -replace '\s+', ' ').Trim()
            $lines.Add("GODOT: no dump of $($t.Name) pid=$($t.ProcessId): dotnet-dump exit $($dp.ExitCode): $msg")
        }
    }
    return $lines
}

$snapshotted = $false
$windowBottomed = $false
$windowSeenOff = $false
$windowSeenAt = [DateTime]::MaxValue
$lastWindowPoll = [DateTime]::MinValue
if ($windowPlaced) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class NvZOrder
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect r, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcEntry { public uint Size, Usage, Pid; public IntPtr Heap; public uint Module, Threads, Parent; public int Base; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcEntry e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    /// <summary>The process and every process below it, from one process snapshot (a few milliseconds, so the window can be looked for often).</summary>
    public static int[] Tree(int root)
    {
        var parent = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);   // TH32CS_SNAPPROCESS
        if (snap == (IntPtr)(-1)) return new[] { root };
        try
        {
            var e = new ProcEntry(); e.Size = (uint)Marshal.SizeOf(typeof(ProcEntry));
            if (Process32FirstW(snap, ref e)) do { parent[e.Pid] = e.Parent; } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        var ids = new List<int> { root };
        for (int i = 0; i < ids.Count; i++) foreach (var kv in parent) if (kv.Value == (uint)ids[i] && kv.Key != (uint)ids[i] && !ids.Contains((int)kv.Key)) ids.Add((int)kv.Key);
        return ids.ToArray();
    }
    private const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    private const int GwlExStyle = -20, WsExLayered = 0x00080000, WsExNoActivate = 0x08000000, SwShowNoActivate = 4;
    private const uint LwaAlpha = 2;
    /// <summary>Brings a minimised window back at (x, y), off every monitor, never seen and never activated: transparent while the system
    /// restores it (it puts it back where it was first placed, partly on a monitor; a placement off every monitor it moves back onto one),
    /// moved off, then opaque again. WS_EX_NOACTIVATE stays on it, so the system never makes it the active window, whatever window the
    /// person closes or minimises.</summary>
    public static void RestoreOff(IntPtr h, int x, int y)
    {
        int ex = GetWindowLong(h, GwlExStyle);
        SetWindowLong(h, GwlExStyle, ex | WsExLayered | WsExNoActivate);
        SetLayeredWindowAttributes(h, 0, 0, LwaAlpha);
        ShowWindow(h, SwShowNoActivate);
        SetWindowPos(h, (IntPtr)1, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        SetWindowLong(h, GwlExStyle, (ex | WsExNoActivate) & ~WsExLayered);
    }
    /// <summary>Keeps every visible top-level window of the given processes off every monitor and never active: a window that meets a
    /// monitor is moved to (x, y), at the bottom of the stacking order and without activating it; a minimised one is brought back there
    /// (RestoreOff) when <paramref name="restoreMinimised"/>, else left minimised; each is marked WS_EX_NOACTIVATE. Returns how many
    /// windows are up and not minimised (counting one just brought back), -1 when none is.</summary>
    public static int PlaceOff(int[] pids, int x, int y, bool restoreMinimised)
    {
        var mine = new HashSet<uint>();
        foreach (int id in pids) mine.Add((uint)id);
        int n = -1;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (mine.Contains(pid) && IsWindowVisible(h))
            {
                int ex = GetWindowLong(h, GwlExStyle);
                if ((ex & WsExNoActivate) == 0) SetWindowLong(h, GwlExStyle, ex | WsExNoActivate);
                if (IsIconic(h))
                {
                    if (!restoreMinimised) return true;
                    RestoreOff(h, x, y);
                    if (n < 0) n = 0;
                    n++;
                    return true;
                }
                if (n < 0) n = 0;
                n++;
                Rect r;
                if (GetWindowRect(h, out r) && MonitorFromRect(ref r, 0) != IntPtr.Zero)   // MONITOR_DEFAULTTONULL: null when it meets no monitor
                    SetWindowPos(h, (IntPtr)1, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            }
            return true;
        }, IntPtr.Zero);
        return n;
    }
    /// <summary>Sends every visible top-level window of the given processes to the bottom of the stacking order without activating it; returns how many.</summary>
    public static int SendToBottom(int[] pids)
    {
        var mine = new HashSet<uint>();
        foreach (int id in pids) mine.Add((uint)id);
        int n = 0;
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (mine.Contains(pid) && IsWindowVisible(h) && !IsIconic(h))
            {
                SetWindowPos(h, (IntPtr)1, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);   // HWND_BOTTOM
                n++;
            }
            return true;
        }, IntPtr.Zero);
        return n;
    }
}
"@
}
$deadline = $started.AddSeconds($TimeoutSeconds)
$overdueCase = $null
function Get-OverdueServedCase {
    if (-not $ServeDir) { return $null }
    $cur = Join-Path $ServeDir "current"
    try {
        if (-not (Test-Path -LiteralPath $cur)) { return $null }
        $f = (Get-Content -Raw -LiteralPath $cur).Trim() -split "`t"
        if ($f.Count -lt 3) { return $null }
        if (Test-Path -LiteralPath (Join-Path $ServeDir ($f[0] + ".done"))) { return $null }
        $ran = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [double]$f[1]
        if ($ran -gt [double]$f[2]) { return "case $($f[0]) ran $ran s against its limit of $($f[2]) s" }
    } catch { }
    return $null
}
while (-not $p.HasExited) {
    $overdueCase = Get-OverdueServedCase
    if ($notDrawingFile -and -not $notDrawingHandled -and (Test-Path -LiteralPath $notDrawingFile)) {
        $notDrawingHandled = $true
        Save-Tree "the run reports its window is not drawing"
        $notDrawingLines = @(Save-Dump)
        Set-Content -LiteralPath ($notDrawingFile -replace '\.txt$', '.dumped') -Value $notDrawingLines -Encoding utf8
    }
    if ((Get-Date) -ge $deadline -or $overdueCase) {
        $limitText = if ($overdueCase) { $overdueCase } else { "timeout after $TimeoutSeconds s" }
        Save-Tree "$limitText, before kill"
        $dumpLines = if ($ExpectTimeout) { @("GODOT: no dump: the run was expected to end by timeout") } else { @(Save-Dump) }
        Stop-OwnedTree $p.Id | Out-Null
        $p.WaitForExit(10000) | Out-Null
        Start-Sleep -Milliseconds 500
        $text = $pump.Text + (($dumpLines | ForEach-Object { "$_`n" }) -join '')
        if ($pump.Streaming) { $pump.Append((($dumpLines | ForEach-Object { "$_`n" }) -join '')); $pump.CloseFile() }
        elseif ($LogFile) { Set-Content -LiteralPath $LogFile -Value $text -Encoding utf8 }
        Write-Output $text
        Write-Output "GODOT: killed after $(if ($overdueCase) { $overdueCase } else { "$TimeoutSeconds s" }) (pid $($p.Id)) args: $ArgLine"
        Write-RunExit 124 @()
        exit 124
    }
    if (-not $snapshotted -and $HangSnapshotSeconds -gt 0 -and $pump.IdleSeconds -ge $HangSnapshotSeconds) {
        Save-Tree "no output for $HangSnapshotSeconds s, process still running"
        $snapshotted = $true
    }
    # A placed test window is a new top-level window, which Windows puts above everything else when it opens. As soon as the
    # run's process has a window, it is sent to the bottom of the stacking order without being activated (the no-focus setting
    # keeps it from taking focus). Only windows of this run's own process tree are touched. An off-screen window is also moved
    # off every monitor (the engine keeps a new window partly on one whatever position it was given), and checked again every
    # 2 s until the run ends. A corner window is kept at the bottom by the viewer (host\Viewer.Godot\TestWindow.cs), so for it
    # this polls only until the window has been found once, and for at most a minute.
    if ($windowPlaced -and -not $windowBottomed -and (((Get-Date) - $started).TotalSeconds -lt 60 -or $windowSeenOff) -and ((Get-Date) - $lastWindowPoll).TotalMilliseconds -ge $(if ($windowSeenOff -and ((Get-Date) - $windowSeenAt).TotalSeconds -ge 15) { 2000 } else { 40 })) {
        $lastWindowPoll = Get-Date
        try {
            $ids = New-Object System.Collections.Generic.List[int]
            $ids.Add($p.Id)
            if ($kind -eq [NvTools.TestWindowKind]::Corner) {
                $procs = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId)
                for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $procs) { if ([int]$c.ParentProcessId -eq $ids[$i]) { $ids.Add([int]$c.ProcessId) } } }
            }
            if ($kind -eq [NvTools.TestWindowKind]::OffScreen) {
                # opened minimised: brought back off every monitor the first time it is seen (later a check may minimise it on purpose),
                # then kept off every monitor, checked again every 2 s for the whole run in case the program moves it back (every 40 ms
                # for the first 15 s after it appears: the engine places its window again once while it starts)
                if ([NvZOrder]::PlaceOff([NvZOrder]::Tree($p.Id), $offX, $offY, -not $windowSeenOff) -gt 0 -and -not $windowSeenOff) { $windowSeenOff = $true; $windowSeenAt = Get-Date }
            } elseif ($kind -eq [NvTools.TestWindowKind]::Quiet) {
                # a quiet normal start stays minimised; it is only kept from being made the active window or put on a monitor
                [void][NvZOrder]::PlaceOff([NvZOrder]::Tree($p.Id), $offX, $offY, $false)
            } elseif ([NvZOrder]::SendToBottom($ids.ToArray()) -gt 0) { $windowBottomed = $true }
        } catch { $windowBottomed = $true; Write-Output "GODOT: could not send the test window to the bottom ($($_.Exception.Message))" }
    }
    Start-Sleep -Milliseconds $(if ($windowPlaced -and -not $windowBottomed -and -not ($windowSeenOff -and ((Get-Date) - $windowSeenAt).TotalSeconds -ge 15)) { 20 } else { 500 })
}

# Exited. Give the pipes a moment to reach EOF; if a child still holds them, record it (and do not block forever).
$pipeDeadline = (Get-Date).AddSeconds(10)
while (-not ($pump.StdoutClosed -and $pump.StderrClosed) -and (Get-Date) -lt $pipeDeadline) { Start-Sleep -Milliseconds 200 }
if (-not ($pump.StdoutClosed -and $pump.StderrClosed)) { Save-Tree "process exited but its output pipes are still open 10 s later" }

$text = $pump.Text + (($notDrawingLines | ForEach-Object { "$_`n" }) -join '')
$crashNames = @()
if ($crashWatch) {
    foreach ($f in @(Get-ChildItem -Path (Join-Path $crashDir "*") -Include "crash-*", "dotnet-*" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-1) })) {
        $text += "GODOT: crash evidence: $($f.FullName) ($([math]::Round($f.Length / 1MB, 1)) MiB)`n"
        $crashNames += $f.Name
    }
}
if ($pump.Streaming) {
    $pump.Append($text.Substring($pump.Text.Length))
    $pump.CloseFile()
    Write-Output "GODOT: streaming run: the log file holds the whole output; this is its last part"
}
elseif ($LogFile) { Set-Content -LiteralPath $LogFile -Value $text -Encoding utf8 }
Write-Output $text
Write-Output "GODOT: exit=$($p.ExitCode) pid=$($p.Id) args: $ArgLine"
Write-RunExit $p.ExitCode $crashNames
exit $p.ExitCode

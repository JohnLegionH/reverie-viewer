# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# close-cycles.ps1 - starts the exported viewer on a region capture, waits until the scene is loaded and settled, then closes
# the window the way the close button does (a WM_CLOSE posted to it) and records how the process ended. Repeated per backend.
#
# Why: the engine's own quit with a live scene is a different code path from a harness run that ends itself, and a process
# that ends in a crash at shutdown is easy to miss (the window is gone either way). Every cycle runs through tools\godot.ps1,
# so crash-watch is on (a native crash leaves a dump and a note under <DiagDir>\crash). No login is made: the scene is a
# recorded region capture drawn by the harness (--tr-hold-for-close keeps it drawing until the window is closed).
#
# A cycle passes when the process exits 0 within -CloseSeconds of the close and leaves no crash file. Anything else is a fail
# with the cycle's log tail, exit code and crash evidence in the results. Results: <OutDir>\cycles.csv and a summary line.
#     powershell -NoProfile -File <repo>\tools\close-cycles.ps1 -OutDir <folder> -DiagDir <folder> [-Cycles 20] [-Backends vulkan,d3d12]
# Needs the exported build (tools\s1.ps1) and the local capture fixtures (tools\fixtures-local.ps1).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$DiagDir,
    [int]$Cycles = 20,
    [string[]]$Backends = @("vulkan", "d3d12"),
    [string]$Capture = "capture-region-a-v9.jsonl.gz",
    [int]$ReadySeconds = 180,
    [int]$CloseSeconds = 60
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "own-processes.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
. (Join-Path $PSScriptRoot "fixtures-local.ps1")
$repo = Split-Path -Parent $PSScriptRoot
$godot = Join-Path $PSScriptRoot "godot.ps1"
$build = Join-Path $repo "host\Viewer.Godot\build"
$exe = Join-Path $build "Reverie.console.exe"
$local = Get-FixturesLocal $repo
$fixture = Join-Path $local $Capture
$assets = Join-Path $local "assets"
foreach ($need in @($exe, $fixture, $assets)) {
    if (-not (Test-Path -LiteralPath $need)) { Write-Output "SKIP: $need is missing (the exported build and the local capture fixtures are needed)"; exit 0 }
}
New-Item -ItemType Directory -Force -Path $OutDir, $DiagDir | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NvClose
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public const uint WM_CLOSE = 0x0010;
    /// <summary>The engine windows of a process: the visible ones, or when none is visible (the engine shows its window late or not
    /// at all on some machines) the hidden ones of the engine's window class. <paramref name="description"/> lists every window seen.</summary>
    public static IntPtr[] WindowsOf(int pid, out string description)
    {
        var found = new List<IntPtr>(); var engine = new List<IntPtr>(); var text = new StringBuilder();
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != (uint)pid) return true;
            var cls = new StringBuilder(128); GetClassNameW(h, cls, 128);
            bool vis = IsWindowVisible(h);
            text.Append(cls + (vis ? "(visible) " : "(hidden) "));
            if (vis) found.Add(h);
            if (cls.ToString() == "Engine") engine.Add(h);
            return true;
        }, IntPtr.Zero);
        description = text.ToString();
        return found.Count > 0 ? found.ToArray() : engine.ToArray();
    }
}
'@

function Stop-Tree([int]$id) { Stop-OwnedTree $id | Out-Null }

$rows = New-Object System.Collections.Generic.List[object]
$stamp = (Get-Date).ToString("HHmmss")   # a tag per invocation: a ready file of an earlier one is never mistaken for this one
foreach ($be in $Backends) {
    for ($i = 1; $i -le $Cycles; $i++) {
        $tag = "close-$be-$i-$stamp"
        $ready = Join-Path $OutDir "$tag.ready"
        $log = Join-Path $OutDir "$tag.log"
        $argLine = "--rendering-driver $be -- --test-render `"--tr-out=$OutDir`" --tr-tag=$tag `"--tr-fixture=$fixture`" `"--tr-assets=$assets`" --tr-path=40 --tr-seed=8 --tr-budget=2048m --tr-size=960x540 --tr-hold-for-close"
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = (Get-Command powershell.exe).Source
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$godot`" -DiagDir `"$DiagDir`" -Exe `"$exe`" -AppDataDir `"$(Join-Path $build 'appdata')`" -TimeoutSeconds 400 -LogFile `"$log`" -ArgLine `"$($argLine -replace '"', '\"')`""
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $started = Get-Date
        $run = [System.Diagnostics.Process]::Start($psi)
        $null = $run.StandardOutput.ReadToEndAsync(); $null = $run.StandardError.ReadToEndAsync()
        $problem = $null; $closedAt = $null; $viewerPid = 0
        while (-not (Test-Path -LiteralPath $ready)) {
            if ($run.HasExited) { $problem = "ended before the scene was loaded (exit $($run.ExitCode))"; break }
            if (((Get-Date) - $started).TotalSeconds -gt $ReadySeconds) { $problem = "the scene was not loaded within $ReadySeconds s"; break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $problem) {
            $viewerPid = [int](Get-Content -LiteralPath $ready -Raw).Trim()
            Start-Sleep -Milliseconds 700   # a moment of drawing with the scene up
            $windows = @(); $seen = ""
            for ($k = 0; $k -lt 40 -and $windows.Count -eq 0; $k++) {
                $windows = @([NvClose]::WindowsOf($viewerPid, [ref]$seen))
                if ($windows.Count -eq 0) { Start-Sleep -Milliseconds 250 }
            }
            if ($windows.Count -eq 0) { $problem = "no window for process $viewerPid (its windows: $seen)" }
            else { $closedAt = Get-Date; foreach ($w in $windows) { [void][NvClose]::PostMessageW($w, [NvClose]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) } }
        }
        if ($closedAt) {
            if (-not $run.WaitForExit($CloseSeconds * 1000)) { $problem = "still running $CloseSeconds s after the close"; }
        }
        $exitCode = if ($run.HasExited) { $run.ExitCode } else { -1 }
        $closeTook = if ($closedAt -and $run.HasExited) { [math]::Round(((Get-Date) - $closedAt).TotalSeconds, 2) } else { $null }
        if (-not $run.HasExited) { Stop-Tree $run.Id; [void]$run.WaitForExit(15000) }   # the whole tree, never only the script
        $crash = @(Get-ChildItem -LiteralPath (Join-Path $DiagDir "crash") -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-1) } | ForEach-Object Name)
        $pass = (-not $problem) -and $exitCode -eq 0 -and $crash.Count -eq 0
        if (-not $pass -and -not $problem) { $problem = "exit $exitCode$(if ($crash.Count) { ", crash files: $($crash -join ' ')" })" }
        $rows.Add([pscustomobject]@{ Backend = $be; Cycle = $i; Pass = $pass; Windows = $seen; Exit = $exitCode; CloseSeconds = $closeTook; Crash = ($crash -join ' '); Problem = $problem })
        Write-Output ("{0,-7} cycle {1,2}: {2}  exit={3} close={4} s {5}" -f $be, $i, $(if ($pass) { "PASS" } else { "FAIL" }), $exitCode, $closeTook, $problem)
    }
}
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir "cycles.csv")
foreach ($be in $Backends) {
    $mine = @($rows | Where-Object Backend -eq $be)
    Write-Output ("{0}: {1} of {2} cycles closed cleanly" -f $be, @($mine | Where-Object Pass).Count, $mine.Count)
}
if (@($rows | Where-Object { -not $_.Pass }).Count -gt 0) { exit 1 }
exit 0

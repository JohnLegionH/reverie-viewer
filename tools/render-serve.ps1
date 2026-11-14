# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# render-serve.ps1 - the batch mode of tools\render.ps1 (dot-sourced by it).
#
# One viewer process per rendering backend is started once (through the launcher script that render.ps1 passes in, with --tr-serve=<folder>) and is handed the
# cases one after another through files in its folder (host\Viewer.Godot\TestRender.Serve.cs describes the protocol). A caller gets
# the same answer a one-case process would give: the exit code, the case's <tag>.json and the seconds. The checks that read the
# result stay in render.ps1, so they are the same checks in both modes.
#
#   - A case that ends the process (a crash, a hang that the launcher stops at the case's own time limit, the not-drawing exit) is
#     reported as that case's failure; the next case starts a new process.
#   - Cases that need a process of their own stay one-process-per-case: measuring runs, timing cases, lifecycle and leak checks,
#     warm-up and pipeline-gate checks, cases that end the process or hold it for close, another rendering driver than the two
#     approved ones (Test-CaseServable says which and why).
#   - -OneProcessPerCase on render.ps1 (or NV_RENDER_ONE_PER_CASE=1) keeps every case on the old path.
# =====================================================================================

# the Windows rules for splitting a command line, so a served case gets exactly the arguments a started process would
if (-not ([System.Management.Automation.PSTypeName]'NvArgv').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NvArgv
{
    [DllImport("shell32.dll", SetLastError = true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmd, out int argc);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    public static string[] Split(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new string[0];
        int argc;
        IntPtr argv = CommandLineToArgvW("x " + line, out argc);
        if (argv == IntPtr.Zero) throw new ArgumentException("cannot split: " + line);
        try
        {
            var result = new string[argc - 1];
            for (int i = 1; i < argc; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
            return result;
        }
        finally { LocalFree(argv); }
    }
}
"@
}

function ConvertTo-CaseArgs([string]$line) { return [string[]][NvArgv]::Split($line) }

# Switches of a case that make it unfit for a process that has run other cases (the reason is the second part)
$script:ServeDeny = @(
    @("--tr-path", "a camera path run measures a whole process (timing, memory)"),
    @("--tr-measure", "a measurement"),
    @("--tr-lifecycle", "a lifecycle check counts what a process holds from its start"),
    @("--tr-teardown", "a teardown check counts what a process holds from its start"),
    @("--tr-engineerrors", "counts the engine's errors from a clean process"),
    @("--tr-warmup", "a warm-up check counts the pipelines a process compiles"),
    @("--tr-compile-gate", "the pipeline gate counts the pipelines a process compiles"),
    @("--tr-hold", "ends by the window being closed or holds a fetch"),
    @("--tr-minimize", "ends the process or minimises its window"),
    @("--tr-hang-test", "stops the process on purpose"),
    @("--tr-leak-at-quit", "checks what is live at the process's end"),
    @("--tr-soak", "a long run of its own"),
    @("--tr-keptcollections", "checks the collections kept from the process's start"),
    @("--tr-disposestress", "a stress run"),
    @("--tr-handoverstress", "a stress run"),
    @("--tr-surfacestress", "a stress run"),
    @("--tr-blendstress", "a stress run"),
    @("--tr-busy", "a timed run"),
    @("--tr-uploadbench", "a benchmark"),
    @("--tr-skycycle", "counts video memory from a clean process"),
    @("--tr-viewreuse", "counts what a process holds from its start"),
    @("--tr-sharedmeasure", "a measurement"),
    @("--tr-crash", "crashes the process on purpose"),
    @("--tr-vsync", "a pacing control"),
    @("--tr-pacing-guard", "a measuring run"),
    @("--tr-avatars=rigged", "its pictures differ by a few hundred pixels in a process that has run other cases (measured; the cause is not found), and match the golden exactly in a process of their own"),
    @("--tr-avatars=riggedblend", "the same: pictures differ in a process that has run other cases"),
    @("--tr-avatars=shapelook", "the same: the picture differs in a process that has run other cases")
)

# Cases with a time limit run alone, in a process of their own, never beside other cases (and never served)
# The rigged-avatar and shape-look pictures are timing cases too: their pictures vary from run to run (a few hundred pixels; in the shape-look case the shape is sometimes not
# applied when the picture is taken, and the case then fails), and an idle batch process or an earlier batch run in the same script run changed them further.
$script:TimingTagPattern = '(^|-)(stall|stress|crossing|arrival|arrive|pipeline-gate|slowdetach|not-drawing|window-minimized|churn|soak)(-|$)|^path-|^warmup-path|^terrain-settle|^crowd-|^arrival-|^avatars-rigged-(vulkan|d3d12|negative|blend)|^avatars-shape-look|^merge-update-'

function Test-TimingCase([string]$tag) { return [bool]($tag -match $script:TimingTagPattern) }

# Returns @{ Serve = $true/$false; Why = "<reason when not>" }
function Test-CaseServable([string]$driver, [string]$tag, [string]$extra, [bool]$measuring, [bool]$noDump) {
    if ($driver -notin @("vulkan", "d3d12")) { return @{ Serve = $false; Why = "backend $driver is not one of the two approved ones" } }
    if ($measuring) { return @{ Serve = $false; Why = "a measuring run" } }
    if ($noDump) { return @{ Serve = $false; Why = "a run that is expected to be stopped" } }
    if (Test-TimingCase $tag) { return @{ Serve = $false; Why = "a timing case runs alone" } }
    foreach ($d in $script:ServeDeny) {
        $follow = if ($d[0] -like "--tr-avatars=*") { "(\s|`"|$)" } else { "(=|-|\s|`"|$)" }   # an avatar mode is named exactly; the other switches are prefixes
        if ($extra -match ("(^|\s|`")" + [regex]::Escape($d[0]) + $follow)) { return @{ Serve = $false; Why = "$($d[0]): $($d[1])" } }
    }
    return @{ Serve = $true; Why = "" }
}

# ---- the workers ----------------------------------------------------------------------------------------------------------------
$script:Serve = @{ Workers = @{}; Failures = 0; Starts = 0; Disabled = $false }

function Get-ServeStats { return @{ Starts = $script:Serve.Starts; Failures = $script:Serve.Failures } }

# $Launcher is a scriptblock that starts the worker: it gets the driver, the folder and returns the Process of the launcher script.
function Start-ServeWorker([string]$driver, [string]$outDir, [scriptblock]$launcher, [int]$readySeconds = 180) {
    $n = 1
    while (Test-Path -LiteralPath (Join-Path $outDir "serve-$driver-$n")) { $n++ }
    $dir = Join-Path $outDir "serve-$driver-$n"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:Serve.Starts++
    $proc = & $launcher $driver $dir
    $w = @{ Dir = $dir; Process = $proc; Cases = 0; Driver = $driver }
    $t0 = Get-Date
    while (-not (Test-Path -LiteralPath (Join-Path $dir "ready"))) {
        if ($proc.HasExited -or ((Get-Date) - $t0).TotalSeconds -gt $readySeconds) {
            if (-not $proc.HasExited) { Stop-OwnedTree $proc.Id | Out-Null }
            $script:Serve.Failures++
            return $null
        }
        Start-Sleep -Milliseconds 50
    }
    $script:Serve.Workers[$driver] = $w
    return $w
}

# A case's exit code from its done file. The file can be seen for a moment before the code is in it (a worker that writes it in place):
# it is read again until the code is there, for at most five seconds; a file that stays empty or holds no number counts as exit 2.
function Read-DoneCode([string]$done) {
    $until = (Get-Date).AddSeconds(5)
    while ($true) {
        $text = Get-Content -Raw -LiteralPath $done -ErrorAction SilentlyContinue
        $code = 0
        if ($text -and [int]::TryParse($text.Trim(), [ref]$code)) { return $code }
        if ((Get-Date) -ge $until) { return 2 }
        Start-Sleep -Milliseconds 50
    }
}

# Runs one case in the worker of its backend. Returns @{ Exit; Json; Seconds; Served = $true; WorkerDied }, or $null when no worker could be had
# (the caller then runs the case the old way). $launcher: see Start-ServeWorker.
function Invoke-ServedCase([string]$driver, [string]$tag, [string]$extra, [int]$timeout, [string]$outDir, [scriptblock]$launcher) {
    $t0 = Get-Date
    $w = $script:Serve.Workers[$driver]
    if ($w -and $w.Process.HasExited) { $script:Serve.Workers.Remove($driver); $w = $null }
    if (-not $w) {
        $w = Start-ServeWorker $driver $outDir $launcher
        if (-not $w) {
            if ($script:Serve.Failures -ge 2) { $script:Serve.Disabled = $true }
            return $null
        }
    }
    $dir = $w.Dir
    $done = Join-Path $dir "$tag.done"
    if (Test-Path -LiteralPath $done) { Move-Item -LiteralPath $done -Destination ("$done." + [guid]::NewGuid().ToString("N").Substring(0, 8)) }
    $json = Join-Path $outDir "$tag.json"
    $lines = @("timeout=$timeout", "--test-render", "--tr-out=$outDir", "--tr-tag=$tag") + (ConvertTo-CaseArgs $extra)
    $req = Join-Path $dir "$tag.request"
    [System.IO.File]::WriteAllLines("$req.tmp", [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath "$req.tmp" -Destination $req
    $w.Cases++
    $hardStop = $t0.AddSeconds($timeout + 180)
    while (-not (Test-Path -LiteralPath $done)) {
        if ($w.Process.HasExited) { break }
        if ((Get-Date) -ge $hardStop) {
            Stop-OwnedTree $w.Process.Id | Out-Null
            $w.Process.WaitForExit(10000) | Out-Null
            break
        }
        Start-Sleep -Milliseconds 15
    }
    $code = 2
    $died = $false
    if (Test-Path -LiteralPath $done) { $code = Read-DoneCode $done }
    else {
        $died = $true
        $w.Process.WaitForExit(15000) | Out-Null
        $code = if ($w.Process.HasExited) { $w.Process.ExitCode } else { 124 }
        $script:Serve.Workers.Remove($driver)
        # the worker's last words go in the case's log, so the failure says what happened
        $workerLog = Join-Path $dir "worker.log"
        if (Test-Path -LiteralPath $workerLog) { Set-Content -LiteralPath (Join-Path $outDir "$tag.log") -Value (@("the process serving this case ended with exit $code before the case finished; its log follows") + (Get-Content -LiteralPath $workerLog -Tail 40)) -Encoding utf8 }
    }
    $j = if ((Test-Path -LiteralPath $json) -and (Get-Item -LiteralPath $json).LastWriteTime -ge $t0.AddSeconds(-1)) { Get-Content -Raw -LiteralPath $json | ConvertFrom-Json } else { $null }
    if (-not $died) { Set-Content -LiteralPath (Join-Path $outDir "$tag.log") -Value "served by process $($w.Process.Id) as case $($w.Cases) of its batch; exit $code" -Encoding utf8 }
    return @{ Exit = $code; Json = $j; Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1); Served = $true; WorkerDied = $died }
}

# Asks every worker to leave and waits for it; a worker that does not leave within the time is stopped (its process tree).
function Stop-ServeWorkers([int]$waitSeconds = 60) {
    foreach ($d in @($script:Serve.Workers.Keys)) {
        $w = $script:Serve.Workers[$d]
        if (-not $w.Process.HasExited) {
            $q = Join-Path $w.Dir "quit.request"
            [System.IO.File]::WriteAllText("$q.tmp", "", (New-Object System.Text.UTF8Encoding($false)))
            Move-Item -LiteralPath "$q.tmp" -Destination $q
            if (-not $w.Process.WaitForExit($waitSeconds * 1000)) { Stop-OwnedTree $w.Process.Id | Out-Null; $w.Process.WaitForExit(10000) | Out-Null }
        }
    }
    $script:Serve.Workers = @{}
}

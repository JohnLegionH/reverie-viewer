# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# job-guard.ps1 - dot-sourced by the entry scripts (test.ps1, s1.ps1, render.ps1, godot.ps1, close-cycles.ps1).
#
# Puts the calling script's own process into a Windows job object that ends every process in it when the job's last handle
# closes, and the handle closes when the script's process ends however it ends: a normal exit, a stop of that one process
# (taskkill without /T, Stop-Process, a terminal that closed), a crash. Every process the script starts afterwards (the
# viewer, the editor, dotnet and what it starts, a nested script and its children) belongs to the same job, so stopping a run
# stops its whole process tree, not only the process that was named. Without it, stopping only the top script leaves the
# viewer and its helpers running, still holding the machine's cores, the window and the run's files.
#
# Nothing is installed and no system setting changes: a job object is a per-process kernel object. Jobs nest (Windows 8 and
# later), so a script started inside another job (a nested script, a terminal's own job) still joins its own. A process that
# cannot join (an older Windows, a job that forbids it) logs one line and carries on as before.
# No script under tools\ takes a claim of its own (StoppedRunTests scans for it): a claim belongs to whoever starts the whole run.
# NV_NO_JOB_GUARD=1 skips the join (the tests use it as the negative control).
# =====================================================================================
if (-not ([System.Management.Automation.PSTypeName]'NvJobGuard').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class NvJobGuard
{
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    const uint KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    static IntPtr s_job;   // held for the life of the process: the handle closing is what ends the job

    /// <summary>Joins this process to a new kill-on-close job. Returns null when it did, else why not.</summary>
    public static string Join()
    {
        if (s_job != IntPtr.Zero) return null;
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return "CreateJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
            return "SetInformationJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            return "AssignProcessToJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
        s_job = job;
        return null;
    }
}
"@
}
if ($env:NV_NO_JOB_GUARD -ne "1") {
    $jobGuardProblem = [NvJobGuard]::Join()
    if ($jobGuardProblem) { Write-Output "job-guard: this script's process tree is not tied to it ($jobGuardProblem)" }
}

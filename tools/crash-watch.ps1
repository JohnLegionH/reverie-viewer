# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# crash-watch.ps1 - runs a program under a minimal debugger of its own so a native crash leaves a dump.
#
# Why: a process that ends in a heap corruption (exit code 0xC0000374) or an unhandled native exception while it starts or
# runs leaves nothing: the .NET runtime's own crash-dump settings (DOTNET_DbgEnableMiniDump) only cover failures the runtime
# itself handles, and the Windows crash-dump facility (Error Reporting local dumps) is a system setting this repository does
# not change. A debugger sees the exception in the dying process before the process is ended, from its first instruction,
# in native code as well as managed code.
#
# What it does: starts -Exe (and any process it starts) under the debugging API, with the output handles of this script
# passed on so the caller sees the program's output as before, and runs the debug event loop. It writes a full-memory dump
# (crash-<pid>.dmp) and a short text file (crash-<pid>.txt: exception code, address, faulting thread, loaded modules, the
# thread's registers) into -DumpDir when
#   - a heap corruption (0xC0000374), a stack buffer overrun / fast fail (0xC0000409) or a stack overflow (0xC00000FD)
#     is raised (these end the process even if the first chance goes unhandled), or
#   - any exception is not handled (the second chance), which is what ends the process.
# Every exception is otherwise passed on to the program unchanged, so it behaves as without the debugger (the program's
# exit code is this script's exit code). The heap manager's debug mode, which a debugger would otherwise switch on, is
# turned off (_NO_DEBUG_HEAP=1), so the program's heap behaves as it does without a debugger.
# A debugger slows the program down: this is for failure hunting, not for measuring.
# The .NET runtime's own dump (dotnet-<pid>.dmp, heap) is switched on as well, for the failures the runtime ends itself.
# Nothing is installed and no system setting is changed; the program is stopped with this script (debuggee kill on exit).
#
# Usage: powershell -NoProfile -File crash-watch.ps1 -Exe <program> -DumpDir <folder> [-Name <prefix>]
# The program's command-line arguments are in the environment variable NV_CRASHWATCH_ARGS (one string, as the caller
# would pass them), which avoids a second round of quoting.
# Exit code = the program's; 126 if it could not be started.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DumpDir,
    [string]$Name = "crash"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $DumpDir | Out-Null
$argLine = $env:NV_CRASHWATCH_ARGS
Remove-Item Env:\NV_CRASHWATCH_ARGS -ErrorAction SilentlyContinue
$env:_NO_DEBUG_HEAP = "1"
# Failures the .NET runtime ends itself (a fatal error after a native access violation, which the debugger never sees as an
# unhandled exception) are dumped by the runtime's own crash-dump support: DOTNET_DbgEnableMiniDump, heap dump type.
if (-not $env:DOTNET_DbgEnableMiniDump) {
    $env:DOTNET_DbgEnableMiniDump = "1"
    $env:DOTNET_DbgMiniDumpType = "2"
    $env:DOTNET_DbgMiniDumpName = (Join-Path $DumpDir "dotnet-%p.dmp")
}

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class NvCrashWatch
{
    const uint DEBUG_PROCESS = 0x1;
    const uint STARTF_USESTDHANDLES = 0x100;
    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    const uint DBG_CONTINUE = 0x00010002, DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    const uint EXCEPTION_DEBUG_EVENT = 1, CREATE_THREAD_DEBUG_EVENT = 2, CREATE_PROCESS_DEBUG_EVENT = 3, EXIT_THREAD_DEBUG_EVENT = 4,
        EXIT_PROCESS_DEBUG_EVENT = 5, LOAD_DLL_DEBUG_EVENT = 6, UNLOAD_DLL_DEBUG_EVENT = 7;
    const uint EXCEPTION_BREAKPOINT = 0x80000003, STATUS_WX86_BREAKPOINT = 0x4000001F;
    const int MiniDumpWithFullMemory = 0x2, MiniDumpWithHandleData = 0x4, MiniDumpWithUnloadedModules = 0x20, MiniDumpWithThreadInfo = 0x1000;

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFO
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    [DllImport("kernel32.dll")] static extern bool WaitForDebugEvent(IntPtr ev, uint ms);
    [DllImport("kernel32.dll")] static extern bool ContinueDebugEvent(int pid, int tid, uint status);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll")] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, int pid, IntPtr file, int type, IntPtr exc, IntPtr user, IntPtr cb);

    const uint PROCESS_ALL = 0x1F0FFF, THREAD_GET_CONTEXT_ETC = 0x1F03FF;

    // DEBUG_EVENT on x64: dwDebugEventCode (4), dwProcessId (4), dwThreadId (4), pad (4), union at 16.
    // EXCEPTION_DEBUG_INFO: EXCEPTION_RECORD (152 bytes on x64) then dwFirstChance (4).
    public static int Run(string exe, string args, string dumpDir, string name, TextWriter log)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = (int)STARTF_USESTDHANDLES;
        si.hStdInput = GetStdHandle(STD_INPUT_HANDLE); si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE); si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        foreach (var h in new[] { si.hStdInput, si.hStdOutput, si.hStdError }) if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, 1, 1);
        var cmd = new StringBuilder("\"" + exe + "\" " + args);
        PROCESS_INFORMATION pi;
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, DEBUG_PROCESS, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
        {
            log.WriteLine("crash-watch: could not start " + exe + " (error " + Marshal.GetLastWin32Error() + ")");
            return 126;
        }
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        int rootPid = pi.dwProcessId;
        DebugSetProcessKillOnExit(true);
        IntPtr ev = Marshal.AllocHGlobal(1024);
        var dumped = new HashSet<int>();
        var modules = new Dictionary<int, List<string>>();
        int rootExit = 0;
        try
        {
            while (true)
            {
                if (!WaitForDebugEvent(ev, 1000)) { if (Marshal.GetLastWin32Error() == 121) continue; continue; }
                uint code = (uint)Marshal.ReadInt32(ev, 0);
                int pid = Marshal.ReadInt32(ev, 4), tid = Marshal.ReadInt32(ev, 8);
                uint status = DBG_CONTINUE;
                if (code == EXCEPTION_DEBUG_EVENT)
                {
                    uint exc = (uint)Marshal.ReadInt32(ev, 16);
                    IntPtr addr = Marshal.ReadIntPtr(ev, 16 + 16);
                    int firstChance = Marshal.ReadInt32(ev, 16 + 152);
                    if (exc == EXCEPTION_BREAKPOINT || exc == STATUS_WX86_BREAKPOINT) status = DBG_CONTINUE;   // the initial breakpoint
                    else
                    {
                        status = DBG_EXCEPTION_NOT_HANDLED;
                        bool fatalAtFirstChance = exc == 0xC0000374 || exc == 0xC0000409 || exc == 0xC00000FD;
                        if ((fatalAtFirstChance || firstChance == 0) && !dumped.Contains(pid))
                        {
                            dumped.Add(pid);
                            Dump(pid, tid, exc, addr, firstChance != 0, dumpDir, name, modules, log);
                        }
                    }
                }
                else if (code == CREATE_PROCESS_DEBUG_EVENT)
                {
                    IntPtr hFile = Marshal.ReadIntPtr(ev, 16);
                    if (hFile != IntPtr.Zero) CloseHandle(hFile);
                }
                else if (code == LOAD_DLL_DEBUG_EVENT)
                {
                    IntPtr hFile = Marshal.ReadIntPtr(ev, 16);
                    if (hFile != IntPtr.Zero) CloseHandle(hFile);
                    // the module list is read from the dump; the names seen here are kept for the text file
                }
                else if (code == EXIT_PROCESS_DEBUG_EVENT)
                {
                    int exit = Marshal.ReadInt32(ev, 16);
                    if (pid == rootPid) { rootExit = exit; ContinueDebugEvent(pid, tid, DBG_CONTINUE); break; }
                }
                ContinueDebugEvent(pid, tid, status);
            }
        }
        finally { Marshal.FreeHGlobal(ev); }
        return rootExit;
    }

    static void Dump(int pid, int tid, uint exc, IntPtr addr, bool firstChance, string dir, string name, Dictionary<int, List<string>> modules, TextWriter log)
    {
        string baseName = Path.Combine(dir, name + "-" + pid);
        IntPtr proc = OpenProcess(PROCESS_ALL, false, pid);
        var sb = new StringBuilder();
        sb.AppendLine("exception: 0x" + exc.ToString("X8") + (firstChance ? " (first chance, fatal)" : " (not handled)"));
        sb.AppendLine("address: 0x" + addr.ToInt64().ToString("X"));
        sb.AppendLine("process: " + pid + "  faulting thread: " + tid + " (0x" + tid.ToString("X") + ")");
        sb.AppendLine("time (UTC): " + DateTime.UtcNow.ToString("o"));
        sb.AppendLine("open the dump in a debugger and select the faulting thread (WinDbg: ~~[0x" + tid.ToString("X") + "]s, then k); dotnet-dump shows the managed side.");
        string dmp = baseName + ".dmp";
        try
        {
            using (var fs = new FileStream(dmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                bool ok = MiniDumpWriteDump(proc, pid, fs.SafeFileHandle.DangerousGetHandle(), MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                sb.AppendLine(ok ? "dump: " + dmp : "dump FAILED (error " + Marshal.GetLastWin32Error() + ")");
            }
        }
        catch (Exception e) { sb.AppendLine("dump FAILED: " + e.Message); }
        try { File.WriteAllText(baseName + ".txt", sb.ToString()); } catch { }
        log.WriteLine("crash-watch: exception 0x" + exc.ToString("X8") + " in pid " + pid + " thread " + tid + " -> " + baseName + ".dmp");
        CloseHandle(proc);
    }
}
"@

$code = [NvCrashWatch]::Run($Exe, $argLine, $DumpDir, $Name, [Console]::Error)
exit $code

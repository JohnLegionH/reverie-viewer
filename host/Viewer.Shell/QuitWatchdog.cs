// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;
using Viewer.Core.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// Bounds the time a quitting viewer may take. Once the viewer has begun to quit, nothing may keep the process alive for
/// longer than <see cref="Limit"/>: a watchdog thread of its own ends the process when the limit passes, and says in the
/// log which stage the quit had reached and which of the viewer's own threads were still running.
/// Why: a quit was seen to stall after every one of the viewer's cleanup steps had run. The engine's shutdown waits for its
/// worker threads to exit; some of them stay parked on their wake-up and the wait never ends, so the window stays open until
/// something kills the process. The viewer cannot repair the engine's wait, so it bounds it, and the stall is logged
/// (never retried or hidden) with a non-zero exit code.
/// The thread is a background thread: a quit that finishes normally ends the process with it.
/// </summary>
public sealed class QuitWatchdog
{
    /// <summary>How long a quit may take. A normal quit takes a few seconds at most; this leaves room for a loaded machine.</summary>
    public static TimeSpan Limit { get; } = TimeSpan.FromSeconds(20);

    /// <summary>The exit code of a process ended by the watchdog (a clean quit exits 0).</summary>
    public const int ExitCode = 70;

    private readonly CoreLog _log;
    private readonly Action<int> _terminate;
    private readonly TimeSpan _limit;
    private readonly Func<IReadOnlyList<string>>? _holding;
    private readonly ManualResetEventSlim _done = new(false);
    private string _stage = "not started";
    private int _armed;

    /// <param name="log">Where the stall is written.</param>
    /// <param name="terminate">Ends the process with the given exit code (tests pass a recorder).</param>
    /// <param name="limit">How long the quit may take.</param>
    /// <param name="holding">Names of the viewer's own threads still running, for the log; optional.</param>
    public QuitWatchdog(CoreLog log, Action<int> terminate, TimeSpan limit, Func<IReadOnlyList<string>>? holding = null)
    {
        _log = log;
        _terminate = terminate;
        _limit = limit;
        _holding = holding;
    }

    /// <summary>The quit entered a new stage. Each stage is written to the log as it is entered, so a process that ends during its
    /// quit (a crash in the engine's own shutdown, for example) leaves the log at the stage it had reached, which tells it apart
    /// from a clean exit; the stage is named again if the quit stalls there.</summary>
    public void Enter(string stage)
    {
        Volatile.Write(ref _stage, stage);
        try { _log.Info("quit.stage", CoreLog.F("stage", stage)); }
        catch (Exception) { /* the log is a convenience; the quit goes on */ }
    }

    /// <summary>Starts the clock. Calling it again does nothing.</summary>
    public void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        new Thread(Watch) { IsBackground = true, Name = "quit-watchdog" }.Start();
    }

    /// <summary>The quit finished: the watchdog stands down.</summary>
    public void Disarm() => _done.Set();

    private void Watch()
    {
        if (_done.Wait(_limit)) return;
        string stage = Volatile.Read(ref _stage);
        string[] names;
        try { names = _holding?.Invoke().ToArray() ?? []; }
        catch (Exception) { names = ["(the thread list could not be read)"]; }
        try
        {
            _log.Error("quit.stalled", null, CoreLog.F("stage", stage), CoreLog.F("limit-seconds", _limit.TotalSeconds),
                CoreLog.F("threads-still-running", names.Length == 0 ? "none of the viewer's own" : string.Join(",", names)));
        }
        catch (Exception) { /* the log is a convenience; the process still ends */ }
        _terminate(ExitCode);
    }

    /// <summary>Ends this process at once, without running the engine's shutdown (the stalled part).</summary>
    public static void TerminateThisProcess(int exitCode)
    {
        try { Console.Out.Flush(); Console.Error.Flush(); } catch (Exception) { /* nothing more to flush */ }
        if (OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), (uint)exitCode);
        Environment.Exit(exitCode);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

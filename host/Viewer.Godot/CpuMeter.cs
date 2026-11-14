// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Viewer.Godot;

/// <summary>
/// Processor time used by this process and by the calling thread, for measuring runs. Processor time moves much less than wall-clock time
/// when other programs share the machine, so a comparison of two builds on a busy machine reads it instead of elapsed time. Take one reading
/// at the start of a stretch and one at the end, on the same thread, and subtract.
/// </summary>
internal readonly record struct CpuMeter(double ProcessMs, double ThreadMs)
{
    /// <summary>Processor time (user plus kernel) of the whole process and of the calling thread so far.</summary>
    public static CpuMeter Take()
    {
        double process = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        double thread = 0;
        if (OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
            thread = (kernel + user) / 10000.0;   // 100 ns units to milliseconds
        return new CpuMeter(process, thread);
    }

    /// <summary>The processor time between two readings: the process, the calling thread, and everything else (workers, the engine's threads).</summary>
    public (double ProcessMs, double ThreadMs, double OtherMs) Since(CpuMeter start)
    {
        double p = ProcessMs - start.ProcessMs, t = ThreadMs - start.ThreadMs;
        return (Math.Round(p, 1), Math.Round(t, 1), Math.Round(p - t, 1));
    }

    private static double _cyclesPerMs;

    /// <summary>The calling thread's cycle counter (the unit <see cref="CyclesPerMs"/> converts to milliseconds). Zero off Windows.</summary>
    public static long ThreadCycles()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return (long)cycles;
    }

    /// <summary>Counter cycles per millisecond, calibrated on first use (<see cref="ThreadCycleMs"/>).</summary>
    public static double CyclesPerMs { get { if (_cyclesPerMs == 0) ThreadCycleMs(); return _cyclesPerMs == 0 ? 1 : _cyclesPerMs; } }

    /// <summary>
    /// The calling thread's processor time in milliseconds from its cycle counter, which (unlike <see cref="Take"/>) is fine enough to time one
    /// slice of a few milliseconds. The counter's rate is calibrated once against the wall clock, taking the best of several short spins so a spin
    /// that was descheduled does not lower it.
    /// </summary>
    public static double ThreadCycleMs()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (_cyclesPerMs == 0)
        {
            double best = 0;
            for (int i = 0; i < 8; i++)
            {
                QueryThreadCycleTime(GetCurrentThread(), out ulong c0);
                long t0 = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - t0 < Stopwatch.Frequency / 200) { }
                QueryThreadCycleTime(GetCurrentThread(), out ulong c1);
                double wallMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                best = Math.Max(best, (c1 - c0) / wallMs);
            }
            _cyclesPerMs = best;
        }
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return cycles / _cyclesPerMs;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Globalization;
using Viewer.Core.Physics;

namespace Viewer.Core.Diagnostics;

/// <summary>One measurement of the current process.</summary>
public readonly record struct ProcessSample(string Label, int Cycle, int Threads, double WorkingSetMiB, double PrivateMiB)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"SAMPLE {Label,-8} cycle={Cycle,5} threads={Threads,5} ws_mib={WorkingSetMiB,8:F1} private_mib={PrivateMiB,8:F1}");
}

public sealed record PhysicsLifecycleResult(
    bool Pass,
    int Cycles,
    ProcessSample Before,
    ProcessSample After,
    IReadOnlyList<ProcessSample> Samples,
    bool ThreadsOk,
    double WorkingSetGrowthMiB,
    double WorkingSetSlopeKiBPerCycle,
    bool WorkingSetOk,
    double PrivateGrowthMiB,
    double PrivateSlopeKiBPerCycle,
    bool PrivateOk,
    string Verdict);

/// <summary>
/// Physics lifecycle probe (ADR-009): N PhysicsWorld create/destroy cycles in the CURRENT
/// process, with thread count, working set and private bytes before, every K cycles, and after (GC + settle).
/// Shared by the console smoke and the Godot S-1 scene, so both use one set of bounds.
///
/// PASS when threads_after &lt;= threads_before + ThreadMargin and memory shows no steady growth, for BOTH working
/// set and private bytes: (after - first in-loop sample) &lt;= MemGrowthBoundMiB and the least-squares slope over the
/// in-loop samples &lt;= MemSlopeBoundKiBPerCycle.
/// </summary>
public static class PhysicsLifecycleProbe
{
    public const int ThreadMargin = 2;
    public const double MemGrowthBoundMiB = 32;
    public const double MemSlopeBoundKiBPerCycle = 16;

    public static PhysicsLifecycleResult Run(int cycles = 1000, int sampleEvery = 100, Action<string>? log = null)
    {
        var samples = new List<ProcessSample>();
        var before = Measure("before", 0);
        log?.Invoke(before.ToString());

        for (int c = 1; c <= cycles; c++)
        {
            using (var world = new PhysicsWorld())
            {
                if (c == 1)
                    log?.Invoke($"HANDLES {world.DescribeHandleOwnership()}");
                if (!world.Step(1f / 60f))
                    throw new InvalidOperationException($"Step reported an error at cycle {c}");
            }
            if (c % sampleEvery == 0)
            {
                var m = Measure("cycle", c);
                samples.Add(m);
                log?.Invoke(m.ToString());
            }
        }

        Thread.Sleep(500);
        var after = Measure("after", cycles);
        log?.Invoke(after.ToString());

        bool threadsOk = after.Threads <= before.Threads + ThreadMargin;
        double wsGrowth = samples.Count > 0 ? after.WorkingSetMiB - samples[0].WorkingSetMiB : 0;
        double wsSlope = Slope(samples, s => s.WorkingSetMiB) * 1024;
        double privGrowth = samples.Count > 0 ? after.PrivateMiB - samples[0].PrivateMiB : 0;
        double privSlope = Slope(samples, s => s.PrivateMiB) * 1024;
        bool wsOk = wsGrowth <= MemGrowthBoundMiB && wsSlope <= MemSlopeBoundKiBPerCycle;
        bool privOk = privGrowth <= MemGrowthBoundMiB && privSlope <= MemSlopeBoundKiBPerCycle;
        bool pass = threadsOk && wsOk && privOk;

        static string Ok(bool ok) => ok ? "ok" : "EXCEEDED";
        string verdict = string.Create(CultureInfo.InvariantCulture,
            $"VERDICT {(pass ? "PASS" : "FAIL")} threads {before.Threads}->{after.Threads} (bound +{ThreadMargin}): {Ok(threadsOk)}; " +
            $"ws growth {wsGrowth:F1} MiB, slope {wsSlope:F2} KiB/cycle: {Ok(wsOk)}; " +
            $"private growth {privGrowth:F1} MiB, slope {privSlope:F2} KiB/cycle: {Ok(privOk)} " +
            $"(memory bounds {MemGrowthBoundMiB} MiB, {MemSlopeBoundKiBPerCycle} KiB/cycle)");
        log?.Invoke(verdict);

        return new PhysicsLifecycleResult(pass, cycles, before, after, samples, threadsOk,
            wsGrowth, wsSlope, wsOk, privGrowth, privSlope, privOk, verdict);
    }

    private static ProcessSample Measure(string label, int cycle)
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Thread.Sleep(200);
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new ProcessSample(label, cycle, p.Threads.Count, p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0);
    }

    // Least-squares slope of a memory series (MiB) against cycle number.
    private static double Slope(List<ProcessSample> s, Func<ProcessSample, double> y)
    {
        if (s.Count < 2)
            return 0;
        double mx = s.Average(x => (double)x.Cycle), my = s.Average(y);
        double num = s.Sum(x => (x.Cycle - mx) * (y(x) - my));
        double den = s.Sum(x => (x.Cycle - mx) * (x.Cycle - mx));
        return den == 0 ? 0 : num / den;
    }
}

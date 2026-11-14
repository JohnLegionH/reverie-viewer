// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Physics lifecycle smoke (ADR-009): 1,000 PhysicsWorld create/destroy cycles with the vendored
/// patched joltc; thread count must return within +2 of baseline and the working set must not grow steadily.
/// Each run is its own child process (Viewer.Core.LifecycleSmoke), so a leaking run cannot pollute another test.
/// </summary>
public class PhysicsLifecycleTests
{
    private readonly ITestOutputHelper _out;
    public PhysicsLifecycleTests(ITestOutputHelper output) { _out = output; }

    private (int ExitCode, string Output) RunSmoke(params string[] args)
    {
        Assert.True(File.Exists(TestPaths.LifecycleSmokeDll), $"smoke not built: {TestPaths.LifecycleSmokeDll}");
        var r = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.LifecycleSmokeDll, .. args], TimeSpan.FromMinutes(10));
        _out.WriteLine(r.Output);
        return r;
    }

    [Fact]
    public void Thousand_create_destroy_cycles_leave_threads_and_memory_flat()
    {
        var (exit, output) = RunSmoke("--cycles", "1000", "--sample-every", "100");
        Assert.Contains("VERDICT PASS", output);
        Assert.Equal(0, exit);
    }

    // The check must be able to fail: with the job pools' native destroy disabled, JoltPhysicsSharp 2.19.1 leaks every pool's workers.
    // 20 cycles, not 1,000: the leak is one full pool of threads per cycle, and a thousand pools of leaked
    // threads would only load the host without proving anything more.
    [Fact]
    public void Without_the_JOLT_7e_workaround_the_thread_check_fails()
    {
        var (exit, output) = RunSmoke("--cycles", "20", "--sample-every", "5", "--no-workaround");
        Assert.Contains("VERDICT FAIL", output);
        Assert.Matches(@"threads \d+->\d+ \(bound \+2\): EXCEEDED", output);
        Assert.Equal(1, exit);
    }

    // The PhysicsSystem destroy (viewer extension, see PhysicsWorld): without it every cycle leaks the native
    // system and the patched joltc's per-system TempAllocator (~8.5 MiB). 20 cycles for the same reason as above.
    [Fact]
    public void Without_the_PhysicsSystem_destroy_the_memory_check_fails()
    {
        var (exit, output) = RunSmoke("--cycles", "20", "--sample-every", "5", "--no-system-destroy");
        Assert.Contains("VERDICT FAIL", output);
        Assert.Matches(@"threads \d+->\d+ \(bound \+2\): ok", output);
        Assert.Matches(@"private growth [\d.]+ MiB, slope [\d.]+ KiB/cycle: EXCEEDED", output);
        Assert.Equal(1, exit);
    }
}

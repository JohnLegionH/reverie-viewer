// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What counts as presentation pacing. A cap is the display, the compositor or the engine holding frames back, not a frame
/// time that happens to be steady: a heavy scene is steady too. The series below are made for four refresh rates, and the
/// real per-frame series are from replays of one recorded region path (window 1152x648, 60 Hz display) on both graphics
/// backends: Vulkan ran free, Direct3D 12 let ten or so frames through and then waited for the next refresh, every refresh.
/// </summary>
public class FramePacingGuardRefreshTests
{
    public static IEnumerable<object[]> RefreshRates() => new[] { 60.0, 120.0, 144.0, 165.0 }.Select(hz => new object[] { hz });

    private static List<double> Steady(int n, double ms, double spread, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => ms * (1 + spread * (r.NextDouble() * 2 - 1))).ToList();
    }

    /// <summary>Alternates between one and two refresh intervals.</summary>
    private static List<double> OneAndTwoIntervals(int n, double interval, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => interval * (r.Next(2) + 1) * (1 + 0.01 * (r.NextDouble() * 2 - 1))).ToList();
    }

    /// <summary>
    /// Fast frames, and once per refresh interval one frame that waits for the display: the frames pass until the present
    /// queue is full, then one of them waits for a refresh. <paramref name="regular"/> false puts the same number of slow frames at random places.
    /// </summary>
    private static List<double> WaitsOncePerInterval(double seconds, double interval, double fastMs, double waitMs, bool regular, int seed)
    {
        var r = new Random(seed);
        var v = new List<double>();
        double total = 0, nextWait = interval;
        double limit = seconds * 1000;
        double hz = 1000.0 / interval;
        double randomShare = hz * fastMs / (1000 - hz * (waitMs - fastMs));   // as many slow frames a second as the regular series has
        while (total < limit)
        {
            bool slow = regular ? total >= nextWait : r.NextDouble() < randomShare;
            if (slow)
            {
                double f = waitMs * (1 + 0.1 * (r.NextDouble() * 2 - 1));
                v.Add(f); total += f; nextWait = total + interval - f;
            }
            else
            {
                double f = fastMs * (1 + 0.15 * (r.NextDouble() * 2 - 1));
                v.Add(f); total += f;
            }
        }
        return v;
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void A_steady_frame_time_that_is_not_a_multiple_of_the_refresh_interval_is_not_capped(double hz)
    {
        double interval = 1000.0 / hz;
        var verdict = FramePacingGuard.Evaluate(Steady(3000, interval * 1.5, 0.01, 1), hz);
        Assert.False(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.None, verdict.Kind);
    }

    [Fact]
    public void Steady_heavy_frames_of_23_ms_are_not_capped_at_60_Hz()
    {
        var verdict = FramePacingGuard.Evaluate(Steady(3000, 23.0, 0.02, 2), 60);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Steady_heavy_frames_are_not_capped_without_a_refresh_rate_either(double hz)
    {
        var verdict = FramePacingGuard.Evaluate(Steady(3000, 21.0, 0.03, 3), 0);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Frames_locked_at_the_refresh_interval_are_capped(double hz)
    {
        var verdict = FramePacingGuard.Evaluate(Steady(3000, 1000.0 / hz, 0.01, 4), hz);
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshLocked, verdict.Kind);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Frames_alternating_between_one_and_two_refresh_intervals_are_capped(double hz)
    {
        var verdict = FramePacingGuard.Evaluate(OneAndTwoIntervals(3000, 1000.0 / hz, 5), hz);
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshLocked, verdict.Kind);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Noisy_frames_that_are_not_held_by_anything_are_not_capped(double hz)
    {
        var verdict = FramePacingGuard.Evaluate(Steady(4000, 5.5, 0.6, 6), hz);   // 2.2 to 8.8 ms, spread over the range
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void Fast_frames_with_one_wait_per_refresh_interval_are_capped(double hz)
    {
        double interval = 1000.0 / hz;
        var verdict = FramePacingGuard.Evaluate(WaitsOncePerInterval(20, interval, 0.5, 0.6 * interval, regular: true, seed: 7), hz);
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshStalls, verdict.Kind);
        Assert.InRange(verdict.StallsPerSecond, hz * 0.9, hz * 1.1);
        Assert.Contains("slow frames", verdict.Message);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void A_wait_on_some_refreshes_and_not_others_is_still_a_refresh_wait(double hz)
    {
        // the wait comes on every refresh or every second one, as a run held for only part of its length would show
        double interval = 1000.0 / hz;
        var r = new Random(14);
        var v = new List<double>();
        for (int i = 0; i < 700; i++)
        {
            int intervals = 1 + r.Next(2);
            double used = 0;
            while (used < intervals * interval - 0.6 * interval) { double f = 0.5 * (1 + 0.1 * (r.NextDouble() * 2 - 1)); v.Add(f); used += f; }
            v.Add(0.6 * interval);
        }
        var verdict = FramePacingGuard.Evaluate(v, hz);
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshStalls, verdict.Kind);
    }

    [Theory]
    [MemberData(nameof(RefreshRates))]
    public void The_same_number_of_slow_frames_at_random_places_is_not_a_refresh_wait(double hz)
    {
        double interval = 1000.0 / hz;
        var verdict = FramePacingGuard.Evaluate(WaitsOncePerInterval(20, interval, 0.5, 0.6 * interval, regular: false, seed: 8), hz);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Adaptive")]
    [InlineData("Mailbox")]
    public void An_engine_with_vertical_sync_on_is_reported_capped_whatever_its_frames_look_like(string mode)
    {
        var verdict = FramePacingGuard.Evaluate(Steady(3000, 0.8, 0.2, 9), 60, new PresentSettings(mode, 0, "opted-out"));
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.EngineLimit, verdict.Kind);
        Assert.Contains(mode, verdict.Message);
    }

    [Fact]
    public void An_engine_with_a_frame_rate_limit_is_reported_capped()
    {
        var verdict = FramePacingGuard.Evaluate(Steady(3000, 0.8, 0.2, 10), 60, new PresentSettings("Disabled", 120, "unknown"));
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.EngineLimit, verdict.Kind);
    }

    [Fact]
    public void An_engine_with_vsync_off_and_no_limit_leaves_the_judgement_to_the_frames()
    {
        var free = FramePacingGuard.Evaluate(Steady(3000, 0.8, 0.2, 11), 60, new PresentSettings("Disabled", 0, "system-decides"));
        Assert.False(free.Capped, free.Message);
        var locked = FramePacingGuard.Evaluate(Steady(3000, 1000.0 / 60, 0.01, 12), 60, new PresentSettings("Disabled", 0, "system-decides"));
        Assert.True(locked.Capped, locked.Message);
    }

    // ---- the real series ----

    private static List<double> Series(string name)
    {
        string path = Path.Combine(TestPaths.RepoRoot, "fixtures", "pacing", name + ".txt");
        string line = File.ReadLines(path).Single(l => !l.StartsWith('#'));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
    }

    [Fact]
    public void The_Direct3D12_replay_waits_once_per_refresh_and_is_capped()
    {
        var v = Series("d3d12-region-path");
        var verdict = FramePacingGuard.Evaluate(v, 60, new PresentSettings("Disabled", 0, "system-decides"));
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshStalls, verdict.Kind);
        Assert.InRange(verdict.StallsPerSecond, 55, 66);
    }

    [Fact]
    public void The_Vulkan_replay_runs_free_and_is_not_capped()
    {
        var v = Series("vulkan-region-path");
        var verdict = FramePacingGuard.Evaluate(v, 60, new PresentSettings("Disabled", 0, "system-decides"));
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Fact]
    public void The_Direct3D12_vsync_control_is_capped_by_its_frames_and_by_its_settings()
    {
        var v = Series("d3d12-vsync-on-control");
        Assert.Equal(PacingKind.RefreshLocked, FramePacingGuard.Evaluate(v, 60, new PresentSettings("Disabled", 0, "unknown")).Kind);
        Assert.Equal(PacingKind.EngineLimit, FramePacingGuard.Evaluate(v, 60, new PresentSettings("Enabled", 0, "unknown")).Kind);
    }

    [Fact]
    public void The_steady_busy_view_at_6000_objects_is_not_capped()
    {
        // every frame about 21 ms: the scene's cost, which an earlier reading took for a pile at one length
        var v = Series("d3d12-busy-6000-objects");
        Assert.InRange(v.Count, 600, 800);
        var verdict = FramePacingGuard.Evaluate(v, 60, new PresentSettings("Disabled", 0, "system-decides"));
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Fact]
    public void The_Vulkan_vsync_control_is_capped_by_its_frames()
    {
        var verdict = FramePacingGuard.Evaluate(Series("vulkan-vsync-on-control"), 60, new PresentSettings("Disabled", 0, "unknown"));
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Equal(PacingKind.RefreshLocked, verdict.Kind);
    }

    [Fact]
    public void Slow_frames_of_one_length_with_no_refresh_rhythm_are_not_a_cap_when_the_refresh_rate_is_known()
    {
        // a tenth of the frames at about 7 ms among 0.8 ms frames, in no rhythm: scene work, or a wait, the frames alone do not say
        var r = new Random(13);
        var v = Enumerable.Range(0, 20000).Select(_ => r.NextDouble() < 0.12 ? 7.0 * (1 + 0.03 * (r.NextDouble() * 2 - 1)) : 0.8 * (1 + 0.4 * (r.NextDouble() * 2 - 1))).ToList();
        Assert.False(FramePacingGuard.Evaluate(v, 60).Capped);
        Assert.True(FramePacingGuard.Evaluate(v, 0).Capped);   // with no refresh rate the pile is all there is to go on
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Frame figures from the frame cost (the larger of the engine's measured CPU and GPU render time of the main viewport) on Direct3D 12,
/// and from real frame times on Vulkan: synthetic series in, figures and verdicts out.
/// </summary>
public class FrameFiguresTests
{
    private static readonly PresentSettings Free = new("Disabled", 0, "system-decides");

    private static List<double> Repeat(double value, int n) => Enumerable.Repeat(value, n).ToList();

    /// <summary>The swap chain held to a 60 Hz display: bursts of ten fast frames and one wait of about nine milliseconds, every refresh.</summary>
    private static List<double> PresentQueue(int refreshes)
    {
        var frames = new List<double>();
        for (int r = 0; r < refreshes; r++)
        {
            for (int i = 0; i < 10; i++) frames.Add(0.8);
            frames.Add(16.667 - 8.0);
        }
        return frames;
    }

    [Fact]
    public void Cost_is_the_larger_of_the_cpu_and_gpu_time_of_each_frame()
    {
        var cost = FrameFigures.Cost([1.0, 5.0, 0.0, 2.5], [3.0, 2.0, 0.0, 2.5]);
        Assert.Equal([3.0, 5.0, 0.0, 2.5], cost);
    }

    [Fact]
    public void Cost_of_series_of_different_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FrameFigures.Cost([1.0, 2.0], [1.0]));
    }

    [Theory]
    [InlineData("d3d12", FrameBasis.FrameCost)]
    [InlineData("D3D12", FrameBasis.FrameCost)]
    [InlineData("vulkan", FrameBasis.FrameTime)]
    [InlineData("opengl3", FrameBasis.FrameTime)]
    [InlineData(null, FrameBasis.FrameTime)]
    public void Direct3D_12_takes_its_figures_from_the_cost_and_every_other_driver_from_the_real_frame_times(string? driver, FrameBasis expected)
    {
        Assert.Equal(expected, FrameFigures.BasisFor(driver, true));
    }

    [Fact]
    public void The_shipped_setting_keeps_real_frame_times_on_every_driver_because_the_cost_did_not_agree_on_vulkan()
    {
        Assert.False(FrameFigures.D3D12UsesFrameCost);
        foreach (string driver in new[] { "d3d12", "vulkan", "opengl3" })
            Assert.Equal(FrameBasis.FrameTime, FrameFigures.BasisFor(driver));
        var frames = PresentQueue(600);
        var j = FrameFigures.Judge("d3d12", frames, Repeat(1.4, frames.Count), Repeat(2.1, frames.Count), 60, Free);
        Assert.Equal(FrameBasis.FrameTime, j.Basis);
        Assert.True(j.Fails);   // a run held by the present queue still fails: its frame times are what is reported
    }

    [Fact]
    public void A_busy_scene_bound_by_the_main_thread_has_a_cost_far_below_its_frame_time_and_the_comparison_says_so()
    {
        // the busy view at 6,000 objects on Vulkan: frame time about 22.5 ms, the render CPU and GPU times about 8.7 ms
        var frame = Repeat(22.5, 600);
        var r = FrameFigures.Compare(frame, Repeat(8.7, 600));
        Assert.False(r.Agrees);
        Assert.Contains("do not agree", r.Message);
    }

    [Fact]
    public void A_direct3d_12_run_held_by_the_present_queue_passes_on_its_frame_cost_and_the_figures_are_the_cost()
    {
        var frames = PresentQueue(600);   // 6,600 frames, 600 slow ones, one per refresh: the guard reads this as capped
        var cpu = Repeat(1.4, frames.Count);
        var gpu = Repeat(2.1, frames.Count);
        var j = FrameFigures.Judge("d3d12", frames, cpu, gpu, 60, Free, true);
        Assert.True(j.Pacing.Capped);                       // the real frame times are the present queue's
        Assert.Equal(FrameBasis.FrameCost, j.Basis);
        Assert.False(j.Fails);                              // ... and the run is judged on its cost
        Assert.All(j.Figures, v => Assert.Equal(2.1, v));   // the figures are the larger of the two
        Assert.Contains("present queue", j.Message);
    }

    [Fact]
    public void The_same_series_on_vulkan_fails_because_vulkan_figures_are_real_frame_times()
    {
        var frames = PresentQueue(600);
        var j = FrameFigures.Judge("vulkan", frames, Repeat(1.4, frames.Count), Repeat(2.1, frames.Count), 60, Free);
        Assert.Equal(FrameBasis.FrameTime, j.Basis);
        Assert.True(j.Fails);
        Assert.Same(frames, j.Figures);
    }

    [Fact]
    public void A_vsync_control_run_is_flagged_on_both_drivers_even_on_the_cost_basis()
    {
        var frames = Repeat(16.67, 600);
        var vsync = new PresentSettings("Enabled", 0, "system-decides");
        foreach (string driver in new[] { "d3d12", "vulkan" })
        {
            var j = FrameFigures.Judge(driver, frames, Repeat(1.0, 600), Repeat(1.5, 600), 60, vsync, true);
            Assert.True(j.Fails, driver);
            Assert.Equal(PacingKind.EngineLimit, j.Pacing.Kind);
        }
    }

    [Fact]
    public void A_frame_rate_limit_is_flagged_on_the_cost_basis_too()
    {
        var frames = Repeat(16.67, 600);
        var j = FrameFigures.Judge("d3d12", frames, Repeat(1.0, 600), Repeat(1.5, 600), 60, new PresentSettings("Disabled", 60, "system-decides"), true);
        Assert.True(j.Fails);
    }

    [Fact]
    public void A_busy_d3d12_run_with_free_frames_passes_with_the_cost_as_its_figures()
    {
        var frames = Repeat(22.0, 600);   // a steady heavy scene: not a pacing pattern
        var cpu = Repeat(21.0, 600);
        var gpu = Repeat(19.0, 600);
        var j = FrameFigures.Judge("d3d12", frames, cpu, gpu, 60, Free, true);
        Assert.False(j.Fails);
        Assert.False(j.Pacing.Capped);
        Assert.Equal(FrameBasis.FrameCost, j.Basis);
        Assert.Equal(21.0, FrameFigures.Percentile(j.Figures, 95));
    }

    [Fact]
    public void A_d3d12_run_without_a_measured_cost_cannot_use_it_and_falls_back_to_the_frame_times()
    {
        var frames = PresentQueue(600);
        var j = FrameFigures.Judge("d3d12", frames, Repeat(0, frames.Count), Repeat(0, frames.Count), 60, Free, true);
        Assert.Equal(FrameBasis.FrameTime, j.Basis);
        Assert.True(j.Fails);
        Assert.Contains("not measured", j.Message);
    }

    [Fact]
    public void A_cost_series_of_the_wrong_length_is_treated_as_not_measured()
    {
        var frames = Repeat(1.0, 600);
        var j = FrameFigures.Judge("d3d12", frames, Repeat(1.0, 10), Repeat(1.0, 10), 60, Free, true);
        Assert.Equal(FrameBasis.FrameTime, j.Basis);
    }

    [Fact]
    public void Cost_that_matches_the_frame_times_agrees_and_one_that_is_far_off_does_not()
    {
        var rnd = new Random(8);
        var frame = Enumerable.Range(0, 2000).Select(_ => 20.0 + rnd.NextDouble() * 4).ToList();
        var near = frame.Select(f => f * 0.9).ToList();
        Assert.True(FrameFigures.Compare(frame, near).Agrees);
        var far = frame.Select(f => f * 0.5).ToList();
        var bad = FrameFigures.Compare(frame, far);
        Assert.False(bad.Agrees);
        Assert.Contains("do not agree", bad.Message);
    }

    [Fact]
    public void Millisecond_frames_agree_within_the_floor_not_the_share()
    {
        // a fast free-running frame of 0.6 ms: 25 % would be 0.15 ms, the floor is 0.25 ms
        var frame = Repeat(0.6, 500);
        Assert.True(FrameFigures.Compare(frame, Repeat(0.84, 500)).Agrees);    // 0.24 ms apart
        Assert.False(FrameFigures.Compare(frame, Repeat(0.9, 500)).Agrees);    // 0.30 ms apart
    }

    [Fact]
    public void Agreement_needs_both_the_median_and_the_95th_percentile()
    {
        var frame = new List<double>(); var cost = new List<double>();
        for (int i = 0; i < 1000; i++) { bool tail = i % 10 == 0; frame.Add(tail ? 30 : 10); cost.Add(tail ? 12 : 10); }
        var r = FrameFigures.Compare(frame, cost);
        Assert.False(r.Agrees);       // the median matches, the tail (a frame that was slow on the main thread) does not
        Assert.Equal(0, r.P50Difference, 6);
        Assert.Equal(-18, r.P95Difference, 6);
    }

    [Fact]
    public void Nothing_to_compare_does_not_agree()
    {
        Assert.False(FrameFigures.Compare([], []).Agrees);
    }

    [Fact]
    public void Marked_frames_are_left_out_of_a_series()
    {
        var kept = FrameFigures.Unmarked([1, 2, 3, 4], [false, true, false, true]);
        Assert.Equal([1.0, 3.0], kept);
        Assert.Throws<ArgumentException>(() => FrameFigures.Unmarked([1, 2], [false]));
    }

    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(100, 1000, false)]   // exactly 10 %: allowed
    [InlineData(101, 1000, true)]    // over 10 %: NOT_DRAWING
    [InlineData(1000, 1000, true)]
    [InlineData(0, 0, false)]
    public void A_run_with_more_than_a_tenth_of_its_frames_marked_is_not_drawing(int marked, int total, bool notDrawing)
    {
        var flags = Enumerable.Range(0, total).Select(i => i < marked).ToList();
        var m = FrameFigures.Marks(flags);
        Assert.Equal(marked, m.Marked);
        Assert.Equal(total, m.Total);
        Assert.Equal(notDrawing, m.NotDrawing);
    }

    [Fact]
    public void The_cost_and_the_real_frame_time_of_a_free_run_agree_when_the_work_is_all_in_the_render_cost()
    {
        // a free-running scene: the time between frames is the render cost plus a little fixed engine overhead
        var rnd = new Random(3);
        var cpu = new List<double>(); var gpu = new List<double>(); var frame = new List<double>();
        for (int i = 0; i < 3000; i++)
        {
            double c = 3.0 + rnd.NextDouble(), g = 2.0 + rnd.NextDouble() * 2;
            cpu.Add(c); gpu.Add(g); frame.Add(Math.Max(c, g) + 0.1);
        }
        Assert.True(FrameFigures.Compare(frame, FrameFigures.Cost(cpu, gpu)).Agrees);
    }
}

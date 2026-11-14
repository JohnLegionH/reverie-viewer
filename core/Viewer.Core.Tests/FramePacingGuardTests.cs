// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A measuring run's frame times must not be presentation-paced: a frame figure taken while presents wait on the display
/// (vsync, a driver frame-rate limit, a compositor) is the pacing interval, not the scene's cost, and hides any cost below it.
/// The shapes below are the ones seen in stored runs: free running at about 0.8 ms a frame, and the same scene paced so
/// that about one frame in ten took about 7 ms and the 95th percentile landed on it.
/// </summary>
public class FramePacingGuardTests
{
    private static List<double> Jittered(int n, double centre, double spread, int seed)
    {
        var r = new Random(seed);
        var v = new List<double>(n);
        for (int i = 0; i < n; i++) v.Add(centre * (1 + spread * (r.NextDouble() * 2 - 1)));
        return v;
    }

    /// <summary>Mostly short frames, a share of them at one paced interval.</summary>
    private static List<double> Paced(int n, double fastMs, double pacedMs, double pacedShare, int seed)
    {
        var r = new Random(seed);
        var v = new List<double>(n);
        for (int i = 0; i < n; i++)
            v.Add(r.NextDouble() < pacedShare ? pacedMs * (1 + 0.03 * (r.NextDouble() * 2 - 1)) : fastMs * (1 + 0.4 * (r.NextDouble() * 2 - 1)));
        return v;
    }

    [Fact]
    public void A_free_running_scene_is_not_capped()
    {
        var v = Paced(20000, 0.8, 1.3, 0.0, 1);
        var verdict = FramePacingGuard.Evaluate(v, 60);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Fact]
    public void Frames_all_at_the_refresh_interval_are_capped_and_the_message_names_it()
    {
        var verdict = FramePacingGuard.Evaluate(Jittered(2000, 1000.0 / 60, 0.01, 2), 60);
        Assert.True(verdict.Capped);
        Assert.Contains("60", verdict.Message);
        Assert.Contains("refresh", verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(verdict.NearRefreshFraction > 0.9);
    }

    [Fact]
    public void Frames_at_two_refresh_intervals_are_capped_too()
    {
        var verdict = FramePacingGuard.Evaluate(Jittered(2000, 2000.0 / 144, 0.01, 3), 144);
        Assert.True(verdict.Capped);
    }

    [Fact]
    public void The_same_shape_is_caught_when_the_refresh_rate_is_unknown()
    {
        var verdict = FramePacingGuard.Evaluate(Paced(20000, 0.8, 7.0, 0.12, 5), 0);
        Assert.True(verdict.Capped, verdict.Message);
        Assert.Contains("unknown", verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_few_scattered_hitches_are_not_a_cap()
    {
        var v = Paced(20000, 0.8, 1.3, 0.0, 6);
        var r = new Random(7);
        for (int i = 0; i < 150; i++) v[r.Next(v.Count)] = 20 + r.NextDouble() * 400;   // 0.75 % of frames, all different lengths
        var verdict = FramePacingGuard.Evaluate(v, 60);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Fact]
    public void A_scene_that_is_simply_slow_but_not_clustered_is_not_a_cap()
    {
        var v = Jittered(5000, 12.0, 0.45, 8);   // 6.6 to 17.4 ms, spread across the range
        var verdict = FramePacingGuard.Evaluate(v, 144);
        Assert.False(verdict.Capped, verdict.Message);
    }

    [Fact]
    public void Too_few_frames_to_judge_is_reported_as_not_judged_and_not_capped()
    {
        var verdict = FramePacingGuard.Evaluate(Jittered(20, 16.7, 0.01, 9), 60);
        Assert.False(verdict.Capped);
        Assert.Contains("too few", verdict.Message, StringComparison.OrdinalIgnoreCase);
    }
}

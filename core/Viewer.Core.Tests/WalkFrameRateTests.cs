// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The walk cycle's playback must not depend on how often the viewer draws. The same walk, stepped at 30, 60 and 144 frames a second,
/// has to move the cycle's clock by the same amount (the clock decides how far the feet have travelled: cycle time times the stride),
/// settle on the same rate, and reach the same rate when it stops. Pure; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class WalkFrameRateTests
{
    /// <summary>The cycle time reached after <paramref name="seconds"/> of the walk script, at <paramref name="fps"/> frames a second:
    /// real time plus the skew, which is what the feet's travelled distance follows.</summary>
    private static (double CycleTime, float Rate) Walk(int fps, Vector2 velocity, double seconds, double standingSeconds = 0)
    {
        var clip = WalkAdjustTests.Stride();
        var table = FootSpeedTable.Of(WalkAdjustTests.Rig(), clip);
        var p = new WalkPlayback();
        p.Start(0, clip, table);
        int frames = (int)Math.Round(seconds * fps);
        for (int i = 0; i <= frames; i++)
            p.Step(i / (double)fps, velocity, 0f);
        double end = frames / (double)fps;
        if (standingSeconds > 0)
        {
            int more = (int)Math.Round(standingSeconds * fps);
            for (int i = 1; i <= more; i++)
                p.Step(end + i / (double)fps, Vector2.Zero, 0f);
            end += more / (double)fps;
        }
        return (end + p.Skew, p.Rate);
    }

    [Theory]
    [InlineData(2.9f)]   // faster than the cycle's feet: the rate climbs to its cap
    [InlineData(1.3f)]   // close to the cycle's own speed: the rate settles near 1
    public void The_same_walk_moves_the_cycle_clock_the_same_distance_at_30_60_and_144_frames_a_second(float speed)
    {
        var at60 = Walk(60, new Vector2(speed, 0), 8);
        foreach (int fps in new[] { 30, 144 })
        {
            var other = Walk(fps, new Vector2(speed, 0), 8);
            // within 1 % of the cycle time travelled; the steps differ only in how finely the same curve is cut
            Assert.InRange(other.CycleTime, at60.CycleTime * 0.99, at60.CycleTime * 1.01);
            Assert.InRange(other.Rate, at60.Rate - 0.03f, at60.Rate + 0.03f);
        }
        Assert.NotEqual(8.0, at60.CycleTime, 2);   // the rate really moved the clock, so the comparison above means something
    }

    [Fact]
    public void Slowing_to_a_stop_after_a_walk_reaches_the_same_rate_at_every_frame_rate()
    {
        var at60 = Walk(60, new Vector2(2.9f, 0), 4, standingSeconds: 1.5);
        foreach (int fps in new[] { 30, 144 })
        {
            var other = Walk(fps, new Vector2(2.9f, 0), 4, standingSeconds: 1.5);
            Assert.InRange(other.Rate, at60.Rate - 0.03f, at60.Rate + 0.03f);
            Assert.InRange(other.CycleTime, at60.CycleTime * 0.99, at60.CycleTime * 1.01);
        }
    }

    [Fact]
    public void One_long_frame_moves_the_clock_by_the_time_it_took_so_the_walk_ends_where_a_smooth_one_does()
    {
        // one 1 s hitch in the middle of a steady walk, against the same walk drawn smoothly
        var clip = WalkAdjustTests.Stride();
        var table = FootSpeedTable.Of(WalkAdjustTests.Rig(), clip);
        var smooth = new WalkPlayback();
        var hitch = new WalkPlayback();
        smooth.Start(0, clip, table);
        hitch.Start(0, clip, table);
        var v = new Vector2(2.9f, 0);
        for (int i = 0; i <= 60 * 6; i++)
        {
            smooth.Step(i / 60.0, v, 0f);
            bool skipped = i > 180 && i < 240;   // the hitch draws nothing for a second
            if (!skipped)
                hitch.Step(i / 60.0, v, 0f);
        }
        Assert.InRange(hitch.Skew, smooth.Skew * 0.97, smooth.Skew * 1.03);
    }
}

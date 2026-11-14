// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The far distance of the agent's updates follows the official viewer: half the draw distance (50 m at least) while arriving, then a
/// tenth of the draw distance more each second, and a lower draw distance at once.</summary>
public class FarRampTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Theory]
    [InlineData(1024f, 512f)]
    [InlineData(128f, 64f)]
    [InlineData(64f, 50f)]
    [InlineData(80f, 50f)]
    public void While_arriving_the_far_distance_is_half_the_draw_distance_and_at_least_50(float draw, float expected)
    {
        var r = new FarRamp(S(0));
        Assert.Equal(expected, r.Next(S(1), arriving: true, draw));
        Assert.Equal(expected, r.Next(S(30), arriving: true, draw));
    }

    [Fact]
    public void Afterwards_it_grows_a_tenth_of_the_draw_distance_each_second_up_to_the_draw_distance()
    {
        var r = new FarRamp(S(1));
        Assert.Equal(512f, r.Next(S(1), true, 1024f));
        Assert.Equal(512f, r.Next(S(1.5), false, 1024f));          // less than a second since the last step: no change
        Assert.Equal(614.4f, r.Next(S(2.1), false, 1024f), 1);
        Assert.Equal(716.8f, r.Next(S(3.2), false, 1024f), 1);
        float last = 0;
        for (double t = 4.3; t < 12; t += 1.1) last = r.Next(S(t), false, 1024f);
        Assert.Equal(1024f, last);
    }

    [Fact]
    public void A_lower_draw_distance_is_sent_at_once_and_a_higher_one_is_reached_by_steps()
    {
        var r = new FarRamp(S(0));
        r.Next(S(1), true, 256f);
        for (double t = 2.1; t < 10; t += 1.1) r.Next(S(t), false, 256f);
        Assert.Equal(256f, r.Next(S(11), false, 256f));
        Assert.Equal(128f, r.Next(S(11.1), false, 128f));
        Assert.Equal(230.4f, r.Next(S(12.2), false, 1024f), 1);
        Assert.Equal(332.8f, r.Next(S(13.3), false, 1024f), 1);
    }
}

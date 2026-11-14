// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The time-of-day choice: fixed points of the day on the region's own day cycle, and Region following its clock.</summary>
public class SkyChoiceTests
{
    private static string Synthetic => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", "synthetic-environment.llsd.xml"));
    private static RegionEnvironment Region => RegionEnvironment.FromLlsd(Synthetic, out _)!;

    [Theory]
    [InlineData(SkyChoice.Midnight, 0.0)]
    [InlineData(SkyChoice.Sunrise, 0.25)]
    [InlineData(SkyChoice.Midday, 0.5)]
    [InlineData(SkyChoice.Sunset, 0.75)]
    public void A_fixed_choice_is_a_point_of_the_day(SkyChoice c, double f) => Assert.Equal(f, SkyChoices.Fraction(c));

    [Fact]
    public void Region_has_no_fixed_point() => Assert.Null(SkyChoices.Fraction(SkyChoice.Region));

    [Fact]
    public void The_key_cycles_through_every_choice_and_back_to_region()
    {
        var seen = new List<SkyChoice> { SkyChoice.Region };
        for (int i = 0; i < 5; i++) seen.Add(SkyChoices.Next(seen[^1]));
        Assert.Equal([SkyChoice.Region, SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.Sunset, SkyChoice.Midnight, SkyChoice.Region], seen);
    }

    [Theory]
    [InlineData("midday", SkyChoice.Midday)]
    [InlineData("Noon", SkyChoice.Midday)]
    [InlineData(" MIDNIGHT ", SkyChoice.Midnight)]
    [InlineData("sunrise", SkyChoice.Sunrise)]
    [InlineData("sunset", SkyChoice.Sunset)]
    [InlineData("region", SkyChoice.Region)]
    public void Names_parse(string text, SkyChoice want)
    {
        Assert.True(SkyChoices.TryParse(text, out var c));
        Assert.Equal(want, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("dusk")]
    public void Unknown_names_do_not_parse(string? text) => Assert.False(SkyChoices.TryParse(text, out _));

    [Fact]
    public void Midday_puts_the_sun_overhead_and_midnight_puts_it_below_the_region_clock_notwithstanding()
    {
        var e = Region;
        double unix = 3600;   // the region clock reads 0.25 (morning)
        var day = e.SkyFor(SkyChoice.Midday, unix, out var fd);
        var night = e.SkyFor(SkyChoice.Midnight, unix, out var fn);
        Assert.Equal(0.5, fd, 6);
        Assert.Equal(0.0, fn, 6);
        Assert.True(day.SunDirection.Z > 0.95f, "midday sun overhead");
        Assert.True(night.SunDirection.Z < 0.5f, "midnight: the sun is not high");
        Assert.NotEqual(day.SunDirection, night.SunDirection);
        Assert.NotEqual(day.SunColor, night.SunColor);
    }

    [Fact]
    public void A_fixed_choice_evaluates_the_regions_own_cycle()
    {
        var e = Region;
        var s = e.SkyFor(SkyChoice.Sunrise, 0, out _);
        Assert.Equal(e.SkyAt(0.25).SunDirection, s.SunDirection);
        Assert.Equal(e.SkyAt(0.25).Ambient, s.Ambient);
    }

    [Fact]
    public void Region_restores_the_regions_own_clock()
    {
        var e = Region;
        foreach (double unix in new[] { 0.0, 3600, 7200, 10800 })
        {
            var s = e.SkyFor(SkyChoice.Region, unix, out var f);
            Assert.Equal(e.DayFraction(unix), f, 9);
            Assert.Equal(e.SkyAt(f), s);
        }
    }

    [Fact]
    public void A_region_without_a_day_cycle_uses_the_default_sky_and_it_has_a_night()
    {
        var e = RegionEnvironment.Default;
        var day = e.SkyFor(SkyChoice.Midday, 0, out _);
        var night = e.SkyFor(SkyChoice.Midnight, 0, out _);
        Assert.True(day.SunDirection.Z > 0.8f);
        Assert.True(night.SunDirection.Z < 0f, "the sun is down at midnight");
        Assert.True(night.MoonDirection.Z > 0.5f, "the moon is up at midnight");
        Assert.True(night.Ambient.X < day.Ambient.X);
        // Region on the default environment is the unchanged single sky
        Assert.Equal(e.SkyAt(0), e.SkyFor(SkyChoice.Region, 0, out _));
    }
}

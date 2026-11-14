// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Globalization;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The clock in the menu bar shows the time of day in the person's format; test pictures give it a fixed text so pictures taken hours apart match.</summary>
public class ClockTextTests
{
    [Fact]
    public void The_clock_shows_the_short_time_of_the_culture()
    {
        var now = new DateTime(2026, 1, 2, 15, 7, 0);
        Assert.Equal("3:07 PM", ClockText.For(now, null, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("15:07", ClockText.For(now, null, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void A_fixed_text_replaces_the_time_whatever_it_is()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("12:00 PM", ClockText.For(new DateTime(2026, 1, 2, 15, 7, 0), "12:00 PM", culture));
        Assert.Equal("12:00 PM", ClockText.For(new DateTime(2026, 6, 9, 3, 41, 0), "12:00 PM", culture));
    }

    [Fact]
    public void An_empty_fixed_text_means_the_real_time()
    {
        Assert.Equal("3:07 PM", ClockText.For(new DateTime(2026, 1, 2, 15, 7, 0), "", CultureInfo.GetCultureInfo("en-US")));
    }
}

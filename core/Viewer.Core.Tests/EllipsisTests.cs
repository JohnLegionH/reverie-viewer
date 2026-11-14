// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A hint that does not fit its field ends with an ellipsis instead of being cut off mid-letter.</summary>
public class EllipsisTests
{
    // every character is 10 wide, the ellipsis one too
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void Text_that_fits_is_unchanged()
    {
        Assert.Equal("Say something", Ellipsis.Fit("Say something", 130, Measure));
        Assert.Equal("Say something", Ellipsis.Fit("Say something", 500, Measure));
    }

    [Fact]
    public void Text_that_does_not_fit_is_cut_where_the_ellipsis_still_fits_and_ends_with_it()
    {
        string cut = Ellipsis.Fit("Say something", 100, Measure);
        Assert.Equal("Say somet…", cut);
        Assert.True(Measure(cut) <= 100);
    }

    [Fact]
    public void A_space_before_the_cut_is_not_left_hanging_in_front_of_the_ellipsis()
    {
        Assert.Equal("Say…", Ellipsis.Fit("Say something", 50, Measure));   // "Say " + "…" is 5 characters; the space is dropped
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_field_too_narrow_for_even_the_ellipsis_shows_nothing(float width)
    {
        Assert.Equal("", Ellipsis.Fit("Say something", width, Measure));
    }

    [Fact]
    public void Nothing_to_fit_is_nothing()
    {
        Assert.Equal("", Ellipsis.Fit("", 100, Measure));
        Assert.Equal("", Ellipsis.Fit(null, 100, Measure));
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // each char is 10 wide, a pair is two chars: 20; "ab😀cd" with room for 5 chars cuts before the pair, not in it
        string cut = Ellipsis.Fit("ab\U0001F600cd", 40, Measure);
        Assert.Equal("ab…", cut);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Chat;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Nearby chat shown in the world fades the way the official viewer's chat toasts do (indra/newview/llfloaterimnearbychathandler.cpp
/// 136-137 and 265-266, indra/newview/lltoast.cpp setFading, indra/llui/lluictrl.cpp getCurrentTransparency): a line shows for the
/// toast lifetime (NearbyToastLifeTime, 23 s), then for the fading time (NearbyToastFadingTime, 3 s) at half its opacity, then it is gone.
/// A lifetime of 0 keeps the lines until newer lines push them out.
/// </summary>
public class NearbyChatFadeTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);

    private static ChatLine Line(double secondsAgo, string text) => new(Now.AddSeconds(-secondsAgo), ChatLineStyle.Agent, Guid.Empty, "Ann", text);

    [Fact]
    public void The_defaults_are_the_official_ones()
    {
        Assert.Equal(23, Settings.ChatOverlaySeconds.Default);
        Assert.Equal(3, Settings.ChatOverlayFadeSeconds.Default);
    }

    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(10, 1.0f)]
    [InlineData(23, 1.0f)]
    [InlineData(23.5, 0.5f)]
    [InlineData(26, 0.5f)]
    public void A_line_is_whole_for_the_lifetime_and_half_for_the_fading_time(double age, float opacity)
    {
        var shown = NearbyOverlayRules.Shown([Line(age, "x")], Now, life: 23, fade: 3, shown: 6);
        var only = Assert.Single(shown);
        Assert.Equal(opacity, only.Opacity);
    }

    [Fact]
    public void A_line_is_gone_after_the_lifetime_and_the_fading_time()
    {
        Assert.Empty(NearbyOverlayRules.Shown([Line(26.01, "x")], Now, 23, 3, 6));
    }

    [Fact]
    public void With_no_fading_time_a_line_is_gone_when_its_lifetime_ends()
    {
        Assert.Single(NearbyOverlayRules.Shown([Line(23, "x")], Now, 23, 0, 6));
        Assert.Empty(NearbyOverlayRules.Shown([Line(23.01, "x")], Now, 23, 0, 6));
    }

    [Fact]
    public void A_lifetime_of_zero_keeps_lines_until_newer_ones_push_them_out()
    {
        var lines = Enumerable.Range(0, 8).Select(i => Line(3600 - i, "line " + i)).ToList();
        var shown = NearbyOverlayRules.Shown(lines, Now, life: 0, fade: 3, shown: 6);
        Assert.Equal(["line 2", "line 3", "line 4", "line 5", "line 6", "line 7"], shown.Select(s => s.Line.Text));
        Assert.All(shown, s => Assert.Equal(1.0f, s.Opacity));
    }

    [Fact]
    public void Old_lines_fade_and_go_while_newer_ones_stay_whole()
    {
        var lines = new[] { Line(40, "gone"), Line(25, "fading"), Line(5, "new") };
        var shown = NearbyOverlayRules.Shown(lines, Now, 23, 3, 6);
        Assert.Equal([("fading", 0.5f), ("new", 1.0f)], shown.Select(s => (s.Line.Text, s.Opacity)));
    }

    [Fact]
    public void A_faded_line_is_drawn_with_half_opacity_colour_codes()
    {
        var line = Line(0, "hello");
        string whole = ChatText.Format(line, ThemeSpec.Default, StringTable.English(), showTime: false);
        string half = ChatText.Format(line, ThemeSpec.Default, StringTable.English(), showTime: false, opacity: 0.5f);
        Assert.DoesNotMatch(@"\[color=#[0-9a-f]{8}\]", whole);
        Assert.Matches(@"\[color=#[0-9a-f]{6}80\]", half);
    }
}

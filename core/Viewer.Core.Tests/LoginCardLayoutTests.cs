// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The login card's size follows the window and the interface size and never runs off the window. Pure data; no engine.</summary>
public class LoginCardLayoutTests
{
    [Fact]
    public void The_card_takes_a_share_of_the_window_width_between_its_least_and_most()
    {
        Assert.Equal(460.8f, LoginCardLayout.Width(1152), 1);
        Assert.Equal(LoginCardLayout.MaxWidth, LoginCardLayout.Width(3840));
        Assert.Equal(LoginCardLayout.MinWidth, LoginCardLayout.Width(800));
    }

    [Fact]
    public void A_wider_window_gives_a_wider_card_until_the_most()
    {
        Assert.True(LoginCardLayout.Width(1280) > LoginCardLayout.Width(1100));
        Assert.True(LoginCardLayout.Width(1100) > LoginCardLayout.Width(900));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void At_every_interface_size_the_card_fits_beside_its_margins_in_the_test_window(int percent)
    {
        float w = 1152f * 100 / percent, h = 648f * 100 / percent;
        float card = LoginCardLayout.Width(w);
        Assert.True(card + 2 * LoginCardLayout.SideMargin <= w, $"{percent} %: card {card} in {w}");
        Assert.True(LoginCardLayout.MaxHeight(h) <= h - 2 * LoginCardLayout.VerticalMargin);
        Assert.True(LoginCardLayout.MaxHeight(h) > 0);
    }

    [Fact]
    public void A_window_narrower_than_the_least_card_still_holds_the_card()
    {
        float card = LoginCardLayout.Width(320);
        Assert.True(card + 2 * LoginCardLayout.SideMargin <= 320);
        Assert.True(card > 0);
    }

    [Fact]
    public void The_card_is_never_taller_than_the_window_less_the_margins()
    {
        Assert.Equal(568f, LoginCardLayout.MaxHeight(648));
        Assert.Equal(244f, LoginCardLayout.MaxHeight(324));
        Assert.Equal(0f, LoginCardLayout.MaxHeight(50));
    }
}

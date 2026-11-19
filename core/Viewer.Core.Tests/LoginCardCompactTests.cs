// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The login card is laid out tightly in a short window (a 1152 x 648 window at 200 percent), so Log in shows without scrolling.</summary>
public class LoginCardCompactTests
{
    [Theory]
    [InlineData(324, true)]    // 200 percent
    [InlineData(399, true)]
    [InlineData(432, false)]   // 150 percent
    [InlineData(648, false)]   // 100 percent
    public void Only_a_short_window_gets_the_compact_card(float height, bool compact) => Assert.Equal(compact, LoginCardLayout.Compact(height));

    [Fact]
    public void The_compact_card_has_closer_rows_less_padding_a_smaller_header_and_a_shorter_status_area()
    {
        Assert.True(LoginCardLayout.RowSeparation(324) < LoginCardLayout.RowSeparation(648));
        Assert.True(LoginCardLayout.CardPadding(324) < LoginCardLayout.CardPadding(648));
        Assert.True(LoginCardLayout.HeaderMark(324) < LoginCardLayout.HeaderMark(648));
        Assert.True(LoginCardLayout.HeaderGap(324) < LoginCardLayout.HeaderGap(648));
        Assert.True(LoginCardLayout.StatusMaxHeight(324) < LoginCardLayout.StatusMaxHeight(648));
    }

    [Fact]
    public void The_normal_card_keeps_its_sizes()
    {
        Assert.Equal((6, 20, 22, 6, 100f), (LoginCardLayout.RowSeparation(648), LoginCardLayout.CardPadding(648), LoginCardLayout.HeaderMark(648),
            LoginCardLayout.HeaderGap(648), LoginCardLayout.StatusMaxHeight(648)));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>When the Nearby list is read again.</summary>
public class NearbyRefreshTests
{
    [Fact]
    public void It_is_read_at_once_and_then_once_a_second()
    {
        var gate = new NearbyRefresh();
        Assert.True(gate.Due(0.016));
        Assert.False(gate.Due(0.5));
        Assert.False(gate.Due(0.4));
        Assert.True(gate.Due(0.11));
    }

    [Fact]
    public void A_click_holds_the_list_still_long_enough_for_a_double_click()
    {
        var gate = new NearbyRefresh();
        Assert.True(gate.Due(0.016));
        Assert.False(gate.Due(0.95));
        gate.Clicked();
        Assert.False(gate.Due(0.59));
        Assert.True(gate.Due(0.02));
    }

    [Fact]
    public void A_click_never_brings_the_next_read_forward()
    {
        var gate = new NearbyRefresh();
        Assert.True(gate.Due(0.016));
        gate.Clicked();
        Assert.False(gate.Due(0.9));
        Assert.True(gate.Due(0.11));
    }

    [Fact]
    public void Shown_again_after_being_hidden_it_is_read_at_once()
    {
        var gate = new NearbyRefresh();
        Assert.True(gate.Due(0.016));
        gate.Hidden();
        Assert.True(gate.Due(0.016));
    }
}

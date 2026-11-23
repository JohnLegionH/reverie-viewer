// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Another avatar is drawn only once it is "fully loaded" as the official viewer judges it (<see cref="AvatarArrivalGate"/>): not while a part is missing
/// or a bake is downloading, then only 3 s after that ends (each worn mesh arriving starts the wait again); after its first showing, only a missing part
/// hides it again, for 1 s past its end. Pure (times passed in), runs in parallel.
/// </summary>
public class AvatarArrivalGateTests
{
    [Fact]
    public void Not_drawn_while_a_part_is_missing()
    {
        var g = new AvatarArrivalGate(0);
        for (double t = 0; t < AvatarArrivalGate.GreyAfterSeconds; t += 0.5) Assert.False(g.Update(t, missingParts: true, bakesPending: false));
    }

    [Fact]
    public void Not_drawn_while_a_bake_is_downloading()
    {
        var g = new AvatarArrivalGate(0);
        for (double t = 0; t < AvatarArrivalGate.GreyAfterSeconds; t += 0.5) Assert.False(g.Update(t, missingParts: false, bakesPending: true));
    }

    [Fact]
    public void Drawn_three_seconds_after_loading_ends_and_not_before()
    {
        var g = new AvatarArrivalGate(0);
        Assert.False(g.Update(1.0, false, true));
        Assert.False(g.Update(2.0, false, false));   // loading ended at 1.0..2.0: the wait starts
        Assert.False(g.Update(4.0, false, false));
        Assert.True(g.Update(5.1, false, false));
        Assert.True(g.EverShown);
    }

    [Fact]
    public void A_worn_mesh_arriving_starts_the_wait_again()
    {
        var g = new AvatarArrivalGate(0);
        Assert.False(g.Update(0.0, false, false));
        g.WornArrived(2.5);
        Assert.False(g.Update(3.2, false, false));
        Assert.False(g.Update(5.4, false, false));
        Assert.True(g.Update(5.6, false, false));
    }

    [Fact]
    public void After_the_first_showing_a_bake_downloading_does_not_hide_it()
    {
        var g = new AvatarArrivalGate(0);
        Assert.True(g.Update(3.5, false, false));
        Assert.True(g.Update(10, false, true));   // an outfit change: the last good bakes stay
        g.WornArrived(10.5);
        Assert.True(g.Update(11, false, false));   // a piece put on later does not make a cloud
    }

    [Fact]
    public void After_the_first_showing_a_missing_part_hides_it_until_one_second_after_it_returns()
    {
        var g = new AvatarArrivalGate(0);
        Assert.True(g.Update(3.5, false, false));
        Assert.False(g.Update(10, true, false));
        Assert.False(g.Update(11, true, false));   // the part is back after this frame
        Assert.False(g.Update(11.5, false, false));
        Assert.False(g.Update(11.9, false, false));
        Assert.True(g.Update(12.1, false, false));
    }

    [Fact]
    public void Shown_anyway_after_the_give_up_time()
    {
        var g = new AvatarArrivalGate(5);
        Assert.False(g.Update(5 + AvatarArrivalGate.GreyAfterSeconds - 1, true, true));
        Assert.True(g.Update(5 + AvatarArrivalGate.GreyAfterSeconds + 0.1, true, true));
    }

    [Fact]
    public void An_avatar_handed_over_from_another_region_is_drawn_at_once_even_with_no_look()
    {
        var g = new AvatarArrivalGate(0);
        g.TakeOver();
        Assert.True(g.Update(0, missingParts: true, bakesPending: true));
        Assert.True(g.EverShown);
    }

    [Fact]
    public void An_avatar_handed_over_has_no_second_gate_when_its_look_goes_missing_later()
    {
        var g = new AvatarArrivalGate(0);
        g.TakeOver();
        Assert.True(g.Update(0, false, false));
        Assert.True(g.Update(5, true, true));
        Assert.True(g.Update(5.5, true, true));
        Assert.True(g.Update(7, true, false));
    }

    [Fact]
    public void An_avatar_whose_look_never_arrives_is_drawn_in_the_fallback_after_the_wait_and_not_before()
    {
        var g = new AvatarArrivalGate(0);
        Assert.False(g.Update(0, true, true));
        Assert.False(g.Update(AvatarArrivalGate.GreyAfterSeconds - 0.1, true, true));
        Assert.False(g.Fallback);
        Assert.True(g.Update(AvatarArrivalGate.GreyAfterSeconds + 0.1, true, true));
        Assert.True(g.Fallback);
    }

    [Fact]
    public void An_avatar_whose_bakes_never_load_is_drawn_in_the_fallback_after_the_wait()
    {
        var g = new AvatarArrivalGate(0);
        Assert.False(g.Update(10, false, true));
        Assert.True(g.Update(AvatarArrivalGate.GreyAfterSeconds + 0.1, false, true));
        Assert.True(g.Fallback);
    }

    [Fact]
    public void The_fallback_ends_when_the_look_is_complete_and_the_avatar_stays_drawn()
    {
        var g = new AvatarArrivalGate(0);
        Assert.True(g.Update(16, true, true));
        Assert.True(g.Fallback);
        Assert.True(g.Update(17, false, false));
        Assert.False(g.Fallback);
        Assert.True(g.Update(17.5, false, false));
    }

    [Fact]
    public void An_avatar_already_shown_whose_look_goes_missing_for_good_is_drawn_again_in_the_fallback()
    {
        var g = new AvatarArrivalGate(0);
        Assert.True(g.Update(3.5, false, false));
        Assert.False(g.Update(10, true, false));
        Assert.True(g.Update(10 + AvatarArrivalGate.GreyAfterSeconds + 0.1, true, false));
        Assert.True(g.Fallback);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Another avatar's pieces removed in a burst stay drawn for about half a second and go together, or with the avatar if it goes in that time; a single
/// removal applies at once (<see cref="WornRemovalHold"/>). A body part a removed mesh stopped hiding is uncovered only after the same half second
/// (<see cref="BodyUncoverDelay"/>). Pure (times passed in), runs in parallel.
/// </summary>
public class WornRemovalHoldTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"), B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void A_single_removal_applies_at_once()
    {
        var h = new WornRemovalHold();
        Assert.False(h.Removed(A, 1, 10.0));
        Assert.False(h.Removed(A, 2, 12.0));   // well apart: two single removals
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void A_burst_is_held_and_goes_together_half_a_second_after_its_latest_removal()
    {
        var h = new WornRemovalHold();
        Assert.False(h.Removed(A, 1, 10.00));
        Assert.True(h.Removed(A, 2, 10.02));
        Assert.True(h.Removed(A, 3, 10.20));
        Assert.True(h.Removed(A, 4, 10.40));
        Assert.Empty(h.Due(10.80));
        Assert.True(h.Holding(A));
        Assert.Equal([2u, 3u, 4u], h.Due(10.91).Order());
        Assert.Equal(0, h.Count);
        Assert.False(h.Holding(A));
    }

    [Fact]
    public void A_burst_is_never_held_longer_than_the_cap()
    {
        var h = new WornRemovalHold();
        h.Removed(A, 1, 0);
        uint piece = 2;
        double t = 0.1;
        for (; t < WornRemovalHold.MaxHoldSeconds + 0.1; t += 0.2) h.Removed(A, piece++, t);
        Assert.NotEmpty(h.Due(0.1 + WornRemovalHold.MaxHoldSeconds + 0.01));
    }

    [Fact]
    public void The_wearer_going_takes_every_held_piece_with_it()
    {
        var h = new WornRemovalHold();
        h.Removed(A, 1, 5.0);
        h.Removed(A, 2, 5.01);
        h.Removed(A, 3, 5.02);
        h.Removed(B, 9, 5.0);
        h.Removed(B, 10, 5.01);
        Assert.Equal([2u, 3u], h.WearerGone(A).Order());
        Assert.True(h.Holding(B));
        Assert.Equal([10u], h.Due(6.0));
    }

    [Fact]
    public void Wearers_are_judged_apart()
    {
        var h = new WornRemovalHold();
        Assert.False(h.Removed(A, 1, 5.0));
        Assert.False(h.Removed(B, 2, 5.01));   // another wearer's removal is not part of A's burst
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void A_held_piece_sent_again_is_no_longer_held()
    {
        var h = new WornRemovalHold();
        h.Removed(A, 1, 5.0);
        h.Removed(A, 2, 5.01);
        Assert.True(h.Forget(2));
        Assert.False(h.Forget(2));
        Assert.Empty(h.Due(9));
    }

    [Fact]
    public void An_uncovered_part_stays_hidden_for_the_delay_then_shows()
    {
        var u = new BodyUncoverDelay();
        var before = new HashSet<string> { "upper", "lower" };
        var after = new HashSet<string> { "lower" };
        Assert.Equal(before, u.Next(before, after, 1.0));
        Assert.True(u.Pending);
        Assert.Equal(before, u.Next(before, after, 1.4));
        Assert.Equal(after, u.Next(before, after, 1.0 + BodyUncoverDelay.Seconds + 0.01));
        Assert.False(u.Pending);
    }

    [Fact]
    public void A_newly_hidden_part_is_hidden_at_once_and_nothing_waits()
    {
        var u = new BodyUncoverDelay();
        var before = new HashSet<string> { "lower" };
        var after = new HashSet<string> { "lower", "upper" };
        Assert.Equal(after, u.Next(before, after, 1.0));
        Assert.False(u.Pending);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A teleport by region name asks the map which region it is before it sends anything, which takes seconds on a slow grid. Cancelling
/// during that wait must stop it: the teleport must not be sent once the user has cancelled. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class PendingNamedTeleportTests
{
    [Fact]
    public void Cancel_stops_the_teleport_that_is_still_finding_its_region()
    {
        var pending = new PendingNamedTeleport();
        var token = pending.Begin(CancellationToken.None);
        Assert.False(token.IsCancellationRequested);
        Assert.True(pending.Cancel());
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_with_nothing_pending_does_nothing_and_a_second_cancel_finds_nothing()
    {
        var pending = new PendingNamedTeleport();
        Assert.False(pending.Cancel());
        pending.Begin(CancellationToken.None);
        Assert.True(pending.Cancel());
        Assert.False(pending.Cancel());
    }

    [Fact]
    public void A_new_teleport_by_name_stops_the_one_before_it()
    {
        var pending = new PendingNamedTeleport();
        var first = pending.Begin(CancellationToken.None);
        var second = pending.Begin(CancellationToken.None);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void The_sessions_own_end_stops_it_too()
    {
        using var ending = new CancellationTokenSource();
        var pending = new PendingNamedTeleport();
        var token = pending.Begin(ending.Token);
        ending.Cancel();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void A_teleport_that_finished_can_no_longer_be_cancelled_and_does_not_stop_a_later_one()
    {
        var pending = new PendingNamedTeleport();
        var done = pending.Begin(CancellationToken.None);
        pending.End(done);
        Assert.False(pending.Cancel());
        Assert.False(done.IsCancellationRequested);
    }
}

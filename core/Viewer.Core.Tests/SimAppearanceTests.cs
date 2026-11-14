// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

public class SimAppearanceTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Something_already_there_is_returned_without_waiting()
    {
        var thing = new object();
        int sleeps = 0;

        var found = SimAppearance.WaitFor(() => thing, TimeSpan.FromSeconds(1), Poll, _ => sleeps++);

        Assert.Same(thing, found);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void Something_that_appears_while_waiting_is_returned_after_the_looks_it_took()
    {
        var thing = new object();
        int looks = 0, sleeps = 0;

        var found = SimAppearance.WaitFor(() => ++looks >= 3 ? thing : null, TimeSpan.FromSeconds(1), Poll, _ => sleeps++);

        Assert.Same(thing, found);
        Assert.Equal(3, looks);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void Something_that_never_appears_is_given_up_on_at_the_limit_and_the_wait_is_bounded()
    {
        var slept = TimeSpan.Zero;

        var found = SimAppearance.WaitFor<object>(() => null, TimeSpan.FromMilliseconds(500), Poll, d => slept += d);

        Assert.Null(found);
        Assert.InRange(slept.TotalMilliseconds, 450, 550);   // ten polls of 50 ms: no more than the limit
    }
}

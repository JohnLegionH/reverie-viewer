// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Scheduling;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Letting go of a dropped region's objects in slices. The cost of one release is a made-up number on a test clock, so the tests
/// neither wait nor depend on the machine; each test builds its own queue, so the class runs in parallel.
/// </summary>
public class SlicedReleaseTests
{
    private sealed class Clock
    {
        public double Ms;
        public double Now() => Ms;
    }

    [Fact]
    public void A_hundred_thousand_objects_are_released_without_one_slice_over_the_budget()
    {
        const double perItem = 0.01, budget = 2.0;
        var clock = new Clock();
        var items = Enumerable.Range(0, 100_000).ToList();
        int released = 0;
        var queue = new SlicedRelease<int>(items, _ => { clock.Ms += perItem; released++; });
        double worst = 0;
        int steps = 0;
        while (!queue.Done && steps < 10_000)
        {
            double before = clock.Ms;
            queue.Step(budget, clock.Now);
            worst = Math.Max(worst, clock.Ms - before);
            steps++;
        }
        Assert.Equal(100_000, released);
        Assert.Equal(0, queue.Remaining);
        Assert.True(worst <= budget + perItem + 1e-9, $"the longest slice cost {worst} ms against a budget of {budget} ms");
        Assert.InRange(steps, 400, 600);   // 100,000 x 0.01 ms = 1,000 ms of work in slices of about 2 ms
    }

    [Fact]
    public void Every_slice_releases_at_least_one_item_whatever_the_budget()
    {
        var clock = new Clock();
        var order = new List<int>();
        var queue = new SlicedRelease<int>([1, 2, 3], i => { clock.Ms += 5; order.Add(i); });
        Assert.Equal(1, queue.Step(0, clock.Now));
        Assert.Equal(1, queue.Step(-3, clock.Now));
        Assert.Equal(1, queue.Remaining);
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void Items_are_released_in_the_order_given_and_each_once()
    {
        var clock = new Clock();
        var order = new List<string>();
        var queue = new SlicedRelease<string>(["instance", "mesh", "texture"], s => { clock.Ms += 1; order.Add(s); });
        for (int i = 0; i < 100 && !queue.Done; i++) queue.Step(1.5, clock.Now);
        Assert.Equal(["instance", "mesh", "texture"], order);
        Assert.Equal(0, queue.Step(10, clock.Now));
        Assert.Equal(3, order.Count);
    }

    [Fact]
    public void A_release_that_throws_does_not_stop_the_others_and_is_counted()
    {
        var clock = new Clock();
        var done = new List<int>();
        var queue = new SlicedRelease<int>([1, 2, 3, 4], i => { clock.Ms += 1; if (i == 2) throw new InvalidOperationException("engine"); done.Add(i); });
        for (int i = 0; i < 100 && !queue.Done; i++) queue.Step(10, clock.Now);
        Assert.Equal([1, 3, 4], done);
        Assert.Equal(1, queue.Failed);
        Assert.Equal(0, queue.Remaining);
    }

    [Fact]
    public void ReleaseAll_lets_go_of_everything_left_at_once()
    {
        var clock = new Clock();
        int released = 0;
        var queue = new SlicedRelease<int>(Enumerable.Range(0, 1000).ToList(), _ => { clock.Ms += 0.01; released++; });
        queue.Step(1, clock.Now);
        int first = released;
        Assert.InRange(first, 1, 999);
        Assert.Equal(1000 - first, queue.ReleaseAll());
        Assert.Equal(1000, released);
        Assert.True(queue.Done);
    }

    [Fact]
    public void An_empty_list_is_done_at_once()
    {
        var queue = new SlicedRelease<int>([], _ => { });
        Assert.True(queue.Done);
        Assert.Equal(0, queue.Step(2, () => 0));
    }
}

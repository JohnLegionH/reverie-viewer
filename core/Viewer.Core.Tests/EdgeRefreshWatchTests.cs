// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>When a neighbour's terrain edge is reported as changed, so the regions next to it rebuild their border normals once, not at every patch.</summary>
public class EdgeRefreshWatchTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static IReadOnlyDictionary<string, int> C(params (string, int)[] c) => c.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Counts_that_never_change_report_nothing()
    {
        var w = new EdgeRefreshWatch<string>();
        w.Poll(C(("a", 5)), T0);
        Assert.Empty(w.Poll(C(("a", 5)), T0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_region_that_appears_with_terrain_is_reported_once_it_has_been_quiet()
    {
        var w = new EdgeRefreshWatch<string>();
        w.Poll(C(("a", 5)), T0);
        Assert.Empty(w.Poll(C(("a", 5), ("b", 300)), T0 + TimeSpan.FromSeconds(1)));   // just changed
        Assert.Empty(w.Poll(C(("a", 5), ("b", 300)), T0 + TimeSpan.FromSeconds(1.5)));  // quiet for half a second
        Assert.Equal(["b"], w.Poll(C(("a", 5), ("b", 300)), T0 + TimeSpan.FromSeconds(2.2)));
        Assert.Empty(w.Poll(C(("a", 5), ("b", 300)), T0 + TimeSpan.FromSeconds(10)));    // reported once
    }

    [Fact]
    public void A_region_that_keeps_changing_is_reported_after_the_longest_wait_and_again_after_the_next()
    {
        var w = new EdgeRefreshWatch<string>();
        w.Poll(C(("b", 0)), T0);
        int n = 0;
        var reported = new List<double>();
        for (double s = 0.5; s <= 9; s += 0.5)
            if (w.Poll(C(("b", ++n)), T0 + TimeSpan.FromSeconds(s)).Count > 0)
                reported.Add(s);
        Assert.Equal([4.5, 9.0], reported);
    }

    [Fact]
    public void A_region_that_has_gone_is_reported_at_once()
    {
        var w = new EdgeRefreshWatch<string>();
        w.Poll(C(("a", 5), ("b", 300)), T0);
        Assert.Equal(["b"], w.Poll(C(("a", 5)), T0 + TimeSpan.FromMilliseconds(100)));
    }
}

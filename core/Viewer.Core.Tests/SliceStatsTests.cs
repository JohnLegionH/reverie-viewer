// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The slice figures the live reading keeps: count, slices over the limit, the longest, and the longest started again on request.</summary>
public class SliceStatsTests
{
    [Fact]
    public void Slices_are_counted_and_those_over_the_limit_are_counted_apart()
    {
        var s = new SliceStats(4.0);
        foreach (double ms in new[] { 0.5, 3.9, 4.0, 4.1, 12.0 }) s.Add(ms);
        Assert.Equal(5, s.Count);
        Assert.Equal(2, s.Over);   // strictly over the limit: 4.1 and 12
        Assert.Equal(12.0, s.MaxMs, 6);
        Assert.Equal(24.5, s.TotalMs, 6);
    }

    [Fact]
    public void Starting_the_longest_again_leaves_the_counts_alone()
    {
        var s = new SliceStats(4.0);
        s.Add(9.0);
        s.Add(1.0);
        s.ResetMax();
        Assert.Equal(0.0, s.MaxMs);
        Assert.Equal(2, s.Count);
        Assert.Equal(1, s.Over);
        s.Add(2.5);
        Assert.Equal(2.5, s.MaxMs, 6);
    }
}

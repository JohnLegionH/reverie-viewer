// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A dirty terrain chunk starts only once the patches its snapshot reads have been quiet
/// for the window and the region's detail textures are not still arriving; a chunk dirty for the cap starts anyway. On a
/// test clock (1 tick = 1 ms): no waits.
/// </summary>
public class TerrainSettleTests
{
    private sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    private static (TerrainSettle S, Clock C) Make(double window = 250, double cap = 1000)
    {
        var c = new Clock { Ms = 5000 };
        return (new TerrainSettle(window, cap, c.Now, 1000), c);
    }

    [Fact]
    public void A_chunk_whose_patches_keep_changing_waits()
    {
        var (s, c) = Make();
        for (int i = 0; i < 8; i++)
        {
            s.PatchChanged(5, 5);   // chunk (1, 1)
            s.Dirty((1, 1));
            c.Ms += 100;   // under the window each time, and 800 ms in all: under the cap
            Assert.False(s.Ready((1, 1), texturesPending: false, out _));
        }
    }

    [Fact]
    public void A_chunk_quiet_for_the_window_starts()
    {
        var (s, c) = Make();
        s.PatchChanged(5, 5);
        s.Dirty((1, 1));
        c.Ms += 249;
        Assert.False(s.Ready((1, 1), false, out _));
        c.Ms += 1;
        Assert.True(s.Ready((1, 1), false, out bool byCap));
        Assert.False(byCap);
        s.Start((1, 1), byCap);
        Assert.Equal(1, s.Started);
        Assert.Equal(0, s.StartedByCap);
        Assert.Equal(250, Assert.Single(s.SettleDelayMs), 3);
    }

    [Fact]
    public void A_chunk_dirty_past_the_cap_starts_though_its_patches_still_change()
    {
        var (s, c) = Make();
        s.Dirty((1, 1));
        for (int t = 0; t < 1000; t += 100)
        {
            s.PatchChanged(5, 5);
            Assert.False(s.Ready((1, 1), false, out _));
            c.Ms += 100;
        }
        s.PatchChanged(5, 5);   // changed just now, and dirty for 1000 ms: the cap
        Assert.True(s.Ready((1, 1), false, out bool byCap));
        Assert.True(byCap);
        s.Start((1, 1), byCap);
        Assert.Equal(1, s.StartedByCap);
        Assert.Equal(1000, Assert.Single(s.SettleDelayMs), 3);
    }

    [Fact]
    public void A_chunk_waits_while_the_detail_textures_arrive_up_to_the_cap()
    {
        var (s, c) = Make();
        s.Dirty((0, 0));   // the catch-up: no patch change seen
        Assert.False(s.Ready((0, 0), texturesPending: true, out _));
        c.Ms += 714;
        Assert.False(s.Ready((0, 0), true, out _));
        Assert.True(s.Ready((0, 0), false, out bool byCap));   // the textures came
        Assert.False(byCap);
        c.Ms += 286;
        Assert.True(s.Ready((0, 0), true, out byCap));   // they never come: the cap
        Assert.True(byCap);
    }

    [Fact]
    public void A_chunk_with_no_patch_change_and_no_textures_pending_starts_at_once()
    {
        var (s, _) = Make();
        s.Dirty((2, 3));
        Assert.True(s.Ready((2, 3), false, out bool byCap));
        Assert.False(byCap);
    }

    [Fact]
    public void A_change_to_a_ring_patch_holds_the_neighbour_that_reads_it()
    {
        var (s, c) = Make();
        // patch 4 is chunk 1's first and chunk 0's ring; patch 3 is chunk 0's last and chunk 1's ring; patch 5 is chunk 1's only
        s.PatchChanged(4, 6);
        Assert.False(s.Ready((0, 1), false, out _));
        Assert.False(s.Ready((1, 1), false, out _));
        Assert.True(s.Ready((2, 1), false, out _));
        s.PatchChanged(5, 9);   // y 9: chunk row 2 only
        Assert.False(s.Ready((1, 2), false, out _));
        Assert.True(s.Ready((0, 2), false, out _));
        Assert.False(s.Ready((1, 1), false, out _));   // still inside patch 4's window
        c.Ms += 250;
        Assert.True(s.Ready((0, 1), false, out _));
        Assert.True(s.Ready((1, 2), false, out _));
    }

    [Fact]
    public void A_zero_window_starts_every_chunk_at_once_as_part_3q()
    {
        var (s, _) = Make(window: 0);
        s.PatchChanged(5, 5);
        s.Dirty((1, 1));
        Assert.True(s.Ready((1, 1), texturesPending: true, out bool byCap));
        Assert.False(byCap);
    }

    [Fact]
    public void A_region_change_forgets_every_change_and_wait()
    {
        var (s, c) = Make();
        s.PatchChanged(5, 5);
        s.Dirty((1, 1));
        s.Clear(newRegion: true);
        Assert.Equal(0, s.Waiting);
        Assert.True(s.Ready((1, 1), false, out _));
        c.Ms += 10;
        s.PatchChanged(5, 5);
        Assert.Equal(10, s.Changes[^1].Ms, 3);   // measured from the new region's start
    }
}

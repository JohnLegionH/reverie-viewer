// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The simulator references the adapter hands the session: one per simulator, and never one that carries a handle the simulator
/// no longer has (the library can report a simulator before its handle is final, and a neighbour placed from that handle is drawn in the wrong place).</summary>
public class SimRefMapTests
{
    private sealed class Sim;

    [Fact]
    public void The_same_simulator_and_handle_give_the_same_reference()
    {
        var map = new SimRefMap<Sim>();
        var sim = new Sim();
        Assert.Same(map.Get(sim, 1UL << 32, "A"), map.Get(sim, 1UL << 32, "A"));
    }

    [Fact]
    public void A_handle_that_changed_gives_a_new_reference_with_the_new_handle()
    {
        var map = new SimRefMap<Sim>();
        var sim = new Sim();
        var early = map.Get(sim, 256, "A");
        var later = map.Get(sim, 262144UL << 32 | 255744UL, "A");
        Assert.NotSame(early, later);
        Assert.Equal(262144UL << 32 | 255744UL, later.Handle);
        Assert.Same(later, map.Get(sim, 262144UL << 32 | 255744UL, "A"));
    }

    [Fact]
    public void Different_simulators_give_different_references()
    {
        var map = new SimRefMap<Sim>();
        Assert.NotSame(map.Get(new Sim(), 1, "A"), map.Get(new Sim(), 1, "A"));
    }

    [Fact]
    public void A_name_that_arrives_later_keeps_the_reference()
    {
        var map = new SimRefMap<Sim>();
        var sim = new Sim();
        var first = map.Get(sim, 5, "");
        Assert.Same(first, map.Get(sim, 5, "Named"));
    }

    [Fact]
    public void A_replaced_reference_is_reported_with_the_old_and_the_new_one_and_a_first_one_is_not()
    {
        var map = new SimRefMap<Sim>();
        var seen = new List<(SimRef Old, SimRef New)>();
        map.OnReplaced = (_, o, n) => seen.Add((o, n));
        var sim = new Sim();
        var first = map.Get(sim, 0, "");
        Assert.Empty(seen);
        map.Get(sim, 0, "");
        Assert.Empty(seen);
        var later = map.Get(sim, 5UL << 32 | 9, "N");
        var one = Assert.Single(seen);
        Assert.Same(first, one.Old);
        Assert.Same(later, one.New);
        map.Get(sim, 5UL << 32 | 9, "N");
        Assert.Single(seen);
    }

    [Fact]
    public void A_reference_it_handed_out_finds_its_simulator_and_one_it_did_not_finds_none()
    {
        var map = new SimRefMap<Sim>();
        var sim = new Sim();
        var other = new Sim();
        var r = map.Get(sim, 7, "A");
        var r2 = map.Get(other, 8, "B");
        Assert.Same(sim, map.Find(r));
        Assert.Same(other, map.Find(r2));
        Assert.Null(map.Find(new SimRef(7, "A")));
        Assert.Null(map.Find(null));
    }
}

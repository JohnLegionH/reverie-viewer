// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>What is built follows the draw distance: objects beyond it are held back, ones that fall beyond are released farthest first,
/// ones that come within are presented again, and the work is sliced by a clock.</summary>
public class DrawDistanceGateTests
{
    private static readonly Vector3 Here = Vector3.Zero;
    private static Vector3 At(float x) => new(x, 0, 0);
    private static long Never() => 0;

    [Fact]
    public void With_no_distance_set_everything_is_presented()
    {
        var g = new DrawDistanceGate();
        Assert.True(g.Hold(1, At(5000), 1, Here));
        Assert.False(g.IsBeyond(At(50000), 1, Here));
    }

    [Fact]
    public void An_object_is_beyond_when_its_nearest_edge_is_past_the_distance()
    {
        var g = new DrawDistanceGate { Distance = 128 };
        Assert.True(g.Hold(1, At(100), 1, Here));
        Assert.True(g.Hold(2, At(128), 0, Here));    // exactly on the far plane is in
        Assert.True(g.Hold(3, At(140), 12, Here));   // centre beyond, edge within: in
        Assert.False(g.Hold(4, At(140), 11, Here));  // edge at 129: out
        Assert.False(g.Hold(5, At(500), 1, Here));
    }

    [Fact]
    public void Objects_that_fall_beyond_are_released_farthest_first_and_only_those()
    {
        var g = new DrawDistanceGate { Distance = 1000 };
        for (uint i = 1; i <= 20; i++) g.Hold(i, At(i * 10), 0, Here);   // 10 .. 200 m
        var far = At(0);
        g.Distance = 50;   // 6..20 are now beyond (60 .. 200 m)
        g.Scan(far, Never, long.MaxValue, _ => { });
        var order = new List<uint>();
        while (g.TryNextRelease(far, out var id, out var origin, out _))
        {
            order.Add(id);
            g.Forget(id);
            g.HoldBeyond(id, origin, 0);
        }
        Assert.Equal(Enumerable.Range(6, 15).Select(i => (uint)i).Reverse(), order);
        Assert.Equal(15, g.Beyond);
    }

    [Fact]
    public void An_object_that_comes_back_within_is_woken_and_not_released()
    {
        var g = new DrawDistanceGate { Distance = 100 };
        g.Hold(1, At(150), 0, Here);
        g.HoldBeyond(1, At(150), 0);
        var woken = new List<uint>();
        g.Scan(At(100), Never, long.MaxValue, woken.Add);   // the camera walked 100 m toward it
        Assert.Equal(new uint[] { 1 }, woken);
        Assert.False(g.TryNextRelease(At(100), out _, out _, out _));
    }

    [Fact]
    public void A_release_candidate_that_is_within_again_by_the_time_it_is_taken_stays()
    {
        var g = new DrawDistanceGate { Distance = 100 };
        g.Hold(1, At(150), 0, Here);
        g.Distance = 100;
        g.Scan(Here, Never, long.MaxValue, _ => { });   // beyond: queued
        Assert.False(g.TryNextRelease(At(120), out _, out _, out _));   // the camera moved closer meanwhile
    }

    [Fact]
    public void The_scan_stops_at_its_deadline_and_carries_on_from_there()
    {
        var g = new DrawDistanceGate { Distance = 100 };
        for (uint i = 1; i <= 10_000; i++) g.Hold(i, At(500 + i), 0, Here);
        long ticks = 0;
        long Clock() => ticks += 10;
        var woken = new List<uint>();
        int calls = 0;
        bool more;
        do { more = g.Scan(Here, Clock, ticks + 100, woken.Add); calls++; } while (more && calls < 1000);
        Assert.False(more, "the scan did not finish");
        Assert.InRange(calls, 30, 400);   // ten clock reads a call, one per sixteen objects: about 160 objects a call
    }

    [Fact]
    public void Forgetting_an_object_removes_it_from_release_and_wake()
    {
        var g = new DrawDistanceGate { Distance = 100 };
        g.Hold(1, At(150), 0, Here);
        g.Scan(Here, Never, long.MaxValue, _ => { });
        g.Forget(1);
        Assert.False(g.TryNextRelease(Here, out _, out _, out _));
        Assert.Equal(0, g.Tracked);
    }
}

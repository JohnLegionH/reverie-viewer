// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Net;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Faces' own textures are fetched first, then PBR base colours, then detail maps (PBR normal, metallic-roughness,
/// emissive): the fetch queue serves a waiting request of the earliest class first, within a class in arrival order; a
/// request takes a free slot whenever no request of an earlier class waits, and once it has waited the aging time no later
/// request overtakes it. The residency manager asks for loads and refinements in the same class order. Engine-free, on a
/// test clock.
/// </summary>
public class FetchQueueTests
{
    private const long MiB = 1024 * 1024;
    private static readonly TextureFetchClass Base = TextureFetchClass.BaseColour, Pbr = TextureFetchClass.PbrBaseColour,
        Detail = TextureFetchClass.Detail;

    private sealed class Clock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
    }

    /// <summary>Requests enqueued behind one held slot; each release lets one through. Returns the order they got a slot.</summary>
    private static List<string> Drain(FetchQueue q, List<(string Name, Task Task)> waiting)
    {
        var order = new List<string>();
        for (int i = 0; i < waiting.Count; i++)
        {
            q.Release();
            var granted = waiting.Where(w => w.Task.IsCompletedSuccessfully && !order.Contains(w.Name)).Select(w => w.Name).ToList();
            Assert.Single(granted);
            order.Add(granted[0]);
        }
        return order;
    }

    [Fact]
    public void Waiting_base_colour_requests_go_before_waiting_detail_requests_each_in_arrival_order()
    {
        var clock = new Clock();
        var q = new FetchQueue(1, clock: clock.Read);
        Assert.True(q.EnterAsync(Base, CancellationToken.None).IsCompletedSuccessfully);   // the slot, held
        var waiting = new List<(string, Task)>();
        foreach (var (name, cls) in new[] { ("d1", Detail), ("d2", Detail), ("b1", Base), ("d3", Detail), ("b2", Base) })
        {
            waiting.Add((name, q.EnterAsync(cls, CancellationToken.None)));
            clock.Now += TimeSpan.FromMilliseconds(10);
        }
        Assert.Equal(5, q.Waiting);
        Assert.Equal(new[] { "b1", "b2", "d1", "d2", "d3" }, Drain(q, waiting));
        var s = q.Stats;
        Assert.Equal(3, s.BaseColour.Requests);
        Assert.Equal(3, s.Detail.Requests);
        Assert.Equal(0, s.Detail.Aged);
    }

    [Fact]
    public void A_detail_request_takes_a_free_slot_when_no_base_colour_request_waits()
    {
        var q = new FetchQueue(2);
        Assert.True(q.EnterAsync(Detail, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(q.EnterAsync(Detail, CancellationToken.None).IsCompletedSuccessfully);
        var third = q.EnterAsync(Detail, CancellationToken.None);
        Assert.False(third.IsCompleted);
        q.Release();
        Assert.True(third.IsCompletedSuccessfully);   // nothing else waited: the detail request gets the slot
    }

    [Fact]
    public void A_detail_request_that_has_waited_the_aging_time_is_not_overtaken_by_later_requests()
    {
        var clock = new Clock();
        var q = new FetchQueue(1, TimeSpan.FromSeconds(2), clock.Read);
        _ = q.EnterAsync(Base, CancellationToken.None);
        var d1 = q.EnterAsync(Detail, CancellationToken.None);          // t = 0
        clock.Now = TimeSpan.FromSeconds(0.5);
        var b1 = q.EnterAsync(Base, CancellationToken.None);            // t = 0.5
        clock.Now = TimeSpan.FromSeconds(1.9);
        var b2 = q.EnterAsync(Base, CancellationToken.None);            // t = 1.9
        q.Release();                                                    // t = 1.9: d1 has not aged, b1 first
        Assert.True(b1.IsCompletedSuccessfully);
        Assert.False(d1.IsCompleted);
        clock.Now = TimeSpan.FromSeconds(2.0);
        var b3 = q.EnterAsync(Base, CancellationToken.None);            // t = 2.0
        q.Release();                                                    // d1 aged and arrived before b2 and b3
        Assert.True(d1.IsCompletedSuccessfully);
        Assert.False(b2.IsCompleted || b3.IsCompleted);
        Assert.Equal(1, q.Stats.Detail.Aged);
        Assert.Equal(2000, q.Stats.Detail.WaitMaxMs);
    }

    [Fact]
    public void Under_a_steady_stream_of_base_colour_requests_a_detail_request_waits_no_longer_than_the_aging_time_plus_the_queue_ahead()
    {
        var clock = new Clock();
        var aging = TimeSpan.FromSeconds(2);
        var tick = TimeSpan.FromMilliseconds(100);
        var q = new FetchQueue(2, aging, clock.Read);
        _ = q.EnterAsync(Base, CancellationToken.None);
        _ = q.EnterAsync(Base, CancellationToken.None);
        for (int i = 0; i < 5; i++) _ = q.EnterAsync(Base, CancellationToken.None);   // 5 waiting ahead
        var detail = q.EnterAsync(Detail, CancellationToken.None);
        TimeSpan? servedAt = null;
        // each tick two slots free and two more base-colour requests arrive: the base queue never empties
        for (int t = 0; t < 100 && servedAt is null; t++)
        {
            clock.Now += tick;
            _ = q.EnterAsync(Base, CancellationToken.None);
            _ = q.EnterAsync(Base, CancellationToken.None);
            q.Release();
            q.Release();
            if (detail.IsCompletedSuccessfully) servedAt = clock.Now;
        }
        Assert.NotNull(servedAt);
        // ahead of it at arrival: 5 base-colour requests, 3 ticks at two slots per tick
        Assert.True(servedAt <= aging + 3 * tick, $"served at {servedAt}");
        Assert.True(servedAt >= aging, $"served at {servedAt}, before the aging time while base-colour requests waited");
    }

    [Fact]
    public void A_cancelled_waiting_request_leaves_the_queue_and_its_slot_goes_to_the_next()
    {
        var q = new FetchQueue(1);
        _ = q.EnterAsync(Base, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var cancelled = q.EnterAsync(Base, cts.Token);
        var next = q.EnterAsync(Detail, CancellationToken.None);
        cts.Cancel();
        Assert.True(cancelled.IsCanceled);
        Assert.Equal(1, q.Waiting);
        q.Release();
        Assert.True(next.IsCompletedSuccessfully);
        q.Release();
        Assert.True(q.EnterAsync(Base, CancellationToken.None).IsCompletedSuccessfully);   // the slot came back free
    }

    private static ResidencyCamera Cam() =>
        new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureUser Face(uint id, int face = 0) => new(TextureUserKind.Face, id, face);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    [Fact]
    public void The_residency_manager_asks_for_base_colour_loads_before_detail_only_loads_whatever_their_priority()
    {
        var classes = new TextureFetchClasses();
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB, MaxLoadsPerFrame = 3 }) { FetchClasses = classes };
        var near = new Bounds(new(5, 0, 0), 2f);
        var far = new Bounds(new(200, 0, 0), 0.5f);
        r.SetUser(Face(1, 256), G(1), near, use: Detail);   // a near face's normal map
        r.SetUser(Face(1, 512), G(2), near, use: Detail);   // its ORM map
        r.SetUser(Face(2), G(3), far);                       // a far face's base colour
        r.SetUser(Face(3), G(4), far);
        r.SetUser(Face(4, 256), G(5), far, use: Detail);    // a detail map also used as a base colour: fetched as one
        r.SetUser(Face(5), G(5), far);
        Assert.Equal(TextureFetchClass.Detail, classes.Of(G(1)));
        Assert.Equal(TextureFetchClass.BaseColour, classes.Of(G(5)));
        Assert.Equal(TextureFetchClass.BaseColour, classes.Of(G(3)));
        var first = r.Update(Cam(), TestClock.Frames(0)).Where(a => a.Kind == ResidencyActionKind.Load).Select(a => a.Texture).ToList();
        Assert.Equal(3, first.Count);
        Assert.Equal(new[] { G(3), G(4), G(5) }.OrderBy(g => g), first.OrderBy(g => g));
        var second = r.Update(Cam(), TestClock.Frames(1)).Where(a => a.Kind == ResidencyActionKind.Load).Select(a => a.Texture).ToList();
        Assert.Equal(new[] { G(1), G(2) }, second);   // within the class, highest priority first (ties by id)
        // the base colour user leaves: the shared texture becomes detail-only; the last user leaves: no class
        r.RemoveUser(Face(5));
        Assert.Equal(TextureFetchClass.Detail, classes.Of(G(5)));
        r.SetUser(Face(4, 256), G(5), far, use: Base);
        Assert.Equal(TextureFetchClass.BaseColour, classes.Of(G(5)));
        r.Clear();
        Assert.Equal(0, classes.DetailTextures);
    }

    [Fact]
    public void Refinements_of_base_colour_textures_go_before_refinements_of_detail_maps()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB, MaxLoadsPerFrame = 1 });
        var near = new Bounds(new(5, 0, 0), 2f);
        var far = new Bounds(new(15, 0, 0), 1f);
        r.SetUser(Face(1, 256), G(1), near, use: Detail);
        r.SetUser(Face(2), G(2), far);
        // both resident at their coarsest level of a 1024 x 1024 texture
        foreach (var g in new[] { G(1), G(2) })
            r.Uploaded(g, 5, 6, 1024, 1024, TextureFootprint.Bytes(32, 32, true), PlaceholderColour.Of(1, 2, 3, 255), TestClock.Frames(0));
        var acts = r.Update(Cam(), TestClock.Frames(1));
        var level = Assert.Single(acts, a => a.Kind == ResidencyActionKind.Level);
        Assert.Equal(G(2), level.Texture);
        Assert.Equal(G(1), Assert.Single(r.Update(Cam(), TestClock.Frames(2)), a => a.Kind == ResidencyActionKind.Level).Texture);
    }

    [Fact]
    public void Waiting_requests_are_served_plain_base_colours_then_pbr_base_colours_then_detail_maps_each_in_arrival_order()
    {
        var clock = new Clock();
        var q = new FetchQueue(1, clock: clock.Read);
        Assert.True(q.EnterAsync(Base, CancellationToken.None).IsCompletedSuccessfully);   // the slot, held
        var waiting = new List<(string, Task)>();
        foreach (var (name, cls) in new[] { ("d1", Detail), ("p1", Pbr), ("b1", Base), ("d2", Detail), ("p2", Pbr), ("b2", Base) })
        {
            waiting.Add((name, q.EnterAsync(cls, CancellationToken.None)));
            clock.Now += TimeSpan.FromMilliseconds(10);
        }
        Assert.Equal(new[] { "b1", "b2", "p1", "p2", "d1", "d2" }, Drain(q, waiting));
        var s = q.Stats;
        Assert.Equal(3, s.BaseColour.Requests);
        Assert.Equal(2, s.PbrBaseColour.Requests);
        Assert.Equal(2, s.Detail.Requests);
        Assert.Equal(0, s.PbrBaseColour.Aged + s.Detail.Aged);
    }

    [Fact]
    public void A_request_of_any_class_that_has_waited_the_aging_time_is_not_overtaken_by_any_later_request()
    {
        var clock = new Clock();
        var q = new FetchQueue(1, TimeSpan.FromSeconds(2), clock.Read);
        _ = q.EnterAsync(Base, CancellationToken.None);
        var p1 = q.EnterAsync(Pbr, CancellationToken.None);             // t = 0
        clock.Now = TimeSpan.FromSeconds(0.1);
        var d1 = q.EnterAsync(Detail, CancellationToken.None);          // t = 0.1
        clock.Now = TimeSpan.FromSeconds(0.5);
        var b1 = q.EnterAsync(Base, CancellationToken.None);            // t = 0.5
        clock.Now = TimeSpan.FromSeconds(1.9);
        q.Release();                                                    // nothing aged: the plain base colour first
        Assert.True(b1.IsCompletedSuccessfully);
        Assert.False(p1.IsCompleted || d1.IsCompleted);
        clock.Now = TimeSpan.FromSeconds(2.0);
        var b2 = q.EnterAsync(Base, CancellationToken.None);            // t = 2.0
        q.Release();                                                    // p1 aged and arrived before b2
        Assert.True(p1.IsCompletedSuccessfully);
        Assert.False(d1.IsCompleted || b2.IsCompleted);
        clock.Now = TimeSpan.FromSeconds(2.05);
        q.Release();                                                    // d1 has waited 1.95 s: b2 first
        Assert.True(b2.IsCompletedSuccessfully);
        Assert.False(d1.IsCompleted);
        clock.Now = TimeSpan.FromSeconds(2.1);
        var b3 = q.EnterAsync(Base, CancellationToken.None);            // t = 2.1
        var p2 = q.EnterAsync(Pbr, CancellationToken.None);
        q.Release();                                                    // d1 aged: neither later request goes first
        Assert.True(d1.IsCompletedSuccessfully);
        Assert.False(b3.IsCompleted || p2.IsCompleted);
        q.Release();
        Assert.True(b3.IsCompletedSuccessfully);
        q.Release();
        Assert.True(p2.IsCompletedSuccessfully);
        var s = q.Stats;
        Assert.Equal(1, s.PbrBaseColour.Aged);
        Assert.Equal(1, s.Detail.Aged);
        Assert.Equal(0, s.BaseColour.Aged);
    }

    [Theory]
    [InlineData(TextureFetchClass.PbrBaseColour, TextureFetchClass.BaseColour)]
    [InlineData(TextureFetchClass.Detail, TextureFetchClass.PbrBaseColour)]
    [InlineData(TextureFetchClass.Detail, TextureFetchClass.BaseColour)]
    public void Under_a_steady_stream_of_an_earlier_class_a_later_class_request_waits_no_longer_than_the_aging_time_plus_the_queue_ahead(
        TextureFetchClass later, TextureFetchClass stream)
    {
        var clock = new Clock();
        var aging = TimeSpan.FromSeconds(2);
        var tick = TimeSpan.FromMilliseconds(100);
        var q = new FetchQueue(2, aging, clock.Read);
        _ = q.EnterAsync(stream, CancellationToken.None);
        _ = q.EnterAsync(stream, CancellationToken.None);
        for (int i = 0; i < 5; i++) _ = q.EnterAsync(stream, CancellationToken.None);   // 5 waiting ahead
        var waiter = q.EnterAsync(later, CancellationToken.None);
        TimeSpan? servedAt = null;
        for (int t = 0; t < 100 && servedAt is null; t++)
        {
            clock.Now += tick;
            _ = q.EnterAsync(stream, CancellationToken.None);
            _ = q.EnterAsync(stream, CancellationToken.None);
            q.Release();
            q.Release();
            if (waiter.IsCompletedSuccessfully) servedAt = clock.Now;
        }
        Assert.NotNull(servedAt);
        Assert.True(servedAt <= aging + 3 * tick, $"served at {servedAt}");
        Assert.True(servedAt >= aging, $"served at {servedAt}, before the aging time while earlier-class requests waited");
    }

    [Fact]
    public void The_residency_manager_asks_for_plain_base_colours_then_pbr_base_colours_then_detail_maps_whatever_their_priority()
    {
        var classes = new TextureFetchClasses();
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB, MaxLoadsPerFrame = 2 }) { FetchClasses = classes };
        var near = new Bounds(new(5, 0, 0), 2f);
        var mid = new Bounds(new(50, 0, 0), 1f);
        var far = new Bounds(new(200, 0, 0), 0.5f);
        r.SetUser(Face(1, 256), G(1), near, use: Detail);   // a near PBR face's normal map
        r.SetUser(Face(1, 512), G(2), near, use: Detail);   // its ORM map
        r.SetUser(Face(1), G(3), mid, use: Pbr);            // a PBR face's base colour
        r.SetUser(Face(2), G(4), mid, use: Pbr);
        r.SetUser(Face(3), G(5), far);                      // far faces' own textures
        r.SetUser(Face(4), G(6), far);
        r.SetUser(Face(5, 256), G(7), far, use: Detail);    // a detail map also used as a PBR base colour: fetched as one
        r.SetUser(Face(6), G(7), far, use: Pbr);
        r.SetUser(Face(7), G(8), far, use: Pbr);            // a PBR base colour also used as a face's own texture: a plain one
        r.SetUser(Face(8), G(8), far);
        Assert.Equal(TextureFetchClass.Detail, classes.Of(G(1)));
        Assert.Equal(TextureFetchClass.PbrBaseColour, classes.Of(G(3)));
        Assert.Equal(TextureFetchClass.PbrBaseColour, classes.Of(G(7)));
        Assert.Equal(TextureFetchClass.BaseColour, classes.Of(G(8)));
        Assert.Equal(TextureFetchClass.BaseColour, classes.Of(G(5)));
        Assert.Equal(3, classes.PbrBaseColourTextures);
        Assert.Equal(2, classes.DetailTextures);
        List<Guid> Loads(int frame) => r.Update(Cam(), TestClock.Frames(frame)).Where(a => a.Kind == ResidencyActionKind.Load).Select(a => a.Texture).ToList();
        var plain = Loads(0).Concat(Loads(1)).ToList();
        Assert.Equal(new[] { G(5), G(6), G(8) }.OrderBy(g => g), plain.Take(3).OrderBy(g => g));
        var pbr = plain.Skip(3).Concat(Loads(2)).Concat(Loads(3)).ToList();
        Assert.Equal(new[] { G(3), G(4), G(7) }.OrderBy(g => g), pbr.Take(3).OrderBy(g => g));
        Assert.Equal(new[] { G(1), G(2) }, pbr.Skip(3).Concat(Loads(4)).Take(2));
        // the plain user leaves: G(8) becomes a PBR base colour; the PBR user of G(7) leaves: a detail map
        r.RemoveUser(Face(8));
        Assert.Equal(TextureFetchClass.PbrBaseColour, classes.Of(G(8)));
        r.RemoveUser(Face(6));
        Assert.Equal(TextureFetchClass.Detail, classes.Of(G(7)));
    }

    [Fact]
    public void Refinements_go_plain_base_colours_then_pbr_base_colours_then_detail_maps()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB, MaxLoadsPerFrame = 1 });
        var near = new Bounds(new(5, 0, 0), 2f);
        var mid = new Bounds(new(10, 0, 0), 1.5f);
        var far = new Bounds(new(15, 0, 0), 1f);
        r.SetUser(Face(1, 256), G(1), near, use: Detail);
        r.SetUser(Face(1), G(2), mid, use: Pbr);
        r.SetUser(Face(2), G(3), far);
        foreach (var g in new[] { G(1), G(2), G(3) })
            r.Uploaded(g, 5, 6, 1024, 1024, TextureFootprint.Bytes(32, 32, true), PlaceholderColour.Of(1, 2, 3, 255), TestClock.Frames(0));
        Guid Level(int frame) => Assert.Single(r.Update(Cam(), TestClock.Frames(frame)), a => a.Kind == ResidencyActionKind.Level).Texture;
        Assert.Equal(new[] { G(3), G(2), G(1) }, new[] { Level(1), Level(2), Level(3) });
    }
}

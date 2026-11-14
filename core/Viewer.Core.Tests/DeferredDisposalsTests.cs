// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The queue that holds the Godot values an engine call used until the drain after the
/// frame's upload budget disposes them. A fake clock advances 0.1 ms per disposal, so the per-frame bound is exact: the
/// drain stops at its time slice (0.5 ms, 0.9 ms when behind), a full drain empties it, every value is disposed exactly
/// once, and past the hard cap it drains down to the behind mark (counted as forced).
/// </summary>
public class DeferredDisposalsTests
{
    private sealed class Fake
    {
        public long Ticks;
        public readonly Dictionary<int, int> Disposed = new();
        public DeferredDisposals<int> Make() => new(v =>
        {
            Disposed[v] = Disposed.GetValueOrDefault(v) + 1;
            Ticks += 100;   // 0.1 ms at 1,000,000 ticks a second
        }, () => Ticks, 1_000_000);
    }

    [Fact]
    public void A_frame_drains_up_to_its_time_slice()
    {
        var f = new Fake();
        var q = f.Make();
        for (int i = 0; i < 20; i++) q.Add(i, 1000);
        var r = q.DrainFrame();
        Assert.Equal(5, r.Values);   // 0.5 ms slice, 0.1 ms each
        Assert.Equal(5000, r.Bytes);
        Assert.Equal(15, q.Count);
        Assert.Equal(15_000, q.HeldBytes);
        Assert.False(r.Behind);
        Assert.Equal(0.5, q.MaxFrameMs, 3);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, f.Disposed.Keys.OrderBy(k => k));   // oldest first
    }

    [Fact]
    public void A_full_drain_empties_it_and_every_value_is_disposed_exactly_once()
    {
        var f = new Fake();
        var q = f.Make();
        int next = 0;
        for (int frame = 0; frame < 10; frame++)
        {
            for (int i = 0; i < 7; i++) q.Add(next++, 10);
            q.DrainFrame();
        }
        Assert.True(q.Count > 0);
        var all = q.DrainAll();
        Assert.Equal(0, q.Count);
        Assert.Equal(0, q.HeldBytes);
        Assert.Equal(70, f.Disposed.Count);
        Assert.All(f.Disposed.Values, n => Assert.Equal(1, n));
        Assert.Equal(q.Queued, q.Disposed);
        Assert.Equal(70, q.Queued);
        Assert.Equal(1, q.FullDrains);
        Assert.True(all.Values > 0);
        Assert.Equal(0, q.DrainAll().Values);   // an empty drain disposes nothing
        Assert.Equal(70, f.Disposed.Count);
    }

    [Fact]
    public void Behind_it_drains_a_larger_slice_and_past_the_cap_it_drains_to_the_behind_mark()
    {
        var f = new Fake();
        var q = f.Make();
        q.BehindBytes = 10_000;
        q.HardCapBytes = 100_000;
        for (int i = 0; i < 20; i++) q.Add(i, 1000);   // 20,000 held: behind
        var r = q.DrainFrame();
        Assert.True(r.Behind);
        Assert.Equal(9, r.Values);   // 0.9 ms
        Assert.Equal(1, q.FramesBehind);
        Assert.Equal(0, q.ForcedDrains);
        for (int i = 100; i < 200; i++) q.Add(i, 1000);   // 111,000 held: over the cap
        r = q.DrainFrame();
        Assert.True(r.Forced);
        Assert.Equal(1, q.ForcedDrains);
        Assert.True(q.HeldBytes <= 10_000);
        Assert.Equal(101, r.Values);   // 111,000 down to 10,000
        Assert.All(f.Disposed.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void A_frame_always_disposes_at_least_one_value_and_keeps_its_peaks_and_percentiles()
    {
        var f = new Fake();
        var q = f.Make();
        q.SliceMs = 0.05;   // smaller than one disposal
        q.Add(1, 7);
        q.Add(2, 9);
        Assert.Equal(2, q.PeakCount);
        Assert.Equal(16, q.PeakBytes);
        Assert.Equal(1, q.DrainFrame().Values);
        Assert.Equal(1, q.DrainFrame().Values);
        Assert.Equal(0, q.DrainFrame().Values);   // empty: nothing, and not a drain frame
        Assert.Equal(2, q.DrainFrames);
        Assert.Equal(0.1, q.MaxValueMs, 3);
        Assert.Equal(0.1, q.FrameMsP95, 3);
        Assert.Equal(16, q.BytesDisposed);
    }

    [Fact]
    public void A_stall_between_values_shows_in_the_wall_time_but_not_in_what_the_drain_controls()
    {
        var f = new Fake();
        var q = f.Make();
        for (int i = 0; i < 20; i++) q.Add(i, 1000);
        bool stalled = false;
        q.AfterValueForTest = () => { if (!stalled) { stalled = true; f.Ticks += 2000; } };   // one 2 ms stall
        var r = q.DrainFrame();
        Assert.Equal(1, r.Values);                      // the slice is spent once the stall is over: no more values
        Assert.Equal(2.1, q.MaxFrameMs, 3);             // a wall-clock gate of 1 ms fails on the stall alone
        Assert.Equal(0.1, q.MaxFrameValueMs, 3);        // the disposals the drain ran
        Assert.Equal(0, q.LateStarts);
        Assert.Equal(1, q.StalledFrames);
    }

    [Fact]
    public void A_slowed_disposal_shows_in_what_the_drain_controls()
    {
        var f = new Fake();
        var q = f.Make();
        for (int i = 0; i < 20; i++) q.Add(i, 1000);
        q.InsideValueForTest = () => f.Ticks += 400;    // each disposal 0.5 ms instead of 0.1
        q.DrainFrame();
        q.InsideValueForTest = () => f.Ticks += 1100;   // each disposal 1.2 ms
        q.DrainFrame();
        Assert.Equal(1.2, q.MaxFrameValueMs, 3);
        Assert.Equal(0, q.LateStarts);
        Assert.Equal(0, q.StalledFrames);
    }

    [Fact]
    public void A_drain_that_ignores_its_slice_starts_values_late_and_runs_over_its_bound()
    {
        var f = new Fake();
        var q = f.Make();
        for (int i = 0; i < 20; i++) q.Add(i, 1000);
        q.IgnoreSliceForTest = true;
        var r = q.DrainFrame();
        Assert.Equal(20, r.Values);
        Assert.Equal(15, q.LateStarts);                 // every value after the fifth (0.5 ms) started late
        Assert.Equal(2.0, q.MaxFrameValueMs, 3);
    }

    [Fact]
    public void A_drain_within_its_slice_starts_nothing_late_and_its_controlled_time_is_its_disposals()
    {
        var f = new Fake();
        var q = f.Make();
        q.BehindBytes = 10_000;
        for (int i = 0; i < 40; i++) q.Add(i, 1000);
        q.DrainFrame();                                 // behind: 0.9 ms
        while (q.Count > 0) q.DrainFrame();
        Assert.Equal(0, q.LateStarts);
        Assert.Equal(0.9, q.MaxFrameValueMs, 3);
        Assert.Equal(q.MaxFrameMs, q.MaxFrameValueMs, 3);
        Assert.Equal(0, q.StalledFrames);
    }

    [Fact]
    public void A_value_whose_disposal_throws_is_counted_and_the_drain_goes_on()
    {
        int disposed = 0;
        var q = new DeferredDisposals<int>(v => { if (v == 2) throw new InvalidOperationException("x"); disposed++; });
        q.Add(1, 1); q.Add(2, 1); q.Add(3, 1);
        q.DrainAll();
        Assert.Equal(2, disposed);
        Assert.Equal(1, q.Errors);
        Assert.Equal(0, q.Count);
        Assert.Contains("InvalidOperationException", q.LastError);
    }

    [Fact]
    public void Only_the_thread_that_made_it_may_use_it()
    {
        var q = new DeferredDisposals<int>(_ => { });
        Exception? add = null, drain = null;
        var t = new Thread(() =>
        {
            try { q.Add(1, 1); } catch (Exception e) { add = e; }
            try { q.DrainFrame(); } catch (Exception e) { drain = e; }
        });
        t.Start();
        t.Join();
        Assert.IsType<InvalidOperationException>(add);
        Assert.IsType<InvalidOperationException>(drain);
        Assert.Equal(0, q.Count);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The cap on mesh preparations running at once. Every preparation waits on its own gate the test
/// holds, so what is running is certain: never more than the cap, the rest queued and started in the order asked as
/// places free up, and a region change (Clear) forgetting the queue.
/// </summary>
public class MeshLodPreparerCapTests
{
    private static MeshFace Quad() => new(
        [-0.5f, 0, -0.5f, 0.5f, 0, -0.5f, 0.5f, 0, 0.5f, -0.5f, 0, 0.5f],
        [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3]);

    private static (Guid, int) Key(int i) => (new Guid(i, 0, 0, new byte[8]), 3);

    /// <summary>A gate per preparation: each entry waits on its own completion source, released by the test in turn.</summary>
    private sealed class Gates
    {
        private readonly object _lock = new();
        public readonly List<TaskCompletionSource> Entered = new();
        public int Waiting, PeakWaiting;

        public Task Enter(CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                Entered.Add(tcs);
                PeakWaiting = Math.Max(PeakWaiting, ++Waiting);
            }
            return tcs.Task.WaitAsync(token).ContinueWith(t => { lock (_lock) Waiting--; return t; }, TaskScheduler.Default).Unwrap();
        }

        public int Count { get { lock (_lock) return Entered.Count; } }
        public void Release(int i) { lock (_lock) Entered[i].TrySetResult(); }
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(2);
        Assert.True(done(), "timed out");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Never_more_than_the_cap_run_and_every_queued_one_runs_in_turn(int cap)
    {
        using var p = new MeshLodPreparer(cap);
        var gates = new Gates();
        p.Gate = gates.Enter;
        int asked = cap * 3 + 1;
        for (int i = 0; i < asked; i++) p.Request(Key(i), [Quad()]);
        await Until(() => gates.Count == cap);
        await Task.Delay(50);   // nothing else may start while every place is taken
        Assert.Equal(cap, gates.Count);
        Assert.Equal(cap, p.Running);
        Assert.Equal(asked - cap, p.Queued);
        Assert.Equal(asked, p.InFlight);
        var arrivals = new List<(Guid, int)>();
        for (int i = 0; i < asked; i++)
        {
            await Until(() => gates.Count > i);
            gates.Release(i);
            await Until(() => p.Held == i + 1);   // it arrived before the next one could start (the next waits on its own gate)
            // the queued one that takes the freed place has reached its gate before the next release. The preparer starts
            // queued ones in the order asked, each on the thread pool; with two started and not yet at their gates, the
            // pool may run the later one first, and gates are numbered as they are reached. One start at a time keeps the
            // numbering the start order, and checks that exactly one queued preparation starts per place freed.
            int started = Math.Min(asked, cap + i + 1);
            await Until(() => gates.Count == started);
            Assert.Equal(asked - started, p.Queued);
            Assert.True(p.TryTakeArrival(out var k));
            arrivals.Add(k);
            Assert.True(p.Running <= cap);
        }
        // the first places are taken at once (in whatever order the pool starts them); every queued one starts, so here
        // finishes, in the order asked
        Assert.Equal(Enumerable.Range(0, cap).Select(Key).ToHashSet(), arrivals.Take(cap).ToHashSet());
        Assert.Equal(Enumerable.Range(cap, asked - cap).Select(Key).ToList(), arrivals.Skip(cap).ToList());
        Assert.Equal(asked, p.Prepared);
        Assert.Equal(0, p.Queued);
        Assert.Equal(0, p.InFlight);
        Assert.True(p.PeakRunning <= cap, $"peak running {p.PeakRunning} over the cap {cap}");
        Assert.Equal(cap, p.PeakRunning);
        Assert.True(gates.PeakWaiting <= cap);
    }

    [Fact]
    public async Task A_region_change_forgets_the_queue_and_nothing_queued_starts_after_it()
    {
        using var p = new MeshLodPreparer(1);
        var gates = new Gates();
        p.Gate = gates.Enter;
        for (int i = 0; i < 4; i++) p.Request(Key(i), [Quad()]);
        await Until(() => gates.Count == 1);
        Assert.Equal(3, p.Queued);
        p.Clear();
        Assert.Equal(0, p.Queued);
        Assert.Equal(0, p.InFlight);
        await Until(() => p.Running == 0);   // the running one was cancelled by the clear and threw its result away
        await Task.Delay(50);
        Assert.Equal(1, gates.Count);        // nothing queued before the clear started
        Assert.Equal(0, p.Prepared);
        Assert.Equal(0, p.Held);
        Assert.Equal(1, p.Discarded);
        // after the change, new requests run again
        p.Request(Key(9), [Quad()]);
        await Until(() => gates.Count == 2);
        gates.Release(1);
        await Until(() => p.Held == 1);
    }

    [Fact]
    public async Task A_preparation_still_running_after_a_region_change_keeps_its_place_under_the_cap()
    {
        using var p = new MeshLodPreparer(1);
        var gates = new Gates();
        p.Gate = _ => gates.Enter(CancellationToken.None);   // ignores the clear's cancellation: still running after it
        p.Request(Key(0), [Quad()]);
        await Until(() => gates.Count == 1);
        p.Clear();
        p.Request(Key(1), [Quad()]);
        await Task.Delay(50);
        Assert.Equal(1, gates.Count);   // the old one still runs, so the new one waits
        Assert.Equal(1, p.Queued);
        gates.Release(0);
        await Until(() => gates.Count == 2);
        gates.Release(1);
        await Until(() => p.Held == 1);
        Assert.Equal(1, p.PeakRunning);
        Assert.Equal(1, p.Discarded);
    }

    [Fact]
    public async Task No_cap_runs_every_preparation_at_once()
    {
        using var p = new MeshLodPreparer(0);
        var gates = new Gates();
        p.Gate = gates.Enter;
        for (int i = 0; i < 6; i++) p.Request(Key(i), [Quad()]);
        await Until(() => gates.Count == 6);
        Assert.Equal(0, p.Queued);
        for (int i = 0; i < 6; i++) gates.Release(i);
        await Until(() => p.Held == 6);
    }

    [Fact]
    public void The_viewer_takes_the_named_cap()
    {
        Assert.Null(MeshLodPreparer.CapOverride);
        using var p = new MeshLodPreparer();
        Assert.Equal(MeshLodPreparer.MaxConcurrent, p.Cap);
        Assert.True(MeshLodPreparer.MaxConcurrent >= 1);
    }
}

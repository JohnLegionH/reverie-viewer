// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Containment of a decode the guard misses. Slow fake decodes block on a gate the test opens at
/// the end, so no written-off thread is left running after the test (they are blocked, never spinning).
/// </summary>
[Collection("TimingTests")]
public class DecodeSchedulerTests
{
    private static async Task<Exception?> Fails(Task t)
    {
        try { await t.WaitAsync(TimeSpan.FromSeconds(60)); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task A_decode_past_its_deadline_fails_and_the_others_carry_on()
    {
        var sink = new ListSink();
        using var gate = new ManualResetEventSlim(false);
        using var s = new DecodeScheduler(2, TimeSpan.FromMilliseconds(150), 2, new CoreLog(sink, "test"));
        try
        {
            var slow = s.Run(() => { gate.Wait(); return 1; }, "slow-asset");
            var quick = Enumerable.Range(0, 20).Select(i => s.Run(() => i * 2, "quick")).ToList();
            Assert.Equal(Enumerable.Range(0, 20).Select(i => i * 2), await Task.WhenAll(quick).WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.IsType<DecodeDeadlineException>(await Fails(slow));
            Assert.Equal(1, s.WrittenOff);
            Assert.Equal(2, s.Live);                // replaced
            Assert.Equal(3, s.Created);
            Assert.Equal(7, await s.Run(() => 7, "after").WaitAsync(TimeSpan.FromSeconds(60)));
            var warn = Assert.Single(sink.Entries, e => e.Event == "texture.decode.deadline");   // logged once
            Assert.Equal(LogLevel.Warn, warn.Level);
            Assert.Contains(warn.Fields, f => f.Key == "asset" && f.Value == "slow-asset");
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task The_cap_holds_and_past_it_decoding_stops_without_taking_anything_else_down()
    {
        var sink = new ListSink();
        using var gate = new ManualResetEventSlim(false);
        using var s = new DecodeScheduler(2, TimeSpan.FromMilliseconds(100), 2, new CoreLog(sink, "test"));
        try
        {
            // two stall the first threads; both are written off and replaced (2 of the cap of 2)
            var first = new[] { s.Run(() => { gate.Wait(); return 0; }, "a"), s.Run(() => { gate.Wait(); return 0; }, "b") };
            foreach (var t in first) Assert.IsType<DecodeDeadlineException>(await Fails(t));
            Assert.Equal((2, 2, 4), (s.WrittenOff, s.Live, s.Created));
            // two more stall the replacements: past the cap, nothing is replaced and decoding stops
            var second = new[] { s.Run(() => { gate.Wait(); return 0; }, "c"), s.Run(() => { gate.Wait(); return 0; }, "d") };
            var queued = s.Run(() => 5, "queued-behind");
            foreach (var t in second) Assert.IsType<DecodeDeadlineException>(await Fails(t));
            Assert.IsType<DecodeStoppedException>(await Fails(queued));
            Assert.Equal((4, 0, 4), (s.WrittenOff, s.Live, s.Created));   // no thread beyond the first two plus the cap
            Assert.True(s.Stopped);
            Assert.IsType<DecodeStoppedException>(await Fails(s.Run(() => 1, "later")));   // fails at once
            Assert.Equal(4, sink.Entries.Count(e => e.Event == "texture.decode.deadline"));
            Assert.Single(sink.Entries, e => e.Event == "texture.decode.stopped" && e.Level == LogLevel.Error);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task A_decode_that_throws_faults_only_its_own_task()
    {
        using var s = new DecodeScheduler(1, TimeSpan.FromSeconds(10), 1);
        Assert.IsType<InvalidDataException>(await Fails(s.Run<int>(() => throw new InvalidDataException(), "x")));
        Assert.Equal(3, await s.Run(() => 3, "y"));
        Assert.Equal(0, s.WrittenOff);
    }

    [Fact]
    public void The_defaults_follow_the_core_count()
    {
        Assert.Equal(1, DecodeScheduler.DefaultThreads(1));
        Assert.Equal(2, DecodeScheduler.DefaultThreads(4));
        Assert.Equal(1, DecodeScheduler.DefaultThreads(2));
        Assert.Equal(16, DecodeScheduler.DefaultThreads(20));
        Assert.Equal(16, DecodeScheduler.DefaultThreads(64));
        Assert.Equal(TimeSpan.FromSeconds(30), DecodeScheduler.DefaultDeadline);
    }

    /// <summary>A decode thread keeps nothing of a finished job's result while it waits for
    /// its next job, so a result the caller took and dropped can be collected. Before the fix an idle decode thread kept
    /// its last result (a decoded level) reachable until its next job. Both halves of the fix are needed: the result
    /// handed over once (not kept in the job), and each job run in its own method (not the loop's locals).</summary>
    [Fact]
    public void A_result_the_caller_dropped_can_be_collected_while_its_thread_waits_for_the_next_job()
    {
        using var s = new DecodeScheduler(1, TimeSpan.FromSeconds(30), 1);
        var result = RunAndDrop(s);
        bool alive = true;
        for (int i = 0; i < 10 && alive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            alive = result.IsAlive;
            if (alive) Thread.Sleep(20);   // the thread may still be on its way back to waiting for the next job
        }
        Assert.False(alive, "the dropped result is still reachable while its decode thread waits for its next job");
        Assert.Equal(4, s.Run(() => 4, "after").WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult());   // the thread still takes work
    }

    /// <summary>Runs one job returning a plain array, takes the result and drops it. Kept out of line so no local of the
    /// caller keeps the array.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RunAndDrop(DecodeScheduler s)
    {
        var array = s.Run(() => new byte[1 << 20], "plain-array").WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        Assert.Equal(1 << 20, array.Length);
        return new WeakReference(array);
    }

    /// <summary>Through the real texture path: a decode that overruns fails its texture (it keeps its placeholder) with
    /// the deadline as its reason, and it is not retried; a texture decoded on a healthy decoder still arrives.</summary>
    [Fact]
    public void A_texture_whose_decode_overruns_is_unavailable_and_not_retried()
    {
        var cs = PartialFetchTests.Codestream.Value;
        var id = Guid.NewGuid();
        int fetches = 0;
        using var tight = new DecodeScheduler(1, TimeSpan.FromMilliseconds(1), 1);
        // the overrun must not depend on when the scheduler's thread-pool timer runs. The decode job marks
        // its start on its decode thread (TextureTimeline.Mark reads the clock there); the clock holds that thread until the
        // scheduler has written it off, so the decode is past its deadline whatever the timer's latency. A scheduler that
        // never writes it off (no deadline check) is let go after 5 s and the texture comes back Ready: the test fails.
        var held = new ManualResetEventSlim();
        long Clock()
        {
            if (Thread.CurrentThread.Name?.StartsWith("texture-decode-", StringComparison.Ordinal) == true && !held.IsSet)
            {
                held.Set();
                var giveUp = DateTime.UtcNow.AddSeconds(5);
                while (tight.WrittenOff == 0 && DateTime.UtcNow < giveUp) Thread.Sleep(1);
            }
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => { Interlocked.Increment(ref fetches); return Task.FromResult(AssetFetchResult.Ok(cs)); }), tight)
        {
            Timeline = new TextureTimeline(Clock, System.Diagnostics.Stopwatch.Frequency),
        };
        Assert.Equal(MeshAvailability.Pending, src.TryGetLevel(id, 0, out _));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (src.Pending > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(held.IsSet, "the decode never started on a decode thread");
        Assert.Equal(MeshAvailability.Unavailable, src.TryGetLevel(id, 0, out var t));
        Assert.Null(t);
        Assert.Equal(TextureSource.DecodeDeadline, src.WhyUnavailable(id, 0));
        Assert.Equal(MeshAvailability.Unavailable, src.TryGetLevel(id, 0, out _));   // asked again: not retried
        Assert.Equal(0, src.Pending);
        Assert.Equal(1, fetches);
        Assert.Equal(1, tight.WrittenOff);

        using var healthy = new DecodeScheduler(2, TimeSpan.FromSeconds(30), 2);
        using var ok = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(cs))), healthy);
        ok.TryGetLevel(id, 2, out _);
        until = DateTime.UtcNow.AddSeconds(20);
        while (ok.Pending > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(MeshAvailability.Ready, ok.TryGetLevel(id, 2, out var good));
        Assert.Equal(256, good!.Width);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The per-kind allocation accounting (<see cref="AllocationMeter"/>) and the collection events
/// (<see cref="GcEventRecorder"/>). Each test counts into its own meter, so the class runs in parallel with the rest; the
/// one check on the shared meter (a large buffer outside any work) asserts only what it added. The recorder test forces a
/// collection; nothing here reads a clock window.
/// </summary>
public class AllocationMeterTests
{
    // what a work item allocates beyond its buffer: the array header, and on a thread's first counted work the runtime's
    // one-time thread-static storage (measured about 280 bytes; the steady state allocates nothing, tested below)
    private const long Slack = 1024;

    private static byte[] Keep = [];

    // the work runs on a thread of its own: its counter is that thread's alone
    private static void Run(Thread thread)
    {
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void Work_on_a_worker_counts_the_bytes_it_allocates_under_its_kind()
    {
        var meter = new AllocationMeter();
        var t = new Thread(() =>
        {
            var s = meter.Begin(AllocKind.MeshDecode);
            Keep = new byte[100_000];
            s.End();
        });
        Run(t);
        long bytes = meter.BytesOf(AllocKind.MeshDecode, mainThread: false);
        Assert.InRange(bytes, 100_000, 100_000 + Slack);
        Assert.Equal(1, meter.ItemsOf(AllocKind.MeshDecode, mainThread: false));
        Assert.Equal(0, meter.BytesOf(AllocKind.MeshDecode, mainThread: true));
        Assert.Equal(0, meter.BytesOf(AllocKind.TextureDecode, mainThread: false));
    }

    [Fact]
    public void Several_items_of_one_kind_add_up()
    {
        var meter = new AllocationMeter();
        var t = new Thread(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                var s = meter.Begin(AllocKind.TerrainPrepare);
                Keep = new byte[10_000];
                s.End();
            }
        });
        Run(t);
        Assert.InRange(meter.BytesOf(AllocKind.TerrainPrepare, false), 50_000, 50_000 + 5 * Slack);
        Assert.Equal(5, meter.ItemsOf(AllocKind.TerrainPrepare, false));
    }

    [Fact]
    public void Work_on_the_marked_main_thread_is_counted_apart()
    {
        var meter = new AllocationMeter();
        long own = 0;
        var t = new Thread(() =>
        {
            meter.MarkMainThread();
            var s = meter.Begin(AllocKind.Prim);
            Keep = new byte[20_000];
            own = s.End();
        });
        Run(t);
        Assert.InRange(own, 20_000, 20_000 + Slack);
        Assert.Equal(own, meter.BytesOf(AllocKind.Prim, mainThread: true));
        Assert.Equal(0, meter.BytesOf(AllocKind.Prim, mainThread: false));
    }

    [Fact]
    public void Nested_work_is_taken_out_of_the_outer_so_the_kinds_add_up_to_the_thread_total()
    {
        var meter = new AllocationMeter();
        long outer = 0, inner = 0, total = 0;
        var t = new Thread(() =>
        {
            long t0 = GC.GetAllocatedBytesForCurrentThread();
            var o = meter.Begin(AllocKind.Prim);
            Keep = new byte[10_000];
            var i = meter.Begin(AllocKind.MeshDecode);
            Keep = new byte[50_000];
            inner = i.End();
            Keep = new byte[5_000];
            outer = o.End();
            total = GC.GetAllocatedBytesForCurrentThread() - t0;
        });
        Run(t);
        Assert.InRange(inner, 50_000, 50_000 + Slack);
        Assert.InRange(outer, 15_000, 15_000 + 2 * Slack);
        Assert.Equal(total, outer + inner);   // the meter itself allocates nothing
        Assert.Equal(outer, meter.BytesOf(AllocKind.Prim, false));
        Assert.Equal(inner, meter.BytesOf(AllocKind.MeshDecode, false));
    }

    [Fact]
    public void Begin_End_and_Read_allocate_nothing()
    {
        var meter = new AllocationMeter();
        var b = new long[2 * AllocationMeter.Kinds];
        var it = new long[2 * AllocationMeter.Kinds];
        var l = new long[2 * AllocationMeter.Kinds];
        long own = -1, around = -1;
        var t = new Thread(() =>
        {
            meter.Read(b, it, l);   // first calls out of the way (JIT)
            meter.Begin(AllocKind.Texture).End();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                var s = meter.Begin(AllocKind.Texture);
                own = s.End();
                AllocationMeter.Large(100);
                meter.Read(b, it, l);
            }
            around = GC.GetAllocatedBytesForCurrentThread() - a0;
        });
        Run(t);
        Assert.Equal(0, own);
        Assert.Equal(0, around);
        Assert.Equal(1001, meter.ItemsOf(AllocKind.Texture, false));
    }

    [Fact]
    public void Large_buffers_go_to_the_running_kind_and_small_ones_are_not_counted()
    {
        var meter = new AllocationMeter();
        var t = new Thread(() =>
        {
            var s = meter.Begin(AllocKind.TextureDecode);
            AllocationMeter.Large(AllocationMeter.LargeObjectBytes - 1);
            AllocationMeter.Large(200_000);
            var inner = meter.Begin(AllocKind.TexturePrepare);
            AllocationMeter.Large(90_000);
            inner.End();
            AllocationMeter.Large(100_000);   // back under the outer kind
            s.End();
            meter.Large(300_000, AllocKind.MeshDecode);
        });
        Run(t);
        Assert.Equal(300_000, meter.LargeOf(AllocKind.TextureDecode, false));
        Assert.Equal(90_000, meter.LargeOf(AllocKind.TexturePrepare, false));
        Assert.Equal(300_000, meter.LargeOf(AllocKind.MeshDecode, false));
        Assert.Equal(0, meter.LargeOf(AllocKind.Other, false));
    }

    [Fact]
    public void A_large_buffer_outside_any_work_goes_to_the_shared_meter_as_other()
    {
        long before = 0, after = 0;
        var t = new Thread(() =>
        {
            before = AllocationMeter.Shared.LargeOf(AllocKind.Other, false);
            AllocationMeter.Large(1_000_000);
            after = AllocationMeter.Shared.LargeOf(AllocKind.Other, false);
        });
        Run(t);
        Assert.True(after - before >= 1_000_000, $"{after - before}");
    }

    [Fact]
    public void Work_that_throws_is_still_counted_and_the_thread_state_restored()
    {
        var meter = new AllocationMeter();
        var t = new Thread(() =>
        {
            var s = meter.Begin(AllocKind.MeshPrepare);
            try { Keep = new byte[30_000]; throw new InvalidOperationException(); }
            catch (InvalidOperationException) { }
            finally { s.End(); }
            AllocationMeter.Large(200_000);   // outside any work now: the shared meter's other, not this one's
        });
        Run(t);
        Assert.True(meter.BytesOf(AllocKind.MeshPrepare, false) >= 30_000);
        Assert.Equal(0, meter.LargeOf(AllocKind.MeshPrepare, false));
    }

    [Fact]
    public async Task The_recorder_sees_a_forced_full_collection_with_its_generation_and_reason()
    {
        using var rec = GcEventRecorder.Start(allocationTicks: false);
        int before = GC.CollectionCount(0);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        int index = GC.CollectionCount(0);
        Assert.True(index > before);
        // the runtime hands its events over on its own thread: wait for this one (generous cap), never a fixed window
        GcEventRecorder.Collection? seen = null;
        for (var sw = System.Diagnostics.Stopwatch.StartNew(); seen is null && sw.Elapsed.TotalSeconds < 30;)
        {
            seen = rec.Collections().Where(c => c.Index > before && c.Generation == 2).Select(c => (GcEventRecorder.Collection?)c).FirstOrDefault();
            if (seen is null) await Task.Delay(50);
        }
        Assert.NotNull(seen);
        Assert.Equal("induced", GcEventRecorder.ReasonName(seen!.Value.Reason));
        Assert.Equal("blocking", GcEventRecorder.TypeName(seen.Value.Type));
    }

    [Fact]
    public async Task With_ticks_on_the_recorder_sees_large_allocation_samples_by_type()
    {
        using var rec = GcEventRecorder.Start(allocationTicks: true);
        // the runtime samples about every 100 KB: allocate well past that in large arrays of one type, until one is seen
        List<GcEventRecorder.Tick> large = new();
        for (var sw = System.Diagnostics.Stopwatch.StartNew(); large.Count == 0 && sw.Elapsed.TotalSeconds < 30;)
        {
            for (int i = 0; i < 64; i++) Keep = new byte[200_000];
            await Task.Delay(50);
            large = rec.Ticks().Where(t => t.Large && t.Type.Contains("Byte", StringComparison.Ordinal)).ToList();
        }
        Assert.NotEmpty(large);
        Assert.All(large, t => Assert.True(t.Bytes > 0));
    }

    [Fact]
    public async Task A_sample_is_placed_by_the_noted_role_of_its_thread()
    {
        if (!OperatingSystem.IsWindows()) return;   // roles are noted on Windows only
        var meter = new AllocationMeter();
        using var rec = GcEventRecorder.Start(allocationTicks: true);
        using var stop = new CancellationTokenSource();
        var t = new Thread(() =>
        {
            meter.NoteThread("probe");
            while (!stop.IsCancellationRequested) { for (int i = 0; i < 64; i++) Keep = new byte[200_000]; Thread.Sleep(20); }
        });
        t.Start();
        // until a large sample from that thread is seen (generous cap)
        bool seen = false;
        for (var sw = System.Diagnostics.Stopwatch.StartNew(); !seen && sw.Elapsed.TotalSeconds < 30;)
        {
            await Task.Delay(50);
            seen = rec.Ticks().Any(x => x.Large && meter.RoleOf(x.OsThread) == "probe");
        }
        stop.Cancel();
        t.Join();
        Assert.True(seen);
        Assert.Null(meter.RoleOf(-1));
    }

    [Fact]
    public void Reason_and_type_names_cover_the_runtime_values()
    {
        Assert.Equal("alloc_small", GcEventRecorder.ReasonName(0));
        Assert.Equal("alloc_large", GcEventRecorder.ReasonName(4));
        Assert.Equal("reason_99", GcEventRecorder.ReasonName(99));
        Assert.Equal("background", GcEventRecorder.TypeName(1));
    }
}

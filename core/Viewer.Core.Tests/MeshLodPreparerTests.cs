// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Mesh LOD surface data prepared off the main thread. The preparations wait on a gate the test
/// holds where an in-flight state matters, so nothing depends on timing. The byte-for-byte match with the engine's own
/// layout is checked in the engine by the render harness (--tr-meshprep); here, the preparer's lifecycle and the layout's
/// engine-free rules.
/// </summary>
public class MeshLodPreparerTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-00000000000a");

    private static MeshFace Quad(float y = 0.5f) => new(
        [-0.5f, y, -0.5f, 0.5f, y, -0.5f, 0.5f, y, 0.5f, -0.5f, y, 0.5f],
        [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3]);

    private static MeshFace Strip(int vertices)
    {
        var p = new float[vertices * 3];
        var n = new float[vertices * 3];
        for (int i = 0; i < vertices; i++) { p[i * 3] = i * 0.001f; p[i * 3 + 1] = (i % 2) * 0.01f; n[i * 3 + 2] = 1; }
        var idx = new int[(vertices - 2) * 3];
        for (int t = 0; t < vertices - 2; t++) { idx[t * 3] = t; idx[t * 3 + 1] = t + 1; idx[t * 3 + 2] = t + 2; }
        return new MeshFace(p, n, new float[vertices * 2], idx);
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(2);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task A_prepared_lod_arrives_once_and_is_what_the_encoder_makes()
    {
        using var p = new MeshLodPreparer();
        IReadOnlyList<MeshFace> faces = [Quad(), Quad(-0.5f)];
        p.Request((A, 2), faces);
        p.Request((A, 2), faces);   // asked twice: prepared once
        await Until(() => p.Held == 1);
        Assert.True(p.TryTakeArrival(out var key));
        Assert.Equal((A, 2), key);
        Assert.False(p.TryTakeArrival(out _));
        Assert.True(p.TryTake((A, 2), out var prepared));
        var expected = MeshSurfaceData.Prepare(faces);
        Assert.Equal(2, prepared!.Surfaces.Count);
        for (int s = 0; s < 2; s++)
        {
            Assert.Equal(expected.Surfaces[s].VertexData, prepared.Surfaces[s].VertexData);
            Assert.Equal(expected.Surfaces[s].AttributeData, prepared.Surfaces[s].AttributeData);
            Assert.Equal(expected.Surfaces[s].IndexData, prepared.Surfaces[s].IndexData);
        }
        Assert.Equal(1, p.Prepared);
        Assert.Equal(0, p.Held);
        Assert.Equal(0, p.HeldBytes);
        Assert.False(p.IsPending((A, 2)));
    }

    [Fact]
    public async Task Prepared_data_nobody_wants_is_dropped_and_nothing_is_held()
    {
        using var p = new MeshLodPreparer();
        p.Request((A, 3), [Quad()]);
        await Until(() => p.Held == 1);
        Assert.True(p.HeldBytes > 0);
        p.Drop((A, 3));
        Assert.Equal(0, p.Held);
        Assert.Equal(0, p.HeldBytes);
        Assert.Equal(1, p.Dropped);
        Assert.False(p.TryTake((A, 3), out _));
    }

    [Fact]
    public async Task A_clear_with_preparations_in_flight_leaves_nothing_in_flight_or_held_and_their_results_are_thrown_away()
    {
        using var p = new MeshLodPreparer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Gate = t => gate.Task.WaitAsync(t);
        p.Request((A, 0), [Quad()]);
        p.Request((A, 1), [Quad()]);
        Assert.Equal(2, p.InFlight);
        await Until(() => p.Running == 2);
        p.Clear();   // a region change
        Assert.Equal(0, p.InFlight);
        Assert.Equal(0, p.Held);
        gate.SetResult();
        await Until(() => p.Running == 0);
        Assert.Equal(2, p.Discarded);
        Assert.Equal(0, p.Held);
        Assert.Equal(0, p.HeldBytes);
        Assert.False(p.TryTakeArrival(out _));
        // asked again after the clear: prepared afresh
        p.Gate = null;
        p.Request((A, 0), [Quad()]);
        await Until(() => p.Held == 1);
    }

    [Fact]
    public async Task A_clear_after_a_preparation_finished_forgets_it_and_its_arrival()
    {
        using var p = new MeshLodPreparer();
        p.Request((A, 1), [Quad()]);
        await Until(() => p.Held == 1);
        p.Clear();
        Assert.Equal(0, p.Held);
        Assert.Equal(0, p.HeldBytes);
        Assert.False(p.TryTakeArrival(out _));
        Assert.False(p.TryTake((A, 1), out _));
    }

    [Fact]
    public async Task Dispose_with_a_preparation_held_at_the_gate_leaves_nothing_running()
    {
        var p = new MeshLodPreparer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Gate = t => gate.Task.WaitAsync(t);
        p.Request((A, 0), [Quad()]);
        await Until(() => p.Running == 1);
        p.Dispose();
        await Until(() => p.Running == 0);   // cancelled at the gate, not waiting for it
        Assert.Equal(0, p.InFlight);
        Assert.Equal(0, p.Held);
        Assert.Equal(1, p.Discarded);
    }

    [Fact]
    public async Task A_preparation_that_fails_arrives_empty_so_the_host_builds_the_lod_itself()
    {
        using var p = new MeshLodPreparer();
        var broken = new MeshFace([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1], [0, 0, 1, 0, 0, 1], [0, 1, 2]);   // normals short
        p.Request((A, 0), [broken]);
        await Until(() => p.Held == 1);
        Assert.True(p.TryTake((A, 0), out var prepared));
        Assert.Null(prepared);
        Assert.Equal(1, p.Failed);
    }

    /// <summary>Holds the worker right after it recorded a finished preparation, until the test lets it go.</summary>
    private sealed class HeldAfterRecord : IDisposable
    {
        public readonly ManualResetEventSlim Reached = new(), Go = new();
        public void Hold((Guid, int) _) { Reached.Set(); Go.Wait(TimeSpan.FromSeconds(30)); }
        public void Dispose() { Go.Set(); }
    }

    [Fact]
    public void A_preparation_counted_as_held_has_already_arrived()
    {
        using var p = new MeshLodPreparer();
        using var held = new HeldAfterRecord();
        p.AfterRecord = held.Hold;
        p.Request((A, 0), [Quad()]);
        Assert.True(held.Reached.Wait(TimeSpan.FromSeconds(60)), "the preparation never finished");
        // the worker is held after recording: what Held counts must already be announced as an arrival
        Assert.Equal(1, p.Held);
        Assert.True(p.TryTakeArrival(out var key), "held, but its arrival was not there yet");
        Assert.Equal((A, 0), key);
    }

    [Fact]
    public async Task A_clear_right_after_a_preparation_was_recorded_leaves_no_arrival_behind()
    {
        using var p = new MeshLodPreparer();
        var held = new HeldAfterRecord();
        p.AfterRecord = held.Hold;
        p.Request((A, 0), [Quad()]);
        Assert.True(held.Reached.Wait(TimeSpan.FromSeconds(60)), "the preparation never finished");
        p.Clear();
        held.Go.Set();
        await Until(() => p.Running == 0);   // the worker has finished everything it does after recording
        Assert.Equal(0, p.Held);
        Assert.False(p.TryTakeArrival(out _), "an arrival from before the clear came after it");
    }

    [Fact]
    public void The_layout_reverses_each_triangle_and_uses_16_bit_indices_up_to_65536_vertices()
    {
        var q = MeshSurfaceData.Encode(Quad());
        Assert.Equal(MeshSurfaceData.Format, q.Format);
        Assert.Equal(4, q.VertexCount);
        Assert.Equal(6, q.IndexCount);
        Assert.Equal(new ushort[] { 0, 2, 1, 0, 3, 2 }, MemoryMarshal.Cast<byte, ushort>(q.IndexData).ToArray());
        Assert.Equal(4 * 12 + 4 * 8, q.VertexData.Length);   // positions, then normal and tangent pairs
        Assert.Equal(4 * 8, q.AttributeData.Length);          // UVs
        Assert.Equal((65536 - 2) * 3 * 2, MeshSurfaceData.Encode(Strip(65536)).IndexData.Length);
        Assert.Equal((65537 - 2) * 3 * 4, MeshSurfaceData.Encode(Strip(65537)).IndexData.Length);
    }

    [Fact]
    public void Positions_go_to_engine_axes_and_the_box_holds_them_with_a_least_size_on_a_flat_axis()
    {
        var q = MeshSurfaceData.Encode(Quad(0.5f));   // flat at SL y = 0.5: engine z = -0.5
        var pos = MemoryMarshal.Cast<byte, float>(q.VertexData.AsSpan(0, 48)).ToArray();
        Assert.Equal(new[] { -0.5f, -0.5f, -0.5f }, pos[..3]);   // SL (-0.5, 0.5, -0.5) -> engine (x, z, -y)
        Assert.Equal(new[] { -0.5f, -0.5f, -0.5f, 1f, 1f, 0.00001f }, q.Aabb);
    }
}

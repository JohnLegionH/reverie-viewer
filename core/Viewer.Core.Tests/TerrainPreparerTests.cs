// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Terrain chunks prepared off the main thread. The scheduling (engine-free): a result whose chunk
/// changed since it started is discarded on arrival; a region change drops everything in flight; the cap holds; a chunk
/// runs once at a time. The snapshot: what a worker builds from it is byte-identical to the main thread's build, holes and
/// composition included, and a patch changed after the snapshot does not reach it.
/// </summary>
public class TerrainPreparerTests
{
    /// <summary>A gate per preparation, released by the test in turn.</summary>
    private sealed class Gates
    {
        private readonly object _lock = new();
        public readonly List<TaskCompletionSource> Entered = new();

        public Task Enter(CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) Entered.Add(tcs);
            return tcs.Task.WaitAsync(token);
        }

        public int Count { get { lock (_lock) return Entered.Count; } }
        public void Release(int i) { lock (_lock) Entered[i].TrySetResult(); }
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(done(), "timed out");
    }

    private static PreparedTerrainChunk Chunk((int X, int Y) k) => new(k.X, k.Y, null, 0, false);

    [Fact]
    public async Task A_result_arrives_and_is_taken_at_its_version()
    {
        using var p = new TerrainPreparer();
        Assert.True(p.Start((1, 2), () => Chunk((1, 2))));
        await Until(() => p.Running == 0);
        Assert.True(p.TryTake(out var r, out var key, out bool failed));
        Assert.Equal((1, 2), key);
        Assert.False(failed);
        Assert.Equal(1, r!.ChunkX);
        Assert.Equal(1, p.Prepared);
        Assert.Equal(0, p.InFlight);
    }

    [Fact]
    public async Task A_result_whose_chunk_changed_while_it_prepared_is_discarded_and_the_chunk_can_start_again()
    {
        using var p = new TerrainPreparer();
        var gates = new Gates();
        p.Gate = gates.Enter;
        Assert.True(p.Start((0, 0), () => Chunk((0, 0))));
        await Until(() => gates.Count == 1);
        p.Touch((0, 0));   // its patches changed while it prepared
        Assert.False(p.CanStart((0, 0)));   // still in flight: not started twice
        gates.Release(0);
        await Until(() => p.Running == 0);
        Assert.False(p.TryTake(out _, out _, out _));
        Assert.Equal(1, p.Stale);
        Assert.Equal(0, p.Prepared);
        Assert.Equal(0, p.InFlight);
        // prepared again, from a new snapshot, at the new version: taken
        Assert.True(p.Start((0, 0), () => Chunk((0, 0))));
        await Until(() => gates.Count == 2);
        gates.Release(1);
        await Until(() => p.Running == 0);
        Assert.True(p.TryTake(out _, out var key, out _));
        Assert.Equal((0, 0), key);
        Assert.Equal(1, p.Prepared);
    }

    [Fact]
    public async Task A_touch_of_another_chunk_does_not_discard_this_one()
    {
        using var p = new TerrainPreparer();
        var gates = new Gates();
        p.Gate = gates.Enter;
        p.Start((0, 0), () => Chunk((0, 0)));
        await Until(() => gates.Count == 1);
        p.Touch((1, 0));
        gates.Release(0);
        await Until(() => p.Running == 0);
        Assert.True(p.TryTake(out _, out _, out _));
        Assert.Equal(0, p.Stale);
    }

    [Fact]
    public async Task A_region_change_drops_everything_in_flight_and_nothing_old_is_taken()
    {
        using var p = new TerrainPreparer(cap: 3);
        var gates = new Gates();
        p.Gate = gates.Enter;
        for (int i = 0; i < 3; i++) Assert.True(p.Start((i, 0), () => Chunk((i, 0))));
        await Until(() => gates.Count == 3);
        gates.Release(0);
        await Until(() => p.Running == 2);   // one arrived before the change, not taken yet
        p.Clear();
        Assert.Equal(0, p.InFlight);
        Assert.Equal(3, p.InFlightAtClear);
        await Until(() => p.Running == 0);   // the others were cancelled at their gates
        Assert.False(p.TryTake(out _, out _, out _));
        Assert.Equal(0, p.Prepared);
        Assert.Equal(3, p.Discarded);
        // the new region starts clean
        Assert.True(p.CanStart((0, 0)));
    }

    [Fact]
    public async Task A_result_finishing_after_a_region_change_is_thrown_away()
    {
        using var p = new TerrainPreparer();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        p.Start((0, 0), () => { inside.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return Chunk((0, 0)); });   // past the gate: not cancellable
        await inside.Task;
        p.Clear();
        release.Set();
        await Until(() => p.Running == 0);
        Assert.False(p.TryTake(out _, out _, out _));
        Assert.Equal(1, p.Discarded);
        Assert.Equal(0, p.InFlight);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Never_more_than_the_cap_run(int cap)
    {
        using var p = new TerrainPreparer(cap);
        var gates = new Gates();
        p.Gate = gates.Enter;
        int started = 0;
        for (int i = 0; i < cap + 3; i++) if (p.Start((i, 0), () => Chunk((0, 0)))) started++;
        Assert.Equal(cap, started);
        Assert.False(p.HasRoom);
        await Until(() => gates.Count == cap);
        Assert.Equal(cap, p.Running);
        gates.Release(0);
        await Until(() => p.Running == cap - 1);
        Assert.True(p.HasRoom);
        Assert.Equal(cap, p.PeakRunning);
        for (int i = 1; i < cap; i++) gates.Release(i);
    }

    [Fact]
    public void The_viewer_cap_is_two()
    {
        Assert.Equal(2, TerrainPreparer.MaxConcurrent);
        Assert.Equal(2, new TerrainPreparer().Cap);
    }

    [Fact]
    public async Task A_preparation_that_throws_arrives_as_failed()
    {
        using var p = new TerrainPreparer();
        p.Start((3, 3), () => throw new InvalidOperationException("boom"));
        await Until(() => p.Running == 0);
        Assert.True(p.TryTake(out var r, out var key, out bool failed));
        Assert.True(failed);
        Assert.Null(r);
        Assert.Equal((3, 3), key);
        Assert.Equal(1, p.Failed);
    }

    // ---------------- the snapshot ----------------

    private static Dictionary<(int, int), float[]> Patches(TerrainFixture t, Func<int, int, bool>? keep = null) =>
        t.Patches().Where(p => keep?.Invoke(p.X, p.Y) ?? true).ToDictionary(p => (p.X, p.Y), p => p.Heights);

    private static PatchSource Source(Dictionary<(int, int), float[]> patches) => (int x, int y, out float[] h) => patches.TryGetValue((x, y), out h!);

    private static void SameBytes(PreparedSurface? a, PreparedSurface? b, string what)
    {
        Assert.True((a is null) == (b is null), what + ": presence");
        if (a is null || b is null) return;
        Assert.Equal(a.Format, b.Format);
        Assert.Equal(a.VertexCount, b.VertexCount);
        Assert.Equal(a.IndexCount, b.IndexCount);
        Assert.True(a.VertexData.AsSpan().SequenceEqual(b.VertexData), what + ": vertex bytes");
        Assert.True(a.AttributeData.AsSpan().SequenceEqual(b.AttributeData), what + ": attribute bytes");
        Assert.True(a.IndexData.AsSpan().SequenceEqual(b.IndexData), what + ": index bytes");
        Assert.Equal(a.Aabb, b.Aabb);
    }

    [Theory]
    [InlineData(256, 256, false, false)]
    [InlineData(256, 256, true, false)]
    [InlineData(512, 256, false, true)]    // a var region with holes (patches not arrived)
    [InlineData(200, 136, true, true)]     // sizes that are not whole chunks
    public void Every_chunk_built_from_its_snapshot_is_byte_identical_to_the_main_thread_build(int sx, int sy, bool composed, bool holes)
    {
        var t = SyntheticTerrain.Make("snap", sx, sy);
        var patches = Patches(t, holes ? (x, y) => (x * 7 + y * 3) % 5 != 0 : null);
        var grid = new TerrainGrid(sx, sy, 16, Source(patches)) { Composition = composed ? TerrainDetails.SyntheticTerrain : null, OriginX = 256000, OriginY = 256000 };
        int chunks = 0, empty = 0;
        for (int cy = 0; cy < grid.ChunksY; cy++)
            for (int cx = 0; cx < grid.ChunksX; cx++)
            {
                var main = grid.Build(cx, cy);
                var prepared = grid.Snapshot(cx, cy).Prepare();
                Assert.Equal((cx, cy), (prepared.ChunkX, prepared.ChunkY));
                SameBytes(main is null ? null : TerrainSurface.Prepare(main), prepared.Surface, $"chunk {cx},{cy}");
                Assert.Equal(main?.VertexCount ?? 0, prepared.Vertices);
                Assert.Equal(main?.Composition is not null, prepared.Composed);
                chunks++;
                if (main is null) empty++;
            }
        Assert.Equal(grid.ChunksX * grid.ChunksY, chunks);
        if (!holes) Assert.Equal(0, empty);
    }

    [Fact]
    public void A_region_with_no_patches_prepares_every_chunk_as_removed()
    {
        var grid = new TerrainGrid(256, 256, 16, Source(new()));
        var p = grid.Snapshot(1, 1).Prepare();
        Assert.Null(p.Surface);
        Assert.Equal(0, p.Vertices);
    }

    [Fact]
    public void A_patch_changed_after_the_snapshot_does_not_reach_it()
    {
        var t = SyntheticTerrain.Make("snap", 256, 256);
        var patches = Patches(t);
        var grid = new TerrainGrid(256, 256, 16, Source(patches));
        var before = TerrainSurface.Prepare(grid.Build(1, 1)!);
        var snapshot = grid.Snapshot(1, 1);
        foreach (var k in patches.Keys.ToList())
        {
            patches[k][0] += 50f;                                               // changed in place (the snapshot holds copies)
            patches[k] = patches[k].Select(h => h + 10f).ToArray();             // and replaced (a terraform edit)
        }
        SameBytes(before, snapshot.Prepare().Surface, "snapshot");
        Assert.False(TerrainSurface.Prepare(grid.Build(1, 1)!).VertexData.AsSpan().SequenceEqual(before.VertexData));   // the live grid moved on
    }

    [Fact]
    public void The_snapshot_copies_only_the_chunk_and_the_ring_around_it()
    {
        var t = SyntheticTerrain.Make("snap", 1024, 1024);
        var grid = new TerrainGrid(1024, 1024, 16, Source(Patches(t)));
        Assert.Equal(36, grid.Snapshot(5, 5).PatchesCopied);   // 4 x 4 and its ring
        Assert.Equal(25, grid.Snapshot(0, 0).PatchesCopied);   // a corner: no ring before it
        Assert.Equal(25, grid.Snapshot(grid.ChunksX - 1, grid.ChunksY - 1).PatchesCopied);
    }
}

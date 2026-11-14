// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Core.Meshing;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The keeper lets an asset's decoded geometry go 10 s after it was last needed,
/// never before, never while a preparation is in flight for it or an object waits for one of its LODs, and counts.</summary>
public class DecodedMeshKeeperTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Geometry_is_let_go_10_s_after_it_was_last_needed_and_not_before()
    {
        var k = new DecodedMeshKeeper();
        var out1 = new List<Guid>();
        k.Needed(A, S(1));
        Assert.Equal(0, k.Collect(S(10.999), _ => false, out1));
        Assert.Empty(out1);
        Assert.Equal(1, k.Tracked);
        Assert.Equal(1, k.Collect(S(11), _ => false, out1));
        Assert.Equal([A], out1);
        Assert.Equal(0, k.Tracked);
        Assert.Equal(1, k.LetGo);
        Assert.Equal(0, k.Collect(S(100), _ => false, out1));   // once let go, not handed back again
        Assert.Single(out1);
    }

    [Fact]
    public void Each_need_restarts_the_10_s_and_an_earlier_time_does_not_move_it_back()
    {
        var k = new DecodedMeshKeeper();
        var due = new List<Guid>();
        k.Needed(A, S(0));
        k.Needed(A, S(8));
        k.Needed(A, S(3));   // an earlier time: ignored
        Assert.Equal(0, k.Collect(S(17.9), _ => false, due));
        Assert.Equal(1, k.Collect(S(18), _ => false, due));
    }

    [Fact]
    public void Never_while_a_preparation_is_in_flight_or_an_object_waits_then_let_go_when_that_ends()
    {
        var k = new DecodedMeshKeeper();
        var due = new List<Guid>();
        bool busy = true;
        k.Needed(A, S(0));
        k.Needed(B, S(0));
        Assert.Equal(1, k.Collect(S(30), id => id == A && busy, due));
        Assert.Equal([B], due);
        Assert.Equal(1, k.Tracked);
        Assert.Equal(0, k.Collect(S(60), id => id == A && busy, due));   // still busy: kept however long
        busy = false;
        Assert.Equal(1, k.Collect(S(60.001), id => id == A && busy, due));
        Assert.Equal([B, A], due);
        Assert.Equal(2, k.LetGo);
    }

    [Fact]
    public void Forget_and_clear_stop_tracking_without_counting_a_let_go()
    {
        var k = new DecodedMeshKeeper();
        var due = new List<Guid>();
        k.Needed(A, S(0));
        k.Needed(B, S(0));
        k.Forget(A);
        Assert.Equal(1, k.Tracked);
        k.Clear();
        Assert.Equal(0, k.Tracked);
        Assert.Equal(0, k.Collect(S(100), _ => false, due));
        Assert.Equal(0, k.LetGo);
    }

    [Fact]
    public void A_shorter_keep_is_honoured_and_a_negative_one_refused()
    {
        var k = new DecodedMeshKeeper(S(2));
        var due = new List<Guid>();
        k.Needed(A, S(0));
        Assert.Equal(0, k.Collect(S(1.9), _ => false, due));
        Assert.Equal(1, k.Collect(S(2), _ => false, due));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedMeshKeeper(S(-1)));
    }

    [Fact]
    public void The_lod_rule_resolves_from_the_lods_an_asset_has_as_from_its_geometry()
    {
        // every combination of present LODs and every wanted detail: the mask kept after a let-go resolves as the geometry does
        var face = new MeshFace([0, 0, 0], [0, 0, 1], [0, 0], [0, 0, 0]);
        for (int mask = 0; mask < 16; mask++)
        {
            var g = new MeshGeometry(Enumerable.Range(0, 4).Select(d => (mask & (1 << d)) != 0 ? new MeshLod([face]) : null).ToList(), null);
            Assert.Equal((byte)mask, g.LodMask);
            for (int detail = -1; detail <= 4; detail++)
                Assert.Equal(g.Resolve(detail), MeshGeometry.Resolve((byte)mask, detail));
        }
    }
}

/// <summary>The mesh source lets a decoded asset go (the counters go down, its LODs stay known) and
/// decodes it again when asked, counting the re-decode, whether it came soon after the let-go, and where its bytes came from.</summary>
public class MeshSourceLetGoTests
{
    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(done());
    }

    private static MeshSource Source(Viewer.Core.Assets.SharedAssetStore store, Func<int> count)
    {
        return new MeshSource(new Viewer.Core.Assets.SessionMeshes(
            new Viewer.Core.Assets.SessionAssets(store, _ => true, (id, _) => { count(); return Task.FromResult(
                MeshZoo.Assets.TryGetValue(id, out var f) ? Viewer.Core.Assets.AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(MeshDecodeTests.MeshDir, f))) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound)); }),
            (i, b) => { var r = Viewer.Core.Protocol.LmvMesh.Decode(i, b); return (r.Geometry, r.Refusal); }));
    }

    [Fact]
    public async Task A_let_go_mesh_is_decoded_again_from_the_shared_store_and_counted()
    {
        int network = 0;
        var clock = TimeSpan.Zero;
        using var src = Source(new Viewer.Core.Assets.SharedAssetStore(), () => Interlocked.Increment(ref network));
        src.Clock = () => clock;
        var id = MeshZoo.Assets.Keys.First();
        Assert.False(src.Release(id, clock));   // not decoded yet: nothing to let go
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);
        Assert.Equal(MeshAvailability.Ready, src.TryGet(id, out var g));
        byte lods = g!.LodMask;
        long bytes = src.DecodedBytes;
        Assert.True(bytes > 0);
        clock = TimeSpan.FromSeconds(20);
        Assert.True(src.Release(id, clock));
        Assert.False(src.Release(id, clock));   // already let go
        Assert.Equal(0, src.DecodedEntries);
        Assert.Equal(0, src.DecodedBytes);
        Assert.Equal(bytes, src.PeakDecodedBytes);
        Assert.True(src.TryGetLods(id, out var known));
        Assert.Equal(lods, known);
        Assert.True(src.IsLetGo(id));
        Assert.Equal(1, src.LetGo);
        clock = TimeSpan.FromSeconds(25);   // within 10 s of the let-go
        Assert.Equal(MeshAvailability.Pending, src.TryGet(id, out _));
        await Until(() => src.Pending == 0);
        Assert.Equal(MeshAvailability.Ready, src.TryGet(id, out _));
        Assert.False(src.IsLetGo(id));
        Assert.Equal(bytes, src.DecodedBytes);
        Assert.Equal(1, network);   // the first fetch only: the re-decode read the shared store
        Assert.Equal(1, src.ReDecodes);
        Assert.Equal(1, src.ReDecodesSoon);
        Assert.Equal(0, src.NetworkReDecodes);
        Assert.Equal(1, src.ReDecodeSources["shared"]);
        Assert.Single(src.ReDecodeMs);
        // a region change lets everything go and forgets it: the next request is a first decode
        Assert.Equal(1, src.ReleaseAll());
        Assert.Equal(0, src.DecodedEntries);
        Assert.Equal(0, src.DecodedBytes);
        Assert.False(src.TryGetLods(id, out _));
        Assert.Equal(1, src.LetGoAtReset);
        Assert.Equal(1, src.LetGo);
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);
        Assert.Equal(1, src.ReDecodes);
    }

    [Fact]
    public async Task A_re_decode_that_neither_store_can_serve_is_counted_as_from_the_network_and_not_soon_after_10_s()
    {
        int network = 0;
        var clock = TimeSpan.Zero;
        using var src = Source(new Viewer.Core.Assets.SharedAssetStore(0), () => Interlocked.Increment(ref network));   // keeps nothing
        src.Clock = () => clock;
        var id = MeshZoo.Assets.Keys.First();
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);
        Assert.True(src.Release(id, clock));
        clock = TimeSpan.FromSeconds(10);   // 10 s after the let-go: not soon
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);
        Assert.Equal(MeshAvailability.Ready, src.TryGet(id, out _));
        Assert.Equal(2, network);
        Assert.Equal(1, src.ReDecodes);
        Assert.Equal(0, src.ReDecodesSoon);
        Assert.Equal(1, src.NetworkReDecodes);
        Assert.Equal(1, src.ReDecodeSources["network"]);
    }
}

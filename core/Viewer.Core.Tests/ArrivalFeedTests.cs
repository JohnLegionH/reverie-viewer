// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every region's layer waits for the meshes and materials its own objects need, and a mesh or material source is shared by all of them. An arrival
/// has to reach every layer that subscribed, not only the first one to look: otherwise a layer whose object waits for an asset that another layer took
/// the arrival of never draws it (a worn piece in the region entered, for an asset the region left had also asked for).
/// </summary>
public class ArrivalFeedTests
{
    private static MeshSource Source() =>
        new(new Viewer.Core.Assets.SessionMeshes(
            new Viewer.Core.Assets.SessionAssets(new Viewer.Core.Assets.SharedAssetStore(), _ => true, (id, _) => Task.FromResult(
                MeshZoo.Assets.TryGetValue(id, out var f) ? Viewer.Core.Assets.AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(MeshDecodeTests.MeshDir, f))) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound))),
            (i, b) => { var r = Viewer.Core.Protocol.LmvMesh.Decode(i, b); return (r.Geometry, r.Refusal); }));

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public async Task A_mesh_arrival_reaches_every_subscribed_layer()
    {
        using var src = Source();
        var first = src.Subscribe();
        var second = src.Subscribe();
        var id = MeshZoo.Assets.Keys.First();
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);

        Assert.True(first.TryTake(out var a));   // the layer that looks first takes it
        Assert.Equal(id, a);
        Assert.True(second.TryTake(out var b));  // the other layer is told too
        Assert.Equal(id, b);
        Assert.False(first.TryTake(out _));
        Assert.False(second.TryTake(out _));
    }

    [Fact]
    public async Task A_layer_that_subscribes_later_is_not_told_of_arrivals_before_it_subscribed()
    {
        using var src = Source();
        var early = src.Subscribe();
        var id = MeshZoo.Assets.Keys.First();
        src.TryGet(id, out _);
        await Until(() => src.Pending == 0);
        var late = src.Subscribe();
        Assert.True(early.TryTake(out _));
        Assert.False(late.TryTake(out _));   // it asks for what it needs and finds it ready
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The regions of a session share one texture source and one asset store: a texture two regions' layers ask for one after the other
/// (the first has uploaded it and let its decoded copy go) is not fetched from the asset source a second time.
/// </summary>
public class SharedTextureFetchTests
{
    private static readonly Guid Id = new(9, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly byte[] Codestream = LmvTexture.Encode(64, 64, Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)(i * 5)).ToArray(), false);

    private static void ReadyAt(TextureSource s, int level)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        MeshAvailability a;
        while ((a = s.TryGetLevel(Id, level, out _)) == MeshAvailability.Pending && DateTime.UtcNow < until)
            Thread.Sleep(5);
        Assert.Equal(MeshAvailability.Ready, a);
    }

    [Fact]
    public void A_texture_the_second_layer_asks_for_after_the_first_released_its_copy_is_fetched_once()
    {
        int fetches = 0;
        using var s = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(AssetFetchResult.Ok(Codestream));
        }));

        ReadyAt(s, 0);          // the anchor region's layer
        s.ReleaseLevel(Id, 0);  // uploaded: the CPU keeps no copy
        ReadyAt(s, 0);          // a neighbour's layer asks for the same texture

        Assert.Equal(1, fetches);
    }

    [Fact]
    public void Control_two_layers_with_a_store_each_fetch_the_texture_twice()
    {
        int fetches = 0;
        TextureSource Layer() => new(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(AssetFetchResult.Ok(Codestream));
        }));
        using var anchor = Layer();
        using var neighbour = Layer();

        ReadyAt(anchor, 0);
        ReadyAt(neighbour, 0);

        Assert.Equal(2, fetches);   // the control: the one shared store, not the test, is what makes the count one
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The small copy of a texture kept for a projector's picture. A decode prepared off the main thread hands the host a level with
/// no pixels of its own (the engine image holds them), so the copy has to come from the image data the engine holds, which carries the whole
/// mip chain with level 0 first.</summary>
public class ProjectorSourceTests
{
    private static readonly Guid Id = new(9, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly byte[] Codestream = LmvTexture.Encode(32, 32, Enumerable.Range(0, 32 * 32 * 4).Select(i => (byte)(i * 7)).ToArray(), false);

    [Fact]
    public void A_level_prepared_off_the_main_thread_has_no_pixels_of_its_own()
    {
        using var s = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(Codestream))));
        s.Prepare = d => new object();
        DecodedTexture? tex = null;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (s.TryGetLevel(Id, 0, out tex, out _) == MeshAvailability.Pending && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.NotNull(tex);
        Assert.Empty(tex!.Rgba);   // the empty-data case: a copy made from Rgba would be handed 0 bytes for a 32 x 32 image
    }

    [Fact]
    public void The_copy_is_level_zero_of_the_engine_data_with_its_whole_mip_chain()
    {
        var chain = new byte[(8 * 8 + 4 * 4 + 2 * 2 + 1) * 4];
        for (int i = 0; i < 8 * 8 * 4; i++) chain[i] = (byte)(i % 251);
        var r = ProjectorPicture.SourceFrom(chain, 8, 8, 256)!.Value;
        Assert.Equal((8, 8), (r.Width, r.Height));
        Assert.Equal(chain.Take(8 * 8 * 4), r.Rgba);
    }

    [Fact]
    public void A_large_image_is_averaged_down_to_the_size_limit_keeping_its_shape()
    {
        var px = new byte[64 * 32 * 4];
        for (int i = 0; i < 64 * 32; i++) { px[i * 4] = 200; px[i * 4 + 1] = 100; px[i * 4 + 2] = 40; px[i * 4 + 3] = 255; }
        var r = ProjectorPicture.SourceFrom(px, 64, 32, 16)!.Value;
        Assert.Equal((16, 8), (r.Width, r.Height));
        Assert.Equal(16 * 8 * 4, r.Rgba.Length);
        Assert.All(Enumerable.Range(0, 16 * 8), i => Assert.Equal((200, 100, 40, 255), ((int)r.Rgba[i * 4], r.Rgba[i * 4 + 1], r.Rgba[i * 4 + 2], r.Rgba[i * 4 + 3])));
    }

    [Fact]
    public void No_data_gives_no_copy_instead_of_a_crash()
    {
        Assert.Null(ProjectorPicture.SourceFrom([], 32, 32, 256));
        Assert.Null(ProjectorPicture.SourceFrom(new byte[10], 32, 32, 256));
    }
}

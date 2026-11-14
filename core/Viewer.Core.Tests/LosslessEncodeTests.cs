// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The lossless encoder for fixtures whose exact pixels matter.</summary>
public class LosslessEncodeTests
{
    [Theory]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    [InlineData(256, 16)]
    [InlineData(256, 64)]
    public void A_lossless_codestream_decodes_bit_exact_through_the_guarded_decoder(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(w * 1000 + h);
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)rng.Next(256);
            rgba[i * 4 + 1] = (byte)rng.Next(256);
            rgba[i * 4 + 2] = (byte)rng.Next(256);
            rgba[i * 4 + 3] = 255;
        }
        var d = LmvTexture.Decode(LmvTexture.EncodeLossless(w, h, rgba));
        Assert.True(d.Ok, d.Refusal);
        Assert.Equal((w, h), (d.Texture!.Width, d.Texture.Height));
        Assert.Equal(rgba, d.Texture.Rgba);
    }

    [Fact]
    public void The_default_encoder_is_lossy_so_the_lossless_one_is_needed()
    {
        var rgba = new byte[64 * 64 * 4];
        var rng = new Random(7);
        for (int i = 0; i < rgba.Length; i++) rgba[i] = i % 4 == 3 ? (byte)255 : (byte)rng.Next(256);
        var d = LmvTexture.Decode(LmvTexture.Encode(64, 64, rgba, false));
        Assert.True(d.Ok, d.Refusal);
        Assert.NotEqual(rgba, d.Texture!.Rgba);
    }
}

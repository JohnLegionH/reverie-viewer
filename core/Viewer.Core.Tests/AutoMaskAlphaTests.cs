// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official cut-out rule for a face without a material: a texture whose alpha is all or nothing (the sixteen-bucket histogram of
/// its alpha and of its 2x2 averages, <see cref="AlphaMask"/>) is drawn as a mask, not blended, when the face's colour alpha is 1, it
/// does not glow, it is not rigged and no material says blend; the two RenderAutoMaskAlpha settings decide for lit and for fullbright faces.
/// The expected values are worked by hand from the rule (bucket = alpha / 16; mid-range = buckets 2 to 12; limit = samples / 48).
/// </summary>
public class AlphaMaskAnalysisTests
{
    private const int N = 64;

    private static byte[] Picture(Func<int, int, byte> alpha, int w = N, int h = N)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; b[i] = 10; b[i + 1] = 20; b[i + 2] = 30; b[i + 3] = alpha(x, y); }
        return b;
    }

    [Fact]
    public void A_picture_clear_on_one_half_and_solid_on_the_other_is_a_mask()
    {
        var p = Picture((x, y) => (byte)(x < N / 2 ? 0 : 255));
        Assert.True(AlphaMask.Analyze(p, N, N));
    }

    [Fact]
    public void A_one_pixel_checkerboard_of_clear_and_solid_is_not_a_mask_because_its_2x2_averages_are_mid_range()
    {
        var p = Picture((x, y) => (byte)((x + y) % 2 == 0 ? 255 : 0));
        Assert.False(AlphaMask.Analyze(p, N, N));
    }

    [Theory]
    [InlineData(255, true)]    // solid everywhere: all in the top bucket and the total is 255 per sample
    [InlineData(0, true)]      // clear everywhere: all in the bottom bucket and the total is 0
    [InlineData(128, false)]   // mid-range everywhere
    [InlineData(250, false)]   // all in the upper half but not 255: an intentional translucency
    [InlineData(5, false)]     // all in the lower half but not 0: the same
    [InlineData(200, false)]   // bucket 12 is mid-range
    [InlineData(32, false)]    // bucket 2 is mid-range
    [InlineData(31, false)]
    public void A_uniform_alpha_is_a_mask_only_when_it_is_clear_or_solid(int alpha, bool expected)
    {
        var p = Picture((x, y) => (byte)alpha);
        Assert.Equal(expected, AlphaMask.Analyze(p, N, N));
    }

    [Theory]
    [InlineData(34, true)]    // 34 isolated mid pixels in clear blocks count 5 samples each (1 + 4 in the averaged histogram): 170, and the limit is 8192 / 48 = 170, not exceeded
    [InlineData(35, false)]   // 175 is over it
    public void The_mid_range_limit_is_a_forty_eighth_of_the_samples(int midPixels, bool expected)
    {
        // each mid pixel (128) sits alone in its own 2x2 block of an otherwise clear left half; the right half is solid
        var mids = new HashSet<(int, int)>();
        for (int i = 0; i < midPixels; i++) mids.Add((2 * (i % 16), 2 * (i / 16)));
        var p = Picture((x, y) => x >= N / 2 ? (byte)255 : mids.Contains((x, y)) ? (byte)128 : (byte)0);
        Assert.Equal(expected, AlphaMask.Analyze(p, N, N));
    }

    [Fact]
    public void A_picture_one_pixel_wide_is_read_pixel_by_pixel()
    {
        Assert.True(AlphaMask.Analyze(Picture((x, y) => (byte)(y < 32 ? 0 : 255), 1, N), 1, N));
        Assert.False(AlphaMask.Analyze(Picture((x, y) => (byte)128, 1, N), 1, N));
        // four samples, one mid-range: the limit is 4 / 48 = 0, so one is over it
        var four = Picture((x, y) => y == 3 ? (byte)128 : (byte)(y == 0 ? 0 : 255), 1, 4);
        Assert.False(AlphaMask.Analyze(four, 1, 4));
    }

    [Fact]
    public void Hair_strands_with_soft_edges_are_blended_and_only_all_or_nothing_strands_are_cut_out()
    {
        // vertical strands 16 pixels wide on a clear ground; the soft ones fall from solid to clear over 8 pixels on each side, the hard ones in one pixel
        byte Strand(int x, int softness)
        {
            int d = Math.Abs((x % 32) - 16);   // distance from the strand's middle
            if (softness <= 1) return (byte)((x % 32) is >= 8 and < 24 ? 255 : 0);   // edges on even pixels: the analysis averages 2x2 blocks
            return (byte)Math.Clamp(255 - (d - 4) * 255 / softness, 0, 255);
        }
        var soft = Picture((x, _) => Strand(x, 8));
        var hard = Picture((x, _) => Strand(x, 1));
        Assert.False(AlphaMask.Analyze(soft, N, N));
        Assert.True(AlphaMask.Analyze(hard, N, N));
        // a face of the soft texture stays blended, however the setting stands, and a rigged face of even the hard one is never cut out (the official rule)
        var face = new AutoMaskFace(1f, 0f, AlphaMask.Analyze(soft, N, N), Rigged: false, Fullbright: false, Hud: false, HasPbrMaterial: false, LegacyMaterialBlends: false);
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Blended, face, AutoMaskSettings.Official));
        var rigged = face with { TextureIsMask = AlphaMask.Analyze(hard, N, N), Rigged = true };
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, rigged, AutoMaskSettings.Official));
        Assert.Equal(FaceAlpha.Mask, FaceLooks.Plain(AlphaClass.Binary, rigged with { Rigged = false }, AutoMaskSettings.Official));   // the positive state
    }

    [Fact]
    public void A_decoded_binary_texture_is_a_mask_and_a_smooth_gradient_or_an_opaque_one_is_not()
    {
        var binary = LmvTexture.Decode(TextureFixtures.Binary64).Texture!;
        Assert.Equal(AlphaClass.Binary, binary.Alpha);
        Assert.True(binary.IsAlphaMask);
        var blended = LmvTexture.Decode(TextureFixtures.Blended64).Texture!;
        Assert.Equal(AlphaClass.Blended, blended.Alpha);
        Assert.False(blended.IsAlphaMask);
        var opaque = LmvTexture.Decode(TextureFixtures.Opaque64).Texture!;
        Assert.Equal(AlphaClass.Opaque, opaque.Alpha);
        Assert.False(opaque.IsAlphaMask);
    }
}

public class AutoMaskRuleTests
{
    private static readonly AutoMaskFace Plain = new(ColourAlpha: 1f, Glow: 0f, TextureIsMask: true, Rigged: false, Fullbright: false, Hud: false, HasPbrMaterial: false, LegacyMaterialBlends: false);
    private static readonly AutoMaskSettings Official = AutoMaskSettings.Official;

    [Fact]
    public void The_official_settings_are_both_on_and_the_cutoff_is_a_third()
    {
        Assert.True(Official.Deferred);
        Assert.True(Official.NonDeferred);
        Assert.Equal(0.33f, AutoMask.Cutoff);
    }

    [Fact]
    public void A_plain_face_with_a_mask_texture_is_drawn_as_a_mask() => Assert.True(AutoMask.CanRenderAsMask(Plain, Official));

    [Fact]
    public void A_face_with_any_of_these_is_not()
    {
        Assert.True(AutoMask.CanRenderAsMask(Plain, Official));                                   // the positive state first
        Assert.False(AutoMask.CanRenderAsMask(Plain with { ColourAlpha = 0.99f }, Official));   // face alpha
        Assert.False(AutoMask.CanRenderAsMask(Plain with { Glow = 0.1f }, Official));            // glowing masks are not made
        Assert.False(AutoMask.CanRenderAsMask(Plain with { TextureIsMask = false }, Official));  // the texture does not qualify
        Assert.False(AutoMask.CanRenderAsMask(Plain with { Rigged = true }, Official));          // never a rigged face
        Assert.False(AutoMask.CanRenderAsMask(Plain with { HasPbrMaterial = true }, Official));
        Assert.False(AutoMask.CanRenderAsMask(Plain with { LegacyMaterialBlends = true }, Official));
    }

    [Fact]
    public void The_deferred_setting_decides_for_lit_faces_and_the_other_for_fullbright_and_heads_up_faces()
    {
        var deferredOff = new AutoMaskSettings(Deferred: false, NonDeferred: true);
        var nonDeferredOff = new AutoMaskSettings(Deferred: true, NonDeferred: false);
        Assert.False(AutoMask.CanRenderAsMask(Plain, deferredOff));
        Assert.True(AutoMask.CanRenderAsMask(Plain with { Fullbright = true }, deferredOff));
        Assert.True(AutoMask.CanRenderAsMask(Plain, nonDeferredOff));
        Assert.False(AutoMask.CanRenderAsMask(Plain with { Fullbright = true }, nonDeferredOff));
        Assert.False(AutoMask.CanRenderAsMask(Plain with { Hud = true }, nonDeferredOff));
        Assert.True(AutoMask.CanRenderAsMask(Plain with { Hud = true }, deferredOff));
    }

    [Theory]
    [InlineData(AlphaClass.Binary, true, FaceAlpha.Mask)]
    [InlineData(AlphaClass.Blended, true, FaceAlpha.Mask)]     // mostly all-or-nothing, a few soft edge pixels: the official histogram still says mask
    [InlineData(AlphaClass.Binary, false, FaceAlpha.Blend)]    // high-frequency alpha stays blended
    [InlineData(AlphaClass.Blended, false, FaceAlpha.Blend)]
    [InlineData(AlphaClass.Opaque, true, FaceAlpha.Opaque)]    // no alpha worth drawing
    [InlineData(null, true, FaceAlpha.Opaque)]                 // texture not on screen yet
    public void A_plain_face_is_masked_blended_or_opaque_by_its_texture(AlphaClass? texture, bool isMask, FaceAlpha expected) =>
        Assert.Equal(expected, FaceLooks.Plain(texture, Plain with { TextureIsMask = isMask }, Official));

    [Fact]
    public void A_face_the_rule_refuses_keeps_the_plain_blend()
    {
        Assert.Equal(FaceAlpha.Mask, FaceLooks.Plain(AlphaClass.Binary, Plain, Official));       // the positive state first
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, Plain with { ColourAlpha = 0.5f }, Official));
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, Plain with { Rigged = true }, Official));
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, Plain with { Glow = 0.5f }, Official));
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, Plain, new AutoMaskSettings(false, false)));
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(AlphaClass.Binary, Plain with { Hud = true }, Official));   // the heads-up display is not drawn as a mask
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Plain(null, Plain with { ColourAlpha = 0.5f }, Official));       // face alpha with the texture not yet on screen
    }
}

/// <summary>
/// The cut-out render case's texture (fixtures\textures\tex-alpha-mask.j2c): a 64x64 green panel with five square holes, alpha 255 inside and 0 outside,
/// made here from a formula and encoded with LibreMetaverse's encoder (as the other fixture textures are). The first test regenerates
/// it when NV_WRITE_FIXTURE is set; the second checks the committed file is what the case needs: alpha all or nothing, and a mask by the official analysis.
/// </summary>
public class AutoMaskFixtureTests
{
    internal static byte[] Pixels() => TextureFixtures.Rgba(64, 64, (x, y) =>
    {
        // a green panel with five square holes, every edge on an even pixel (the analysis averages 2x2 blocks, so an edge between blocks is not mid-range)
        bool Hole(int x0, int y0, int size) => x >= x0 && x < x0 + size && y >= y0 && y < y0 + size;
        bool holes = Hole(8, 8, 12) || Hole(44, 8, 12) || Hole(8, 44, 12) || Hole(44, 44, 12) || Hole(26, 26, 12);
        return (60, 170, 50, (byte)(holes ? 0 : 255));
    });

    private static string Path_ => System.IO.Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "tex-alpha-mask.j2c");

    [Fact]
    public void Regenerate_the_cut_out_texture_when_asked()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_FIXTURE") != "1") return;   // only a deliberate regeneration writes into the repo
        File.WriteAllBytes(Path_, LmvTexture.Encode(64, 64, Pixels(), withAlpha: true));
    }

    [Fact]
    public void The_committed_cut_out_texture_has_all_or_nothing_alpha_and_is_a_mask()
    {
        Assert.True(File.Exists(Path_), "fixtures\textures\tex-alpha-mask.j2c is missing");
        var t = LmvTexture.Decode(File.ReadAllBytes(Path_)).Texture!;
        Assert.Equal(AlphaClass.Binary, t.Alpha);
        Assert.True(t.IsAlphaMask);
    }
}

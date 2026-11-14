// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Each face kind's glow inputs and the glow blur's arithmetic, engine-free. The expected values are worked from the
/// behaviour in Docs/GLOW-SPEC.md, not from the code under test.
/// </summary>
public class GlowTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.1f, 25f / 255f)]       // 0.1 * 255 = 25.5, truncated
    [InlineData(0.5f, 127f / 255f)]
    [InlineData(1f, 1f)]
    [InlineData(-0.3f, 0f)]
    [InlineData(1.7f, 1f)]
    public void A_glow_value_is_kept_as_a_byte_truncated(float glow, float kept)
    {
        Assert.Equal(kept, FaceGlow.Quantise(glow), 6);
    }

    [Fact]
    public void A_legacy_face_glows_by_its_value_whatever_its_alpha_mode()
    {
        Assert.Equal(127f / 255f, FaceGlow.Weight(0.5f, GlowFace.Legacy), 6);
        Assert.Equal(0f, FaceGlow.Weight(0f, GlowFace.Legacy));
    }

    [Fact]
    public void A_pbr_face_glows_by_its_value_times_its_brightest_emissive_channel()
    {
        float lum = FaceGlow.EmissiveMax(0.5f, 0.2f, 0.1f, 1f, 1f, 1f);
        Assert.Equal(0.5f, lum, 6);
        Assert.Equal(1f * 0.5f, FaceGlow.Weight(1f, GlowFace.Pbr, lum), 6);
        // a texel dims the emissive colour per channel; the brightest channel after that counts
        Assert.Equal(0.2f * 0.8f, FaceGlow.EmissiveMax(0.5f, 0.2f, 0.1f, 0f, 0.8f, 0.5f), 6);
        // no emissive light, no glow
        Assert.Equal(0f, FaceGlow.Weight(1f, GlowFace.Pbr, FaceGlow.EmissiveMax(0, 0, 0, 1, 1, 1)));
    }

    [Fact]
    public void A_pbr_face_in_mask_alpha_mode_draws_no_glow()
    {
        Assert.Equal(0f, FaceGlow.Weight(1f, GlowFace.PbrMask, 1f));
    }

    [Fact]
    public void The_glow_at_a_pixel_is_the_weight_times_the_texture_alpha()
    {
        Assert.Equal(0.25f, FaceGlow.AtPixel(0.5f, 0.5f), 6);
        Assert.Equal(0f, FaceGlow.AtPixel(0.5f, 0f));
        Assert.Equal(0.5f, FaceGlow.AtPixel(0.5f, 1f), 6);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void The_glow_scale_takes_a_colour_to_the_one_whose_display_value_is_the_weight_times_its_own(float weight)
    {
        // exact at every colour, the dark end of the display curve included
        foreach (float c in new[] { 0.001f, 0.01f, 0.058f, 0.2f, 0.5f, 0.8f, 1f })
        {
            float shown = Encode(c * FaceGlow.Scale(c, weight));
            float want = weight * Encode(c);
            Assert.True(MathF.Abs(shown - want) < 1e-4f, $"colour {c}, weight {weight}: {shown} against {want}");
        }
        Assert.Equal(0f, FaceGlow.Scale(0.5f, 0f));
        Assert.Equal(1f, FaceGlow.Scale(0.5f, 1f), 5);
    }

    private static float Encode(float c) => FaceGlow.Encode(c);

    [Fact]
    public void The_blur_uses_the_official_viewer_s_numbers()
    {
        Assert.Equal(512, GlowBlur.Resolution);
        Assert.Equal(4, GlowBlur.Passes);                      // two iterations: horizontal, vertical, horizontal, vertical
        Assert.Equal(1.3f / 512f, GlowBlur.Delta, 9);
        Assert.Equal(new[] { 0.25f, 0.5f, 0.8f, 1f, 1f, 0.8f, 0.5f, 0.25f }, GlowBlur.Kernel.ToArray());
        Assert.Equal(5.1f, GlowBlur.Kernel.ToArray().Sum(), 5);
    }

    [Fact]
    public void A_flat_glow_picture_gains_the_kernel_sum_times_the_strength_at_each_pass_and_stops_at_one()
    {
        // a flat picture is unchanged in shape: each pass multiplies it by 5.1 * 0.325 = 1.6575, clamped to 1 after each
        var rgb = Enumerable.Repeat(0.1f, 16 * 16 * 3).ToArray();
        var outp = GlowBlur.Run(rgb, 16, 16);
        float want = 0.1f;
        for (int i = 0; i < GlowBlur.Passes; i++) want = MathF.Min(1f, want * 5.1f * 0.325f);
        Assert.All(outp, v => Assert.Equal(want, v, 4));
        Assert.Equal(0.7547f, want, 3);
        var bright = GlowBlur.Run(Enumerable.Repeat(0.9f, 16 * 16 * 3).ToArray(), 16, 16);
        Assert.All(bright, v => Assert.Equal(1f, v, 6));
    }

    [Fact]
    public void A_single_bright_pixel_spreads_to_its_neighbours_and_stays_symmetric()
    {
        const int n = GlowBlur.Resolution;   // the taps are a fixed share of the view apart, so the picture must be the glow size
        var rgb = new float[n * n * 3];
        for (int c = 0; c < 3; c++) rgb[((n / 2) * n + n / 2) * 3 + c] = 1f;
        var outp = GlowBlur.Run(rgb, n, n);
        float At(int x, int y) => outp[(y * n + x) * 3];
        Assert.Equal(outp[((n / 2) * n + n / 2) * 3], outp[((n / 2) * n + n / 2) * 3 + 2]);   // the channels are blurred alike
        Assert.True(At(n / 2, n / 2) > 0f);
        Assert.True(At(n / 2 + 3, n / 2) > 0f);                // the halo reaches several pixels out
        Assert.Equal(0f, At(n / 2 + 12, n / 2), 6);            // but not far: two passes of four taps each way
        Assert.Equal(At(n / 2 + 2, n / 2), At(n / 2 - 2, n / 2), 4);
        Assert.Equal(At(n / 2, n / 2 + 2), At(n / 2, n / 2 - 2), 4);
        Assert.True(At(n / 2 + 1, n / 2) < At(n / 2, n / 2));
    }

    [Fact]
    public void An_image_with_no_glow_is_unchanged_and_the_glow_adds_then_clamps()
    {
        Assert.Equal(0.4f, GlowBlur.Add(0.4f, 0f), 6);
        Assert.Equal(0.7f, GlowBlur.Add(0.4f, 0.3f), 6);
        Assert.Equal(1f, GlowBlur.Add(0.9f, 0.3f), 6);
        var none = GlowBlur.Run(new float[8 * 8 * 3], 8, 8);
        Assert.All(none, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void A_rectangle_filling_the_view_gives_the_flat_picture_and_a_part_covered_pixel_takes_its_share()
    {
        var pic = new float[GlowBlur.Resolution * GlowBlur.Resolution * 3];
        GlowBlur.Fill(pic, 0f, 0f, 1f, 1f, 0.2f, 0.4f, 0.6f);
        Assert.Equal(0.2f, pic[0], 6);
        Assert.Equal(0.6f, pic[^1], 6);
        var half = new float[pic.Length];
        GlowBlur.Fill(half, 0.25f, 0.25f, 0.75f + 0.5f / GlowBlur.Resolution, 0.75f, 1f, 1f, 1f);   // the last column half covered
        var (r, _, _) = GlowBlur.At(half, 0.5f, 0.5f);
        Assert.Equal(1f, r, 4);
        Assert.Equal(0f, GlowBlur.At(half, 0.1f, 0.1f).R, 6);
        int lastCol = (int)(0.75f * GlowBlur.Resolution);
        Assert.Equal(0.5f, half[((GlowBlur.Resolution / 2) * GlowBlur.Resolution + lastCol) * 3], 3);
    }
}

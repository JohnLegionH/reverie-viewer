// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>L1 material parameter assertions: how a face's material decides its alpha handling, maps and specular values.</summary>
public class FaceLookTests
{
    private static readonly MaterialMap NoMap = new(Guid.Empty, 0, 0, 1, 1, 0);
    private static readonly Guid NormalId = Guid.NewGuid(), SpecId = Guid.NewGuid();

    private static LegacyMaterial M(LegacyAlphaMode mode, byte cutoff = 0, bool normal = false, bool spec = false, byte exponent = 51, byte env = 0) =>
        new(Guid.NewGuid(), normal ? new MaterialMap(NormalId, 0.25f, 0, 2, 3, 0.5f) : NoMap, spec ? new MaterialMap(SpecId, 0, 0.5f, 4, 4, 0) : NoMap,
            new Rgba(1f, 0.5f, 0.25f, 1f), exponent, env, mode, cutoff);

    [Theory]
    [InlineData(1f, null, FaceAlpha.Opaque)]
    [InlineData(1f, AlphaClass.Opaque, FaceAlpha.Opaque)]
    [InlineData(1f, AlphaClass.Binary, FaceAlpha.Blend)]
    [InlineData(1f, AlphaClass.Blended, FaceAlpha.Blend)]
    [InlineData(0.5f, null, FaceAlpha.Blend)]
    [InlineData(0.5f, AlphaClass.Opaque, FaceAlpha.Blend)]
    public void A_face_without_a_material_blends_when_its_colour_or_texture_has_alpha(float a, AlphaClass? t, FaceAlpha expected) =>
        Assert.Equal(expected, FaceLooks.Plain(a, t));

    [Theory]
    [InlineData(LegacyAlphaMode.None, (byte)0, AlphaClass.Blended, FaceAlpha.Opaque)]
    [InlineData(LegacyAlphaMode.Blend, (byte)0, AlphaClass.Blended, FaceAlpha.Blend)]
    [InlineData(LegacyAlphaMode.Blend, (byte)0, AlphaClass.Opaque, FaceAlpha.Opaque)]
    [InlineData(LegacyAlphaMode.Mask, (byte)128, AlphaClass.Binary, FaceAlpha.Mask)]
    [InlineData(LegacyAlphaMode.Mask, (byte)128, AlphaClass.Blended, FaceAlpha.Mask)]
    [InlineData(LegacyAlphaMode.Mask, (byte)128, AlphaClass.Opaque, FaceAlpha.Opaque)]
    [InlineData(LegacyAlphaMode.Mask, (byte)0, AlphaClass.Binary, FaceAlpha.Opaque)]
    [InlineData(LegacyAlphaMode.Emissive, (byte)0, AlphaClass.Blended, FaceAlpha.Emissive)]
    [InlineData(LegacyAlphaMode.Emissive, (byte)0, AlphaClass.Opaque, FaceAlpha.Emissive)]
    public void A_legacy_alpha_mode_decides_how_the_texture_alpha_is_used(LegacyAlphaMode mode, byte cutoff, AlphaClass t, FaceAlpha expected) =>
        Assert.Equal(expected, FaceLooks.Legacy(M(mode, cutoff), 1f, t).Alpha);

    [Theory]
    [InlineData(LegacyAlphaMode.None)]
    [InlineData(LegacyAlphaMode.Mask)]
    [InlineData(LegacyAlphaMode.Emissive)]
    public void A_translucent_colour_blends_whatever_the_mode(LegacyAlphaMode mode) =>
        Assert.Equal(FaceAlpha.Blend, FaceLooks.Legacy(M(mode, 128), 0.6f, AlphaClass.Opaque).Alpha);

    [Fact]
    public void While_the_texture_is_not_on_screen_mask_and_blend_draw_solid()
    {
        Assert.Equal(FaceAlpha.Opaque, FaceLooks.Legacy(M(LegacyAlphaMode.Mask, 128), 1f, null).Alpha);
        Assert.Equal(FaceAlpha.Opaque, FaceLooks.Legacy(M(LegacyAlphaMode.Blend), 1f, null).Alpha);
        Assert.Equal(FaceAlpha.Emissive, FaceLooks.Legacy(M(LegacyAlphaMode.Emissive), 1f, null).Alpha);
    }

    [Theory]
    [InlineData((byte)128, 128f / 255f)]
    [InlineData((byte)140, 140f / 255f)]
    [InlineData((byte)200, 200f / 255f)]
    [InlineData((byte)255, 1f)]
    public void The_mask_cutoff_is_the_byte_over_255(byte cutoff, float expected)
    {
        var l = FaceLooks.Legacy(M(LegacyAlphaMode.Mask, cutoff), 1f, AlphaClass.Binary);
        Assert.Equal(expected, l.Cutoff, 6);
        Assert.Equal(0f, FaceLooks.Legacy(M(LegacyAlphaMode.Blend, cutoff), 1f, AlphaClass.Binary).Cutoff);
    }

    [Fact]
    public void A_specular_map_takes_the_material_colour_exponent_and_environment()
    {
        var l = FaceLooks.Legacy(M(LegacyAlphaMode.None, spec: true, exponent: 51, env: 102), 1f, AlphaClass.Opaque);
        Assert.True(l.HasSpecularMap);
        Assert.Equal(SpecId, l.Specular.TextureId);
        Assert.Equal(4f, l.Specular.RepeatU);
        Assert.Equal(0.5f, l.Specular.OffsetV);
        Assert.Equal(new Rgba(1f, 0.5f, 0.25f, 1f), l.SpecularColour);
        Assert.Equal(51f / 255f, l.Glossiness, 6);
        Assert.Equal(102f / 255f, l.Environment, 6);
    }

    /// <summary>The official viewer gives a face whose material has no specular map the face's own shiny level (llvovolume.cpp, the draw info's
    /// specular colour: none, 0.25, 0.5 and 0.75 for the four levels, used as the colour, the glossiness and the environment intensity alike);
    /// the material's colour, exponent and environment intensity count only with a specular map.</summary>
    [Theory]
    [InlineData((byte)0, 0f)]
    [InlineData((byte)1, 0.25f)]
    [InlineData((byte)2, 0.5f)]
    [InlineData((byte)3, 0.75f)]
    public void Without_a_specular_map_the_specular_is_the_faces_shiny_level(byte shiny, float level)
    {
        var l = FaceLooks.Legacy(M(LegacyAlphaMode.None, normal: true, exponent: 200, env: 255), 1f, AlphaClass.Opaque, shiny);
        Assert.False(l.HasSpecularMap);
        Assert.Equal(new Rgba(level, level, level, level), l.SpecularColour);
        Assert.Equal(level, l.Glossiness);
        Assert.Equal(level, l.Environment);
    }

    [Fact]
    public void With_a_specular_map_the_faces_shiny_level_counts_for_nothing()
    {
        var l = FaceLooks.Legacy(M(LegacyAlphaMode.None, spec: true, exponent: 51, env: 102), 1f, AlphaClass.Opaque, 3);
        Assert.Equal(new Rgba(1f, 0.5f, 0.25f, 1f), l.SpecularColour);
        Assert.Equal(51f / 255f, l.Glossiness, 6);
        Assert.Equal(102f / 255f, l.Environment, 6);
    }

    [Fact]
    public void The_normal_map_keeps_its_own_placement()
    {
        var l = FaceLooks.Legacy(M(LegacyAlphaMode.None, normal: true), 1f, AlphaClass.Opaque);
        Assert.True(l.HasNormalMap);
        Assert.Equal(new MaterialMap(NormalId, 0.25f, 0, 2, 3, 0.5f), l.Normal);
        Assert.False(FaceLooks.Legacy(M(LegacyAlphaMode.None), 1f, AlphaClass.Opaque).HasNormalMap);
    }

    [Theory]
    [InlineData(PbrAlphaMode.Opaque, FaceAlpha.Opaque, 0f)]
    [InlineData(PbrAlphaMode.Blend, FaceAlpha.Blend, 0f)]
    [InlineData(PbrAlphaMode.Mask, FaceAlpha.Mask, 0.3f)]
    public void A_pbr_alpha_mode_is_drawn_as_glTF_defines_it(PbrAlphaMode mode, FaceAlpha expected, float cutoff)
    {
        var (a, c) = FaceLooks.Pbr(new PbrMaterial { AlphaMode = mode, AlphaCutoff = 0.3f });
        Assert.Equal(expected, a);
        Assert.Equal(cutoff, c, 6);
    }
}

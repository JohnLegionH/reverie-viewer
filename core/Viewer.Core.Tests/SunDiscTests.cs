// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The sun's disc in the sky: bright at its centre whatever the light's strength, never darker than the sky behind it, tinted by the light's colour.
/// Each test builds its own values, so the class runs in parallel.</summary>
public class SunDiscTests
{
    private static SkyFrame Frame(Vector3 sunColour) =>
        new(0.5f, Vector3.UnitZ, sunColour, -Vector3.UnitZ, 0.5f, new Vector3(0.35f, 0.3f, 0.4f), new Vector3(0.4f, 0.35f, 0.5f), new Vector3(0.2f, 0.3f, 0.5f), 0.19f, 0.7f, 0.0002f, 0.8f);

    [Fact]
    public void The_tint_of_a_dim_dusk_sun_is_still_a_bright_disc()
    {
        // the sunlight at dusk is nearly gone (a few thousandths); the disc is drawn at full brightness all the same
        var t = SkyLighting.SunDiscTint(Frame(new Vector3(0.004f, 0.002f, 0.001f)));
        Assert.Equal(1f, MathF.Max(t.X, MathF.Max(t.Y, t.Z)), 4);
        Assert.True(t.X > 0.6f && t.Y > 0.6f && t.Z > 0.6f, $"the tint {t} is not a pale disc");
    }

    [Fact]
    public void The_tint_carries_the_hue_of_the_light()
    {
        var t = SkyLighting.SunDiscTint(Frame(new Vector3(1.8f, 1.2f, 0.9f)));
        Assert.True(t.X > t.Y && t.Y > t.Z, $"an orange sun gives a tint with red over green over blue, got {t}");
    }

    [Fact]
    public void A_light_with_no_colour_gives_a_white_disc()
    {
        var t = SkyLighting.SunDiscTint(Frame(Vector3.Zero));
        Assert.Equal(Vector3.One, t);
    }

    [Fact]
    public void The_disc_is_never_darker_than_the_sky_behind_it_and_brighter_at_its_centre()
    {
        var sky = new Vector3(0.9f, 0.6f, 0.5f);
        var tint = new Vector3(1f, 0.85f, 0.75f);
        var centre = SkyLighting.DiscOverSky(sky, tint, 1f, 1.1f);
        Assert.True(centre.X >= sky.X && centre.Y >= sky.Y && centre.Z >= sky.Z, $"{centre} is darker than the sky {sky}");
        Assert.True(centre.X + centre.Y + centre.Z > sky.X + sky.Y + sky.Z, $"the centre {centre} is not brighter than the sky {sky}");
        Assert.True(centre.X <= 1.1f && centre.Y <= 1.1f && centre.Z <= 1.1f, "the ceiling holds");
    }

    [Fact]
    public void Outside_the_disc_the_sky_is_unchanged()
    {
        var sky = new Vector3(0.9f, 0.6f, 0.5f);
        Assert.Equal(sky, SkyLighting.DiscOverSky(sky, Vector3.One, 0f, 1.1f));
    }

    // ---- the disc drawn from the sky's sun texture (sunDiscF.glsl, on the square llvosky.cpp updateHeavenlyBodyGeometry makes)

    private static Vector3 Right(Vector3 sun) => Vector3.Normalize(Vector3.Cross(sun, Vector3.UnitZ));
    private static Vector3 Up(Vector3 sun) => Vector3.Cross(Right(sun), sun);
    private static float HalfSize(Vector3 sun, float scale, bool horizontal) => 0.05f * scale * (1f + (1f - sun.Z) * (horizontal ? 0.3f : 0.2f));

    [Fact]
    public void The_direction_of_the_sun_is_the_middle_of_the_picture()
    {
        var sun = Vector3.Normalize(new Vector3(1f, 0.3f, 0.5f));
        var c = SunDisc.Coordinates(sun, sun, 1f);
        Assert.NotNull(c);
        Assert.Equal(0.5f, c!.Value.X, 4);
        Assert.Equal(0.5f, c.Value.Y, 4);
    }

    [Fact]
    public void The_square_reaches_one_half_size_to_each_side_and_the_picture_runs_edge_to_edge()
    {
        var sun = Vector3.Normalize(new Vector3(1f, 0.3f, 0.5f));
        // the square's half size is the body factor (0.1) times the disk radius (0.5) times the scale, grown towards the horizon (0.3 across, 0.2 up)
        var right = SunDisc.Coordinates(Vector3.Normalize(sun + Right(sun) * HalfSize(sun, 1f, true)), sun, 1f);
        var left = SunDisc.Coordinates(Vector3.Normalize(sun - Right(sun) * HalfSize(sun, 1f, true)), sun, 1f);
        var top = SunDisc.Coordinates(Vector3.Normalize(sun + Up(sun) * HalfSize(sun, 1f, false)), sun, 1f);
        var bottom = SunDisc.Coordinates(Vector3.Normalize(sun - Up(sun) * HalfSize(sun, 1f, false)), sun, 1f);
        Assert.Equal(1f, right!.Value.X, 3); Assert.Equal(0.5f, right.Value.Y, 3);
        Assert.Equal(0f, left!.Value.X, 3);
        Assert.Equal(1f, top!.Value.Y, 3); Assert.Equal(0.5f, top.Value.X, 3);
        Assert.Equal(0f, bottom!.Value.Y, 3);
    }

    [Fact]
    public void Outside_the_square_and_behind_the_camera_there_is_no_disc()
    {
        var sun = Vector3.Normalize(new Vector3(1f, 0.3f, 0.5f));
        Assert.Null(SunDisc.Coordinates(Vector3.Normalize(sun + Right(sun) * HalfSize(sun, 1f, true) * 1.2f), sun, 1f));
        Assert.Null(SunDisc.Coordinates(-sun, sun, 1f));
    }

    [Fact]
    public void A_larger_sun_scale_makes_a_larger_disc()
    {
        var sun = Vector3.Normalize(new Vector3(1f, 0.3f, 0.5f));
        var d = Vector3.Normalize(sun + Right(sun) * HalfSize(sun, 1f, true) * 0.5f);
        float small = SunDisc.Coordinates(d, sun, 1f)!.Value.X - 0.5f;
        float large = SunDisc.Coordinates(d, sun, 2f)!.Value.X - 0.5f;
        Assert.Equal(small / 2f, large, 3);
    }

    [Fact]
    public void The_texture_is_the_skys_own_or_the_default_sun_texture_when_it_names_none()
    {
        var named = Guid.NewGuid();
        Assert.Equal(named, SunDisc.TextureOf(Frame(Vector3.One) with { SunTextureId = named }));
        Assert.Equal(new Guid("cce0f112-878f-4586-a2e2-a8f104bba271"), SunDisc.TextureOf(Frame(Vector3.One)));
    }

    [Fact]
    public void A_sky_asset_carries_its_sun_texture_and_scale_and_a_sky_without_them_none_and_one()
    {
        var id = Guid.NewGuid();
        var with = SettingsSky.Parse(System.Text.Encoding.UTF8.GetBytes($"<? llsd/notation ?>\n{{'type':'sky','sun_id':u{id},'sun_scale':r2.5}}"), out var why1);
        Assert.True(with is not null, why1);
        Assert.Equal(id, with!.SunTextureId);
        Assert.Equal(2.5f, with.SunScale, 4);
        var without = SettingsSky.Parse(System.Text.Encoding.UTF8.GetBytes("<? llsd/notation ?>\n{'type':'sky'}"), out var why2);
        Assert.True(without is not null, why2);
        Assert.Null(without!.SunTextureId);
        Assert.Equal(1f, without.SunScale, 4);
    }
}

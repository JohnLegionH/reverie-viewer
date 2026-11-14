// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A prim's light and projector as plain numbers (Docs/PROJECTOR-SPEC.md): the reach, the distance attenuation, the
/// projector's beam and the numbers an engine light is given.</summary>
public class ProjectorLightTests
{
    private static PrimLight Light(float radius = 10f, float falloff = 0f, float intensity = 1f) =>
        new(new Vector3(1f, 0.5f, 0.25f), intensity, radius, falloff, 0f);

    private static readonly Guid Pattern = new("4e560009-0000-4000-8000-0000000000a1");

    [Fact]
    public void The_reach_is_one_and_a_half_times_the_radius() => Assert.Equal(15f, LightRules.Reach(Light(10f)), 4);

    [Fact]
    public void The_linear_colour_is_the_colour_times_the_intensity()
    {
        var c = LightRules.LinearColour(Light(intensity: 0.5f));
        Assert.Equal(new Vector3(0.5f, 0.25f, 0.125f), c);
    }

    [Theory]
    [InlineData(0f, 0f, 2f)]        // at the centre with no falloff: twice the colour (the viewer's legacy factor)
    [InlineData(7.5f, 0f, 0.5f)]    // halfway to the reach: (1 - 0.5)^2 x 2
    [InlineData(15f, 0f, 0f)]       // at the reach: dark
    [InlineData(40f, 0f, 0f)]       // beyond it: dark
    [InlineData(0f, 1f, 0.8888889f)]  // falloff 1 is 0.5 in the curve: 2 x (1 / 1.5)^2
    public void Attenuation_follows_the_viewers_curve(float distance, float falloff, float expected)
    {
        Assert.Equal(expected, LightRules.Attenuation(Light(10f, falloff), distance), 4);
    }

    [Fact]
    public void A_light_with_no_projector_texture_has_no_beam() =>
        Assert.Null(LightRules.BeamOf(Light(), null, new Vector3(0.5f, 0.5f, 0.5f)));

    [Fact]
    public void A_projector_with_no_texture_id_has_no_beam() =>
        Assert.Null(LightRules.BeamOf(Light(), new PrimProjector(Guid.Empty, MathF.PI / 2, 0f, 0f), new Vector3(0.5f, 0.5f, 0.5f)));

    [Fact]
    public void The_beams_apex_sits_behind_the_prims_face_so_the_face_is_the_pictures_window()
    {
        // a 0.5 m prim, a quarter-turn field of view: the apex is half the prim's height / tan(45 deg) = 0.25 m from the face
        var b = LightRules.BeamOf(Light(10f), new PrimProjector(Pattern, MathF.PI / 2, 0f, 0f), new Vector3(0.5f, 0.5f, 0.5f))!.Value;
        Assert.Equal(0.25f, b.Near, 4);
        Assert.Equal(0f, b.Apex.Z, 4);                 // face at z = -0.25, apex 0.25 behind it
        Assert.Equal(15f + 0.25f - 0.25f, b.Far, 3);   // reach + near - half the prim's depth
        Assert.Equal(1f, b.Aspect, 4);
    }

    [Fact]
    public void A_narrow_field_of_view_puts_the_apex_further_back()
    {
        var wide = LightRules.BeamOf(Light(), new PrimProjector(Pattern, 1.2f, 0f, 0f), new Vector3(1f, 1f, 0.1f))!.Value;
        var narrow = LightRules.BeamOf(Light(), new PrimProjector(Pattern, 0.4f, 0f, 0f), new Vector3(1f, 1f, 0.1f))!.Value;
        Assert.True(narrow.Near > wide.Near);
        Assert.Equal(0.5f / MathF.Tan(0.2f), narrow.Near, 3);
    }

    [Fact]
    public void The_aspect_is_the_prims_x_over_its_y() =>
        Assert.Equal(2f, LightRules.BeamOf(Light(), new PrimProjector(Pattern, 1f, 0f, 0f), new Vector3(2f, 1f, 0.1f))!.Value.Aspect, 4);

    [Fact]
    public void A_point_light_becomes_an_omni_light_of_the_reach()
    {
        var e = LightRules.ForEngine(Light(10f, 0f), null, new Vector3(0.5f, 0.5f, 0.5f))!.Value;
        Assert.False(e.IsSpot);
        Assert.True(e.Range > 14f && e.Range <= 15f, "the engine's range ends at the reach or just inside it");
        Assert.True(e.Energy > 0f);
        Assert.Equal(1f, e.ColourSrgb.X, 4);      // colour normalised to its brightest channel
        Assert.Equal(LightRules.LinearToSrgb(0.5f), e.ColourSrgb.Y, 4);
        Assert.Null(e.Beam);
    }

    [Fact]
    public void Falloff_dims_the_light_not_the_range()
    {
        var plain = LightRules.ForEngine(Light(10f, 0f), null, Vector3.One)!.Value;
        var soft = LightRules.ForEngine(Light(10f, 1f), null, Vector3.One)!.Value;
        Assert.Equal(plain.Range, soft.Range, 4);
        Assert.Equal(plain.Energy / (1.5f * 1.5f), soft.Energy, 3);   // 2 / (1 + 0.5)^2 against 2
    }

    [Fact]
    public void A_projector_becomes_a_spot_light_whose_cone_holds_the_pictures_corners()
    {
        // square picture, quarter-turn field of view: half-width tan(45 deg) = 1 at unit distance; its corner is at sqrt(2)
        var e = LightRules.ForEngine(Light(10f), new PrimProjector(Pattern, MathF.PI / 2, 0f, 0f), new Vector3(0.5f, 0.5f, 0.5f))!.Value;
        Assert.True(e.IsSpot);
        Assert.Equal(MathF.Atan(MathF.Sqrt(2f)), e.SpotHalfAngle, 4);
        Assert.NotNull(e.Beam);
    }

    [Fact]
    public void A_wide_picture_widens_the_cone()
    {
        var sq = LightRules.ForEngine(Light(), new PrimProjector(Pattern, 1f, 0f, 0f), new Vector3(1f, 1f, 0.1f))!.Value;
        var wide = LightRules.ForEngine(Light(), new PrimProjector(Pattern, 1f, 0f, 0f), new Vector3(2f, 1f, 0.1f))!.Value;
        Assert.True(wide.SpotHalfAngle > sq.SpotHalfAngle);
        Assert.Equal(MathF.Atan(MathF.Tan(0.5f) * MathF.Sqrt(5f)), wide.SpotHalfAngle, 4);
    }

    [Fact]
    public void A_very_wide_field_of_view_is_held_to_what_the_engine_can_take()
    {
        var e = LightRules.ForEngine(Light(), new PrimProjector(Pattern, 3.1f, 0f, 0f), new Vector3(1f, 1f, 0.1f))!.Value;
        Assert.Equal(LightRules.MaxHalfAngle, e.SpotHalfAngle, 5);
    }

    [Fact]
    public void A_light_with_no_colour_or_no_radius_is_not_made()
    {
        Assert.Null(LightRules.ForEngine(new PrimLight(Vector3.Zero, 1f, 10f, 0f, 0f), null, Vector3.One));
        Assert.Null(LightRules.ForEngine(Light(0f), null, Vector3.One));
        Assert.Null(LightRules.ForEngine(Light(intensity: 0f), null, Vector3.One));
    }

    [Fact]
    public void Linear_to_sRGB_is_the_standard_curve()
    {
        Assert.Equal(0f, LightRules.LinearToSrgb(0f), 5);
        Assert.Equal(1f, LightRules.LinearToSrgb(1f), 5);
        Assert.Equal(0.7353569f, LightRules.LinearToSrgb(0.5f), 4);
        Assert.Equal(0.0031308f * 12.92f, LightRules.LinearToSrgb(0.0031308f), 5);
    }
}

public class ProjectorAdapterTests
{
    private static byte[] LightBytes(byte r, byte g, byte b, byte a, float radius, float cutoff, float falloff)
    {
        var bytes = new byte[16];
        bytes[0] = r; bytes[1] = g; bytes[2] = b; bytes[3] = a;
        BitConverter.GetBytes(radius).CopyTo(bytes, 4);
        BitConverter.GetBytes(cutoff).CopyTo(bytes, 8);
        BitConverter.GetBytes(falloff).CopyTo(bytes, 12);
        return bytes;
    }

    private static byte[] ImageBytes(LibreMetaverse.UUID id, float fov, float focus, float ambiance)
    {
        var bytes = new byte[28];
        id.ToBytes(bytes, 0);
        BitConverter.GetBytes(fov).CopyTo(bytes, 16);
        BitConverter.GetBytes(focus).CopyTo(bytes, 20);
        BitConverter.GetBytes(ambiance).CopyTo(bytes, 24);
        return bytes;
    }

    [Fact]
    public void A_light_block_translates_to_the_cores_light()
    {
        var p = new LibreMetaverse.Primitive { LocalID = 7, PrimData = new LibreMetaverse.Primitive.ConstructionData() };
        p.Light = new LibreMetaverse.Primitive.LightData(LightBytes(255, 128, 0, 204, 8f, 0.5f, 0.25f), 0);
        var l = Protocol.LmvTransport.ToPrimData(p).Light!.Value;
        Assert.Equal(1f, l.Color.X, 3);
        Assert.Equal(128f / 255f, l.Color.Y, 3);
        Assert.Equal(0f, l.Color.Z, 3);
        Assert.Equal(204f / 255f, l.Intensity, 3);
        Assert.Equal(8f, l.Radius, 4);
        Assert.Equal(0.25f, l.Falloff, 4);
        Assert.Equal(0.5f, l.Cutoff, 4);
    }

    [Fact]
    public void A_prim_with_no_light_block_has_no_light_and_no_projector()
    {
        var p = new LibreMetaverse.Primitive { LocalID = 7, PrimData = new LibreMetaverse.Primitive.ConstructionData() };
        var d = Protocol.LmvTransport.ToPrimData(p);
        Assert.Null(d.Light);
        Assert.Null(d.Projector);
    }

    [Fact]
    public void A_light_image_block_translates_to_the_cores_projector_on_a_prim_with_a_light()
    {
        var tex = LibreMetaverse.UUID.Random();
        var p = new LibreMetaverse.Primitive { LocalID = 7, PrimData = new LibreMetaverse.Primitive.ConstructionData() };
        p.Light = new LibreMetaverse.Primitive.LightData(LightBytes(255, 255, 255, 255, 8f, 0f, 0f), 0);
        p.LightMap = new LibreMetaverse.Primitive.LightImage(ImageBytes(tex, 1.25f, 3f, 0.4f), 0);
        var pr = Protocol.LmvTransport.ToPrimData(p).Projector!.Value;
        Assert.Equal(tex.Guid, pr.Texture);
        Assert.Equal(1.25f, pr.Fov, 4);
        Assert.Equal(3f, pr.Focus, 4);
        Assert.Equal(0.4f, pr.Ambiance, 4);
    }

    [Fact]
    public void A_light_image_with_no_texture_or_no_light_is_no_projector()
    {
        var noTexture = new LibreMetaverse.Primitive { LocalID = 7, PrimData = new LibreMetaverse.Primitive.ConstructionData() };
        noTexture.Light = new LibreMetaverse.Primitive.LightData(LightBytes(255, 255, 255, 255, 8f, 0f, 0f), 0);
        noTexture.LightMap = new LibreMetaverse.Primitive.LightImage(ImageBytes(LibreMetaverse.UUID.Zero, 1f, 0f, 0f), 0);
        Assert.Null(Protocol.LmvTransport.ToPrimData(noTexture).Projector);
        var noLight = new LibreMetaverse.Primitive { LocalID = 7, PrimData = new LibreMetaverse.Primitive.ConstructionData() };
        noLight.LightMap = new LibreMetaverse.Primitive.LightImage(ImageBytes(LibreMetaverse.UUID.Random(), 1f, 0f, 0f), 0);
        Assert.Null(Protocol.LmvTransport.ToPrimData(noLight).Projector);
    }

    [Fact]
    public void The_world_state_carries_a_prims_light_and_projector_and_a_capture_round_trips_them()
    {
        var light = new PrimLight(new Vector3(1f, 0.5f, 0.25f), 0.8f, 6f, 0.3f, 0.1f);
        var proj = new PrimProjector(Guid.NewGuid(), 1.1f, 2f, 0.5f);
        var prim = new PrimData(5, Guid.NewGuid(), 0, 0, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One, Light: light, Projector: proj);
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.ApplyObject(new object(), prim);
        var o = w.Object(new ObjectId(w.Epoch, 5))!;
        Assert.Equal(light, o.Light);
        Assert.Equal(proj, o.Projector);
        var back = Replay.CaptureFile.RoundTripPrim(prim);
        Assert.Equal(light, back.Light);
        Assert.Equal(proj, back.Projector);
        Assert.Null(Replay.CaptureFile.RoundTripPrim(prim with { Light = null, Projector = null }).Light);
    }
}

/// <summary>The engine's own distance curve (a window times an inverse power of the distance) stands in for the viewer's
/// (1 - d / reach) squared: the engine light's numbers must follow it closely over the whole reach.</summary>
public class EngineLightCurveTests
{
    // the engine's attenuation: ((1 - (d / range)^4) clamped at 0) squared, over d to the exponent
    private static float EngineCurve(EngineLight e, float d)
    {
        float w = MathF.Max(0f, 1f - MathF.Pow(d / e.Range, 4f));
        return e.Energy * w * w * MathF.Pow(MathF.Max(d, 1e-4f), -e.Attenuation);
    }

    [Theory]
    [InlineData(3f, 0f)]
    [InlineData(10f, 0f)]
    [InlineData(10f, 1f)]
    [InlineData(25f, 0.5f)]
    public void A_point_light_follows_the_viewers_curve_within_a_third_over_the_reach(float radius, float falloff)
    {
        var light = new PrimLight(new Vector3(1f, 1f, 1f), 1f, radius, falloff, 0f);
        var e = LightRules.ForEngine(light, null, Vector3.One)!.Value;
        float reach = LightRules.Reach(light);
        for (float u = 0.1f; u <= 0.9f; u += 0.1f)
        {
            float want = LightRules.Attenuation(light, u * reach);
            float got = EngineCurve(e, u * reach);
            Assert.InRange(got / want, 0.67f, 1.5f);
        }
        Assert.Equal(0f, EngineCurve(e, reach * 1.05f), 4);   // dark beyond the reach
    }

    [Fact]
    public void A_projector_follows_the_viewers_curve_along_its_axis_measured_from_the_apex()
    {
        var light = new PrimLight(new Vector3(1f, 1f, 1f), 1f, 12f, 0f, 0f);
        var scale = new Vector3(0.5f, 0.5f, 0.5f);
        var e = LightRules.ForEngine(light, new PrimProjector(Guid.NewGuid(), 1f, 0f, 0f), scale)!.Value;
        var beam = e.Beam!.Value;
        float reach = LightRules.Reach(light);
        for (float u = 0.1f; u <= 0.9f; u += 0.1f)
        {
            float onAxis = u * reach;                            // metres from the prim's centre along the axis
            float fromApex = onAxis + (beam.Near - scale.Z * 0.5f);
            Assert.InRange(EngineCurve(e, fromApex) / LightRules.Attenuation(light, onAxis), 0.67f, 1.5f);
        }
    }
}

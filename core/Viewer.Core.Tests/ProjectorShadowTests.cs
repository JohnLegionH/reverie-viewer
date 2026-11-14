// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Which projectors cast shadows (the official viewer gives two projectors a shadow map at a time, the two with the largest area on
/// screen), and the projector scene's occluder and the probes inside and beside its shadow (Docs/PROJECTOR-SPEC.md).</summary>
public class ProjectorShadowTests
{
    private static readonly Vector3 East = new(1, 0, 0);

    private static (uint, Vector3, Vector3, float) Lamp(uint id, float x, float y, float radius = 20f) => (id, new Vector3(x, y, 35f), East, radius);

    [Fact]
    public void At_most_two_projectors_cast_shadows_as_in_the_official_viewer()
    {
        Assert.Equal(2, LightRules.MaxShadowedProjectors);
    }

    [Fact]
    public void Fewer_projectors_than_the_limit_all_cast()
    {
        Assert.Equal(new uint[] { 1 }, LightRules.ShadowCasters([Lamp(1, 64, 100)], Vector3.Zero, East, 2).OrderBy(x => x));
        Assert.Empty(LightRules.ShadowCasters([], Vector3.Zero, East, 2));
    }

    [Fact]
    public void The_two_with_the_largest_area_on_screen_cast()
    {
        // the same radius: the two nearest the eye are the largest on screen
        var lamps = new[] { Lamp(1, 90, 0), Lamp(2, 40, 0), Lamp(3, 70, 0), Lamp(4, 55, 0) };
        Assert.Equal(new uint[] { 2, 4 }, LightRules.ShadowCasters(lamps, Vector3.Zero, East, 2).OrderBy(x => x));
        // a larger light at the same place outranks a smaller one
        var sizes = new[] { Lamp(5, 60, 0, 5f), Lamp(6, 60, 0, 40f), Lamp(7, 60, 0, 10f) };
        Assert.Equal(new uint[] { 6, 7 }, LightRules.ShadowCasters(sizes, Vector3.Zero, East, 2).OrderBy(x => x));
    }

    [Fact]
    public void A_light_close_to_the_eye_is_ranked_by_the_official_viewers_ramp_not_by_raw_distance()
    {
        // within 16 m the distance is shrunk (dist^2 / 16), so a light right at the eye ranks above one 10 m away with the same radius
        var near = SpotArea(0.5f);
        var mid = SpotArea(10f);
        Assert.True(near > mid);
        // and past 16 m the area falls with distance
        Assert.True(SpotArea(20f) > SpotArea(40f));
    }

    private static float SpotArea(float distance) => LightRules.SpotLightPriority(new Vector3(distance, 0, 0), new Vector3(0, 0, -1), 5f, Vector3.Zero, East);

    [Fact]
    public void Ties_go_to_the_lower_id_so_the_choice_is_stable()
    {
        var lamps = new[] { Lamp(9, 60, 0), Lamp(3, 60, 0), Lamp(5, 60, 0) };
        Assert.Equal(new uint[] { 3, 5 }, LightRules.ShadowCasters(lamps, Vector3.Zero, East, 2).OrderBy(x => x));
    }

    [Fact]
    public void The_scene_has_an_occluder_and_the_lamp_whose_beam_holds_it_is_one_of_the_two_that_cast_from_the_cameras_place()
    {
        var prims = ProjectorZoo.Make();
        Assert.Contains(prims, p => p.LocalId == ProjectorZoo.ShadowBlockId);
        var lamps = Enumerable.Range(0, ProjectorZoo.Lamps.Length).Select(i => prims.First(p => p.LocalId == ProjectorZoo.LampId(i)))
            .Select(p => (p.LocalId, p.Position, Vector3.Transform(new Vector3(0, 0, -1), p.Rotation), p.Light!.Value.Radius)).ToList();
        var casters = LightRules.ShadowCasters(lamps, ProjectorZoo.Eye, East, LightRules.MaxShadowedProjectors);
        Assert.Equal(2, casters.Count);
        Assert.Contains(ProjectorZoo.LampId(ProjectorZoo.ShadowLamp), casters);
    }

    [Fact]
    public void The_shadow_probe_is_in_the_beam_and_behind_the_occluder_and_the_one_beside_it_is_in_the_beam_and_clear()
    {
        var block = ProjectorZoo.Make().First(p => p.LocalId == ProjectorZoo.ShadowBlockId);
        var lamp = ProjectorZoo.LampPosition(ProjectorZoo.ShadowLamp);
        var probes = ProjectorZoo.ShadowProbes();
        Assert.Equal(2, probes.Count);
        foreach (var p in probes)
        {
            var (inside, edge) = ProjectorZoo.Window(ProjectorZoo.ShadowLamp, p.Point);
            Assert.True(inside && edge < 0.9f, $"{p.Name} lies well inside the beam (edge {edge})");
            Assert.Equal(p.Shadowed, SegmentHitsBox(lamp, p.Point, block.Position, block.Scale));
        }
        Assert.Contains(probes, p => p.Shadowed);
        Assert.Contains(probes, p => !p.Shadowed);
    }

    [Fact]
    public void The_occluder_does_not_stand_in_front_of_any_of_the_other_probes_or_between_the_camera_and_them()
    {
        var block = ProjectorZoo.Make().First(p => p.LocalId == ProjectorZoo.ShadowBlockId);
        foreach (var p in ProjectorZoo.Probes())
        {
            Assert.False(SegmentHitsBox(ProjectorZoo.LampPosition(p.Lamp), p.Point, block.Position, block.Scale, 0.4f), p.Name + " would be in the occluder's shadow");
            Assert.False(SegmentHitsBox(ProjectorZoo.Eye, p.Point, block.Position, block.Scale, 0.4f), p.Name + " would be hidden behind the occluder");
        }
    }

    /// <summary>Whether the segment from a to b passes through the axis-aligned box centred c of size s (grown by pad each side).</summary>
    private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 c, Vector3 s, float pad = 0f)
    {
        var lo = c - s / 2 - new Vector3(pad); var hi = c + s / 2 + new Vector3(pad);
        float t0 = 0, t1 = 1;
        for (int k = 0; k < 3; k++)
        {
            float o = k == 0 ? a.X : k == 1 ? a.Y : a.Z, d = (k == 0 ? b.X : k == 1 ? b.Y : b.Z) - o;
            float l = k == 0 ? lo.X : k == 1 ? lo.Y : lo.Z, h = k == 0 ? hi.X : k == 1 ? hi.Y : hi.Z;
            if (MathF.Abs(d) < 1e-6f) { if (o < l || o > h) return false; continue; }
            float ta = (l - o) / d, tb = (h - o) / d;
            t0 = MathF.Max(t0, MathF.Min(ta, tb)); t1 = MathF.Min(t1, MathF.Max(ta, tb));
            if (t0 > t1) return false;
        }
        return true;
    }
}

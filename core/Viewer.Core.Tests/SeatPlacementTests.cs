// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Sitting on an object as the official viewer does it: the offset a sit request carries (llagentcamera.cpp calcFocusOffset, sent by
/// llviewermenu.cpp handle_object_sit), where a seated avatar's soles go (llvoavatar.cpp updateCharacter), which sit animation plays, and
/// the one log line written per sit. Also the Mono setting for sounds placed in the world, and no wind while falling.
/// </summary>
public class SeatPlacementTests
{
    private static void Near(float expected, float actual, float tol = 1e-3f) =>
        Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    private static Vector3 At(Vector3 camera, Vector3 hit) => Vector3.Normalize(hit - camera);

    [Fact]
    public void A_click_on_a_box_seen_from_afar_is_taken_to_the_plane_through_its_centre_that_faces_the_camera()
    {
        // a 1 m box at (10, 10, 1), seen from 5 m west and 3 m up: the plane through its centre facing the camera is x = 10; the click on
        // the top lands there 0.396 m above the centre, inside the box, so the region seats the avatar lower than the top surface
        var box = new Vector3(10, 10, 1);
        var camera = new Vector3(5, 10, 4);
        var hit = new Vector3(9.8f, 10, 1.5f);
        var offset = SeatPlacement.SitOffset(box, Quaternion.Identity, Vector3.One, camera, At(camera, hit), hit);
        Near(0f, offset.X);
        Near(0f, offset.Y);
        Near(4f - 2.5f * (5f / 4.8f) - 1f, offset.Z);
    }

    [Fact]
    public void A_click_from_close_by_is_pulled_part_way_towards_the_surface_point()
    {
        // camera 1.1 m from the click: the plane is the box's level one (z = 1) and the point is moved 56.25 % of the way to the surface
        var box = new Vector3(10, 10, 1);
        var camera = new Vector3(9.0f, 10, 2.3f);
        var hit = new Vector3(9.8f, 10, 1.5f);
        var offset = SeatPlacement.SitOffset(box, Quaternion.Identity, Vector3.One, camera, At(camera, hit), hit);
        Near(0.3f + (-0.2f - 0.3f) * 0.5625f, offset.X);
        Near(0f, offset.Y);
        Near(0.5f * 0.5625f, offset.Z);
    }

    [Fact]
    public void The_offset_is_in_region_axes_not_the_objects_own()
    {
        // a large turned box with the camera inside it: the offset is the surface point itself, region axes (east, north, up)
        var box = new Vector3(10, 10, 5);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var camera = new Vector3(11, 10, 6);
        var hit = new Vector3(12, 10.5f, 7);
        var offset = SeatPlacement.SitOffset(box, turned, new Vector3(10, 10, 10), camera, At(camera, hit), hit);
        Near(2f, offset.X);
        Near(0.5f, offset.Y);
        Near(2f, offset.Z);
    }

    // OpenSim without a sit target and without the physics engine's own sit (the plain fallback, ScenePresence.SendSitResponse): the avatar's
    // centre goes half its physics box above the requested point, and the box is the stated height plus 0.2 m (AvatarAppearance AVBOXAJUST)
    private static float RegionCentreAbove(float regionHeight) => (regionHeight + 0.2f) / 2;

    [Theory]
    [InlineData(1.43f)]
    [InlineData(1.67f)]
    [InlineData(2.085f)]
    public void A_seated_avatar_on_a_box_without_a_sit_target_has_its_root_at_the_place_the_region_states_whatever_its_height(float height)
    {
        // the region keeps the avatar's own height; the requested point is the box's top (0.5 m above its centre at z = 20). The official
        // seated branch puts the root at the stated place and adds nothing for the body's size: no half-body drop, no pelvis-to-foot
        float point = 20.5f;
        var place = new Vector3(3, 4, point + RegionCentreAbove(height));
        var root = SeatPlacement.SeatedRoot(place, Quaternion.Identity, hover: 0f);
        Near(place.X, root.X);
        Near(place.Y, root.Y);
        Near(point + RegionCentreAbove(height), root.Z);
    }

    [Fact]
    public void Seated_every_avatar_keeps_its_hover_offset_as_the_official_seated_branch_adds_it()
    {
        // the hover offset (the own avatar's hover setting, another's stated hover) is added seated too, turned with the avatar
        Near(20.25f, SeatPlacement.SeatedRoot(new Vector3(0, 0, 20f), Quaternion.Identity, hover: 0.25f).Z);
        Near(19.75f, SeatPlacement.SeatedRoot(new Vector3(0, 0, 20f), Quaternion.Identity, hover: -0.25f).Z);
    }

    [Fact]
    public void A_sit_animation_is_the_default_one_an_override_or_another()
    {
        Assert.Equal("default", SeatPlacement.SitKind(Guid.Parse("1a5fe8ac-a804-8a5d-7cbd-56bd83184568"), startedByAo: false));
        Assert.Equal("default", SeatPlacement.SitKind(Guid.Parse("245f3c54-f1c0-bf2e-811f-46d8eeb386e7"), startedByAo: false));
        Assert.Equal("default", SeatPlacement.SitKind(Guid.Parse("b1709c8d-ecd3-54a1-4f28-d55ac0840782"), startedByAo: false));
        Assert.Equal("ao", SeatPlacement.SitKind(Guid.Parse("0f0e0d0c-1111-4222-8333-944444444444"), startedByAo: true));
        Assert.Equal("other", SeatPlacement.SitKind(Guid.Parse("0f0e0d0c-1111-4222-8333-944444444444"), startedByAo: false));
        Assert.Equal("none", SeatPlacement.SitKind(Guid.Empty, startedByAo: false));
    }

    [Fact]
    public void The_sit_line_carries_every_field()
    {
        var pose = new SeatedPose(Root: new(0, 0, 21.7f), Pelvis: new(0, 0, 21.6f), HipLeft: new(0, 0.1f, 21.5f), HipRight: new(0, -0.1f, 21.5f),
            KneeLeft: new(0.5f, 0.1f, 21.4f), KneeRight: new(0.5f, -0.1f, 21.4f), SoleLeft: new(0.6f, 0.1f, 20.9f), SoleRight: new(0.6f, -0.1f, 20.7f));
        var r = new SitReport(SitTarget: false, SentOffset: new(0.1f, 0.2f, 0.3f), SeatPosition: new(0, 0, 1.2f), SeatRotation: Quaternion.Identity,
            PrimTop: 20.5f, Pose: pose, BodyHeight: 2.085f, Animation: "sit", AnimationKind: "default", AnimationPriority: 3);
        var f = r.Fields().ToDictionary(x => x.Key, x => x.Value);
        string[] keys = ["sit_target", "sent_x", "sent_y", "sent_z", "seat_x", "seat_y", "seat_z", "seat_yaw_deg", "prim_top", "root_over_top",
            "pelvis_over_top", "knee_over_top", "sole_over_top", "thigh_deg", "body_height", "animation", "animation_kind", "animation_priority"];
        foreach (var k in keys) Assert.True(f.ContainsKey(k), $"missing {k}");
        foreach (var gone in new[] { "soles_over_top", "drop_half_body", "region_box_extra", "hover_kept", "hover_dropped", "pelvis_to_foot", "region_height" })
            Assert.False(f.ContainsKey(gone), $"estimate field {gone} still written");
        Assert.Equal(false, f["sit_target"]);
        Assert.Equal(1.2f, (float)f["root_over_top"]!, 3);
        Assert.Equal(1.1f, (float)f["pelvis_over_top"]!, 3);
        Assert.Equal(0.9f, (float)f["knee_over_top"]!, 3);   // the two knees' mean
        Assert.Equal(0.3f, (float)f["sole_over_top"]!, 3);   // the two soles' mean
        Assert.Equal(11.31f, (float)f["thigh_deg"]!, 1);     // 0.1 down over 0.5 forward
        Assert.Equal("default", f["animation_kind"]);
        Assert.Equal(3, f["animation_priority"]);
    }

    [Fact]
    public void Mono_takes_the_left_right_placement_off_every_sound_in_the_world()
    {
        var s = new SettingsService(null);
        Assert.Equal(1f, AudioLevels.PanningStrength(s));
        s.Set(AudioLevels.Mono, true);
        Assert.Equal(0f, AudioLevels.PanningStrength(s));
    }

    [Fact]
    public void Falling_fast_is_strong_wind_but_silent_because_the_official_viewer_mutes_the_wind_unless_flying()
    {
        // falling at 30 m/s: the wind felt is at full gain, but the loudest the wind may be fades to nothing when not flying
        var felt = WindSound.Relative(Vector3.Zero, new Vector3(0, 0, -30), Quaternion.Identity);
        Assert.Equal(1f, WindSound.Gain(felt));
        float max = 0.5f;
        for (int i = 0; i < 4; i++) max = WindSound.StepMaxGain(max, flying: false, master: 1f, ambient: 0.5f, seconds: 0.5f);
        Assert.Equal(0f, max * WindSound.Gain(felt));
        Assert.True(WindSound.StepMaxGain(0f, flying: true, master: 1f, ambient: 0.5f, seconds: 0.5f) > 0f);
    }
}

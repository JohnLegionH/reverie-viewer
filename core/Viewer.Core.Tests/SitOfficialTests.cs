// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Sitting on an object as the official viewer does it, end to end:
/// <list type="bullet">
/// <item>the point a sit request carries (llagentcamera.cpp calcFocusOffset on the pick, sent by llviewermenu.cpp handle_object_sit): the
/// click projected onto the plane through the object's centre whose normal is the object axis most facing the camera, pulled back into the
/// object's box along the camera's ray, then moved towards the clicked surface by how deep that surface lies along the view;</item>
/// <item>where the seated avatar's root goes (llvoavatar.cpp updateRootPositionAndRotation, the branch for an avatar sitting on an object):
/// the place the region states for it relative to the seat plus the hover offset turned by the avatar's rotation, nothing else;</item>
/// <item>the sit animation's whole pose on the skeleton (its hip, knee and ankle rotations and its pelvis position, at its priority, above
/// the stand), with the body noise stopped while seated (llvoavatar.cpp sitOnObject stops it, getOffObject starts it again);</item>
/// <item>the one avatar.sit line, with the heights measured from the posed skeleton.</item>
/// </list>
/// Expected values are worked by hand in the comments, or, for the turned box, by <see cref="Reference"/>, written here from the
/// description of the behaviour and not from the code under test. Pure, runs in parallel.
/// </summary>
public class SitOfficialTests
{
    private static void Near(float expected, float actual, float tol = 1e-3f, string what = "") =>
        Assert.True(MathF.Abs(expected - actual) <= tol, $"{what} expected {expected}, got {actual}");

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-3f, string what = "")
    {
        Near(expected.X, actual.X, tol, what + ".x");
        Near(expected.Y, actual.Y, tol, what + ".y");
        Near(expected.Z, actual.Z, tol, what + ".z");
    }

    // ---------------- the sit request's point ----------------

    // a 0.5 m box standing on the ground: centre (20, 20, 0.25), top face at z = 0.5. The camera is behind and above an avatar that
    // faces the box from the west, looking at the box's centre
    private static readonly Vector3 Box = new(20, 20, 0.25f), Cube = new(0.5f);
    private static readonly Vector3 Behind = new(17.5f, 20, 1.5f);
    private static Vector3 LookAt(Vector3 camera, Vector3 target) => Vector3.Normalize(target - camera);

    [Fact]
    public void A_click_on_the_near_half_of_the_top_gives_a_point_inside_the_box()
    {
        // the camera's direction to the centre, (2.5, 0, -1.25), is mostly along x: the plane is x = 20. The ray to (19.85, 20, 0.5) runs
        // (2.35, 0, -1) and meets it at z = 1.5 - 2.5 / 2.35 = 0.43617, 0.18617 above the centre: inside. The surface point lies
        // 0.246 m deep along the view at 2.554 m, a ratio under 0.1, so it is not moved towards the surface
        var hit = new Vector3(19.85f, 20, 0.5f);
        var o = SeatPlacement.SitOffset(Box, Quaternion.Identity, Cube, Behind, LookAt(Behind, Box), hit);
        Near(new Vector3(0, 0, 1.5f - 2.5f / 2.35f - 0.25f), o);
    }

    [Fact]
    public void A_click_on_the_centre_of_the_top_gives_the_top_face_over_the_centre()
    {
        // the ray to (20, 20, 0.5) meets x = 20 at the hit itself: 0.25 above the centre, on the top face
        var hit = new Vector3(20, 20, 0.5f);
        var o = SeatPlacement.SitOffset(Box, Quaternion.Identity, Cube, Behind, LookAt(Behind, Box), hit);
        Near(new Vector3(0, 0, 0.25f), o);
    }

    [Fact]
    public void A_click_on_the_far_half_of_the_top_is_pulled_back_along_the_ray_onto_the_top_face()
    {
        // the ray to (20.15, 20, 0.5) runs (2.65, 0, -1) and meets x = 20 at z = 1.5 - 2.5 / 2.65 = 0.55660: 0.30660 above the centre,
        // 0.05660 over the top. Pulled back along the camera's ray by 0.0566 / (2.5 / 2.65) of its length: x moves on by 0.15, z comes
        // down to the top face
        var hit = new Vector3(20.15f, 20, 0.5f);
        var o = SeatPlacement.SitOffset(Box, Quaternion.Identity, Cube, Behind, LookAt(Behind, Box), hit);
        Near(new Vector3(0.15f, 0, 0.25f), o);
    }

    [Fact]
    public void A_click_on_the_side_facing_the_camera_gives_the_point_on_the_central_plane_behind_it()
    {
        // the west face at (19.75, 20.1, 0.3): the ray (2.25, 0.1, -1.2) meets x = 20 at 2.5 / 2.25 of its length: y = 20.11111,
        // z = 1.5 - 1.33333 = 0.16667, inside the box; 0.246 m deep at 2.552 m, no move towards the surface
        var hit = new Vector3(19.75f, 20.1f, 0.3f);
        var o = SeatPlacement.SitOffset(Box, Quaternion.Identity, Cube, Behind, LookAt(Behind, Box), hit);
        Near(new Vector3(0, 0.1f / 0.9f, 1.5f - 1.2f / 0.9f - 0.25f), o);
    }

    [Fact]
    public void A_steep_top_down_camera_takes_the_level_plane_through_the_centre()
    {
        // the camera 3.75 m straight above the centre: the plane is z = 0.25. The ray to (20.1, 20.05, 0.5) runs (0.1, 0.05, -3.5) and
        // meets it at 3.75 / 3.5 of its length
        var camera = new Vector3(20, 20, 4);
        var hit = new Vector3(20.1f, 20.05f, 0.5f);
        var o = SeatPlacement.SitOffset(Box, Quaternion.Identity, Cube, camera, LookAt(camera, Box), hit);
        Near(new Vector3(0.1f * 3.75f / 3.5f, 0.05f * 3.75f / 3.5f, 0), o);
    }

    [Theory]
    [InlineData(19.95f, 20.0f)]
    [InlineData(20.1f, 20.15f)]
    [InlineData(20.3f, 19.9f)]
    public void A_turned_box_gives_the_point_the_official_formula_gives(float x, float y)
    {
        // a 1 x 0.5 x 0.5 box turned 45 degrees about up and 10 about its own x; the expected point is the reference's
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 18);
        var scale = new Vector3(1f, 0.5f, 0.5f);
        var centre = new Vector3(20, 20, 0.4f);
        var camera = new Vector3(17.2f, 18.6f, 2.1f);
        // the clicked point on the box's top: where the camera's ray towards (x, y) on a level just above the centre leaves the box's +z face
        var hit = TopFaceHit(centre, turned, scale, camera, new Vector3(x, y, 0.6f));
        var at = LookAt(camera, centre);
        Near(Reference(centre, turned, scale, camera, at, hit), SeatPlacement.SitOffset(centre, turned, scale, camera, at, hit), 2e-3f);
    }

    /// <summary>The ray from <paramref name="camera"/> through <paramref name="through"/> meets the plane of the box's own +z face.</summary>
    private static Vector3 TopFaceHit(Vector3 centre, Quaternion rotation, Vector3 scale, Vector3 camera, Vector3 through)
    {
        var up = Vector3.Transform(Vector3.UnitZ, rotation);
        var facePoint = centre + up * scale.Z / 2;
        var dir = Vector3.Normalize(through - camera);
        float s = Vector3.Dot(facePoint - camera, up) / Vector3.Dot(dir, up);
        return camera + dir * s;
    }

    /// <summary>
    /// The official sit point, written from the description: in the box's own frame (a matrix), pick the axis whose share of the
    /// camera-to-centre direction, divided by the box's size along it, is largest (x, then y, then z on ties); intersect the camera's ray
    /// through the clicked point with the plane through the centre normal to that axis; if the point lies outside the box, slide it back
    /// towards the camera along the camera-to-point line by the largest fraction any axis needs; back to region axes; then, with the camera
    /// outside the box, blend it towards the clicked surface point by clamp((depth / distance - 0.1) / 0.6, 0, 1), where depth is the
    /// surface point's offset along the view direction and distance the camera's distance to it.
    /// </summary>
    private static Vector3 Reference(Vector3 centre, Quaternion rotation, Vector3 scale, Vector3 camera, Vector3 at, Vector3 hit)
    {
        var toLocal = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation));
        var toRegion = Matrix4x4.CreateFromQuaternion(rotation);
        var d = Vector3.TransformNormal(Vector3.Normalize(centre - camera), toLocal);
        float[] share = [MathF.Abs(d.X / scale.X), MathF.Abs(d.Y / scale.Y), MathF.Abs(d.Z / scale.Z)];
        int axis = share[0] > share[1] && share[0] > share[2] ? 0 : share[1] > share[2] ? 1 : 2;
        var n = Vector3.TransformNormal(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, toRegion);
        var ray = Vector3.Normalize(hit - camera);
        var p = camera + ray * (Vector3.Dot(centre - camera, n) / Vector3.Dot(ray, n));
        var local = Vector3.TransformNormal(p - centre, toLocal);
        var back = Vector3.TransformNormal(p - camera, toLocal);
        float best = 0, bestAbs = -1;
        for (int i = 0; i < 3; i++)
        {
            float v = i == 0 ? local.X : i == 1 ? local.Y : local.Z, half = (i == 0 ? scale.X : i == 1 ? scale.Y : scale.Z) / 2;
            float b = i == 0 ? back.X : i == 1 ? back.Y : back.Z;
            float over = v > 0 ? MathF.Max(0, v - half) : MathF.Min(0, v + half);
            float f = MathF.Abs(b) < 1e-4f ? 0 : over / b;
            if (MathF.Abs(f) > bestAbs) { bestAbs = MathF.Abs(f); best = f; }
        }
        var inside = Vector3.TransformNormal(local - best * back, toRegion);
        var surface = hit - centre;
        float ratio = MathF.Abs(Vector3.Dot(surface, Vector3.Normalize(at))) / Vector3.Distance(hit, camera);
        float bias = Math.Clamp((ratio - 0.1f) / 0.6f, 0, 1);
        return inside + (surface - inside) * bias;
    }

    // ---------------- the seated root ----------------

    // OpenSim without a sit target and without a physics sit (ScenePresence.SendSitResponse's fallback): the avatar's place relative to
    // the box is the sent point plus half its physics box, the stated height plus 0.2 m. A 2.1 m avatar the region keeps at 2.1 m: half of
    // 2.3 m is 1.15. Sent: the top's centre, (0, 0, 0.25) from the box's centre at z = 20.25; so the region states (0, 0, 1.4)
    private const float BoxCentreZ = 20.25f, Top = 20.5f;
    private static readonly Vector3 StatedSeat = new(0, 0, 0.25f + (2.1f + 0.2f) / 2);

    [Fact]
    public void Seated_the_root_is_exactly_the_place_the_region_states()
    {
        var root = SeatPlacement.SeatedRoot(new Vector3(20, 20, BoxCentreZ) + StatedSeat, Quaternion.Identity, hover: 0f);
        Near(new Vector3(20, 20, Top + 1.15f), root);
    }

    [Fact]
    public void Seated_the_hover_offset_is_added_turned_with_the_avatar()
    {
        var place = new Vector3(20, 20, BoxCentreZ) + StatedSeat;
        Near(place + new Vector3(0, 0, 0.2f), SeatPlacement.SeatedRoot(place, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), hover: 0.2f));
        // an avatar seated tipped a quarter turn forward about y: its up points along x, and the hover with it
        Near(place + new Vector3(0.2f, 0, 0), SeatPlacement.SeatedRoot(place, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), hover: 0.2f));
    }

    // ---------------- the sit animation's pose ----------------

    // legs of round lengths: hip 0.1 below and 0.1 beside the pelvis, thigh 0.5, shin 0.45, foot 0.1 forward and 0.05 down (1.1 m pelvis to sole)
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1.1" rot="0 0 0" scale="1 1 1" support="base">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
            <bone name="mHipLeft" pos="0 0.1 -0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mKneeLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mAnkleLeft" pos="0 0 -0.45" rot="0 0 0" scale="1 1 1" support="base">
                  <bone name="mFootLeft" pos="0.1 0 -0.05" rot="0 0 0" scale="1 1 1" support="base"/>
                </bone>
              </bone>
            </bone>
            <bone name="mHipRight" pos="0 -0.1 -0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mKneeRight" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mAnkleRight" pos="0 0 -0.45" rot="0 0 0" scale="1 1 1" support="base">
                  <bone name="mFootRight" pos="0.1 0 -0.05" rot="0 0 0" scale="1 1 1" support="base"/>
                </bone>
              </bone>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """<linden_avatar><attachment_point id="2" group="2" name="Chest" joint="mChest" position="0 0 0" rotation="0 0 0"/></linden_avatar>""";

    private static Quaternion Turn(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);

    // the thigh forward (a quarter turn about y takes -z to +x), the shin back down, the ankle as it was
    private static readonly Quaternion HipKey = Turn(Vector3.UnitY, -90), KneeKey = Turn(Vector3.UnitY, 90), AnkleKey = Turn(Vector3.UnitY, 5);
    private static readonly Vector3 PelvisKey = new(-0.3f, 0, -0.45f);

    private static JointTrack Rot(string joint, int priority, Quaternion from, Quaternion to) => new(joint, priority, [0f, 1f], [from, to], [], []);

    // a sit like the grid's default one: it plays once into the seated pose and holds its last frame (loop in = loop out = its length)
    private static AnimationClip Sit() => new()
    {
        Duration = 1f, Loop = true, LoopIn = 1f, LoopOut = 1f, Priority = 4, EaseIn = 0.3f, EaseOut = 0.5f, Emote = "",
        Tracks =
        [
            new JointTrack("mPelvis", 4, [], [], [0f, 1f], [Vector3.Zero, PelvisKey]),
            Rot("mHipLeft", 4, Quaternion.Identity, HipKey), Rot("mHipRight", 4, Quaternion.Identity, HipKey),
            Rot("mKneeLeft", 4, Quaternion.Identity, KneeKey), Rot("mKneeRight", 4, Quaternion.Identity, KneeKey),
            Rot("mAnkleLeft", 4, Quaternion.Identity, AnkleKey), Rot("mAnkleRight", 4, Quaternion.Identity, AnkleKey),
        ],
    };

    // the stand, at the lowest priority, with legs of its own
    private static AnimationClip Stand() => new()
    {
        Duration = 1.5f, Loop = true, LoopIn = 0f, LoopOut = 1.5f, Priority = 0, EaseIn = 0.3f, EaseOut = 0.5f, Emote = "",
        Tracks =
        [
            new JointTrack("mPelvis", 0, [0f, 1.5f], [Turn(Vector3.UnitZ, 3), Turn(Vector3.UnitZ, 3)], [0f, 1.5f], [new(0, 0, 0.02f), new(0, 0, 0.02f)]),
            Rot("mHipLeft", 0, Turn(Vector3.UnitY, 12), Turn(Vector3.UnitY, 12)), Rot("mHipRight", 0, Turn(Vector3.UnitY, 12), Turn(Vector3.UnitY, 12)),
            Rot("mKneeLeft", 0, Turn(Vector3.UnitY, 4), Turn(Vector3.UnitY, 4)), Rot("mKneeRight", 0, Turn(Vector3.UnitY, 4), Turn(Vector3.UnitY, 4)),
        ],
    };

    private static readonly Guid StandId = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f"), SitId = Guid.Parse("1a5fe8ac-a804-8a5d-7cbd-56bd83184568");

    /// <summary>The skeleton posed by everything a seated avatar plays: the stand and the sit from the region, and the built-in motions
    /// stepped as seated, at <paramref name="at"/> seconds (the sit started at 1 s).</summary>
    private static (AvatarJoints Joints, AvatarRig Rig, AvatarDefaultMotions Defaults, MotionController Motions) Seated(double at)
    {
        var rig = new AvatarRig(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));
        var motions = new MotionController();
        var defaults = new AvatarDefaultMotions(seed: 3);
        defaults.Start(motions, 0);
        var stand = Stand(); var sit = Sit();
        motions.Update(0, [new SignalledMotion(StandId, stand, 1)]);
        for (double t = 0; t <= at + 1e-9; t += 0.1)
        {
            var playing = t >= 1 ? new[] { new SignalledMotion(StandId, stand, 1), new SignalledMotion(SitId, sit, 2) } : [new SignalledMotion(StandId, stand, 1)];
            defaults.Step(motions, t, 0f, 0f, walkSignalled: false, inAir: false, sitting: t >= 1, requestedHandPose: null);
            motions.Update(t, playing);
        }
        var joints = new AvatarJoints(rig);
        joints.Begin(AvatarJoints.TracksFor(rig, motions.Mix), (float)at);
        return (joints, rig, defaults, motions);
    }

    private static Quaternion WorldRotation(AvatarJoints j, int node) =>
        Matrix4x4.Decompose(j.World(node), out _, out var q, out _) ? q : throw new InvalidOperationException("no rotation");

    private static void NearRotation(Quaternion expected, Quaternion actual, string what)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 0.9999f, $"{what}: expected {expected}, got {actual} (|dot| {dot})");
    }

    [Fact]
    public void The_sit_animations_hip_knee_and_ankle_keys_and_pelvis_position_reach_the_posed_skeleton_above_the_stand()
    {
        var (j, rig, _, _) = Seated(at: 6);
        int pelvis = rig.Pelvis;
        foreach (var side in new[] { "Left", "Right" })
        {
            int hip = rig.Find("mHip" + side), knee = rig.Find("mKnee" + side), ankle = rig.Find("mAnkle" + side);
            var pelvisQ = WorldRotation(j, pelvis);
            var hipQ = WorldRotation(j, hip); var kneeQ = WorldRotation(j, knee); var ankleQ = WorldRotation(j, ankle);
            // each joint's own rotation, read back as its world rotation in its parent's
            NearRotation(HipKey, Quaternion.Concatenate(hipQ, Quaternion.Inverse(pelvisQ)), "hip " + side);
            NearRotation(KneeKey, Quaternion.Concatenate(kneeQ, Quaternion.Inverse(hipQ)), "knee " + side);
            NearRotation(AnkleKey, Quaternion.Concatenate(ankleQ, Quaternion.Inverse(kneeQ)), "ankle " + side);
        }
        // the sit's pelvis position wins over the stand's and the pelvis fix's (the sit keys no pelvis turn, so the stand's turn stays, as
        // the official blend keeps a joint's rotation and position apart)
        Near(PelvisKey, j.World(pelvis).Translation, 1e-3f, "pelvis");
    }

    [Fact]
    public void Seated_the_body_noise_is_stopped_and_standing_up_starts_it_again()
    {
        var (_, _, defaults, motions) = Seated(at: 3);
        Assert.False(motions.IsRunning(defaults.BodyNoise.Name, 3), "body noise while seated");
        defaults.Step(motions, 3.1, 0f, 0f, walkSignalled: false, inAir: false, sitting: false, requestedHandPose: null);
        Assert.True(motions.IsRunning(defaults.BodyNoise.Name, 3.1), "body noise after standing");
    }

    [Fact]
    public void The_seated_heights_of_the_posed_skeleton_and_the_sit_line()
    {
        // the root exactly at the seat, then the pose: pelvis 0.45 below it and 0.3 back; hips 0.1 lower; thighs level, 0.5 forward to the
        // knees; shins 0.45 down to the ankles; the feet 0.05 lower (the ankle's 5 degrees turn the foot's 0.1 forward by 0.0087 down)
        var root = SeatPlacement.SeatedRoot(new Vector3(20, 20, BoxCentreZ) + StatedSeat, Quaternion.Identity, hover: 0f);
        Near(Top + 1.15f, root.Z, 1e-4f, "root before the animation");
        var (j, rig, _, _) = Seated(at: 6);
        Vector3 At(string joint) => root + j.World(rig.Find(joint)).Translation;
        var pose = new SeatedPose(root, At("mPelvis"), At("mHipLeft"), At("mHipRight"), At("mKneeLeft"), At("mKneeRight"), At("mFootLeft"), At("mFootRight"));
        Near(Top + 0.70f, pose.Pelvis.Z, 1e-3f, "pelvis");
        Near(Top + 0.60f, pose.KneeLeft.Z, 1e-3f, "knee");
        float sole = 0.15f - 0.05f * MathF.Cos(5 * MathF.PI / 180) - 0.1f * MathF.Sin(5 * MathF.PI / 180);
        Near(Top + sole, pose.SoleLeft.Z, 1e-3f, "sole");
        Near(0f, SitReport.ThighAngleDeg(pose.HipLeft, pose.KneeLeft), 0.05f, "thigh");

        var line = new SitReport(SitTarget: false, SentOffset: new(0, 0, 0.25f), SeatPosition: StatedSeat, SeatRotation: Quaternion.Identity, PrimTop: Top,
            Pose: pose, BodyHeight: 2.1f, Animation: "sit", AnimationKind: "default", AnimationPriority: 4).Fields().ToDictionary(x => x.Key, x => x.Value);
        string[] keys = ["sit_target", "sent_x", "sent_y", "sent_z", "seat_x", "seat_y", "seat_z", "seat_yaw_deg", "prim_top", "root_over_top", "pelvis_over_top",
            "knee_over_top", "sole_over_top", "thigh_deg", "body_height", "animation", "animation_kind", "animation_priority"];
        Assert.Equal(keys, line.Keys.ToArray());
        Near(1.15f, (float)line["root_over_top"]!, 1e-3f, "root_over_top");
        Near(0.70f, (float)line["pelvis_over_top"]!, 1e-3f, "pelvis_over_top");
        Near(0.60f, (float)line["knee_over_top"]!, 1e-3f, "knee_over_top");
        Near(sole, (float)line["sole_over_top"]!, 2e-3f, "sole_over_top");
        Near(0f, (float)line["thigh_deg"]!, 0.1f, "thigh_deg");
        Assert.Equal(1.4f, (float)line["seat_z"]!, 3);
    }

    [Theory]
    [InlineData(0f, 0f, -0.5f, 90f)]       // hanging straight down
    [InlineData(0.5f, 0f, 0f, 0f)]         // level
    [InlineData(0.3f, 0.3f, -0.3f, 35.26f)] // forward and to the side, 0.3 down over 0.424 across
    public void The_thigh_angle_is_measured_from_the_level(float dx, float dy, float dz, float degrees)
    {
        Near(degrees, SitReport.ThighAngleDeg(new Vector3(1, 2, 3), new Vector3(1 + dx, 2 + dy, 3 + dz)), 0.05f);
    }

    [Fact]
    public void Without_a_prim_top_the_heights_over_it_are_left_out()
    {
        var pose = new SeatedPose(new(0, 0, 2), new(0, 0, 1.6f), new(0, 0.1f, 1.5f), new(0, -0.1f, 1.5f), new(0.5f, 0.1f, 1.5f), new(0.5f, -0.1f, 1.5f), new(0.6f, 0.1f, 1f), new(0.6f, -0.1f, 1f));
        var line = new SitReport(false, default, default, Quaternion.Identity, null, pose, 2f, "sit", "default", 4).Fields().ToDictionary(x => x.Key, x => x.Value);
        foreach (var key in new[] { "root_over_top", "pelvis_over_top", "knee_over_top", "sole_over_top", "thigh_deg" })
            Assert.True(line.ContainsKey(key), $"missing {key}");
        Assert.Null(line["root_over_top"]);
        Assert.Null(line["pelvis_over_top"]);
        Assert.Null(line["knee_over_top"]);
        Assert.Null(line["sole_over_top"]);
        Near(0f, (float)line["thigh_deg"]!, 0.05f);
    }
}

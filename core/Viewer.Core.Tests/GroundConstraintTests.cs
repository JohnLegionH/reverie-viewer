// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The constraint block of an animation and the pelvis ground constraint (the constrained ground sit): the clip's pelvis keys drop the
/// pelvis a fixed number of metres, so a taller avatar's seat hovers above the ground unless the constraint pulls the pelvis volume's
/// source point onto it (llkeyframemotion.cpp, applyConstraint, the constraint with no joint chain). The synthetic cases are worked by hand
/// and checked through <see cref="AvatarJoints"/>, which composes the skeleton separately from <see cref="ConstraintSolver"/>; the cases
/// on the real animation and character files are local input and skip with their reason without them. Pure, parallel.
/// </summary>
public class GroundConstraintTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
            <collision_volume name="PELVIS" pos="-0.01 0 -0.02" rot="0 8 0" scale="0.12 0.16 0.17" support="base"/>
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base"/>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = "<linden_avatar/>";

    private static CharacterSkeleton Skeleton() => CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml));

    private static readonly ClipConstraint Pelvis = new(0, false, "PELVIS", new Vector3(0, 0, -0.9f), "GROUND", Vector3.Zero, Vector3.Zero, 0.6f, 0.8f, 10f, 10f);

    /// <summary>A sit-like clip: the pelvis keys sink to 0.85 m below the standing hip and tip back, as the official ground sit does.</summary>
    private static AnimationClip Sit(params ClipConstraint[] constraints) => new()
    {
        Duration = 2.8f, Loop = true, LoopIn = 2.8f, LoopOut = 2.8f, Priority = 3, EaseIn = 0.3f, EaseOut = 1f, Emote = "",
        Tracks = [new JointTrack("mPelvis", 3, [0f, 2.8f], [Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f)], [0f, 0.5f, 2.8f], [Vector3.Zero, new Vector3(0, 0, -0.85f), new Vector3(0, 0, -0.85f)])],
        Constraints = constraints,
    };

    /// <summary>The solver run frame by frame (sixty a second) from time zero up to <paramref name="until"/>, as a viewer would; the last frame's result.</summary>
    internal static ConstraintPose Run(AnimationClip clip, AvatarRig rig, AvatarShape? shape, float groundBelowPelvis, float until, out ConstraintState state)
    {
        var solver = new ConstraintSolver(rig);
        state = ConstraintState.For(null, clip, rig.NodeCount)!;
        var tracks = AvatarJoints.TracksFor(rig, clip);
        ConstraintPose? last = null;
        for (int i = 0; i * (1f / 60f) <= until + 1e-4f; i++) last = solver.Step(state, tracks, shape, null, clip.TimeAt(i / 60.0), groundBelowPelvis);
        return last!;
    }

    /// <summary>Where the pelvis volume's source point is, in the pelvis frame (the frame's origin is the standing hip), with the constraint's result applied.</summary>
    private static float SourceHeight(AnimationClip clip, AvatarRig rig, double t, ConstraintPose? pose)
    {
        var joints = new AvatarJoints(rig) { Constraint = pose };
        joints.Begin(AvatarJoints.TracksFor(rig, clip), clip.TimeAt(t));
        return Vector3.Transform(Pelvis.SourceOffset, joints.World(rig.Skeleton.IndexOf("PELVIS"))).Z;
    }

    [Theory]
    [InlineData(1.00f)]   // an avatar whose soles are a hip height below the pelvis
    [InlineData(1.15f)]
    [InlineData(1.30f)]   // a tall avatar: the keys alone leave the seat well above the ground
    [InlineData(0.85f)]   // a short one: the keys alone would sink it
    public void The_pelvis_volume_source_point_ends_on_the_ground_within_two_centimetres(float groundBelowPelvis)
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Sit(Pelvis);
        float t = 2.0f;
        float without = SourceHeight(clip, rig, t, null);
        var pose = Run(clip, rig, null, groundBelowPelvis, t, out _);
        float with = SourceHeight(clip, rig, t, pose);
        Assert.InRange(with, -groundBelowPelvis - 0.02f, -groundBelowPelvis + 0.02f);
        if (groundBelowPelvis >= 1.15f) Assert.True(without - (-groundBelowPelvis) > 0.05f, $"the keys alone leave the seat {without + groundBelowPelvis:F3} m above the ground");
    }

    /// <summary>The seat's height above the soles for an avatar whose pelvis rest height is <paramref name="hip"/> above them, with the ground
    /// <paramref name="groundBelowPelvis"/> below the pelvis.</summary>
    private static float SeatAboveSoles(AnimationClip clip, AvatarRig rig, float hip, float groundBelowPelvis)
    {
        float t = 2.0f;
        var pose = Run(clip, rig, null, groundBelowPelvis, t, out _);
        return hip + SourceHeight(clip, rig, t, pose);
    }

    [Theory]
    [InlineData(0f, 0.00f)]   // on the land
    [InlineData(0f, 0.40f)]   // the region rests it 40 cm above the land: the land is the ground
    [InlineData(3f, 0.00f)]   // on a deck 3 m above the land, the foot plane saying so
    [InlineData(0.5f, 0.00f)] // on a platform 50 cm above the land
    public void A_ground_sit_ends_within_three_centimetres_of_the_surface_the_avatar_is_on(float floorAboveLand, float solesAboveSurface)
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Sit(Pelvis);
        const float hip = 1.0f, landZ = 24.15f;
        float surface = floorAboveLand > 0 ? landZ + floorAboveLand - OfficialGround.PlaneAllowance : landZ;   // the official ground: the land, or the floor less the physics allowance
        var soles = new Vector3(100, 100, (floorAboveLand > 0 ? landZ + floorAboveLand : landZ) + solesAboveSurface);
        var plane = floorAboveLand > 0 ? new Vector4(0, 0, 1, landZ + floorAboveLand) : default;
        float gap = soles.Z - (OfficialGround.HeightUnder(soles, landZ, plane) ?? soles.Z);
        float seatZ = soles.Z + SeatAboveSoles(clip, rig, hip, hip + gap);
        Assert.InRange(seatZ, surface - 0.03f, surface + 0.03f);
    }

    [Fact]
    public void Nothing_moves_before_the_constraint_eases_in_and_it_is_part_way_in_the_middle_of_the_ease()
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Sit(Pelvis);
        Assert.Null(Run(clip, rig, null, 1.3f, 0.5f, out _).PelvisPosition);
        float t = 0.7f;
        float keysOnly = SourceHeight(clip, rig, t, null);
        float partly = SourceHeight(clip, rig, t, Run(clip, rig, null, 1.3f, t, out _));
        float full = -1.3f;
        Assert.True(MathF.Abs(partly - full) < MathF.Abs(keysOnly - full), "the seat has moved towards the ground");
        Assert.True(MathF.Abs(partly - full) > 0.02f, "but is not there yet");
    }

    [Fact]
    public void A_clip_with_no_constraint_or_with_one_the_skeleton_cannot_hold_does_not_move_the_pelvis()
    {
        var rig = new AvatarRig(Skeleton());
        Assert.Null(ConstraintState.For(null, Sit(), rig.NodeCount));   // nothing to solve
        Assert.Null(ConstraintState.For(null, null, rig.NodeCount));
        // a chain that runs above the top of the skeleton, and a target volume the skeleton does not have: left out
        Assert.Null(Run(Sit(Pelvis with { ChainLength = 2 }), rig, null, 1.3f, 2f, out _).PelvisPosition);
        Assert.Null(Run(Sit(Pelvis with { Target = "R_UPPER_LEG" }), rig, null, 1.3f, 2f, out _).PelvisPosition);
    }

    [Fact]
    public void The_pose_and_the_joints_move_the_pelvis_by_the_same_drop()
    {
        var clip = Sit(Pelvis);
        var rig = new AvatarRig(Skeleton());
        var joints = new AvatarJoints(rig) { PelvisDrop = -0.3f };
        joints.Begin(AvatarJoints.TracksFor(rig, clip), 2f);
        Assert.Equal(-0.85f - 0.3f, joints.World(rig.Pelvis).Translation.Z, 4);
    }

    [Fact]
    public void A_constraint_in_a_mix_follows_its_motion_and_fades_with_it()
    {
        var sit = Sit(Pelvis);
        var mix = AnimationMix.Blend([new BlendInput(sit, new MotionTiming(10.0, 0.3, 0.2, null, 0), 1)]);
        Assert.NotNull(mix);
        var c = Assert.Single(mix!.Constraints);
        Assert.Equal(0f, c.WeightAt(10.5f));            // 0.5 s into the motion: before its constraint eases in
        Assert.Equal(1f, c.WeightAt(11.0f), 3);         // 1.0 s in: fully applied
        var stopped = AnimationMix.Blend([new BlendInput(sit, new MotionTiming(10.0, 0.3, 0.2, 12.0, 1.0), 1)])!.Constraints[0];
        Assert.Equal(0f, stopped.WeightAt(12.5f));      // the motion has finished fading out
    }

    [Fact]
    public void The_state_of_a_constraint_that_goes_on_is_kept_when_the_clip_changes_and_a_new_constraint_starts_fresh()
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Sit(Pelvis);
        Run(clip, rig, null, 1.3f, 2f, out var state);
        var other = Sit(Pelvis, Pelvis with { SourceOffset = new Vector3(0, 0, -0.5f) });
        var next = ConstraintState.For(state, other, rig.NodeCount)!;
        Assert.NotSame(state, next);
        Assert.Same(next, ConstraintState.For(next, other, rig.NodeCount));
        Assert.True(next.HasLast);
        Assert.NotNull(next.Prev.PelvisPosition);   // the body is as the constraints left it
        Assert.True(next.Slots[0].Active);          // the same constraint goes on
        Assert.False(next.Slots[1].Active);         // the new one starts fresh
    }

    [Fact]
    public void The_constraint_block_of_an_animation_is_read()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)1); w.Write((ushort)0); w.Write(3); w.Write(1f);
            w.Write((byte)0); w.Write(0f); w.Write(1f); w.Write(1);   // emote name, loop in and out, loop flag
            w.Write(0f); w.Write(0f); w.Write(0);                       // ease in, ease out, hand pose
            w.Write(0);                                                 // no joints
            w.Write(1);                                                 // one constraint
            w.Write((byte)0); w.Write((byte)0);
            WriteName(w, "PELVIS"); WriteVector(w, 0, 0, -0.9f);
            WriteName(w, "GROUND"); WriteVector(w, 0, 0, 0.01f); WriteVector(w, 0, 0, 0);
            w.Write(0.6f); w.Write(0.8f); w.Write(10f); w.Write(10f);
        }
        var clip = AnimationClip.Parse(ms.ToArray());
        var c = Assert.Single(clip.Constraints);
        Assert.Equal(0, c.ChainLength);
        Assert.False(c.Plane);
        Assert.Equal("PELVIS", c.SourceVolume);
        Assert.Equal(new Vector3(0, 0, -0.9f), c.SourceOffset);
        Assert.True(c.TargetsGround);
        Assert.Equal(0.01f, c.TargetOffset.Z);
        Assert.Equal((0.6f, 0.8f, 10f, 10f), (c.EaseInStart, c.EaseInStop, c.EaseOutStart, c.EaseOutStop));
    }

    [Fact]
    public void A_clip_that_ends_before_its_constraints_or_has_malformed_ones_has_none()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)1); w.Write((ushort)0); w.Write(3); w.Write(1f);
            w.Write((byte)0); w.Write(0f); w.Write(1f); w.Write(1);
            w.Write(0f); w.Write(0f); w.Write(0); w.Write(0);
        }
        Assert.Empty(AnimationClip.Parse(ms.ToArray()).Constraints);   // no constraint block at all
        ms.Write(BitConverter.GetBytes(1));                              // a count with nothing after it
        Assert.Empty(AnimationClip.Parse(ms.ToArray()).Constraints);
    }

    private static void WriteName(BinaryWriter w, string name) { var b = new byte[16]; Encoding.UTF8.GetBytes(name).CopyTo(b, 0); w.Write(b); }
    private static void WriteVector(BinaryWriter w, float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }

    /// <summary>Skipped with its reason unless the character folder and the ground-sit animation file are named (grid content, never committed).</summary>
    internal sealed class GroundSitFactAttribute : FactAttribute
    {
        public const string Variable = "NV_GROUND_SIT_ANIM";

        public GroundSitFactAttribute()
        {
            if (CharacterFactAttribute.Folder is null) Skip = "local-only character data: NV_CHARACTER_DIR is not set";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))) Skip = $"local-only animation: {Variable} is not set";
        }
    }

    /// <summary>As <see cref="GroundSitFactAttribute"/>, and a visual-parameter set in a local JSON file (NV_FACE_LOOK_PARAMS: {"order": [ids], "bytes": [values]}).</summary>
    private sealed class GroundSitWithShapeFactAttribute : FactAttribute
    {
        public GroundSitWithShapeFactAttribute()
        {
            if (CharacterFactAttribute.Folder is null) Skip = "local-only character data: NV_CHARACTER_DIR is not set";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GroundSitFactAttribute.Variable))) Skip = $"local-only animation: {GroundSitFactAttribute.Variable} is not set";
            else if (LocalReferenceFactAttribute.Path("NV_FACE_LOOK_PARAMS") is null) Skip = "local-only parameter set: NV_FACE_LOOK_PARAMS is not set";
        }
    }

    [GroundSitWithShapeFact]
    public void The_official_ground_sit_of_a_stored_shape_ends_on_the_land_and_on_a_floor_within_three_centimetres()
    {
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var rig = new AvatarRig(body.Skeleton);
        var clip = AnimationClip.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable(GroundSitFactAttribute.Variable)!));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path("NV_FACE_LOOK_PARAMS")!));
        var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var raw = doc.RootElement.GetProperty("bytes").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        var shape = AvatarShape.From(rig, body.Shape!, new Viewer.Core.World.VisualParamSet(raw, order));
        var neutral = AvatarShape.Neutral(rig);
        float lift = PoseComputer.LiftOf(neutral, neutral.Measure(null)?.PelvisToFoot ?? 0f, shape, null, 0f);
        float hip = body.Skeleton.Joints[0].Position.Z - body.FootHeight;
        int volume = rig.Skeleton.IndexOf("PELVIS");
        var pelvis = Assert.Single(clip.Constraints, c => c.ChainLength == 0);
        const float landZ = 24.15f;
        foreach (var (floorAboveLand, solesAboveSurface) in new[] { (0f, 0f), (0f, 0.4f), (0.5f, 0f), (3f, 0f) })
        {
            float surface = floorAboveLand > 0 ? landZ + floorAboveLand - OfficialGround.PlaneAllowance : landZ;
            var soles = new Vector3(100, 100, (floorAboveLand > 0 ? landZ + floorAboveLand : landZ) + solesAboveSurface);
            var plane = floorAboveLand > 0 ? new Vector4(0, 0, 1, landZ + floorAboveLand) : default;
            float gap = soles.Z - (OfficialGround.HeightUnder(soles, landZ, plane) ?? soles.Z);
            float t = 2.0f;
            var pose = Run(clip, rig, shape, hip + lift + gap, t, out _);
            var joints = new AvatarJoints(rig) { Shape = shape, Constraint = pose };
            joints.Begin(AvatarJoints.TracksFor(rig, clip), clip.TimeAt(t));
            float seatZ = soles.Z + hip + lift + Vector3.Transform(pelvis.SourceOffset, joints.World(volume)).Z;
            Assert.True(MathF.Abs(seatZ - surface) <= 0.03f, $"floor {floorAboveLand} m, soles {solesAboveSurface} m above it: the seat ends {seatZ - surface:F3} m from the surface");
        }
    }

    [GroundSitFact]
    public void The_official_ground_sit_animation_puts_the_seat_on_the_ground_for_avatars_of_any_height()
    {
        string path = Environment.GetEnvironmentVariable(GroundSitFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{GroundSitFactAttribute.Variable} names a file that does not exist");
        string dir = CharacterFactAttribute.Folder!;
        var rig = new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"))));
        var clip = AnimationClip.Parse(File.ReadAllBytes(path));
        Assert.Equal(9, clip.Constraints.Count);
        var pelvis = Assert.Single(clip.Constraints, c => c.ChainLength == 0);
        Assert.Equal("PELVIS", pelvis.SourceVolume);
        int volume = rig.Skeleton.IndexOf("PELVIS");
        foreach (float below in new[] { 0.95f, 1.07f, 1.2f, 1.35f })
        {
            float t = 2.0f;
            var pose = Run(clip, rig, null, below, t, out _);
            var joints = new AvatarJoints(rig) { Constraint = pose };
            joints.Begin(AvatarJoints.TracksFor(rig, clip), clip.TimeAt(t));
            float source = Vector3.Transform(pelvis.SourceOffset, joints.World(volume)).Z;
            Assert.InRange(source, -below - 0.02f, -below + 0.02f);
        }
    }
}

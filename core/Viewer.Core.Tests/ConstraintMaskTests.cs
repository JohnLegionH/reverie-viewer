// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A clip's constraint is not applied to joints that another motion already drives at an equal or higher priority (the joint mask of the official
/// viewer's keyframe motion: llkeyframemotion.cpp applyConstraint, with the joint signatures llmotioncontroller.cpp updateMotionsByType keeps from the
/// motions it has already run, newest first, counting only motions at full weight). The cases pose a leg constraint of a ground sit under other motions
/// that turn the same leg. Pure, parallel.
/// </summary>
public class ConstraintMaskTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
            <bone name="mHipLeft" pos="0 0.1 -0.05" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mKneeLeft" pos="0 0 -0.45" rot="0 0 0" scale="1 1 1" support="base">
                <collision_volume name="L_LOWER_LEG" pos="0 0 -0.2" rot="0 0 0" scale="0.1 0.1 0.2" support="base"/>
              </bone>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes("<linden_avatar/>")));

    private static readonly ClipConstraint LeftLeg = new(1, false, "L_LOWER_LEG", new Vector3(0, 0, -0.1f), "GROUND", Vector3.Zero, Vector3.Zero, 0.6f, 0.8f, 10f, 10f);

    private static Quaternion Turn(float a) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, a);

    private static JointTrack Rotating(string joint, float angle, int priority = -1) =>
        new(joint, priority, [0f, 5f], [Turn(angle), Turn(angle)], [], []);

    private static JointTrack Moving(string joint) => new(joint, -1, [], [], [0f, 5f], [Vector3.Zero, Vector3.Zero]);

    private static AnimationClip Clip(int priority, IEnumerable<JointTrack> tracks, params ClipConstraint[] constraints) => new()
    {
        Duration = 5f, Loop = true, LoopIn = 5f, LoopOut = 5f, Priority = priority, EaseIn = 0f, EaseOut = 0f, Emote = "",
        Tracks = [.. tracks], Constraints = constraints,
    };

    /// <summary>The ground sit: priority 3, turns the pelvis, hip and knee, and holds the lower leg to the ground.</summary>
    private static AnimationClip Sit() => Clip(3, [Rotating("mPelvis", 0.2f), Rotating("mHipLeft", -1.2f), Rotating("mKneeLeft", 1.4f)], LeftLeg);

    private static readonly MotionTiming Full = new(0, 0, 0, null, 0);

    /// <summary>The sit's constraint run frame by frame to two seconds under the given motions; what it changed in the pose.</summary>
    private static ConstraintPose Run(params BlendInput[] inputs)
    {
        var rig = Rig();
        var mix = AnimationMix.Blend(inputs)!;
        var solver = new ConstraintSolver(rig);
        var state = ConstraintState.For(null, mix, rig.NodeCount)!;
        var tracks = AvatarJoints.TracksFor(rig, mix);
        ConstraintPose? last = null;
        for (int i = 0; i <= 120; i++) last = solver.Step(state, tracks, null, null, i / 60f, 0.65f);
        return last!;
    }

    private static bool Applied(ConstraintPose pose, AvatarRig rig) => pose.Rotations[rig.Skeleton.IndexOf("mKneeLeft")] is not null;

    [Fact]
    public void With_nothing_else_driving_the_leg_the_sits_leg_constraint_is_applied()
    {
        var rig = Rig();
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 1)), rig), "the constraint changes the knee");
    }

    [Fact]
    public void A_newer_motion_of_a_higher_priority_that_turns_the_legs_keeps_the_constraint_from_being_applied()
    {
        var rig = Rig();
        var higher = Clip(4, [Rotating("mHipLeft", 0.3f), Rotating("mKneeLeft", 0.2f)]);
        Assert.False(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(higher, Full, 2)), rig));
    }

    [Fact]
    public void A_newer_motion_of_the_same_priority_also_keeps_it_from_being_applied()
    {
        var rig = Rig();
        var same = Clip(3, [Rotating("mHipLeft", 0.3f)]);
        Assert.False(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(same, Full, 2)), rig));
    }

    [Fact]
    public void A_motion_of_a_lower_priority_does_not()
    {
        var rig = Rig();
        var lower = Clip(2, [Rotating("mHipLeft", 0.3f), Rotating("mKneeLeft", 0.2f)]);
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(lower, Full, 2)), rig));
    }

    [Fact]
    public void A_higher_priority_motion_that_started_earlier_does_not_because_motions_are_taken_newest_first()
    {
        var rig = Rig();
        var higher = Clip(4, [Rotating("mHipLeft", 0.3f), Rotating("mKneeLeft", 0.2f)]);
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 2), new BlendInput(higher, Full, 1)), rig));
    }

    [Fact]
    public void A_motion_that_only_moves_the_legs_or_turns_other_joints_does_not()
    {
        var rig = Rig();
        var positions = Clip(4, [Moving("mHipLeft"), Moving("mKneeLeft")]);
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(positions, Full, 2)), rig));
        var elsewhere = Clip(4, [Rotating("mPelvis", 0.3f)]);
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(elsewhere, Full, 2)), rig));
    }

    [Fact]
    public void A_higher_priority_motion_still_fading_in_does_not_keep_it_off_and_one_at_full_weight_does()
    {
        var rig = Rig();
        var higher = Clip(4, [Rotating("mHipLeft", 0.3f), Rotating("mKneeLeft", 0.2f)]);
        var fadingIn = new MotionTiming(0, 100.0, 0, null, 0);   // a hundred seconds to ease in: under full weight all through the run
        Assert.True(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(higher, fadingIn, 2)), rig));
        Assert.False(Applied(Run(new BlendInput(Sit(), Full, 1), new BlendInput(higher, Full, 2)), rig));
    }

    [Fact]
    public void A_built_in_motion_that_turns_the_legs_counts_like_any_other()
    {
        var rig = Rig();
        var procedural = new FixedProcedural(["mHipLeft", "mKneeLeft"], priority: 4);
        var mix = AnimationMix.Blend([new BlendInput(Sit(), Full, 1)], [new ProceduralInput(procedural, Full, 2)], null)!;
        var solver = new ConstraintSolver(rig);
        var state = ConstraintState.For(null, mix, rig.NodeCount)!;
        var tracks = AvatarJoints.TracksFor(rig, mix);
        ConstraintPose? last = null;
        for (int i = 0; i <= 120; i++) last = solver.Step(state, tracks, null, null, i / 60f, 0.65f);
        Assert.False(Applied(last!, rig));
    }

    private sealed class FixedProcedural(string[] joints, int priority) : IProceduralMotion
    {
        public int Priority { get; } = priority;
        public string Name => "fixed";
        public bool Additive => false;
        public float EaseIn => 0f;
        public float EaseOut => 0f;
        public IReadOnlyList<string> RotationJoints { get; } = joints;
        public IReadOnlyList<string> PositionJoints { get; } = [];
        public Quaternion Rotation(string joint, double t) => Turn(0.1f);
        public Vector3 Position(string joint, double t) => Vector3.Zero;
    }
}

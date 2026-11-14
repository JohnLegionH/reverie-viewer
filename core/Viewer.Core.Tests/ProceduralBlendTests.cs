// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Built-in motions in the blend: a normal one competes with the animations by priority and weight, an additive one is added on top of what the
/// others blend to without using up any of their weight (llpose.cpp LLJointStateBlender::addJointState and blendJointStates: the final rotation is
/// <c>added * blended</c>, each additive state's share nlerped from the identity by its weight), and the motion controller runs them from the
/// moment they start (llvoavatar.cpp startDefaultMotions). Pure, runs in parallel.
/// </summary>
public class ProceduralBlendTests
{
    private sealed class Fixed(string name, int priority, bool additive, float easeIn, float easeOut, string joint, Quaternion rotation, Vector3? position = null) : IProceduralMotion
    {
        public string Name => name;
        public int Priority => priority;
        public bool Additive => additive;
        public float EaseIn => easeIn;
        public float EaseOut => easeOut;
        public IReadOnlyList<string> RotationJoints => rotation == Quaternion.Identity && position is not null ? [] : [joint];
        public IReadOnlyList<string> PositionJoints => position is not null ? [joint] : [];
        public double LastT;
        public Quaternion Rotation(string j, double t) { LastT = t; return rotation; }
        public Vector3 Position(string j, double t) => position ?? Vector3.Zero;
    }

    private static Quaternion Turn(Vector3 axis, float radians) => Quaternion.CreateFromAxisAngle(axis, radians);
    private static readonly Quaternion QA = Turn(Vector3.UnitX, 0.6f), QX = Turn(Vector3.UnitY, 0.5f), QD = Turn(Vector3.UnitZ, 0.2f);

    private static void Near(Quaternion expected, Quaternion actual)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 0.99995f, $"expected {expected} but got {actual} (|dot| {dot})");
    }

    private static AnimationClip Clip(int priority, float easeIn, float easeOut, params JointTrack[] tracks) => new()
    {
        Duration = 1f, Loop = true, LoopIn = 0, LoopOut = 1f, Priority = priority, EaseIn = easeIn, EaseOut = easeOut, Emote = "", Tracks = tracks,
    };

    private static JointTrack Hold(string joint, Quaternion q) => new(joint, -1, [0f, 1f], [q, q], [], []);
    private static BlendInput Anim(AnimationClip c, int order, double start = 0) => new(c, MotionTiming.Start(c, start), order);
    private static ProceduralInput Proc(IProceduralMotion m, int order, double start = 0) => new(m, new MotionTiming(start, m.EaseIn, m.EaseOut, null, 0), order);

    private static AnimationClip Mixed(IReadOnlyList<BlendInput> animations, IReadOnlyList<ProceduralInput> procedurals, Func<string, Quaternion?>? defaults)
    {
        var mix = AnimationMix.Blend(animations, procedurals, defaults);
        Assert.NotNull(mix);
        return mix!;
    }

    private static AnimationClip Mix(MotionController c)
    {
        Assert.NotNull(c.Mix);
        return c.Mix!;
    }

    private static Quaternion Rot(AnimationClip mix, string joint, float t) => AnimationClip.RotationAt(Assert.Single(mix.Tracks, x => x.Joint == joint), t);

    [Fact]
    public void An_additive_motion_is_added_on_top_of_the_blended_pose_as_added_then_blended()
    {
        var mix = Mixed([Anim(Clip(2, 0, 0, Hold("mChest", QA)), 1)], [Proc(new Fixed("x", 2, true, 0, 0, "mChest", QX), 2)], null);
        Near(QA * QX, Rot(mix, "mChest", 3f));   // the official added * blended is, with this library's product, blended * added
    }

    [Fact]
    public void An_additive_motion_counts_for_its_weight_nlerped_from_the_identity()
    {
        var mix = Mixed([Anim(Clip(2, 0, 0, Hold("mChest", QA)), 1)], [Proc(new Fixed("x", 2, true, 1f, 0, "mChest", QX), 2)], null);
        var half = Quaternion.Normalize(new Quaternion(QX.X * 0.5f, QX.Y * 0.5f, QX.Z * 0.5f, (1f + QX.W) * 0.5f));
        Near(QA * half, Rot(mix, "mChest", 0.5f));
        Near(QA, Rot(mix, "mChest", 0f));
    }

    [Fact]
    public void An_additive_motion_uses_none_of_the_weight_the_animations_share()
    {
        // two animations cross-fading, half each: the additive turn is on top of their blend whatever its own priority
        var a = Clip(2, 1f, 0, Hold("mChest", QA));
        var b = Clip(2, 1f, 0, Hold("mChest", QD));
        var withAdd = Mixed([Anim(a, 1), Anim(b, 2)], [Proc(new Fixed("x", 4, true, 0, 0, "mChest", QX), 3)], null);
        var without = Mixed([Anim(a, 1), Anim(b, 2)], [], null);
        Near(Rot(without, "mChest", 0.5f) * QX, Rot(withAdd, "mChest", 0.5f));
    }

    [Fact]
    public void A_joint_only_an_additive_motion_turns_starts_from_the_skeletons_own_rotation()
    {
        var motion = new Fixed("x", 2, true, 0, 0, "mTorso", QX);
        var mix = Mixed([Anim(Clip(2, 0, 0, Hold("mChest", QA)), 1)], [Proc(motion, 2)], j => j == "mTorso" ? QD : null);
        Near(QD * QX, Rot(mix, "mTorso", 1f));
        var plain = Mixed([Anim(Clip(2, 0, 0, Hold("mChest", QA)), 1)], [Proc(motion, 2)], null);
        Near(QX, Rot(plain, "mTorso", 1f));
    }

    [Fact]
    public void A_normal_built_in_motion_competes_by_priority_and_a_newer_motion_wins_a_tie()
    {
        var chest = new Fixed("breath", 1, false, 0, 0, "mChest", QX);
        // an animation of higher priority covers it
        Near(QA, Rot(Mixed([Anim(Clip(2, 0, 0, Hold("mChest", QA)), 5)], [Proc(chest, 1)], null), "mChest", 1f));
        // one of lower priority gives way to it
        Near(QX, Rot(Mixed([Anim(Clip(0, 0, 0, Hold("mChest", QA)), 5)], [Proc(chest, 1)], null), "mChest", 1f));
        // the same priority: the one that started later wins
        Near(QA, Rot(Mixed([Anim(Clip(1, 0, 0, Hold("mChest", QA)), 5)], [Proc(chest, 1)], null), "mChest", 1f));
        Near(QX, Rot(Mixed([Anim(Clip(1, 0, 0, Hold("mChest", QA)), 1)], [Proc(chest, 5)], null), "mChest", 1f));
    }

    [Fact]
    public void A_built_in_motion_is_read_at_the_time_since_it_started()
    {
        var m = new Fixed("x", 1, false, 0, 0, "mChest", QX);
        var mix = Mixed([], [Proc(m, 1, start: 10)], null);
        Rot(mix, "mChest", 12.5f);
        Assert.Equal(2.5, m.LastT, 3);
    }

    [Fact]
    public void A_built_in_position_motion_marks_the_joint_and_gives_way_to_a_newer_animation_of_the_same_priority()
    {
        var fix = new Fixed("pelvis", 0, false, 0.5f, 0.5f, "mPelvis", Quaternion.Identity, Vector3.Zero);
        var moved = new JointTrack("mPelvis", -1, [], [], [0f, 1f], [new Vector3(0, 0, 0.3f), new Vector3(0, 0, 0.3f)]);
        var alone = Mixed([], [Proc(fix, 1)], null);
        var track = Assert.Single(alone.Tracks, t => t.Joint == "mPelvis");
        Assert.Equal(Vector3.Zero, AnimationClip.PositionAt(track, 5f));
        Assert.True(track.PositionTimes.Length > 0);
        var withAnimation = Mixed([Anim(Clip(0, 0, 0, moved), 2)], [Proc(fix, 1)], null);
        Assert.Equal(0.3f, AnimationClip.PositionAt(Assert.Single(withAnimation.Tracks, t => t.Joint == "mPelvis"), 5f).Z, 4);
    }

    [Fact]
    public void The_controller_runs_a_built_in_motion_from_its_start_with_no_animation_playing_and_stops_it_once_eased_out()
    {
        var c = new MotionController();
        var m = new Fixed("x", 1, false, 0, 0.5f, "mChest", QX);
        Assert.True(c.StartProcedural(m, 100.0));
        Assert.False(c.StartProcedural(m, 100.0));
        Assert.NotNull(c.Mix);
        Near(QX, Rot(Mix(c), "mChest", 101f));
        Assert.Empty(c.States(101.0));   // not an animation: the animation list does not show it
        Assert.True(c.StopProcedural("x", 110.0));
        Assert.False(c.StopProcedural("x", 110.0));
        c.Update(110.6, []);
        Assert.Null(c.Mix);
    }

    [Fact]
    public void The_controller_hands_the_skeletons_rotations_to_the_blend()
    {
        var c = new MotionController { DefaultRotation = j => j == "mTorso" ? QD : null };
        c.StartProcedural(new Fixed("noise", 2, true, 0, 0, "mTorso", QX), 1.0);
        Near(QD * QX, Rot(Mix(c), "mTorso", 5f));
    }

    [Fact]
    public void Animations_and_built_in_motions_share_the_controllers_mix()
    {
        var c = new MotionController();
        c.StartProcedural(new Fixed("breath", 1, false, 0, 0, "mChest", QX), 1.0);
        var clip = Clip(2, 0, 0, Hold("mChest", QA));
        var id = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        c.Update(2.0, [new SignalledMotion(id, clip, 1)]);
        Near(QA, Rot(Mix(c), "mChest", 3f));   // priority 2 covers the priority 1 breath
        c.Update(4.0, []);
        c.Update(5.0, []);
        Near(QX, Rot(Mix(c), "mChest", 5f));   // gone: the breath shows again
    }
}

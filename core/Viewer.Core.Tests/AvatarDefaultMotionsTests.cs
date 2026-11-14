// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// When an avatar's built-in motions run (llvoavatar.cpp startDefaultMotions and processAnimationStateChanges): the head, eye, body sway, breathing
/// and pelvis-fix motions from the moment the avatar is drawn; the flying roll while it is in the air, not sitting and no walk animation is
/// signalled; the hand pose following what the animations ask for. Pure, runs in parallel.
/// </summary>
public class AvatarDefaultMotionsTests
{
    private static AnimationClip Mix(MotionController c)
    {
        Assert.NotNull(c.Mix);
        return c.Mix!;
    }

    private static JointTrack? Track(AnimationClip mix, string joint) => mix.Tracks.FirstOrDefault(t => t.Joint == joint);

    [Fact]
    public void Starting_runs_the_always_on_motions_on_their_joints_once()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(1);
        Assert.False(d.Started);
        d.Start(c, 5.0);
        Assert.True(d.Started);
        var mix = Mix(c);
        foreach (var joint in new[] { "mTorso", "mNeck", "mHead", "mChest", "mEyeLeft", "mEyeRight", "mFaceEyeAltLeft", "mFaceEyeAltRight" })
        {
            var t = Track(mix, joint);
            Assert.NotNull(t);
            Assert.True(t!.RotationTimes.Length > 0, joint + " should turn");
        }
        var pelvis = Track(mix, "mPelvis");
        Assert.NotNull(pelvis);
        Assert.True(pelvis!.PositionTimes.Length > 0);
        Assert.Equal(0, pelvis.RotationTimes.Length);   // the roll is not running
        d.Start(c, 9.0);   // a second start changes nothing
        Assert.Equal(mix.Tracks.Count, Mix(c).Tracks.Count);
        Assert.False(c.IsRunning("fly_adjust", 9.0));
        Assert.True(c.IsRunning("breathe_rot", 9.0));
        Assert.True(c.IsRunning("body_noise", 9.0));
        Assert.True(c.IsRunning("eye_rot", 9.0));
        Assert.True(c.IsRunning("head_rot", 9.0));
        Assert.True(c.IsRunning("pelvis_fix", 9.0));
    }

    [Fact]
    public void The_chest_breathes_with_the_period_of_the_sine_from_the_moment_it_starts()
    {
        var c = new MotionController();
        new AvatarDefaultMotions(2).Start(c, 100.0);
        var chest = Track(Mix(c), "mChest")!;
        // the breathing eases in over no time and is read at the time since it started
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Sin(2.0f) * 0.05f);
        var actual = AnimationClip.RotationAt(chest, 102f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 0.99999f, $"{expected} vs {actual}");
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]    // a walk animation signalled: walk adjust, not fly adjust
    [InlineData(false, false, false, false)]  // on the ground
    [InlineData(false, true, true, false)]    // sitting
    public void The_flying_roll_runs_in_the_air_when_not_sitting_and_not_walking(bool walk, bool air, bool sitting, bool expected) =>
        Assert.Equal(expected, AvatarDefaultMotions.ShouldFly(walk, air, sitting));

    [Fact]
    public void The_flying_roll_starts_and_stops_with_the_flight_and_follows_the_turn_rate()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(3);
        d.Start(c, 0.0);
        Assert.False(d.Step(c, 1.0, 0f, 11f, false, false, false, null));   // first frame, on the ground
        Assert.True(d.Step(c, 1.05, 0.1f, 11f, false, true, false, null));   // takes off: the roll starts
        Assert.True(c.IsRunning("fly_adjust", 1.05));
        float yaw = 0.1f;
        double now = 1.05;
        for (int i = 0; i < 100; i++)
        {
            now += 0.05;
            yaw += 0.1f;   // 2 rad/s
            Assert.False(d.Step(c, now, yaw, 11f, false, true, false, null));
        }
        Assert.Equal(-0.6f, d.Fly.Roll, 2);
        // the pelvis is turned by it in the mix
        var pelvis = Track(Mix(c), "mPelvis")!;
        var turned = AnimationClip.RotationAt(pelvis, (float)now);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d.Fly.Roll);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, turned)) > 0.9999f, $"{expected} vs {turned}");
        now += 0.05;
        Assert.True(d.Step(c, now, yaw, 11f, false, false, false, null));   // lands: the roll stops
        Assert.False(c.IsRunning("fly_adjust", now));
    }

    [Fact]
    public void The_turn_rate_is_taken_across_the_half_turn_wrap()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(4);
        d.Start(c, 0.0);
        // turning at 2 rad/s, the heading passing from +pi round to -pi: the roll must stay where a steady turn puts it, with no jump at the wrap
        for (int i = 0; i < 40; i++)
        {
            float yaw = 3.0f + 0.1f * i;
            while (yaw > MathF.PI) yaw -= 2 * MathF.PI;
            d.Step(c, 1.0 + 0.05 * i, yaw, 11f, false, true, false, null);
            if (i >= 12) Assert.InRange(d.Fly.Roll, -0.7f, -0.5f);
        }
    }

    [Fact]
    public void The_hand_pose_follows_what_the_animations_ask_for_from_the_second_frame_on()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(5);
        d.Start(c, 0.0);
        d.Step(c, 1.0, 0f, 0f, false, false, false, 3);
        Assert.Equal(1, d.Hand.Wanted);   // the first frame has no time step yet
        d.Step(c, 1.1, 0f, 0f, false, false, false, 3);
        Assert.Equal(3, d.Hand.Wanted);
        Assert.Equal(0.5f, d.Hand.Weight(3), 3);
    }

    [Fact]
    public void The_head_chain_is_stepped_with_the_torso_the_neck_hangs_from()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(6);
        d.Start(c, 0.0);
        var chest = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        for (int i = 0; i < 200; i++) d.Step(c, 1.0 + 0.05 * i, 0f, 0f, false, false, false, null, Quaternion.Identity, chest);
        var back = Quaternion.Inverse(chest);
        var expected = OfficialMath.Nlerp(0.5f, Quaternion.Identity, back);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, d.Head.Head)) > 0.9999f, $"{expected} vs {d.Head.Head}");
        Assert.True(MathF.Abs(Quaternion.Dot(expected, d.Head.Neck)) > 0.9999f);
    }

    [Fact]
    public void A_target_given_to_the_step_turns_the_head()
    {
        var c = new MotionController();
        var d = new AvatarDefaultMotions(7);
        d.Start(c, 0.0);
        for (int i = 0; i < 200; i++) d.Step(c, 1.0 + 0.05 * i, 0f, 0f, false, false, false, null, Quaternion.Identity, Quaternion.Identity, new Vector3(0, 2, 0));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f * 0.8f);
        var expected = OfficialMath.Nlerp(0.5f, Quaternion.Identity, turn);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, d.Head.Head)) > 0.9999f, $"{expected} vs {d.Head.Head}");
    }
}

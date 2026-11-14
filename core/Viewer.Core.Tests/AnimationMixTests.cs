// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Several animations playing on one avatar, blended as the official viewer blends them (llcharacter/llpose.cpp LLJointStateBlender::addJointState,
/// lines 196-230: each joint takes the motion with the highest priority for it, a newer motion first among equals; llkeyframemotion.cpp:1519-1534: a
/// joint priority of -1, USE_MOTION_PRIORITY, stands for the motion's own priority). A joint no higher motion animates is left to a lower one, so an
/// avatar whose top animation moves only some joints keeps the others posed by the rest, and not in the bind pose. Steady state only: no ease in or out.
/// Pure, runs in parallel.
/// </summary>
public class AnimationMixTests
{
    private static Quaternion Turn(Vector3 axis, float radians) => Quaternion.CreateFromAxisAngle(axis, radians);

    private static AnimationClip Clip(int priority, float duration, params JointTrack[] tracks) => new()
    {
        Duration = duration, Loop = true, LoopIn = 0, LoopOut = duration, Priority = priority, EaseIn = 0, EaseOut = 0, Emote = "", Tracks = tracks,
    };

    private static JointTrack Rot(string joint, int priority, Quaternion from, Quaternion to, float duration = 1f) => new(joint, priority, [0f, duration], [from, to], [], []);

    private static Quaternion? RotationOf(AnimationClip? mix, string joint) =>
        mix?.Tracks.FirstOrDefault(t => t.Joint == joint) is { } track ? AnimationClip.RotationAt(track, 0f) : null;

    private static readonly Quaternion A = Turn(Vector3.UnitX, 0.4f), B = Turn(Vector3.UnitY, 0.5f), C = Turn(Vector3.UnitZ, 0.6f), D = Turn(Vector3.UnitX, 1.1f);

    [Fact]
    public void The_highest_priority_motion_drives_each_joint_and_the_others_fill_the_rest()
    {
        var low = Clip(1, 1f, Rot("mChest", 1, A, A), Rot("mTorso", 1, B, B), Rot("mNeck", 1, C, C));
        var high = Clip(4, 1f, Rot("mChest", 4, D, D));
        var mix = AnimationMix.Build([new PlayingMotion(low, 1), new PlayingMotion(high, 2)]);
        Assert.Equal(D, RotationOf(mix, "mChest"));
        Assert.Equal(B, RotationOf(mix, "mTorso"));
        Assert.Equal(C, RotationOf(mix, "mNeck"));
    }

    [Fact]
    public void A_joints_own_priority_counts_and_minus_one_stands_for_the_motions_priority()
    {
        var x = Clip(2, 1f, Rot("mTorso", -1, A, A));    // the joint takes the motion's 2
        var y = Clip(5, 1f, Rot("mTorso", 1, B, B));     // the motion says 5, this joint 1
        var mix = AnimationMix.Build([new PlayingMotion(y, 9), new PlayingMotion(x, 1)]);
        Assert.Equal(A, RotationOf(mix, "mTorso"));
    }

    [Fact]
    public void Of_equal_priority_the_newer_motion_drives_the_joint()
    {
        var older = Clip(3, 1f, Rot("mChest", 3, A, A));
        var newer = Clip(3, 1f, Rot("mChest", 3, B, B));
        Assert.Equal(B, RotationOf(AnimationMix.Build([new PlayingMotion(older, 4), new PlayingMotion(newer, 7)]), "mChest"));
        Assert.Equal(B, RotationOf(AnimationMix.Build([new PlayingMotion(newer, 7), new PlayingMotion(older, 4)]), "mChest"));
    }

    [Fact]
    public void Each_track_is_read_at_its_own_clips_time()
    {
        var shortCycle = Clip(1, 1f, Rot("mTorso", 1, Quaternion.Identity, Turn(Vector3.UnitZ, 1.2f), 1f));
        var longCycle = Clip(2, 4f, Rot("mChest", 2, Quaternion.Identity, Turn(Vector3.UnitX, 1.2f), 4f));
        var mix = AnimationMix.Build([new PlayingMotion(shortCycle, 1), new PlayingMotion(longCycle, 2)])!;
        float playing = 1.25f;
        float t = mix.TimeAt(playing);
        var torso = mix.Tracks.FirstOrDefault(x => x.Joint == "mTorso");
        var chest = mix.Tracks.FirstOrDefault(x => x.Joint == "mChest");
        Assert.NotNull(torso);
        Assert.NotNull(chest);
        Assert.Equal(AnimationClip.RotationAt(shortCycle.Tracks[0], shortCycle.TimeAt(playing)), AnimationClip.RotationAt(torso!, t));   // 0.25 s into a 1 s cycle
        Assert.Equal(AnimationClip.RotationAt(longCycle.Tracks[0], longCycle.TimeAt(playing)), AnimationClip.RotationAt(chest!, t));     // 1.25 s into a 4 s one
    }

    [Fact]
    public void One_motion_is_that_motions_tracks()
    {
        var only = Clip(2, 1f, Rot("mChest", 2, A, B));
        var mix = AnimationMix.Build([new PlayingMotion(only, 1)])!;
        Assert.Equal(["mChest"], mix.Tracks.Select(x => x.Joint));
        Assert.Equal(AnimationClip.RotationAt(only.Tracks[0], 0.5f), AnimationClip.RotationAt(mix.Tracks[0], mix.TimeAt(0.5)));
    }

    [Fact]
    public void No_motion_is_no_clip()
    {
        Assert.Null(AnimationMix.Build([]));
    }

    [Fact]
    public void The_pose_of_a_mix_differs_from_the_bind_pose_where_a_lower_motion_fills_in()
    {
        var skeleton = AvatarTests.Skeleton();
        var rig = new AvatarRig(skeleton);
        var low = Clip(1, 1f, Rot("mTorso", 1, Turn(Vector3.UnitY, 0.7f), Turn(Vector3.UnitY, 0.7f)));
        var high = Clip(4, 1f, Rot("mChest", 4, Turn(Vector3.UnitX, 0.3f), Turn(Vector3.UnitX, 0.3f)));
        var mix = AnimationMix.Build([new PlayingMotion(low, 1), new PlayingMotion(high, 2)])!;
        var posed = new AvatarJoints(rig);
        posed.Begin(AvatarJoints.TracksFor(rig, mix), mix.TimeAt(0.5));
        var bind = new AvatarJoints(rig);
        bind.Begin(AvatarJoints.TracksFor(rig, null), 0f);
        int torso = rig.Find("mTorso");
        Assert.NotEqual(bind.World(torso), posed.World(torso));   // the torso is posed by the lower motion, not left in the bind pose
    }
}

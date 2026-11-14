// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Animations that start, fade and stop as the official viewer's motion controller plays them: only newly signalled animations start and only the
/// ones no longer signalled stop (llvoavatar.cpp processAnimationStateChanges); a motion that is active and not blending keeps playing, one that is
/// blending or stopping is replaced by a new instance (llmotioncontroller.cpp startMotion); ease in and ease out weights are cubic_step over the
/// clip's ease durations, and a motion stopped while still easing in eases out from the weight it had (updateMotionsByType). The joints are
/// blended by priority until the weights add up to one (llpose.cpp LLJointStateBlender::blendJointStates). Pure, runs in parallel.
/// </summary>
public class MotionControllerTests
{
    private static float Step(float x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);
    private static Quaternion Turn(Vector3 axis, float radians) => Quaternion.CreateFromAxisAngle(axis, radians);

    private static AnimationClip Clip(int priority, float easeIn, float easeOut, bool loop, float duration, params JointTrack[] tracks) => new()
    {
        Duration = duration, Loop = loop, LoopIn = 0, LoopOut = duration, Priority = priority, EaseIn = easeIn, EaseOut = easeOut, Emote = "", Tracks = tracks,
    };

    private static JointTrack Hold(string joint, Quaternion q) => new(joint, -1, [0f, 1f], [q, q], [], []);

    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a"), IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Quaternion QA = Turn(Vector3.UnitX, 0.6f), QB = Turn(Vector3.UnitY, 0.9f);

    private static SignalledMotion Sig(Guid id, AnimationClip clip, int seq = 1) => new(id, clip, seq);

    private static JointTrack ChestOf(MotionController c)
    {
        Assert.NotNull(c.Mix);
        return Assert.Single(c.Mix!.Tracks, t => t.Joint == "mChest");
    }

    private static double WeightOf(MotionController c, Guid id, double t) => c.States(t).Where(s => s.Id == id).Sum(s => s.Weight);

    [Fact]
    public void A_new_animation_eases_in_by_cubic_step_over_its_ease_in_duration_with_no_jump()
    {
        var clip = Clip(2, 0.4f, 0.3f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(10.0, [Sig(IdA, clip)]);
        Assert.Equal(0.0, WeightOf(c, IdA, 10.0), 4);
        Assert.Equal(Step(0.05f / 0.4f), WeightOf(c, IdA, 10.05), 4);
        Assert.Equal(0.5, WeightOf(c, IdA, 10.2), 4);
        Assert.Equal(1.0, WeightOf(c, IdA, 10.4), 4);
        Assert.Equal(1.0, WeightOf(c, IdA, 11.0), 4);
        Assert.True(WeightOf(c, IdA, 10.05) < 0.1);   // no jump from 0 to 1
    }

    [Fact]
    public void An_animation_no_longer_signalled_eases_out_by_cubic_step_and_is_then_gone()
    {
        var clip = Clip(2, 0f, 0.3f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(20.0, [Sig(IdA, clip)]);
        c.Update(30.0, [Sig(IdA, clip)]);
        Assert.Equal(1.0, WeightOf(c, IdA, 30.0), 4);
        c.Update(30.0, []);   // stopped now
        Assert.Equal(1.0, WeightOf(c, IdA, 30.0), 4);
        Assert.Equal(0.5, WeightOf(c, IdA, 30.15), 4);   // cubic_step(1 - 0.5)
        Assert.Equal(Step(1f - 0.1f / 0.3f), WeightOf(c, IdA, 30.1), 4);
        Assert.True(Assert.Single(c.States(30.15)).Stopped);
        c.Update(30.31, []);
        Assert.Empty(c.States(30.31));
        Assert.Null(c.Mix);
    }

    [Fact]
    public void An_animation_stopped_while_easing_in_eases_out_from_the_weight_it_had()
    {
        var clip = Clip(2, 0.4f, 0.3f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(0.0, [Sig(IdA, clip)]);
        Assert.Equal(0.5, WeightOf(c, IdA, 0.2), 4);
        c.Update(0.2, []);
        Assert.Equal(0.5, WeightOf(c, IdA, 0.2), 4);
        Assert.Equal(0.5 * 0.5, WeightOf(c, IdA, 0.35), 4);   // the residual weight times cubic_step(1 - 0.5)
    }

    [Fact]
    public void An_animation_signalled_again_while_it_fades_out_starts_a_new_instance_and_the_old_one_finishes_fading_out()
    {
        var clip = Clip(2, 0.4f, 0.3f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(0.0, [Sig(IdA, clip)]);
        c.Update(5.0, [Sig(IdA, clip)]);
        c.Update(5.0, []);
        c.Update(5.15, [Sig(IdA, clip)]);
        var states = c.States(5.25).OrderBy(s => s.Activation).ToList();
        Assert.Equal(2, states.Count);
        Assert.NotEqual(states[0].Instance, states[1].Instance);
        Assert.Equal(Step(1f - 0.25f / 0.3f), states[0].Weight, 4);   // the old instance, still easing out from 1
        Assert.True(states[0].Stopped);
        Assert.Equal(5.15, states[1].Activation, 6);
        Assert.Equal(Step(0.1f / 0.4f), states[1].Weight, 4);         // the new one, easing in from 0
        Assert.False(states[1].Stopped);
    }

    [Fact]
    public void An_unchanged_animation_keeps_its_instance_and_phase_when_another_one_starts()
    {
        var turn = new JointTrack("mChest", -1, [0f, 4f], [Quaternion.Identity, Turn(Vector3.UnitX, 1.2f)], [], []);
        var a = Clip(2, 0.2f, 0.2f, true, 4f, turn);
        var b = Clip(3, 0.2f, 0.2f, true, 1f, Hold("mNeck", QB));
        var c = new MotionController();
        c.Update(10.0, [Sig(IdA, a)]);
        int before = Assert.Single(c.States(11.0)).Instance;
        c.Update(12.0, [Sig(IdA, a), Sig(IdB, b, 2)]);
        var states = c.States(12.5);
        Assert.Equal(2, states.Count);
        var sa = states.Single(s => s.Id == IdA);
        Assert.Equal(before, sa.Instance);
        Assert.Equal(10.0, sa.Activation, 6);
        // the mix reads A 2.5 s into its own cycle
        var chest = ChestOf(c);
        Assert.Equal(AnimationClip.RotationAt(turn, a.TimeAt(2.5)), AnimationClip.RotationAt(chest, 12.5f));
    }

    [Fact]
    public void A_new_sequence_number_on_an_animation_that_has_finished_easing_in_changes_nothing()
    {
        var clip = Clip(2, 0.2f, 0.2f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(1.0, [Sig(IdA, clip, 5)]);
        c.Update(2.0, [Sig(IdA, clip, 6)]);
        var s = Assert.Single(c.States(2.0));
        Assert.Equal(1.0, s.Activation, 6);
        Assert.Equal(1.0, s.Weight, 4);
    }

    [Fact]
    public void A_new_sequence_number_on_an_animation_still_easing_in_replaces_it_with_a_new_instance()
    {
        var clip = Clip(2, 0.4f, 0.2f, true, 1f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(1.0, [Sig(IdA, clip, 5)]);
        c.Update(1.1, [Sig(IdA, clip, 6)]);
        var states = c.States(1.1).OrderBy(s => s.Activation).ToList();
        Assert.Equal(2, states.Count);
        Assert.True(states[0].Stopped);
        Assert.Equal(1.1, states[1].Activation, 6);
    }

    [Fact]
    public void An_animation_that_does_not_loop_stops_itself_early_enough_to_ease_out_at_its_end()
    {
        var clip = Clip(2, 0f, 0.5f, false, 2f, Hold("mChest", QA));
        var c = new MotionController();
        c.Update(0.0, [Sig(IdA, clip)]);
        Assert.Equal(1.0, WeightOf(c, IdA, 1.5), 4);                   // duration 2 minus ease out 0.5
        Assert.Equal(0.5, WeightOf(c, IdA, 1.75), 4);
        Assert.Equal(0.0, WeightOf(c, IdA, 2.0), 4);
    }

    [Fact]
    public void A_joint_two_motions_animate_is_blended_by_priority_until_the_weights_add_up_to_one()
    {
        var low = Clip(1, 0f, 0f, true, 1f, Hold("mChest", QA));
        var high = Clip(4, 0.4f, 0f, true, 1f, Hold("mChest", QB));
        var c = new MotionController();
        c.Update(0.0, [Sig(IdA, low, 1)]);
        c.Update(1.0, [Sig(IdA, low, 1), Sig(IdB, high, 2)]);
        var chest = ChestOf(c);
        Assert.Equal(QA, AnimationClip.RotationAt(chest, 1.0f));                       // the high one has no weight yet
        var half = AnimationClip.RotationAt(chest, 1.2f);                              // weight 0.5: lerp of the two, normalised
        var expected = Quaternion.Normalize(Quaternion.Lerp(QA, QB, 0.5f));
        Assert.True(Quaternion.Dot(half, expected) > 0.99999f);
        Assert.Equal(QB, AnimationClip.RotationAt(chest, 1.5f));                       // full weight: the low one is out of it
    }
}

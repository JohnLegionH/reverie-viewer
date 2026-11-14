// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The built-in avatar motions, against the values the official viewer's formulas give (llvoavatar.cpp LLBodyNoiseMotion, LLBreatheMotionRot,
/// LLPelvisFixMotion; llheadrotmotion.cpp LLHeadRotMotion and LLEyeMotion; llhandmotion.cpp; llkeyframewalkmotion.cpp LLFlyAdjustMotion), each
/// worked out here from the formula itself rather than from our code. Pure, runs in parallel.
/// </summary>
public class DefaultMotionsTests
{
    private const float DegToRad = MathF.PI / 180f;

    private static void Near(Quaternion expected, Quaternion actual, float tolerance = 1e-4f)
    {
        // the same rotation either way round
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1f - tolerance, $"expected {expected} but got {actual} (|dot| {dot})");
    }

    private static Quaternion AngleAxis(float radians, Vector3 axis) => Quaternion.CreateFromAxisAngle(axis, radians);

    // ---- helpers -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Euler_rotation_matches_the_official_formula()
    {
        float roll = 0.1f, pitch = 0.2f, yaw = 0.3f;
        float sx = MathF.Sin(roll / 2), cx = MathF.Cos(roll / 2), sy = MathF.Sin(pitch / 2), cy = MathF.Cos(pitch / 2), sz = MathF.Sin(yaw / 2), cz = MathF.Cos(yaw / 2);
        var expected = new Quaternion(sx * cy * cz + cx * sy * sz, cx * sy * cz - sx * cy * sz, cx * cy * sz + sx * sy * cz, cx * cy * cz - sx * sy * sz);
        var actual = OfficialMath.FromEuler(roll, pitch, yaw);
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
        Assert.Equal(expected.W, actual.W, 5);
    }

    [Fact]
    public void Then_applies_the_first_rotation_and_then_the_second()
    {
        var a = AngleAxis(0.7f, Vector3.UnitZ);
        var b = AngleAxis(0.4f, Vector3.UnitX);
        var v = new Vector3(1, 0.5f, -0.25f);
        var expected = Vector3.Transform(Vector3.Transform(v, a), b);
        var actual = Vector3.Transform(v, OfficialMath.Then(a, b));
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{expected} vs {actual}");
    }

    [Fact]
    public void Constrain_leaves_a_small_turn_and_holds_a_large_one_at_the_limit_on_the_same_axis()
    {
        var small = AngleAxis(0.3f, Vector3.UnitZ);
        Near(small, OfficialMath.Constrain(small, 1.0f));
        var held = OfficialMath.Constrain(AngleAxis(2.0f, Vector3.UnitZ), 1.0f);
        Near(AngleAxis(1.0f, Vector3.UnitZ), held);
        Near(AngleAxis(-1.0f, Vector3.UnitZ), OfficialMath.Constrain(AngleAxis(-2.0f, Vector3.UnitZ), 1.0f));
    }

    [Fact]
    public void Nlerp_runs_from_the_first_rotation_to_the_second()
    {
        var a = Quaternion.Identity;
        var b = AngleAxis(1.0f, Vector3.UnitY);
        Near(a, OfficialMath.Nlerp(0f, a, b));
        Near(b, OfficialMath.Nlerp(1f, a, b));
        var half = Quaternion.Normalize(new Quaternion((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2, (a.W + b.W) / 2));
        Near(half, OfficialMath.Nlerp(0.5f, a, b));
    }

    [Fact]
    public void The_smoothing_interpolant_is_one_minus_two_to_the_minus_dt_over_half_life()
    {
        Assert.Equal(0.5f, OfficialMath.Interpolant(0.15, 0.15), 5);
        Assert.Equal(0.75f, OfficialMath.Interpolant(0.3, 0.15), 5);
        Assert.Equal(0f, OfficialMath.Interpolant(0, 0.15), 5);
    }

    [Fact]
    public void Clamp_rescale_maps_and_holds_at_the_ends()
    {
        Assert.Equal(0f, OfficialMath.ClampRescale(5f, 7f, 15f, 0f, -0.6f), 5);
        Assert.Equal(-0.3f, OfficialMath.ClampRescale(11f, 7f, 15f, 0f, -0.6f), 5);
        Assert.Equal(-0.6f, OfficialMath.ClampRescale(40f, 7f, 15f, 0f, -0.6f), 5);
    }

    [Fact]
    public void Noise_is_zero_on_the_lattice_bounded_continuous_and_not_flat()
    {
        for (int i = -3; i <= 3; i++)
            for (int j = -3; j <= 3; j++)
                Assert.Equal(0f, OfficialNoise.Noise2(i, j), 5);
        float lo = 0, hi = 0, last = OfficialNoise.Noise2(0.5f, 0.5f);
        for (float x = 0; x < 60f; x += 0.01f)
        {
            float n = OfficialNoise.Noise2(x, 0.37f);
            lo = MathF.Min(lo, n); hi = MathF.Max(hi, n);
            Assert.True(MathF.Abs(n - last) < 0.1f, "noise must not jump");
            last = n;
        }
        Assert.True(hi > 0.2f && lo < -0.2f, $"noise should swing both ways (lo {lo}, hi {hi})");
        Assert.True(hi <= 1f && lo >= -1f);
        Assert.Equal(OfficialNoise.Noise2(3.3f, 4.4f), OfficialNoise.Noise2(3.3f, 4.4f));
    }

    // ---- body noise, breathing, pelvis fix -----------------------------------------------------------------------------------------

    [Fact]
    public void Body_noise_sways_the_torso_by_the_official_formula_and_is_additive_at_high_priority()
    {
        var m = new BodyNoiseMotion();
        Assert.True(m.Additive);
        Assert.Equal(2, m.Priority);
        Assert.Equal(0f, m.EaseIn);
        Assert.Equal(0f, m.EaseOut);
        Assert.Equal(["mTorso"], m.RotationJoints);
        Assert.Empty(m.PositionJoints);
        float biggest = 0;
        for (double t = 0; t < 120; t += 0.37)
        {
            float noiseX = OfficialNoise.Noise2((float)t * 0.2f, 0f), noiseY = OfficialNoise.Noise2(0f, (float)t * 0.2f);
            float rx = 1.0f * DegToRad * noiseX / 0.42f, ry = 1.0f * DegToRad * noiseY / 0.42f;
            Near(OfficialMath.FromEuler(rx, ry, 0f), m.Rotation("mTorso", t));
            biggest = MathF.Max(biggest, 2 * MathF.Acos(MathF.Min(1f, MathF.Abs(m.Rotation("mTorso", t).W))));
        }
        Assert.True(biggest > 0.002f, "the torso should sway");
        Assert.True(biggest < 3f * DegToRad, "and by a degree or so, not more");
    }

    [Fact]
    public void Breathing_turns_the_chest_about_y_by_the_sine_of_the_time_times_the_strength()
    {
        var m = new BreatheMotion();
        Assert.False(m.Additive);
        Assert.Equal(1, m.Priority);
        Assert.Equal(["mChest"], m.RotationJoints);
        for (double t = 0; t < 20; t += 0.25)
            Near(AngleAxis(MathF.Sin((float)t) * 0.05f, Vector3.UnitY), m.Rotation("mChest", t));
        // one breath takes 2 pi seconds
        Near(m.Rotation("mChest", 1.0), m.Rotation("mChest", 1.0 + 2 * Math.PI));
        float peak = MathF.Abs(m.Rotation("mChest", Math.PI / 2).Y);
        Assert.Equal(MathF.Sin(0.025f), peak, 5);
    }

    [Fact]
    public void Pelvis_fix_holds_the_pelvis_at_zero_at_low_priority_with_half_second_eases()
    {
        var m = new PelvisFixMotion();
        Assert.False(m.Additive);
        Assert.Equal(0, m.Priority);
        Assert.Equal(0.5f, m.EaseIn);
        Assert.Equal(0.5f, m.EaseOut);
        Assert.Equal(["mPelvis"], m.PositionJoints);
        Assert.Empty(m.RotationJoints);
        Assert.Equal(Vector3.Zero, m.Position("mPelvis", 3.0));
    }

    // ---- flying ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fly_adjust_is_an_additive_pelvis_motion_at_higher_priority()
    {
        var m = new FlyAdjustMotion();
        Assert.True(m.Additive);
        Assert.Equal(3, m.Priority);
        Assert.Equal(["mPelvis"], m.RotationJoints);
        Assert.Equal(["mPelvis"], m.PositionJoints);
        Assert.Equal(Vector3.Zero, m.Position("mPelvis", 1.0));
    }

    [Fact]
    public void The_roll_settles_at_the_turn_rate_times_the_speed_factor_and_the_pelvis_turns_by_it()
    {
        var m = new FlyAdjustMotion();
        for (int i = 0; i < 200; i++) m.Step(0.05, 2f, 11f);   // 11 m/s: factor -0.3; turn rate 2: target -0.6
        Assert.Equal(-0.6f, m.Roll, 3);
        Near(AngleAxis(m.Roll, Vector3.UnitZ), m.Rotation("mPelvis", 12.0));
        for (int i = 0; i < 200; i++) m.Step(0.05, 2f, 5f);    // slower than 7 m/s: no roll
        Assert.Equal(0f, m.Roll, 3);
        for (int i = 0; i < 200; i++) m.Step(0.05, 10f, 30f);  // turn rate held at 4, speed factor at -0.6
        Assert.Equal(-2.4f, m.Roll, 3);
    }

    [Fact]
    public void The_roll_follows_its_target_with_a_hundred_millisecond_half_life()
    {
        var m = new FlyAdjustMotion();
        m.Step(0.1, 2f, 11f);   // target -0.6; one half-life covers half
        Assert.Equal(-0.3f, m.Roll, 4);
        m.Step(0.1, 2f, 11f);
        Assert.Equal(-0.45f, m.Roll, 4);
    }

    // ---- eyes ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_blink_closes_over_fifteen_milliseconds_the_right_eye_five_milliseconds_behind_stays_shut_thirty_and_opens_the_same_way()
    {
        var e = new EyeMotion(7);
        Assert.Equal(0f, e.Blink(0.0).Left, 4);
        var (l, r) = e.Blink(0.0075);
        Assert.Equal(0.5f, l, 3);
        Assert.Equal(0.0025f / 0.015f, r, 3);
        Assert.Equal((1f, 1f), e.Blink(0.04));   // shut from 0.02 to 0.05
        (l, r) = e.Blink(0.0575);
        Assert.Equal(0.5f, l, 3);
        Assert.Equal(1f - 0.0025f / 0.015f, r, 3);
        Assert.Equal((0f, 0f), e.Blink(0.1));
    }

    [Fact]
    public void Blinks_come_at_most_eight_seconds_apart_and_are_the_same_for_the_same_seed()
    {
        var a = new EyeMotion(3);
        var b = new EyeMotion(3);
        var other = new EyeMotion(4);
        var starts = new List<double>();
        bool open = true;
        bool differs = false;
        for (double t = 0; t < 600; t += 0.005)
        {
            var (l, _) = a.Blink(t);
            Assert.InRange(l, 0f, 1f);
            Assert.Equal(l, b.Blink(t).Left);
            if (other.Blink(t).Left != l) differs = true;
            if (l > 0f && open) { starts.Add(t); open = false; }
            if (l == 0f) open = true;
        }
        Assert.True(differs, "another seed blinks at other times");
        Assert.InRange(starts.Count, 40, 700);
        for (int i = 1; i < starts.Count; i++) Assert.True(starts[i] - starts[i - 1] <= 8.1, $"blinks {starts[i - 1]} and {starts[i]} are over 8 s apart");
    }

    [Fact]
    public void The_eyes_wander_within_the_official_limits_and_change_over_time()
    {
        var e = new EyeMotion(11);
        var yaws = new HashSet<float>();
        for (double t = 0; t < 300; t += 0.05)
        {
            var (yaw, pitch) = e.Wander(t);
            Assert.InRange(yaw, -(0.08f + 0.15f) - 1e-5f, 0.08f + 0.15f + 1e-5f);
            Assert.InRange(pitch, -(0.015f + 0.12f) - 1e-5f, 0.015f + 0.12f + 1e-5f);
            yaws.Add(yaw);
        }
        Assert.True(yaws.Count > 30, $"only {yaws.Count} different yaws in 300 s");
    }

    [Fact]
    public void Without_a_target_both_eyes_turn_by_the_wander_alone()
    {
        var left = EyeMotion.Aim(null, 0f, Quaternion.Identity, 0.06f, 0.05f, -0.01f, left: true);
        var right = EyeMotion.Aim(null, 0f, Quaternion.Identity, 0.06f, 0.05f, -0.01f, left: false);
        Near(OfficialMath.FromEuler(0f, -0.01f, 0.05f), left);
        Near(left, right);
    }

    [Fact]
    public void With_a_target_the_eyes_aim_at_it_in_head_axes_and_converge_by_the_vergence()
    {
        float interocular = 0.06f, distance = 2f;
        float vergence = -MathF.Atan2(interocular / 2f, distance) + 4f * DegToRad;
        var jitter = OfficialMath.FromEuler(0f, 0.002f, 0.01f);
        var left = EyeMotion.Aim(Vector3.UnitX, distance, Quaternion.Identity, interocular, 0.01f, 0.002f, left: true);
        var right = EyeMotion.Aim(Vector3.UnitX, distance, Quaternion.Identity, interocular, 0.01f, 0.002f, left: false);
        Near(OfficialMath.Then(AngleAxis(vergence, Vector3.UnitZ), jitter), left);
        Near(OfficialMath.Then(AngleAxis(-vergence, Vector3.UnitZ), jitter), right);
        // a target the head has turned away from is held to 0.3 of a quarter turn
        var head = AngleAxis(0.5f, Vector3.UnitZ);
        var held = EyeMotion.Aim(Vector3.UnitX, distance, head, interocular, 0.01f, 0.002f, left: true);
        Near(OfficialMath.Then(OfficialMath.Then(AngleAxis(vergence, Vector3.UnitZ), jitter), AngleAxis(-MathF.PI / 2f * 0.3f, Vector3.UnitZ)), held);
    }

    // ---- head ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void With_nothing_to_look_at_and_an_upright_body_the_head_chain_is_not_turned()
    {
        var h = new HeadRotMotion();
        Assert.Equal(1, h.Priority);
        Assert.False(h.Additive);
        Assert.Equal(1f, h.EaseIn);
        Assert.Equal(1f, h.EaseOut);
        Assert.Equal(["mTorso", "mNeck", "mHead"], h.RotationJoints);
        for (int i = 0; i < 20; i++) h.Step(0.05, null, Quaternion.Identity, Quaternion.Identity, Quaternion.Identity);
        Near(Quaternion.Identity, h.Torso);
        Near(Quaternion.Identity, h.Neck);
        Near(Quaternion.Identity, h.Head);
        Near(Quaternion.Identity, h.Rotation("mHead", 5.0));
    }

    [Fact]
    public void A_target_to_the_left_turns_the_head_chain_by_the_official_lags_and_limit()
    {
        // the target is two metres to the left of an avatar facing +x: a quarter turn, held at 0.8 of a quarter turn
        var limit = AngleAxis(MathF.PI / 2f * 0.8f, Vector3.UnitZ);
        var h = new HeadRotMotion();
        h.Step(0.15, new Vector3(0, 2, 0), Quaternion.Identity, Quaternion.Identity, Quaternion.Identity);
        // one step of 0.15 s: the head follows by half (half-life 0.15 s), the torso by its own interpolant towards 0.35 of the head turn
        var headTurn = OfficialMath.Nlerp(0.5f, Quaternion.Identity, limit);
        var torsoTarget = OfficialMath.Nlerp(0.35f, Quaternion.Identity, limit);
        var torso = OfficialMath.Nlerp((float)(1 - Math.Pow(2, -0.15 / 0.27)), Quaternion.Identity, torsoTarget);
        Near(torso, h.Torso);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, headTurn), h.Neck);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, headTurn), h.Head);
        for (int i = 0; i < 300; i++) h.Step(0.05, new Vector3(0, 2, 0), Quaternion.Identity, Quaternion.Identity, Quaternion.Identity);
        Near(torsoTarget, h.Torso);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, limit), h.Neck);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, limit), h.Head);
    }

    [Fact]
    public void A_target_closer_than_thirty_centimetres_is_looked_at_as_the_pelvis_faces()
    {
        var pelvis = AngleAxis(0.4f, Vector3.UnitZ);
        var h = new HeadRotMotion();
        for (int i = 0; i < 300; i++) h.Step(0.05, new Vector3(0.1f, 0, 0), Quaternion.Identity, pelvis, Quaternion.Identity);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, pelvis), h.Head);
    }

    [Fact]
    public void A_torso_bent_under_the_neck_turns_the_neck_and_head_back_by_half_each()
    {
        // no target: the head is meant to face where the root does, so a chest turned by 0.4 turns the neck and head the other way
        var chest = AngleAxis(0.4f, Vector3.UnitY);
        var h = new HeadRotMotion();
        for (int i = 0; i < 300; i++) h.Step(0.05, null, Quaternion.Identity, Quaternion.Identity, chest);
        var back = Quaternion.Inverse(chest);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, back), h.Neck);
        Near(OfficialMath.Nlerp(0.5f, Quaternion.Identity, back), h.Head);
    }

    // ---- hands -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_hand_pose_asked_for_is_the_one_of_the_highest_priority_animation_a_later_one_winning_a_tie()
    {
        Assert.Null(HandMotion.Request([]));
        Assert.Equal(3, HandMotion.Request([(3, 1)]));
        Assert.Equal(2, HandMotion.Request([(3, 1), (2, 2)]));
        Assert.Equal(3, HandMotion.Request([(3, 2), (2, 1)]));
        Assert.Equal(2, HandMotion.Request([(3, 1), (2, 1)]));
    }

    [Fact]
    public void Hands_start_relaxed_and_blend_to_a_new_pose_over_a_fifth_of_a_second()
    {
        var h = new HandMotion();
        Assert.Equal(1, h.Current);
        Assert.Equal(1f, h.Weight(1));
        Assert.Equal(0f, h.Weight(3));
        h.Step(0.1, 3);   // fist asked for
        Assert.Equal(3, h.Wanted);
        Assert.Equal(1, h.Current);
        Assert.Equal(0.5f, h.Weight(3), 4);
        Assert.Equal(0.5f, h.Weight(1), 4);
        h.Step(0.1, 3);
        Assert.Equal(3, h.Current);
        Assert.Equal(1f, h.Weight(3), 4);
        Assert.Equal(0f, h.Weight(1), 4);
        h.Step(0.016, null);   // nothing asked for: relaxed again
        Assert.Equal(1, h.Wanted);
        Assert.Equal(3, h.Current);
    }

    [Fact]
    public void The_spread_pose_is_the_mesh_as_it_is_and_has_no_morph()
    {
        Assert.Null(HandMotion.MorphName(HandMotion.Spread));
        Assert.Equal("Hands_Relaxed", HandMotion.MorphName(1));
        Assert.Equal("Hands_Fist", HandMotion.MorphName(3));
        Assert.Equal("Hands_Spread_R", HandMotion.MorphName(13));
        var h = new HandMotion();
        h.Step(0.1, HandMotion.Spread);
        h.Step(0.1, HandMotion.Spread);
        Assert.Equal(HandMotion.Spread, h.Current);
        Assert.Equal(0f, h.Weight(1), 4);
    }

    [Fact]
    public void An_animation_asset_states_its_hand_pose()
    {
        var bytes = AvatarTests.Anim(1f, true, 0, 1, [("mChest", [(0f, Quaternion.Identity), (1f, Quaternion.Identity)], [])]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38, 4), 3);
        Assert.Equal(3, AnimationClip.Parse(bytes).HandPose);
    }
}

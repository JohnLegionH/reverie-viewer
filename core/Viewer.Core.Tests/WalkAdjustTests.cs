// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The walk cycle's playback rate against an independent transcription of the official viewer's rule (its
/// llkeyframewalkmotion.cpp walk-adjust update, with llcriticaldamp.cpp's interpolant), the planted foot's speed read from a
/// clip's own ankles, and the playback clock that follows from the rate. Pure; runs in parallel.
/// </summary>
public class WalkAdjustTests
{
    private const double Frame = 1.0 / 60;

    /// <summary>The rule transcribed straight from the official source's lines, in double precision, with no sharing with the
    /// class under test.</summary>
    private sealed class Oracle
    {
        public double Adjusted, Anim;

        public double Step(double dt, double vx, double vy, double heading, double foot)
        {
            dt = Math.Min(Math.Max(dt, 0.001), 2.0);
            double len = Math.Sqrt(vx * vx + vy * vy);
            double speed = Math.Min(Math.Max(len, 0), 8.0);                       // llclamp(avatar_velocity.magVec(), 0, MAX_WALK_PLAYBACK_SPEED)
            if (speed > 0.1)                                                       // MIN_WALK_SPEED
            {
                double minMult = speed >= 1 ? 0.1 : speed * 0.1;                  // clamp_rescale(speed, 0, 1, 0, 0.1)
                double desired = Math.Min(Math.Max(speed / foot, minMult), 1.5);   // llclamp(speed / foot_speed, min, ANIM_SPEED_MAX)
                double t = 1 - Math.Pow(2, -dt / 0.1);                             // SPEED_ADJUST_TIME_CONSTANT
                double next = Adjusted * (1 - t) + desired * t;
                double delta = Math.Min(Math.Max(next - Adjusted, -2 * dt), 2 * dt);   // SPEED_ADJUST_MAX_SEC
                Adjusted += delta;
                double dirForward = (vx * Math.Cos(heading) + vy * Math.Sin(heading)) / len;
                Anim = Adjusted * dirForward;
            }
            else
            {
                double t = 1 - Math.Pow(2, -dt / 0.2);
                Anim = Anim * (1 - t) + 1 * t;
            }
            return Anim;
        }
    }

    [Fact]
    public void The_rate_follows_the_official_rule_over_a_run_of_speeds_directions_and_stops()
    {
        var w = new WalkAdjust();
        var o = new Oracle();
        // standing, a walk, a run, over the 8 m/s cap, sideways, backward, then standing again
        (double Seconds, Vector2 V, float Heading)[] script =
        [
            (0.5, Vector2.Zero, 0f), (2.0, new Vector2(2.46f, 0), 0f), (2.0, new Vector2(4.1f, 0), 0f), (1.0, new Vector2(11f, 0), 0f),
            (1.5, new Vector2(0, 3f), 0f), (1.5, new Vector2(-2.46f, 0), 0f), (0.5, new Vector2(0.05f, 0), 0f), (1.0, Vector2.Zero, 0f),
            (1.5, new Vector2(2.0f, 2.0f), MathF.PI / 2),
        ];
        float foot = 2.9f;
        double worst = 0;
        foreach (var (seconds, v, heading) in script)
            for (double t = 0; t < seconds; t += Frame)
            {
                float expectFoot = foot * MathF.Abs(w.AnimSpeed);   // the foot as the cycle plays now
                double a = w.Update(Frame, v, heading, expectFoot);
                double b = o.Step(Frame, v.X, v.Y, heading, expectFoot);
                worst = Math.Max(worst, Math.Abs(a - b));
            }
        Assert.True(worst < 1e-4, $"the rate differed from the official rule by {worst}");
    }

    [Fact]
    public void A_rate_that_is_the_ratio_settles_at_the_square_root_of_it_as_the_official_code_does_and_one_when_the_foot_matches()
    {
        // the planted foot is read at the rate played, so wanted = speed / (foot * rate): the fixed point is sqrt(speed / foot)
        foreach (var (speed, foot, expect) in new[] { (2.9f, 2.9f, 1.0), (4.1f, 2.9f, Math.Sqrt(4.1 / 2.9)), (1.5f, 2.9f, Math.Sqrt(1.5 / 2.9)) })
        {
            var w = new WalkAdjust();
            for (double t = 0; t < 12; t += Frame) w.Update(Frame, new Vector2(speed, 0), 0f, foot * MathF.Abs(w.AnimSpeed));
            Assert.InRange(w.AnimSpeed, expect - 0.03, expect + 0.03);
        }
    }

    [Fact]
    public void The_rate_never_passes_one_and_a_half_however_slow_the_foot_and_never_falls_below_the_floor()
    {
        var w = new WalkAdjust();
        for (double t = 0; t < 10; t += Frame) Assert.True(w.Update(Frame, new Vector2(8f, 0), 0f, 0.05f) <= WalkAdjust.MaxRate + 1e-6f);
        var slow = new WalkAdjust();
        float lowest = float.MaxValue;
        for (double t = 0; t < 10; t += Frame) lowest = Math.Min(lowest, slow.Update(Frame, new Vector2(0.5f, 0), 0f, 50f));
        Assert.True(lowest >= 0f);
        Assert.InRange(slow.AdjustedSpeed, 0.05f - 1e-4f, 0.06f);   // the floor is a tenth of the speed below 1 m/s
    }

    [Fact]
    public void The_rate_changes_by_at_most_two_a_second_from_a_standing_start()
    {
        var w = new WalkAdjust();
        float last = 0;
        for (double t = 0; t < 2; t += Frame)
        {
            w.Update(Frame, new Vector2(4.1f, 0), 0f, 0.5f);   // wanting the cap at once
            Assert.True(w.AdjustedSpeed - last <= 2 * Frame + 1e-6, $"rose {w.AdjustedSpeed - last} in a frame");
            last = w.AdjustedSpeed;
        }
        Assert.InRange(last, 1.4f, 1.5f);
    }

    [Fact]
    public void Standing_eases_the_rate_back_to_one_with_a_two_tenths_of_a_second_half_life()
    {
        var w = new WalkAdjust();
        for (double t = 0; t < 12; t += Frame) w.Update(Frame, Vector2.Zero, 0f, 1f);   // from 0
        Assert.InRange(w.AnimSpeed, 0.999f, 1.001f);
        var half = new WalkAdjust();
        double elapsed = 0;
        for (; elapsed < 0.2 - 1e-9; elapsed += 0.01) half.Update(0.01, Vector2.Zero, 0f, 1f);
        Assert.InRange(half.AnimSpeed, 0.49f, 0.51f);   // half the way after one half-life
    }

    [Fact]
    public void Sliding_sideways_stops_the_cycle_and_walking_backward_plays_it_in_reverse()
    {
        var side = new WalkAdjust();
        var back = new WalkAdjust();
        for (double t = 0; t < 6; t += Frame)
        {
            side.Update(Frame, new Vector2(0, 2.5f), 0f, 2.5f * MathF.Max(1e-3f, MathF.Abs(side.AnimSpeed)));
            back.Update(Frame, new Vector2(-2.5f, 0), 0f, 2.5f * MathF.Abs(back.AnimSpeed));
        }
        Assert.InRange(side.AnimSpeed, -0.01f, 0.01f);
        Assert.True(back.AnimSpeed < -0.8f, $"backward rate {back.AnimSpeed}");
    }

    [Fact]
    public void A_frame_time_outside_the_official_limits_is_held_to_them()
    {
        var a = new WalkAdjust();
        var b = new WalkAdjust();
        a.Update(0, new Vector2(3f, 0), 0f, 2f);
        b.Update(0.001, new Vector2(3f, 0), 0f, 2f);
        Assert.Equal(b.AnimSpeed, a.AnimSpeed);
        var c = new WalkAdjust();
        var d = new WalkAdjust();
        c.Update(10, new Vector2(3f, 0), 0f, 2f);
        d.Update(2, new Vector2(3f, 0), 0f, 2f);
        Assert.Equal(d.AnimSpeed, c.AnimSpeed);
    }

    // ---- the planted foot, read from a clip ----

    // mPelvis (0,0,1) > mHipLeft (0.1,0.1,-0.05) > mAnkleLeft (0,0,-0.9); the same on the right; no other joint.
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip avatar_mPelvis">
            <bone name="mHipLeft" pos="0.1 0.1 -0.05" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mAnkleLeft" pos="0 0 -0.9" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
            <bone name="mHipRight" pos="0.1 -0.1 -0.05" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mAnkleRight" pos="0 0 -0.9" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = "<linden_avatar></linden_avatar>";

    internal static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));

    /// <summary>A one second cycle in which each ankle goes back from +0.3 to -0.3 m in half of it (so 1.2 m/s) and forward in the other
    /// half, the right half a cycle after the left.</summary>
    internal static AnimationClip Stride(bool loop = true)
    {
        static JointTrack Ankle(string name, float phase)
        {
            // key times are in clip seconds; the foot is at +0.3 at `phase`, at -0.3 half a second later
            float[] t = [0f, 0.5f, 1f];
            float a = phase < 0.5f ? 0.3f - 0.6f * phase / 0.5f : -0.3f + 0.6f * (phase - 0.5f) / 0.5f;   // the value at time 0
            float mid = -a;
            return new JointTrack(name, 1, [], [], t, [new Vector3(a, 0, 0), new Vector3(mid, 0, 0), new Vector3(a, 0, 0)]);
        }
        return new AnimationClip
        {
            Duration = 1f, Loop = loop, LoopIn = 0f, LoopOut = 1f, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "",
            Tracks = [Ankle("mAnkleLeft", 0f), Ankle("mAnkleRight", 0.5f)],
        };
    }

    [Fact]
    public void The_planted_foot_speed_of_a_clip_is_read_from_its_ankles()
    {
        var table = FootSpeedTable.Of(Rig(), Stride());
        Assert.NotNull(table);
        // one ankle is always moving back at 1.2 m/s (the other forward at the same speed), away from the turning points
        Assert.InRange(table!.At(0.25f), 1.15f, 1.25f);
        Assert.InRange(table.At(0.75f), 1.15f, 1.25f);
        Assert.InRange(table.Mean, 1.1f, 1.25f);
        Assert.InRange(table.Peak, 1.15f, 1.3f);
    }

    [Fact]
    public void A_skeleton_without_ankles_or_a_clip_without_length_has_no_table()
    {
        var empty = new AnimationClip { Duration = 0f, Loop = true, LoopIn = 0, LoopOut = 0, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "", Tracks = [] };
        Assert.Null(FootSpeedTable.Of(Rig(), empty));
    }

    // ---- the playback clock ----

    [Fact]
    public void An_avatar_without_a_walk_motion_keeps_the_real_clock()
    {
        var p = new WalkPlayback();
        p.Start(0, Stride(), null);
        for (double t = 0; t < 3; t += Frame) p.Step(t, new Vector2(4f, 0), 0f);
        Assert.Equal(0, p.Skew);
        Assert.Equal(1f, p.Rate);
    }

    [Fact]
    public void A_walking_avatar_faster_than_its_cycle_gets_its_clock_moved_ahead_and_a_standing_one_does_not()
    {
        var clip = Stride();
        var table = FootSpeedTable.Of(Rig(), clip);
        var walking = new WalkPlayback();
        walking.Start(0, clip, table);
        for (double t = 0; t < 6; t += Frame) walking.Step(t, new Vector2(2.9f, 0), 0f);   // 2.9 m/s against a 1.2 m/s foot: wants the cap
        Assert.InRange(walking.Rate, 1.3f, 1.5f);
        Assert.True(walking.Skew > 0.5, $"skew {walking.Skew}");
        var standing = new WalkPlayback();
        standing.Start(0, clip, table);
        for (double t = 0; t < 6; t += Frame) standing.Step(t, Vector2.Zero, 0f);
        Assert.InRange(standing.Rate, 0.99f, 1.01f);   // the rate eases from 0 up to 1 when nothing moves
        Assert.InRange(standing.Skew, -0.35, -0.2);     // and the clock ran slow only while it did: the integral of (rate - 1) is -0.2 / ln 2
    }

    [Fact]
    public void A_frame_at_the_same_time_as_the_last_one_moves_nothing_when_a_stalled_clock_is_ignored()
    {
        var clip = Stride();
        var table = FootSpeedTable.Of(Rig(), clip);
        var ignoring = new WalkPlayback();
        var counting = new WalkPlayback();
        foreach (var p in new[] { ignoring, counting }) { p.Start(0, clip, table); p.Step(0, Vector2.Zero, 0f); }
        for (int i = 0; i < 20; i++) { ignoring.Step(0, Vector2.Zero, 0f, ignoreStalledClock: true); counting.Step(0, Vector2.Zero, 0f); }
        Assert.Equal(0, ignoring.Skew);   // a test that holds its clock still gets the same cycle however many frames it runs
        Assert.NotEqual(0, counting.Skew);   // (the default keeps the official viewer's minimum frame step, which a running clock never meets)
        Assert.Equal(0f, ignoring.Rate);
    }

    [Fact]
    public void Starting_a_new_motion_resets_the_rate_and_the_clock()
    {
        var clip = Stride();
        var table = FootSpeedTable.Of(Rig(), clip);
        var p = new WalkPlayback();
        p.Start(0, clip, table);
        for (double t = 0; t < 3; t += Frame) p.Step(t, new Vector2(2.9f, 0), 0f);
        Assert.NotEqual(0, p.Skew);
        p.Start(3, clip, table);
        Assert.Equal(0, p.Skew);
        Assert.Equal(0f, p.Rate);
    }

    [Fact]
    public void A_looping_clip_played_in_reverse_wraps_instead_of_holding_at_its_start()
    {
        var clip = Stride();
        Assert.Equal(0.75f, clip.TimeAt(-0.25), 4);
        Assert.Equal(0.1f, clip.TimeAt(-0.9), 4);
    }
}

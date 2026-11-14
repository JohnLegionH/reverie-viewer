// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The wind sound against the official viewer's rules (llvieweraudio.cpp audio_update_wind; llaudioengine.cpp mapWindVecToGain, Pitch and
/// Pan; llaudioengine_openal.cpp updateWind; llwindgen.h): the wind felt, its gain, pan, pitch and centre frequency, the fade with
/// flying, the filter's coefficients and the generator's output.
/// </summary>
public class WindSoundTests
{
    private static void Near(float expected, float actual, float tol = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void The_wind_felt_is_half_the_regions_wind_less_the_agents_velocity_in_the_agents_frame()
    {
        var felt = WindSound.Relative(new Vector3(4, 0, 0), new Vector3(10, 0, 0), Quaternion.Identity);
        Near(-8f, felt.X); Near(0f, felt.Y); Near(0f, felt.Z);
        // facing north (+y) and moving north at 10 m/s in still air: the wind comes from straight ahead
        var north = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var ahead = WindSound.Relative(Vector3.Zero, new Vector3(0, 10, 0), north);
        Near(-10f, ahead.X); Near(0f, ahead.Y); Near(0f, ahead.Z);
    }

    [Fact]
    public void Gain_is_the_speed_over_twenty_and_rises_with_flying_speed_up_to_one()
    {
        Near(0.4f, WindSound.Gain(new Vector3(-8, 0, 0)));
        Near(1f, WindSound.Gain(new Vector3(30, 0, 0)));
        Near(0f, WindSound.Gain(Vector3.Zero));
        float last = -1f;
        foreach (float speed in new[] { 2f, 5f, 10f, 15f, 20f })
        {
            float g = WindSound.Gain(WindSound.Relative(new Vector3(1, 1, 0), new Vector3(speed, 0, 0), Quaternion.Identity));
            Assert.True(g > last, $"gain {g} at {speed} m/s not above {last}");
            last = g;
        }
        Near(1f, WindSound.Gain(WindSound.Relative(Vector3.Zero, new Vector3(40, 0, 0), Quaternion.Identity)));
    }

    [Fact]
    public void Pan_and_pitch_follow_the_angle_to_the_listeners_right()
    {
        // the listener's right is -y in the agent's frame
        Near(0f, WindSound.Pan(new Vector3(0, -5, 0)));
        Near(1f, WindSound.Pan(new Vector3(0, 5, 0)));
        Near(0.5f, WindSound.Pan(new Vector3(-8, 0, 0)));
        Near(0f, WindSound.Pitch(new Vector3(0, -5, 0)));
        Near(0f, WindSound.Pitch(new Vector3(0, 5, 0)));
        Near(0.5f, WindSound.Pitch(new Vector3(-8, 0, 0)));
        Near(0.25f, WindSound.Pan(new Vector3(-3, -3, 0)));
        // no wind: the angle to the right is a right angle
        Near(0.5f, WindSound.Pan(Vector3.Zero));
        Near(0.5f, WindSound.Pitch(Vector3.Zero));
    }

    [Fact]
    public void The_centre_frequency_is_eighty_hertz_raised_by_pitch_and_gain()
    {
        Near(80f * MathF.Pow(1.5f, 2.5f * 1.4f), WindSound.CentreFrequency(new Vector3(-8, 0, 0)), 1e-2f);
        Near(80f, WindSound.CentreFrequency(new Vector3(0, -5, 0)), 1e-3f);
        Near(80f * MathF.Pow(1.5f, 5f), WindSound.CentreFrequency(new Vector3(30, 0, 0)), 1e-2f);
    }

    [Fact]
    public void The_loudest_fades_up_to_master_times_ambient_while_flying_and_down_to_silence_otherwise_over_two_seconds()
    {
        Near(0.125f, WindSound.StepMaxGain(0f, flying: true, master: 1f, ambient: 0.5f, seconds: 0.5f));
        Near(0.5f, WindSound.StepMaxGain(0.4f, flying: true, master: 1f, ambient: 0.5f, seconds: 1f));
        Near(0.25f, WindSound.StepMaxGain(0.5f, flying: false, master: 1f, ambient: 0.5f, seconds: 1f));
        Near(0f, WindSound.StepMaxGain(0.1f, flying: false, master: 1f, ambient: 0.5f, seconds: 1f));
        // the ambient slider (or a mute) at 0 silences it
        Near(0f, WindSound.StepMaxGain(0.3f, flying: true, master: 1f, ambient: 0f, seconds: 0.5f));
        Near(0.4f, WindSound.StepMaxGain(0f, flying: true, master: 0.4f, ambient: 1f, seconds: 5f));
    }

    [Fact]
    public void The_filter_is_a_fifty_hertz_wide_resonator_at_the_centre_frequency()
    {
        double period = 2.0 / 44100.0;
        double b2 = Math.Exp(-2 * Math.PI * 50 * period);
        foreach (float f in new[] { 80f, 100f, 300f })
        {
            double b1 = -4 * b2 / (1 + b2) * Math.Cos(2 * Math.PI * f * period);
            double a0 = (1 - b2) * Math.Sqrt(1 - b1 * b1 / (4 * b2));
            var (A0, B1, B2) = WindGenerator.Coefficients(f);
            Near((float)a0, A0, 1e-6f); Near((float)b1, B1, 1e-5f); Near((float)b2, B2, 1e-6f);
        }
    }

    [Fact]
    public void The_regions_wind_at_a_point_is_its_cell_of_the_sixteen_by_sixteen_grid()
    {
        var x = new float[256];
        var y = new float[256];
        x[0] = 1; y[0] = 2;               // south-west corner
        x[15] = 3; y[15] = -1;            // south-east corner
        x[16 * 15 + 3] = -4; y[16 * 15 + 3] = 5;   // the top row, fourth cell
        var wind = new Viewer.Core.World.RegionWind(x, y);
        Assert.Equal(new Vector3(1, 2, 0), wind.At(3, 4, 256, 256));
        Assert.Equal(new Vector3(3, -1, 0), wind.At(255, 0, 256, 256));
        Assert.Equal(new Vector3(-4, 5, 0), wind.At(50, 250, 256, 256));
        // a larger region spreads the same grid over its size, and a point outside takes the nearest edge cell
        Assert.Equal(new Vector3(-4, 5, 0), wind.At(200, 1000, 1024, 1024));
        Assert.Equal(new Vector3(1, 2, 0), wind.At(-5, -5, 256, 256));
    }

    [Fact]
    public void A_walk_of_the_own_avatar_is_summed_up_in_one_log_line_when_it_ends()
    {
        var walk = new WalkSummary();
        Assert.Null(walk.End());   // no walk, no line
        walk.Frame(0.2f, 0.01f, 0.11f, 2.2f, 1.9f, stepped: true, onLand: true);
        walk.Frame(-0.03f, 0.3f, 0.12f, 2.2f, 1.9f, stepped: false, onLand: true);
        walk.Frame(float.NaN, 0.1f, 0.12f, 2.2f, 1.9f, stepped: true, onLand: false);
        var fields = walk.End()!.ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal(3, fields["frames"]);
        Assert.Equal(2, fields["steps"]);
        Assert.Equal(-0.03, (double)fields["left_low"]!, 3);
        Assert.Equal(0.2, (double)fields["left_high"]!, 3);
        Assert.Equal(0.01, (double)fields["right_low"]!, 3);
        Assert.Equal(0.3, (double)fields["right_high"]!, 3);
        Assert.Equal(0.12, (double)fields["gap"]!, 3);
        Assert.Equal(2.2, (double)fields["body_height"]!, 3);
        Assert.Equal(1.9, (double)fields["region_height"]!, 3);
        Assert.Equal(2, fields["on_land_frames"]);
        Assert.Null(walk.End());   // it starts afresh
    }

    private static (double Left, double Right) Rms(WindGenerator g, int frames, bool mono)
    {
        var l = new float[frames];
        var r = new float[frames];
        g.Fill(l, r, mono);
        double sl = 0, sr = 0;
        for (int i = 0; i < frames; i++) { sl += l[i] * l[i]; sr += r[i] * r[i]; }
        return (Math.Sqrt(sl / frames), Math.Sqrt(sr / frames));
    }

    [Fact]
    public void The_generator_is_silent_at_no_gain_louder_with_more_gain_and_eases_its_gain_sample_by_sample()
    {
        var quiet = new WindGenerator { TargetGain = 0f };
        var (ql, qr) = Rms(quiet, 44100, false);
        Assert.Equal(0d, ql); Assert.Equal(0d, qr);

        var soft = new WindGenerator { TargetGain = 0.2f, TargetFrequency = 120f };
        var loud = new WindGenerator { TargetGain = 0.8f, TargetFrequency = 120f };
        Rms(soft, 44100, false); Rms(loud, 44100, false);   // eased in
        var (sl, sr) = Rms(soft, 44100, false);
        var (ll, lr) = Rms(loud, 44100, false);
        Assert.True(sl > 0 && sr > 0, "the soft wind is heard");
        Assert.True(ll + lr > 2.5 * (sl + sr), $"loud {ll + lr} against soft {sl + sr}");

        // the gain eases by a thousandth of the way per synthesized sample (two frames each)
        var g = new WindGenerator { TargetGain = 1f };
        g.Fill(new float[2000], new float[2000], false);
        Near(1f - MathF.Pow(0.999f, 1000), g.Gain, 1e-3f);
    }

    [Fact]
    public void Pan_sends_the_sound_to_one_side_and_mono_puts_it_on_both()
    {
        var right = new WindGenerator { TargetGain = 0.8f, TargetPanRight = 1f, TargetFrequency = 120f };
        Rms(right, 88200, false);
        var (l, r) = Rms(right, 44100, false);
        Assert.True(r > 0 && l < 0.05 * r, $"left {l} right {r}");

        var mono = new WindGenerator { TargetGain = 0.8f, TargetPanRight = 1f, TargetFrequency = 120f };
        var left = new float[44100];
        var rightSide = new float[44100];
        mono.Fill(left, rightSide, true);
        Assert.Equal(left, rightSide);
        Assert.Contains(left, s => s != 0f);
    }
}

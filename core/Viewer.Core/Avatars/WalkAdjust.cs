// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// The walk cycle's playback rate, as the official viewer sets it (behaviour taken from its llkeyframewalkmotion.cpp, the
/// walk-adjust motion's onUpdate, and llcriticaldamp.cpp's interpolant, written down in our own words first):
/// each frame the avatar's ground speed (its velocity in the ground plane, at most 8 m/s) is compared with the speed of the
/// planted foot, the foot that is pushing back faster, as the cycle plays now; the cycle's rate is moved towards the ratio of
/// the two, kept between a floor and 1.5, eased with a 0.1 second half-life, and allowed to change by at most 2 a second. The
/// rate is then signed by how the movement lies against the way the avatar faces (1 walking forward, 0 sliding sideways,
/// negative walking backward, the cycle playing in reverse). Below 0.1 m/s (standing or turning in place) the rate eases back to 1 with a
/// 0.2 second half-life. The planted foot's speed is the cycle's own at the rate it was played at the frame before, so the
/// rate settles where the foot would move at the ratio's square root of the avatar's speed, which is what the official code
/// does; <see cref="FootSpeedTable"/> gives the cycle's foot speed at rate 1.
/// A half-life <c>h</c> over <c>dt</c> seconds moves a value that fraction of the way: <c>1 - 2^(-dt/h)</c>
/// (llcriticaldamp.cpp, LLSmoothInterpolation::calcInterpolant). Not thread-safe.
/// </summary>
public sealed class WalkAdjust
{
    /// <summary>The ground speed above which the cycle is matched to it; the fastest speed it is matched to.</summary>
    public const float MinWalkSpeed = 0.1f, MaxSpeed = 8f;
    /// <summary>The cycle's rate never goes above this (the official ANIM_SPEED_MAX).</summary>
    public const float MaxRate = 1.5f;
    /// <summary>The most the rate changes in a second.</summary>
    public const float MaxRateChangePerSecond = 2f;
    /// <summary>Half-lives (seconds) of the move towards the wanted rate while moving, and of the return to 1 while standing.</summary>
    public const float AdjustHalfLife = 0.1f, StandHalfLife = 0.2f;
    /// <summary>The frame time is kept between these (seconds).</summary>
    public const double MinFrameSeconds = 0.001, MaxFrameSeconds = 2.0;

    /// <summary>The rate before the direction is applied, and the rate the cycle is played at now.</summary>
    public float AdjustedSpeed { get; private set; }
    public float AnimSpeed { get; private set; }

    /// <summary>A walk motion starts: both rates are 0, as the official walk-adjust motion's activation sets them.</summary>
    public void Reset() => AdjustedSpeed = AnimSpeed = 0;

    /// <summary>The fraction of the way a value with half-life <paramref name="halfLife"/> moves in <paramref name="dt"/> seconds.</summary>
    public static float Interpolant(double dt, float halfLife) => Math.Clamp(1f - MathF.Pow(2f, -(float)dt / halfLife), 0f, 1f);

    /// <summary>One frame. <paramref name="velocity"/>: the avatar's velocity on the ground plane (m/s); <paramref name="heading"/>:
    /// the way it faces (radians about up from east); <paramref name="plantedFootSpeed"/>: the speed (m/s, as the cycle is
    /// playing now) of the planted foot relative to the avatar. Returns the rate to play the cycle at.</summary>
    public float Update(double deltaSeconds, Vector2 velocity, float heading, float plantedFootSpeed)
    {
        float dt = (float)Math.Clamp(deltaSeconds, MinFrameSeconds, MaxFrameSeconds);
        float length = velocity.Length();
        float speed = Math.Clamp(length, 0f, MaxSpeed);
        if (speed > MinWalkSpeed)
        {
            float floor = Math.Clamp(speed, 0f, 1f) * 0.1f;   // clamp_rescale(speed, 0, 1, 0, 0.1)
            float wanted = plantedFootSpeed > 1e-6f ? Math.Clamp(speed / plantedFootSpeed, floor, MaxRate) : MaxRate;
            float next = AdjustedSpeed + (wanted - AdjustedSpeed) * Interpolant(dt, AdjustHalfLife);
            AdjustedSpeed += Math.Clamp(next - AdjustedSpeed, -MaxRateChangePerSecond * dt, MaxRateChangePerSecond * dt);
            var direction = velocity / length;
            float factor = direction.X * MathF.Cos(heading) + direction.Y * MathF.Sin(heading);   // the movement's forward part in the avatar's frame
            AnimSpeed = AdjustedSpeed * factor;
        }
        else
            AnimSpeed += (1f - AnimSpeed) * Interpolant(dt, StandHalfLife);
        return AnimSpeed;
    }
}

/// <summary>
/// How fast the planted foot moves back against the avatar when a cycle plays at rate 1, for each point of the cycle: of the
/// two ankles, the one moving back faster along the avatar's forward axis (the pelvis-frame X) at that point, taken from the
/// clip's own joint motion (<see cref="AvatarJoints"/>). The official viewer measures the same thing from the animated feet
/// frame by frame; a clip's feet do not change, so it is read once. Zero or negative never appears (a foot moving forward is
/// not planted): the table holds at least 0.
/// </summary>
public sealed class FootSpeedTable
{
    public const int Samples = 64;
    private readonly float[] _speed;
    private readonly float _start, _period;

    private FootSpeedTable(float[] speed, float start, float period)
    {
        _speed = speed;
        _start = start;
        _period = period;
        Mean = speed.Average();
        Peak = speed.Max();
    }

    /// <summary>The mean and the largest of the planted foot's speed over the cycle (m/s).</summary>
    public float Mean { get; }
    public float Peak { get; }

    /// <summary>The speed at clip time <paramref name="clipTime"/>, as the cycle repeats.</summary>
    public float At(float clipTime)
    {
        float x = (clipTime - _start) / _period;
        x -= MathF.Floor(x);
        float at = x * Samples;
        int i = (int)at % Samples;
        float f = at - MathF.Floor(at);
        return _speed[i] + (_speed[(i + 1) % Samples] - _speed[i]) * f;
    }

    /// <summary>The table of <paramref name="clip"/> on <paramref name="rig"/>; null when the skeleton has no ankles or the
    /// clip has no length. The cycle is the loop from its loop-in to its loop-out point (the whole clip when it does not loop).</summary>
    public static FootSpeedTable? Of(AvatarRig rig, AnimationClip clip)
    {
        int left = rig.Find("mAnkleLeft"), right = rig.Find("mAnkleRight");
        if (left < 0 || right < 0 || clip.Duration <= 0) return null;
        float start = clip.Loop ? Math.Clamp(clip.LoopIn, 0, clip.Duration) : 0;
        float end = clip.Loop ? Math.Clamp(clip.LoopOut, start, clip.Duration) : clip.Duration;
        float period = end - start;
        if (period < 1e-3f) return null;
        var tracks = AvatarJoints.TracksFor(rig, clip);
        var joints = new AvatarJoints(rig);
        var l = new float[Samples];
        var r = new float[Samples];
        for (int i = 0; i < Samples; i++)
        {
            joints.Begin(tracks, start + period * i / Samples);
            l[i] = Vector3.Transform(Vector3.Zero, joints.World(left)).X;
            r[i] = Vector3.Transform(Vector3.Zero, joints.World(right)).X;
        }
        float step = 2f * period / Samples;
        var speed = new float[Samples];
        for (int i = 0; i < Samples; i++)
        {
            int next = (i + 1) % Samples, previous = (i + Samples - 1) % Samples;
            float backLeft = -(l[next] - l[previous]) / step, backRight = -(r[next] - r[previous]) / step;
            speed[i] = Math.Max(0f, Math.Max(backLeft, backRight));
        }
        return new FootSpeedTable(speed, start, period);
    }
}

/// <summary>
/// One avatar's walk-cycle playback: which clip's cycle is playing, how far its clock has been moved off the real one by the
/// rate (<see cref="Skew"/>, seconds, to be added to the playing time), and the <see cref="WalkAdjust"/> that sets the rate.
/// Without a table (the avatar plays no walk or run motion) the clock is the real one. No engine type; main thread only.
/// </summary>
public sealed class WalkPlayback
{
    private readonly WalkAdjust _adjust = new();
    private FootSpeedTable? _table;
    private AnimationClip? _clip;
    private double _startedAt, _lastAt = double.NaN;

    /// <summary>Seconds the cycle's clock is ahead of (or behind, when negative) the real one.</summary>
    public double Skew { get; private set; }

    /// <summary>The rate the cycle is played at now (1 when no walk motion plays).</summary>
    public float Rate => _table is null ? 1f : _adjust.AnimSpeed;

    public bool Active => _table is not null;

    /// <summary>The avatar plays <paramref name="clip"/> from <paramref name="now"/> (a walk or run motion has a
    /// <paramref name="table"/>; any other animation, or none, has null).</summary>
    public void Start(double now, AnimationClip? clip, FootSpeedTable? table)
    {
        _clip = clip;
        _table = table;
        _startedAt = now;
        _lastAt = double.NaN;
        Skew = 0;
        _adjust.Reset();
    }

    /// <summary>One frame at <paramref name="now"/>: the avatar's ground velocity and the way it faces. Returns <see cref="Skew"/>.</summary>
    public double Step(double now, Vector2 velocity, float heading, bool ignoreStalledClock = false)
    {
        if (_table is null || _clip is null) return Skew = 0;
        if (double.IsNaN(_lastAt)) { _lastAt = now; return Skew; }
        double dt = now - _lastAt;
        if (ignoreStalledClock && dt <= 0) return Skew;   // no time has passed: a frame adds nothing (only a test holds its clock still; a running one always moves)
        _lastAt = now;
        float clipTime = _clip.TimeAt(now - _startedAt + Skew);
        float planted = _table.At(clipTime) * MathF.Abs(_adjust.AnimSpeed);
        float rate = _adjust.Update(dt, velocity, heading, planted);
        Skew += (rate - 1.0) * Math.Clamp(dt, WalkAdjust.MinFrameSeconds, WalkAdjust.MaxFrameSeconds);
        return Skew;
    }
}

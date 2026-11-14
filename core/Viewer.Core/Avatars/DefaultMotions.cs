// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @File llvoavatar.cpp
 * @brief Implementation of LLVOAvatar class which is a derivation of LLViewerObject
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file llheadrotmotion.cpp
 * @brief Implementation of LLHeadRotMotion class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file llhandmotion.cpp
 * @brief Implementation of LLHandMotion class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file llkeyframewalkmotion.cpp
 * @brief Implementation of LLKeyframeWalkMotion class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds. The body sway, breathing and pelvis fix motions (llvoavatar.cpp), the eye and head motions (llheadrotmotion.cpp), the hand pose
// blend (llhandmotion.cpp) and the flying roll (llkeyframewalkmotion.cpp) follow the files named above, rewritten in C# for our own types: the constants, the
// joints each motion animates, their priorities and blend types, and the formulas. The eye timings are run forward from a seeded generator instead of read from
// real-time timers each frame. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.Avatars;

// The motions the official viewer starts on every avatar (llvoavatar.cpp startDefaultMotions) and the one it adds while the avatar flies
// (processAnimationStateChanges). Behaviour taken from the official viewer's llvoavatar.cpp (LLBodyNoiseMotion, LLBreatheMotionRot,
// LLPelvisFixMotion), llcharacter/llheadrotmotion.cpp (LLHeadRotMotion, LLEyeMotion), llcharacter/llhandmotion.cpp and
// llcharacter/llkeyframewalkmotion.cpp (LLFlyAdjustMotion), written down in our own words and rewritten in C#. Joint priorities follow the
// asset numbering: 0 low, 1 medium, 2 high, 3 higher.

/// <summary>Base of the built-in motions: nothing turned, nothing moved, a medium priority that blends normally.</summary>
public abstract class ProceduralMotionBase : IProceduralMotion
{
    public abstract string Name { get; }
    public virtual int Priority => 1;
    public virtual bool Additive => false;
    public virtual float EaseIn => 0f;
    public virtual float EaseOut => 0f;
    public virtual IReadOnlyList<string> RotationJoints => [];
    public virtual IReadOnlyList<string> PositionJoints => [];
    public virtual Quaternion Rotation(string joint, double t) => Quaternion.Identity;
    public virtual Vector3 Position(string joint, double t) => Vector3.Zero;
}

/// <summary>The torso's slow random sway: about a degree about x and y from two noise tracks (llvoavatar.cpp LLBodyNoiseMotion), additive at high priority.</summary>
public sealed class BodyNoiseMotion : ProceduralMotionBase
{
    /// <summary>Degrees of deviation from up, and the time scale of the noise.</summary>
    public const float Amount = 1.0f, Speed = 0.2f;

    private static readonly System.Collections.Immutable.ImmutableArray<string> Torso = ["mTorso"];
    public override string Name => "body_noise";
    public override int Priority => 2;
    public override bool Additive => true;
    public override IReadOnlyList<string> RotationJoints => Torso;

    public override Quaternion Rotation(string joint, double t)
    {
        float x = (float)t * Speed;
        float noiseX = OfficialNoise.Noise2(x, 0f), noiseY = OfficialNoise.Noise2(0f, x);
        float rx = Amount * (MathF.PI / 180f) * noiseX / 0.42f, ry = Amount * (MathF.PI / 180f) * noiseY / 0.42f;
        return OfficialMath.FromEuler(rx, ry, 0f);
    }
}

/// <summary>The chest turned about y by sin(time) times 0.05 radians (llvoavatar.cpp LLBreatheMotionRot), at medium priority.</summary>
public sealed class BreatheMotion : ProceduralMotionBase
{
    public const float Strength = 0.05f;
    private static readonly System.Collections.Immutable.ImmutableArray<string> Chest = ["mChest"];
    public override string Name => "breathe_rot";
    public override int Priority => 1;
    public override IReadOnlyList<string> RotationJoints => Chest;
    public override Quaternion Rotation(string joint, double t) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.Sin(t) * Strength);
}

/// <summary>The pelvis held at zero, at low priority, so an animation that does not move it leaves it in place (llvoavatar.cpp LLPelvisFixMotion).</summary>
public sealed class PelvisFixMotion : ProceduralMotionBase
{
    private static readonly System.Collections.Immutable.ImmutableArray<string> Pelvis = ["mPelvis"];
    public override string Name => "pelvis_fix";
    public override int Priority => 0;
    public override float EaseIn => 0.5f;
    public override float EaseOut => 0.5f;
    public override IReadOnlyList<string> PositionJoints => Pelvis;
}

/// <summary>
/// The roll of a flying avatar into a turn (llkeyframewalkmotion.cpp LLFlyAdjustMotion): the pelvis is turned about z by a roll that follows,
/// with a 100 ms half-life, the avatar's turn rate (held to 4 rad/s) times a factor that grows from 0 at 7 m/s to -0.6 at 15 m/s. Additive at
/// higher priority. The official viewer started it when the avatar is in the air and not sitting and stops it otherwise.
/// </summary>
public sealed class FlyAdjustMotion : ProceduralMotionBase
{
    public const float MaxRoll = 0.6f, MaxTurnRate = 4f, HalfLife = 0.1f;
    private static readonly System.Collections.Immutable.ImmutableArray<string> Pelvis = ["mPelvis"];
    private volatile float _roll;

    public override string Name => "fly_adjust";
    public override int Priority => 3;
    public override bool Additive => true;
    public override IReadOnlyList<string> RotationJoints => Pelvis;
    public override IReadOnlyList<string> PositionJoints => Pelvis;

    /// <summary>The roll now, in radians.</summary>
    public float Roll => _roll;

    /// <summary>The motion starts: no roll (onActivate).</summary>
    public void Reset() => _roll = 0f;

    /// <summary>One frame: <paramref name="angularVelocityZ"/> is the avatar's turn rate about up (rad/s), <paramref name="speed"/> its speed (m/s).</summary>
    public void Step(double dt, float angularVelocityZ, float speed)
    {
        float factor = OfficialMath.ClampRescale(speed, 7f, 15f, 0f, -MaxRoll);
        float target = Math.Clamp(angularVelocityZ, -MaxTurnRate, MaxTurnRate) * factor;
        _roll += (target - _roll) * OfficialMath.Interpolant(dt, HalfLife);
    }

    public override Quaternion Rotation(string joint, double t) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _roll);
}

/// <summary>
/// The eyes' blinks and wander (llheadrotmotion.cpp LLEyeMotion). Every avatar's eyes keep the official timings: a jitter (a small turn of the
/// eyes) every 0.3 to 2.5 s, a look-away (a larger turn that is later undone) every 5 to 15 s and back after 1 to 5 s, a blink every 0.5 to
/// 8 s, and a blink with nine of ten look-aways. The official code reads real-time timers once a frame; here the same rules are run forward
/// from the start with a seeded generator, so what the eyes do is a function of the time since the motion started and the same however the
/// frames fall. A look-away that lands while a blink is under way does not start another (the official code would start that blink over).
/// </summary>
public sealed class EyeMotion : ProceduralMotionBase
{
    public const float JitterMin = 0.3f, JitterMax = 2.5f, JitterYaw = 0.08f, JitterPitch = 0.015f;
    public const float LookAwayMin = 5f, LookAwayMax = 15f, LookBackMin = 1f, LookBackMax = 5f, LookAwayYaw = 0.15f, LookAwayPitch = 0.12f;
    public const float BlinkMin = 0.5f, BlinkMax = 8f, BlinkClose = 0.03f, BlinkSpeed = 0.015f, BlinkDelta = 0.005f;
    public const float RotLimit = MathF.PI / 2f * 0.3f;
    /// <summary>The frame time the rules are run at (the official code runs them once a frame).</summary>
    private const double Frame = 1.0 / 60.0;
    /// <summary>A blink's whole length: the close, the time shut, and the open, the right eye following the left by <see cref="BlinkDelta"/>.</summary>
    private const double BlinkLength = BlinkDelta + BlinkSpeed + BlinkClose + BlinkDelta + BlinkSpeed;

    private static readonly System.Collections.Immutable.ImmutableArray<string> Eyes = ["mEyeLeft", "mFaceEyeAltLeft", "mEyeRight", "mFaceEyeAltRight"];

    private readonly object _lock = new();
    private readonly Random _random;
    private readonly List<(double T, float Yaw, float Pitch)> _wander = [];
    private readonly List<double> _blinks = [0.0];
    // the jitter timer's state, run forward
    private double _cursor, _jitterStart;
    private double _jitterTime, _lookAwayTime;
    private float _jitterYaw, _jitterPitch, _awayYaw, _awayPitch;
    private double _blinkNext;
    private double _horizon = -1;

    public EyeMotion(int seed = 0)
    {
        _random = new Random(seed);
        _blinkNext = BlinkLength + BlinkMin + _random.NextDouble() * (BlinkMax - BlinkMin);
        _wander.Add((double.NegativeInfinity, 0f, 0f));
    }

    public override string Name => "eye_rot";
    public override int Priority => 1;
    public override float EaseIn => 0.5f;
    public override float EaseOut => 0.5f;
    public override IReadOnlyList<string> RotationJoints => Eyes;

    private float Frand() => (float)_random.NextDouble();

    /// <summary>Runs the rules forward to <paramref name="until"/>.</summary>
    private void Extend(double until)
    {
        if (until <= _horizon) return;
        double target = until + 30.0;
        while (_cursor <= target)
        {
            double elapsed = _cursor - _jitterStart;
            if (elapsed > _jitterTime)
            {
                _jitterTime = JitterMin + Frand() * (JitterMax - JitterMin);
                _jitterYaw = (Frand() * 2f - 1f) * JitterYaw;
                _jitterPitch = (Frand() * 2f - 1f) * JitterPitch;
                _lookAwayTime -= Math.Max(0.0, elapsed);
                _jitterStart = _cursor;
                Record();
            }
            else if (elapsed > _lookAwayTime)
            {
                bool blink = Frand() > 0.1f;
                if (blink) BlinkNow(_cursor);
                if (_awayYaw == 0f && _awayPitch == 0f)
                {
                    _awayYaw = (Frand() * 2f - 1f) * LookAwayYaw;
                    _awayPitch = (Frand() * 2f - 1f) * LookAwayPitch;
                    _lookAwayTime = LookBackMin + Frand() * (LookBackMax - LookBackMin);
                }
                else
                {
                    _awayYaw = 0f;
                    _awayPitch = 0f;
                    _lookAwayTime = LookAwayMin + Frand() * (LookAwayMax - LookAwayMin);
                }
                Record();
            }
            FlushBlinks(_cursor);
            _cursor += Frame;
        }
        _horizon = _cursor;
    }

    private void Record()
    {
        float yaw = _jitterYaw + _awayYaw, pitch = _jitterPitch + _awayPitch;
        if (_wander.Count > 0 && _wander[^1].T == _cursor) _wander[^1] = (_cursor, yaw, pitch);
        else _wander.Add((_cursor, yaw, pitch));
    }

    /// <summary>A look-away asks for a blink now: it starts when the eyes are open and waiting, and brings the next one forward.</summary>
    private void BlinkNow(double at)
    {
        double last = _blinks[^1];
        if (at >= last + BlinkLength && at < _blinkNext) _blinkNext = at;
    }

    /// <summary>Blinks that have fallen due by <paramref name="now"/> are started, and the next waited for.</summary>
    private void FlushBlinks(double now)
    {
        while (_blinkNext <= now)
        {
            _blinks.Add(_blinkNext);
            _blinkNext += BlinkLength + BlinkMin + _random.NextDouble() * (BlinkMax - BlinkMin);
        }
    }

    /// <summary>The weights of the Blink_Left and Blink_Right morphs at <paramref name="t"/> seconds.</summary>
    public (float Left, float Right) Blink(double t)
    {
        if (t < 0) return (0f, 0f);
        double start;
        lock (_lock)
        {
            Extend(t);
            int lo = 0, hi = _blinks.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (_blinks[mid] <= t) lo = mid + 1; else hi = mid; }
            start = lo == 0 ? double.NaN : _blinks[lo - 1];
        }
        if (double.IsNaN(start)) return (0f, 0f);
        double tau = t - start;
        double openAt = BlinkDelta + BlinkSpeed + BlinkClose;
        if (tau < openAt)
            return (Ramp(tau / BlinkSpeed), Ramp((tau - BlinkDelta) / BlinkSpeed));
        return (1f - Ramp((tau - openAt) / BlinkSpeed), 1f - Ramp((tau - openAt - BlinkDelta) / BlinkSpeed));
    }

    private static float Ramp(double x) => (float)Math.Clamp(x, 0.0, 1.0);

    /// <summary>The jitter and look-away angles the eyes are turned by at <paramref name="t"/>: (yaw, pitch).</summary>
    public (float Yaw, float Pitch) Wander(double t)
    {
        lock (_lock)
        {
            Extend(Math.Max(t, 0));
            int lo = 0, hi = _wander.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (_wander[mid].T <= t) lo = mid + 1; else hi = mid; }
            var w = _wander[Math.Max(lo - 1, 0)];
            return (w.Yaw, w.Pitch);
        }
    }

    public override Quaternion Rotation(string joint, double t)
    {
        var (yaw, pitch) = Wander(t);
        // with no target to look at, both eyes turn by the wander alone; the look-at path (Aim) is used by a caller that has a target
        return Aim(null, 0f, Quaternion.Identity, 0.06f, yaw, pitch, joint.Contains("Left", StringComparison.Ordinal));
    }

    /// <summary>The rotation of one eye towards a point (llheadrotmotion.cpp adjustEyeTarget): the direction and distance of the point from the eye in world axes (null: nothing to look at), the head's world rotation, the eyes' distance apart.</summary>
    public static Quaternion Aim(Vector3? lookAt, float distance, Quaternion headWorld, float interocular, float wanderYaw, float wanderPitch, bool left)
    {
        Quaternion target;
        float vergence;
        bool hasTarget = lookAt is not null;
        if (lookAt is { } dir0)
        {
            var dir = Vector3.Normalize(dir0);
            var l = Vector3.Cross(Vector3.UnitZ, dir);
            var up = Vector3.Cross(dir, l);
            target = OfficialMath.FromAxes(dir, l, up);
            // into head axes, without roll, and held in front of the face
            target = OfficialMath.Then(target, Quaternion.Inverse(headWorld));
            var (_, pitch, yaw) = OfficialMath.ToEuler(target);
            target = OfficialMath.Constrain(OfficialMath.FromEuler(0f, pitch, yaw), RotLimit);
            vergence = -MathF.Atan2(interocular / 2f, distance);   // the official viewer clamps this and drops the result
        }
        else
        {
            target = Quaternion.Identity;
            vergence = 0f;
        }
        vergence += 4f * (MathF.PI / 180f);   // the foveal offset from the pupil
        // no jitter while the eyes are crossed hard on something close
        var jitter = vergence > -0.05f ? OfficialMath.FromEuler(0f, wanderPitch, wanderYaw) : Quaternion.Identity;
        var vergenceQ = hasTarget ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, left ? vergence : -vergence) : Quaternion.Identity;
        return OfficialMath.Then(OfficialMath.Then(vergenceQ, jitter), target);
    }
}

/// <summary>
/// The torso, neck and head turning towards what the avatar looks at (llheadrotmotion.cpp LLHeadRotMotion). The head's wanted turn is the
/// look-at direction in the root's frame (the root's own facing when there is none; the pelvis's facing for a point nearer than 0.3 m), held to
/// 0.8 of a quarter turn. The torso follows 0.35 of it with a 0.27 s half-life, the head follows it with a 0.15 s half-life and is split between
/// neck and head, each taking half of it after the turn of the torso's parent is taken off. Frame rate does not change the result: each lag is a
/// half-life.
/// </summary>
public sealed class HeadRotMotion : ProceduralMotionBase
{
    public const float TorsoLag = 0.35f, NeckLag = 0.5f, HeadHalfLife = 0.15f, TorsoHalfLife = 0.27f, MinDistance = 0.3f;
    public const float Constraint = MathF.PI / 2f * 0.8f;

    private sealed record Frame(Quaternion Torso, Quaternion Neck, Quaternion Head);

    private static readonly System.Collections.Immutable.ImmutableArray<string> Joints = ["mTorso", "mNeck", "mHead"];
    private volatile Frame _frame = new(Quaternion.Identity, Quaternion.Identity, Quaternion.Identity);
    private Quaternion _lastHead = Quaternion.Identity;

    public override string Name => "head_rot";
    public override int Priority => 1;
    public override float EaseIn => 1f;
    public override float EaseOut => 1f;
    public override IReadOnlyList<string> RotationJoints => Joints;

    public Quaternion Torso => _frame.Torso;
    public Quaternion Neck => _frame.Neck;
    public Quaternion Head => _frame.Head;

    public override Quaternion Rotation(string joint, double t) => joint switch { "mTorso" => _frame.Torso, "mNeck" => _frame.Neck, "mHead" => _frame.Head, _ => Quaternion.Identity };

    /// <summary>One frame. <paramref name="lookAt"/>: the point looked at, relative to the head in world axes (null: none). The rotations are in world axes.</summary>
    public void Step(double dt, Vector3? lookAt, Quaternion rootWorld, Quaternion pelvisWorld, Quaternion neckParentWorld)
    {
        float headLag = OfficialMath.Interpolant(dt, HeadHalfLife), torsoLag = OfficialMath.Interpolant(dt, TorsoHalfLife);
        var invRoot = Quaternion.Inverse(rootWorld);
        Quaternion targetWorld;
        if (lookAt is { } look)
        {
            float distance = look.Length();
            var dir = distance > 1e-9f ? look / distance : Vector3.UnitX;
            if (distance < MinDistance) targetWorld = pelvisWorld;
            else
            {
                var rootUp = Vector3.Transform(Vector3.UnitZ, rootWorld);
                var left = Vector3.Cross(rootUp, dir);
                // looking straight up or down: bring the direction towards the way the root faces so the frame is defined
                if (left.LengthSquared() < 0.15f)
                {
                    var rootAt = Vector3.Transform(Vector3.UnitX, rootWorld);
                    rootAt.Z = 0f;
                    rootAt = Vector3.Normalize(rootAt);
                    dir = Vector3.Normalize(dir + (rootAt - dir) * 0.4f);
                    left = Vector3.Cross(rootUp, dir);
                }
                var up = Vector3.Cross(dir, left);
                targetWorld = OfficialMath.FromAxes(dir, left, up);
            }
        }
        else targetWorld = rootWorld;

        var headLocal = OfficialMath.Constrain(OfficialMath.Then(targetWorld, invRoot), Constraint);
        var torsoTarget = OfficialMath.Nlerp(TorsoLag, Quaternion.Identity, headLocal);
        var torso = OfficialMath.Nlerp(torsoLag, _frame.Torso, torsoTarget);
        headLocal = OfficialMath.Nlerp(headLag, _lastHead, headLocal);
        _lastHead = headLocal;
        var torsoRotLocal = OfficialMath.Then(neckParentWorld, invRoot);
        headLocal = OfficialMath.Then(headLocal, Quaternion.Inverse(torsoRotLocal));
        _frame = new Frame(torso, OfficialMath.Nlerp(NeckLag, Quaternion.Identity, headLocal), OfficialMath.Nlerp(1f - NeckLag, Quaternion.Identity, headLocal));
    }
}

/// <summary>
/// The hand pose the animations ask for and the morph weights that follow it (llhandmotion.cpp LLHandMotion). Every animation asset names a hand
/// pose; the one of the highest-priority animation wins, and none asking means relaxed. A pose is a morph of the hands (the parameters named
/// Hands_Relaxed and so on; the spread pose is the mesh as it is and has none); the old pose's weight falls and the new one's rises by
/// dt / 0.2 each frame until the change is complete.
/// </summary>
public sealed class HandMotion
{
    public const int Spread = 0, Relaxed = 1, PoseCount = 14;
    public const float BlendTime = 0.2f;

    private static readonly System.Collections.Immutable.ImmutableArray<string?> Names =
    [
        null, "Hands_Relaxed", "Hands_Point", "Hands_Fist", "Hands_Relaxed_L", "Hands_Point_L", "Hands_Fist_L",
        "Hands_Relaxed_R", "Hands_Point_R", "Hands_Fist_R", "Hands_Salute_R", "Hands_Typing", "Hands_Peace_R", "Hands_Spread_R",
    ];

    private readonly float[] _weight = new float[PoseCount];
    private int _current = Relaxed, _wanted = Relaxed;

    public HandMotion() => _weight[Relaxed] = 1f;

    /// <summary>The pose shown (its change complete) and the pose the blend is going to.</summary>
    public int Current => _current;
    public int Wanted => _wanted;

    /// <summary>The weight of the morph of pose <paramref name="pose"/> (0 for a pose with no morph).</summary>
    public float Weight(int pose) => pose > 0 && pose < PoseCount ? _weight[pose] : 0f;

    /// <summary>Whether the pose is still changing.</summary>
    public bool Blending => _current != _wanted;

    /// <summary>One frame: <paramref name="requested"/> is the pose the playing animations ask for (null: none).</summary>
    public void Step(double dt, int? requested)
    {
        if (requested is null)
        {
            if (_wanted != Relaxed && _wanted != _current) Settle();
            _wanted = Relaxed;
        }
        else if (requested is >= 0 and < PoseCount)
        {
            if (requested != _wanted && _wanted != _current) Settle();
            _wanted = requested.Value;
        }
        if (_current == _wanted) return;
        float incoming = 1f, outgoing = 0f;
        if (_wanted != Spread)
        {
            incoming = Math.Clamp(_weight[_wanted] + (float)dt / BlendTime, 0f, 1f);
            _weight[_wanted] = incoming;
        }
        if (_current != Spread)
        {
            outgoing = Math.Clamp(_weight[_current] - (float)dt / BlendTime, 0f, 1f);
            _weight[_current] = outgoing;
        }
        if (incoming == 1f && outgoing == 0f) _current = _wanted;
    }

    /// <summary>A change was asked for before the last was done: the pose it was going to goes back to nothing and the one shown to full, so neither is stuck part way.</summary>
    private void Settle()
    {
        if (_wanted != Spread) _weight[_wanted] = 0f;
        if (_current != Spread) _weight[_current] = 1f;
    }

    /// <summary>The pose the animations playing now ask for: the one whose animation has the highest priority, a later one winning among equals.</summary>
    public static int? Request(IEnumerable<(int Pose, int Priority)> playingOldestFirst)
    {
        int? pose = null;
        int best = int.MinValue;
        foreach (var (p, priority) in playingOldestFirst)
            if (priority >= best) { pose = p; best = priority; }
        return pose;
    }

    /// <summary>The name of the morph parameter of a pose (null for the spread pose and for a number that is no pose).</summary>
    public static string? MorphName(int pose) => pose > 0 && pose < PoseCount ? Names[pose] : null;
}

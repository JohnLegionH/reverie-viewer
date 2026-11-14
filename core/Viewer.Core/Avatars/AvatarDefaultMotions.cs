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

// Adapted 2026 by Legion Builds. The motions started on every avatar and the conditions that start and stop the flying roll (LLVOAvatar::startDefaultMotions and
// processAnimationStateChanges) follow the file named above, rewritten in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>Which of an avatar's built-in motions run. All of them, always, except in a test that draws a picture with one left out to see what it moves.</summary>
[Flags]
public enum DefaultMotionSet
{
    None = 0, Head = 1, Eye = 2, BodySway = 4, Breathe = 8, PelvisFix = 16, Hands = 32, Fly = 64,
    All = Head | Eye | BodySway | Breathe | PelvisFix | Hands | Fly,
}

/// <summary>Names the motions to leave out (the test switch --tr-motions-off): a comma-separated list of head, eye, sway, breathe, pelvis, hands, fly; an unknown name is refused (null).</summary>
public static class DefaultMotionNames
{
    public static DefaultMotionSet? Parse(string? list)
    {
        var set = DefaultMotionSet.None;
        if (string.IsNullOrWhiteSpace(list)) return set;
        foreach (var raw in list.Split(','))
        {
            var one = raw.Trim().ToLowerInvariant() switch
            {
                "head" => DefaultMotionSet.Head,
                "eye" => DefaultMotionSet.Eye,
                "sway" => DefaultMotionSet.BodySway,
                "breathe" => DefaultMotionSet.Breathe,
                "pelvis" => DefaultMotionSet.PelvisFix,
                "hands" => DefaultMotionSet.Hands,
                "fly" => DefaultMotionSet.Fly,
                "all" => DefaultMotionSet.All,
                _ => DefaultMotionSet.None,
            };
            if (one == DefaultMotionSet.None) return null;
            set |= one;
        }
        return set;
    }
}

/// <summary>
/// The built-in motions of one avatar and when they run, as the official viewer runs them (llvoavatar.cpp startDefaultMotions and
/// processAnimationStateChanges, read for behaviour): the head, eye, body sway, breathing and pelvis-fix motions run from the moment the
/// avatar is drawn, whoever it is, except that the body sway stops while the avatar sits on an object; the flying roll runs while the avatar
/// is in the air and not sitting, and not while a walk animation plays.
/// Hands follow the animations' hand poses. The physics motion (breasts, belly, butt), the targeting motion and the editing motion are not
/// part of this set. No engine type; main thread.
/// </summary>
public sealed class AvatarDefaultMotions
{
    private readonly DefaultMotionSet _enabled;

    public AvatarDefaultMotions(int seed, DefaultMotionSet enabled = DefaultMotionSet.All)
    {
        Eye = new EyeMotion(seed);
        _enabled = enabled;
    }

    public BodyNoiseMotion BodyNoise { get; } = new();
    public BreatheMotion Breathe { get; } = new();
    public PelvisFixMotion PelvisFix { get; } = new();
    public FlyAdjustMotion Fly { get; } = new();
    public EyeMotion Eye { get; }
    public HeadRotMotion Head { get; } = new();
    public HandMotion Hand { get; } = new();

    private bool _started;
    private double _lastStep = double.NaN;
    private float _lastYaw;

    public bool Started => _started;

    /// <summary>Starts the always-on motions on <paramref name="motions"/> at <paramref name="now"/> (once).</summary>
    public void Start(MotionController motions, double now)
    {
        if (_started) return;
        _started = true;
        if (_enabled.HasFlag(DefaultMotionSet.Head)) motions.StartProcedural(Head, now);
        if (_enabled.HasFlag(DefaultMotionSet.Eye)) motions.StartProcedural(Eye, now);
        if (_enabled.HasFlag(DefaultMotionSet.BodySway)) motions.StartProcedural(BodyNoise, now);
        if (_enabled.HasFlag(DefaultMotionSet.Breathe)) motions.StartProcedural(Breathe, now);
        if (_enabled.HasFlag(DefaultMotionSet.PelvisFix)) motions.StartProcedural(PelvisFix, now);
    }

    /// <summary>Whether the flying roll should run: in the air and not sitting, with no walk animation signalled (processAnimationStateChanges).</summary>
    public static bool ShouldFly(bool walkSignalled, bool inAir, bool sitting) => !walkSignalled && inAir && !sitting;

    /// <summary>
    /// One frame. Starts or stops the flying roll as <see cref="ShouldFly"/> says, steps it by the avatar's turn rate (taken from the change of
    /// <paramref name="yaw"/>, radians about up, since the last frame) and its <paramref name="speed"/>, and steps the hand pose towards what
    /// the playing animations ask for. The head motion is stepped with the world rotations of the pelvis and of the neck's parent joint, in the avatar's own
    /// frame (null: not turned), and the point looked at relative to the head (null: none). True when the motion mix changed (the flying roll started or stopped), so the caller poses from the new mix.
    /// </summary>
    public bool Step(MotionController motions, double now, float yaw, float speed, bool walkSignalled, bool inAir, bool sitting, int? requestedHandPose, Quaternion? pelvisWorld = null, Quaternion? neckParentWorld = null, Vector3? lookAt = null)
    {
        double dt = double.IsNaN(_lastStep) ? 0 : Math.Clamp(now - _lastStep, 0.0, 0.2);
        bool fly = _enabled.HasFlag(DefaultMotionSet.Fly) && ShouldFly(walkSignalled, inAir, sitting), changed = false;
        if (fly && !motions.IsRunning(Fly.Name, now))
        {
            Fly.Reset();
            changed = motions.StartProcedural(Fly, now);
        }
        else if (!fly && motions.IsRunning(Fly.Name, now)) changed = motions.StopProcedural(Fly.Name, now);
        // the body sway stops while the avatar sits on an object and starts again when it gets off (llvoavatar.cpp sitOnObject, getOffObject)
        if (_started && _enabled.HasFlag(DefaultMotionSet.BodySway))
        {
            bool swaying = motions.IsRunning(BodyNoise.Name, now);
            if (sitting && swaying) changed |= motions.StopProcedural(BodyNoise.Name, now);
            else if (!sitting && !swaying) changed |= motions.StartProcedural(BodyNoise, now);
        }
        if (dt > 0)
        {
            float delta = yaw - _lastYaw;
            while (delta > MathF.PI) delta -= 2 * MathF.PI;
            while (delta < -MathF.PI) delta += 2 * MathF.PI;
            Fly.Step(dt, delta / (float)dt, speed);
            if (_enabled.HasFlag(DefaultMotionSet.Hands)) Hand.Step(dt, requestedHandPose);
            if (_enabled.HasFlag(DefaultMotionSet.Head)) Head.Step(dt, lookAt, Quaternion.Identity, pelvisWorld ?? Quaternion.Identity, neckParentWorld ?? Quaternion.Identity);
        }
        _lastYaw = yaw;
        _lastStep = now;
        return changed;
    }
}

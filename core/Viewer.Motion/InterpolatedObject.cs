// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llviewerobject.cpp
 * @brief Base class for viewer objects
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
 * @file llworld.cpp
 * @brief Initial test structure to organize viewer regions
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
/*
 * Ported to C# from the Second Life Viewer 26.4 (release tag Second_Life_Release#e960123-26.4, commit e960123230d521e8557a9582100bebcac36a7ca2) source files whose notices are above.
 * Adapted 2026 by Legion Builds. Copyright (c) 2026 Legion Builds for the changes.
 */
using System.Numerics;

namespace Viewer.Motion;

/// <summary>What an object's region tells the viewer each frame: its time dilation, how long its circuit has been quiet, its width,
/// whether another region lies at a place off its edge, and the land height at a place (both in the region's own coordinates).</summary>
public sealed record RegionFrame
{
    /// <summary>The region's time dilation: 1 is real time, less when the simulator runs slow.</summary>
    public float TimeDilation { get; init; } = 1f;

    /// <summary>Seconds since the last packet of any kind came on the region's circuit.</summary>
    public double CircuitQuietSeconds { get; init; }

    public float Width { get; init; } = 256f;

    /// <summary>False for a linked child, whose place is relative to its parent: it has no region edge or land of its own.</summary>
    public bool Bounded { get; init; } = true;

    /// <summary>Whether a region the viewer knows lies at a place outside this one. Null: none does.</summary>
    public Func<Vector3, bool>? HasRegionAt { get; init; }

    /// <summary>The land height at a place. Null: not known, and an object is not held above the land.</summary>
    public Func<Vector3, float>? LandHeight { get; init; }
}

/// <summary>
/// An object that is not an avatar, between the region's updates, as the official viewer moves a viewer object (release 26.4).
/// Each frame (<c>LLViewerObject::idleUpdate</c>) the time since the last frame is scaled by the region's time dilation; the object
/// turns by its angular velocity over that time (<c>applyAngularVelocity</c>, which is how llTargetOmega is drawn) and is carried
/// along its velocity and acceleration (<c>interpolateLinearMotion</c>). The carried motion tapers off after
/// <see cref="PhaseOutSeconds"/> without an update and stops after <see cref="MaxUpdateInterpolationSeconds"/>, but only when the
/// region's circuit has gone quiet too: a region sends nothing for an object that moves as predicted. An object predicted off the
/// region's edge stops at the edge when no region lies there, and otherwise is carried for at most
/// <see cref="MaxRegionCrossingSeconds"/> more. An update (<c>processUpdateMessage</c>) sets the object at once and keeps the turn
/// the viewer has added, unless the update brings a new rotation. Positions are in the region's own coordinates (a child prim's are
/// relative to its parent, as the region states them); times are seconds on the caller's clock.
/// </summary>
public sealed class InterpolatedObject
{
    /// <summary>llviewerobject.cpp sMaxUpdateInterpolationTime, sPhaseOutUpdateInterpolationTime and sMaxRegionCrossingInterpolationTime (lines 129 to 131).</summary>
    public const double MaxUpdateInterpolationSeconds = 3.0, PhaseOutSeconds = 2.0, MaxRegionCrossingSeconds = 1.0;

    /// <summary>llviewerobject.cpp PHYSICS_TIMESTEP (line 143): an update's velocity is the average over the last physics step.</summary>
    public const float PhysicsTimestep = 1f / 45f;

    /// <summary>xform.h MAX_OBJECT_Z, the height no object is predicted above; llmath.h F_ALMOST_ZERO.</summary>
    public const float MaxObjectZ = 4096f, AlmostZero = 0.0001f;

    private Quaternion _previousRotation = Quaternion.Identity, _addedTurn = Quaternion.Identity;
    private double _lastMessage, _lastInterpolation, _crossExpire;
    private bool _known;

    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; } = Quaternion.Identity;
    public Vector3 Velocity { get; private set; }
    public Vector3 Acceleration { get; private set; }
    public Vector3 AngularVelocity { get; private set; }

    /// <summary>The length of the object's scale vector: half of it is how far below the land the object may be predicted.</summary>
    public float ScaleLength { get; set; }

    public bool Moving => Velocity != Vector3.Zero || Acceleration != Vector3.Zero || AngularVelocity != Vector3.Zero;

    /// <summary>How long an object predicted across a border is carried before it is stopped (default the official
    /// <see cref="MaxRegionCrossingSeconds"/>; infinity: never stopped). For comparing other viewers' rules; the viewer keeps the default.</summary>
    public double RegionCrossingSeconds { get; init; } = MaxRegionCrossingSeconds;

    /// <summary>Whether an update starts the border-crossing time again (the official viewer starts it only when the object is first predicted
    /// across, and ends it once the object is predicted inside). For comparing other viewers' rules; the viewer keeps the default (false).</summary>
    public bool UpdateRestartsCrossingTime { get; init; }

    /// <summary>True while the viewer's own turn is part of the rotation: the object is drawn turned from where the region states it.</summary>
    public bool Turned => _addedTurn != Quaternion.Identity;

    /// <summary>An update from the region (<c>processUpdateMessage</c>). <paramref name="full"/>: a full object update, where an
    /// angular velocity of zero forgets the turn the viewer added; a terse update keeps it.</summary>
    public void Update(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 acceleration, Vector3 angularVelocity, double now, bool full = false, bool usesPhysics = false)
    {
        rotation = Quaternion.Normalize(rotation);
        var oldAngular = AngularVelocity;
        if (full && angularVelocity == Vector3.Zero) ResetTurn();
        Position = position;
        Velocity = velocity;
        Acceleration = acceleration;
        AngularVelocity = angularVelocity;
        if (!_known || !NearlyEqual(rotation, Rotation) || angularVelocity != oldAngular)
        {
            // a new rotation forgets the added turn; a changed angular velocity with the same rotation forgets it only for a physical object
            if (!_known || rotation != _previousRotation) ResetTurn();
            else if (angularVelocity != oldAngular && usesPhysics) ResetTurn();
            _previousRotation = rotation;
            // the stated rotation, then the turn the viewer has added (llSetTargetOmega)
            Rotation = Quaternion.Normalize(_addedTurn * rotation);
        }
        _known = true;
        _lastInterpolation = now;
        _lastMessage = now;
        if (UpdateRestartsCrossingTime) _crossExpire = 0;
    }

    private void ResetTurn() => _addedTurn = Quaternion.Identity;

    /// <summary>Stops the object where it is (no velocity or acceleration) until the next update; not an official step, for a caller that has to
    /// hold an avatar still.</summary>
    internal void Stop()
    {
        Velocity = Vector3.Zero;
        Acceleration = Vector3.Zero;
    }

    private static bool NearlyEqual(Quaternion a, Quaternion b) =>
        MathF.Abs(a.X - b.X) < AlmostZero && MathF.Abs(a.Y - b.Y) < AlmostZero && MathF.Abs(a.Z - b.Z) < AlmostZero && MathF.Abs(a.W - b.W) < AlmostZero;

    /// <summary>One frame (<c>idleUpdate</c>). True when the object's position or rotation changed.</summary>
    public bool Idle(double now, RegionFrame region)
    {
        if (!_known) return false;
        float dt = region.TimeDilation * (float)(now - _lastInterpolation);
        bool turned = ApplyAngularVelocity(dt);
        bool moved = InterpolateLinearMotion(now, dt, region);
        return turned || moved;
    }

    /// <summary>applyAngularVelocity: the turn over <paramref name="dt"/> about the angular velocity's axis, added after the object's rotation.</summary>
    private bool ApplyAngularVelocity(float dt)
    {
        float omega = AngularVelocity.LengthSquared();
        if (omega <= 0.00001f) return false;
        omega = MathF.Sqrt(omega);
        float angle = omega * dt;
        var turn = Quaternion.CreateFromAxisAngle(AngularVelocity / omega, angle);
        // the added turn is kept, to be applied again when an update restates the rotation
        _addedTurn = Quaternion.Normalize(turn * _addedTurn);
        Rotation = Quaternion.Normalize(turn * Rotation);
        return angle != 0f;
    }

    /// <summary>interpolateLinearMotion. True when the position changed.</summary>
    private bool InterpolateLinearMotion(double now, float dt, RegionFrame region)
    {
        double sinceUpdate = now - _lastMessage;
        if (sinceUpdate <= 0 || dt <= 0f) return false;

        bool moved = false;
        var accel = Acceleration;
        var vel = Velocity;
        if (accel != Vector3.Zero || vel != Vector3.Zero)
        {
            // the predicted step and change of velocity
            var step = (vel + (0.5f * (dt - PhysicsTimestep)) * accel) * dt;
            var newVelocity = accel * dt;

            if (sinceUpdate > PhaseOutSeconds && region.CircuitQuietSeconds > PhaseOutSeconds)
            {
                // no update for a while and the circuit is quiet as well: the region is likely stalled, so the prediction is reduced
                double sinceInterpolation = now - _lastInterpolation;
                double phaseOut;
                if (sinceUpdate > MaxUpdateInterpolationSeconds) phaseOut = 0;
                else if (_lastInterpolation - _lastMessage > PhaseOutSeconds)
                    phaseOut = (MaxUpdateInterpolationSeconds - sinceUpdate) / (MaxUpdateInterpolationSeconds - sinceInterpolation);
                else phaseOut = (MaxUpdateInterpolationSeconds - sinceUpdate) / (MaxUpdateInterpolationSeconds - PhaseOutSeconds);
                phaseOut = Math.Clamp(phaseOut, 0.0, 1.0);
                step *= (float)phaseOut;
                newVelocity *= (float)phaseOut;
            }

            var old = Position;
            var next = step + old;
            newVelocity += vel;

            // not above the greatest object height, and not further under the land than half the object's size
            if (region.Bounded)
            {
                next.Z = MathF.Min(MaxObjectZ, next.Z);
                if (region.LandHeight is { } land) next.Z = MathF.Max(land(next) - 0.5f * ScaleLength, next.Z);
            }

            if (!region.Bounded)
            {
                _crossExpire = 0;
            }
            else if (next.X < 0f || next.X > region.Width || next.Y < 0f || next.Y > region.Width)
            {
                // going off this region
                if (region.HasRegionAt?.Invoke(next) != true)
                {
                    // no region to enter there: stopped at the edge, until the region's update says what it did
                    next = ClipToRegion(old, next, region.Width);
                    newVelocity = Vector3.Zero;
                    Acceleration = Vector3.Zero;
                }
                else if (_crossExpire == 0)
                {
                    _crossExpire = now + RegionCrossingSeconds;   // the time is taken when the crossing is first predicted
                }
                else if (now > _crossExpire)
                {
                    // predicted across the border for too long: stopped
                    newVelocity = Vector3.Zero;
                    Acceleration = Vector3.Zero;
                    _crossExpire = 0;
                }
            }
            else
            {
                _crossExpire = 0;
            }

            moved = next != Position;
            Position = next;
            Velocity = newVelocity;
        }
        _lastInterpolation = now;
        return moved;
    }

    /// <summary>LLWorld::clipToVisibleRegions for a path that starts in this region and ends where no region is: the end is brought
    /// back along the path to the edge it left by, and kept inside the region.</summary>
    private static Vector3 ClipToRegion(Vector3 start, Vector3 end, float width)
    {
        var delta = end - start;
        var deltaAbs = Vector3.Abs(delta);
        float clip = 1f;
        if (end.X < 0f) clip = end.Y < end.X ? -(end.Y / deltaAbs.Y) : -(end.X / deltaAbs.X);
        else if (end.X > width) clip = end.Y > end.X ? (end.Y - width) / deltaAbs.Y : (end.X - width) / deltaAbs.X;
        else if (end.Y < 0f) clip = -(end.Y / deltaAbs.Y);
        else if (end.Y > width) clip = (end.Y - width) / deltaAbs.Y;
        var clipped = float.IsFinite(clip) ? end - delta * clip : end;
        return new Vector3(Math.Clamp(clipped.X, 0f, width - AlmostZero), Math.Clamp(clipped.Y, 0f, width - AlmostZero), Math.Clamp(clipped.Z, 0f, MaxObjectZ - AlmostZero));
    }
}

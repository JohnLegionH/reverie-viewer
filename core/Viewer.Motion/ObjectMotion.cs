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
 * @file lldrawable.cpp
 * @brief LLDrawable class implementation
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
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
 * @file llcriticaldamp.cpp
 * @brief Implementation of the critical damping functionality.
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
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

/// <summary>
/// Where an avatar is drawn, the user's own and others alike, as the official viewer moves a viewer object (release 26.4: LLVOAvatar::idleUpdate calls
/// LLViewerObject::idleUpdate). The object is an <see cref="InterpolatedObject"/>: an update sets its place at once where it says, at the time it is
/// taken (<c>processUpdateMessage</c>; the official PingInterpolate setting, off by default, would move it on by half the ping and a frame:
/// <see cref="OneWayDelay"/>, which nothing sets), and each frame it is carried along its velocity, scaled by its region's time dilation, tapered
/// only when the circuit has gone quiet, and carried at most a second past its region's edge into a region the viewer knows
/// (<c>interpolateLinearMotion</c>). What is drawn follows the object's place with exponential damping of time constant
/// <see cref="DampingTimeConstant"/> (<c>LLDrawable::updateXform</c>), snapping to it when the two are further apart than 10 m or closer than a
/// thousandth of the camera distance. The official viewer takes every update of a frame before it moves anything (<c>LLAppViewer::idle</c>), so an
/// update taken here after the frame has been drawn is drawn as if it had come first. Times are seconds on the caller's clock; places are in the
/// caller's frame (the region's origin is given in it by <see cref="Region"/>).
/// <para>When a movement key comes up nothing changes here: the object goes on being carried along its velocity until the region's own stop arrives,
/// so the avatar coasts to rest. <see cref="Halt"/> is not part of the official object and is not called for a key release; it stays for a caller
/// that has to hold the drawn avatar still.</para>
/// </summary>
public sealed class ObjectMotion : IAvatarMotion
{
    /// <summary>llviewerobject.cpp sMaxUpdateInterpolationTime and sPhaseOutUpdateInterpolationTime (lines 128, 129).</summary>
    public const double MaxUpdateInterpolationSeconds = InterpolatedObject.MaxUpdateInterpolationSeconds, PhaseOutSeconds = InterpolatedObject.PhaseOutSeconds;

    /// <summary>lldrawable.cpp OBJECT_DAMPING_TIME_CONSTANT (line 60), and the snap distances (lines 57, 58).</summary>
    public const float DampingTimeConstant = 0.06f, MinInterpolateDistance = 0.001f, MaxInterpolateDistanceSquared = 10f * 10f;

    /// <summary>How long a halted avatar stays halted while updates still say it moves, and the speed below which it counts as stopped.</summary>
    public const double HaltHoldSeconds = 0.6;
    public const float StoppedSpeed = 0.05f;

    private readonly InterpolatedObject _object = new();
    private Vector3 _origin, _drawn, _drawnBefore;
    private Quaternion _drawnRotation = Quaternion.Identity, _drawnRotationBefore = Quaternion.Identity;
    private double _lastDamp = double.NaN, _haltedAt = double.NaN;
    private float _lastDampSeconds;
    private bool _known;

    /// <summary>Half the circuit's round trip plus a frame (the official <c>ping_delay</c>, used only when its PingInterpolate setting is on, which by
    /// default it is not): an update's position is moved on by the velocity over this long. 0: taken where it says.</summary>
    public double OneWayDelay { get; set; }

    /// <summary>Seconds since the last packet of any kind came on the region's circuit; above <see cref="PhaseOutSeconds"/> the
    /// carried motion tapers off (the official check on the circuit's last packet time).</summary>
    public double CircuitQuietSeconds { get; set; }

    /// <summary>The distance from the camera to the avatar (metres), which scales the smallest distance worth damping.</summary>
    public float CameraDistance { get; set; } = 1f;

    /// <summary>The region whose statements the object takes (null: none known, and the object is carried with no region edge).</summary>
    public MotionRegion? Region { get; set; }

    /// <summary>That region's time dilation: 1 is real time, less when the simulator runs slow.</summary>
    public float TimeDilation { get; set; } = 1f;

    public Vector3 Stated { get; private set; }
    public int Updates { get; private set; }
    public int Snaps { get; private set; }
    public bool Halted => !double.IsNaN(_haltedAt);

    public void Update(Vector3 position, Vector3 velocity, Quaternion rotation, double now)
    {
        if (Halted)
        {
            if (velocity.Length() >= StoppedSpeed && now - _haltedAt < HaltHoldSeconds) return;   // the region has not stopped it yet
            _haltedAt = double.NaN;
        }
        Stated = position;
        _origin = Region?.Origin ?? Vector3.Zero;
        _object.Update(position + velocity * (float)OneWayDelay - _origin, rotation, velocity, Vector3.Zero, Vector3.Zero, now);
        if (!_known)
        {
            _known = true;
            Snaps++;
            _drawn = _drawnBefore = Place;
            _drawnRotation = _drawnRotationBefore = _object.Rotation;
            _lastDamp = now;
            _lastDampSeconds = 0;
        }
        else if (now <= _lastDamp)
        {
            // taken after the frame drew it: drawn as if taken first, the only order the official viewer has (the frame's damping done again)
            (_drawn, _drawnRotation) = Follow(_drawnBefore, _drawnRotationBefore, Place, _object.Rotation, _lastDampSeconds, CameraDistance);
        }
        Updates++;
    }

    /// <summary>The object's place now, in the caller's frame.</summary>
    private Vector3 Place => _object.Position + _origin;

    public void Snap(Vector3 position, Vector3 velocity, Quaternion rotation, double now)
    {
        _haltedAt = double.NaN;
        _known = false;
        Update(position, velocity, rotation, now);
    }

    public void Halt(double now)
    {
        if (!_known || Halted || _object.Velocity.Length() < StoppedSpeed) return;
        Advance(now);
        _object.Stop();
        _haltedAt = now;
    }

    public void Resume() => _haltedAt = double.NaN;

    public Vector3 Velocity(double now) => _object.Velocity;

    public (Vector3 Position, Quaternion Rotation) At(double now)
    {
        Advance(now);
        return (_drawn, _drawnRotation);
    }

    /// <summary>Takes the object and what is drawn on to <paramref name="now"/>; calling it again for the same time changes nothing. The object is
    /// moved from the time of its last move (an update's time included: <c>interpolateLinearMotion</c> returns in the frame an update came); what is
    /// drawn follows it once a frame (idleUpdate calls updateDrawable whether or not the object moved).</summary>
    private void Advance(double now)
    {
        if (!_known) return;
        var origin = _origin;
        float dilation = Region?.Dilation?.Invoke() ?? TimeDilation;   // the object's region's, every frame (mRegionp->getTimeDilation())
        var region = Region is { } r
            ? new RegionFrame { TimeDilation = dilation, CircuitQuietSeconds = CircuitQuietSeconds, Width = r.Width, HasRegionAt = r.HasRegionAt is { } has ? local => has(local + origin) : null }
            : new RegionFrame { TimeDilation = dilation, CircuitQuietSeconds = CircuitQuietSeconds, Bounded = false };
        _object.Idle(now, region);
        if (now <= _lastDamp) return;
        _drawnBefore = _drawn;
        _drawnRotationBefore = _drawnRotation;
        _lastDampSeconds = (float)(now - _lastDamp);
        (_drawn, _drawnRotation) = Follow(_drawn, _drawnRotation, Place, _object.Rotation, _lastDampSeconds, CameraDistance);
        _lastDamp = now;
    }

    /// <summary>updateXform's damping over one frame of <paramref name="dt"/> seconds: what is drawn moves towards the object's place and rotation
    /// by a fraction of the way, or is put there when the two are further apart than 10 m or closer than a thousandth of the camera distance.</summary>
    internal static (Vector3 Position, Quaternion Rotation) Follow(Vector3 drawn, Quaternion drawnRotation, Vector3 target, Quaternion targetRotation, float dt, float cameraDistance)
    {
        float lerp = Math.Clamp(1f - MathF.Pow(2f, -dt / DampingTimeConstant), 0f, 1f);
        var newPos = Vector3.Lerp(drawn, target, lerp);
        float distSquared = Vector3.DistanceSquared(newPos, target);
        var from = drawnRotation;
        if (Quaternion.Dot(from, targetRotation) < 0) from = Quaternion.Negate(from);
        var newRot = Quaternion.Normalize(Quaternion.Lerp(from, targetRotation, lerp));
        distSquared += (1f - Quaternion.Dot(newRot, targetRotation)) * 10f;
        float min = MinInterpolateDistance * cameraDistance;
        return distSquared >= min * min && distSquared <= MaxInterpolateDistanceSquared ? (newPos, newRot) : (target, targetRotation);
    }
}

/// <summary>The region an object's statements come from: its origin in the caller's frame, its width, whether the viewer knows a region at a
/// place (in the caller's frame) outside it, and its time dilation now (null: <see cref="ObjectMotion.TimeDilation"/> is used).</summary>
public sealed record MotionRegion(Vector3 Origin, float Width, Func<Vector3, bool>? HasRegionAt, Func<float>? Dilation = null);

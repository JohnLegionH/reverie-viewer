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
/// The user's own avatar as the official viewer (release 26.4) would draw it from a given series of region statements, for replaying recorded
/// crossings against this viewer's own motion. It is one object across the border (<c>LLViewerObjectList::processObjectUpdate</c> finds it by its
/// full id and moves it to the new region, <c>LLViewerObject::setRegion</c>). A statement sets its place at once, moved on by the velocity over half
/// the ping plus a frame scaled by time dilation only when <see cref="PingInterpolate"/> is on (<c>processUpdateMessage</c>, <c>sPingInterpolate</c>;
/// off by default), and is the time of its last move; each
/// frame it is carried along its velocity (<c>interpolateLinearMotion</c>, through <see cref="InterpolatedObject"/>, which also carries it at most
/// one second across a border into a known region), and what is drawn follows it (<c>LLDrawable::updateXform</c>). A statement is taken in the
/// network step of the frame it arrives in, after the frame's time is set and before objects move (<c>LLAppViewer::idle</c>). Places are metres
/// in one frame for every region; each region is a square of the given width at the given origin. Times are seconds.
/// </summary>
public sealed class OfficialOwnAvatarReplay
{
    private readonly InterpolatedObject _object;
    private Vector3 _origin, _drawn;
    private float _width = 256f;
    private bool _known;
    private Quaternion _drawnRotation = Quaternion.Identity;

    /// <param name="regionCrossingSeconds">How long the object is carried across a border before it stops (official: one second).</param>
    /// <param name="updateRestartsCrossingTime">Whether a statement starts that time again (official: no).</param>
    public OfficialOwnAvatarReplay(double regionCrossingSeconds = InterpolatedObject.MaxRegionCrossingSeconds, bool updateRestartsCrossingTime = false) =>
        _object = new InterpolatedObject { RegionCrossingSeconds = regionCrossingSeconds, UpdateRestartsCrossingTime = updateRestartsCrossingTime };

    /// <summary>Round trip of the region's circuit, seconds (the ping the official viewer measures; 0 when not known).</summary>
    public double PingSeconds { get; set; }

    /// <summary>The official setting PingInterpolate (app_settings/settings.xml: off by default, applied every frame in
    /// <c>LLViewerObjectList::update</c>): only when on is a statement moved on by half the ping plus a frame.</summary>
    public bool PingInterpolate { get; set; }

    /// <summary>The object's place now (not what is drawn).</summary>
    public Vector3 Place => _object.Position + _origin;

    /// <summary>Its velocity now (zero once it has been stopped across a border).</summary>
    public Vector3 Velocity => _object.Velocity;

    /// <summary>A statement from the region at <paramref name="regionOrigin"/>, taken in the frame at <paramref name="frameTime"/>, which came
    /// <paramref name="frameSeconds"/> after the frame before; <paramref name="timeDilation"/> is the region's.</summary>
    public void Statement(Vector3 position, Vector3 velocity, Vector3 regionOrigin, float regionWidth, double frameTime, double frameSeconds, float timeDilation)
    {
        _origin = regionOrigin;
        _width = regionWidth;
        // only with PingInterpolate on (llviewerobject.cpp 2262-2271); gFrameDTClamped is the frame time, at most 0.2 s
        float pingDelay = PingInterpolate ? 0.5f * timeDilation * ((float)PingSeconds + (float)Math.Min(frameSeconds, 0.2)) : 0f;
        _object.Update(position - regionOrigin + velocity * pingDelay, Quaternion.Identity, velocity, Vector3.Zero, Vector3.Zero, frameTime);
        if (_known) return;
        _known = true;
        _drawn = _seed ?? Place;
    }

    private Vector3? _seed;

    /// <summary>Where the object was already drawn when the replay begins, for a recording that starts in the middle of a flight (call before the
    /// first statement). Without it the first statement puts what is drawn at its place, as a new object's first update does.</summary>
    public void SeedDrawn(Vector3 drawn) => _seed = drawn;

    /// <summary>One frame at <paramref name="frameTime"/>, <paramref name="frameSeconds"/> after the last; <paramref name="hasRegionAt"/> says
    /// whether the viewer knows a region at a place (metres in the common frame). Returns what is drawn.</summary>
    public Vector3 Frame(double frameTime, double frameSeconds, float timeDilation, Func<Vector3, bool> hasRegionAt)
    {
        if (!_known) return default;
        var origin = _origin;
        _object.Idle(frameTime, new RegionFrame { TimeDilation = timeDilation, Width = _width, HasRegionAt = local => hasRegionAt(local + origin) });
        (_drawn, _drawnRotation) = ObjectMotion.Follow(_drawn, _drawnRotation, Place, Quaternion.Identity, (float)frameSeconds, 1f);
        return _drawn;
    }
}

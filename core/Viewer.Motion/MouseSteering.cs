// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file lltoolpie.cpp
 * @brief LLToolPie class implementation
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
 * Ported to C# from the Second Life Viewer 26.4 (release tag Second_Life_Release#e960123-26.4, commit e960123230d521e8557a9582100bebcac36a7ca2) source file whose notice is above.
 * Adapted 2026 by Legion Builds. Copyright (c) 2026 Legion Builds for the changes.
 */
using System.Numerics;

namespace Viewer.Motion;

/// <summary>
/// Steering the avatar with the left mouse button (lltoolpie.cpp handleHover, startCameraSteering, steerCameraWithMouse). With the left
/// click bound in third person (it is, by default), holding the button on the world and dragging past <see cref="DragThreshold"/>
/// pixels grabs the picked point: the avatar then turns about the vertical axis so that the mouse keeps pointing at it. Positions are
/// region metres, Z up; the avatar's heading is the angle from east, growing to the left.
/// <para>The picked point is the pick itself, or, for a click on the own avatar, a point 3 metres along the camera's axis from the avatar
/// (the avatar cannot be grabbed by turning), or, for a click on the sky, a point far along the mouse ray. The turn is the change of the
/// angle at which the mouse ray meets a sphere round the avatar through the picked point, measured from the avatar's facing; a point
/// nearer to the camera than the avatar turns the avatar the other way.</para>
/// </summary>
public sealed class MouseSteering
{
    /// <summary>DRAG_N_DROP_DISTANCE_THRESHOLD (llui.h): a drag starts when the pointer is more than this many pixels from where the button went down.</summary>
    public const float DragThreshold = 3f;
    /// <summary>MIN_ROTATION_RADIUS_FRACTION of the distance from the camera to the axis of rotation.</summary>
    public const float MinRotationRadiusFraction = 0.2f;
    /// <summary>How far in front of the avatar the own avatar's grab point is put.</summary>
    public const float OwnAvatarGrabDistance = 3f;

    private bool _down, _outsideSlop, _steering, _clockwise;
    private float _downX, _downY, _steerX, _steerY;
    private Vector3 _pick, _origin;

    /// <summary>The left button is down.</summary>
    public bool Pressed => _down;

    /// <summary>A drag is steering the avatar (inCameraSteerMode).</summary>
    public bool Steering => _down && _steering;

    public void MouseDown(float x, float y)
    {
        _down = true;
        _outsideSlop = _steering = false;
        _downX = x;
        _downY = y;
    }

    public void MouseUp() => _down = _outsideSlop = _steering = false;

    /// <summary>Whether the pointer at (<paramref name="x"/>, <paramref name="y"/>) has gone far enough from where the button went down to start
    /// steering: only once per press, and only when the left click is bound to a movement in third person (<paramref name="leftClickBound"/>).</summary>
    public bool ExceedsThreshold(float x, float y, bool leftClickBound)
    {
        if (_outsideSlop || !_down || !leftClickBound) return false;
        float dx = x - _downX, dy = y - _downY;
        return dx * dx + dy * dy > DragThreshold * DragThreshold;
    }

    /// <summary>Starts steering from the press position. <paramref name="focusOnAvatar"/>: the camera follows the avatar (no steering otherwise);
    /// <paramref name="pickValid"/> and <paramref name="pickPoint"/>: what was under the pointer when the button went down;
    /// <paramref name="pickIsSelf"/>: it was the own avatar; <paramref name="farPoint"/>: the point a hundred metres along the mouse ray, used for the sky.
    /// Returns whether steering is under way.</summary>
    public bool Begin(bool focusOnAvatar, bool pickValid, bool pickIsSelf, Vector3 pickPoint, Vector3 farPoint, Vector3 agentOrigin, Vector3 cameraOrigin, Vector3 cameraAt)
    {
        _outsideSlop = true;
        _steering = false;
        if (!focusOnAvatar) return false;
        var pick = pickPoint;
        if (pickIsSelf) pick = agentOrigin + cameraAt * OwnAvatarGrabDistance;
        else if (!pickValid) pick = farPoint;
        _pick = pick;
        _origin = agentOrigin;
        _steerX = _downX;
        _steerY = _downY;
        _clockwise = Vector3.Dot(agentOrigin - cameraOrigin, pick - agentOrigin) < 0f;
        _steering = true;
        return true;
    }

    /// <summary>The pointer moved to (<paramref name="x"/>, <paramref name="y"/>): the angle in radians to turn the avatar by, positive to the
    /// left (LLAgent::yaw). <paramref name="rayAt"/> gives the direction of the ray through a pixel; <paramref name="heading"/> is the avatar's heading.</summary>
    public float Steer(float x, float y, Func<float, float, Vector3> rayAt, Vector3 cameraOrigin, float heading)
    {
        if (!Steering) return 0f;
        var at = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
        var left = new Vector3(-MathF.Sin(heading), MathF.Cos(heading), 0f);

        var center = new Vector3(_origin.X, _origin.Y, _pick.Z);   // the axis of rotation, at the picked point's height
        float minRadius = MinRotationRadiusFraction * Vector3.Distance(center, cameraOrigin);
        float radius = MathF.Max(Vector3.Distance(_pick, center), minRadius);
        var adjustedCamera = new Vector3(cameraOrigin.X, cameraOrigin.Y, center.Z);
        float cameraDistance = Vector3.Distance(adjustedCamera, center);

        var mouseRay = Horizontal(rayAt(x, y));
        var oldRay = Horizontal(rayAt(_steerX, _steerY));
        if (mouseRay == Vector3.Zero || oldRay == Vector3.Zero) return 0f;

        float YawOf(Vector3 ray)
        {
            if (FarIntersection(adjustedCamera, ray, center, radius, out var hit))
            {
                var offset = hit - center;
                return MathF.Atan2(Vector3.Dot(offset, left), Vector3.Dot(offset, at));
            }
            float miss = MathF.PI / 2 + MathF.Asin(Math.Clamp(radius / MathF.Max(cameraDistance, 1e-6f), -1f, 1f));
            return Vector3.Dot(mouseRay, left) < 0f ? -miss : miss;   // the sign comes from the new ray for both, as in the source
        }

        float delta = MathF.IEEERemainder(YawOf(mouseRay) - YawOf(oldRay), 2 * MathF.PI);
        _steerX = x;
        _steerY = y;
        return _clockwise ? delta : -delta;
    }

    private static Vector3 Horizontal(Vector3 v)
    {
        var h = new Vector3(v.X, v.Y, 0f);
        float len = h.Length();
        return len < 1e-6f ? Vector3.Zero : h / len;
    }

    /// <summary>intersect_ray_with_sphere with FAR_INTERSECTION: the farther of the two points where the ray meets the sphere.</summary>
    private static bool FarIntersection(Vector3 rayPoint, Vector3 rayDir, Vector3 center, float radius, out Vector3 hit)
    {
        var toRay = rayPoint - center;
        float b = 2f * Vector3.Dot(rayDir, toRay);
        float c = toRay.LengthSquared() - radius * radius;
        float discriminant = b * b - 4f * c;
        if (discriminant < 0f)
        {
            hit = default;
            return false;
        }
        float t1 = (-b + MathF.Sqrt(discriminant)) / 2f;
        hit = rayPoint + rayDir * t1;
        return true;
    }
}

// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llagentcamera.cpp
 * @brief LLAgent class implementation
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
 * @file lltoolfocus.cpp
 * @brief A tool to set the build focus point.
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
 * @file llviewerinput.cpp
 * @brief LLViewerInput class implementation
 *
 * $LicenseInfo:firstyear=2005&license=viewerlgpl$
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
namespace Viewer.Motion;

using System.Numerics;

/// <summary>Which of the two cameras is in use: behind the avatar (the third-person camera, including the unlocked views of it), or the
/// avatar's own eye.</summary>
public enum CameraViewMode { ThirdPerson, Mouselook }

/// <summary>
/// The user's camera, ported from the official viewer's agent camera (llagentcamera.cpp; the mouse tool is <see cref="CameraControls"/>, from
/// lltoolfocus.cpp and llviewerinput.cpp). Region metres, Z up; no engine type; main thread only.
/// <para>While the focus is on the avatar the camera sits behind it: the avatar's position, the third-person head offset (0, 0, 1), and the
/// rear-view offset (-3, 0, 0.75) times the zoom fraction (0.25 to 8), turned with the avatar's heading; the focus is the rear-view focus
/// offset (1, 0, 1). The distance eases to a new zoom with a 0.07 s half-life; the region's constraint plane shortens it; after half a second
/// in the air the camera trails the avatar's velocity; the offset from the avatar is smoothed with the CameraPositionSmoothing half-life; the
/// camera stays under the draw distance from the avatar and half a metre above the ground.</para>
/// <para>Unlocking the view (<see cref="UnlockView"/>, which every camera key and an Alt-click does) leaves the camera where it is and makes
/// the focus the avatar's head, or a clicked object or point; orbit, zoom and pan then change the camera's offset from that focus.
/// <see cref="ResetView"/> puts everything back.</para>
/// </summary>
public class AgentCamera
{
    public static readonly Vector3 CameraOffset = new(-3f, 0f, 0.75f), FocusOffset = new(1f, 0f, 1f), DefaultHeadOffset = new(0f, 0f, 1f);
    public const float MinZoom = 0.25f, MaxZoom = 8f, MaxDistance = 50f, ZoomHalfLife = 0.07f;
    /// <summary>The camera's elevation above or below the avatar's centre stays within this angle (radians) for the preset orbit.</summary>
    public const float MaxPitch = 1.4f;

    /// <summary>Half-life (seconds) of the offset smoothing (llagentcamera.cpp, SMOOTHING_HALF_LIFE 0.02 times the CameraPositionSmoothing
    /// setting of 1.0), and the longest offset smoothed over (MAX_CAMERA_SMOOTH_DISTANCE). The avatar's position itself is not damped here:
    /// the camera follows the agent's position, which is the avatar's render position, already damped by its drawable (llvoavatar.cpp,
    /// updateCharacter: gAgent.setPositionAgent(getRenderPosition())).</summary>
    public const float PositionSmoothingHalfLife = 0.02f, MaxSmoothDistance = 50f;
    /// <summary>The distance kept from a constraint plane, and the nearest the camera comes to the avatar (llagentcamera.cpp).</summary>
    public const float CollideEpsilon = 0.1f, MinDistance = 0.1f;
    /// <summary>The flight lag (llagentcamera.cpp and settings.xml): half-lives in the air and on the ground, the least the avatar is kept
    /// in front by, the most the lag grows to, the DynamicCameraStrength setting and the divisor of the velocity.</summary>
    public const float LagHalfLife = 0.25f, GroundLagHalfLife = 0.15f, MinLag = 0.5f, MaxLag = 5f, DynamicCameraStrength = 2f, LagDivisor = 30f;
    /// <summary>The time in the air before the lag starts, and how long it takes to come in (llagentcamera.cpp).</summary>
    public const float AirStartSeconds = 0.5f, AirTransitionSeconds = 0.5f;
    /// <summary>Nearest the focus may be approached: ground or a point (LAND_MIN_ZOOM), an object (OBJECT_MIN_ZOOM); the farthest from an
    /// object (MAX_CAMERA_DISTANCE_FROM_OBJECT), the margin kept inside the region (CAMERA_FUDGE_FROM_OBJECT), how far above the ground the
    /// camera stays (getCameraMinOffGround), and the near clip plane used by the avatar's minimum distance.</summary>
    public const float LandMinZoom = 0.15f, ObjectMinZoom = 0.02f, MaxDistanceFromObject = 496f, RegionFudge = 16f, MinOffGround = 0.5f, NearClip = 0.1f;
    /// <summary>The orbit's limits from straight up (cameraOrbitOver: 1 and 179 degrees), the key rates (llagentcamera.cpp updateCamera:
    /// 90 degrees a second to orbit, 5 m a second to pan) and the wheel's base (sqrt(sqrt 2)).</summary>
    public const float OrbitMinFromUp = 1f * MathF.PI / 180, OrbitMaxFromUp = 179f * MathF.PI / 180, OrbitRate = MathF.PI / 2, PanRate = 5f;
    public static readonly float RootRootTwo = MathF.Sqrt(MathF.Sqrt(2f));
    /// <summary>How high the eye is in mouse-look, as a fraction of the avatar's height.</summary>
    public const float EyeHeightFraction = 0.9f;

    /// <summary>Half-life of the offset smoothing; 0 turns it off.</summary>
    public float PositionHalfLife { get; set; } = PositionSmoothingHalfLife;
    /// <summary>The camera position smoothing setting (the official CameraPositionSmoothing): 1 is the official half-life, 0 turns the smoothing off.</summary>
    public float Smoothing
    {
        get => _smoothing;
        set
        {
            _smoothing = float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 1f;
            PositionHalfLife = PositionSmoothingHalfLife * _smoothing;
        }
    }

    private float _smoothing = 1f;

    /// <summary>The offset added to the avatar's position for the third-person camera (official: (0, 0, 1)).</summary>
    public Vector3 ThirdPersonHeadOffset { get; set; } = DefaultHeadOffset;
    /// <summary>The avatar's height (its eye and head are placed from it).</summary>
    public float AvatarHeight { get; set; } = 1.9f;
    /// <summary>The avatar's bounding box (official: the avatar's scale), used for how near an avatar focus may be approached.</summary>
    public Vector3 AvatarExtents { get; set; } = new(0.45f, 0.6f, 1.9f);
    /// <summary>The draw distance, and the region's width: the farthest the camera goes from the avatar, and from a focus.</summary>
    public float DrawDistance { get; set; } = 128f;
    public float RegionWidth { get; set; } = 256f;
    /// <summary>The ground height at a region position, or null: the camera does not go under it (plus <see cref="MinOffGround"/>).</summary>
    public Func<float, float, float>? LandHeight { get; set; }
    /// <summary>The agent has god powers: the draw-distance limit does not apply (llagentcamera.cpp 1976, isGodlike).</summary>
    public bool Godlike { get; set; }
    /// <summary>Whether the agent can manage the estate of the region that holds a point (LLViewerRegion::canManageEstate): the camera is not held
    /// to the draw distance there (llagentcamera.cpp 1978-1983). Null: no region is one.</summary>
    public Func<float, float, bool>? CanManageEstateAt { get; set; }
    /// <summary>The rotation of the object the avatar sits on, or null when it does not sit (llagentcamera.cpp 1839-1849, 1855-1856, 1686-1689).</summary>
    public Quaternion? SeatRotation { get; set; }
    /// <summary>The DisableCameraConstraints setting (off by default): no draw-distance, constraint-plane or zoom limits (llagentcamera.cpp
    /// 581, 765, 814, 948, 999, 1011, 1847, 1968, 2051), and the ground floor drops to 1,000 m under the land (getCameraMinOffGround, 2129).</summary>
    public bool ConstraintsOff { get; set; }
    /// <summary>The EnableMouselook setting: when off, nothing enters mouse-look.</summary>
    public bool MouselookAllowed { get; set; } = true;
    /// <summary>Whether a zoom past the closest changes the mode itself. A host that keeps its own mouse-look state turns this off and
    /// acts on the true that <see cref="ScrollWheel"/> and <see cref="OrbitIn"/> return.</summary>
    public bool AutoEnterMouselook { get; set; } = true;
    /// <summary>The avatar is turned (yaw, radians; left positive) or tilted by an orbit while the focus is on the avatar (gAgent.yaw / pitch).</summary>
    public Action<float>? YawAvatar { get; set; }
    public Action<float>? PitchAvatar { get; set; }
    /// <summary>The focus came back to the avatar from elsewhere: the avatar turns to face this heading (radians about up from east), where
    /// the camera was looking (llagentcamera.cpp setFocusOnAvatar with reset_axes).</summary>
    public event Action<float>? FaceHeading;

    public CameraViewMode Mode { get; private set; }
    public bool FocusOnAvatar { get; private set; } = true;

    /// <summary>The region's constraint plane: the camera stays on the avatar's side of the plane through the points p with
    /// (x, y, z) . p = w. All zero: no plane.</summary>
    public Vector4 CollidePlane { get; set; }

    private Vector3? _anchor, _smoothedOffset, _smoothedGlobal;
    private Vector3 _lag;
    private Vector3 _lastEye, _lastTarget;
    private bool _hasPose, _smoothStop;
    private float _heading;
    private float _distance = -1;   // eased distance; negative until the first pose
    private float _targetDistance = CameraOffset.Length();

    // the unlocked view
    private Vector3 _offsetTarget, _focusFixed, _focusOffset, _focusExtents;
    private Func<Vector3>? _focusFollow;
    private bool _focusIsAvatar, _focusIsObject;

    /// <summary>A walk key went down: the view returns to the avatar and the orbit (turn and tilt) of the preset view clears; the zoom stays
    /// (llagent.cpp moveAt and friends call resetView() without the camera change).</summary>
    public void ResetOrbit() => ResetView();

    /// <summary>Whether a zoom by <paramref name="factor"/> (below 1: closer) would take the camera closer than the closest it goes
    /// (llagentcamera.cpp, cameraOrbitIn: a zoom fraction under the minimum while moving in enters mouse-look).</summary>
    public bool ZoomWouldPassClosest(float factor) => factor < 1f && Zoom * factor < MinZoom;

    public float OrbitYaw { get; private set; }
    public float OrbitPitch { get; private set; }
    /// <summary>The zoom asked for, as a fraction of the default offset (1 = the default).</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>The distance the camera eases towards. The rear view is held to <see cref="MaxDistance"/> unless the constraints are off
    /// (the official offset is the zoom fraction times the rear offset, with no cap of its own: llagentcamera.cpp 1827-1828).</summary>
    public float TargetDistance => ConstraintsOff ? CameraOffset.Length() * Zoom : MathF.Min(CameraOffset.Length() * Zoom, MaxDistance);
    public float Distance => _distance < 0 ? TargetDistance : _distance;

    /// <summary>The preset orbit of the rear view (automation and tests): turns the rear view by <paramref name="yaw"/> (about up) and
    /// <paramref name="pitch"/> (up positive), radians. The user's orbit is <see cref="OrbitAround"/> and <see cref="OrbitOver"/>.</summary>
    public void Orbit(float yaw, float pitch) => SetOrbit(OrbitYaw + yaw, OrbitPitch + pitch);

    public void SetOrbit(float yaw, float pitch)
    {
        OrbitYaw = MathF.IEEERemainder(yaw, 2 * MathF.PI);
        float baseElevation = MathF.Atan2(CameraOffset.Z, -CameraOffset.X);
        OrbitPitch = Math.Clamp(pitch, -MaxPitch - baseElevation, MaxPitch - baseElevation);
    }

    /// <summary>Multiplies the zoom (above 1: farther), within the limits.</summary>
    public void ZoomBy(float factor) => SetZoom(Zoom * factor);

    public void SetZoom(float zoom)
    {
        zoom = float.IsFinite(zoom) ? zoom : 1f;
        Zoom = ConstraintsOff ? zoom : Math.Clamp(zoom, MinZoom, MaxZoom);   // cameraOrbitIn 999-1002: the clamp is skipped when constraints are off
    }

    /// <summary>Back to the default view behind the avatar, as at the start.</summary>
    public void Reset()
    {
        OrbitYaw = OrbitPitch = 0;
        Zoom = 1f;
        CollidePlane = default;
        FocusOnAvatar = true;
        Mode = CameraViewMode.ThirdPerson;
        _anchor = _smoothedOffset = _smoothedGlobal = null;   // the next pose starts at the avatar
        _lag = default;
        _distance = -1;
        _hasPose = false;
    }

    // ---- the official operations ----

    /// <summary>llagentcamera.cpp unlockView: the camera stays where it is, the focus becomes the avatar's head, and the camera's offset
    /// from it is what orbit, zoom and pan change. Does nothing when the view is already unlocked, or before the first pose.</summary>
    public void UnlockView()
    {
        if (!FocusOnAvatar || !_hasPose || _anchor is not { } anchor) return;
        _focusIsAvatar = true;
        _focusIsObject = true;
        _focusFollow = null;
        _focusExtents = AvatarExtents;
        _focusOffset = new Vector3(0, 0, EyeHeightFraction * AvatarHeight - AvatarHeight / 2);
        _offsetTarget = _lastEye - (anchor + _focusOffset);
        FocusOnAvatar = false;
        _smoothedGlobal = null;
    }

    /// <summary>Focuses a clicked point, or an object (<paramref name="follow"/> gives its position; the focus keeps its place on it),
    /// leaving the camera where it is (llagentcamera.cpp setFocusGlobal after setFocusOnAvatar(false)).</summary>
    public void SetFocus(Vector3 point, Func<Vector3>? follow = null, Vector3? extents = null, bool isAvatar = false)
    {
        if (!_hasPose) return;
        _focusIsAvatar = false;
        _focusIsObject = follow is not null;
        _focusFollow = follow;
        _focusExtents = extents ?? default;
        _focusFixed = point;
        _focusOffset = follow is null ? default : point - follow();
        _offsetTarget = _lastEye - point;
        FocusOnAvatar = false;
        _smoothedGlobal = null;
        if (isAvatar) _focusIsAvatar = false;   // another avatar: its extents bound the approach like any object's box does not apply here
    }

    /// <summary>The distance from the camera (as last posed) to the focus.</summary>
    public float FocusDistance => (_lastEye - FocusPosition(_anchor ?? default)).Length();

    /// <summary>The focus point of the unlocked view.</summary>
    public Vector3 FocusPoint => FocusPosition(_anchor ?? default);

    private Vector3 FocusPosition(Vector3 anchor) =>
        _focusIsAvatar ? anchor + _focusOffset : _focusFollow is { } f ? f() + _focusOffset : _focusFixed;

    /// <summary>cameraOrbitAround: on the avatar the avatar turns; otherwise the offset from the focus turns about up.</summary>
    public void OrbitAround(float radians)
    {
        if (FocusOnAvatar && Mode == CameraViewMode.ThirdPerson) { YawAvatar?.Invoke(radians); return; }
        _offsetTarget = Vector3.Transform(_offsetTarget, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians));
        ZoomIn(1f);
    }

    /// <summary>cameraOrbitOver: on the avatar the avatar tilts; otherwise the offset turns about the camera's left axis, its angle from up
    /// staying between 1 and 179 degrees. A positive angle takes the camera up.</summary>
    public void OrbitOver(float angle)
    {
        if (FocusOnAvatar && Mode == CameraViewMode.ThirdPerson) { PitchAvatar?.Invoke(angle); return; }
        var unit = Vector3.Normalize(_offsetTarget);
        float fromUp = MathF.Acos(Math.Clamp(unit.Z, -1f, 1f));
        float next = Math.Clamp(fromUp - angle, OrbitMinFromUp, OrbitMaxFromUp);
        _offsetTarget = Vector3.Transform(_offsetTarget, Quaternion.CreateFromAxisAngle(LeftAxis(), fromUp - next));
        ZoomIn(1f);
    }

    /// <summary>cameraZoomIn: the distance to the focus times <paramref name="fraction"/>, within the limits (the nearest from the focus,
    /// the farthest from the draw distance and the region, at most four times the current distance).</summary>
    public void ZoomIn(float fraction)
    {
        if (FocusOnAvatar) { if (fraction != 1f) SetZoom(Zoom * fraction); return; }
        float current = _offsetTarget.Length();
        if (current < 1e-6f) return;
        var unit = _offsetTarget / current;
        float next = current * fraction;
        if (!ConstraintsOff)
        {
            float min = LandMinZoom;
            if (_focusIsAvatar) min = AvatarMinDistance(unit);
            else if (_focusIsObject) min = ObjectMinZoom;
            next = MathF.Max(next, min);
            next = MathF.Min(next, MathF.Min(MaxZoomDistance(), current * 4f));
        }
        else next = MathF.Min(next, MaxZoomDistance());   // constraints off: still held to the largest distance there is
        _offsetTarget = unit * next;
    }

    /// <summary>The largest distance when the camera constraints are off: INT_MAX metres, as a float (llagentcamera.cpp getCameraMaxZoomDistance).</summary>
    public const float UnconstrainedZoomDistance = int.MaxValue;

    /// <summary>getCameraMaxZoomDistance: the smaller of 496 m, the draw distance less a metre, and the region's width less 16 m; with the camera
    /// constraints off, <see cref="UnconstrainedZoomDistance"/>.</summary>
    public float MaxZoomDistance() => ConstraintsOff ? UnconstrainedZoomDistance : MathF.Min(MaxDistanceFromObject, MathF.Min(DrawDistance - 1f, RegionWidth - RegionFudge));

    /// <summary>cameraOrbitIn: moves the camera towards the focus by <paramref name="meters"/> (negative: away). On the avatar in third
    /// person it sets the zoom fraction, and a zoom past the closest (while moving in) asks for mouse-look: the result is true.</summary>
    public bool OrbitIn(float meters)
    {
        if (FocusOnAvatar && Mode == CameraViewMode.ThirdPerson)
        {
            float offsetDistance = MathF.Max(0.001f, CameraOffset.Length());
            float zoom = (_targetDistance - meters) / offsetDistance;
            bool enter = zoom < MinZoom && meters > 0f && MouselookAllowed;
            if (!ConstraintsOff) zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            Zoom = float.IsFinite(zoom) ? zoom : 1f;
            if (enter && AutoEnterMouselook) ChangeToMouselook();
            return enter;
        }
        float current = _offsetTarget.Length();
        if (current < 1e-6f) return false;
        var unit = _offsetTarget / current;
        float next = current - meters;
        if (!ConstraintsOff)
        {
            float min = LandMinZoom;
            if (_focusIsAvatar) min = AvatarMinDistance(unit);
            else if (_focusIsObject) min = ObjectMinZoom;
            next = MathF.Min(MathF.Max(next, min), MaxZoomDistance());
        }
        else next = MathF.Min(next, MaxZoomDistance());   // constraints off: still held to the largest distance there is
        _offsetTarget = unit * next;
        ZoomIn(1f);
        return false;
    }

    /// <summary>handleScrollWheel: a click away from the user (negative) zooms in, a click towards (positive) zooms out, by the fourth root
    /// of two per click. True when the zoom reached mouse-look.</summary>
    public bool ScrollWheel(float clicks)
    {
        if (Mode == CameraViewMode.Mouselook) return false;
        if (FocusOnAvatar)
        {
            float offsetMagnitude = CameraOffset.Length();
            float fraction = _targetDistance / offsetMagnitude;
            fraction *= 1f - MathF.Pow(RootRootTwo, clicks);
            return OrbitIn(fraction * offsetMagnitude);
        }
        return OrbitIn(_offsetTarget.Length() * (1f - MathF.Pow(RootRootTwo, clicks)));
    }

    /// <summary>cameraPanLeft / cameraPanUp / cameraPanIn: the focus, and the camera with it, move along the camera's left, up or at axis.</summary>
    public void PanLeft(float meters) => Pan(LeftAxis(), meters);
    public void PanUp(float meters) { var (f, l) = Axes(); Pan(Vector3.Cross(f, l), meters); }
    public void PanIn(float meters) => Pan(Axes().Forward, meters);

    private void Pan(Vector3 axis, float meters)
    {
        if (FocusOnAvatar) UnlockView();
        if (FocusOnAvatar) return;
        var delta = axis * meters;
        if (_focusIsAvatar || _focusFollow is not null) _focusOffset += delta; else _focusFixed += delta;
        _smoothStop = true;
        _smoothedGlobal = null;
    }

    /// <summary>The camera's forward and left axes from the last pose.</summary>
    private (Vector3 Forward, Vector3 Left) Axes()
    {
        var forward = _lastTarget - _lastEye;
        forward = forward.LengthSquared() < 1e-12f ? Vector3.UnitX : Vector3.Normalize(forward);
        var left = Vector3.Cross(Vector3.UnitZ, forward);
        left = left.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(left);
        return (forward, left);
    }

    private Vector3 LeftAxis() => Axes().Left;

    /// <summary>resetView: the focus returns to the avatar; with <paramref name="changeCamera"/> (View, Reset View, Esc) the camera also goes
    /// back to the default third-person view and zoom. A focus that was elsewhere turns the avatar to face where the camera looked, unless
    /// <paramref name="turnAvatar"/> is off (Esc: the focus is put back on the avatar first, without turning it).</summary>
    public void ResetView(bool changeCamera = false, bool turnAvatar = true)
    {
        if (changeCamera) { ChangeToThirdPerson(); _distance = -1; }
        if (!FocusOnAvatar)
        {
            var (forward, _) = Axes();
            FocusOnAvatar = true;
            _focusFollow = null;
            _focusIsAvatar = _focusIsObject = false;
            _smoothedOffset = null;
            _smoothedGlobal = null;
            float heading = MathF.Atan2(forward.Y, forward.X);
            if (turnAvatar && forward.X * forward.X + forward.Y * forward.Y > 1e-8f) FaceHeading?.Invoke(heading);
        }
        OrbitYaw = OrbitPitch = 0;
        _smoothStop = false;
    }

    /// <summary>changeCameraToMouselook (refused while mouse-look is not allowed).</summary>
    public void ChangeToMouselook()
    {
        if (!MouselookAllowed) return;
        Mode = CameraViewMode.Mouselook;
        _lag = default;
        _smoothedOffset = null;
    }

    public void EnterMouselook() => ChangeToMouselook();

    /// <summary>changeCameraToThirdPerson: the zoom is the default again, the orbit is square behind the avatar, the lag is gone, and the
    /// camera starts at the avatar and eases out to its distance.</summary>
    public void ChangeToThirdPerson()
    {
        Mode = CameraViewMode.ThirdPerson;
        Zoom = 1f;
        SetOrbit(0, 0);
        _lag = default;
        _smoothedOffset = null;
        _distance = MinDistance;
    }

    public void LeaveMouselook() => ChangeToThirdPerson();

    // ---- the pose ----

    /// <summary>The fraction of the way a value with half-life <paramref name="halfLife"/> moves in <paramref name="dt"/> seconds
    /// (llcriticaldamp.cpp: 1 - 2^(-dt/h)); 1 for a half-life of 0.</summary>
    private static float Fraction(double dt, float halfLife) => halfLife <= 0 ? 1f : Math.Clamp(1f - MathF.Pow(2f, -(float)Math.Max(0, dt) / halfLife), 0f, 1f);

    /// <summary>The avatar's position the camera follows: where the avatar is drawn this frame.</summary>
    private Vector3 Anchored(Vector3 centre)
    {
        _anchor = centre;
        return centre;
    }

    /// <summary>The pose in mouse-look for an avatar whose centre is <paramref name="centre"/> and height <paramref name="height"/>,
    /// the view pointing along <paramref name="heading"/> (radians about up from east) and <paramref name="pitch"/> (above the
    /// horizon). The eye is where the avatar's eye is, following the avatar where it is drawn, and the view follows the mouse at once: no offset smoothing, no flight lag, no
    /// constraint plane (llagentcamera.cpp, the mouse-look branches of the camera position update).</summary>
    public (Vector3 Eye, Vector3 Target) MouselookPose(Vector3 centre, float height, float heading, float pitch, double dt)
    {
        var anchor = Anchored(centre);
        var eye = anchor + new Vector3(0, 0, EyeHeightFraction * height - height / 2);
        var look = new Vector3(MathF.Cos(pitch) * MathF.Cos(heading), MathF.Cos(pitch) * MathF.Sin(heading), MathF.Sin(pitch));
        _lastEye = eye;
        _lastTarget = eye + look;
        _hasPose = true;
        _heading = heading;
        return (eye, eye + look);
    }

    /// <summary>The pose for an avatar that is not in the air.</summary>
    public (Vector3 Eye, Vector3 Target) Pose(Vector3 centre, float heading, double dt) => Pose(centre, heading, dt, default, 0f);

    /// <summary>The camera's eye and the point it looks at, for an avatar whose centre is <paramref name="centre"/> facing
    /// <paramref name="heading"/> (radians about up from east), <paramref name="dt"/> seconds after the last pose.
    /// <paramref name="velocity"/>: the avatar's velocity as the region states it; <paramref name="secondsInAir"/>: how long the
    /// region has had it in the air (0 on the ground).</summary>
    public (Vector3 Eye, Vector3 Target) Pose(Vector3 centre, float heading, double dt, Vector3 velocity, float secondsInAir)
    {
        // the avatar where it is drawn (the drawable has damped it); the camera smooths only its offset from it, below
        var anchor = Anchored(centre);
        _heading = heading;
        Vector3 eye, target;
        if (FocusOnAvatar) (eye, target) = RearPose(anchor, heading, dt, velocity, secondsInAir);
        else (eye, target) = FreePose(anchor, dt);
        _lastEye = eye;
        _lastTarget = target;
        _hasPose = true;
        return (eye, target);
    }

    private (Vector3, Vector3) RearPose(Vector3 anchor, float heading, double dt, Vector3 velocity, float secondsInAir)
    {
        var baseAt = anchor + ThirdPersonHeadOffset;
        var turn = AvatarFrame(heading) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, OrbitYaw);
        // the preset pitch tilts about the avatar's own side axis (Y in its frame): positive raises the camera behind it
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, OrbitPitch);
        var direction = Vector3.Transform(Vector3.Normalize(CameraOffset), turn * tilt);

        // the distance asked for, shortened where the region's plane would put the camera behind a wall
        float target = TargetDistance;
        // (not while the avatar sits: llagentcamera.cpp 1855-1856)
        if (CollidePlane != Vector4.Zero && !ConstraintsOff && SeatRotation is null)
        {
            var normal = new Vector3(CollidePlane.X, CollidePlane.Y, CollidePlane.Z);
            float w = CollidePlane.W;
            float full = MathF.Min(CameraOffset.Length() * Zoom, MaxDistance);
            float offsetDotNormal = Vector3.Dot(direction * full, normal);
            if (MathF.Abs(offsetDotNormal) < 0.001f) offsetDotNormal = 0.001f;
            float positionDotNormal = Vector3.Dot(baseAt, normal);
            if (positionDotNormal > w)
            {
                if (offsetDotNormal + positionDotNormal < w)
                    target = full * (positionDotNormal - w - CollideEpsilon) / -offsetDotNormal;
            }
            else if (offsetDotNormal + positionDotNormal > w)
                target = full * (w - positionDotNormal - CollideEpsilon) / offsetDotNormal;
        }
        target = MathF.Max(target, MinDistance);
        _targetDistance = target;
        _distance = _distance < 0 ? target : target + (_distance - target) * (float)Math.Pow(0.5, Math.Max(0, dt) / ZoomHalfLife);
        var eye = baseAt + direction * _distance;

        // the lag behind the avatar's velocity while it is in the air
        var at = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0);
        if (secondsInAir > AirStartSeconds)
        {
            float u = Math.Clamp((secondsInAir - AirStartSeconds) / AirTransitionSeconds, 0f, 1f);
            var wanted = velocity * DynamicCameraStrength / LagDivisor;
            _lag += (wanted - _lag) * (Fraction(dt, LagHalfLife) * u);
            float length = _lag.Length();
            if (length > MaxLag) _lag *= MaxLag / length;
            float dot = Vector3.Dot(_lag - at * (MinLag * u), at);
            if (dot < -(MinLag * u)) _lag -= (dot + MinLag * u) * at;   // the avatar stays in front of the camera
        }
        else _lag -= _lag * Fraction(dt, GroundLagHalfLife);
        eye -= _lag;

        eye = Constrain(eye, anchor);

        // the offset from the avatar, smoothed (not over a long distance)
        var offset = eye - anchor;
        if (_smoothedOffset is not { } before || (offset - before).Length() >= MaxSmoothDistance) _smoothedOffset = offset;
        else _smoothedOffset = before + (offset - before) * Fraction(dt, PositionHalfLife);
        eye = anchor + _smoothedOffset.Value;

        var focus = Vector3.Transform(FocusOffset, turn);
        return (eye, anchor + focus);
    }

    /// <summary>The frame the rear view's offset and focus are turned by: the avatar's heading about up; while it sits, the agent's frame
    /// is kept in the seat's own frame and turned by the seat's rotation, so a tilted seat tilts the camera with it (llagentcamera.cpp 1839-1849
    /// and calcThirdPersonFocusOffset 1686-1689: the at axis, flattened in the world, is carried into the seat's frame and the offset is turned
    /// by the agent's frame and then by the seat).</summary>
    private Quaternion AvatarFrame(float heading)
    {
        if (SeatRotation is not { } seat) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading);
        var flatAt = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
        var at = Vector3.Transform(flatAt, Quaternion.Inverse(seat));
        if (at.LengthSquared() < 1e-12f) return seat;
        at = Vector3.Normalize(at);
        var left = Vector3.Cross(Vector3.UnitZ, at);
        left = left.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(left);
        var up = Vector3.Cross(at, left);
        var basis = new Matrix4x4(at.X, at.Y, at.Z, 0, left.X, left.Y, left.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        return seat * Quaternion.CreateFromRotationMatrix(basis);
    }

    private (Vector3, Vector3) FreePose(Vector3 anchor, double dt)
    {
        var focus = FocusPosition(anchor);
        var eye = Constrain(focus + _offsetTarget, anchor);
        // the camera's own place is smoothed in the region's frame, except for the frame after a pan
        if (!_smoothStop && _smoothedGlobal is { } last && (eye - last).Length() < MaxSmoothDistance)
            eye = last + (eye - last) * Fraction(dt, PositionHalfLife);
        _smoothedGlobal = eye;
        _smoothStop = false;
        return (eye, focus);
    }

    /// <summary>calcCameraPositionTargetGlobal's limits: no farther from the avatar than the draw distance, and half a metre above the
    /// ground (none in mouse-look).</summary>
    private Vector3 Constrain(Vector3 eye, Vector3 anchor)
    {
        // no draw-distance limit for a god, nor where the camera is in a region whose estate the agent can manage (llagentcamera.cpp 1976-1997)
        if (!ConstraintsOff && !Godlike && CanManageEstateAt?.Invoke(eye.X, eye.Y) != true)
        {
            var offset = eye - anchor;
            float distance = offset.Length();
            if (distance > DrawDistance && distance > 0) eye = anchor + offset * (DrawDistance / distance);
        }
        if (LandHeight is { } land)
        {
            float min = land(eye.X, eye.Y) + CameraMinOffGround();
            if (eye.Z < min) eye.Z = min;
        }
        return eye;
    }

    /// <summary>getCameraMinOffGround (llagentcamera.cpp 2122-2135): 0 in mouse-look, 1,000 m under the land with the constraints off
    /// (the use case is retrieving objects), otherwise half a metre above it.</summary>
    public float CameraMinOffGround() => Mode == CameraViewMode.Mouselook ? 0f : ConstraintsOff ? -1000f : MinOffGround;

    /// <summary>calcCameraMinDistance for an avatar: how near the camera may come to the avatar's box (its extents shrunk by the official
    /// fudge factors) along the line to the focus.</summary>
    private float AvatarMinDistance(Vector3 offsetUnit)
    {
        var inverse = Quaternion.Inverse(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _heading));
        var origin = Vector3.Transform(_focusOffset, inverse);
        var camera = Vector3.Transform(offsetUnit, inverse);
        var extents = new Vector3(_focusExtents.X * 0.55f, _focusExtents.Y * 0.7f, _focusExtents.Z * 1.15f);
        float[] e = { extents.X, extents.Y, extents.Z }, o = { origin.X, origin.Y, origin.Z }, c = { camera.X, camera.Y, camera.Z };
        for (int i = 0; i < 3; i++)
        {
            if (c[i] > 0f) e[i] -= o[i] * 2f; else e[i] += o[i] * 2f;
            e[i] = MathF.Max(e[i], 0.001f);
            c[i] = MathF.Max(MathF.Abs(c[i]), 0.001f);
        }
        float norm = MathF.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
        for (int i = 0; i < 3; i++) c[i] /= norm;
        float sx = c[0] / e[0], sy = c[1] / e[1], sz = c[2] / e[2];
        int axis = sx > sy && sx > sz ? 0 : sy > sz ? 1 : 2;
        float distance = c[axis] < 0.001f ? e[axis] * 0.5f : e[axis] * 0.5f / c[axis];
        distance = MathF.Min(distance, 10f * MathF.Sqrt(3f));
        return distance + NearClip + 0.1f;
    }
}

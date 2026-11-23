// SPDX-License-Identifier: LGPL-2.1-only
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

/// <summary>The modifier keys held (the official MASK_SHIFT, MASK_CONTROL, MASK_ALT).</summary>
[Flags]
public enum CameraMask { None = 0, Shift = 1, Control = 2, Alt = 4 }

/// <summary>The camera key commands of key_bindings.xml's third-person section (the names are the official action names).</summary>
public enum CameraKey { SpinAroundCw, SpinAroundCcw, SpinOver, SpinUnder, MoveIn, MoveOut, PanLeft, PanRight, PanUp, PanDown }

/// <summary>
/// The camera's mouse tool and keys (lltoolfocus.cpp LLToolCamera::handleHover; llviewerinput.cpp camera_* actions; key_bindings.xml).
/// <para>Mouse: with the left button down on a valid point (a click on the sky is not one), a drag past 4 pixels in either direction
/// acts by the modifier keys held: Ctrl (with or without Alt) orbits, Ctrl+Shift (with or without Alt) pans, Alt alone turns around with
/// the horizontal movement and zooms with the vertical (0.99 to the power of the pixels). One turn of the orbit is the width of the
/// view; a pan is 3 times the distance to the focus over the view width per pixel.</para>
/// <para>Keys: each bound key unlocks the view and, while held, acts every frame at the key rate (0.05 rising to 1 over a quarter
/// second of holding): 90 degrees a second to turn, the distance to the focus a second to move in or out, 5 metres a second to pan.</para>
/// </summary>
public sealed class CameraControls
{
    /// <summary>SLOP_RANGE, ORBIT_NUDGE_RATE, NUDGE_TIME, the zoom drag's IN_FACTOR and the pan drag's fudge factor.</summary>
    public const float SlopRange = 4f, OrbitNudgeRate = 0.05f, ZoomDragFactor = 0.99f, PanDragFudge = 3f;
    public const double NudgeTime = 0.25;

    public CameraControls(AgentCamera camera) => Camera = camera;

    public AgentCamera Camera { get; }

    /// <summary>The drag is under way on a valid point.</summary>
    public bool Dragging => _down && _valid;

    /// <summary>A key or the wheel moved the camera in past the closest: the view becomes mouse-look.</summary>
    public event Action? MouselookRequested;

    private bool _down, _valid, _outsideX, _outsideY;
    private float _accumX, _accumY;
    private readonly Dictionary<CameraKey, double> _held = new();

    // ---- the mouse ----

    /// <summary>The left button went down with the tool engaged. <paramref name="validPoint"/>: the click hit an object or the ground
    /// (not the sky). Returns whether the drag acts.</summary>
    public bool MouseDown(CameraMask mask, bool validPoint, bool mouseSteering = false)
    {
        _down = true;
        _valid = validPoint;
        _accumX = _accumY = 0;
        _outsideX = _outsideY = false;
        return validPoint;
    }

    public void MouseUp() => _down = _valid = _outsideX = _outsideY = false;

    /// <summary>The drag was started by a left press on the own avatar (mMouseSteering).</summary>
    public bool MouseSteering { get; private set; }

    /// <summary>Whether a left press that reached the own avatar (or an attachment of it with no touch handler) steers.</summary>
    public static bool SteersOnOwnAvatar(CameraMask mask, bool thirdPerson, bool freezeTime, bool hitOwnAvatar) => false;

    /// <summary>The pointer moved by (<paramref name="dx"/>, <paramref name="dyDown"/>) pixels in window terms (y runs down). The official
    /// tool reads the movement in the window system's terms with y up (LLCoordGL; lltoolfocus.cpp handleHover), so a pointer pulled up is a
    /// positive vertical movement there: it zooms in, tilts the camera down and pans down.</summary>
    public void MouseMoveScreen(float dx, float dyDown, CameraMask mask, float viewWidth) => MouseMove(dx, -dyDown, mask, viewWidth);

    /// <summary>The pointer moved by (<paramref name="dx"/>, <paramref name="dy"/>) pixels, y down, with <paramref name="mask"/> held;
    /// <paramref name="viewWidth"/> is the world view's width in pixels.</summary>
    public void MouseMove(float dx, float dy, CameraMask mask, float viewWidth)
    {
        if (!Dragging || viewWidth <= 0) return;
        _accumX += MathF.Abs(dx);
        _accumY += MathF.Abs(dy);
        if (_accumX >= SlopRange) _outsideX = true;
        if (_accumY >= SlopRange) _outsideY = true;
        if (!_outsideX && !_outsideY) return;
        float radiansPerPixel = 2 * MathF.PI / viewWidth;
        if (mask is CameraMask.Control or (CameraMask.Control | CameraMask.Alt))
        {
            if (dx != 0) Camera.OrbitAround(-dx * radiansPerPixel);
            if (dy != 0) Camera.OrbitOver(-dy * radiansPerPixel);
        }
        else if (mask is (CameraMask.Control | CameraMask.Shift) or (CameraMask.Control | CameraMask.Shift | CameraMask.Alt))
        {
            float metresPerPixel = PanDragFudge * Camera.FocusDistance / viewWidth;
            if (dx != 0) Camera.PanLeft(dx * metresPerPixel);
            if (dy != 0) Camera.PanUp(-dy * metresPerPixel);
        }
        else
        {
            if (dx != 0) Camera.OrbitAround(-dx * radiansPerPixel);
            if (dy != 0 && _outsideY) Camera.ZoomIn(MathF.Pow(ZoomDragFactor, dy));
        }
    }

    // ---- the keys ----

    /// <summary>The camera key a key name with the held modifiers is bound to (key_bindings.xml, third_person): Alt moves the camera
    /// about the focus, Ctrl+Alt tilts it with W/S and the up/down arrows, Ctrl+Alt+Shift pans it. The masks must match exactly.</summary>
    public static bool TryKey(string name, CameraMask mask, out CameraKey key)
    {
        key = default;
        if (KeyBindingTable.TryFind(KeyMode.ThirdPerson, name, mask, out var command) && KeyBindingTable.Route(command) is { Kind: KeyRouteKind.Camera } route)
        {
            key = route.Camera;
            return true;
        }
        return false;
    }

    /// <summary>get_orbit_rate: the key's rate after it has been held <paramref name="heldSeconds"/>.</summary>
    public static float OrbitRate(double heldSeconds) =>
        heldSeconds < NudgeTime ? OrbitNudgeRate + (float)heldSeconds * (1f - OrbitNudgeRate) / (float)NudgeTime : 1f;

    /// <summary>A camera key went down (a repeat of a held key is ignored): the view is unlocked.</summary>
    public void KeyDown(CameraKey key, double now)
    {
        if (_held.ContainsKey(key)) return;
        _held[key] = now;
        Camera.UnlockView();
    }

    public void KeyUp(CameraKey key) => _held.Remove(key);

    /// <summary>Whether any camera key is held.</summary>
    public bool KeysHeld => _held.Count > 0;

    /// <summary>Releases every key (the window lost the keyboard).</summary>
    public void ReleaseAll() => _held.Clear();

    /// <summary>One frame of <paramref name="dt"/> seconds: every held key acts at its rate (updateCamera, llagentcamera.cpp 1267-1305).</summary>
    public void Frame(double now, double dt)
    {
        if (_held.Count == 0 || dt <= 0) return;
        float step = (float)dt;
        float Rate(CameraKey k) => _held.TryGetValue(k, out var since) ? OrbitRate(Math.Max(0, now - since)) : 0f;
        float over = Rate(CameraKey.SpinOver) - Rate(CameraKey.SpinUnder);
        if (over != 0) Camera.OrbitOver(over * AgentCamera.OrbitRate * step);
        float around = Rate(CameraKey.SpinAroundCcw) - Rate(CameraKey.SpinAroundCw);
        if (around != 0) Camera.OrbitAround(around * AgentCamera.OrbitRate * step);
        float inOut = Rate(CameraKey.MoveIn) - Rate(CameraKey.MoveOut);
        if (inOut != 0 && Camera.OrbitIn(inOut * Camera.FocusDistance * step)) MouselookRequested?.Invoke();
        float left = Rate(CameraKey.PanRight) - Rate(CameraKey.PanLeft);
        if (left != 0) Camera.PanLeft(left * -AgentCamera.PanRate * step);
        float up = Rate(CameraKey.PanUp) - Rate(CameraKey.PanDown);
        if (up != 0) Camera.PanUp(up * AgentCamera.PanRate * step);
    }
}

// SPDX-License-Identifier: LGPL-2.1-only
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
/**
 * @file llagent.cpp
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
 * @file llmoveview.cpp
 * @brief Container for movement buttons like forward, left, fly
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
 * @file llviewermessage.cpp
 * @brief Dumping ground for viewer-side message system callbacks.
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
 * @file indra_constants.h
 * @brief some useful short term constants for Indra
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
using System.Collections.Immutable;
using System.Numerics;

namespace Viewer.Motion;

/// <summary>
/// The agent control bits of the AgentUpdate message, with the values the protocol defines for them (the official
/// viewer's indra_constants.h, AGENT_CONTROL_*). Only the bits this viewer sends are named.
/// </summary>
public static class AgentControlFlags
{
    public const uint AtPos = 0x1, AtNeg = 0x2, LeftPos = 0x4, LeftNeg = 0x8, UpPos = 0x10, UpNeg = 0x20,
        YawPos = 0x100, YawNeg = 0x200, FastAt = 0x400, FastLeft = 0x800, FastUp = 0x1000, Fly = 0x2000, Stop = 0x4000,
        NudgeAtPos = 0x80000, NudgeAtNeg = 0x100000, NudgeLeftPos = 0x200000, NudgeLeftNeg = 0x400000;

    /// <summary>AGENT_CONTROL_FINISH_ANIM: tells the region that the animation the viewer was playing (stand up, pre-jump, landing) has finished (lasts one update).</summary>
    public const uint FinishAnim = 0x8000;

    /// <summary>The view is mouse-look (stays set while it is), and the left mouse button's down and up bits, for the ordinary
    /// view and for mouse-look (each lasts one update).</summary>
    public const uint Mouselook = 0x40000, LButtonDown = 0x10000000, LButtonUp = 0x20000000, MlLButtonDown = 0x40000000, MlLButtonUp = 0x80000000;

    /// <summary>The bits that stay set between key presses (the others last only while a key holds them).</summary>
    public const uint Persistent = Fly | Mouselook;

    /// <summary>Every bit named here, for checks that a mapping covers them all.</summary>
    public static readonly ImmutableArray<uint> All = [AtPos, AtNeg, LeftPos, LeftNeg, UpPos, UpNeg, YawPos, YawNeg, FastAt, FastLeft, FastUp,
        Fly, Stop, FinishAnim, NudgeAtPos, NudgeAtNeg, NudgeLeftPos, NudgeLeftNeg, Mouselook, LButtonDown, LButtonUp, MlLButtonDown, MlLButtonUp];
}

/// <summary>What a bound key asks the avatar to do (the official viewer's third-person command names in
/// <see cref="Names"/>).</summary>
public enum MoveCommand { Forward, Backward, TurnLeft, TurnRight, SlideLeft, SlideRight, Jump, Down, ToggleFly, Stop, RunForward, RunBackward, RunLeft, RunRight, ToggleRun }

public static class MoveCommands
{
    /// <summary>The command names a key bindings file uses (the official viewer's names for the same actions).</summary>
    public static readonly IReadOnlyDictionary<string, MoveCommand> Names = new Dictionary<string, MoveCommand>(StringComparer.Ordinal)
    {
        ["push_forward"] = MoveCommand.Forward, ["push_backward"] = MoveCommand.Backward,
        ["turn_left"] = MoveCommand.TurnLeft, ["turn_right"] = MoveCommand.TurnRight,
        ["slide_left"] = MoveCommand.SlideLeft, ["slide_right"] = MoveCommand.SlideRight,
        ["jump"] = MoveCommand.Jump, ["push_down"] = MoveCommand.Down,
        ["toggle_fly"] = MoveCommand.ToggleFly, ["stop_moving"] = MoveCommand.Stop,
        ["run_forward"] = MoveCommand.RunForward, ["run_backward"] = MoveCommand.RunBackward,
        ["run_left"] = MoveCommand.RunLeft, ["run_right"] = MoveCommand.RunRight, ["toggle_run"] = MoveCommand.ToggleRun,
    };
}

/// <summary>One AgentUpdate as the viewer asks for it: the control bits, the body's rotation (the heading the region
/// moves the avatar along), and the camera's eye and the point it looks at (region metres). <paramref name="HeadRotation"/> null: the head
/// follows the body (the official viewer's rule outside mouse-look, see <see cref="AgentHead"/>). <paramref name="State"/> and
/// <paramref name="Flags"/> are the update's render state and flag bytes (<see cref="AgentRenderStates"/>).</summary>
public readonly record struct AgentUpdate(uint Controls, Quaternion BodyRotation, Vector3 CameraEye, Vector3 CameraTarget, Quaternion? HeadRotation = null, byte State = 0, byte Flags = 0);

/// <summary>The render state byte and the flags byte of an AgentUpdate, with the values the official viewer sends (llagent.cpp
/// AGENT_STATE_TYPING and AGENT_STATE_EDITING; llviewermessage.cpp AU_FLAGS_HIDETITLE and AU_FLAGS_CLIENT_AUTOPILOT).</summary>
public static class AgentRenderStates
{
    public const byte Typing = 0x04, Editing = 0x10;
    public const byte HideTitle = 0x01, Autopilot = 0x02;

    public static byte State(bool typing, bool editing) => (byte)((typing ? Typing : 0) | (editing ? Editing : 0));
    public static byte Flags(bool hideTitle, bool autopilot) => (byte)((hideTitle ? HideTitle : 0) | (autopilot ? Autopilot : 0));
}

/// <summary>The head rotation of an AgentUpdate (behaviour taken from the official viewer's llagent.cpp getHeadRotation, in our own words).</summary>
public static class AgentHead
{
    /// <summary>The head rotation to send: null outside mouse-look (the avatar's own rotation, which is the body rotation sent beside it); in
    /// mouse-look, the rotation whose forward axis is the camera's, whose up axis is perpendicular to it and to the body's left axis, and whose
    /// left axis completes the frame. Null too when the camera looks along the body's left axis (no such up axis).</summary>
    public static Quaternion? Rotation(bool mouseLook, Quaternion body, Vector3 cameraAt)
    {
        if (!mouseLook) return null;
        var at = cameraAt.LengthSquared() > 0 ? Vector3.Normalize(cameraAt) : Vector3.UnitX;
        var up = Vector3.Cross(at, Vector3.Transform(Vector3.UnitY, body));
        if (up.LengthSquared() < 1e-10f) return null;
        up = Vector3.Normalize(up);
        var left = Vector3.Cross(up, at);
        var frame = new Matrix4x4(at.X, at.Y, at.Z, 0, left.X, left.Y, left.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);   // rows: where the x, y and z axes go
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(frame));
    }
}

/// <summary>
/// The keyboard's side of the user's own avatar, as the official viewer drives it (behaviour taken from its
/// llviewerinput.cpp, llagent.cpp and llmoveview.cpp): held commands become control bits each frame, and the turn keys
/// turn the avatar's heading here, at once, the region following the body rotation it is sent.
/// <list type="bullet">
/// <item>Forward, back and the slides: for the first <see cref="NudgeSeconds"/> of a press (or its first
/// <see cref="NudgeFrames"/> frames) the nudge bit only, then the move bit with its fast bit.</item>
/// <item>Turning: <see cref="TurnRate"/> radians a second, ramping from <see cref="TurnNudgeRate"/> of it to all of it
/// over the first <see cref="NudgeSeconds"/>; the yaw bit of the direction. Both turn keys together cancel the turn
/// and send both bits.</item>
/// <item>Jump (up): the up bit and its fast bit; held past <see cref="FlyAfterSeconds"/> (and
/// <see cref="FlyAfterFrames"/> frames) while walking, it starts flying. Down: the down bit and the fast up bit.</item>
/// <item>Fly: a press toggles the persistent fly bit. Landing: flying, down held, the avatar not in the air and moving
/// slower than <see cref="AutoLandSpeed"/> across the ground turns flying off.</item>
/// <item>Stop: the stop bit while the stop-moving key is held (llviewerinput.cpp stop_moving). The viewer's own stops (llagent.cpp:
/// going away, the end of autopilot; llagentcamera.cpp: the camera turning to a chatter) set it through
/// <see cref="SetControlFlags"/>, for one update.</item>
/// <item>Mouse-look (behaviour taken from the official viewer's lltoolgun.cpp, llagent.cpp and llviewerinput.cpp, in our own words): while
/// <see cref="MouseLook"/> is on, the mouse turns the heading and tilts the view (<see cref="LookBy"/>, at the rate
/// <see cref="MouseLookRules.RadiansPerPixel"/> gives for <see cref="MouseSensitivity"/>), the mouse-look bit goes out with every
/// update, the turn keys do nothing, and the left button reaches a script that took it (<see cref="Script"/>) as one down and one
/// up bit. A script that took the turn controls (not passing them on) keeps the keys from turning the heading, and one that took
/// the up control keeps a held jump from starting a flight; the bits go out unchanged in both cases.</item>
/// <item>Running (behaviour taken from the official viewer's llviewerinput.cpp, llagent.cpp and llviewermessage.cpp, in
/// our own words): the avatar either walks or runs, and the region is told each change (the reliable SetAlwaysRun
/// message), through <see cref="WalkRunChanged"/>. Pressing a walk key (forward, back, either slide) while not running
/// and the same key was the last walk key pressed less than <see cref="NudgeSeconds"/> ago (a double-tap) runs until
/// that key comes up; any walk-key press while not running starts the window afresh for that key. A run key
/// (<see cref="MoveCommand.RunForward"/> and the others, unbound by default as in the official viewer) moves like its
/// walk key and runs while held. <see cref="MoveCommand.ToggleRun"/> flips <see cref="AlwaysRun"/>, which keeps the avatar
/// running between key presses. Losing focus ends a temporary run. <see cref="ResendRun"/> tells the region the state
/// again (the official viewer does after every region arrival). One deliberate difference: the official run keys clear the
/// running state when released even with Always Run on; here Always Run keeps it.</item>
/// <item>Release: a key that comes up sends nothing of its own (llviewerinput.cpp: every movement handler returns on key-up).
/// Its bits are simply no longer set in the next update (llagent.cpp: the control bits are cleared after each update and
/// set again by the keys still held), and the region brings the avatar to rest in its own way. No stop bit goes out for
/// a released walk, run, slide, fly or turn key, or when focus is lost.</item>
/// </list>
/// Key presses count only while the window has focus; losing focus releases every held key at once. Times are seconds
/// on the caller's clock. Not thread-safe: the main thread only.
/// </summary>
public sealed class AgentControlInput
{
    public const double NudgeSeconds = 0.25, FlyAfterSeconds = 0.5;
    public const int NudgeFrames = 2, FlyAfterFrames = 4;
    public const float TurnRate = 90f * MathF.PI / 180f, TurnNudgeRate = 0.05f, AutoLandSpeed = 4f;

    private sealed class Held(double since, MoveCommand source) { public double Since = since; public readonly MoveCommand Source = source; public int Frames; }

    private readonly Dictionary<MoveCommand, Held> _held = new();
    private bool _fly;
    /// <summary>Bits set through <see cref="SetControlFlags"/>, waiting for the next update.</summary>
    private uint _set;
    private bool _running, _alwaysRun;
    /// <summary>The walk key whose press started the double-tap window, and when the last walk key was pressed.</summary>
    private MoveCommand? _tapKey;
    private double _tapAt = double.NegativeInfinity;

    /// <summary>Whether the window has focus; key presses are ignored while it has not.</summary>
    public bool Focused { get; private set; } = true;

    /// <summary>The avatar's heading here (radians about up from east); null until <see cref="AdoptHeading"/>.</summary>
    public float? Heading { get; private set; }

    public bool Flying => _fly;

    /// <summary>How long after a jump input a landing animation's end is not reported to the region (the official viewer's RecentJumpThresholdSecs).</summary>
    public const double RecentJumpThresholdSeconds = 1.0;

    /// <summary>The time (on the caller's clock) of the last jump input given while not flying; 0 before any.</summary>
    public double LastJumpInputTime { get; private set; }

    private bool _finishAnim;

    /// <summary>
    /// One of the viewer's own animations has run out (the official LLAgent::onAnimStop): the end of stand up, pre-jump or a landing asks the
    /// region to go on, with the finish-animation bit in the next update. A landing's end is not reported while the jump key is down or within
    /// <see cref="RecentJumpThresholdSeconds"/> of the last jump input, so a quick second jump is not cut short; the end of pre-jump always is,
    /// or a quick tap from standing could stall.
    /// </summary>
    public void AnimationStopped(Guid animation, double now)
    {
        if (animation == AnimationIds.StandUp)
        {
            _finishAnim = true;
        }
        else if (animation == AnimationIds.PreJump || animation == AnimationIds.Land || animation == AnimationIds.MediumLand)
        {
            bool landing = animation != AnimationIds.PreJump;
            bool upHeld = (Controls & AgentControlFlags.UpPos) != 0;
            bool recentJump = LastJumpInputTime > 0.0 && now - LastJumpInputTime < RecentJumpThresholdSeconds;
            if (!upHeld && !(landing && recentJump)) _finishAnim = true;
        }
    }

    /// <summary>The control bits of the last <see cref="Frame"/>.</summary>
    public uint Controls { get; private set; }

    public bool IsHeld(MoveCommand c) => _held.ContainsKey(c);

    /// <summary>Whether the view is mouse-look.</summary>
    public bool MouseLook { get; private set; }
    /// <summary>How far the view points above the horizon in mouse-look (radians; below is negative); 0 outside it.</summary>
    public float Pitch { get; private set; }
    /// <summary>The mouse sensitivity setting (the official viewer's MouseSensitivity, 0 to 15), whether the mouse is inverted
    /// (InvertMouse) and whether its movement is smoothed (MouseSmooth).</summary>
    public float MouseSensitivity { get; set; } = MouseLookRules.DefaultSensitivity;
    public bool InvertMouse { get; set; }
    public bool SmoothMouse { get; set; }
    /// <summary>The controls scripts in the region have taken; null: none known.</summary>
    public ScriptControls? Script { get; set; }

    /// <summary>The view becomes mouse-look: the turn keys are let go, the mouse-look bit goes out with every update.</summary>
    public void EnterMouseLook()
    {
        if (MouseLook) return;
        MouseLook = true;
        _smoother.Reset();
        _buttonDown = _buttonUp = false;
        // the turn keys do not exist in this view (the official first-person bindings slide instead)
        foreach (var turn in new[] { MoveCommand.TurnLeft, MoveCommand.TurnRight })
            _held.Remove(turn);
    }

    /// <summary>The view goes back to the ordinary one: no pitch, no mouse-look bit.</summary>
    public void LeaveMouseLook()
    {
        if (!MouseLook) return;
        MouseLook = false;
        Pitch = 0;
        _buttonDown = _buttonUp = false;
    }

    /// <summary>The mouse moved <paramref name="dx"/> to the right and <paramref name="dy"/> down (pixels) in mouse-look, over a
    /// frame of <paramref name="dt"/> seconds: the heading turns and the view tilts.</summary>
    public void LookBy(float dx, float dy, double dt)
    {
        if (!MouseLook || Heading is not float heading) return;
        if (SmoothMouse)
        {
            var (sx, sy) = _smoother.Step(dx, dy, dt);
            (dx, dy) = (sx, sy);
        }
        float perPixel = MouseLookRules.RadiansPerPixel(MouseSensitivity);
        // right turns clockwise seen from above (the heading falls); up the screen (a falling y) looks up unless the mouse is inverted
        Heading = MathF.IEEERemainder(heading - dx * perPixel, 2 * MathF.PI);
        Pitch = Math.Clamp(Pitch - dy * perPixel * (InvertMouse ? -1f : 1f), MouseLookRules.MinPitch, MouseLookRules.MaxPitch);
    }

    /// <summary>The left mouse button went down or up. A script that wants it gets the button bits for one update each.</summary>
    public void LeftButton(bool down)
    {
        if (Script?.LeftButtonGrabbed(MouseLook) != true) return;
        if (down) _buttonDown = true;
        else _buttonUp = true;
    }

    private readonly MouseSmoother _smoother = new();
    private bool _buttonDown, _buttonUp;

    /// <summary>Whether double-tap running is on (the official viewer's AllowTapTapHoldRun, on by default).</summary>
    public bool AllowDoubleTapRun { get; set; } = true;

    /// <summary>Whether holding the jump key makes the avatar fly (the official viewer's AutomaticFly, on by default).</summary>
    public bool AutomaticFly { get; set; } = true;
    /// <summary>Whether the avatar is trying to run now (a temporary run, a run key, or Always Run).</summary>
    public bool Running => _running;
    /// <summary>Whether Always Run is on: the avatar runs unless told otherwise.</summary>
    public bool AlwaysRun => _alwaysRun;
    /// <summary>A key that moves the avatar (a walk, slide or run key, jump, push down, a turn key, the fly toggle) went down, or is held
    /// during a <see cref="Frame"/>: the official viewer returns the camera to the avatar each time (llagent.cpp moveAt 666, moveAtNudge 696,
    /// moveLeft 721, moveLeftNudge 746, moveUp 771, moveYaw 796, toggleFlying 925: resetView). The look keys (movePitch), the stop key and
    /// the run toggle do not. Main thread.</summary>
    public event Action? WalkKeyPressed;

    private static bool ResetsView(MoveCommand c) => IsHorizontal(c) || c is MoveCommand.Jump or MoveCommand.Down or MoveCommand.TurnLeft or MoveCommand.TurnRight;

    /// <summary>The run state to tell the region (true: run) after a change; main thread.</summary>
    public event Action<bool>? WalkRunChanged;

    /// <summary>Takes the Always Run default (at start), telling the region when it changes.</summary>
    public void SetAlwaysRun(bool on)
    {
        if (on == _alwaysRun) return;
        _alwaysRun = on;
        _running = on;
        WalkRunChanged?.Invoke(on);
    }

    /// <summary>Tells the region the run state again (after a region arrival): running, or Always Run.</summary>
    public void ResendRun() => WalkRunChanged?.Invoke(_running || _alwaysRun);

    private void SetRunning(bool run)
    {
        if (_running == run) return;
        _running = run;
        WalkRunChanged?.Invoke(run);
    }

    private static bool IsRunKey(MoveCommand c) => c is MoveCommand.RunForward or MoveCommand.RunBackward or MoveCommand.RunLeft or MoveCommand.RunRight;

    /// <summary>The key movement a command makes: a run key moves like its walk key.</summary>
    private static MoveCommand Walk(MoveCommand c) => c switch
    {
        MoveCommand.RunForward => MoveCommand.Forward, MoveCommand.RunBackward => MoveCommand.Backward,
        MoveCommand.RunLeft => MoveCommand.SlideLeft, MoveCommand.RunRight => MoveCommand.SlideRight, _ => c,
    };

    /// <summary>A key that went down (a walk key or a run key) starts running: the double-tap, or the run key.</summary>
    private void PressedForRun(MoveCommand c, double now)
    {
        if (IsRunKey(c))
        {
            _tapKey = Walk(c);
            SetRunning(true);
        }
        else if (IsHorizontal(c) && AllowDoubleTapRun && !_running)
        {
            if (_tapKey == c && now - _tapAt < NudgeSeconds) SetRunning(true);
            _tapAt = now;   // any walk-key press starts the window afresh
            _tapKey = c;
        }
    }

    /// <summary>A key that was held (a walk key or a run key) came up: a temporary run it started ends.</summary>
    private void ReleasedForRun(MoveCommand c)
    {
        if (IsRunKey(c))
        {
            if (_tapKey == Walk(c)) _tapKey = null;
            SetRunning(_alwaysRun);
        }
        else if (IsHorizontal(c) && _tapKey == c && _running && !_alwaysRun)
            SetRunning(false);
    }

    /// <summary>Sets control bits on the agent from outside the keyboard (llagent.cpp setControlFlags): they go out with the next
    /// update beside whatever the keys set, and are cleared after it, as the official viewer clears every bit but the persistent
    /// ones once an update has carried them. This is how the viewer's own stops reach the region: <see cref="AgentControlFlags.Stop"/>
    /// when the agent goes away, when autopilot ends or reaches its target, and when the camera turns to a chatter.</summary>
    public void SetControlFlags(uint mask) => _set |= mask;

    /// <summary>Whether a key that moves the avatar along the ground or up and down is held.</summary>
    public bool Moving => _held.Keys.Any(c => IsHorizontal(c) || c is MoveCommand.Jump or MoveCommand.Down);

    private static bool IsHorizontal(MoveCommand c) => c is MoveCommand.Forward or MoveCommand.Backward or MoveCommand.SlideLeft or MoveCommand.SlideRight;

    /// <summary>A key bound to <paramref name="c"/> went down. <paramref name="synthetic"/>: from the automation API,
    /// which presses keys as a focused window would.</summary>
    public void Press(MoveCommand c, double now, bool synthetic = false)
    {
        if (!Focused && !synthetic) return;
        if (c == MoveCommand.ToggleFly)
        {
            _fly = !_fly;
            WalkKeyPressed?.Invoke();
            return;
        }
        if (c == MoveCommand.ToggleRun)
        {
            SetAlwaysRun(!_alwaysRun);
            return;
        }
        if (MouseLook && c is MoveCommand.TurnLeft or MoveCommand.TurnRight) return;   // no turn keys in mouse-look
        var key = Walk(c);
        if (_held.ContainsKey(key)) return;   // a repeat of a held key changes nothing
        _held[key] = new Held(now, c);
        PressedForRun(c, now);
        if (ResetsView(key)) WalkKeyPressed?.Invoke();
    }

    public void Release(MoveCommand c)
    {
        var key = Walk(c);
        if (!_held.TryGetValue(key, out var held) || held.Source != c) return;   // a key other than the one that holds it
        _held.Remove(key);   // nothing is sent for a key-up: its bits are just not set in the next update
        ReleasedForRun(c);
    }

    /// <summary>The window gained or lost focus. Losing it releases every held key.</summary>
    public void SetFocused(bool focused)
    {
        Focused = focused;
        if (focused) return;
        foreach (var h in _held.Values.ToList()) ReleasedForRun(h.Source);
        _held.Clear();
    }

    /// <summary>Takes the region's heading for the avatar (the first time it is placed, after a region change or a
    /// sit), dropping any turn of its own.</summary>
    public void AdoptHeading(float heading) => Heading = heading;

    /// <summary>Turns the heading by <paramref name="angle"/> radians, left for a positive one (llagent.cpp yaw), unless a script took the
    /// turn controls. The mouse steering turns the avatar through this.</summary>
    public void Yaw(float angle)
    {
        if (Heading is not float heading || Script?.RotateGrabbed == true) return;
        Heading = MathF.IEEERemainder(heading + angle, 2 * MathF.PI);
    }

    /// <summary>Sets whether the agent flies, from outside the keys: the saved state of the last session's end at login, and off
    /// when the agent sits down.</summary>
    public void AdoptFlying(bool flying) => _fly = flying;

    /// <summary>One frame, <paramref name="dt"/> seconds after the last: turns the heading by the turn keys and returns
    /// the control bits. <paramref name="inAir"/> and <paramref name="groundSpeed"/> describe the avatar as the region
    /// last stated it (for landing).</summary>
    public uint Frame(double now, double dt, bool inAir, float groundSpeed)
    {
        uint c = 0;
        foreach (var (cmd, h) in _held) h.Frames++;
        if (_held.Keys.Any(ResetsView)) WalkKeyPressed?.Invoke();   // a held key resets the view in every frame, as its handler does

        Move(MoveCommand.Forward, AgentControlFlags.NudgeAtPos, AgentControlFlags.AtPos | AgentControlFlags.FastAt);
        Move(MoveCommand.Backward, AgentControlFlags.NudgeAtNeg, AgentControlFlags.AtNeg | AgentControlFlags.FastAt);
        Move(MoveCommand.SlideLeft, AgentControlFlags.NudgeLeftPos, AgentControlFlags.LeftPos | AgentControlFlags.FastLeft);
        Move(MoveCommand.SlideRight, AgentControlFlags.NudgeLeftNeg, AgentControlFlags.LeftNeg | AgentControlFlags.FastLeft);

        float yawKey = 0;
        if (_held.TryGetValue(MoveCommand.TurnLeft, out var left)) { yawKey += YawRate(now - left.Since); c |= AgentControlFlags.YawPos; }
        if (_held.TryGetValue(MoveCommand.TurnRight, out var right)) { yawKey -= YawRate(now - right.Since); c |= AgentControlFlags.YawNeg; }
        if (left is not null && right is not null) yawKey = 0;   // both ways cancel; both bits still go to the region
        // a script that took the turn controls keeps the keys from turning the avatar, but still gets the bits (llagent.cpp, yaw)
        if (Heading is float heading && yawKey != 0 && dt > 0 && Script?.RotateGrabbed != true)
            Heading = MathF.IEEERemainder(heading + TurnRate * yawKey * (float)dt, 2 * MathF.PI);

        if (_held.TryGetValue(MoveCommand.Jump, out var jump))
        {
            // a script that took the up control keeps a held jump from becoming a flight (llviewerinput.cpp, agent_jump)
            if (!_fly && AutomaticFly && now - jump.Since >= FlyAfterSeconds && jump.Frames > FlyAfterFrames && Script?.UpGrabbed != true) _fly = true;
            if (!_fly) LastJumpInputTime = now;   // moveUp notes the time of a jump input given while not flying
            c |= AgentControlFlags.UpPos | AgentControlFlags.FastUp;
        }
        if (_held.ContainsKey(MoveCommand.Down))
        {
            c |= AgentControlFlags.UpNeg | AgentControlFlags.FastUp;
            if (_fly && !inAir && groundSpeed < AutoLandSpeed) _fly = false;
        }
        if (_held.ContainsKey(MoveCommand.Stop)) c |= AgentControlFlags.Stop;   // the stop-moving key, while it is down
        c |= _set;   // bits set from outside the keyboard last one update
        _set = 0;
        if (_fly) c |= AgentControlFlags.Fly;
        if (MouseLook) c |= AgentControlFlags.Mouselook;
        // the left button reaches a script that asked for it as one down and one up bit, each for one update
        if (_buttonDown) c |= MouseLook ? AgentControlFlags.MlLButtonDown : AgentControlFlags.LButtonDown;
        if (_buttonUp) c |= MouseLook ? AgentControlFlags.MlLButtonUp : AgentControlFlags.LButtonUp;
        _buttonDown = _buttonUp = false;
        if (_finishAnim) { c |= AgentControlFlags.FinishAnim; _finishAnim = false; }   // lasts one update
        return Controls = c;

        void Move(MoveCommand cmd, uint nudge, uint move)
        {
            if (!_held.TryGetValue(cmd, out var h)) return;
            c |= now - h.Since < NudgeSeconds || h.Frames <= NudgeFrames ? nudge : move;
        }
    }

    /// <summary>The turn rate's fraction for a key held <paramref name="held"/> seconds.</summary>
    public static float YawRate(double held) =>
        held < NudgeSeconds ? TurnNudgeRate + (float)held * (1 - TurnNudgeRate) / (float)NudgeSeconds : 1f;

    /// <summary>The body rotation for a heading (radians about up from east).</summary>
    public static Quaternion BodyRotation(float heading) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading);

    /// <summary>The body rotation for a heading and a view pointing <paramref name="pitch"/> radians above the horizon (the
    /// official viewer sends the whole frame's rotation in mouse-look, and the region keeps only its heading to move the avatar).</summary>
    public static Quaternion BodyRotation(float heading, float pitch) =>
        pitch == 0 ? BodyRotation(heading) : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -pitch);
}

/// <summary>
/// When an AgentUpdate goes to the region, as the official viewer decides it (llviewermessage.cpp, send_agent_update):
/// at once when the control bits change, and once more after that change; otherwise no more than
/// <see cref="MaxPerSecond"/> a second, and then only when the camera moved more than 1 cm or turned more than 0.1
/// degree, or the body turned by about 2.5 degrees; and at least once a second whatever happens. Not thread-safe.
/// </summary>
public sealed class AgentUpdateRule
{
    public const double MaxPerSecond = 125, KeepAliveSeconds = 1;
    private const float MoveThreshold = 0.01f, CameraTurnThreshold = 0.2f * MathF.PI / 360f;
    /// <summary>send_agent_update: a body or head rotation whose dot product with the last sent is below <c>MinHeadDot</c> (about 2.5 degrees) is
    /// sent at once; above <c>MaxHeadDot</c> (about 0.5 degrees) it is close enough; between them the threshold rises towards the upper
    /// figure over <c>HeadGrowthSeconds</c>, so a slow, small head movement is still sent after a while.</summary>
    private const double MinHeadDot = 0.9997, MaxHeadDot = 0.99999, HeadGrowthSeconds = 0.5;

    private double _last = double.NegativeInfinity;
    private uint _lastControls;
    private bool _followUp;
    private Vector3 _lastEye, _lastAt;
    private Quaternion _lastBody = Quaternion.Identity, _lastHead = Quaternion.Identity;
    private byte _lastState, _lastFlags;
    private (double Last, uint Controls, bool FollowUp, Vector3 Eye, Vector3 At, Quaternion Body, Quaternion Head, byte State, byte Flags, int Sent)? _before;

    public int Sent { get; private set; }

    /// <summary>The update <see cref="ShouldSend"/> just approved was not sent after all (the session would not take it): the rule goes back to what it was before,
    /// as the official viewer, which only records what it sent.</summary>
    public void Unsent()
    {
        if (_before is not { } b) return;
        (_last, _lastControls, _followUp, _lastEye, _lastAt, _lastBody, _lastHead, _lastState, _lastFlags, Sent) = (b.Last, b.Controls, b.FollowUp, b.Eye, b.At, b.Body, b.Head, b.State, b.Flags, b.Sent);
        _before = null;
    }

    /// <summary>Whether <paramref name="u"/> should be sent at <paramref name="now"/>; when true it counts as sent.</summary>
    public bool ShouldSend(in AgentUpdate u, double now)
    {
        double since = now - _last;
        bool changed = u.Controls != _lastControls;
        if (!changed && since < 1 / MaxPerSecond) return false;
        var at = u.CameraTarget - u.CameraEye;
        at = at.LengthSquared() > 0 ? Vector3.Normalize(at) : Vector3.UnitX;
        var head = u.HeadRotation ?? u.BodyRotation;
        bool send = changed || since > KeepAliveSeconds || _followUp
            || u.State != _lastState || u.Flags != _lastFlags   // llviewermessage.cpp: a change of render state or flags is sent (after the throttle)
            || (u.CameraEye - _lastEye).Length() > MoveThreshold
            || (at - _lastAt).Length() > CameraTurnThreshold
            || MathF.Abs(Quaternion.Dot(u.BodyRotation, _lastBody)) < MinHeadDot;
        if (!send)
        {
            double headDot = Math.Abs((double)Quaternion.Dot(head, _lastHead));
            if (headDot <= MaxHeadDot)
                send = headDot < MinHeadDot || headDot < MinHeadDot + since * ((MaxHeadDot - MinHeadDot) / HeadGrowthSeconds);
        }
        if (!send) return false;
        _before = (_last, _lastControls, _followUp, _lastEye, _lastAt, _lastBody, _lastHead, _lastState, _lastFlags, Sent);
        _followUp = changed;
        _lastState = u.State;
        _lastFlags = u.Flags;
        _last = now;
        _lastControls = u.Controls;
        _lastEye = u.CameraEye;
        _lastAt = at;
        _lastBody = u.BodyRotation;
        _lastHead = head;
        Sent++;
        return true;
    }
}

/// <summary>The ids of the animations whose end the viewer reports to the region (llanimationstates.cpp).</summary>
public static class AnimationIds
{
    public static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    public static readonly Guid StandUp = Guid.Parse("3da1d753-028a-5446-24f3-9c9b856d9422");
    public static readonly Guid PreJump = Guid.Parse("7a4e87fe-de39-6fcb-6223-024b00893244");
    public static readonly Guid Land = Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f");
    public static readonly Guid MediumLand = Guid.Parse("f4f00d6e-b9fe-9292-f4cb-0ae06ea58d57");
}

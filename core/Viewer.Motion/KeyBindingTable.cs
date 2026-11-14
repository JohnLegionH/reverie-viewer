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
/*
 * Ported to C# from the Second Life Viewer 26.4 (release tag Second_Life_Release#e960123-26.4, commit e960123230d521e8557a9582100bebcac36a7ca2) source files whose notices are above (the key table also from
 * app_settings/key_bindings.xml, a data file with no notice of its own).
 * Adapted 2026 by Legion Builds. Copyright (c) 2026 Legion Builds for the changes.
 */
namespace Viewer.Motion;

/// <summary>The four input modes of key_bindings.xml.</summary>
public enum KeyMode { FirstPerson, ThirdPerson, Sitting, EditAvatar }

/// <summary>One line of the table: a key (or a mouse button) with the modifiers it needs exactly, and the command it gives.</summary>
public readonly record struct KeyBindingEntry(KeyMode Mode, string Key, string Mouse, CameraMask Mask, string Command);

/// <summary>What a command does here.</summary>
public enum KeyRouteKind { None, Move, Camera, StartChat, StartGesture, ToggleVoice, ScriptLeftButton }

/// <summary>Where a command goes: an avatar movement, a camera key, or something else the host does. A sitting command moves the camera
/// unless a script has taken the matching control or the seat has its own camera; then it sends <see cref="Fallback"/> as the avatar's key.</summary>
public readonly record struct KeyRoute(KeyRouteKind Kind, MoveCommand Move = default, CameraKey Camera = default, MoveCommand? Fallback = null);

public static class KeyBindingTable
{
    /// <summary>The whole default table of key_bindings.xml, in the file's order.</summary>
    public static IReadOnlyList<KeyBindingEntry> All { get; } =
    [
        // first_person
        new(KeyMode.FirstPerson, "A", "", CameraMask.None, "slide_left"),
        new(KeyMode.FirstPerson, "D", "", CameraMask.None, "slide_right"),
        new(KeyMode.FirstPerson, "W", "", CameraMask.None, "push_forward"),
        new(KeyMode.FirstPerson, "S", "", CameraMask.None, "push_backward"),
        new(KeyMode.FirstPerson, "E", "", CameraMask.None, "jump"),
        new(KeyMode.FirstPerson, "C", "", CameraMask.None, "push_down"),
        new(KeyMode.FirstPerson, "F", "", CameraMask.None, "toggle_fly"),
        new(KeyMode.FirstPerson, "LEFT", "", CameraMask.None, "slide_left"),
        new(KeyMode.FirstPerson, "RIGHT", "", CameraMask.None, "slide_right"),
        new(KeyMode.FirstPerson, "UP", "", CameraMask.None, "push_forward"),
        new(KeyMode.FirstPerson, "DOWN", "", CameraMask.None, "push_backward"),
        new(KeyMode.FirstPerson, "PGUP", "", CameraMask.None, "jump"),
        new(KeyMode.FirstPerson, "PGDN", "", CameraMask.None, "push_down"),
        new(KeyMode.FirstPerson, "HOME", "", CameraMask.None, "toggle_fly"),
        new(KeyMode.FirstPerson, "SPACE", "", CameraMask.None, "stop_moving"),
        new(KeyMode.FirstPerson, "ENTER", "", CameraMask.None, "start_chat"),
        new(KeyMode.FirstPerson, "DIVIDE", "", CameraMask.None, "start_gesture"),
        new(KeyMode.FirstPerson, "", "MMB", CameraMask.None, "toggle_voice"),
        new(KeyMode.FirstPerson, "", "LMB", CameraMask.None, "script_trigger_lbutton"),
        // third_person
        new(KeyMode.ThirdPerson, "A", "", CameraMask.None, "turn_left"),
        new(KeyMode.ThirdPerson, "D", "", CameraMask.None, "turn_right"),
        new(KeyMode.ThirdPerson, "A", "", CameraMask.Shift, "slide_left"),
        new(KeyMode.ThirdPerson, "D", "", CameraMask.Shift, "slide_right"),
        new(KeyMode.ThirdPerson, "W", "", CameraMask.None, "push_forward"),
        new(KeyMode.ThirdPerson, "S", "", CameraMask.None, "push_backward"),
        new(KeyMode.ThirdPerson, "E", "", CameraMask.None, "jump"),
        new(KeyMode.ThirdPerson, "C", "", CameraMask.None, "push_down"),
        new(KeyMode.ThirdPerson, "F", "", CameraMask.None, "toggle_fly"),
        new(KeyMode.ThirdPerson, "SPACE", "", CameraMask.None, "stop_moving"),
        new(KeyMode.ThirdPerson, "ENTER", "", CameraMask.None, "start_chat"),
        new(KeyMode.ThirdPerson, "DIVIDE", "", CameraMask.None, "start_gesture"),
        new(KeyMode.ThirdPerson, "LEFT", "", CameraMask.None, "turn_left"),
        new(KeyMode.ThirdPerson, "LEFT", "", CameraMask.Shift, "slide_left"),
        new(KeyMode.ThirdPerson, "RIGHT", "", CameraMask.None, "turn_right"),
        new(KeyMode.ThirdPerson, "RIGHT", "", CameraMask.Shift, "slide_right"),
        new(KeyMode.ThirdPerson, "UP", "", CameraMask.None, "push_forward"),
        new(KeyMode.ThirdPerson, "DOWN", "", CameraMask.None, "push_backward"),
        new(KeyMode.ThirdPerson, "PGUP", "", CameraMask.None, "jump"),
        new(KeyMode.ThirdPerson, "PGDN", "", CameraMask.None, "push_down"),
        new(KeyMode.ThirdPerson, "HOME", "", CameraMask.None, "toggle_fly"),
        new(KeyMode.ThirdPerson, "LEFT", "", CameraMask.Alt, "spin_around_cw"),
        new(KeyMode.ThirdPerson, "RIGHT", "", CameraMask.Alt, "spin_around_ccw"),
        new(KeyMode.ThirdPerson, "UP", "", CameraMask.Alt, "move_forward"),
        new(KeyMode.ThirdPerson, "DOWN", "", CameraMask.Alt, "move_backward"),
        new(KeyMode.ThirdPerson, "PGUP", "", CameraMask.Alt, "spin_over"),
        new(KeyMode.ThirdPerson, "PGDN", "", CameraMask.Alt, "spin_under"),
        new(KeyMode.ThirdPerson, "A", "", CameraMask.Alt, "spin_around_cw"),
        new(KeyMode.ThirdPerson, "D", "", CameraMask.Alt, "spin_around_ccw"),
        new(KeyMode.ThirdPerson, "W", "", CameraMask.Alt, "move_forward"),
        new(KeyMode.ThirdPerson, "S", "", CameraMask.Alt, "move_backward"),
        new(KeyMode.ThirdPerson, "E", "", CameraMask.Alt, "spin_over"),
        new(KeyMode.ThirdPerson, "C", "", CameraMask.Alt, "spin_under"),
        new(KeyMode.ThirdPerson, "W", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.ThirdPerson, "S", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.ThirdPerson, "UP", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.ThirdPerson, "DOWN", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.ThirdPerson, "A", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left"),
        new(KeyMode.ThirdPerson, "D", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_right"),
        new(KeyMode.ThirdPerson, "W", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_up"),
        new(KeyMode.ThirdPerson, "S", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_down"),
        new(KeyMode.ThirdPerson, "LEFT", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left"),
        new(KeyMode.ThirdPerson, "RIGHT", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_right"),
        new(KeyMode.ThirdPerson, "UP", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_up"),
        new(KeyMode.ThirdPerson, "DOWN", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_down"),
        new(KeyMode.ThirdPerson, "", "MMB", CameraMask.None, "toggle_voice"),
        new(KeyMode.ThirdPerson, "", "LMB", CameraMask.None, "script_trigger_lbutton"),
        // sitting
        new(KeyMode.Sitting, "A", "", CameraMask.Alt, "spin_around_cw"),
        new(KeyMode.Sitting, "D", "", CameraMask.Alt, "spin_around_ccw"),
        new(KeyMode.Sitting, "W", "", CameraMask.Alt, "move_forward"),
        new(KeyMode.Sitting, "S", "", CameraMask.Alt, "move_backward"),
        new(KeyMode.Sitting, "E", "", CameraMask.Alt, "spin_over_sitting"),
        new(KeyMode.Sitting, "C", "", CameraMask.Alt, "spin_under_sitting"),
        new(KeyMode.Sitting, "LEFT", "", CameraMask.Alt, "spin_around_cw"),
        new(KeyMode.Sitting, "RIGHT", "", CameraMask.Alt, "spin_around_ccw"),
        new(KeyMode.Sitting, "UP", "", CameraMask.Alt, "move_forward"),
        new(KeyMode.Sitting, "DOWN", "", CameraMask.Alt, "move_backward"),
        new(KeyMode.Sitting, "PGUP", "", CameraMask.Alt, "spin_over"),
        new(KeyMode.Sitting, "PGDN", "", CameraMask.Alt, "spin_under"),
        new(KeyMode.Sitting, "W", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.Sitting, "S", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.Sitting, "E", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.Sitting, "C", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.Sitting, "UP", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.Sitting, "DOWN", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.Sitting, "PGUP", "", CameraMask.Control | CameraMask.Alt, "spin_over"),
        new(KeyMode.Sitting, "PGDN", "", CameraMask.Control | CameraMask.Alt, "spin_under"),
        new(KeyMode.Sitting, "A", "", CameraMask.None, "spin_around_cw_sitting"),
        new(KeyMode.Sitting, "D", "", CameraMask.None, "spin_around_ccw_sitting"),
        new(KeyMode.Sitting, "W", "", CameraMask.None, "move_forward_sitting"),
        new(KeyMode.Sitting, "S", "", CameraMask.None, "move_backward_sitting"),
        new(KeyMode.Sitting, "E", "", CameraMask.None, "spin_over_sitting"),
        new(KeyMode.Sitting, "C", "", CameraMask.None, "spin_under_sitting"),
        new(KeyMode.Sitting, "LEFT", "", CameraMask.None, "spin_around_cw_sitting"),
        new(KeyMode.Sitting, "RIGHT", "", CameraMask.None, "spin_around_ccw_sitting"),
        new(KeyMode.Sitting, "UP", "", CameraMask.None, "move_forward_sitting"),
        new(KeyMode.Sitting, "DOWN", "", CameraMask.None, "move_backward_sitting"),
        new(KeyMode.Sitting, "PGUP", "", CameraMask.None, "spin_over_sitting"),
        new(KeyMode.Sitting, "PGDN", "", CameraMask.None, "spin_under_sitting"),
        new(KeyMode.Sitting, "A", "", CameraMask.Shift, "slide_left"),
        new(KeyMode.Sitting, "D", "", CameraMask.Shift, "slide_right"),
        new(KeyMode.Sitting, "W", "", CameraMask.Shift, "move_forward_sitting"),
        new(KeyMode.Sitting, "S", "", CameraMask.Shift, "move_backward_sitting"),
        new(KeyMode.Sitting, "E", "", CameraMask.Shift, "spin_over_sitting"),
        new(KeyMode.Sitting, "C", "", CameraMask.Shift, "spin_under_sitting"),
        new(KeyMode.Sitting, "LEFT", "", CameraMask.Shift, "slide_left"),
        new(KeyMode.Sitting, "RIGHT", "", CameraMask.Shift, "slide_right"),
        new(KeyMode.Sitting, "UP", "", CameraMask.Shift, "move_forward_sitting"),
        new(KeyMode.Sitting, "DOWN", "", CameraMask.Shift, "move_backward_sitting"),
        new(KeyMode.Sitting, "PGUP", "", CameraMask.Shift, "spin_over_sitting"),
        new(KeyMode.Sitting, "PGDN", "", CameraMask.Shift, "spin_under_sitting"),
        new(KeyMode.Sitting, "A", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left"),
        new(KeyMode.Sitting, "D", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_right"),
        new(KeyMode.Sitting, "W", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_up"),
        new(KeyMode.Sitting, "S", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_down"),
        new(KeyMode.Sitting, "LEFT", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left"),
        new(KeyMode.Sitting, "RIGHT", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_right"),
        new(KeyMode.Sitting, "UP", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_up"),
        new(KeyMode.Sitting, "DOWN", "", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_down"),
        new(KeyMode.Sitting, "ENTER", "", CameraMask.None, "start_chat"),
        new(KeyMode.Sitting, "DIVIDE", "", CameraMask.None, "start_gesture"),
        new(KeyMode.Sitting, "", "MMB", CameraMask.None, "toggle_voice"),
        new(KeyMode.Sitting, "", "LMB", CameraMask.None, "script_trigger_lbutton"),
        // edit_avatar
        new(KeyMode.EditAvatar, "A", "", CameraMask.None, "edit_avatar_spin_cw"),
        new(KeyMode.EditAvatar, "D", "", CameraMask.None, "edit_avatar_spin_ccw"),
        new(KeyMode.EditAvatar, "W", "", CameraMask.None, "edit_avatar_move_forward"),
        new(KeyMode.EditAvatar, "S", "", CameraMask.None, "edit_avatar_move_backward"),
        new(KeyMode.EditAvatar, "E", "", CameraMask.None, "edit_avatar_spin_over"),
        new(KeyMode.EditAvatar, "C", "", CameraMask.None, "edit_avatar_spin_under"),
        new(KeyMode.EditAvatar, "LEFT", "", CameraMask.None, "edit_avatar_spin_cw"),
        new(KeyMode.EditAvatar, "RIGHT", "", CameraMask.None, "edit_avatar_spin_ccw"),
        new(KeyMode.EditAvatar, "UP", "", CameraMask.None, "edit_avatar_move_forward"),
        new(KeyMode.EditAvatar, "DOWN", "", CameraMask.None, "edit_avatar_move_backward"),
        new(KeyMode.EditAvatar, "PGUP", "", CameraMask.None, "edit_avatar_spin_over"),
        new(KeyMode.EditAvatar, "PGDN", "", CameraMask.None, "edit_avatar_spin_under"),
        new(KeyMode.EditAvatar, "ENTER", "", CameraMask.None, "start_chat"),
        new(KeyMode.EditAvatar, "DIVIDE", "", CameraMask.None, "start_gesture"),
        new(KeyMode.EditAvatar, "", "MMB", CameraMask.None, "toggle_voice"),
        new(KeyMode.EditAvatar, "", "LMB", CameraMask.None, "script_trigger_lbutton"),
    ];

    private static readonly Dictionary<string, KeyRoute> Routes = BuildRoutes();

    private static Dictionary<string, KeyRoute> BuildRoutes()
    {
        var routes = new Dictionary<string, KeyRoute>(StringComparer.Ordinal);
        foreach (var (name, move) in MoveCommands.Names)
            routes[name] = new KeyRoute(KeyRouteKind.Move, Move: move);
        void Camera(string name, CameraKey key, MoveCommand? fallback = null) =>
            routes[name] = new KeyRoute(KeyRouteKind.Camera, Camera: key, Fallback: fallback);
        // llviewerinput.cpp camera_* actions (REGISTER_KEYBOARD_ACTION, 920-960)
        Camera("spin_around_cw", CameraKey.SpinAroundCw);
        Camera("spin_around_ccw", CameraKey.SpinAroundCcw);
        Camera("spin_over", CameraKey.SpinOver);
        Camera("spin_under", CameraKey.SpinUnder);
        Camera("move_forward", CameraKey.MoveIn);
        Camera("move_backward", CameraKey.MoveOut);
        Camera("pan_left", CameraKey.PanLeft);
        Camera("pan_right", CameraKey.PanRight);
        Camera("pan_up", CameraKey.PanUp);
        Camera("pan_down", CameraKey.PanDown);
        // the sitting variants: the camera, unless a script has taken the control or the seat has its own camera (then the avatar's key)
        Camera("spin_around_cw_sitting", CameraKey.SpinAroundCw, MoveCommand.TurnLeft);
        Camera("spin_around_ccw_sitting", CameraKey.SpinAroundCcw, MoveCommand.TurnRight);
        Camera("spin_over_sitting", CameraKey.SpinOver, MoveCommand.Jump);
        Camera("spin_under_sitting", CameraKey.SpinUnder, MoveCommand.Down);
        Camera("move_forward_sitting", CameraKey.MoveIn, MoveCommand.Forward);
        Camera("move_backward_sitting", CameraKey.MoveOut, MoveCommand.Backward);
        // the avatar-editing camera (here the same camera keys)
        Camera("edit_avatar_spin_cw", CameraKey.SpinAroundCw);
        Camera("edit_avatar_spin_ccw", CameraKey.SpinAroundCcw);
        Camera("edit_avatar_spin_over", CameraKey.SpinOver);
        Camera("edit_avatar_spin_under", CameraKey.SpinUnder);
        Camera("edit_avatar_move_forward", CameraKey.MoveIn);
        Camera("edit_avatar_move_backward", CameraKey.MoveOut);
        routes["start_chat"] = new KeyRoute(KeyRouteKind.StartChat);
        routes["start_gesture"] = new KeyRoute(KeyRouteKind.StartGesture);
        routes["toggle_voice"] = new KeyRoute(KeyRouteKind.ToggleVoice);
        routes["script_trigger_lbutton"] = new KeyRoute(KeyRouteKind.ScriptLeftButton);
        return routes;
    }

    public static IEnumerable<KeyBindingEntry> For(KeyMode mode) => All.Where(e => e.Mode == mode);

    /// <summary>The command bound to a key (the file's names: letters, LEFT, RIGHT, UP, DOWN, PGUP, PGDN, HOME, SPACE, ENTER, DIVIDE) with exactly
    /// the modifiers <paramref name="mask"/> in <paramref name="mode"/>. A mask must match exactly (llviewerinput.cpp bindKey).</summary>
    public static bool TryFind(KeyMode mode, string key, CameraMask mask, out string command)
    {
        foreach (var e in All)
            if (e.Mode == mode && e.Mouse.Length == 0 && e.Mask == mask && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                command = e.Command;
                return true;
            }
        command = "";
        return false;
    }

    /// <summary>Whether a mouse button (LMB, MMB) with exactly these modifiers has a binding in a mode (llviewerinput.cpp isMouseBindUsed). Left-drag
    /// steering is on only while the left click is bound in third person.</summary>
    public static bool IsMouseBindUsed(KeyMode mode, string mouse, CameraMask mask) =>
        All.Any(e => e.Mode == mode && e.Mouse.Length > 0 && e.Mask == mask && string.Equals(e.Mouse, mouse, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a command goes.</summary>
    public static KeyRoute Route(string command) => Routes.TryGetValue(command, out var r) ? r : new KeyRoute(KeyRouteKind.None);

    /// <summary>Whether a sitting key sends the avatar's key rather than moving the camera (llviewerinput.cpp camera_*_sitting): a script has
    /// taken the matching control (llagent.cpp rotateGrabbed, upGrabbed, downGrabbed, forwardGrabbed, backwardGrabbed), or the seat has its
    /// own camera, or, for the turn keys, the avatar is running and, for the push keys, is running without Always Run.</summary>
    public static bool SendsAvatarKey(KeyRoute route, ScriptControls? script, bool seatCamera, bool running, bool alwaysRun)
    {
        if (route.Fallback is not { } key) return false;
        if (seatCamera) return true;
        switch (key)
        {
            case MoveCommand.TurnLeft or MoveCommand.TurnRight: return script?.RotateGrabbed == true || running;
            case MoveCommand.Forward: return script?.Grabbed(ScriptControls.AtPosIndex) == true || running && !alwaysRun;
            case MoveCommand.Backward: return script?.Grabbed(ScriptControls.AtNegIndex) == true || running && !alwaysRun;
            case MoveCommand.Jump: return script?.UpGrabbed == true;
            case MoveCommand.Down: return script?.Grabbed(ScriptControls.UpNegIndex) == true;
        }
        return false;
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Collections.Generic;

namespace Viewer.Motion;

/// <summary>What the avatar was doing when an arrow key went down.</summary>
public enum ArrowPose { Standing, Sitting, Flying }

/// <summary>One arrow-key press: the key, the control that held the keyboard, whether a text field was taking keys, whether the avatar's
/// controls received it, and the avatar's state.</summary>
public readonly record struct ArrowKeyReport(string Key, string FocusOwner, bool TextEntry, bool WorldReceived, ArrowPose Pose, bool Mouselook);

/// <summary>
/// Records each arrow-key press once (a held key repeats as echoes, which are not new presses) so the log shows what took the arrows.
/// The host opens a record when the key goes down, marks it when the avatar's controls take the key, and drains the records to the log.
/// </summary>
public sealed class ArrowKeyTrace
{
    private readonly Queue<ArrowKeyReport> _reports = new();
    private ArrowKeyReport? _open;

    /// <summary>A seated avatar is sitting (it cannot fly); otherwise flying or standing.</summary>
    public static ArrowPose PoseOf(bool sitting, bool flying) => sitting ? ArrowPose.Sitting : flying ? ArrowPose.Flying : ArrowPose.Standing;

    /// <summary>An arrow key event. Returns true when it opened a record (a key going down); repeats of a held key are echoes and open none.</summary>
    public bool Press(string key, bool echo, string focusOwner, bool textEntry, ArrowPose pose, bool mouselook)
    {
        if (echo) return false;
        Close();
        _open = new ArrowKeyReport(key, focusOwner, textEntry, false, pose, mouselook);
        return true;
    }

    /// <summary>The avatar's controls took the key of the latest press.</summary>
    public void WorldReceived()
    {
        if (_open is { } o) _open = o with { WorldReceived = true };
    }

    /// <summary>The oldest record not yet taken, or null.</summary>
    public ArrowKeyReport? Take()
    {
        Close();
        return _reports.Count > 0 ? _reports.Dequeue() : null;
    }

    private void Close()
    {
        if (_open is { } o) { _reports.Enqueue(o); _open = null; }
    }
}

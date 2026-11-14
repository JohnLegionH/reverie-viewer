// SPDX-License-Identifier: LGPL-2.1-only
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
namespace Viewer.Motion;

/// <summary>
/// Which controls scripts in the region have taken from the user's avatar (the region's ScriptControlChange message), as the
/// official viewer keeps them (llagent.cpp, processScriptControlChange: a count per control bit, one for takes that pass the
/// control on to the avatar as well and one for takes that do not). Thread-safe: the message arrives on the network thread
/// and the keyboard reads the counts on the main thread.
/// </summary>
public sealed class ScriptControls
{
    /// <summary>The control bit indexes of the protocol (the official viewer's indra_constants.h, CONTROL_*_INDEX).</summary>
    public const int AtPosIndex = 0, AtNegIndex = 1, UpNegIndex = 5, UpPosIndex = 4, YawPosIndex = 8, YawNegIndex = 9, LButtonDownIndex = 28, MlLButtonDownIndex = 30, TotalControls = 32;

    private readonly object _lock = new();
    private readonly int[] _taken = new int[TotalControls], _passed = new int[TotalControls];

    /// <summary>A script took <paramref name="controls"/>; <paramref name="passOn"/>: the avatar still gets them too.</summary>
    public void Take(uint controls, bool passOn) => Count(controls, passOn, +1);

    /// <summary>A script gave back <paramref name="controls"/> it took with the same <paramref name="passOn"/>.</summary>
    public void Release(uint controls, bool passOn) => Count(controls, passOn, -1);

    private void Count(uint controls, bool passOn, int by)
    {
        lock (_lock)
        {
            var counts = passOn ? _passed : _taken;
            for (int i = 0; i < TotalControls; i++)
                if ((controls & (1u << i)) != 0)
                    counts[i] = Math.Max(0, counts[i] + by);
        }
    }

    private bool Any(int index) { lock (_lock) return _taken[index] > 0 || _passed[index] > 0; }

    /// <summary>Whether a script wants the left mouse button in the current view: the mouse-look button bit in mouse-look, the
    /// ordinary one otherwise (llagent.cpp, leftButtonGrabbed).</summary>
    public bool LeftButtonGrabbed(bool mouseLook) => Any(mouseLook ? MlLButtonDownIndex : LButtonDownIndex);

    /// <summary>Whether a script took the turn controls without passing them on (llagent.cpp, rotateGrabbed).</summary>
    public bool RotateGrabbed { get { lock (_lock) return _taken[YawPosIndex] > 0 || _taken[YawNegIndex] > 0; } }

    /// <summary>Whether a script took the up control without passing it on (llagent.cpp, upGrabbed).</summary>
    public bool UpGrabbed { get { lock (_lock) return _taken[UpPosIndex] > 0; } }

    /// <summary>Whether a script took the control with this bit index without passing it on (llagent.cpp forwardGrabbed, backwardGrabbed, downGrabbed).</summary>
    public bool Grabbed(int index) { lock (_lock) return _taken[index] > 0; }
}

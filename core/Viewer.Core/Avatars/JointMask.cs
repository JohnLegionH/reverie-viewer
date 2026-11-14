// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llmotioncontroller.cpp
 * @brief Implementation of LLMotionController class.
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

// Adapted 2026 by Legion Builds. The joint signatures a motion controller keeps while it runs the motions of one blend type (LLMotionController::
// updateMotionsByType, the rotation signature handed to each motion's update) and the test that skips a keyframe motion's constraint
// (LLKeyframeMotion::applyConstraint: joint_mask[joint] >= (0xff >> (7 - priority))) follow the files named above and llmotion.cpp
// (addJointState: the signature byte of a joint is 0xff >> (7 - priority)), rewritten in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.Avatars;

/// <summary>One motion that takes part in a blend, for a joint mask: when it is at full weight, and the priority it turns each of its joints at.</summary>
public readonly record struct MaskMotion(MotionTiming Timing, IReadOnlyDictionary<string, int> RotationPriority);

/// <summary>
/// Which joints another motion already drives, as the official viewer's controller works it out for a keyframe motion's constraints. The controller runs the
/// motions newest first; a motion at full weight leaves a signature per joint (the priority's bits, 0xff >> (7 - priority), when it turns the joint), and the
/// signatures of the motions run before it are what the next motion is handed. A constraint is skipped when one of its chain's joints already carries a
/// signature at least as large as the constraint's own motion's priority bits: a motion run before it turns that joint at an equal or higher priority.
/// A motion under full weight (easing in or out) leaves no signature of its own, and the signature a motion is handed stays what the last full-weight
/// motion was handed (the official loop only reads the accumulated value inside the full-weight branch).
/// </summary>
public sealed class JointMask
{
    private readonly MaskMotion[] _newestFirst;
    private readonly int _own;
    private readonly int _priority;

    /// <param name="newestFirst">Every motion in the blend that is not additive, the newest first.</param>
    /// <param name="own">The index of the motion that carries the constraint.</param>
    /// <param name="priority">That motion's priority.</param>
    public JointMask(MaskMotion[] newestFirst, int own, int priority)
    {
        _newestFirst = newestFirst;
        _own = own;
        _priority = priority;
    }

    private static int Bits(int priority) => 0xff >> (7 - Math.Clamp(priority, 0, 7));

    /// <summary>True when the constraint is skipped at time <paramref name="t"/> (the blend's clock): one of <paramref name="joints"/> is already driven.</summary>
    public bool Covers(IReadOnlyList<string> joints, float t)
    {
        var accumulated = new int[joints.Count];
        var handed = new int[joints.Count];
        for (int i = 0; i <= _own && i < _newestFirst.Length; i++)
        {
            var m = _newestFirst[i];
            if (m.Timing.Weight(t) < 1.0) continue;
            for (int j = 0; j < joints.Count; j++)
            {
                handed[j] = accumulated[j];
                if (m.RotationPriority.TryGetValue(joints[j], out int p)) accumulated[j] |= Bits(p);
            }
        }
        int own = Bits(_priority);
        foreach (int mask in handed)
            if (mask >= own) return true;
        return false;
    }
}

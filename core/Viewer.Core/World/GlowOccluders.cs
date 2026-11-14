// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Residency;

namespace Viewer.Core.World;

/// <summary>
/// Which objects can hide a glowing object from the eye. The glow picture is a second drawing of the scene in which
/// whatever is in front of a glowing face hides it; drawing every object there costs a draw call each, so only the objects
/// that can be in front of some glowing object are drawn (Docs/GLOW-SPEC.md, "Where ours differs"). The test works on
/// bounding spheres and never leaves out an object that could hide a glowing one, so the picture is the same as with every
/// object drawn.
/// </summary>
public static class GlowOccluders
{
    /// <summary>A little extra angle, in radians, so rounding never drops an object that only just touches a sight line.</summary>
    internal const float AngleMargin = 1e-4f;

    /// <summary>
    /// Whether <paramref name="candidate"/> can lie between <paramref name="eye"/> and some point of
    /// <paramref name="target"/>: the eye is inside the candidate, or the two spheres' directions from the eye overlap and
    /// the candidate's nearest point is nearer than the target's farthest.
    /// </summary>
    public static bool CanHide(Vector3 eye, Bounds target, Bounds candidate)
    {
        var toC = candidate.Centre - eye;
        var toG = target.Centre - eye;
        float dc = toC.Length(), dg = toG.Length();
        if (dc <= candidate.Radius) return true;                         // the eye is inside it
        if (dc - candidate.Radius >= dg + target.Radius) return false;   // all of it is farther than all of the target
        if (dg <= target.Radius) return true;                            // the eye is inside the target: it fills every direction
        float cos = Math.Clamp(Vector3.Dot(toC, toG) / (dc * dg), -1f, 1f);
        float reach = MathF.Asin(Math.Min(1f, candidate.Radius / dc)) + MathF.Asin(Math.Min(1f, target.Radius / dg));
        return MathF.Acos(cos) <= reach + AngleMargin;
    }

    /// <summary>Sets <c>selected[i]</c> to whether candidate <c>i</c> can hide any of the targets. Returns the number of target-and-candidate pairs it tested.</summary>
    public static int Select(Vector3 eye, IReadOnlyList<Bounds> targets, IReadOnlyList<Bounds> candidates, bool[] selected) =>
        new GlowChooser().Select(eye, targets, candidates, selected);
}

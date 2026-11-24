// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>
/// Which objects a dragged selection box takes. The official viewer tests an object's vertices against the box: with "Select By Surrounding"
/// on (its setting RectangleSelectInclusive, on by default) every vertex must be inside the box, with it off one vertex inside is enough
/// (llglsandbox.cpp handleRectangleSelection and LLViewerCamera::areVertsVisible, read as behaviour; nothing copied). This viewer tests the
/// eight corners of the object's bounding box in place of its mesh vertices, so a box drawn round an object takes it, and a box that
/// only touches it takes it only with the setting off. Objects farther than the official MaxSelectDistance from the agent are passed over
/// (the official LimitSelectDistance, on by default).
/// </summary>
public static class BoxSelectRules
{
    /// <summary>The official MaxSelectDistance, in metres.</summary>
    public const float MaxSelectDistance = 64f;

    /// <summary>The eight corners of an object's bounding box in region coordinates: its position, turned by its rotation, half its size away.</summary>
    public static Vector3[] Corners(Vector3 position, Quaternion rotation, Vector3 size)
    {
        var corners = new Vector3[8];
        var half = size * 0.5f;
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
            corners[i] = position + Vector3.Transform(local, rotation);
        }
        return corners;
    }

    /// <summary>Whether a box takes an object, from where its corners fall on screen (null for a corner behind the camera, which is outside the
    /// view as the official frustum test has it). <paramref name="surround"/> asks every corner inside the box; otherwise one is enough.</summary>
    public static bool Takes(IReadOnlyList<Vector2?> corners, Vector2 boxA, Vector2 boxB, bool surround)
    {
        float left = Math.Min(boxA.X, boxB.X), right = Math.Max(boxA.X, boxB.X), top = Math.Min(boxA.Y, boxB.Y), bottom = Math.Max(boxA.Y, boxB.Y);
        bool In(Vector2? c) => c is { } p && p.X >= left && p.X <= right && p.Y >= top && p.Y <= bottom;
        return corners.Count > 0 && (surround ? corners.All(In) : corners.Any(In));
    }

    /// <summary>Whether an object is near enough for a box to take it (the official limit on the distance of a drag selection).</summary>
    public static bool InReach(Vector3 agent, Vector3 objectPosition) => Vector3.Distance(agent, objectPosition) <= MaxSelectDistance;
}

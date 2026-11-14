// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Interaction;

/// <summary>The camera a seat asks for: offsets relative to the seat's object, turned into region positions.</summary>
public static class SitCamera
{
    /// <summary>Offsets closer together than this (squared, metres) mean the seat sets no camera.</summary>
    public const float MinSeparationSquared = 0.001f * 0.001f;

    /// <summary>The eye and the point looked at in region coordinates, or null when the two offsets are too close for the seat to mean a camera.</summary>
    public static (Vector3 Eye, Vector3 Focus)? Compute(Vector3 objectPosition, Quaternion objectRotation, Vector3 eyeOffset, Vector3 atOffset)
    {
        if (Vector3.DistanceSquared(eyeOffset, atOffset) <= MinSeparationSquared)
            return null;
        return (objectPosition + Vector3.Transform(eyeOffset, objectRotation), objectPosition + Vector3.Transform(atOffset, objectRotation));
    }
}

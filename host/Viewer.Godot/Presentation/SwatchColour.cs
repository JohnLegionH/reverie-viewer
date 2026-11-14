// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>Makes an engine colour from a sky setting's colour (a colour the user picks in the Personal Lighting window); each channel is kept between 0 and 1.</summary>
internal static class SwatchColour
{
    public static Color Of(System.Numerics.Vector3 c) =>
        new(System.Math.Clamp(c.X, 0f, 1f), System.Math.Clamp(c.Y, 0f, 1f), System.Math.Clamp(c.Z, 0f, 1f));
}

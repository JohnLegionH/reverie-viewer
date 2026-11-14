// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// What the draw distance does to the picture. As in the official viewer, the distance is sent to the region as the agent's far distance and is
/// the 3D camera's far plane, so what lies beyond it (terrain, water, objects) is not drawn.
/// </summary>
public static class DrawDistanceView
{
    /// <summary>The far plane for a draw distance in metres: the distance itself, at least the smallest the viewer allows. The second argument
    /// is the engine camera's own default, which no longer matters (the far plane is the draw distance at every distance).</summary>
    public static float FarPlane(int metres, float engineDefault) => Math.Max(metres, Viewer.Core.World.Interest.MinDrawDistance);
}

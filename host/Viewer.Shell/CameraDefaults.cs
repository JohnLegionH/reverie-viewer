// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What the main view's camera starts with.</summary>
public static class CameraDefaults
{
    /// <summary>The vertical field of view, in degrees. The official viewer's default is its CameraAngle setting, 1.047198 rad (60 degrees),
    /// the vertical angle (LLViewerCamera::setDefaultFOV takes a vertical field); the engine's own default is wider (75 degrees), which
    /// puts a near camera closer for the same framing and exaggerates the depth of a face.</summary>
    public const float VerticalFovDegrees = 60f;
}

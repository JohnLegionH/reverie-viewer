// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The rendering backend in use and whether the one wanted could not run, set once at start-up for the notice and the Graphics page.</summary>
public static class GraphicsBackendInfo
{
    public static BackendStatus? Status { get; set; }
}

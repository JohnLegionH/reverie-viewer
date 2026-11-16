// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Build;

/// <summary>The Build window's settings that are kept between sessions.</summary>
public static class BuildSettings
{
    /// <summary>"Keep Tool selected" in the Create tool's options (the official viewer's CreateToolKeepSelected, off by default).</summary>
    public static readonly Setting<bool> KeepToolSelected = new("create_tool_keep_selected", false, v => v);
}

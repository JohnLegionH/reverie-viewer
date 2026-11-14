// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The text that stands for the grid: in the window title and in the menu bar.</summary>
public static class BrandText
{
    /// <summary>The grid's own name from its grid info, else the viewer's plain name.</summary>
    public static string For(string? gridName, StringTable strings) =>
        string.IsNullOrWhiteSpace(gridName) ? strings.Get("brand.default") : gridName.Trim();

    /// <summary>The window title: the viewer's own name and nothing else, as the official viewer's (llappviewer.cpp sets it to
    /// APP_NAME); the grid's name stays in the menu bar. The name comes from the string table's one entry for it, so renaming
    /// the viewer is one change.</summary>
    public static string WindowTitle(StringTable strings) => strings.Get("brand.default");

    public static string WindowTitle() => "";
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The text that stands for the grid: in the window title and in the menu bar.</summary>
public static class BrandText
{
    /// <summary>The grid's own name from its grid info, else the viewer's plain name.</summary>
    public static string For(string? gridName, StringTable strings) =>
        string.IsNullOrWhiteSpace(gridName) ? strings.Get("brand.default") : gridName.Trim();

    /// <summary>The window title: the same text as the menu bar's.</summary>
    public static string WindowTitle(string? gridName, StringTable strings) => For(gridName, strings);
}

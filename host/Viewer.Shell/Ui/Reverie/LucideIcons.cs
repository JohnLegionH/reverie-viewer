// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The Reverie interface's icons, by their Lucide name (Lucide, ISC licence; the files are in <c>Assets/Icons/Lucide</c>).</summary>
public static class LucideIcons
{
    /// <summary>Every icon the interface may ask for.</summary>
    public static IReadOnlyList<string> All { get; } = [];

    /// <summary>The icon as svg text with its stroke drawn in <paramref name="colour"/>, or null for an unknown name.</summary>
    public static string? Svg(string name, UiColour colour) => null;
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>A rectangle in pixels: left, top, width and height.</summary>
public readonly record struct PlacementRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where a dropdown menu opens against the button that opened it: below the button when it fits there, else above it, so the menu
/// never covers its own button. When it fits on neither side it opens on the side with more room. Its left edge is the button's,
/// moved left as far as needed to stay inside the area. Pure.
/// </summary>
public static class DropdownPlacement
{
    /// <summary>The menu's top-left corner for a menu of the given size opened from <paramref name="anchor"/> inside <paramref name="area"/>.</summary>
    public static (int X, int Y) Place(PlacementRect anchor, int menuWidth, int menuHeight, PlacementRect area)
    {
        int roomBelow = area.Bottom - anchor.Bottom;
        int roomAbove = anchor.Y - area.Y;
        int y = menuHeight <= roomBelow ? anchor.Bottom
            : menuHeight <= roomAbove ? anchor.Y - menuHeight
            : roomBelow >= roomAbove ? anchor.Bottom : anchor.Y - menuHeight;
        int x = Math.Max(area.X, Math.Min(anchor.X, area.Right - menuWidth));
        return (x, y);
    }
}

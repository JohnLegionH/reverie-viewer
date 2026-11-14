// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The window frame's layout as a pure function of the window size (behaviour: a menu bar along the top, then the
/// navigation bar and the favourites strip when they are shown, a chat bar and a toolbar along the bottom that take no room
/// while they are not shown, the main view between them). The size is in logical pixels: the window's pixels divided by the
/// interface scale, so a small window at a large scale is laid out as the small window it is.</summary>
public static class ShellLayout
{
    public static ShellRegions Compute(UiSize window, ShellMetrics metrics, bool toolbarHasButtons) =>
        Compute(window, metrics, new ChromeRows(toolbarHasButtons));

    /// <summary>Menu bar, navigation bar, favourites strip, the main view, chat bar and toolbar, top to bottom; rows that are not shown take no room.</summary>
    public static ShellRegions Compute(UiSize window, ShellMetrics metrics, ChromeRows rows)
    {
        float toolbarHeight = rows.Toolbar ? metrics.ToolbarHeight : 0;
        float chatHeight = rows.ChatBar ? metrics.ChatBarHeight : 0;
        float navHeight = rows.NavBar ? metrics.NavBarHeight : 0;
        float favouritesHeight = rows.Favourites ? metrics.FavouritesHeight : 0;
        // the window is laid out at the size it has; only a window too small for the fixed rows themselves is made as high as they are
        // (the window clips the rest), so no region goes negative
        float w = Math.Max(window.W, 1);
        float h = Math.Max(window.H, metrics.MenuBarHeight + navHeight + favouritesHeight + chatHeight + toolbarHeight);
        var menuBar = new UiRect(0, 0, w, metrics.MenuBarHeight);
        var nav = new UiRect(0, menuBar.Bottom, w, navHeight);
        var favourites = new UiRect(0, nav.Bottom, w, favouritesHeight);
        var toolbar = new UiRect(0, h - toolbarHeight, w, toolbarHeight);
        var chat = new UiRect(0, toolbar.Y - chatHeight, w, chatHeight);
        var main = new UiRect(0, favourites.Bottom, w, chat.Y - favourites.Bottom);
        return new ShellRegions(new UiRect(0, 0, w, h), menuBar, toolbar, main, nav, favourites, chat);
    }
}

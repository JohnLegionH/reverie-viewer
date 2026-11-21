// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The window items of the Me menu: Full screen (switches between full screen and a window; checked while in full screen) and Minimize.
/// The official viewer and Firestorm have neither (FullScreen is a debug setting in both, with no menu item or key), so the shortcut is this
/// viewer's own: Ctrl+Alt+F, which no menu shortcut or key binding of either viewer uses.
/// </summary>
public static class WindowMenu
{
    public const string FullscreenCommand = "app.toggle_fullscreen", MinimizeCommand = "app.minimize";
    public const string FullscreenEntry = "me.fullscreen", MinimizeEntry = "me.minimize";
    public const string FullscreenShortcut = "control|alt|F";

    /// <summary>Adds both entries to the Me menu, above Quit.</summary>
    public static void Install(MenuModel menus, Func<bool> inFullscreen)
    {
        menus.AddEntry("me", new MenuEntry(FullscreenEntry, "menu.me.fullscreen", FullscreenCommand, Order: 900, Checked: inFullscreen, Shortcut: FullscreenShortcut));
        menus.AddEntry("me", new MenuEntry(MinimizeEntry, "menu.me.minimize", MinimizeCommand, Order: 910));
    }
}

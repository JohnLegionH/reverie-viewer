// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The official viewer's keyboard shortcuts (menu_viewer.xml, behaviour reference only) for the menu entries this viewer has. A menu entry
/// added without a shortcut of its own takes its official one from here, so the menu shows it beside the entry and the menu bar runs the
/// entry when it is pressed. Shortcuts whose feature is not built are listed in <see cref="NotBuilt"/>, so the gap is written down.
/// The sky shortcuts (control+shift+O, Y, T, N, Z), Upload image (control+U), Build (control+B), the Focus, Move, Edit and Create tools
/// (control+1 to control+4), Link (control+L) and Unlink (control+shift+L) are wired where those features are.
/// </summary>
public static class ViewerShortcuts
{
    /// <summary>Menu entry id to the official shortcut, written as the official menus write it ("control|I").</summary>
    public static IReadOnlyDictionary<string, string> ByEntry { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["content.inventory"] = "control|I",                     // Inventory
        ["content.outfits"] = "control|O",                       // My Outfits
        ["me.preferences"] = "control|P",                        // Preferences
        ["me.quit"] = "control|Q",                               // Quit
        ["communicate.conversations"] = "control|T",             // Conversations
        ["communicate.chat"] = "control|H",                      // Nearby chat
        ["communicate.people"] = "control|shift|A",              // Nearby people (the People window)
        ["communicate.notifications"] = "alt|shift|N",           // Notifications
        ["world.map"] = "control|M",                             // World map
        ["world.minimap"] = "control|shift|M",                   // Mini-map
        ["world.home"] = "control|shift|H",                      // Teleport home
        ["build.upload.model"] = "control|alt|U",                // Upload model
    };

    /// <summary>The command list (one search over every window, menu command and setting). Neither the official viewer nor Firestorm uses
    /// Ctrl+Shift+K (no menu shortcut and no key binding in either), so Ctrl+K stays Camera controls as in the official viewer.</summary>
    public const string CommandList = "";

    /// <summary>Official shortcuts whose feature this viewer does not have yet: the shortcut and what it does in the official viewer.</summary>
    public static IReadOnlyList<(string Shortcut, string Feature)> NotBuilt { get; } =
    [
        ("control|F", "Search"),
        ("control|K", "Camera controls"),
        ("control|shift|S", "Snapshot"),
        ("control|grave", "Snapshot to disk"),
        ("control|5", "Land tool"),
        ("control|shift|I", "New inventory window"),
        ("control|alt|shift|N", "Beacons"), ("control|alt|shift|P", "Property lines"),
        ("control|period", "Select next part"), ("control|comma", "Select previous part"),
        ("H", "Focus on selection"), ("shift|H", "Zoom to selection"),
        ("control|alt|R", "Rebake textures"), ("control|shift|2", "Scene load statistics"),
        ("control|D", "Duplicate"),
        ("control|E", "Events"),
        ("control|G", "Gestures"),
        ("control|R", "Always run"),
        ("control|shift|F", "Friends (the People window has no Friends tab to open on)"),
        ("control|shift|G", "Groups (the People window has no Groups tab to open on)"),
        ("control|shift|U", "Hide the interface"),
        ("alt|shift|H", "Show HUD attachments"),
        ("alt|shift|R", "Detach all attachments"),
        ("alt|shift|S", "Sit or stand"),
        ("alt|shift|A", "Stop animating me"),
        ("control|alt|H", "Hover height"),
        ("control|W", "Close window"), ("control|shift|W", "Close all windows"),
        ("F1", "Help"),
        ("control|8", "Zoom in"), ("control|9", "Default zoom"), ("control|0", "Zoom out"),
        ("control|shift|1", "Statistics"),
        ("control|alt|Q", "Develop menu"),
    ];

    /// <summary>The entry with its official shortcut when it has none of its own.</summary>
    public static MenuEntry WithOfficial(MenuEntry entry) =>
        entry.Shortcut is null && ByEntry.TryGetValue(entry.Id, out var official) ? entry with { Shortcut = official } : entry;
}

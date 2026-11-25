// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What holds the keyboard when an arrow key goes down.</summary>
public enum ArrowFocusKind
{
    /// <summary>Nothing: the key reaches the world through the unhandled-input path.</summary>
    None,
    /// <summary>A button (toolbar, window close, Stand): it has no use for arrows, they would only move the focus to a neighbour.</summary>
    Button,
    /// <summary>Any other control that does not use arrows itself (a panel, a tab holder, a scroll area): same as a button.</summary>
    Other,
    /// <summary>A text field: it moves its caret with the arrows.</summary>
    TextField,
    /// <summary>A list, tree, slider or similar control that steps through its items with the arrows.</summary>
    List,
}

/// <summary>
/// Whether the avatar's controls take an arrow key before the engine's focus navigation sees it. The engine moves the focus to a
/// neighbouring control on an arrow key and swallows the key, so once a toolbar button, a window or the chat bar had held the keyboard the
/// arrows stopped reaching the avatar and the camera. Controls that use arrows themselves keep them, unless Preferences says the arrows
/// always move the avatar while typing.
/// </summary>
public static class ArrowKeyRouting
{
    /// <param name="recallKey">The key recalls an earlier line in the field that holds the keyboard (Ctrl+Up or Ctrl+Down in the chat bar),
    /// so the field keeps it.</param>
    public static bool TakenByAvatar(ArrowFocusKind owner, bool alt, bool textEntryActive, bool arrowKeysAlwaysMove, bool movementAllowedForArrow, bool recallKey = false)
    {
        if (alt || recallKey || !movementAllowedForArrow) return false;
        return owner switch
        {
            ArrowFocusKind.Button or ArrowFocusKind.Other => !textEntryActive,
            ArrowFocusKind.TextField => textEntryActive && arrowKeysAlwaysMove,
            _ => false,
        };
    }
}

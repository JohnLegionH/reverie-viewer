// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The arrow keys reach the avatar and the camera after real use of the screen. The engine's focus navigation takes an arrow key
/// whenever a control holds the keyboard, so after a toolbar button was clicked, a window opened and closed, or the chat bar clicked,
/// the avatar must still get the key. Controls that use arrows themselves (text fields, lists) keep them.
/// </summary>
public class ArrowKeyRoutingTests
{
    [Theory]
    [InlineData(ArrowFocusKind.Button)]   // a toolbar button was clicked
    [InlineData(ArrowFocusKind.Other)]    // a window was opened and closed and its panel kept the keyboard
    public void A_control_with_no_use_for_arrows_hands_them_to_the_avatar(ArrowFocusKind owner) =>
        Assert.True(ArrowKeyRouting.TakenByAvatar(owner, alt: false, textEntryActive: false, arrowKeysAlwaysMove: false, movementAllowedForArrow: true));

    [Fact]
    public void The_chat_bar_keeps_the_arrows_unless_they_always_move_the_avatar()
    {
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.TextField, false, textEntryActive: true, arrowKeysAlwaysMove: false, movementAllowedForArrow: false));
        Assert.True(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.TextField, false, textEntryActive: true, arrowKeysAlwaysMove: true, movementAllowedForArrow: true));
    }

    [Fact]
    public void Lists_and_the_unfocused_world_are_left_to_their_own_path()
    {
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.List, false, false, true, true));
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.None, false, false, true, true));
    }

    [Fact]
    public void Alt_arrows_are_camera_keys_and_a_window_without_focus_moves_nothing()
    {
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.Button, alt: true, false, false, true));
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.Button, false, false, false, movementAllowedForArrow: false));
    }
}

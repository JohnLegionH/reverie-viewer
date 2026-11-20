// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Inventory;
using Viewer.Motion;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

public sealed class ChatKeyTests
{
    [Theory]
    [InlineData(KeyMode.ThirdPerson)]
    [InlineData(KeyMode.FirstPerson)]
    [InlineData(KeyMode.Sitting)]
    public void Enter_starts_chat_and_the_number_pad_divide_key_starts_a_gesture_line(KeyMode mode)
    {
        Assert.Equal(KeyRouteKind.StartChat, KeyBindingTable.ChatRoute(mode, "ENTER", CameraMask.None));
        Assert.Equal(KeyRouteKind.StartGesture, KeyBindingTable.ChatRoute(mode, "DIVIDE", CameraMask.None));
    }

    [Fact]
    public void Other_keys_and_masks_start_nothing()
    {
        Assert.Equal(KeyRouteKind.None, KeyBindingTable.ChatRoute(KeyMode.ThirdPerson, "ENTER", CameraMask.Shift));
        Assert.Equal(KeyRouteKind.None, KeyBindingTable.ChatRoute(KeyMode.ThirdPerson, "A", CameraMask.None));
    }

    [Fact]
    public void The_chat_bar_starts_with_nothing_or_a_slash_and_keeps_typed_text()
    {
        Assert.Equal("/", ChatStart.GestureLine(""));
        Assert.Null(ChatStart.GestureLine("hello"));
    }

    [Fact]
    public void Function_key_numbers_map_to_gesture_keys_and_F9_is_one_of_them()
    {
        Assert.Equal(GestureKeys.F9, GestureKeys.FromFunctionKey(9));
        Assert.Equal(GestureKeys.F2, GestureKeys.FromFunctionKey(2));
        Assert.Equal(GestureKeys.F12, GestureKeys.FromFunctionKey(12));
        Assert.Equal(GestureKeys.None, GestureKeys.FromFunctionKey(1));
        Assert.Equal(GestureKeys.None, GestureKeys.FromFunctionKey(13));
        Assert.Equal(GestureKeys.Offered, Enumerable.Range(2, 11).Select(GestureKeys.FromFunctionKey).ToArray());
    }

    [Fact]
    public void An_active_gesture_on_F9_plays_and_takes_the_key_otherwise_the_key_is_left_for_the_sky()
    {
        var played = new List<GestureDocument>();
        var set = new ActiveGestures(played.Add, n => 0);
        Assert.False(set.Key(GestureKeys.F9, GestureKeys.MaskNone));   // none active: the caller cycles the environment
        set.Set(Guid.NewGuid(), new GestureDocument { Key = GestureKeys.F9, Mask = GestureKeys.MaskNone });
        Assert.True(set.Key(GestureKeys.F9, GestureKeys.MaskNone));    // an active one wins
        Assert.Single(played);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Interaction;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which action a click on an object runs, whether the object counts as touchable and which cursor it earns.
/// Pure functions; every member is independent, so the class runs in parallel.
/// </summary>
public class ClickActionTests
{
    private static PrimInteraction With(byte action = 0, uint flags = 0) => new(action, flags);

    [Fact]
    public void An_object_without_an_action_or_flags_is_touched_when_clicked()
    {
        Assert.Equal(ClickAction.Touch, ClickAction.Final(With(), With(), attachment: false));
        Assert.Equal(LeftClick.Touch, ClickAction.Left(With(), With(), false, false));
    }

    [Fact]
    public void An_attachment_has_no_click_action()
    {
        // Nothing to run: the official viewer answers "none" for an attachment, so no action and no cursor change.
        Assert.Equal(LeftClick.Unhandled, ClickAction.Left(With(ClickAction.Sit), With(ClickAction.Sit), attachment: true, sitting: false));
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(With(ClickAction.Sit), With(ClickAction.Sit), attachment: true, sitting: false));
    }

    [Fact]
    public void The_objects_own_action_wins_over_its_roots()
    {
        Assert.Equal(ClickAction.Sit, ClickAction.Final(With(ClickAction.Sit), With(ClickAction.Open), false));
    }

    [Fact]
    public void A_child_without_an_action_takes_its_roots()
    {
        Assert.Equal(ClickAction.Open, ClickAction.Final(With(), With(ClickAction.Open), false));
    }

    [Fact]
    public void A_roots_disabled_action_reaches_a_child_that_has_none()
    {
        Assert.Equal(ClickAction.Disabled, ClickAction.Final(With(), With(ClickAction.Disabled), false));
        Assert.Equal(LeftClick.Swallowed, ClickAction.Left(With(), With(ClickAction.Disabled), false, false));
    }

    [Fact]
    public void A_sit_action_sits_unless_already_seated()
    {
        Assert.Equal(LeftClick.Sit, ClickAction.Left(With(ClickAction.Sit), With(), false, sitting: false));
        Assert.Equal(LeftClick.Unhandled, ClickAction.Left(With(ClickAction.Sit), With(), false, sitting: true));
    }

    [Fact]
    public void Open_needs_contents_and_ownership_or_modify_rights()
    {
        Assert.False(ClickAction.OpenAllowed(With(flags: ObjectFlagBits.YouOwner | ObjectFlagBits.InventoryEmpty)));
        Assert.False(ClickAction.OpenAllowed(With(flags: 0)));
        Assert.True(ClickAction.OpenAllowed(With(flags: ObjectFlagBits.YouOwner)));
        Assert.True(ClickAction.OpenAllowed(With(flags: ObjectFlagBits.Modify)));
    }

    [Fact]
    public void An_open_action_opens_when_allowed_and_is_swallowed_when_not()
    {
        var allowed = With(ClickAction.Open, ObjectFlagBits.YouOwner);
        var denied = With(ClickAction.Open, ObjectFlagBits.YouOwner | ObjectFlagBits.InventoryEmpty);
        Assert.Equal(LeftClick.Open, ClickAction.Left(allowed, allowed, false, false));
        Assert.Equal(LeftClick.OpenDenied, ClickAction.Left(denied, denied, false, false));
    }

    [Theory]
    [InlineData(ClickAction.Play)]
    [InlineData(ClickAction.OpenMedia)]
    public void The_actions_this_viewer_does_not_run_are_reported_as_unhandled(byte action)
    {
        Assert.Equal(LeftClick.Unhandled, ClickAction.Left(With(action), With(), false, false));
    }

    [Fact]
    public void Only_the_touch_handler_flag_makes_an_object_touchable()
    {
        Assert.False(ClickAction.Touchable(With(flags: ObjectFlagBits.Scripted), With()));
        Assert.True(ClickAction.Touchable(With(flags: ObjectFlagBits.HandleTouch), With()));
    }

    [Fact]
    public void A_child_is_touchable_when_its_root_handles_touch_unless_its_own_action_is_disabled()
    {
        var root = With(flags: ObjectFlagBits.HandleTouch);
        Assert.True(ClickAction.Touchable(With(), root));
        Assert.False(ClickAction.Touchable(With(ClickAction.Disabled), root));
    }

    [Fact]
    public void The_cursor_follows_the_action_first_then_the_touch_handler()
    {
        Assert.Equal(HoverCursor.Sit, ClickAction.Cursor(With(ClickAction.Sit), With(), false, sitting: false));
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(With(ClickAction.Sit), With(), false, sitting: true));
        var openable = With(ClickAction.Open, ObjectFlagBits.Modify);
        Assert.Equal(HoverCursor.Open, ClickAction.Cursor(openable, openable, false, false));
        var empty = With(ClickAction.Open, ObjectFlagBits.InventoryEmpty);
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(empty, empty, false, false));
        Assert.Equal(HoverCursor.Zoom, ClickAction.Cursor(With(ClickAction.Zoom), With(), false, false));
        Assert.Equal(HoverCursor.Buy, ClickAction.Cursor(With(ClickAction.Buy), With(), false, false));
        Assert.Equal(HoverCursor.Pay, ClickAction.Cursor(With(ClickAction.Pay, ObjectFlagBits.TakesMoney), With(), false, false));
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(With(ClickAction.Pay), With(), false, false));
        Assert.Equal(HoverCursor.Touch, ClickAction.Cursor(With(flags: ObjectFlagBits.HandleTouch), With(), false, false));
        Assert.Equal(HoverCursor.Grab, ClickAction.Cursor(With(flags: ObjectFlagBits.Physics), With(), false, false));
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(With(), With(), false, false));
    }

    [Fact]
    public void A_missing_object_state_is_a_plain_arrow_and_a_touch()
    {
        Assert.Equal(HoverCursor.Arrow, ClickAction.Cursor(null, null, false, false));
        Assert.Equal(LeftClick.Touch, ClickAction.Left(null, null, false, false));
    }
}

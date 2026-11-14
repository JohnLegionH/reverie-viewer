// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A toolbar button behaves as the official viewer's does (indra/llui/lltoolbar.cpp LLToolBar::draw, indra/newview/app_settings/commands.xml,
/// indra/llui/llfloaterreg.cpp toggleInstanceOrBringToFront): it shows pressed exactly while the window it opens is open, and clears as soon
/// as that window closes; pressing it opens a closed window, brings an open window that is not in front to the front, and closes the one
/// that is in front.
/// </summary>
public class ToolbarButtonStateTests
{
    private static FloaterManager Make(params string[] ids)
    {
        var m = new FloaterManager();
        foreach (var id in ids)
            m.Register(new FloaterSpec(id, "t." + id, new UiSize(300, 200), new UiSize(100, 100)));
        return m;
    }

    private static readonly ToolbarButton Chat = new("toolbar_chat", "toolbar.chat", "floater.chat", null, "chat");
    private static readonly ToolbarButton Plain = new("toolbar_x", "toolbar.x", "floater.x");

    [Fact]
    public void A_button_is_pressed_while_its_window_is_open_and_clears_when_it_closes()
    {
        var m = Make("chat", "inv");
        Assert.False(Chat.IsPressed(m));
        m.Open("chat");
        Assert.True(Chat.IsPressed(m));
        m.Open("inv");   // another window in front: the first button stays pressed, its window is still open
        Assert.True(Chat.IsPressed(m));
        m.Close("chat");
        Assert.False(Chat.IsPressed(m));
    }

    [Fact]
    public void A_button_that_opens_no_window_is_never_pressed() => Assert.False(Plain.IsPressed(Make("chat")));

    [Fact]
    public void Pressing_opens_a_closed_window_in_front()
    {
        var m = Make("chat");
        Assert.True(m.ToggleOrBringToFront("chat"));
        Assert.True(m.IsOpen("chat"));
        Assert.Equal("chat", m.FocusedId);
    }

    [Fact]
    public void Pressing_brings_an_open_window_that_is_behind_to_the_front_and_keeps_it_open()
    {
        var m = Make("chat", "inv");
        m.Open("chat");
        m.Open("inv");
        Assert.True(m.ToggleOrBringToFront("chat"));
        Assert.True(m.IsOpen("chat"));
        Assert.True(m.IsOpen("inv"));
        Assert.Equal("chat", m.FocusedId);
    }

    [Fact]
    public void Pressing_closes_the_window_that_is_in_front()
    {
        var m = Make("chat", "inv");
        m.Open("inv");
        m.Open("chat");
        Assert.False(m.ToggleOrBringToFront("chat"));
        Assert.False(m.IsOpen("chat"));
        Assert.False(Chat.IsPressed(m));
        Assert.True(m.IsOpen("inv"));
    }

    [Fact]
    public void A_window_that_refuses_to_close_stays_open_and_pressed()
    {
        var m = Make("chat");
        m.Open("chat");
        m.SetCloseGuard("chat", () => false);
        Assert.True(m.ToggleOrBringToFront("chat"));
        Assert.True(Chat.IsPressed(m));
    }
}

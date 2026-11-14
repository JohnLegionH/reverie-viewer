// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A floater may refuse to close (a window with unsaved changes asks first); the guard is asked on every way of closing.</summary>
public class FloaterCloseGuardTests
{
    private static FloaterManager Manager()
    {
        var m = new FloaterManager();
        m.Register(new FloaterSpec("a", "a.title", new UiSize(300, 200), new UiSize(100, 100)));
        m.Register(new FloaterSpec("b", "b.title", new UiSize(300, 200), new UiSize(100, 100)));
        m.Open("a");
        return m;
    }

    [Fact]
    public void A_guard_that_says_no_keeps_the_floater_open_and_the_close_reports_it()
    {
        var m = Manager();
        int asked = 0;
        m.SetCloseGuard("a", () => { asked++; return false; });
        Assert.False(m.Close("a"));
        Assert.True(m.IsOpen("a"));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_guard_that_says_yes_lets_the_floater_close()
    {
        var m = Manager();
        m.SetCloseGuard("a", () => true);
        Assert.True(m.Close("a"));
        Assert.False(m.IsOpen("a"));
    }

    [Fact]
    public void Forcing_a_close_skips_the_guard()
    {
        var m = Manager();
        int asked = 0;
        m.SetCloseGuard("a", () => { asked++; return false; });
        Assert.True(m.Close("a", force: true));
        Assert.False(m.IsOpen("a"));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Toggling_and_closing_the_focused_floater_ask_the_guard_too()
    {
        var m = Manager();
        int asked = 0;
        m.SetCloseGuard("a", () => { asked++; return false; });
        Assert.True(m.Toggle("a"));          // still open: the close was refused
        Assert.False(m.CloseFocused());
        Assert.Equal(2, asked);
        Assert.True(m.IsOpen("a"));
    }

    [Fact]
    public void A_guard_is_for_its_own_floater_and_removing_it_restores_the_plain_close()
    {
        var m = Manager();
        m.Open("b");
        m.SetCloseGuard("a", () => false);
        Assert.True(m.Close("b"));
        Assert.False(m.Close("a"));
        m.SetCloseGuard("a", null);
        Assert.True(m.Close("a"));
    }

    [Fact]
    public void Closing_a_floater_that_is_not_open_does_not_ask_the_guard()
    {
        var m = Manager();
        int asked = 0;
        m.SetCloseGuard("b", () => { asked++; return true; });
        Assert.False(m.Close("b"));
        Assert.Equal(0, asked);
    }
}

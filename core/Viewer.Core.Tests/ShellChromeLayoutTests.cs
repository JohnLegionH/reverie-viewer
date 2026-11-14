// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The window frame's rows: menu bar 26, navigation bar 32, favourites strip 22, chat bar, bottom toolbar 44. Rows that are
/// not shown take no room; the rest tile the window with no gap or overlap. Sizes are in interface units: the interface size
/// setting (100 to 200 %) scales them all together, so the same layout is made for the smaller logical window. Plain .NET,
/// every member independent, so the class runs in parallel.
/// </summary>
public class ShellChromeLayoutTests
{
    private static readonly ShellMetrics M = ShellMetrics.Default;
    private static readonly ChromeRows All = new(Toolbar: true, NavBar: true, Favourites: true, ChatBar: true);

    [Fact]
    public void The_rows_have_the_heights_of_the_design()
    {
        var r = ShellLayout.Compute(new UiSize(1280, 720), M, All);
        Assert.Equal(26, r.MenuBar.H);
        Assert.Equal(32, r.NavBar.H);
        Assert.Equal(22, r.Favourites.H);
        Assert.Equal(44, r.Toolbar.H);
        Assert.Equal(M.ChatBarHeight, r.ChatBar.H);
        Assert.Equal(720 - 26 - 32 - 22 - 44 - M.ChatBarHeight, r.Main.H);
    }

    [Fact]
    public void The_rows_stand_in_order_from_the_top_and_tile_the_window()
    {
        var r = ShellLayout.Compute(new UiSize(1280, 720), M, All);
        Assert.Equal(0, r.MenuBar.Y);
        Assert.Equal(r.MenuBar.Bottom, r.NavBar.Y);
        Assert.Equal(r.NavBar.Bottom, r.Favourites.Y);
        Assert.Equal(r.Favourites.Bottom, r.Main.Y);
        Assert.Equal(r.Main.Bottom, r.ChatBar.Y);
        Assert.Equal(r.ChatBar.Bottom, r.Toolbar.Y);
        Assert.Equal(720, r.Toolbar.Bottom);
        Assert.All(new[] { r.MenuBar, r.NavBar, r.Favourites, r.Main, r.ChatBar, r.Toolbar }, row => Assert.Equal(1280, row.W));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, true)]
    public void A_row_that_is_not_shown_takes_no_room_and_the_main_view_takes_what_is_left(bool toolbar, bool nav, bool fav, bool chat)
    {
        var r = ShellLayout.Compute(new UiSize(1000, 700), M, new ChromeRows(toolbar, nav, fav, chat));
        Assert.Equal(nav ? 32 : 0, r.NavBar.H);
        Assert.Equal(fav ? 22 : 0, r.Favourites.H);
        Assert.Equal(chat ? M.ChatBarHeight : 0, r.ChatBar.H);
        Assert.Equal(toolbar ? 44 : 0, r.Toolbar.H);
        float rows = 26 + r.NavBar.H + r.Favourites.H + r.ChatBar.H + r.Toolbar.H;
        Assert.Equal(700 - rows, r.Main.H);
        Assert.Equal(700, r.Toolbar.Bottom);
    }

    [Theory]
    [InlineData(1920, 1080, 100)]
    [InlineData(1920, 1080, 150)]
    [InlineData(1920, 1080, 200)]
    [InlineData(2560, 1440, 200)]
    public void The_interface_size_gives_the_same_rows_in_a_window_that_is_smaller_in_interface_units(int w, int h, int percent)
    {
        var logical = new UiSize(w * 100f / percent, h * 100f / percent);
        var r = ShellLayout.Compute(logical, M, All);
        Assert.Equal(26, r.MenuBar.H);
        Assert.Equal(44, r.Toolbar.H);
        Assert.True(r.Main.H > 0 && r.Main.W == logical.W);
        Assert.Equal(logical.H, r.Toolbar.Bottom, 3);
    }

    [Fact]
    public void A_window_below_the_old_minimum_is_laid_out_at_its_own_size_with_every_row()
    {
        var r = ShellLayout.Compute(new UiSize(300, 200), M, All);
        Assert.Equal(200, r.Toolbar.Bottom);
        Assert.Equal(200 - 26 - 32 - 22 - 44 - M.ChatBarHeight, r.Main.H);
        Assert.True(r.Main.H > 0);
    }

    [Fact]
    public void A_frame_that_keeps_no_toolbar_row_has_none_even_with_buttons_and_the_default_keeps_one_for_them()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        shell.Toolbar.Add(new ToolbarButton("tb.about", "menu.help.about", "floater.about"));
        Assert.True(shell.Layout(new UiSize(1280, 720)).Toolbar.H > 0);   // as before: a button gets its row
        shell.Chrome = new ChromeRows(Toolbar: false, ChatBar: true);   // a frame whose dock floats asks for none
        var r = shell.Layout(new UiSize(1280, 720));
        Assert.Equal(0, r.Toolbar.H);
        Assert.Equal(720, r.ChatBar.Y + r.ChatBar.H);
    }

    [Fact]
    public void Floaters_live_in_the_main_view_only_and_the_controller_keeps_them_there_when_a_row_appears()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        var before = shell.Layout(new UiSize(1280, 720));
        shell.Chrome = new ChromeRows(Toolbar: true, NavBar: true, ChatBar: true);
        var after = shell.Layout(new UiSize(1280, 720));
        Assert.Equal(32, after.NavBar.H);
        Assert.True(after.Main.H < before.Main.H);
        Assert.Equal(after.Main, shell.Floaters.Bounds);
    }
}

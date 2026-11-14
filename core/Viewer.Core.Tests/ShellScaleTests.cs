// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The window frame at the interface sizes 100, 150 and 200 %: the layout works in logical pixels (the window's pixels divided by the
/// scale), so a small window at a large scale is a small logical window and must still show every bar. Engine-free; every member
/// builds its own objects, so the class runs in parallel.
/// </summary>
public class ShellScaleTests
{
    private static readonly ShellMetrics M = ShellMetrics.Default;
    private static readonly ChromeRows World = new(Toolbar: true, NavBar: true, ChatBar: true);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (w, h) in new[] { (1152, 648), (1280, 720), (1920, 1080) })
            foreach (int pct in new[] { 100, 150, 200 })
                yield return [pct, w, h];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_bar_lies_inside_the_logical_window_at_every_scale(int pct, int w, int h)
    {
        var logical = new UiSize(w * 100f / pct, h * 100f / pct);
        var r = ShellLayout.Compute(logical, M, World);
        var window = new UiRect(0, 0, logical.W, logical.H);
        Assert.Equal(window, r.Window);
        foreach (var region in new[] { r.MenuBar, r.NavBar, r.ChatBar, r.Toolbar, r.Main })
            Assert.True(window.Encloses(region), $"{region} is not inside {window}");
        Assert.True(r.Main.H > 0, "the main view has no height");
        Assert.Equal(logical.H, r.MenuBar.H + r.NavBar.H + r.Main.H + r.ChatBar.H + r.Toolbar.H, 3);
    }

    [Fact]
    public void A_window_that_cannot_hold_the_fixed_rows_still_has_no_negative_region()
    {
        var r = ShellLayout.Compute(new UiSize(300, 60), M, World);
        Assert.True(r.Main.H >= 0 && r.Main.W > 0);
        Assert.True(r.Toolbar.Bottom >= r.ChatBar.Bottom - 0.001f);
    }

    [Fact]
    public void Floaters_stay_inside_the_main_view_of_a_scaled_window_and_their_title_bar_stays_reachable()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        shell.Chrome = World;
        var regions = shell.Layout(new UiSize(576, 324));   // 1152 x 648 at 200 %
        shell.Floaters.Open(ShellController.AboutFloaterId);
        shell.Floaters.Move(ShellController.AboutFloaterId, 5000, -400);
        var rect = shell.Floaters.RectOf(ShellController.AboutFloaterId)!.Value;
        Assert.True(regions.Main.Encloses(rect), $"{rect} is not inside {regions.Main}");
        Assert.True(rect.Y >= regions.Main.Y);
    }

    [Fact]
    public void A_floater_larger_than_the_scaled_main_view_is_shrunk_to_it()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        shell.Chrome = World;
        shell.Layout(new UiSize(1920, 1080));
        shell.Floaters.Open(ShellController.AboutFloaterId);
        var small = shell.Layout(new UiSize(480, 270));   // 960 x 540 at 200 %
        var rect = shell.Floaters.RectOf(ShellController.AboutFloaterId)!.Value;
        Assert.True(small.Main.Encloses(rect), $"{rect} is not inside {small.Main}");
    }

    [Fact]
    public void A_window_whose_size_is_larger_than_the_main_view_is_made_to_fit_it()
    {
        var manager = new FloaterManager();
        manager.Register(new FloaterSpec("big", "k.big", new UiSize(800, 600), new UiSize(400, 300)));
        var region = new UiRect(0, 60, 480, 190);   // a 960 x 540 window at 200 %, under the bars
        manager.SetBounds(region);
        manager.Open("big");
        Assert.True(region.Encloses(manager.RectOf("big")!.Value), $"{manager.RectOf("big")} is not inside {region}");
        manager.Move("big", -50, 9000);
        Assert.True(region.Encloses(manager.RectOf("big")!.Value));
    }

    // ---------------- bars taller than the design height ----------------

    private static ShellController ShellWithOneButton()
    {
        var shell = new ShellController(StringTable.English(), () => { }) { Chrome = World };
        shell.Toolbar.Add(new ToolbarButton("t", "toolbar.chat", "c"));
        return shell;
    }

    [Fact]
    public void A_bar_whose_content_is_taller_than_the_design_height_gets_the_room_it_needs()
    {
        var r = ShellWithOneButton().Layout(new UiSize(1000, 700), new BarHeights(Menu: 34, Nav: 36, Chat: 38, Toolbar: 50));
        Assert.Equal(34, r.MenuBar.H);
        Assert.Equal(36, r.NavBar.H);
        Assert.Equal(38, r.ChatBar.H);
        Assert.Equal(50, r.Toolbar.H);
        Assert.Equal(700 - 34 - 36 - 38 - 50, r.Main.H);
        Assert.Equal(r.MenuBar.Bottom, r.NavBar.Y);
        Assert.Equal(r.ChatBar.Bottom, r.Toolbar.Y);
        Assert.Equal(700, r.Toolbar.Bottom);
    }

    [Fact]
    public void A_bar_whose_content_is_shorter_than_the_design_height_keeps_the_design_height()
    {
        var r = ShellWithOneButton().Layout(new UiSize(1000, 700), new BarHeights(Menu: 10, Nav: 10, Chat: 10, Toolbar: 10));
        Assert.Equal(M.MenuBarHeight, r.MenuBar.H);
        Assert.Equal(M.NavBarHeight, r.NavBar.H);
        Assert.Equal(M.ChatBarHeight, r.ChatBar.H);
        Assert.Equal(M.ToolbarHeight, r.Toolbar.H);
    }

    // ---------------- toolbar overflow ----------------

    [Fact]
    public void Every_button_shows_when_they_all_fit()
    {
        var f = ToolbarFit.Plan(count: 10, available: 1000, buttonWidth: 64, separation: 4, moreWidth: 48);
        Assert.Equal(new ToolbarFitResult(10, 0), f);
        Assert.False(f.NeedsMore);
    }

    [Fact]
    public void Buttons_that_do_not_fit_go_behind_the_more_button_and_the_more_button_fits_too()
    {
        var f = ToolbarFit.Plan(count: 12, available: 576, buttonWidth: 64, separation: 4, moreWidth: 48);
        Assert.Equal(7, f.Shown);
        Assert.Equal(5, f.Hidden);
        Assert.True(f.NeedsMore);
        float used = f.Shown * 64 + f.Shown * 4 + 48;   // the shown buttons, a separation after each, and the more button
        Assert.True(used <= 576);
        Assert.True(used + 68 > 576, "one more button would also have fitted");
    }

    [Theory]
    [InlineData(12, 100)]
    [InlineData(12, 40)]
    [InlineData(1, 10)]
    public void A_bar_too_narrow_for_any_button_shows_only_the_more_button(int count, float available)
    {
        var f = ToolbarFit.Plan(count, available, 64, 4, 48);
        Assert.Equal(0, f.Shown);
        Assert.Equal(count, f.Hidden);
    }

    [Fact]
    public void Buttons_of_different_widths_are_counted_by_their_own_widths()
    {
        float[] widths = [100, 50, 50, 100, 100];
        var f = ToolbarFit.Plan(widths, available: 300, separation: 4, moreWidth: 40);
        Assert.Equal(new ToolbarFitResult(3, 2), f);   // 100+50+50, three separations, the More button: 252 of 300; a fourth would need 356
        Assert.Equal(new ToolbarFitResult(5, 0), ToolbarFit.Plan(widths, 416, 4, 40));
        Assert.Equal(new ToolbarFitResult(4, 1), ToolbarFit.Plan(widths, 360, 4, 40));
    }

    [Fact]
    public void An_empty_toolbar_has_nothing_shown_and_nothing_hidden()
    {
        Assert.Equal(new ToolbarFitResult(0, 0), ToolbarFit.Plan(0, 500, 64, 4, 48));
    }
}

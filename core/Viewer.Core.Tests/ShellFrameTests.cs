// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shell's frame, engine-free: the layout of the menu bar, toolbar and main view, how floaters open, close, stack,
/// keep their place and stay inside the main view, who gets the keyboard, and the menus and toolbar as data. No window
/// is opened, so no test-window rule applies. Every member builds its own objects (no process-wide state), so the class
/// runs in parallel with the others.
/// </summary>
public class ShellFrameTests
{
    private static readonly ShellMetrics M = ShellMetrics.Default;

    // ---------------- the frame ----------------

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    [InlineData(640, 480)]
    public void The_regions_tile_the_window_with_no_gap_and_no_overlap(float w, float h)
    {
        foreach (bool toolbar in new[] { false, true })
        {
            var r = ShellLayout.Compute(new UiSize(w, h), M, toolbar);
            Assert.Equal(new UiRect(0, 0, w, h), r.Window);
            Assert.Equal(new UiRect(0, 0, w, M.MenuBarHeight), r.MenuBar);
            Assert.Equal(M.MenuBarHeight, r.Main.Y);
            Assert.Equal(w, r.Main.W);
            Assert.Equal(r.Main.Bottom, r.Toolbar.Y);
            Assert.Equal(h, r.Toolbar.Bottom);
            Assert.Equal(w, r.Toolbar.W);
            Assert.False(r.MenuBar.Overlaps(r.Main));
            Assert.False(r.Main.Overlaps(r.Toolbar));
            Assert.Equal(h, r.MenuBar.H + r.Main.H + r.Toolbar.H);
        }
    }

    [Fact]
    public void An_empty_toolbar_takes_no_room_and_a_toolbar_with_a_button_takes_its_height()
    {
        var empty = ShellLayout.Compute(new UiSize(1280, 720), M, toolbarHasButtons: false);
        var full = ShellLayout.Compute(new UiSize(1280, 720), M, toolbarHasButtons: true);
        Assert.Equal(0, empty.Toolbar.H);
        Assert.Equal(M.ToolbarHeight, full.Toolbar.H);
        Assert.Equal(empty.Main.H - M.ToolbarHeight, full.Main.H);
    }

    [Fact]
    public void A_small_window_is_laid_out_at_its_own_size_because_the_layout_works_in_logical_pixels()
    {
        var r = ShellLayout.Compute(new UiSize(300, 200), M, toolbarHasButtons: true);
        Assert.Equal(new UiRect(0, 0, 300, 200), r.Window);
        Assert.True(r.Main.W > 0 && r.Main.H > 0);
    }

    // ---------------- floaters ----------------

    private static readonly FloaterSpec Alpha = new("alpha", "k.alpha", new UiSize(300, 200), new UiSize(150, 100));
    private static readonly FloaterSpec Beta = new("beta", "k.beta", new UiSize(240, 160), new UiSize(120, 80));
    private static readonly FloaterSpec Fixed = new("fixed", "k.fixed", new UiSize(200, 100), new UiSize(200, 100), Resizable: false);
    private static readonly UiRect Bounds = new(0, 26, 1000, 600);

    private static FloaterManager Floaters(IFloaterPlaces? places = null, params FloaterSpec[] specs)
    {
        var m = new FloaterManager(places);
        foreach (var s in specs.Length == 0 ? [Alpha, Beta, Fixed] : specs) m.Register(s);
        m.SetBounds(Bounds);
        return m;
    }

    [Fact]
    public void Opening_a_floater_shows_it_on_top_with_the_focus_inside_the_main_view_at_its_default_size()
    {
        var m = Floaters();
        Assert.False(m.IsOpen("alpha"));
        Assert.True(m.Open("alpha"));
        var s = Assert.Single(m.OpenFloaters);
        Assert.Equal("alpha", s.Id);
        Assert.True(s.Focused);
        Assert.Equal("alpha", m.FocusedId);
        Assert.Equal(300, s.Rect.W);
        Assert.Equal(200, s.Rect.H);
        Assert.True(Bounds.Encloses(s.Rect));
        Assert.False(m.Open("nobody"));   // an unknown floater opens nothing
        Assert.Single(m.OpenFloaters);
    }

    [Fact]
    public void Opening_one_that_is_open_raises_it_and_does_not_open_a_second()
    {
        var m = Floaters();
        m.Open("alpha");
        m.Open("beta");
        Assert.Equal(["alpha", "beta"], m.OpenFloaters.Select(f => f.Id));
        Assert.Equal("beta", m.FocusedId);
        Assert.True(m.Open("alpha"));
        Assert.Equal(["beta", "alpha"], m.OpenFloaters.Select(f => f.Id));
        Assert.Equal("alpha", m.FocusedId);
        Assert.Equal(2, m.OpenFloaters.Count);
        Assert.True(m.OpenFloaters[1].Z > m.OpenFloaters[0].Z);
    }

    [Fact]
    public void A_second_floater_is_offset_from_the_first_so_both_title_bars_show()
    {
        var m = Floaters();
        m.Open("alpha");
        m.Open("beta");
        var a = m.RectOf("alpha")!.Value;
        var b = m.RectOf("beta")!.Value;
        Assert.True(b.X > a.X && b.Y > a.Y);
        Assert.True(Bounds.Encloses(b));
    }

    [Fact]
    public void Closing_passes_the_focus_to_the_next_one_down_and_a_close_of_an_unfocused_one_keeps_it()
    {
        var m = Floaters();
        m.Open("alpha");
        m.Open("beta");
        m.Open("fixed");
        Assert.True(m.Close("alpha"));
        Assert.Equal("fixed", m.FocusedId);
        Assert.True(m.Close("fixed"));
        Assert.Equal("beta", m.FocusedId);
        Assert.False(m.Close("fixed"));   // not open
        Assert.True(m.CloseFocused());
        Assert.Null(m.FocusedId);
        Assert.Empty(m.OpenFloaters);
        Assert.False(m.CloseFocused());
    }

    [Fact]
    public void Toggle_opens_a_closed_floater_and_closes_an_open_one()
    {
        var m = Floaters();
        Assert.True(m.Toggle("alpha"));
        Assert.True(m.IsOpen("alpha"));
        Assert.False(m.Toggle("alpha"));
        Assert.False(m.IsOpen("alpha"));
        Assert.False(m.Toggle("nobody"));
    }

    [Fact]
    public void Focus_raises_a_floater_and_refuses_one_that_is_not_open()
    {
        var m = Floaters();
        m.Open("alpha");
        m.Open("beta");
        Assert.True(m.Focus("alpha"));
        Assert.Equal("alpha", m.OpenFloaters[^1].Id);
        Assert.False(m.Focus("fixed"));
        Assert.Equal("alpha", m.FocusedId);
    }

    [Fact]
    public void A_floater_moved_or_resized_stays_inside_the_main_view_and_above_its_minimum()
    {
        var m = Floaters();
        m.Open("alpha");
        Assert.True(m.Move("alpha", -500, -500));
        Assert.Equal(new UiRect(Bounds.X, Bounds.Y, 300, 200), m.RectOf("alpha"));
        Assert.True(m.Move("alpha", 5000, 5000));
        Assert.Equal(new UiRect(Bounds.Right - 300, Bounds.Bottom - 200, 300, 200), m.RectOf("alpha"));

        Assert.True(m.Resize("alpha", 10, 10));
        Assert.Equal((150f, 100f), (m.RectOf("alpha")!.Value.W, m.RectOf("alpha")!.Value.H));   // the minimum
        Assert.True(m.Resize("alpha", 5000, 5000));
        Assert.True(Bounds.Encloses(m.RectOf("alpha")!.Value));
        Assert.Equal((Bounds.W, Bounds.H), (m.RectOf("alpha")!.Value.W, m.RectOf("alpha")!.Value.H));

        Assert.False(m.Move("beta", 1, 1));    // not open
        m.Open("fixed");
        Assert.False(m.Resize("fixed", 400, 400));   // not resizable
        Assert.Equal(200, m.RectOf("fixed")!.Value.W);
    }

    [Fact]
    public void Shrinking_the_main_view_pulls_every_open_floater_back_inside_it()
    {
        var m = Floaters();
        m.Open("alpha");
        m.Open("beta");
        m.Move("alpha", 700, 400);
        var small = new UiRect(0, 26, 400, 300);
        m.SetBounds(small);
        foreach (var f in m.OpenFloaters)
            Assert.True(small.Encloses(f.Rect), $"{f.Id} {f.Rect} outside {small}");
        // and a main view too small for the floater makes the floater the main view's size, never larger
        m.SetBounds(new UiRect(0, 26, 200, 120));
        Assert.True(new UiRect(0, 26, 200, 120).Encloses(m.RectOf("alpha")!.Value));
    }

    [Fact]
    public void A_floater_reopens_where_it_was_closed_and_a_new_manager_reads_the_same_places()
    {
        var places = new MemoryFloaterPlaces();
        var m = Floaters(places);
        m.Open("alpha");
        m.Move("alpha", 333, 222);
        m.Resize("alpha", 410, 260);
        var where = m.RectOf("alpha")!.Value;
        m.Close("alpha");
        Assert.Null(m.RectOf("alpha"));
        m.Open("alpha");
        Assert.Equal(where, m.RectOf("alpha"));

        var later = Floaters(places);
        later.Open("alpha");
        Assert.Equal(where, later.RectOf("alpha"));

        // a remembered place that no longer fits is pulled inside
        var small = Floaters(places);
        small.SetBounds(new UiRect(0, 26, 500, 300));
        small.Open("alpha");
        Assert.True(new UiRect(0, 26, 500, 300).Encloses(small.RectOf("alpha")!.Value));
    }

    [Fact]
    public void Changed_is_raised_for_each_real_change_and_not_for_a_no_op()
    {
        var m = Floaters();
        int changes = 0;
        m.Changed += () => changes++;
        m.Open("alpha");
        Assert.Equal(1, changes);
        m.Open("alpha");   // already open, already on top: nothing to redraw
        Assert.Equal(1, changes);
        m.Move("alpha", 100, 100);
        Assert.Equal(2, changes);
        m.Move("alpha", 100, 100);
        Assert.Equal(2, changes);
        m.Close("alpha");
        Assert.Equal(3, changes);
        m.Close("alpha");
        Assert.Equal(3, changes);
    }

    [Fact]
    public void A_floater_id_is_registered_once()
    {
        var m = new FloaterManager();
        m.Register(Alpha);
        Assert.Throws<ArgumentException>(() => m.Register(Alpha with { TitleKey = "other" }));
        Assert.Equal(Alpha, m.SpecOf("alpha"));
        Assert.Single(m.Specs);
    }

    // ---------------- who gets the keyboard ----------------

    [Fact]
    public void Movement_keys_count_only_with_the_window_focused_and_no_text_field_in_use()
    {
        var f = new InputFocus(Floaters());
        var seen = new List<bool>();
        f.MovementAllowedChanged += seen.Add;
        Assert.False(f.MovementAllowed);   // nothing is focused yet
        f.SetWindowFocused(true);
        Assert.True(f.MovementAllowed);
        f.SetTextEntryActive(true);
        Assert.False(f.MovementAllowed);
        f.SetTextEntryActive(true);        // no change
        f.SetWindowFocused(false);
        f.SetTextEntryActive(false);       // the window still has no focus
        Assert.False(f.MovementAllowed);
        f.SetWindowFocused(true);
        Assert.True(f.MovementAllowed);
        Assert.Equal([true, false, true], seen);   // each change once, so held keys are let go exactly when typing starts
    }

    [Fact]
    public void Escape_lets_go_of_a_text_field_first_then_closes_the_focused_floater_then_does_nothing()
    {
        var floaters = Floaters();
        var f = new InputFocus(floaters);
        f.SetWindowFocused(true);
        int releases = 0;
        f.TextReleaseRequested += () => releases++;
        floaters.Open("alpha");
        floaters.Open("beta");

        f.SetTextEntryActive(true);
        Assert.Equal(EscapeResult.ReleasedTextEntry, f.OnEscape());
        Assert.Equal(1, releases);
        Assert.True(floaters.IsOpen("beta"));   // the first Escape only let go of the text field
        f.SetTextEntryActive(false);

        Assert.Equal(EscapeResult.ClosedFloater, f.OnEscape());
        Assert.False(floaters.IsOpen("beta"));
        Assert.Equal(EscapeResult.ClosedFloater, f.OnEscape());
        Assert.Equal(EscapeResult.None, f.OnEscape());
        Assert.Equal(1, releases);
    }

    [Fact]
    public void Escape_in_a_field_that_answers_it_itself_is_left_to_that_field()
    {
        var floaters = Floaters();
        var f = new InputFocus(floaters);
        f.SetWindowFocused(true);
        int releases = 0;
        f.TextReleaseRequested += () => releases++;
        floaters.Open("alpha");
        f.SetTextEntryActive(true);
        Assert.Equal(EscapeResult.None, f.OnEscape(fieldTakesEscape: true));   // a name prompt cancels on Esc: the shell does nothing
        Assert.Equal(0, releases);
        Assert.True(floaters.IsOpen("alpha"));
        Assert.Equal(EscapeResult.ReleasedTextEntry, f.OnEscape(fieldTakesEscape: false));   // any other field: as before
        Assert.Equal(1, releases);
    }

    [Fact]
    public void A_click_on_the_world_takes_the_keyboard_back_from_a_text_field_and_otherwise_does_nothing()
    {
        var f = new InputFocus(Floaters());
        f.SetWindowFocused(true);
        int releases = 0;
        f.TextReleaseRequested += () => releases++;
        Assert.False(f.OnWorldPointerDown());
        Assert.Equal(0, releases);
        f.SetTextEntryActive(true);
        Assert.True(f.OnWorldPointerDown());
        Assert.Equal(1, releases);
    }

    // ---------------- menus, toolbar and commands ----------------

    [Fact]
    public void Commands_run_once_when_chosen_and_an_unknown_one_runs_nothing()
    {
        var c = new ShellCommands();
        int runs = 0;
        c.Register("x.go", () => runs++);
        Assert.True(c.Has("x.go"));
        Assert.True(c.Execute("x.go"));
        Assert.Equal(1, runs);
        Assert.False(c.Execute("x.nope"));
        Assert.False(c.Has("x.nope"));
        Assert.Throws<ArgumentException>(() => c.Register("x.go", () => { }));
    }

    [Fact]
    public void Menus_keep_their_order_a_disabled_entry_runs_nothing_and_a_duplicate_is_refused()
    {
        var menus = new MenuModel();
        var commands = new ShellCommands();
        int runs = 0;
        commands.Register("c.one", () => runs++);
        commands.Register("c.two", () => runs += 10);
        bool allowed = false;
        menus.AddMenu("first", "k.first");
        menus.AddMenu("second", "k.second");
        menus.AddEntry("first", new MenuEntry("first.one", "k.one", "c.one"));
        menus.AddEntry("first", new MenuEntry("first.two", "k.two", "c.two", () => allowed));
        Assert.Equal(["first", "second"], menus.Menus.Select(m => m.Id));
        Assert.Equal(["first.one", "first.two"], menus.Menus[0].Entries.Select(e => e.Id));
        Assert.Empty(menus.Menus[1].Entries);

        Assert.True(menus.Activate("first.one", commands));
        Assert.False(menus.Activate("first.two", commands));   // disabled
        Assert.Equal(1, runs);
        allowed = true;
        Assert.True(menus.Activate("first.two", commands));
        Assert.Equal(11, runs);
        Assert.False(menus.Activate("first.nope", commands));

        Assert.Throws<ArgumentException>(() => menus.AddMenu("first", "k.again"));
        Assert.Throws<ArgumentException>(() => menus.AddEntry("first", new MenuEntry("first.one", "k.one", "c.one")));
        Assert.Throws<ArgumentException>(() => menus.AddEntry("missing", new MenuEntry("m.x", "k.x", "c.one")));
    }

    [Fact]
    public void A_toolbar_button_makes_the_toolbar_appear_in_the_layout()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        var window = new UiSize(1280, 720);
        Assert.Equal(0, shell.Layout(window).Toolbar.H);
        shell.Toolbar.Add(new ToolbarButton("tb.about", "menu.help.about", "floater.about"));
        Assert.Equal(M.ToolbarHeight, shell.Layout(window).Toolbar.H);
        Assert.Throws<ArgumentException>(() => shell.Toolbar.Add(new ToolbarButton("tb.about", "menu.help.about", "floater.about")));
    }

    // ---------------- the shell as shipped ----------------

    [Fact]
    public void The_shell_ships_a_me_menu_with_quit_and_a_help_menu_with_about_and_nothing_else()
    {
        int quits = 0;
        var shell = new ShellController(StringTable.English(), () => quits++);
        Assert.Equal(["me", "help"], shell.Menus.Menus.Select(m => m.Id));
        Assert.Equal(["me.quit"], shell.Menus.Menus[0].Entries.Select(e => e.Id));
        Assert.Equal(["help.about"], shell.Menus.Menus[1].Entries.Select(e => e.Id));
        Assert.Empty(shell.Toolbar.Buttons);

        Assert.True(shell.Menus.Activate("me.quit", shell.Commands));
        Assert.Equal(1, quits);

        Assert.False(shell.Floaters.IsOpen(ShellController.AboutFloaterId));
        Assert.True(shell.Menus.Activate("help.about", shell.Commands));
        Assert.True(shell.Floaters.IsOpen(ShellController.AboutFloaterId));
        Assert.True(shell.Menus.Activate("help.about", shell.Commands));   // the entry toggles
        Assert.False(shell.Floaters.IsOpen(ShellController.AboutFloaterId));
    }

    [Fact]
    public void Every_command_a_shipped_entry_or_button_names_is_registered()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        foreach (var e in shell.Menus.Menus.SelectMany(m => m.Entries))
            Assert.True(shell.Commands.Has(e.CommandId), $"{e.Id} names {e.CommandId}, which nothing runs");
        foreach (var b in shell.Toolbar.Buttons)
            Assert.True(shell.Commands.Has(b.CommandId), $"{b.Id} names {b.CommandId}, which nothing runs");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pseudo")]
    public void Every_label_title_and_accessible_name_the_shell_shows_is_in_the_string_table(string table)
    {
        var strings = table == "en" ? StringTable.English() : StringTable.Pseudo();
        var shell = new ShellController(StringTable.English(), () => { });
        var keys = new List<string> { "shell.menubar", "shell.toolbar", "shell.main", "floater.close", "floater.close.named", "floater.resize.named" };
        foreach (var m in shell.Menus.Menus)
        {
            keys.Add(m.LabelKey);
            keys.AddRange(m.Entries.Select(e => e.LabelKey));
        }
        keys.AddRange(shell.Floaters.Specs.Select(s => s.TitleKey));
        keys.Add("about.body");
        foreach (var k in keys)
        {
            Assert.True(strings.Has(k), $"no string for {k}");
            Assert.DoesNotContain("[[", strings.Get(k, "x"));
        }
        Assert.Contains("x", strings.Get("floater.close.named", "x"));   // the close button names the floater it closes
    }

    [Fact]
    public void The_about_floater_is_registered_with_sensible_sizes_and_fits_the_smallest_window()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        var about = Assert.Single(shell.Floaters.Specs, s => s.Id == ShellController.AboutFloaterId);
        Assert.True(about.DefaultSize.W >= about.MinSize.W && about.DefaultSize.H >= about.MinSize.H);
        var regions = shell.Layout(M.MinWindow);
        Assert.True(regions.Main.W >= about.DefaultSize.W && regions.Main.H >= about.DefaultSize.H);
        shell.Floaters.Open(ShellController.AboutFloaterId);
        Assert.True(regions.Main.Encloses(shell.Floaters.RectOf(ShellController.AboutFloaterId)!.Value));
    }

    [Fact]
    public void Laying_the_frame_out_again_keeps_the_open_floaters_inside_the_new_main_view()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        shell.Layout(new UiSize(1920, 1080));
        shell.Floaters.Open(ShellController.AboutFloaterId);
        shell.Floaters.Move(ShellController.AboutFloaterId, 1500, 900);
        var small = shell.Layout(new UiSize(800, 600));
        Assert.True(small.Main.Encloses(shell.Floaters.RectOf(ShellController.AboutFloaterId)!.Value));
    }
}

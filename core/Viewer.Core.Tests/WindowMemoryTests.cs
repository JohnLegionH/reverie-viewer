// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The window remembers its size, place and maximized state as the official viewer does: the size is kept while the window is not
/// maximized, the place likewise, and the maximized state always. A first run (nothing kept yet) opens maximized. A kept place that no
/// screen shows any more is replaced by the official default place, so the window is never out of reach.
/// </summary>
public class WindowMemoryTests
{
    private static readonly WindowRect Screen1 = new(0, 0, 1920, 1080);
    private static readonly WindowRect Screen2 = new(1920, 0, 2560, 1440);
    private static readonly WindowRect[] Both = [Screen1, Screen2];

    private static SettingsService Fresh() => new(null);

    [Fact]
    public void A_first_run_opens_maximized()
    {
        var plan = WindowMemory.Plan(Fresh(), [Screen1], WindowStartMode.Last);
        Assert.True(plan.Maximized);
    }

    [Fact]
    public void A_first_run_that_then_leaves_maximized_opens_at_the_official_default_size_and_place()
    {
        var s = Fresh();
        s.Store(Settings.WindowMaximized, false);
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.False(plan.Maximized);
        Assert.Equal(new WindowRect(10, 10, 1024, 738), plan.Rect);   // the official viewer's WindowX, WindowY, WindowWidth, WindowHeight
    }

    [Fact]
    public void A_kept_window_comes_back_where_and_as_large_as_it_was()
    {
        var s = Fresh();
        Assert.True(WindowMemory.Record(s, maximized: false, new WindowRect(300, 200, 1280, 800)));
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Last);
        Assert.False(plan.Maximized);
        Assert.Equal(new WindowRect(300, 200, 1280, 800), plan.Rect);
    }

    [Fact]
    public void A_maximized_window_comes_back_maximized_and_keeps_the_size_it_had_before()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800));
        Assert.True(WindowMemory.Record(s, maximized: true, new WindowRect(0, 0, 1920, 1080)));   // the maximized size is not kept
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.Equal(new WindowRect(300, 200, 1280, 800), plan.Rect);
    }

    [Fact]
    public void Recording_what_is_already_kept_writes_nothing()
    {
        var s = Fresh();
        Assert.True(WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800)));
        Assert.False(WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800)));
        Assert.True(WindowMemory.Record(s, false, new WindowRect(310, 200, 1280, 800)));   // a move is a change
        Assert.True(WindowMemory.Record(s, true, new WindowRect(0, 0, 1920, 1080)));
        Assert.False(WindowMemory.Record(s, true, new WindowRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void A_window_on_a_screen_that_is_gone_opens_maximized_on_the_primary_screen()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(2400, 300, 1280, 800), Screen2);   // on the second screen
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen1));   // the primary screen, which is the first one given
    }

    private static bool OnScreen(WindowStart plan, WindowRect screen) =>
        plan.Rect is { } r && r.X >= screen.X && r.Y >= screen.Y && r.X < screen.X + screen.Width && r.Y < screen.Y + screen.Height;

    [Fact]
    public void A_maximized_window_comes_back_maximized_on_the_screen_it_was_on()
    {
        var s = Fresh();
        WindowMemory.Record(s, true, new WindowRect(0, 0, 2560, 1440), Screen2);
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen2));
    }

    [Fact]
    public void A_maximized_window_whose_screen_is_gone_opens_maximized_on_the_primary_screen()
    {
        var s = Fresh();
        WindowMemory.Record(s, true, new WindowRect(0, 0, 2560, 1440), Screen2);
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen1));
    }

    [Fact]
    public void The_screen_is_kept_with_the_window_and_only_written_when_it_changes()
    {
        var s = Fresh();
        Assert.True(WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800), Screen2));
        Assert.False(WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800), Screen2));
        Assert.True(WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800), Screen1));
        Assert.Equal(0, s.Get(Settings.WindowMonitorX));
    }

    [Fact]
    public void The_Maximized_choice_opens_maximized_even_when_the_window_was_left_small()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800), Screen1);
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Maximized);
        Assert.True(plan.Maximized);
        Assert.Equal(new WindowRect(300, 200, 1280, 800), plan.Rect);   // the size it returns to when restored is kept
    }

    [Fact]
    public void The_Last_choice_opens_a_small_window_as_it_was_left()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(300, 200, 1280, 800), Screen1);
        Assert.False(WindowMemory.Plan(s, Both, WindowStartMode.Last).Maximized);
    }

    [Fact]
    public void The_Maximized_choice_with_a_kept_place_that_is_off_every_screen_uses_the_primary_screen()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(9000, 9000, 800, 600));
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Maximized);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen1));
    }

    [Fact]
    public void A_window_kept_before_screens_were_recorded_still_opens_where_it_was_when_that_place_shows()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(2400, 300, 1280, 800));   // no screen recorded (an earlier version)
        WindowMemory.Record(s, true, new WindowRect(0, 0, 2560, 1440));
        var plan = WindowMemory.Plan(s, Both, WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen2));
    }

    [Fact]
    public void A_window_larger_than_the_screen_is_cut_to_the_screen()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(0, 0, 5000, 3000));
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.Equal(new WindowRect(0, 0, 1920, 1080), plan.Rect);
    }

    [Fact]
    public void A_window_whose_title_bar_is_off_the_top_of_the_screen_opens_maximized_on_the_primary_screen()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(100, -400, 800, 600));
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.True(plan.Maximized);
        Assert.True(OnScreen(plan, Screen1));
    }

    [Fact]
    public void A_window_partly_off_the_side_but_still_reachable_stays_where_it_was()
    {
        var s = Fresh();
        WindowMemory.Record(s, false, new WindowRect(1500, 100, 800, 600));   // 420 px of it on the screen
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.Equal(new WindowRect(1500, 100, 800, 600), plan.Rect);
    }

    [Fact]
    public void A_nonsense_kept_size_is_ignored()
    {
        var s = Fresh();
        s.Store(Settings.WindowMaximized, false);
        s.Store(Settings.WindowWidth, 3);
        s.Store(Settings.WindowHeight, 2);
        var plan = WindowMemory.Plan(s, [Screen1], WindowStartMode.Last);
        Assert.Equal(new WindowRect(10, 10, 1024, 738), plan.Rect);
    }

    [Fact]
    public void The_window_settings_are_not_among_the_preferences()
    {
        // the window's own memory is not a choice the person makes in Preferences, so it is not in the catalogue
        Assert.All(new[] { WindowMemory.MaximizedKey, WindowMemory.XKey, WindowMemory.YKey, WindowMemory.WidthKey, WindowMemory.HeightKey, WindowMemory.MonitorXKey, WindowMemory.MonitorYKey },
            key => Assert.Null(PreferenceCatalog.Find(key)));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The startup window choice: Maximized window (the default), Last size and position, or Full screen. The official viewer and Firestorm
/// open maximized on a first run and keep the window as it was left (WindowMaximized, WindowX, WindowY, WindowWidth, WindowHeight); neither
/// has a full-screen menu item or key (FullScreen is a debug setting there), so the toggle is this viewer's own, Ctrl+Alt+F, which no
/// menu shortcut or gesture key of either viewer uses.
/// </summary>
public class WindowStartupTests
{
    [Theory]
    [InlineData(null, WindowStartMode.Maximized)]
    [InlineData("maximized", WindowStartMode.Maximized)]
    [InlineData("last", WindowStartMode.Last)]
    [InlineData("fullscreen", WindowStartMode.Fullscreen)]
    [InlineData("windowed", WindowStartMode.Last)]         // the choice earlier versions kept: the window as it was left
    [InlineData("borderless", WindowStartMode.Fullscreen)]  // the engine's full screen is already borderless
    [InlineData("nonsense", WindowStartMode.Maximized)]
    public void Each_saved_choice_maps_to_its_start(string? saved, WindowStartMode expected)
    {
        Assert.Equal(expected, DisplayPlan.For(saved).Start);
    }

    [Theory]
    [InlineData("maximized", false, true)]
    [InlineData("last", false, true)]
    [InlineData("fullscreen", true, false)]
    public void Only_the_full_screen_choice_asks_for_full_screen_and_keeps_no_window(string saved, bool fullscreen, bool remembers)
    {
        var plan = DisplayPlan.For(saved);
        Assert.Equal(fullscreen, plan.Fullscreen);
        Assert.Equal(remembers, plan.Remembers);
    }


    [Theory]
    [InlineData("windowed", "last", true)]
    [InlineData("borderless", "fullscreen", true)]
    [InlineData("maximized", "maximized", false)]
    [InlineData("fullscreen", "fullscreen", false)]
    public void An_earlier_versions_value_is_rewritten_as_the_choice_it_stands_for(string saved, string now, bool wrote)
    {
        var s = new SettingsService(null);
        s.SetText(Settings.WindowModeKey, saved);
        Assert.Equal(wrote, DisplayPlan.Migrate(s));
        Assert.Equal(now, s.GetText(Settings.WindowModeKey));
    }

    [Fact]
    public void With_nothing_saved_the_migration_writes_nothing()
    {
        var s = new SettingsService(null);
        Assert.False(DisplayPlan.Migrate(s));
        Assert.Null(s.GetText(Settings.WindowModeKey));
    }
    [Fact]
    public void The_choice_is_a_graphics_preference_with_three_values_and_maximized_by_default()
    {
        var def = PreferenceCatalog.Find(Settings.WindowModeKey);
        Assert.NotNull(def);
        Assert.Equal(["maximized", "last", "fullscreen"], PreferenceCatalog.WindowModes);
        Assert.Equal("maximized", def!.Default);
        Assert.Equal(PreferenceCatalog.WindowModes, def.Choices);
    }

    [Fact]
    public void The_setting_and_every_choice_have_a_label_in_the_string_table()
    {
        var strings = StringTable.English();
        Assert.Equal("Window at startup", strings.Get("prefs.s.window_mode"));
        Assert.Equal("Maximized window", strings.Get("prefs.c.window_mode.maximized"));
        Assert.Equal("Last size and position", strings.Get("prefs.c.window_mode.last"));
        Assert.Equal("Full screen", strings.Get("prefs.c.window_mode.fullscreen"));
    }

    [Fact]
    public void Full_screen_and_Minimize_are_in_the_Me_menu_with_their_commands_state_and_shortcut()
    {
        bool state = false;
        var menus = new MenuModel();
        menus.AddMenu("me", "menu.me");
        WindowMenu.Install(menus, () => state);
        var entries = menus.Menus.Single(m => m.Id == "me").Entries;
        var full = entries.Single(x => x.Id == WindowMenu.FullscreenEntry);
        var minimize = entries.Single(x => x.Id == WindowMenu.MinimizeEntry);
        Assert.Equal(WindowMenu.FullscreenCommand, full.CommandId);
        Assert.Equal(WindowMenu.MinimizeCommand, minimize.CommandId);
        Assert.Equal("control|alt|F", full.Shortcut);
        Assert.False(full.IsChecked);
        state = true;
        Assert.True(full.IsChecked);
    }

    [Fact]
    public void The_full_screen_shortcut_is_used_by_nothing_else()
    {
        var chord = KeyChord.Parse(WindowMenu.FullscreenShortcut);
        Assert.NotNull(chord);
        Assert.DoesNotContain(ViewerShortcuts.ByEntry.Values, v => KeyChord.Parse(v) == chord);
        Assert.DoesNotContain(ViewerShortcuts.FromOtherViewers.Values, v => KeyChord.Parse(v.Shortcut) == chord);
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, v => KeyChord.Parse(v.Shortcut) == chord);
        Assert.NotEqual(KeyChord.Parse(ViewerShortcuts.CommandList), chord);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Host(string file) => File.ReadAllText(Path.Combine(FindRoot(), "host", "Viewer.Godot", file));

    [Fact]
    public void The_startup_choice_is_applied_only_to_a_normal_launch_and_before_the_first_frame()
    {
        string prefs = Host("Main.Prefs.cs");
        Assert.Contains("if (!_interactive || DisplayChosenOnCommandLine())", prefs);   // a window the command line placed is left alone
        Assert.Contains("ApplyStartupWindow(settings);", prefs);
        string main = Host("Main.cs");
        int prefsAt = main.IndexOf("StartPreferences(startSettings);", StringComparison.Ordinal);
        int testRun = main.IndexOf("AddChild(new TestRender());", StringComparison.Ordinal);
        Assert.True(testRun >= 0 && prefsAt > testRun, "the test and render launches return before the preferences are applied");
        string window = Host("Main.Window.cs");
        Assert.Contains("startup.window", window);
        Assert.Contains("Engine.GetFramesDrawn()", window);
    }

    [Fact]
    public void Full_screen_is_the_borderless_kind_and_never_the_exclusive_one()
    {
        string code = Host("Main.Prefs.cs") + Host("Main.Window.cs");
        Assert.DoesNotContain("Mode = Window.ModeEnum.ExclusiveFullscreen", code);
        Assert.DoesNotContain("Borderless =", code);
    }

    [Fact]
    public void The_commands_and_menu_items_are_installed_with_the_chrome()
    {
        string chrome = Host("Main.Chrome.cs");
        Assert.Contains("WindowMenu.Install(", chrome);
        Assert.Contains("WindowMenu.FullscreenCommand", chrome);
        Assert.Contains("WindowMenu.MinimizeCommand", chrome);
        Assert.Contains("Window.ModeEnum.Minimized", Host("Main.Window.cs"));
    }

    [Fact]
    public void Nothing_in_a_normal_run_takes_the_border_or_the_minimize_box_off_the_window()
    {
        string all = string.Concat(Directory.EnumerateFiles(Path.Combine(FindRoot(), "host", "Viewer.Godot"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains("TestRender") && !p.Contains("S1Smoke") && !p.Contains("TestWindow")).Select(File.ReadAllText));
        Assert.DoesNotContain(".Borderless = true", all);
        Assert.DoesNotContain("Unresizable = true", all);
        Assert.DoesNotContain("SkipTaskbar", all);
    }
}

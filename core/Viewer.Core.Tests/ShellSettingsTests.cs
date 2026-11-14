// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json.Nodes;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shell's foundations that chat needs: the typed settings service, the theme's contrast rule, and menus and toolbar
/// buttons that feature code adds after the frame is drawn. Plain .NET, no engine. Every member builds its own objects
/// (the file tests use their own unique temp folder), so the class runs in parallel.
/// </summary>
public class ShellSettingsTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    // ---------------- settings ----------------

    [Fact]
    public void With_no_file_every_setting_has_its_default()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.True(s.Get(Settings.ChatTimestamps));
        Assert.True(s.Get(Settings.ChatSendTyping));
        Assert.Equal(500, s.Get(Settings.ChatHistoryLines));
        Assert.Equal(15, s.Get(Settings.ChatFontSize));
    }

    [Fact]
    public void Stored_values_are_read_with_their_types()
    {
        var s = new SettingsService(new MemoryStore("""{ "chat_timestamps": false, "chat_history_lines": 1200 }"""));
        Assert.False(s.Get(Settings.ChatTimestamps));
        Assert.Equal(1200, s.Get(Settings.ChatHistoryLines));
        Assert.True(s.Get(Settings.ChatSendTyping));
    }

    [Theory]
    [InlineData("""{ "chat_timestamps": "no", "chat_history_lines": "many" }""")]
    [InlineData("""not json at all""")]
    [InlineData("""[1, 2, 3]""")]
    public void A_stored_value_of_the_wrong_type_or_an_unreadable_file_leaves_the_defaults(string text)
    {
        var s = new SettingsService(new MemoryStore(text));
        Assert.True(s.Get(Settings.ChatTimestamps));
        Assert.Equal(500, s.Get(Settings.ChatHistoryLines));
    }

    [Fact]
    public void A_stored_value_outside_its_range_is_brought_inside_it()
    {
        var s = new SettingsService(new MemoryStore("""{ "chat_history_lines": 999999, "chat_font_size": 1 }"""));
        Assert.Equal(5000, s.Get(Settings.ChatHistoryLines));
        Assert.Equal(10, s.Get(Settings.ChatFontSize));
    }

    [Fact]
    public void Setting_a_value_raises_one_change_and_writes_the_file_keeping_keys_it_does_not_know()
    {
        var store = new MemoryStore("""{ "merge_still_objects": false, "chat_timestamps": true }""");
        var s = new SettingsService(store);
        var changed = new List<string>();
        s.Changed += changed.Add;
        Assert.True(s.Set(Settings.ChatTimestamps, false));
        Assert.Equal(["chat_timestamps"], changed);
        var json = JsonNode.Parse(store.Text!)!.AsObject();
        Assert.False((bool)json["merge_still_objects"]!);
        Assert.False((bool)json["chat_timestamps"]!);
        Assert.False(s.Get(Settings.ChatTimestamps));
    }

    [Fact]
    public void Setting_the_value_it_already_has_changes_nothing_and_writes_nothing()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        var changed = new List<string>();
        s.Changed += changed.Add;
        Assert.True(s.Set(Settings.ChatHistoryLines, 500));
        Assert.Empty(changed);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void A_new_value_goes_through_its_rule()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        Assert.True(s.Set(Settings.ChatFontSize, 400));
        Assert.Equal(32, s.Get(Settings.ChatFontSize));
        Assert.Equal(32, (int)JsonNode.Parse(store.Text!)!["chat_font_size"]!);
    }

    [Fact]
    public void A_rule_that_refuses_a_value_leaves_the_setting_as_it_was()
    {
        var odd = new Setting<int>("odd_only", 1, v => v % 2 == 1 ? v : null);
        var store = new MemoryStore();
        var s = new SettingsService(store);
        var changed = new List<string>();
        s.Changed += changed.Add;
        Assert.False(s.Set(odd, 4));
        Assert.Equal(1, s.Get(odd));
        Assert.Empty(changed);
        Assert.True(s.Set(odd, 3));
        Assert.Equal(3, s.Get(odd));
    }

    [Fact]
    public void The_settings_file_round_trips_through_a_real_folder_and_the_merge_setting_still_reads_its_key()
    {
        string folder = TestTemp.NewFolder("settings");
        File.WriteAllText(Path.Combine(folder, SettingsFile.FileName), """{ "merge_still_objects": false }""");
        var s = new SettingsService(new SettingsFile(folder));
        Assert.True(s.Set(Settings.ChatFontSize, 18));
        var again = new SettingsService(new SettingsFile(folder));
        Assert.Equal(18, again.Get(Settings.ChatFontSize));
        Assert.False(Viewer.Core.Platform.MergeSetting.Resolve([], File.ReadAllText(Path.Combine(folder, SettingsFile.FileName))));
        Assert.False(File.Exists(Path.Combine(folder, SettingsFile.FileName + ".new")));
        TestTemp.Delete(folder);
    }

    // ---------------- theme ----------------

    [Fact]
    public void Contrast_follows_the_wcag_formula_at_its_ends()
    {
        var black = new UiColour(0, 0, 0);
        var white = new UiColour(1, 1, 1);
        Assert.Equal(21.0, UiColour.Contrast(black, white), 3);
        Assert.Equal(21.0, UiColour.Contrast(white, black), 3);
        Assert.Equal(1.0, UiColour.Contrast(white, white), 3);
        Assert.Equal("#ffffff", white.Hex);
        Assert.Equal("#000000", black.Hex);
        Assert.Equal("#ff8000", new UiColour(1, 0.5f, 0).Hex);
    }

    [Fact]
    public void Every_text_colour_of_the_theme_has_at_least_aa_contrast_on_its_panel_and_in_a_field()
    {
        var t = ThemeSpec.Default;
        Assert.True(t.TextColours.Count >= 8, "the theme lists its text colours");
        var panel = t.Panel with { A = 1 };
        foreach (var (name, colour) in t.TextColours)
        {
            Assert.True(UiColour.Contrast(colour, panel) >= 4.5, $"{name} on the panel: {UiColour.Contrast(colour, panel):0.00}");
            Assert.True(UiColour.Contrast(colour, t.Field) >= 4.5, $"{name} in a field: {UiColour.Contrast(colour, t.Field):0.00}");
        }
    }

    // ---------------- menus and toolbar after start-up ----------------

    [Fact]
    public void Adding_a_menu_or_an_entry_after_start_up_says_so()
    {
        var menus = new MenuModel();
        int changes = 0;
        menus.Changed += () => changes++;
        menus.AddMenu("communicate", "menu.communicate");
        menus.AddEntry("communicate", new MenuEntry("communicate.chat", "menu.communicate.chat", "floater.chat"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_refused_menu_addition_does_not_say_it_changed()
    {
        var menus = new MenuModel();
        menus.AddMenu("a", "menu.zeta");
        int changes = 0;
        menus.Changed += () => changes++;
        Assert.Throws<ArgumentException>(() => menus.AddMenu("a", "menu.zeta"));
        Assert.Throws<ArgumentException>(() => menus.AddEntry("missing", new MenuEntry("x", "menu.zeta", "c")));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Adding_a_toolbar_button_after_start_up_says_so()
    {
        var toolbar = new ToolbarModel();
        int changes = 0;
        toolbar.Changed += () => changes++;
        toolbar.Add(new ToolbarButton("chat", "toolbar.chat", "floater.chat"));
        Assert.Equal(1, changes);
    }
}

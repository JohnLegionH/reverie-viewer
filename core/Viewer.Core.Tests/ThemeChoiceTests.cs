// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json.Nodes;
using Viewer.Shell;
using Xunit;
using static Viewer.Core.Tests.ThemeTokenTests;

namespace Viewer.Core.Tests;

/// <summary>
/// What a person chooses on the Interface page of Preferences: a preset, an accent, changes to the panel, bar and text
/// colours, the UI scale and whether to follow the grid's accent. How a choice becomes tokens, how it is saved for an
/// account and read back, how a theme file is imported, and the readability list. Plain .NET, no engine; every member builds
/// its own objects, so the class runs in parallel.
/// </summary>
public class ThemeChoiceTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    // ---------------- resolving ----------------

    [Fact]
    public void The_default_choice_is_the_classic_preset_unchanged_at_a_hundred_percent()
    {
        var choice = ThemeChoice.Default;
        Assert.Equal("classic", choice.Preset);
        Assert.Equal(100, choice.ScalePercent);
        Assert.False(choice.UseGridAccent);
        Assert.Equal(ThemePresets.Get("classic"), ThemeResolver.Resolve(choice, null));
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("classic-light")]
    [InlineData("power-user")]
    [InlineData("high-contrast")]
    public void Each_preset_resolves_to_its_own_tokens(string id) =>
        Assert.Equal(ThemePresets.Get(id), ThemeResolver.Resolve(ThemeChoice.Default with { Preset = id }, null));

    [Fact]
    public void A_picked_accent_replaces_the_accent_and_the_fill_follows_the_rule()
    {
        var t = ThemeResolver.Resolve(ThemeChoice.Default with { Accent = H("#4c9aff") }, null);
        Assert.Equal(H("#2b6fd6"), t.AccentFill);   // the same accent as the preset: its own fill stays
        var teal = ThemeResolver.Resolve(ThemeChoice.Default with { Accent = H("#2dd4bf") }, null);
        Assert.Equal(H("#2dd4bf"), teal.Accent);
        Assert.Equal(H("#2dd4bf"), teal.AccentFill);          // light accent: its own fill, dark text
        Assert.Equal(H("#111111"), teal.AccentFillText);
        var rose = ThemeResolver.Resolve(ThemeChoice.Default with { Accent = H("#f472b6") }, null);
        Assert.Equal(H("#111111"), rose.AccentFillText);
        var violet = ThemeResolver.Resolve(ThemeChoice.Default with { Accent = H("#5a3fd0") }, null);
        Assert.Equal(H("#5a3fd0"), violet.AccentFill);
        Assert.Equal(H("#ffffff"), violet.AccentFillText);
    }

    [Fact]
    public void Changed_panel_bar_and_text_colours_replace_those_and_nothing_else()
    {
        var baseTokens = ThemePresets.Get("classic");
        var t = ThemeResolver.Resolve(ThemeChoice.Default with
        {
            Panel = H("#102030"), Bar = H("#203040"), Text = H("#f0f0f0"), TextSecondary = H("#c0c0c0"),
        }, null);
        Assert.Equal(H("#102030", baseTokens.Panel.A), t.Panel);      // the preset's translucency is kept
        Assert.Equal(H("#203040"), t.Bar);
        Assert.Equal(H("#f0f0f0"), t.Text);
        Assert.Equal(H("#c0c0c0"), t.TextSecondary);
        Assert.Equal(baseTokens with { Panel = t.Panel, Bar = t.Bar, Text = t.Text, TextSecondary = t.TextSecondary }, t);
    }

    [Fact]
    public void The_grid_accent_wins_when_asked_for_and_present_and_is_ignored_otherwise()
    {
        var grid = H("#2dd4bf");
        var asked = ThemeChoice.Default with { UseGridAccent = true, Accent = H("#f59e0b") };
        Assert.Equal(grid, ThemeResolver.Resolve(asked, grid).Accent);
        Assert.Equal(H("#f59e0b"), ThemeResolver.Resolve(asked, null).Accent);                                   // the grid has none
        Assert.Equal(H("#f59e0b"), ThemeResolver.Resolve(asked with { UseGridAccent = false }, grid).Accent);   // not asked for
        Assert.Equal(H("#4c9aff"), ThemeResolver.Resolve(ThemeChoice.Default with { UseGridAccent = true }, null).Accent);
    }

    [Fact]
    public void An_imported_theme_stands_in_for_the_preset_and_the_other_changes_still_apply()
    {
        var mine = ThemePresets.Get("power-user") with { Radius = 8 };
        var choice = ThemeChoice.Import(ThemePack.Write("mine", "Mine", mine), out var error);
        Assert.Null(error);
        Assert.NotNull(choice);
        Assert.Equal(mine, ThemeResolver.Resolve(choice, null));
        Assert.Equal(H("#abcdef"), ThemeResolver.Resolve(choice with { Text = H("#abcdef") }, null).Text);
    }

    // ---------------- import and export ----------------

    [Theory]
    [InlineData("classic")]
    [InlineData("classic-light")]
    [InlineData("power-user")]
    [InlineData("high-contrast")]
    public void A_theme_exported_and_imported_again_gives_the_same_look(string id)
    {
        var shown = ThemeResolver.Resolve(ThemeChoice.Default with { Preset = id, Accent = H("#c084fc"), Panel = H("#112233") }, null);
        var file = ThemePack.Write("exported", "Exported", shown);
        var choice = ThemeChoice.Import(file, out var error);
        Assert.Null(error);
        Assert.NotNull(choice);
        Assert.Equal(shown, ThemeResolver.Resolve(choice, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "format": 1, "colors": { "panel": "#111111" } }""")]
    [InlineData("""{ "format": 7 }""")]
    public void A_broken_theme_file_is_refused_with_a_message_and_gives_no_choice(string file)
    {
        var choice = ThemeChoice.Import(file, out var error);
        Assert.Null(choice);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---------------- the choice as text ----------------

    [Fact]
    public void A_choice_written_and_read_again_is_the_same_choice()
    {
        var pack = ThemePack.Write("mine", "Mine", ThemePresets.Get("classic-light"));
        var original = new ThemeChoice("imported", H("#2dd4bf"), H("#102030"), H("#203040"), H("#f0f0f0"), H("#c0c0c0"), 150, true, pack);
        var read = ThemeChoice.FromJson(original.ToJson());
        Assert.NotNull(read);
        Assert.Equal(original with { ImportedPack = null }, read with { ImportedPack = null });
        Assert.Equal(pack, read.ImportedPack);
        Assert.Equal(ThemeChoice.Default, ThemeChoice.FromJson(ThemeChoice.Default.ToJson()));
    }

    [Theory]
    [InlineData("""{ "preset": "classic", "scale": 50 }""", 75)]
    [InlineData("""{ "preset": "classic", "scale": 900 }""", 400)]
    [InlineData("""{ "preset": "classic", "scale": 300 }""", 300)]
    [InlineData("""{ "preset": "classic" }""", 100)]
    public void The_scale_is_kept_between_the_official_viewers_075_and_4_times(string json, int expected) =>
        Assert.Equal(expected, ThemeChoice.FromJson(json)?.ScalePercent);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{ "preset": "no-such-preset" }""")]
    [InlineData("""{ "preset": "classic", "accent": "banana" }""")]
    [InlineData("""{ "preset": "imported" }""")]
    public void A_stored_choice_that_cannot_be_read_gives_nothing(string json) => Assert.Null(ThemeChoice.FromJson(json));

    // ---------------- saving per account ----------------

    [Fact]
    public void Each_account_keeps_its_own_theme_and_the_file_keeps_other_settings()
    {
        var store = new MemoryStore("""{ "chat_timestamps": false }""");
        var s = new SettingsService(store);
        ThemeStore.Save(s, "alice", ThemeChoice.Default with { Preset = "high-contrast" });
        ThemeStore.Save(s, "bob", ThemeChoice.Default with { Preset = "classic-light", ScalePercent = 125 });
        var again = new SettingsService(store);   // read back from the stored text
        Assert.Equal("high-contrast", ThemeStore.Load(again, "alice").Preset);
        Assert.Equal("classic-light", ThemeStore.Load(again, "bob").Preset);
        Assert.Equal(125, ThemeStore.Load(again, "bob").ScalePercent);
        Assert.False(again.Get(Settings.ChatTimestamps));
    }

    [Fact]
    public void The_login_screen_and_an_account_that_never_saved_get_the_theme_saved_last()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Equal(ThemeChoice.Default, ThemeStore.Load(s, null));
        ThemeStore.Save(s, "alice", ThemeChoice.Default with { Preset = "power-user" });
        Assert.Equal("power-user", ThemeStore.Load(s, null).Preset);
        Assert.Equal("power-user", ThemeStore.Load(s, "carol").Preset);
        ThemeStore.Save(s, "bob", ThemeChoice.Default with { Preset = "classic-light" });
        Assert.Equal("power-user", ThemeStore.Load(s, "alice").Preset);   // her own is still hers
        Assert.Equal("classic-light", ThemeStore.Load(s, null).Preset);
    }

    [Fact]
    public void Account_names_are_compared_without_regard_to_case_and_odd_characters_do_not_break_the_file()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        ThemeStore.Save(s, "Alice Resident", ThemeChoice.Default with { Preset = "power-user" });
        ThemeStore.Save(s, "we\"ird\\name\n", ThemeChoice.Default with { Preset = "classic-light" });
        Assert.Equal("power-user", ThemeStore.Load(s, "alice resident").Preset);
        Assert.NotNull(JsonNode.Parse(store.Text!));
        Assert.Equal("classic-light", ThemeStore.Load(new SettingsService(store), "we\"ird\\name\n").Preset);
    }

    [Fact]
    public void A_stored_theme_that_cannot_be_read_leaves_the_default()
    {
        var s = new SettingsService(new MemoryStore("""{ "ui_theme_alice": "this is not a theme", "ui_theme_last": 5 }"""));
        Assert.Equal(ThemeChoice.Default, ThemeStore.Load(s, "alice"));
    }

    [Fact]
    public void Text_settings_are_stored_read_and_announced_like_the_typed_ones()
    {
        var store = new MemoryStore("""{ "other": 1 }""");
        var s = new SettingsService(store);
        var changed = new List<string>();
        s.Changed += changed.Add;
        Assert.Null(s.GetText("note"));
        Assert.True(s.SetText("note", "hello"));
        Assert.Equal("hello", s.GetText("note"));
        Assert.Equal(["note"], changed);
        Assert.True(s.SetText("note", "hello"));   // the same text: nothing more
        Assert.Equal(["note"], changed);
        Assert.Equal("hello", new SettingsService(store).GetText("note"));
        Assert.Equal(1, (int)JsonNode.Parse(store.Text!)!["other"]!);
    }

    // ---------------- readability ----------------

    [Fact]
    public void The_readability_list_has_four_rows_with_the_ratios_of_the_drawn_pairs()
    {
        var t = ThemePresets.Get("classic");
        var rows = Readability.Check(t);
        Assert.Equal(["text-on-panel", "secondary-on-panel", "accent-on-bar", "button-text"], rows.Select(r => r.Key));
        Assert.Equal(UiColour.Contrast(t.Text, t.Panel.Opaque()), rows[0].Ratio, 3);
        Assert.Equal(UiColour.Contrast(t.TextSecondary, t.Panel.Opaque()), rows[1].Ratio, 3);
        Assert.Equal(UiColour.Contrast(t.Accent, t.Bar), rows[2].Ratio, 3);
        Assert.Equal(UiColour.Contrast(t.AccentFillText, t.AccentFill), rows[3].Ratio, 3);
        Assert.All(rows, r => Assert.True(r.Readable));
    }

    [Fact]
    public void A_row_is_too_low_under_four_and_a_half_to_one_and_readable_at_it()
    {
        var t = ThemePresets.Get("classic") with { Text = H("#2a2d33"), TextSecondary = H("#aeb4bc") };
        var rows = Readability.Check(t);
        Assert.False(rows[0].Readable);   // text close to the panel
        Assert.True(rows[1].Readable);
        Assert.True(rows[0].Ratio < 4.5);
        var edge = ThemePresets.Get("classic") with { Panel = H("#ffffff"), Text = H("#767676") };   // #767676 on white is 4.54:1
        Assert.True(Readability.Check(edge)[0].Readable);
        var under = ThemePresets.Get("classic") with { Panel = H("#ffffff"), Text = H("#777777") };   // 4.48:1
        Assert.False(Readability.Check(under)[0].Readable);
    }

    [Fact]
    public void The_grid_accent_and_a_picked_accent_are_judged_on_the_bar_and_on_the_button()
    {
        var dim = ThemeResolver.Resolve(ThemeChoice.Default with { Accent = H("#3a3f46") }, null);   // nearly the bar's own colour
        var rows = Readability.Check(dim);
        Assert.False(rows[2].Readable);
        Assert.True(rows[3].Readable);   // the fill rule keeps button text readable whatever the accent
    }
}

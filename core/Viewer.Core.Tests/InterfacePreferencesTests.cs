// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;
using static Viewer.Core.Tests.ThemeTokenTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The Interface page of Preferences, without the engine: every change restyles at once, Save stores the theme for the
/// account, Cancel restores what was saved, a "too low" readability row asks before saving and never blocks, and a broken
/// theme file is refused. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class InterfacePreferencesTests
{
    // the Classic preset, which these tests are about (the default choice is the Reverie dark variant)
    private static readonly ThemeChoice Classic = ThemeChoice.Default with { Preset = ThemePresets.Classic };

    private sealed class MemoryStore(string? text = null, bool fail = false) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { if (fail) return false; Text = text; return true; }
    }

    private static InterfacePreferences Page(ThemeChoice? saved = null, UiColour? grid = null) => new(saved ?? Classic, grid);

    [Fact]
    public void A_new_page_shows_the_saved_choice_unchanged()
    {
        var p = Page();
        Assert.False(p.IsChanged);
        Assert.Equal(ThemePresets.Get("classic"), p.Tokens);
        Assert.Equal(4, p.Readability.Count);
    }

    [Fact]
    public void Every_change_raises_changed_once_and_is_seen_in_the_tokens_at_once()
    {
        var p = Page();
        int raised = 0;
        p.Changed += () => raised++;
        p.SelectPreset("power-user");
        Assert.Equal(1, raised);
        Assert.Equal(ThemePresets.Get("power-user"), p.Tokens);
        p.SetAccent(H("#2dd4bf"));
        Assert.Equal(2, raised);
        Assert.Equal(H("#2dd4bf"), p.Tokens.Accent);
        p.SetPanel(H("#102030"));
        p.SetBar(H("#203040"));
        p.SetText(H("#f0f0f0"));
        p.SetTextSecondary(H("#c0c0c0"));
        Assert.Equal(6, raised);
        Assert.Equal((H("#203040"), H("#f0f0f0"), H("#c0c0c0")), (p.Tokens.Bar, p.Tokens.Text, p.Tokens.TextSecondary));
        p.SetScale(150);
        Assert.Equal(7, raised);
        Assert.Equal(150, p.Working.ScalePercent);
        Assert.True(p.IsChanged);
    }

    [Fact]
    public void Setting_what_is_already_set_raises_nothing()
    {
        var p = Page();
        int raised = 0;
        p.Changed += () => raised++;
        p.SelectPreset("classic");
        p.SetScale(100);
        p.SetAccent(null);
        p.SetUseGridAccent(false);
        Assert.Equal(0, raised);
        Assert.False(p.IsChanged);
    }

    [Fact]
    public void The_scale_is_kept_between_the_official_viewers_075_and_4_times()
    {
        var p = Page();
        p.SetScale(40);   // the official UIScaleFactor slider runs 0.75 to 4.0 (panel_preferences_advanced.xml)
        Assert.Equal(75, p.Working.ScalePercent);
        p.SetScale(900);
        Assert.Equal(400, p.Working.ScalePercent);
        p.SetScale(300);
        Assert.Equal(300, p.Working.ScalePercent);
    }

    [Fact]
    public void Picking_another_preset_drops_the_colour_changes_but_keeps_the_scale_and_the_grid_choice()
    {
        var p = Page();
        p.SetAccent(H("#f472b6"));
        p.SetPanel(H("#102030"));
        p.SetScale(125);
        p.SetUseGridAccent(true);
        p.SelectPreset("classic-light");
        Assert.Equal(Classic with { Preset = "classic-light", ScalePercent = 125, UseGridAccent = true }, p.Working);
    }

    [Fact]
    public void Reset_to_preset_drops_colour_changes_and_the_grid_accent_and_keeps_the_preset_and_scale()
    {
        var p = Page(Classic with { Preset = "power-user", ScalePercent = 175 });
        p.SetAccent(H("#2dd4bf"));
        p.SetText(H("#ffffff"));
        p.SetUseGridAccent(true);
        p.ResetToPreset();
        Assert.Equal(Classic with { Preset = "power-user", ScalePercent = 175 }, p.Working);
    }

    [Fact]
    public void The_grid_accent_is_used_only_when_asked_for_and_only_if_the_grid_has_one()
    {
        var p = Page(grid: H("#2dd4bf"));
        Assert.Equal(H("#4c9aff"), p.Tokens.Accent);
        p.SetUseGridAccent(true);
        Assert.Equal(H("#2dd4bf"), p.Tokens.Accent);
        var none = Page();
        none.SetUseGridAccent(true);
        Assert.Equal(H("#4c9aff"), none.Tokens.Accent);
    }

    // ---------------- save and cancel ----------------

    [Fact]
    public void Save_stores_the_theme_for_the_account_and_makes_it_the_saved_one()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var p = Page();
        p.SelectPreset("high-contrast");
        p.SetScale(130);
        Assert.Equal(SaveOutcome.Saved, p.Save(settings, "alice", confirmed: false));
        Assert.False(p.IsChanged);
        Assert.Equal(p.Working, p.Saved);
        var again = ThemeStore.Load(new SettingsService(store), "alice");
        Assert.Equal("high-contrast", again.Preset);
        Assert.Equal(130, again.ScalePercent);
        Assert.Equal(ReverieTheme.DarkId, ThemeStore.Load(new SettingsService(new MemoryStore()), "bob").Preset);
    }

    [Fact]
    public void Cancel_drops_every_unsaved_change_and_restores_the_saved_look()
    {
        var p = Page(Classic with { Preset = "power-user" });
        int raised = 0;
        p.SetAccent(H("#f472b6"));
        p.SelectPreset("classic-light");
        p.SetScale(180);
        p.Changed += () => raised++;
        p.Cancel();
        Assert.Equal(1, raised);
        Assert.Equal(Classic with { Preset = "power-user" }, p.Working);
        Assert.Equal(ThemePresets.Get("power-user"), p.Tokens);
        Assert.False(p.IsChanged);
        p.Cancel();
        Assert.Equal(1, raised);   // nothing left to drop
    }

    [Fact]
    public void Saving_with_a_too_low_row_asks_first_and_stores_nothing_until_confirmed()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var p = Page();
        p.SetText(H("#2a2d33"));   // text almost the panel's own colour
        Assert.Contains(p.Readability, r => !r.Readable);
        Assert.Equal(SaveOutcome.NeedsConfirmation, p.Save(settings, "alice", confirmed: false));
        Assert.Null(store.Text);
        Assert.True(p.IsChanged);
        Assert.Equal(SaveOutcome.Saved, p.Save(settings, "alice", confirmed: true));   // it never blocks
        Assert.Equal(H("#2a2d33"), ThemeStore.Load(new SettingsService(store), "alice") is { } c ? c.Text : null);
    }

    [Fact]
    public void A_readable_theme_saves_without_asking()
    {
        var p = Page();
        p.SelectPreset("power-user");
        Assert.All(p.Readability, r => Assert.True(r.Readable));
        Assert.Equal(SaveOutcome.Saved, p.Save(new SettingsService(new MemoryStore()), "alice", confirmed: false));
    }

    [Fact]
    public void A_settings_file_that_cannot_be_written_is_reported_and_leaves_the_edit_unsaved()
    {
        var p = Page();
        p.SelectPreset("power-user");
        Assert.Equal(SaveOutcome.Failed, p.Save(new SettingsService(new MemoryStore(fail: true)), "alice", confirmed: false));
        Assert.True(p.IsChanged);
    }

    // ---------------- import and export ----------------

    [Fact]
    public void An_exported_theme_imports_into_another_page_as_the_same_look()
    {
        var a = Page();
        a.SelectPreset("classic-light");
        a.SetAccent(H("#c084fc"));
        string file = a.Export("My theme");
        var b = Page();
        Assert.True(b.Import(file, out var error));
        Assert.Null(error);
        Assert.Equal(a.Tokens, b.Tokens);
        Assert.True(b.IsChanged);
    }

    [Fact]
    public void An_import_keeps_the_scale_of_the_page()
    {
        var a = Page();
        a.SelectPreset("power-user");
        string file = a.Export("x");
        var b = Page();
        b.SetScale(160);
        Assert.True(b.Import(file, out _));
        Assert.Equal(160, b.Working.ScalePercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a theme")]
    [InlineData("""{ "format": 1, "colors": {} }""")]
    public void A_broken_theme_file_is_refused_with_a_message_and_nothing_changes(string file)
    {
        var p = Page();
        p.SelectPreset("power-user");
        var before = p.Working;
        int raised = 0;
        p.Changed += () => raised++;
        Assert.False(p.Import(file, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(before, p.Working);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void The_six_accent_swatches_are_offered_in_order_and_each_one_gives_readable_button_text()
    {
        Assert.Equal(["blue", "teal", "green", "amber", "rose", "violet"], ThemeSwatches.Accents.Select(a => a.Key));
        Assert.Equal(H("#4c9aff"), ThemeSwatches.Accents[0].Colour);
        foreach (var (key, colour) in ThemeSwatches.Accents)
        {
            var p = Page();
            p.SetAccent(colour);
            Assert.Equal(colour, p.Tokens.Accent);
            Assert.True(p.Readability[3].Readable, $"{key}: button text {p.Readability[3].Ratio:0.00}");
        }
    }
}

public class ThemeCardTests
{
    [Fact]
    public void The_default_look_and_Reverie_light_each_have_a_card_with_a_name()
    {
        Assert.Contains(ThemeChoice.Default.Preset, ThemePresets.CardIds);
        Assert.Contains(ReverieTheme.LightId, ThemePresets.CardIds);
        Assert.Equal(ThemePresets.Ids.Count + 2, ThemePresets.CardIds.Count);
        Assert.Equal(ThemePresets.CardIds.Count, ThemePresets.CardIds.Distinct().Count());
        Assert.Equal("Reverie dark", ThemePresets.NameOf(ReverieTheme.DarkId));
        Assert.Equal("Reverie light", ThemePresets.NameOf(ReverieTheme.LightId));
        Assert.All(ThemePresets.CardIds, id => Assert.True(ThemePresets.Known(id)));
    }

    [Fact]
    public void Choosing_a_Reverie_card_takes_that_look()
    {
        var page = new InterfacePreferences(ThemeChoice.Default, null);
        page.SelectPreset(ReverieTheme.LightId);
        Assert.Equal(ReverieTheme.LightId, page.Working.Preset);
        Assert.Equal(ThemePresets.Get(ReverieTheme.LightId), page.Tokens);
        page.SelectPreset(ReverieTheme.DarkId);
        Assert.Equal(ThemePresets.Get(ReverieTheme.DarkId), page.Tokens);
        Assert.False(page.IsChanged);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Window opacity as the official viewer has it: one value for the focused window (1.0) and one for the others (0.95), under the
/// official names, edited as a preview on the Interface page. Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class FloaterOpacityTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    [Fact]
    public void The_two_settings_have_the_official_names_and_defaults()
    {
        Assert.Equal("ActiveFloaterTransparency", Settings.ActiveFloaterTransparency.Key);
        Assert.Equal("InactiveFloaterTransparency", Settings.InactiveFloaterTransparency.Key);
        Assert.Equal(1.0f, Settings.ActiveFloaterTransparency.Default);
        Assert.Equal(0.95f, Settings.InactiveFloaterTransparency.Default);
        Assert.Equal(1.0f, FloaterOpacity.DefaultActive);
        Assert.Equal(0.95f, FloaterOpacity.DefaultInactive);
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0.4f, 0.4f)]
    [InlineData(1.5f, 1f)]
    public void A_value_is_kept_between_zero_and_one(float given, float kept)
    {
        Assert.Equal(kept, Settings.ActiveFloaterTransparency.Fix(given));
        Assert.Equal(kept, Settings.InactiveFloaterTransparency.Fix(given));
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_refused()
    {
        Assert.Null(Settings.ActiveFloaterTransparency.Fix(float.NaN));
        Assert.Null(Settings.InactiveFloaterTransparency.Fix(float.PositiveInfinity));
    }

    [Fact]
    public void The_focused_window_uses_the_active_value_and_the_others_the_inactive_one()
    {
        Assert.Equal(1.0f, FloaterOpacity.For(focused: true, active: 1.0f, inactive: 0.95f, themePanelAlpha: 0.9f));
        Assert.Equal(0.95f, FloaterOpacity.For(focused: false, active: 1.0f, inactive: 0.95f, themePanelAlpha: 0.9f));
        Assert.Equal(0.6f, FloaterOpacity.For(focused: true, active: 0.6f, inactive: 0.3f, themePanelAlpha: 0.9f));
        Assert.Equal(0.3f, FloaterOpacity.For(focused: false, active: 0.6f, inactive: 0.3f, themePanelAlpha: 0.9f));
    }

    [Fact]
    public void A_theme_with_a_solid_panel_keeps_every_window_solid()
    {
        Assert.Equal(1f, FloaterOpacity.For(focused: false, active: 0.2f, inactive: 0.2f, themePanelAlpha: 1f));
        Assert.Equal(1f, FloaterOpacity.For(focused: true, active: 0.2f, inactive: 0.2f, themePanelAlpha: 1f));
    }

    [Fact]
    public void The_shipped_themes_give_a_solid_focused_window_by_default_and_high_contrast_is_always_solid()
    {
        foreach (var id in ThemePresets.Ids)
        {
            float alpha = ThemePresets.Get(id).Panel.A;
            Assert.Equal(1f, FloaterOpacity.For(true, Settings.ActiveFloaterTransparency.Default, Settings.InactiveFloaterTransparency.Default, alpha));
        }
        Assert.Equal(1f, FloaterOpacity.For(false, 1f, 0.5f, ThemePresets.Get(ThemePresets.HighContrast).Panel.A));
    }

    [Fact]
    public void The_page_shows_the_defaults_and_follows_the_stored_values()
    {
        var page = new FloaterOpacityPrefs(new SettingsService(new MemoryStore()));
        Assert.Equal(1.0f, page.Active);
        Assert.Equal(0.95f, page.Inactive);
        var stored = new FloaterOpacityPrefs(new SettingsService(new MemoryStore("{\"ActiveFloaterTransparency\":0.7,\"InactiveFloaterTransparency\":0.2}")));
        Assert.Equal(0.7f, stored.Active);
        Assert.Equal(0.2f, stored.Inactive);
    }

    [Fact]
    public void An_edit_is_seen_at_once_and_nothing_is_written_until_Save()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var changed = new List<string>();
        settings.Changed += changed.Add;
        var page = new FloaterOpacityPrefs(settings);

        Assert.True(page.SetActive(0.8f));
        Assert.True(page.SetInactive(0.5f));

        Assert.Equal(0.8f, settings.Get(Settings.ActiveFloaterTransparency));
        Assert.Equal(0.5f, settings.Get(Settings.InactiveFloaterTransparency));
        Assert.Contains("ActiveFloaterTransparency", changed);
        Assert.Contains("InactiveFloaterTransparency", changed);
        Assert.True(page.IsChanged);
        Assert.Null(store.Text);
    }

    [Fact]
    public void Save_writes_both_values_and_ends_the_preview()
    {
        var store = new MemoryStore();
        var page = new FloaterOpacityPrefs(new SettingsService(store));
        page.SetActive(0.8f);
        page.SetInactive(0.5f);

        Assert.True(page.Save());

        Assert.False(page.IsChanged);
        Assert.NotNull(store.Text);
        var reread = new FloaterOpacityPrefs(new SettingsService(new MemoryStore(store.Text)));
        Assert.Equal(0.8f, reread.Active);
        Assert.Equal(0.5f, reread.Inactive);
    }

    [Fact]
    public void Cancel_puts_the_saved_values_back()
    {
        var settings = new SettingsService(new MemoryStore("{\"ActiveFloaterTransparency\":0.9}"));
        var page = new FloaterOpacityPrefs(settings);
        page.SetActive(0.3f);
        page.SetInactive(0.1f);

        page.Cancel();

        Assert.Equal(0.9f, page.Active);
        Assert.Equal(0.95f, page.Inactive);
        Assert.False(page.IsChanged);
    }

    [Fact]
    public void Reset_previews_the_official_defaults_and_can_be_cancelled()
    {
        var settings = new SettingsService(new MemoryStore("{\"ActiveFloaterTransparency\":0.4,\"InactiveFloaterTransparency\":0.2}"));
        var page = new FloaterOpacityPrefs(settings);

        page.Reset();

        Assert.Equal(1.0f, page.Active);
        Assert.Equal(0.95f, page.Inactive);
        Assert.True(page.IsChanged);
        page.Cancel();
        Assert.Equal(0.4f, page.Active);
        Assert.Equal(0.2f, page.Inactive);
    }

    [Fact]
    public void The_keys_the_page_edits_are_the_two_settings()
    {
        var page = new FloaterOpacityPrefs(new SettingsService(new MemoryStore()));
        Assert.Equal(["ActiveFloaterTransparency", "InactiveFloaterTransparency"], page.Keys);
    }
}

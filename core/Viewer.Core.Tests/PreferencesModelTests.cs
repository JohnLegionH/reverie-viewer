// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The graphics presets and the ordinary Preferences pages' logic: an edit is a preview everything sees at once, Save writes the page,
/// Cancel puts the saved values back, Reset to defaults previews the page's defaults, and a preset sets its values until one of them
/// is edited. Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class PreferencesModelTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    private static PrefDef Def(string key) => PreferenceCatalog.Find(key) ?? throw new Xunit.Sdk.XunitException("no setting " + key);

    private static (PreferencesModel Model, SettingsService Settings, MemoryStore Store) Make(string? file = null, string? account = "acct")
    {
        var store = new MemoryStore(file);
        var settings = new SettingsService(store);
        return (new PreferencesModel(settings, account), settings, store);
    }

    // ---------------- the presets ----------------

    [Theory]
    [InlineData("low", 64, 512)]
    [InlineData("lowmid", 96, 1024)]
    [InlineData("mid", 128, 2048)]
    [InlineData("ultra", 256, 2048)]
    public void Values_that_equal_a_level_are_matched_to_the_first_such_level(string id, int draw, int texture)
    {
        Assert.Equal(id, GraphicsPresets.Match(draw, texture));
    }

    [Fact]
    public void Custom_and_unknown_ids_have_no_values_and_other_combinations_are_custom()
    {
        Assert.Null(GraphicsPresets.Values("custom"));
        Assert.Null(GraphicsPresets.Values("extreme"));
        Assert.Equal("custom", GraphicsPresets.Match(300, 2048));
        Assert.Equal("custom", GraphicsPresets.Match(1024, 2048));
        Assert.Equal(1.75f, GraphicsPresets.VolumeLodFactor("custom"));
    }

    [Fact]
    public void The_defaults_of_the_graphics_settings_are_the_HighUltra_level()
    {
        // without a graphics-card class rule the default level is High: 128 m, the 2,048 texture rule
        var high = GraphicsPresets.Values("highultra")!.Value;
        Assert.Equal(high.DrawDistance, Def("draw_distance").Default);
        Assert.Equal(high.MaxTextureResolution, Def("max_texture_resolution").Default);
        Assert.Equal(GraphicsPresets.HighUltra, Def("graphics_preset").Default);
    }

    [Fact]
    public void Presets_get_stricter_from_low_to_ultra()
    {
        var found = GraphicsPresets.Levels.Select(i => GraphicsPresets.Values(i)).ToList();
        Assert.All(found, v => Assert.NotNull(v));
        var order = found.Select(v => v.GetValueOrDefault()).ToList();
        for (int i = 1; i < order.Count; i++)
        {
            Assert.True(order[i].DrawDistance >= order[i - 1].DrawDistance);
            Assert.True(order[i].VolumeLodFactor >= order[i - 1].VolumeLodFactor);
            Assert.True(order[i].MaxTextureResolution >= order[i - 1].MaxTextureResolution);
        }
    }

    [Fact]
    public void Choosing_a_preset_previews_its_values_and_the_page_shows_that_preset()
    {
        var (m, s, store) = Make();
        Assert.True(m.SelectPreset("low"));
        Assert.Equal(64, s.Get(Settings.DrawDistance));
        Assert.Equal(512, s.Get(Settings.MaxTextureResolution));
        Assert.Equal("low", m.Preset);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Choosing_a_preset_leaves_the_frame_limit_vsync_backend_and_window_mode_alone()
    {
        var (m, s, _) = Make("""{ "fps_limit": 60, "vsync": false, "graphics_backend": "d3d12", "window_mode": "fullscreen" }""");
        Assert.True(m.SelectPreset("low"));
        Assert.Equal(64, s.Get(Settings.DrawDistance));
        Assert.Equal(60, s.Get(Settings.FpsLimit));
        Assert.False(s.Get(Settings.Vsync));
        Assert.Equal("d3d12", s.GetText("graphics_backend"));
        Assert.Equal("fullscreen", s.GetText("window_mode"));
    }

    [Fact]
    public void Editing_one_value_of_a_preset_makes_the_preset_custom()
    {
        var (m, s, _) = Make();
        m.SelectPreset("high");
        Assert.Equal("high", m.Preset);

        Assert.True(m.Edit(Def("draw_distance"), 300));

        Assert.Equal("custom", m.Preset);
        Assert.Equal("custom", s.GetText("graphics_preset"));
        Assert.Equal(300, s.Get(Settings.DrawDistance));
    }

    [Theory]
    [InlineData("max_texture_resolution", 512)]
    public void Editing_either_quality_value_makes_the_preset_custom(string key, object value)
    {
        var (m, _, _) = Make();
        m.SelectPreset("ultra");
        Assert.True(m.Edit(Def(key), value));
        Assert.Equal("custom", m.Preset);
    }

    [Fact]
    public void Editing_the_values_back_to_a_preset_shows_that_preset_again()
    {
        var (m, _, _) = Make();
        m.SelectPreset("mid");
        m.Edit(Def("draw_distance"), 300);
        Assert.Equal("custom", m.Preset);
        m.Edit(Def("draw_distance"), 128);
        Assert.Equal("mid", m.Preset);
    }

    [Fact]
    public void Editing_something_that_is_not_a_quality_value_keeps_the_preset()
    {
        var (m, _, _) = Make();
        m.SelectPreset("mid");
        m.Edit(Def("fps_limit"), 60);
        m.Edit(Def("vsync"), false);
        m.Edit(Def("window_mode"), "fullscreen");
        Assert.Equal("mid", m.Preset);
    }

    [Fact]
    public void Custom_cannot_be_chosen_as_a_preset_and_an_unknown_preset_changes_nothing()
    {
        var (m, s, _) = Make();
        Assert.True(m.SelectPreset("mid"));
        var before = s.PendingKeys.OrderBy(k => k).ToList();
        Assert.NotEmpty(before);
        Assert.False(m.SelectPreset("custom"));
        Assert.False(m.SelectPreset("extreme"));
        Assert.Equal(before, s.PendingKeys.OrderBy(k => k).ToList());
        Assert.Equal("mid", m.Preset);
    }

    [Fact]
    public void The_preset_shown_follows_the_values_even_when_a_stored_preset_name_is_stale()
    {
        var (m, _, _) = Make("""{ "graphics_preset": "low", "draw_distance": 300 }""");
        Assert.Equal("custom", m.Preset);
    }

    // ---------------- Save, Cancel, Reset ----------------

    [Fact]
    public void An_edit_is_a_preview_until_the_page_is_saved()
    {
        var (m, s, store) = Make();
        var heard = new List<string>();
        s.Changed += heard.Add;

        Assert.True(m.Edit(Def("chat_font_size"), 22));

        Assert.Equal(22, m.Value(Def("chat_font_size")));
        Assert.Equal(22, s.Get(Settings.ChatFontSize));
        Assert.Contains("chat_font_size", heard);
        Assert.Equal(0, store.Writes);
        Assert.True(m.IsChanged(PrefPage.Chat));
        Assert.False(m.IsChanged(PrefPage.Graphics));
    }

    [Fact]
    public void Save_writes_the_pages_values_and_only_that_pages()
    {
        var (m, s, store) = Make();
        m.Edit(Def("chat_font_size"), 22);
        m.Edit(Def("vsync"), false);

        Assert.True(m.Save(PrefPage.Chat));

        var reread = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal(22, reread.Get(Settings.ChatFontSize));
        Assert.True(reread.Get(Settings.Vsync));
        Assert.False(m.IsChanged(PrefPage.Chat));
        Assert.True(m.IsChanged(PrefPage.Graphics));
        Assert.Contains("vsync", s.PendingKeys);
    }

    [Fact]
    public void Cancel_puts_the_saved_values_back_and_announces_them()
    {
        var (m, s, store) = Make("""{ "chat_font_size": 18 }""");
        m.Edit(Def("chat_font_size"), 30);
        var heard = new List<string>();
        s.Changed += heard.Add;

        m.Cancel(PrefPage.Chat);

        Assert.Equal(18, s.Get(Settings.ChatFontSize));
        Assert.Contains("chat_font_size", heard);
        Assert.False(m.IsChanged(PrefPage.Chat));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Cancel_leaves_the_other_pages_edits_alone()
    {
        var (m, s, _) = Make();
        m.Edit(Def("chat_font_size"), 30);
        m.Edit(Def("vsync"), false);
        m.Cancel(PrefPage.Chat);
        Assert.False(s.Get(Settings.Vsync));
        Assert.True(m.IsChanged(PrefPage.Graphics));
    }

    [Fact]
    public void Reset_previews_every_default_of_the_page_and_touches_no_other_page()
    {
        var (m, s, store) = Make("""{ "chat_font_size": 18, "chat_overlay_seconds": 40, "chat_timestamps": false, "vsync": false }""");

        m.Reset(PrefPage.Chat);

        Assert.Equal(15, s.Get(Settings.ChatFontSize));
        Assert.Equal(23, s.Get(Settings.ChatOverlaySeconds));
        Assert.True(s.Get(Settings.ChatTimestamps));
        Assert.False(s.Get(Settings.Vsync));
        Assert.True(m.IsChanged(PrefPage.Chat));
        Assert.Equal(0, store.Writes);

        m.Save(PrefPage.Chat);
        Assert.Equal(15, new SettingsService(new MemoryStore(store.Text)).Get(Settings.ChatFontSize));
    }

    [Fact]
    public void Resetting_the_graphics_page_brings_back_the_defaults()
    {
        var (m, _, _) = Make("""{ "draw_distance": 300, "clouds_enabled": false, "graphics_preset": "custom" }""");
        Assert.Equal("custom", m.Preset);
        m.Reset(PrefPage.Graphics);
        Assert.Equal(128, Convert.ToInt32(m.Value(Def("draw_distance"))));
        Assert.Equal("highultra", m.Preset);
    }

    [Fact]
    public void A_page_that_only_takes_effect_after_a_restart_says_so_while_it_has_such_a_change()
    {
        var (m, _, _) = Make();
        Assert.False(m.NeedsRestart(PrefPage.Graphics));
        m.Edit(Def("graphics_backend"), "d3d12");
        Assert.True(m.NeedsRestart(PrefPage.Graphics));
        Assert.False(m.NeedsRestart(PrefPage.Chat));
        m.Edit(Def("vsync"), false);
        m.Cancel(PrefPage.Graphics);
        Assert.False(m.NeedsRestart(PrefPage.Graphics));
    }

    [Fact]
    public void A_live_edit_alone_does_not_ask_for_a_restart()
    {
        var (m, _, _) = Make();
        m.Edit(Def("draw_distance"), 300);
        m.Edit(Def("clouds_enabled"), false);
        Assert.False(m.NeedsRestart(PrefPage.Graphics));
    }

    [Fact]
    public void A_refused_edit_changes_nothing()
    {
        var (m, s, _) = Make();
        Assert.False(m.Edit(Def("graphics_backend"), "opengl3"));
        Assert.False(m.Edit(Def("chat_font_size"), "large"));
        Assert.Empty(s.PendingKeys);
    }

    // ---------------- per account ----------------

    [Fact]
    public void The_online_status_control_needs_an_account_and_is_kept_under_it()
    {
        var none = Make(account: null).Model;
        Assert.False(none.Enabled(Def("share_online_status")));
        Assert.True(none.Enabled(Def("chat_font_size")));

        var (m, s, _) = Make();
        Assert.True(m.Enabled(Def("share_online_status")));
        Assert.True(m.Edit(Def("share_online_status"), false));
        Assert.False(s.Get(Settings.ShareOnlineStatusOf("acct")));
        Assert.True(s.Get(Settings.ShareOnlineStatusOf("other")));
        Assert.Equal(false, m.Value(Def("share_online_status")));
    }

    [Fact]
    public void Reset_on_the_privacy_page_with_no_account_leaves_the_account_setting_alone()
    {
        var (m, s, _) = Make("""{ "login_remember_name": true }""", account: null);
        m.Reset(PrefPage.Privacy);
        Assert.DoesNotContain(s.PendingKeys, k => k.StartsWith("share_online_status", StringComparison.Ordinal));
        Assert.Contains("login_remember_name", s.PendingKeys);
    }

    [Fact]
    public void Remember_my_username_is_the_setting_the_login_screen_reads()
    {
        var (m, s, _) = Make();
        m.Edit(Def("login_remember_name"), true);
        Assert.True(s.Get(Settings.LoginRememberName));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every setting on the ordinary Preferences pages: where it is, its default, its range, whether it is saved and read back, whether it
/// applies at once or after a restart, and that the page has the words for it. One table says what the catalogue must hold; the
/// tests run over it. Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class PreferenceCatalogTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private sealed record Expect(string Key, PrefPage Page, PrefKind Kind, PrefApply Apply, PrefScope Scope, object Default, double Min = 0, double Max = 0);

    private const PrefApply Live = PrefApply.Live, Restart = PrefApply.Restart, Next = PrefApply.NextUse;

    // the table: page by page, in the order the pages show them
    private static readonly Expect[] Table =
    [
        new("ui_language", PrefPage.General, PrefKind.Choice, Restart, PrefScope.Global, "default"),
        new("avatar_name_tag_mode", PrefPage.General, PrefKind.Whole, Live, PrefScope.Global, 1, 0, 2),
        new("name_tag_show_self", PrefPage.General, PrefKind.Switch, Live, PrefScope.Global, true),
        new("name_tag_group_titles", PrefPage.General, PrefKind.Switch, Live, PrefScope.Global, true),
        new("name_tag_display_names", PrefPage.General, PrefKind.Switch, Live, PrefScope.Global, true),
        new("name_tag_user_names", PrefPage.General, PrefKind.Switch, Live, PrefScope.Global, true),
        new("name_tag_friends", PrefPage.General, PrefKind.Switch, Live, PrefScope.Global, false),
        new("login_start_location", PrefPage.General, PrefKind.Choice, Next, PrefScope.Global, "last"),

        new("graphics_preset", PrefPage.Graphics, PrefKind.Choice, Live, PrefScope.Global, "highultra"),
        new("draw_distance", PrefPage.Graphics, PrefKind.Whole, Live, PrefScope.Global, 128, 64, 1024),
        new("max_texture_resolution", PrefPage.Graphics, PrefKind.Whole, Live, PrefScope.Global, 2048, 256, 2048),
        new("fps_limit", PrefPage.Graphics, PrefKind.Whole, Live, PrefScope.Global, 0, 0, 360),
        new("vsync", PrefPage.Graphics, PrefKind.Switch, Live, PrefScope.Global, true),
        new("clouds_enabled", PrefPage.Graphics, PrefKind.Switch, Live, PrefScope.Global, true),
        new("neighbours", PrefPage.Graphics, PrefKind.Switch, Next, PrefScope.Global, true),
        new("graphics_backend", PrefPage.Graphics, PrefKind.Choice, Restart, PrefScope.Global, "auto"),
        new("window_mode", PrefPage.Graphics, PrefKind.Choice, Live, PrefScope.Global, "windowed"),

        new("audio_level_master", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 1.0, 0, 1),
        new("audio_mute_master", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, false),
        new("audio_mono", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, false),
        new("audio_level_sounds", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 0.5, 0, 1),
        new("audio_mute_sounds", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, false),
        new("audio_level_ambient", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 0.5, 0, 1),
        new("audio_mute_ambient", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, false),
        new("audio_collision_sounds", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, true),
        new("audio_gesture_sounds", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, true),
        new("audio_footstep_fallback", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, true),
        new("ui_sound_volume", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 0.5, 0, 1),
        new("ui_sound_mute_all", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, false),
        new("ui_sound_mute_minimized", PrefPage.Sound, PrefKind.Switch, Live, PrefScope.Global, true),
        new("ui_sound_money_threshold", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 50.0, 0, 1000000),
        new("ui_sound_health_threshold", PrefPage.Sound, PrefKind.Real, Live, PrefScope.Global, 10.0, 0, 100),

        new("chat_font_size", PrefPage.Chat, PrefKind.Whole, Live, PrefScope.Global, 15, 10, 32),
        new("chat_overlay_seconds", PrefPage.Chat, PrefKind.Whole, Live, PrefScope.Global, 23, 0, 600),
        new("chat_timestamps", PrefPage.Chat, PrefKind.Switch, Live, PrefScope.Global, true),

        new("double_tap_run", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, true),
        new("automatic_fly", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, true),
        new("arrow_keys_always_move", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, false),
        new("mouse_sensitivity", PrefPage.MoveView, PrefKind.Real, Live, PrefScope.Global, 3.0, 0, 15),
        new("invert_mouse", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, false),
        new("enable_mouselook", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, true),
        new("show_crosshairs", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, true),
        new("mouse_smooth", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, false),
        new("camera_smoothing", PrefPage.MoveView, PrefKind.Real, Live, PrefScope.Global, 1.0, 0, 4),
        new("disable_camera_constraints", PrefPage.MoveView, PrefKind.Switch, Live, PrefScope.Global, false),

        new("login_remember_name", PrefPage.Privacy, PrefKind.Switch, Live, PrefScope.Global, false),
        new("share_online_status", PrefPage.Privacy, PrefKind.Switch, Live, PrefScope.PerAccount, true),

        new("cache_folder", PrefPage.Cache, PrefKind.Folder, Restart, PrefScope.Global, ""),
        new("cache_size_mb", PrefPage.Cache, PrefKind.Whole, Restart, PrefScope.Global, 1024, 256, 65536),
    ];

    public static IEnumerable<object[]> Keys() => Table.Select(e => new object[] { e.Key });

    public static IEnumerable<object[]> NumberKeys() => Table.Where(e => e.Kind is PrefKind.Whole or PrefKind.Real).Select(e => new object[] { e.Key });

    private static Expect Row(string key) => Table.Single(e => e.Key == key);

    private static PrefDef Def(string key) => PreferenceCatalog.Find(key) ?? throw new Xunit.Sdk.XunitException("no setting " + key);

    private const string Account = "test-account";

    [Fact]
    public void The_catalogue_holds_exactly_the_settings_of_the_table_in_page_order()
    {
        Assert.Equal(Table.Select(e => e.Key), PreferenceCatalog.All.Select(d => d.Key));
        foreach (var page in Enum.GetValues<PrefPage>())
            Assert.Equal(Table.Where(e => e.Page == page).Select(e => e.Key), PreferenceCatalog.OfPage(page).Select(d => d.Key));
    }

    [Fact]
    public void No_key_is_used_twice()
    {
        var keys = PreferenceCatalog.All.Select(d => d.Key).ToList();
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory, MemberData(nameof(Keys))]
    public void Each_setting_has_its_page_kind_scope_and_timing(string key)
    {
        var e = Row(key);
        var d = Def(key);
        Assert.Equal(e.Page, d.Page);
        Assert.Equal(e.Kind, d.Kind);
        Assert.Equal(e.Apply, d.Apply);
        Assert.Equal(e.Scope, d.Scope);
    }

    [Theory, MemberData(nameof(Keys))]
    public void Each_setting_has_its_default_with_no_file(string key)
    {
        var e = Row(key);
        var d = Def(key);
        Assert.Equal(e.Default, d.Default);
        Assert.Equal(e.Default, d.Read(new SettingsService(new MemoryStore()), Account));
    }

    [Theory, MemberData(nameof(NumberKeys))]
    public void Each_number_setting_keeps_to_its_range(string key)
    {
        var e = Row(key);
        var d = Def(key);
        Assert.Equal(e.Min, d.Min);
        Assert.Equal(e.Max, d.Max);
        object Make(double v) => e.Default is int ? (object)(int)v : v;
        foreach (double wild in new[] { e.Min - 1000, e.Max + 1000 })
        {
            var s = new SettingsService(new MemoryStore());
            bool taken = d.Write(s, Account, Make(wild), preview: false);
            double now = Convert.ToDouble(d.Read(s, Account), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(now, e.Min, e.Max);
            if (!taken)
                Assert.Equal(Convert.ToDouble(e.Default, System.Globalization.CultureInfo.InvariantCulture), now);
        }
        var inside = new SettingsService(new MemoryStore());
        Assert.True(d.Write(inside, Account, Make(e.Min), preview: false));
        Assert.Equal(Convert.ToDouble(e.Min, System.Globalization.CultureInfo.InvariantCulture), Convert.ToDouble(d.Read(inside, Account), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(d.Write(inside, Account, Make(e.Max), preview: false));
        Assert.Equal(Convert.ToDouble(e.Max, System.Globalization.CultureInfo.InvariantCulture), Convert.ToDouble(d.Read(inside, Account), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory, MemberData(nameof(Keys))]
    public void A_value_of_the_wrong_type_is_refused(string key)
    {
        var d = Def(key);
        var s = new SettingsService(new MemoryStore());
        object wrong = d.Default is string ? 5 : "not a value";
        Assert.False(d.Write(s, Account, wrong, preview: false));
        Assert.False(d.Write(s, Account, wrong, preview: true));
        Assert.Equal(d.Default, d.Read(s, Account));
    }

    /// <summary>A value that is not the default and that the setting accepts.</summary>
    private static object Other(PrefDef d, Expect e)
    {
        switch (d.Kind)
        {
            case PrefKind.Switch: return !(bool)e.Default;
            case PrefKind.Whole:
                return key(d) switch
                {
                    "avatar_name_tag_mode" => 2,
                    "max_texture_resolution" => 512,
                    "fps_limit" => 60,
                    "chat_font_size" => 20,
                    "chat_overlay_seconds" => 30,
                    "draw_distance" => 300,
                    "cache_size_mb" => 2048,
                    _ => (int)e.Min,
                };
            case PrefKind.Real: return e.Max / 2 + 0.5 is var half && half == Convert.ToDouble(e.Default) ? e.Min : half;   // a value other than the default
            case PrefKind.Choice: return d.Choices.First(c => c != (string)e.Default);
            default: return "/srv/example-cache";
        }

        static string key(PrefDef x) => x.Key;
    }

    [Fact]
    public void The_neighbour_regions_setting_is_on_the_graphics_page_and_is_the_key_the_login_reads()
    {
        var d = PreferenceCatalog.Find(Viewer.Core.Platform.NeighboursSetting.Key);
        Assert.NotNull(d);
        Assert.Equal(PrefPage.Graphics, d!.Page);
        Assert.True(Settings.NeighbourRegions.Default);   // on, as the session's own default is
        var store = new MemoryStore();
        Assert.True(d.Write(new SettingsService(store), Account, false, preview: false));
        Assert.False(Viewer.Core.Platform.NeighboursSetting.Resolve([], store.Text));
        Assert.True(Viewer.Core.Platform.NeighboursSetting.Resolve(["--neighbours=on"], store.Text));   // a start-up option still wins
    }

    [Theory, MemberData(nameof(Keys))]
    public void Each_setting_is_saved_and_read_back(string key)
    {
        var e = Row(key);
        var d = Def(key);
        var store = new MemoryStore();
        var s = new SettingsService(store);
        object value = Other(d, e);
        Assert.True(d.Write(s, Account, value, preview: false));
        Assert.Equal(value, d.Read(s, Account));
        Assert.Equal(value, d.Read(new SettingsService(new MemoryStore(store.Text)), Account));
    }

    [Theory, MemberData(nameof(Keys))]
    public void A_preview_is_seen_by_everything_that_listens_without_a_restart(string key)
    {
        var e = Row(key);
        var d = Def(key);
        var s = new SettingsService(new MemoryStore());
        var heard = new List<string>();
        s.Changed += heard.Add;
        object value = Other(d, e);
        Assert.True(d.Write(s, Account, value, preview: true));
        Assert.Equal(value, d.Read(s, Account));
        Assert.Equal([d.StoredKey(Account)], heard);
    }

    [Fact]
    public void A_per_account_setting_is_kept_under_the_account_and_one_account_does_not_change_another()
    {
        var d = Def("share_online_status");
        var s = new SettingsService(new MemoryStore());
        Assert.Equal("share_online_status_" + Account, d.StoredKey(Account));
        Assert.True(d.Write(s, Account, false, preview: false));
        Assert.Equal(false, d.Read(s, Account));
        Assert.Equal(true, d.Read(s, "someone-else"));
        Assert.Equal(false, s.Get(Settings.ShareOnlineStatusOf(Account)));
        Assert.Equal(true, s.Get(Settings.ShareOnlineStatusOf("someone-else")));
    }

    [Fact]
    public void The_choices_are_the_ones_the_viewer_has()
    {
        Assert.Equal(["default", "en"], PreferenceCatalog.Languages);
        Assert.Equal(["low", "lowmid", "mid", "midhigh", "high", "highultra", "ultra", "custom"], PreferenceCatalog.Presets);
        Assert.Equal(["auto", "vulkan", "d3d12"], PreferenceCatalog.Backends);
        Assert.Equal(["windowed", "borderless", "fullscreen"], PreferenceCatalog.WindowModes);
        Assert.Equal(["last", "home"], PreferenceCatalog.StartLocations);
        Assert.Equal(PreferenceCatalog.Languages, Def("ui_language").Choices);
        Assert.Equal(PreferenceCatalog.Presets, Def("graphics_preset").Choices);
        Assert.Equal(PreferenceCatalog.Backends, Def("graphics_backend").Choices);
        Assert.Equal(PreferenceCatalog.WindowModes, Def("window_mode").Choices);
        Assert.Equal(PreferenceCatalog.StartLocations, Def("login_start_location").Choices);
    }

    [Fact]
    public void A_choice_that_is_not_offered_is_refused_and_a_stored_one_is_ignored()
    {
        var d = Def("graphics_backend");
        var s = new SettingsService(new MemoryStore("""{ "graphics_backend": "opengl3" }"""));
        Assert.Equal("auto", d.Read(s, null));
        Assert.False(d.Write(s, null, "opengl3", preview: false));
        Assert.False(d.Write(s, null, "opengl3", preview: true));
        Assert.Equal("auto", d.Read(s, null));
    }

    [Fact]
    public void The_pseudo_localised_table_is_not_offered_as_a_language()
    {
        Assert.NotEmpty(PreferenceCatalog.Languages);
        Assert.DoesNotContain(PreferenceCatalog.Languages, id => id.Contains("XA", StringComparison.OrdinalIgnoreCase) || id.Contains("pseudo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_setting_has_its_words_in_the_string_table()
    {
        var strings = StringTable.English();
        foreach (var d in PreferenceCatalog.All)
        {
            Assert.True(strings.Has(d.LabelKey), d.LabelKey);
            foreach (var id in d.Choices)
                Assert.True(strings.Has("prefs.c." + d.Key + "." + id), "prefs.c." + d.Key + "." + id);
        }
        foreach (var page in Enum.GetValues<PrefPage>())
            Assert.True(strings.Has("prefs.category." + page.ToString().ToLowerInvariant()), page.ToString());
        Assert.True(strings.Has("prefs.note.restart"));
        Assert.True(strings.Has("prefs.note.nextuse"));
        Assert.True(strings.Has("prefs.reset.page"));
    }

    [Fact]
    public void The_viewer_offers_exactly_the_string_tables_it_has()
    {
        Assert.Equal(["en"], StringTable.Available);
        Assert.Equal("en", StringTable.ForSetting("default").Locale);
        Assert.Equal("en", StringTable.ForSetting("en").Locale);
        Assert.Equal("en", StringTable.ForSetting("klingon").Locale);
        Assert.Equal("en", StringTable.ForSetting(null).Locale);
    }

    [Fact]
    public void The_defaults_that_features_already_read_are_the_catalogues_defaults()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Equal(Settings.ChatFontSize.Default, Def("chat_font_size").Default);
        Assert.Equal(Settings.ChatTimestamps.Default, Def("chat_timestamps").Default);
        Assert.Equal(Settings.NameTagMode.Default, Def("avatar_name_tag_mode").Default);
        Assert.Equal(Settings.LoginRememberName.Default, Def("login_remember_name").Default);
        Assert.Equal(Settings.NameTagFriends.Default, Def("name_tag_friends").Default);
        Assert.Equal(128, (int)Settings.DrawDistance.Default);
        Assert.Equal(Viewer.Core.World.Interest.MinDrawDistance, (int)Def("draw_distance").Min);
        Assert.Equal(Viewer.Core.World.Interest.MaxDrawDistance, (int)Def("draw_distance").Max);
        Assert.Equal(s.Get(Settings.ChatFontSize), Def("chat_font_size").Read(s, null));
    }
}

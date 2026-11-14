// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Assets;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The built-in skies of World > Environment: each choice is a settings asset named by id (the ids of the official viewer's
/// LLEnvironment::KNOWN_SKY_*), fetched through the region's ViewerAsset capability ("settings_id"), read from LLSD, and put in
/// force only when it loaded. Plain .NET; the asset text below is made up for the tests.
/// </summary>
public class FixedSkyTests
{
    // a sky in LLSD notation with made-up values; the keys are those of a sky settings asset
    private const string SkyNotation =
        "<? llsd/notation ?>\n{'type':'sky','name':'Test sky','legacy_haze':{'ambient':[r0.25,r0.5,r0.75],'blue_density':[r0.1,r0.2,r0.3],'blue_horizon':[r0.4,r0.5,r0.6]," +
        "'density_multiplier':r0.0002,'distance_multiplier':r1.5,'haze_density':r0.6,'haze_horizon':r0.2},'sunlight_color':[r1,r0.9,r0.8],'sun_rotation':[r0,r0,r0,r1],'moon_rotation':[r0,r0,r0,r1]," +
        "'moon_brightness':r0.3,'cloud_color':[r0.3,r0.3,r0.3],'cloud_id':u1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b,'cloud_scale':r0.42,'cloud_scroll_rate':[r0.2,r0.01],'glow':[r5,r0.001,r-0.48]," +
        "'max_y':r1600,'cloud_shadow':r0.27,'reflection_probe_ambiance':r0.8}";

    private const string SkyXml =
        "<?xml version=\"1.0\"?><llsd><map><key>type</key><string>sky</string><key>sunlight_color</key><array><real>0.5</real><real>0.25</real><real>0.125</real></array>" +
        "<key>moon_brightness</key><real>0.9</real><key>legacy_haze</key><map><key>haze_horizon</key><real>0.33</real></map></map></llsd>";

    private static AssetFetchResult Ok(string text) => AssetFetchResult.Ok(Encoding.UTF8.GetBytes(text));

    // ---- the ids

    [Theory]
    [InlineData(SkyChoice.Sunrise, "01e41537-ff51-2f1f-8ef7-17e4df760bfb")]
    [InlineData(SkyChoice.Midday, "c46226b4-0e43-5a56-9708-d27ca1df3292")]
    [InlineData(SkyChoice.LegacyMidday, "6c83e853-e7f8-cad7-8ee6-5f31c453721c")]
    [InlineData(SkyChoice.Sunset, "084e26cd-a900-28e8-08d0-64a9de5c15e2")]
    [InlineData(SkyChoice.Midnight, "8a01b97a-cb20-c1ea-ac63-f7ea84ad0090")]
    public void Each_menu_choice_names_the_official_settings_asset(SkyChoice choice, string id) =>
        Assert.Equal(Guid.Parse(id), KnownSkies.AssetId(choice));

    [Fact]
    public void The_regions_own_environment_names_no_asset() => Assert.Null(KnownSkies.AssetId(SkyChoice.Region));

    [Theory]
    [InlineData("legacy noon", SkyChoice.LegacyMidday)]
    [InlineData("LegacyMidday", SkyChoice.LegacyMidday)]
    public void The_legacy_midday_choice_is_read_by_its_official_event_name(string text, SkyChoice want)
    {
        Assert.True(SkyChoices.TryParse(text, out var c));
        Assert.Equal(want, c);
    }

    [Fact]
    public void Legacy_midday_shows_the_middle_of_the_day_when_only_the_region_cycle_is_available() =>
        Assert.Equal(0.5, SkyChoices.Fraction(SkyChoice.LegacyMidday));

    [Fact]
    public void The_viewer_asset_request_names_the_settings_id()
    {
        var id = Guid.Parse("c46226b4-0e43-5a56-9708-d27ca1df3292");
        Assert.Equal("https://grid.example.org/CAPS/abc/?settings_id=" + id.ToString("D"), LmvMaterials.SettingsUri(new Uri("https://grid.example.org/CAPS/abc/"), id)?.ToString());
        Assert.EndsWith("?x=1&settings_id=" + id.ToString("D"), LmvMaterials.SettingsUri(new Uri("https://grid.example.org/c?x=1"), id)?.ToString());
    }

    // ---- reading the asset

    [Fact]
    public void A_notation_sky_is_read_into_one_frame()
    {
        var f = SettingsSky.Parse(Encoding.UTF8.GetBytes(SkyNotation), out var why);
        Assert.Null(why);
        Assert.NotNull(f);
        Assert.Equal(0f, f!.Key);
        Assert.Equal(new Vector3(1f, 0.9f, 0.8f), f.SunColor);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), f.Ambient);
        Assert.Equal(new Vector3(0.4f, 0.5f, 0.6f), f.BlueHorizon);
        Assert.Equal(0.3f, f.MoonBrightness, 5);
        Assert.Equal(0.2f, f.HazeHorizon, 5);
        Assert.Equal(0.6f, f.HazeDensity, 5);
        Assert.Equal(0.0002f, f.DensityMultiplier, 6);
        Assert.Equal(1.5f, f.DistanceMultiplier, 5);
        Assert.Equal(0.8f, f.ProbeAmbiance, 5);
        Assert.Equal(1600f, f.MaxY, 3);
        Assert.Equal(new Vector3(5f, 0.001f, -0.48f), f.Glow);
        Assert.Equal(0.42f, f.Cloud.Scale, 5);
        Assert.Equal(new Vector2(0.2f, 0.01f), f.Cloud.ScrollRate);
        Assert.Equal(Guid.Parse("1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b"), f.Cloud.TextureId);
        Assert.Equal(new Vector3(1f, 0f, 0f), f.SunDirection);
    }

    [Fact]
    public void An_xml_sky_is_read_too()
    {
        var f = SettingsSky.Parse(Encoding.UTF8.GetBytes(SkyXml), out var why);
        Assert.Null(why);
        Assert.NotNull(f);
        Assert.Equal(new Vector3(0.5f, 0.25f, 0.125f), f!.SunColor);
        Assert.Equal(0.9f, f.MoonBrightness, 5);
        Assert.Equal(0.33f, f.HazeHorizon, 5);
    }

    [Theory]
    [InlineData("<? llsd/notation ?>\n{'type':'water','name':'w'}")]
    [InlineData("<? llsd/notation ?>\n{'name':'no type'}")]
    [InlineData("<? llsd/notation ?>\n[r1,r2]")]
    [InlineData("<? llsd/notation ?>\n{'type':'sky'")]
    [InlineData("not llsd at all")]
    [InlineData("")]
    public void Anything_that_is_not_a_sky_asset_is_refused_with_a_reason(string text)
    {
        var f = SettingsSky.Parse(Encoding.UTF8.GetBytes(text), out var why);
        Assert.Null(f);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void An_asset_over_the_size_cap_is_refused_before_it_is_read()
    {
        var big = new byte[SettingsSky.MaxBytes + 1];
        Array.Fill(big, (byte)' ');
        Assert.Null(SettingsSky.Parse(big, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // ---- the store

    private sealed class Fetches
    {
        public readonly List<Guid> Asked = [];
        public Func<Guid, AssetFetchResult> Answer = _ => Ok(SkyNotation);
        public Task<AssetFetchResult> Fetch(Guid id, CancellationToken ct) { lock (Asked) Asked.Add(id); return Task.FromResult(Answer(id)); }
    }

    [Fact]
    public async Task A_choice_is_fetched_by_its_id_and_kept()
    {
        var f = new Fetches();
        var store = new FixedSkyStore(f.Fetch);
        Assert.True(await store.LoadAsync(SkyChoice.Sunset));
        Assert.Equal([KnownSkies.AssetId(SkyChoice.Sunset)!.Value], f.Asked);
        Assert.True(store.TryGet(SkyChoice.Sunset, out var frame));
        Assert.Equal(new Vector3(1f, 0.9f, 0.8f), frame?.SunColor);
        Assert.False(store.TryGet(SkyChoice.Sunrise, out _));
    }

    [Fact]
    public async Task A_loaded_choice_is_not_fetched_again()
    {
        var f = new Fetches();
        var store = new FixedSkyStore(f.Fetch);
        await store.LoadAsync(SkyChoice.Midnight);
        Assert.True(await store.LoadAsync(SkyChoice.Midnight));
        Assert.Single(f.Asked);
    }

    [Theory]
    [InlineData(AssetFetchStatus.NotFound)]
    [InlineData(AssetFetchStatus.Failed)]
    [InlineData(AssetFetchStatus.Denied)]
    public async Task A_grid_that_does_not_serve_the_asset_fails_the_load_and_says_why(AssetFetchStatus status)
    {
        var store = new FixedSkyStore((_, _) => Task.FromResult(AssetFetchResult.Of(status)));
        Assert.False(await store.LoadAsync(SkyChoice.Midday));
        Assert.False(store.TryGet(SkyChoice.Midday, out _));
        Assert.Contains(status.ToString(), store.LastFailure);
    }

    [Fact]
    public async Task An_asset_that_is_not_a_sky_fails_the_load()
    {
        var store = new FixedSkyStore((_, _) => Task.FromResult(Ok("<? llsd/notation ?>\n{'type':'water'}")));
        Assert.False(await store.LoadAsync(SkyChoice.Midday));
        Assert.False(store.TryGet(SkyChoice.Midday, out _));
        Assert.False(string.IsNullOrEmpty(store.LastFailure));
    }

    [Fact]
    public async Task A_failed_load_is_asked_for_again_next_time()
    {
        var f = new Fetches { Answer = _ => AssetFetchResult.Of(AssetFetchStatus.NotFound) };
        var store = new FixedSkyStore(f.Fetch);
        Assert.False(await store.LoadAsync(SkyChoice.Sunrise));
        f.Answer = _ => Ok(SkyNotation);
        Assert.True(await store.LoadAsync(SkyChoice.Sunrise));
        Assert.Equal(2, f.Asked.Count);
    }

    [Fact]
    public async Task Without_a_way_to_fetch_nothing_loads()
    {
        Assert.False(await new FixedSkyStore(null).LoadAsync(SkyChoice.Midday));
    }

    [Fact]
    public async Task The_regions_own_environment_needs_no_load()
    {
        var f = new Fetches();
        Assert.True(await new FixedSkyStore(f.Fetch).LoadAsync(SkyChoice.Region));
        Assert.Empty(f.Asked);
    }

    // ---- the choice in force

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Read() => null;
        public bool Write(string t) => true;
    }

    private static EnvironmentChoice NewChoice() => new(new SettingsService(new MemoryStore()));

    [Fact]
    public async Task A_choice_is_put_in_force_only_after_it_prepared()
    {
        var c = NewChoice();
        var gate = new TaskCompletionSource<bool>();
        c.Prepare = _ => gate.Task;
        var seen = new List<SkyChoice>();
        c.Changed += seen.Add;
        var pending = c.Request(SkyChoice.Sunset);
        Assert.Equal(SkyChoice.Region, c.Current);
        Assert.Empty(seen);
        gate.SetResult(true);
        await pending;
        Assert.Equal(SkyChoice.Sunset, c.Current);
        Assert.Equal([SkyChoice.Sunset], seen);
    }

    [Fact]
    public async Task A_choice_that_could_not_be_prepared_leaves_the_one_in_force_and_says_so()
    {
        var c = NewChoice();
        await c.Request(SkyChoice.Sunrise);
        c.Prepare = _ => Task.FromResult(false);
        var failed = new List<SkyChoice>();
        c.Failed += failed.Add;
        await c.Request(SkyChoice.Midnight);
        Assert.Equal(SkyChoice.Sunrise, c.Current);
        Assert.Equal([SkyChoice.Midnight], failed);
    }

    [Fact]
    public async Task Without_a_preparation_a_choice_is_set_at_once()
    {
        var c = NewChoice();
        await c.Request(SkyChoice.LegacyMidday);
        Assert.Equal(SkyChoice.LegacyMidday, c.Current);
    }

    [Fact]
    public async Task Going_back_to_the_region_prepares_nothing()
    {
        var c = NewChoice();
        await c.Request(SkyChoice.Sunrise);
        int calls = 0;
        c.Prepare = _ => { calls++; return Task.FromResult(false); };
        await c.Request(SkyChoice.Region);
        Assert.Equal(SkyChoice.Region, c.Current);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Asking_for_the_choice_already_in_force_prepares_nothing()
    {
        var c = NewChoice();
        await c.Request(SkyChoice.Sunrise);
        int calls = 0;
        c.Prepare = _ => { calls++; return Task.FromResult(true); };
        await c.Request(SkyChoice.Sunrise);
        Assert.Equal(0, calls);
    }

    // ---- Pause Clouds

    [Fact]
    public void Pausing_the_clouds_toggles_and_tells_the_listeners()
    {
        var c = NewChoice();
        var seen = new List<bool>();
        c.CloudsPausedChanged += seen.Add;
        Assert.False(c.CloudsPaused);
        c.ToggleCloudsPaused();
        Assert.True(c.CloudsPaused);
        c.ToggleCloudsPaused();
        Assert.False(c.CloudsPaused);
        Assert.Equal([true, false], seen);
    }

    // ---- the menu

    private static (MenuModel Menu, ShellCommands Cmds, EnvironmentChoice Choice) Installed()
    {
        var menu = new MenuModel(); var cmds = new ShellCommands(); var choice = NewChoice();
        menu.AddMenu("world", "menu.world");
        EnvironmentMenu.Install(menu, cmds, choice, () => { });
        return (menu, cmds, choice);
    }

    private static IReadOnlyList<MenuEntry> Kids(MenuModel menu) => menu.Menus.First(m => m.Id == "world").Entries.First(e => e.Id == "world.environment").Children ?? [];

    [Fact]
    public void Midday_legacy_sits_between_midday_and_sunset_and_pause_clouds_comes_last()
    {
        var (menu, _, _) = Installed();
        Assert.Equal(["env.sunrise", "env.midday", "env.legacymidday", "env.sunset", "env.midnight", "env.region", "env.personal", "env.pauseclouds"], Kids(menu).Select(e => e.Id));
        Assert.Equal(["menu.env.sunrise", "menu.env.midday", "menu.env.legacymidday", "menu.env.sunset", "menu.env.midnight", "menu.env.region", "menu.env.personal", "menu.env.pauseclouds"], Kids(menu).Select(e => e.LabelKey));
    }

    [Fact]
    public void Choosing_midday_legacy_checks_it_alone()
    {
        var (menu, cmds, choice) = Installed();
        Assert.True(menu.Activate("env.legacymidday", cmds));
        Assert.Equal(SkyChoice.LegacyMidday, choice.Current);
        Assert.Equal(["env.legacymidday"], Kids(menu).Where(k => k.IsChecked).Select(k => k.Id));
    }

    [Fact]
    public void Midday_legacy_has_the_control_shift_T_shortcut_of_the_official_menu()
    {
        var (_, cmds, _) = Installed();
        Assert.True(cmds.Has(EnvironmentMenu.ShortcutCommand("legacy noon")));
        Assert.False(cmds.Has(EnvironmentMenu.ShortcutCommand("region")));   // Use Shared Environment has none
    }

    [Fact]
    public async Task A_choice_that_failed_to_load_keeps_the_check_mark_where_it_was()
    {
        var (menu, cmds, choice) = Installed();
        menu.Activate("env.midnight", cmds);
        choice.Prepare = _ => Task.FromResult(false);
        await choice.Request(SkyChoice.Sunrise);
        Assert.Equal(["env.midnight"], Kids(menu).Where(k => k.IsChecked).Select(k => k.Id));
    }

    [Fact]
    public void Pause_clouds_is_a_check_item_that_toggles()
    {
        var (menu, cmds, choice) = Installed();
        Assert.True(menu.Activate("env.pauseclouds", cmds));
        Assert.True(choice.CloudsPaused);
        Assert.Contains(Kids(menu), k => k.Id == "env.pauseclouds" && k.IsChecked);
        menu.Activate("env.pauseclouds", cmds);
        Assert.False(choice.CloudsPaused);
    }

    [Fact]
    public void The_new_labels_are_the_official_names()
    {
        var en = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FindEn())).RootElement;
        Assert.Equal("Midday (Legacy)", en.TryGetProperty("menu.env.legacymidday", out var a) ? a.GetString() : null);
        Assert.Equal("Pause Clouds", en.TryGetProperty("menu.env.pauseclouds", out var b) ? b.GetString() : null);
    }

    private static string FindEn()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "host", "Viewer.Shell", "Strings", "en.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("en.json");
    }
}

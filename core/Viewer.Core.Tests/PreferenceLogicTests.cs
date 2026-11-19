// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Chat;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The engine-free rules behind the Preferences settings that need more than a stored value: which nearby chat lines stay over the
/// view, which mouse-look values are in force, where the cache lives, which saved login names go, when the viewer starts itself again
/// for a new backend, and what a window mode asks of the window. Plain .NET; every member builds its own objects, so the class runs in
/// parallel.
/// </summary>
public class PreferenceLogicTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static SettingsService Svc(string? file = null) => new(new MemoryStore(file));

    // ---------------- nearby chat lines over the view ----------------

    private static readonly DateTimeOffset Now = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);

    private static ChatLine Line(double secondsAgo, string text) => new(Now.AddSeconds(-secondsAgo), ChatLineStyle.Agent, Guid.Empty, "Ann", text);

    [Fact]
    public void With_no_time_set_the_newest_lines_show_however_old_they_are()
    {
        var lines = Enumerable.Range(0, 8).Select(i => Line(3600 - i, "line " + i)).ToList();
        var shown = NearbyOverlayRules.Visible(lines, Now, seconds: 0, shown: 6);
        Assert.Equal(["line 2", "line 3", "line 4", "line 5", "line 6", "line 7"], shown.Select(l => l.Text));
    }

    [Fact]
    public void With_a_time_set_lines_older_than_it_are_not_shown_and_the_order_is_kept()
    {
        var lines = new[] { Line(100, "old"), Line(31, "just too old"), Line(29, "fresh a"), Line(5, "fresh b") };
        var shown = NearbyOverlayRules.Visible(lines, Now, seconds: 30, shown: 6);
        Assert.Equal(["fresh a", "fresh b"], shown.Select(l => l.Text));
    }

    [Fact]
    public void A_line_exactly_as_old_as_the_time_still_shows_and_one_moment_older_does_not()
    {
        var shown = NearbyOverlayRules.Visible([Line(30, "on the line"), Line(30.001, "past it")], Now, seconds: 30, shown: 6);
        Assert.Equal(["on the line"], shown.Select(l => l.Text));
    }

    [Fact]
    public void The_count_limit_applies_after_the_age_filter()
    {
        var lines = Enumerable.Range(0, 10).Select(i => Line(10 - i, "line " + i)).ToList();
        var shown = NearbyOverlayRules.Visible(lines, Now, seconds: 60, shown: 6);
        Assert.Equal(6, shown.Count);
        Assert.Equal("line 9", shown[^1].Text);
        Assert.Equal("line 4", shown[0].Text);
    }

    [Fact]
    public void A_line_stamped_in_the_future_is_shown_and_no_lines_gives_none()
    {
        Assert.Equal(["ahead"], NearbyOverlayRules.Visible([Line(-5, "ahead")], Now, seconds: 30, shown: 6).Select(l => l.Text));
        Assert.Empty(NearbyOverlayRules.Visible([], Now, seconds: 30, shown: 6));
    }

    // ---------------- mouse-look ----------------

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void With_nothing_saved_and_no_variables_the_official_defaults_are_in_force()
    {
        Assert.Equal(new MouseLookValues(true, true, 3.0f, false, false), MouseLookSettings.Resolve(Svc(), Env()));
    }

    [Fact]
    public void Saved_preferences_are_used()
    {
        var s = Svc("""{ "enable_mouselook": false, "show_crosshairs": false, "mouse_sensitivity": 7.5, "invert_mouse": true, "mouse_smooth": true }""");
        Assert.Equal(new MouseLookValues(false, false, 7.5f, true, true), MouseLookSettings.Resolve(s, Env()));
    }

    [Fact]
    public void A_variable_that_is_set_wins_over_the_saved_value()
    {
        var s = Svc("""{ "enable_mouselook": true, "show_crosshairs": true, "mouse_sensitivity": 7.5, "invert_mouse": false, "mouse_smooth": false }""");
        var v = MouseLookSettings.Resolve(s, Env(("NV_ENABLE_MOUSELOOK", "off"), ("NV_SHOW_CROSSHAIRS", "0"), ("NV_MOUSE_SENSITIVITY", "5"), ("NV_INVERT_MOUSE", "on"), ("NV_MOUSE_SMOOTH", "1")));
        Assert.Equal(new MouseLookValues(false, false, 5f, true, true), v);
    }

    [Fact]
    public void A_variable_that_does_not_read_is_ignored_and_a_big_sensitivity_is_held_to_the_range()
    {
        var s = Svc("""{ "mouse_sensitivity": 7.5, "invert_mouse": true }""");
        Assert.Equal(new MouseLookValues(true, true, 7.5f, true, false),
            MouseLookSettings.Resolve(s, Env(("NV_MOUSE_SENSITIVITY", "abc"), ("NV_INVERT_MOUSE", "maybe"))));
        Assert.Equal(15f, MouseLookSettings.Resolve(s, Env(("NV_MOUSE_SENSITIVITY", "99"))).Sensitivity);
    }

    // ---------------- the cache ----------------

    [Fact]
    public void With_nothing_saved_the_cache_is_in_the_default_place_with_the_size_it_always_had()
    {
        var c = ContentCacheSetting.Resolve(Svc());
        Assert.Null(c.Root);
        Assert.Equal(1L << 30, c.CapBytes);
    }

    [Fact]
    public void A_full_path_and_a_size_are_taken()
    {
        string folder = TestTemp.NewFolderPath("cache-choice");
        var s = Svc("{ \"cache_folder\": " + System.Text.Json.JsonSerializer.Serialize(folder) + ", \"cache_size_mb\": 2048 }");
        var c = ContentCacheSetting.Resolve(s);
        Assert.Equal(folder, c.Root);
        Assert.Equal(2048L * 1024 * 1024, c.CapBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cache")]
    [InlineData("..\\\\cache")]
    public void A_folder_that_is_not_a_full_path_is_ignored(string folder)
    {
        var s = Svc("{ \"cache_folder\": " + System.Text.Json.JsonSerializer.Serialize(folder) + ", \"cache_size_mb\": 2048 }");
        var c = ContentCacheSetting.Resolve(s);
        Assert.Null(c.Root);
        Assert.Equal(2048L * 1024 * 1024, c.CapBytes);
    }

    [Fact]
    public void A_size_outside_the_range_is_held_to_it()
    {
        Assert.Equal(65536L * 1024 * 1024, ContentCacheSetting.Resolve(Svc("""{ "cache_size_mb": 90000 }""")).CapBytes);
        Assert.Equal(256L * 1024 * 1024, ContentCacheSetting.Resolve(Svc("""{ "cache_size_mb": 1 }""")).CapBytes);
    }

    // ---------------- saved login names ----------------

    [Fact]
    public void Clearing_the_saved_login_names_empties_them_and_leaves_the_remember_switch_alone()
    {
        var store = new MemoryStore("""{ "login_first": "Test", "login_last": "User", "login_remember_name": true }""");
        var s = new SettingsService(store);

        Assert.True(LoginNames.Clear(s));

        Assert.Equal("", s.GetText(Settings.LoginFirstNameKey));
        Assert.Equal("", s.GetText(Settings.LoginLastNameKey));
        Assert.True(s.Get(Settings.LoginRememberName));
        var reread = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal("", reread.GetText(Settings.LoginFirstNameKey));
        Assert.DoesNotContain("Test", store.Text ?? "");
        Assert.False(LoginNames.Clear(s));   // nothing left to clear
    }

    [Fact]
    public void Clearing_one_saved_name_counts_and_nothing_saved_returns_false()
    {
        Assert.False(LoginNames.Clear(Svc()));
        var s = Svc("""{ "login_first": "Test" }""");
        Assert.True(LoginNames.Clear(s));
        Assert.Equal("", s.GetText("login_first"));
    }

    // ---------------- starting again for a new backend ----------------

    private static readonly string[] Engine = ["--path", "viewer"];
    private static readonly string[] User = ["--grid=https://grid.example.org"];

    [Fact]
    public void A_saved_backend_that_differs_from_the_running_one_starts_the_viewer_again_once_with_the_saved_driver()
    {
        var plan = BackendRelaunch.Plan("d3d12", "vulkan", Engine, User);
        Assert.NotNull(plan);
        Assert.Equal(["--path", "viewer", "--rendering-driver", "d3d12", "--", "--grid=https://grid.example.org", "--backend-applied=d3d12"], plan);
    }

    [Fact]
    public void The_other_way_round_works_too()
    {
        Assert.Equal(["--rendering-driver", "vulkan", "--", "--backend-applied=vulkan"], BackendRelaunch.Plan("vulkan", "d3d12", [], []));
    }

    [Fact]
    public void No_second_start_when_the_choice_is_automatic_already_running_or_not_a_backend_the_viewer_uses()
    {
        Assert.NotNull(BackendRelaunch.Plan("d3d12", "vulkan", Engine, User));   // the wanted case, so the refusals below mean something
        foreach (var (saved, running) in new[] { ("auto", "vulkan"), ("vulkan", "vulkan"), ("d3d12", "d3d12"), ("opengl3", "vulkan"), ("", "vulkan") })
            Assert.Null(BackendRelaunch.Plan(saved, running, Engine, User));
    }

    [Fact]
    public void A_second_start_follows_the_first_only_when_wanted()
    {
        Assert.NotNull(BackendRelaunch.Plan("d3d12", "vulkan", Engine, User));   // the wanted case, so the refusals below mean something
        Assert.Null(BackendRelaunch.Plan("d3d12", "vulkan", ["--rendering-driver", "vulkan"], User));        // a driver given on the command line wins
        Assert.Null(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--backend-applied=d3d12"]));            // already started again: never a third time
        Assert.Null(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--test-render"]));                      // test and render runs choose their own
        Assert.Null(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--s1-tag=check"]));
    }

    // ---------------- window mode ----------------

    [Theory]
    [InlineData("windowed", false, false)]
    [InlineData("borderless", false, true)]
    [InlineData("fullscreen", true, false)]
    [InlineData("nonsense", false, false)]
    [InlineData(null, true, false)]
    public void Each_window_mode_asks_for_the_right_window(string? mode, bool fullscreen, bool borderless)
    {
        Assert.Equal(new DisplayPlan(fullscreen, borderless), DisplayPlan.For(mode));
    }

    [Theory]
    [InlineData("borderless", true)]
    [InlineData("windowed", false)]
    [InlineData("fullscreen", false)]   // the engine's own fullscreen fills the screen
    [InlineData(null, false)]
    public void Only_a_borderless_window_is_placed_to_fill_its_screen_by_the_viewer(string? mode, bool fills)
    {
        Assert.Equal(fills, DisplayPlan.For(mode).FillsScreen);
    }
}

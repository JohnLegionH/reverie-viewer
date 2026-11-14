// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The top bar's rules without the engine: the interface size (the screen's display scale by default, 75 % to 200 %, a stored size
/// wins, a move to another screen follows its scale); the menu bar (open on click only, never on hover; once open the pointer
/// switches menus; arrow keys move between titles); the location field (display parts and typed addresses); the narrow-window row
/// rule; the window title; Check for updates for each answer (stubbed, no network); Back's teleport history; and a window's
/// remembered place pulled back on-screen. Pure objects; runs in parallel.
/// </summary>
public class ReverieTopBarTests
{
    // ---- interface size

    [Theory]
    [InlineData(96f, 100)]
    [InlineData(144f, 150)]
    [InlineData(192f, 200)]
    [InlineData(288f, 200)]   // a 300 % screen is held to the top of the range
    [InlineData(48f, 75)]     // below the range is held to its floor
    [InlineData(84f, 88)]
    [InlineData(0f, 100)]     // no reading
    public void The_default_size_is_the_screens_display_scale_within_75_to_200_percent(float dpi, int expected) =>
        Assert.Equal(expected, InterfaceSize.DefaultFor(dpi));

    [Theory]
    [InlineData(125, 192f, 125)]
    [InlineData(300, 96f, 200)]
    [InlineData(50, 96f, 75)]
    public void A_stored_size_wins_over_the_screen_and_is_held_to_the_range(int stored, float dpi, int expected) =>
        Assert.Equal(expected, InterfaceSize.Effective(stored, dpi));

    [Fact]
    public void Without_a_stored_size_the_screen_decides() => Assert.Equal(150, InterfaceSize.Effective(null, 144f));

    [Fact]
    public void Moving_to_a_screen_with_another_scale_follows_it_and_moving_within_a_screen_changes_nothing()
    {
        var size = new InterfaceSizeTracker(null);
        Assert.True(size.Observe(0, 96f));
        Assert.Equal(100, size.Current);
        Assert.False(size.Observe(0, 96f));
        Assert.True(size.Observe(1, 192f));
        Assert.Equal(200, size.Current);
        Assert.False(size.Observe(2, 192f));   // another screen at the same scale: nothing to redo
        Assert.Equal(200, size.Current);
    }

    [Fact]
    public void With_a_stored_size_a_move_to_another_screen_keeps_it_and_clearing_it_follows_the_screen_again()
    {
        var size = new InterfaceSizeTracker(125);
        Assert.True(size.Observe(0, 96f));
        Assert.Equal(125, size.Current);
        Assert.False(size.Observe(1, 192f));
        Assert.Equal(125, size.Current);
        Assert.True(size.SetStored(null));
        Assert.Equal(200, size.Current);
        Assert.True(size.SetStored(90));
        Assert.Equal(90, size.Current);
    }

    // ---- menu bar

    [Fact]
    public void Hovering_across_every_title_with_nothing_open_opens_nothing()
    {
        var bar = new MenuBarState(7);
        for (int i = 0; i < 7; i++)
            bar.Hover(i);
        Assert.Null(bar.Open);
    }

    [Fact]
    public void A_click_opens_a_menu_and_a_second_click_on_it_closes_it()
    {
        var bar = new MenuBarState(7);
        bar.Click(3);
        Assert.Equal(3, bar.Open);
        Assert.Equal(3, bar.Focused);
        bar.Click(3);
        Assert.Null(bar.Open);
    }

    [Fact]
    public void Once_a_menu_is_open_moving_onto_another_title_switches_to_it()
    {
        var bar = new MenuBarState(7);
        bar.Click(1);
        bar.Hover(4);
        Assert.Equal(4, bar.Open);
        Assert.Equal(4, bar.Focused);
        bar.Close();
        Assert.Null(bar.Open);
        bar.Hover(2);
        Assert.Null(bar.Open);
    }

    [Fact]
    public void Arrow_keys_move_between_titles_wrapping_and_carry_an_open_menu_along()
    {
        var bar = new MenuBarState(7);
        bar.Arrow(-1);
        Assert.Equal(6, bar.Focused);
        Assert.Null(bar.Open);
        bar.Arrow(+1);
        Assert.Equal(0, bar.Focused);
        bar.Activate();
        Assert.Equal(0, bar.Open);
        bar.Arrow(+1);
        Assert.Equal(1, bar.Focused);
        Assert.Equal(1, bar.Open);
    }

    // ---- location field

    [Fact]
    public void A_named_parcel_shows_before_the_region_once_and_the_coordinates()
    {
        var parts = LocationField.Display("Example Region", "Example Parcel", new Vector3(128.4f, 64.6f, 25.2f), 0)!;
        Assert.Equal(new LocationParts("Example Parcel", "Example Region", "128, 65, 25"), parts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Example Region")]
    [InlineData(" example region ")]   // a parcel named like its region would show the region twice
    public void A_parcel_without_a_name_of_its_own_is_left_out(string? parcel)
    {
        var parts = LocationField.Display("Example Region", parcel, new Vector3(10, 20, 30), 0)!;
        Assert.Null(parts.Parcel);
        Assert.Equal("Example Region", parts.Region);
        Assert.Equal("10, 20, 30", parts.Coordinates);
    }

    [Fact]
    public void Without_a_region_there_is_nothing_to_show() => Assert.Null(LocationField.Display(null, "P", Vector3.Zero, 0));

    [Theory]
    [InlineData("Example Region", "Example Region", null, null, null)]
    [InlineData("  Example Region  ", "Example Region", null, null, null)]
    [InlineData("Example Region/128/64/22", "Example Region", 128f, 64f, 22f)]
    [InlineData("Example Region/128/64", "Example Region", 128f, 64f, 0f)]
    [InlineData("Example%20Region/10/20/30", "Example Region", 10f, 20f, 30f)]
    [InlineData("hop://grid.example.org:8002/Example Region/10/20/30", "Example Region", 10f, 20f, 30f)]
    [InlineData("secondlife://Example Region/10/20/30", "Example Region", 10f, 20f, 30f)]
    public void A_region_name_or_address_is_read(string text, string region, float? x, float? y, float? z)
    {
        var a = LocationField.Parse(text)!;
        Assert.Equal(region, a.Region);
        Assert.Equal(x is null ? null : new Vector3(x.Value, y!.Value, z!.Value), a.Position);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/10/20/30")]
    [InlineData("Example Region/abc/20/30")]
    [InlineData("Example Region/300/20/30")]
    [InlineData("Example Region/10/20/30/40")]
    public void Text_that_is_no_place_is_refused(string? text) => Assert.Null(LocationField.Parse(text));

    // ---- narrow window

    [Fact]
    public void While_the_three_zones_fit_they_share_one_row_of_38()
    {
        Assert.False(TopBarLayout.LocationOnOwnRow(1600, 520, 520, 300));
        Assert.Equal(38f, TopBarLayout.Height(1600, 520, 520, 300));
    }

    [Fact]
    public void When_they_do_not_fit_the_location_zone_drops_to_its_own_row_under_the_menus()
    {
        Assert.True(TopBarLayout.LocationOnOwnRow(1000, 520, 520, 300));
        Assert.Equal(76f, TopBarLayout.Height(1000, 520, 520, 300));
    }

    [Fact]
    public void The_gaps_between_zones_count()
    {
        float exact = 400 + 300 + 200 + 2 * TopBarLayout.ZoneGap;
        Assert.False(TopBarLayout.LocationOnOwnRow(exact, 400, 300, 200));
        Assert.True(TopBarLayout.LocationOnOwnRow(exact - 1, 400, 300, 200));
    }

    // ---- window title

    [Fact]
    public void Before_login_the_title_is_the_viewers_name_alone() =>
        Assert.Equal("Reverie Viewer", WindowTitleText.For(StringTable.English(), null, "Example Grid"));

    [Fact]
    public void Once_logged_in_the_title_names_the_avatar_and_the_grid() =>
        Assert.Equal("Reverie Viewer - Test User - Example Grid", WindowTitleText.For(StringTable.English(), "Test User", "Example Grid"));

    [Fact]
    public void A_grid_with_no_name_leaves_it_out_and_control_characters_never_reach_the_title()
    {
        Assert.Equal("Reverie Viewer - Test User", WindowTitleText.For(StringTable.English(), "Test User", " "));
        Assert.Equal("Reverie Viewer - Test User - Example Grid", WindowTitleText.For(StringTable.English(), "Test\nUser", "Example\tGrid").Replace("  ", " "));
        Assert.DoesNotContain("time of day", WindowTitleText.For(StringTable.English(), "Test User", "Example Grid"));
    }

    // ---- check for updates

    private const string Release = """{"tag_name":"v0.2.0","html_url":"https://example.org/releases/v0.2.0","name":"Reverie Viewer 0.2.0"}""";

    [Fact]
    public void A_newer_release_is_available_with_its_version_and_link()
    {
        var r = UpdateCheck.Interpret(200, Release, "0.1.0 (early preview)");
        Assert.Equal(new UpdateResult(UpdateOutcome.Available, "0.2.0", "https://example.org/releases/v0.2.0"), r);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.1")]
    public void The_same_or_a_later_build_is_up_to_date(string current) =>
        Assert.Equal(UpdateOutcome.UpToDate, UpdateCheck.Interpret(200, Release, current).Outcome);

    [Theory]
    [InlineData(404, Release)]   // a private repository answers 404 to a request without credentials
    [InlineData(403, Release)]
    [InlineData(500, Release)]
    [InlineData(200, "not json")]
    [InlineData(200, """{"tag_name":"latest"}""")]
    [InlineData(200, null)]
    public void Any_other_answer_could_not_be_checked(int status, string? body) =>
        Assert.Equal(UpdateOutcome.CouldNotCheck, UpdateCheck.Interpret(status, body, "0.1.0").Outcome);

    [Fact]
    public async Task The_request_goes_to_the_latest_release_once_and_a_failure_to_reach_it_could_not_be_checked()
    {
        var asked = new List<string>();
        var ok = await UpdateCheck.RunAsync(u => { asked.Add(u); return Task.FromResult((200, (string?)Release)); }, "0.1.0");
        Assert.Equal(UpdateOutcome.Available, ok.Outcome);
        Assert.Equal([UpdateCheck.LatestReleaseApi], asked);
        var down = await UpdateCheck.RunAsync(_ => throw new HttpRequestException("offline"), "0.1.0");
        Assert.Equal(UpdateOutcome.CouldNotCheck, down.Outcome);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", 1)]
    [InlineData("0.1.0 (early preview)", "v0.1.0", 0)]
    [InlineData("0.1.10", "0.1.9", 1)]
    [InlineData("1.0", "1.0.1", -1)]
    public void Versions_compare_by_their_numbers(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(UpdateCheck.Compare(a, b)!.Value));

    // ---- teleport history

    [Fact]
    public void Back_returns_to_the_places_left_newest_first_and_then_has_nothing()
    {
        var h = new TeleportHistory();
        var a = new LocationAddress("Region A", new Vector3(1, 2, 3));
        var b = new LocationAddress("Region B", new Vector3(4, 5, 6));
        h.Departed(a);
        h.Departed(b);
        Assert.Equal(2, h.Count);
        Assert.Equal(b, h.Peek);
        Assert.Equal(b, h.Back());
        Assert.Equal(a, h.Back());
        Assert.Null(h.Back());
    }

    [Fact]
    public void The_history_keeps_only_the_newest_places()
    {
        var h = new TeleportHistory();
        for (int i = 0; i < TeleportHistory.Capacity + 5; i++)
            h.Departed(new LocationAddress($"R{i}", null));
        Assert.Equal(TeleportHistory.Capacity, h.Count);
        Assert.Equal($"R{TeleportHistory.Capacity + 4}", h.Back()!.Region);
    }

    // ---- window chrome place

    [Fact]
    public void A_window_opens_centred_at_its_default_size_the_first_time()
    {
        Assert.Equal(new ChromeRect(380, 0, 520, 720), WindowChromePlace.Open(null, 520, 720, 360, 420, 1280, 720));
        Assert.Equal(new ChromeRect(380, 100, 520, 520), WindowChromePlace.Open(null, 520, 520, 360, 420, 1280, 720));
    }

    [Fact]
    public void A_remembered_place_is_kept_and_survives_being_written_and_read()
    {
        var r = new ChromeRect(40, 60, 600, 500);
        Assert.Equal(r, WindowChromePlace.Open(WindowChromePlace.Read(WindowChromePlace.Write(r)), 520, 720, 360, 420, 1280, 720));
    }

    [Theory]
    [InlineData(5000, 60, 1280 - 600, 60)]   // off the right
    [InlineData(-5000, 60, 0, 60)]           // off the left
    [InlineData(40, -300, 40, 0)]            // title bar above the top
    [InlineData(40, 5000, 40, 720 - 42)]     // below the bottom: the title bar comes back into view
    public void A_remembered_place_off_screen_is_pulled_back(float x, float y, float ex, float ey)
    {
        var r = WindowChromePlace.PullOnScreen(new ChromeRect(x, y, 600, 500), 360, 420, 1280, 720);
        Assert.Equal(new ChromeRect(ex, ey, 600, 500), r);
    }

    [Fact]
    public void A_remembered_size_larger_than_the_view_is_made_to_fit_and_never_below_the_minimum()
    {
        Assert.Equal(new ChromeRect(0, 0, 1280, 720), WindowChromePlace.PullOnScreen(new ChromeRect(0, 0, 3000, 2000), 360, 420, 1280, 720));
        Assert.Equal(new ChromeRect(10, 10, 360, 420), WindowChromePlace.PullOnScreen(new ChromeRect(10, 10, 100, 100), 360, 420, 1280, 720));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("1,2,-3,4")]
    public void Settings_text_that_is_no_place_is_refused(string? text) => Assert.Null(WindowChromePlace.Read(text));
}

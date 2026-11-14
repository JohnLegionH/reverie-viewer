// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A window opened for the first time is at least as large as the official viewer's window it stands for (the width and height
/// of that window's floater_*.xml, raised to its minimum where the minimum is larger), larger when ours asks for more, and grows
/// to fit its content; it never takes more than 85 % of the main view either way. Remembered places that only ever held the old
/// default size are dropped once, so the new sizes show; places the person changed stay. Pure, runs in parallel.
/// </summary>
public class FirstOpenSizeTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static readonly UiRect Screen = new(0, 0, 1920, 1080);
    private static readonly FloaterSpec Inventory = new("inventory", "inventory.title", new UiSize(420, 520), new UiSize(300, 320));
    private static readonly FloaterSpec People = new("people", "people.title", new UiSize(500, 440), new UiSize(360, 320));
    private static readonly FloaterSpec Model = new("upload_model", "model.title", new UiSize(460, 620), new UiSize(360, 420));
    private static readonly FloaterSpec Prefs = new("preferences", "prefs.title", new UiSize(700, 620), new UiSize(520, 360));
    private static readonly FloaterSpec NoCounterpart = new("addons", "addons.title", new UiSize(420, 460), new UiSize(320, 260));

    private static FloaterManager Manager(IFloaterPlaces? places, UiRect bounds, params FloaterSpec[] specs)
    {
        var m = new FloaterManager(places);
        m.SetBounds(bounds);
        foreach (var s in specs)
            m.Register(s);
        return m;
    }

    [Theory]
    [InlineData("inventory", "floater_my_inventory.xml", 418, 570)]
    [InlineData("people", "floater_people.xml", 370, 570)]
    [InlineData("preferences", "floater_preferences.xml", 658, 530)]
    [InlineData("about_land", "floater_about_land.xml", 634, 420)]
    [InlineData("world_map", "floater_world_map.xml", 650, 600)]
    [InlineData("about", "floater_about.xml", 500, 440)]
    [InlineData("notifications", "floater_notifications_tabbed.xml", 435, 550)]
    public void The_official_sizes_are_those_of_the_official_windows(string id, string file, float w, float h)
    {
        Assert.Equal(new UiSize(w, h), OfficialFloaterSizes.Of(id));
        Assert.Equal(file, OfficialFloaterSizes.Table[id].File);
    }

    [Fact]
    public void A_window_with_no_official_counterpart_has_no_official_size()
    {
        Assert.Null(OfficialFloaterSizes.Of("addons"));
        Assert.Null(OfficialFloaterSizes.Of("no-such-window"));
    }

    [Fact]
    public void The_first_open_size_is_the_larger_of_ours_and_the_official_in_each_direction()
    {
        var m = Manager(null, Screen, Inventory, People, Prefs, NoCounterpart);
        Assert.Equal(new UiSize(420, 570), m.FirstOpenSize(Inventory));
        Assert.Equal(new UiSize(500, 570), m.FirstOpenSize(People));
        Assert.Equal(new UiSize(700, 620), m.FirstOpenSize(Prefs));
        Assert.Equal(new UiSize(420, 460), m.FirstOpenSize(NoCounterpart));
    }

    [Fact]
    public void The_first_open_size_takes_at_most_85_percent_of_the_main_view()
    {
        var m = Manager(null, new UiRect(0, 0, 1000, 700), Model);
        Assert.Equal(new UiSize(850, 595), m.FirstOpenSize(Model));
        Assert.Equal(0.85f, FloaterManager.FirstOpenShare);
    }

    [Fact]
    public void A_window_with_no_remembered_place_opens_at_its_first_open_size()
    {
        var m = Manager(null, Screen, Inventory);
        Assert.True(m.Open("inventory"));
        var r = m.RectOf("inventory")!.Value;
        Assert.Equal(420, r.W);
        Assert.Equal(570, r.H);
    }

    [Fact]
    public void Fitting_to_content_grows_the_window_but_not_past_85_percent_of_the_main_view()
    {
        var m = Manager(null, Screen, Inventory);
        m.Open("inventory");
        Assert.True(m.FitToContent("inventory", 600, 700));
        Assert.Equal(new UiSize(600, 700), new UiSize(m.RectOf("inventory")!.Value.W, m.RectOf("inventory")!.Value.H));
        m.FitToContent("inventory", 4000, 4000);
        var r = m.RectOf("inventory")!.Value;
        Assert.Equal(1920 * 0.85f, r.W, 1);
        Assert.Equal(1080 * 0.85f, r.H, 1);
    }

    [Fact]
    public void A_remembered_place_at_the_old_default_size_is_dropped_once()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        settings.SetText(SettingsFloaterPlaces.KeyOf("inventory"), "40,40,420,520");   // never resized: the old default
        var m = Manager(new SettingsFloaterPlaces(settings), Screen, Inventory);
        m.Open("inventory");
        var r = m.RectOf("inventory")!.Value;
        Assert.Equal(new UiSize(420, 570), new UiSize(r.W, r.H));

        // later the person sizes it to exactly the old default; that is their choice and stays, across a restart too
        m.Resize("inventory", 420, 520);
        m.Close("inventory");
        m.SavePlaces();
        var again = Manager(new SettingsFloaterPlaces(new SettingsService(store)), Screen, Inventory);
        again.Open("inventory");
        var kept = again.RectOf("inventory")!.Value;
        Assert.Equal(new UiSize(420, 520), new UiSize(kept.W, kept.H));
    }

    [Fact]
    public void A_remembered_place_is_not_grown_to_its_content_only_a_first_opening_is()
    {
        var settings = new SettingsService(new MemoryStore());
        settings.SetText(SettingsFloaterPlaces.KeyOf("preferences"), "320,160,760,580");
        var m = Manager(new SettingsFloaterPlaces(settings), Screen, Prefs, NoCounterpart);
        m.Open("preferences");
        Assert.False(m.FitToContent("preferences", 760, 683));
        Assert.Equal(new UiRect(320, 160, 760, 580), m.RectOf("preferences"));
        m.Open("addons");
        Assert.True(m.FitToContent("addons", 500, 700));
        Assert.Equal(new UiSize(500, 700), new UiSize(m.RectOf("addons")!.Value.W, m.RectOf("addons")!.Value.H));
    }

    [Fact]
    public void A_remembered_place_the_person_changed_stays()
    {
        var settings = new SettingsService(new MemoryStore());
        settings.SetText(SettingsFloaterPlaces.KeyOf("inventory"), "200,150,380,480");
        var m = Manager(new SettingsFloaterPlaces(settings), Screen, Inventory);
        m.Open("inventory");
        Assert.Equal(new UiRect(200, 150, 380, 480), m.RectOf("inventory"));
    }
}

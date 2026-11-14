// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the viewer remembers between openings and between runs, as the official viewer does: the inventory's sort (its
/// InventorySortOrder setting: +1 by date, +2 folders always by name, +4 system folders on top) and every window's place and size
/// (each floater's own rectangle setting, put back on screen when it would be off it). A restart is a new settings service over the
/// same stored text. Pure, runs in parallel.
/// </summary>
public class RememberedViewSettingsTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    private static readonly FloaterSpec Spec = new("inventory", "inventory.title", new UiSize(420, 520), new UiSize(300, 320));
    private static readonly UiRect Screen = new(0, 0, 1920, 1080);

    private static FloaterManager Manager(MemoryStore store, out SettingsFloaterPlaces places, params FloaterSpec[] specs)
    {
        places = new SettingsFloaterPlaces(new SettingsService(store));
        var m = new FloaterManager(places);
        m.SetBounds(Screen);
        foreach (var s in specs.Length == 0 ? new[] { Spec } : specs)
            m.Register(s);
        return m;
    }

    [Fact]
    public void The_inventory_sort_is_kept_across_a_restart_under_the_official_setting()
    {
        var store = new MemoryStore();
        var first = new SettingsService(store);
        Assert.Equal(InventorySort.ByName, InventoryViewPrefs.Load(first));   // the earlier behaviour stays the default
        InventoryViewPrefs.Save(first, InventorySort.ByDate);
        var again = new SettingsService(store);
        Assert.Equal(InventorySort.ByDate, InventoryViewPrefs.Load(again));
        Assert.Equal("InventorySortOrder", Settings.InventorySortOrder.Key);
        Assert.Equal(1, again.Get(Settings.InventorySortOrder) & 1);
        InventoryViewPrefs.Save(again, InventorySort.ByName);
        Assert.Equal(InventorySort.ByName, InventoryViewPrefs.Load(new SettingsService(store)));
    }

    [Fact]
    public void A_window_opens_where_it_was_closed_after_a_restart()
    {
        var store = new MemoryStore();
        var m = Manager(store, out _);
        m.Open("inventory");
        m.Move("inventory", 300, 200);
        m.Resize("inventory", 500, 600);
        m.Close("inventory");
        Assert.True(m.Open("inventory"));
        Assert.Equal(new UiRect(300, 200, 500, 600), m.RectOf("inventory"));   // reopened in the same run

        var after = Manager(store, out _);
        after.Open("inventory");
        Assert.Equal(new UiRect(300, 200, 500, 600), after.RectOf("inventory"));   // and after a restart
    }

    [Fact]
    public void Windows_still_open_at_quit_are_kept_by_saving_the_places()
    {
        var store = new MemoryStore();
        var m = Manager(store, out _);
        m.Open("inventory");
        m.Move("inventory", 640, 120);
        int writes = store.Writes;
        m.SavePlaces();
        Assert.True(store.Writes > writes);
        var after = Manager(store, out _);
        after.Open("inventory");
        Assert.Equal(new UiRect(640, 120, 420, 570), after.RectOf("inventory"));
    }

    [Fact]
    public void Moving_a_window_does_not_write_the_settings_file_on_every_step()
    {
        var store = new MemoryStore();
        var m = Manager(store, out _);
        m.Open("inventory");
        int writes = store.Writes;
        for (int i = 0; i < 50; i++)
            m.Move("inventory", 100 + i, 100);
        Assert.Equal(writes, store.Writes);
        m.Close("inventory");
        Assert.True(store.Writes > writes);
    }

    [Fact]
    public void A_remembered_place_off_the_screen_is_brought_back_on_it()
    {
        var store = new MemoryStore("{\"floater_inventory_rect\":\"5000,-300,400,500\"}");
        var m = Manager(store, out _);
        m.Open("inventory");
        Assert.Equal(new UiRect(1520, 0, 400, 500), m.RectOf("inventory"));   // the remembered size, pulled to the nearest place on the screen
    }

    [Fact]
    public void A_remembered_size_below_the_minimum_opens_at_the_minimum_where_it_was()
    {
        var store = new MemoryStore("{\"floater_inventory_rect\":\"100,150,2,3\"}");
        var m = Manager(store, out _);
        m.Open("inventory");
        Assert.Equal(new UiRect(100, 150, 300, 320), m.RectOf("inventory"));
    }

    [Fact]
    public void A_minimised_main_window_does_not_shrink_the_windows_or_their_remembered_size()
    {
        var store = new MemoryStore();
        var m = Manager(store, out _);
        m.Open("inventory");
        m.Move("inventory", 200, 100);
        m.Resize("inventory", 500, 600);
        m.SetBounds(new UiRect(0, 0, 0, 0));          // minimised: the main view has no size
        m.SetBounds(new UiRect(0, 0, 160, 90));       // a sliver while it is restored
        m.SetBounds(Screen);
        Assert.Equal(new UiRect(200, 100, 500, 600), m.RectOf("inventory"));
        m.SetBounds(new UiRect(0, 0, 0, 0));
        m.Close("inventory");
        var after = Manager(store, out _);
        after.Open("inventory");
        Assert.Equal(new UiRect(200, 100, 500, 600), after.RectOf("inventory"));
    }

    [Fact]
    public void Every_window_keeps_its_own_place()
    {
        var store = new MemoryStore();
        var map = new FloaterSpec("map", "map.title", new UiSize(720, 560), new UiSize(460, 360));
        var m = Manager(store, out _, Spec, map);
        m.Open("inventory");
        m.Open("map");
        m.Move("inventory", 10, 20);
        m.Move("map", 900, 300);
        m.SavePlaces();
        var after = Manager(store, out _, Spec, map);
        after.Open("map");
        after.Open("inventory");
        Assert.Equal(new UiRect(10, 20, 420, 570), after.RectOf("inventory"));
        Assert.Equal(new UiRect(900, 300, 720, 560), after.RectOf("map"));
    }
}

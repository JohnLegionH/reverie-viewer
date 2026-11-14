// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The World > Environment menu: the official viewer's items in its order (Sunrise, Midday, Sunset, Midnight, Use Shared
/// Environment; the menu_viewer.xml of the official viewer), a check mark on the one in force, the choice kept across teleports
/// and (only when the official setting EnvironmentPersistAcrossLogin is on, off by default) across logins, and the F9 key
/// that steps through the same choices. Plain .NET.
/// </summary>
public class EnvironmentMenuTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string t) { Text = t; return true; }
    }

    // ---- submenus and check marks in the menu model

    [Fact]
    public void A_submenu_is_one_entry_of_its_menu_and_holds_its_children_in_order()
    {
        var m = new MenuModel();
        m.AddMenu("world", "menu.world");
        m.AddEntry("world", new MenuEntry("world.map", "menu.world.map", "c0"));
        m.AddSubmenu("world", "world.environment", "menu.world.environment", order: 200);
        m.AddSubEntry("world.environment", new MenuEntry("env.b", "k.b", "c2", Order: 2));
        m.AddSubEntry("world.environment", new MenuEntry("env.a", "k.a", "c1", Order: 1));
        var entries = m.Menus[0].Entries;
        Assert.Equal(["world.map", "world.environment"], entries.Select(e => e.Id));
        Assert.Equal(["env.a", "env.b"], entries.FirstOrDefault(e => e.Id == "world.environment")?.Children?.Select(e => e.Id) ?? []);
    }

    [Fact]
    public void Choosing_a_child_runs_its_command_and_a_disabled_child_runs_nothing()
    {
        var m = new MenuModel(); var cmds = new ShellCommands(); int ran = 0; bool on = true;
        cmds.Register("c1", () => ran++);
        m.AddMenu("world", "menu.world");
        m.AddSubmenu("world", "world.environment", "menu.world.environment");
        m.AddSubEntry("world.environment", new MenuEntry("env.a", "k.a", "c1", () => on));
        Assert.True(m.Activate("env.a", cmds));
        Assert.Equal(1, ran);
        on = false;
        Assert.False(m.Activate("env.a", cmds));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void An_entry_reports_whether_it_is_checked()
    {
        bool c = false;
        var e = new MenuEntry("x", "k", "c", Checked: () => c);
        Assert.False(e.IsChecked);
        c = true;
        Assert.True(e.IsChecked);
        Assert.False(new MenuEntry("y", "k", "c").IsChecked);
    }

    [Fact]
    public void An_entry_id_is_unique_across_menus_and_submenus()
    {
        var m = new MenuModel();
        m.AddMenu("world", "menu.world");
        m.AddEntry("world", new MenuEntry("dup", "k", "c"));
        m.AddSubmenu("world", "world.environment", "menu.world.environment");
        Assert.ThrowsAny<ArgumentException>(() => m.AddSubEntry("world.environment", new MenuEntry("dup", "k", "c")));
    }

    // ---- the choice

    [Fact]
    public void The_choice_starts_at_the_regions_own_environment_and_raises_changed_only_for_a_real_change()
    {
        var c = new EnvironmentChoice(new SettingsService(new MemoryStore()));
        var seen = new List<SkyChoice>();
        c.Changed += seen.Add;
        Assert.Equal(SkyChoice.Region, c.Current);
        c.Set(SkyChoice.Region);
        c.Set(SkyChoice.Midnight);
        c.Set(SkyChoice.Midnight);
        Assert.Equal(SkyChoice.Midnight, c.Current);
        Assert.Equal([SkyChoice.Midnight], seen);
    }

    [Fact]
    public void With_the_switch_after_teleport_setting_on_a_finished_teleport_puts_the_regions_own_environment_back()
    {
        var settings = new SettingsService(new MemoryStore());
        settings.Set(Settings.SwitchToSharedEnvAfterTeleport, true);
        var c = new EnvironmentChoice(settings);
        var seen = new List<SkyChoice>();
        c.Set(SkyChoice.Midnight);
        c.Changed += seen.Add;
        c.TeleportFinished();
        Assert.Equal(SkyChoice.Region, c.Current);
        Assert.Equal([SkyChoice.Region], seen);
        c.TeleportFinished();                                    // already the region's: nothing more to announce
        Assert.Equal([SkyChoice.Region], seen);
    }

    [Fact]
    public void By_default_a_finished_teleport_keeps_the_choice_made_in_the_menu()
    {
        Assert.False(new SettingsService(new MemoryStore()).Get(Settings.SwitchToSharedEnvAfterTeleport));
        var c = new EnvironmentChoice(new SettingsService(new MemoryStore()));
        c.Set(SkyChoice.Sunset);
        c.TeleportFinished();
        Assert.Equal(SkyChoice.Sunset, c.Current);
    }

    [Fact]
    public void The_shortcut_key_steps_through_the_choices_and_back_to_the_region()
    {
        var c = new EnvironmentChoice(new SettingsService(new MemoryStore()));
        var seen = new List<SkyChoice>();
        for (int i = 0; i < 5; i++) { c.Cycle(); seen.Add(c.Current); }
        Assert.Equal([SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.Sunset, SkyChoice.Midnight, SkyChoice.Region], seen);
    }

    [Fact]
    public void By_default_the_choice_is_not_kept_across_logins()
    {
        var store = new MemoryStore();
        new EnvironmentChoice(new SettingsService(store)).Set(SkyChoice.Sunset);
        Assert.Equal(SkyChoice.Region, new EnvironmentChoice(new SettingsService(new MemoryStore(store.Text))).Current);
    }

    [Fact]
    public void With_the_persist_setting_on_the_choice_is_kept_across_logins()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        settings.Set(Settings.EnvironmentPersistAcrossLogin, true);
        new EnvironmentChoice(settings).Set(SkyChoice.Sunset);
        Assert.Equal(SkyChoice.Sunset, new EnvironmentChoice(new SettingsService(new MemoryStore(store.Text))).Current);
    }

    [Fact]
    public void Going_back_to_the_region_clears_the_kept_choice()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        settings.Set(Settings.EnvironmentPersistAcrossLogin, true);
        var c = new EnvironmentChoice(settings);
        c.Set(SkyChoice.Midnight);
        c.Set(SkyChoice.Region);
        Assert.Equal(SkyChoice.Region, new EnvironmentChoice(new SettingsService(new MemoryStore(store.Text))).Current);
    }

    [Fact]
    public void A_kept_choice_that_is_not_a_known_name_is_ignored()
    {
        var settings = new SettingsService(new MemoryStore("{\"EnvironmentPersistAcrossLogin\":true,\"LocalEnvironmentSky\":\"fog\"}"));
        Assert.Equal(SkyChoice.Region, new EnvironmentChoice(settings).Current);
    }

    // ---- the menu

    private static (MenuModel Menu, ShellCommands Cmds, EnvironmentChoice Choice) Installed(Action? personal = null)
    {
        var menu = new MenuModel(); var cmds = new ShellCommands(); var choice = new EnvironmentChoice(new SettingsService(new MemoryStore()));
        menu.AddMenu("world", "menu.world");
        EnvironmentMenu.Install(menu, cmds, choice, personal ?? (() => { }));
        return (menu, cmds, choice);
    }

    [Fact]
    public void The_environment_submenu_has_the_official_items_in_the_official_order_then_personal_lighting()
    {
        var (menu, _, _) = Installed();
        var sub = menu.Menus.FirstOrDefault(m => m.Id == "world")?.Entries.FirstOrDefault(e => e.Id == "world.environment");
        Assert.Equal("menu.world.environment", sub?.LabelKey);
        Assert.Equal(["env.sunrise", "env.midday", "env.legacymidday", "env.sunset", "env.midnight", "env.region", "env.personal", "env.pauseclouds"], sub?.Children?.Select(e => e.Id) ?? []);
        Assert.Equal(["menu.env.sunrise", "menu.env.midday", "menu.env.legacymidday", "menu.env.sunset", "menu.env.midnight", "menu.env.region", "menu.env.personal", "menu.env.pauseclouds"], sub?.Children?.Select(e => e.LabelKey) ?? []);
    }

    [Fact]
    public void The_labels_are_the_official_names()
    {
        var en = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FindEn())).RootElement;
        Assert.Equal("Environment", en.TryGetProperty("menu.world.environment", out var a) ? a.GetString() : null);
        Assert.Equal("Sunrise", en.TryGetProperty("menu.env.sunrise", out var b) ? b.GetString() : null);
        Assert.Equal("Midday", en.TryGetProperty("menu.env.midday", out var c) ? c.GetString() : null);
        Assert.Equal("Sunset", en.TryGetProperty("menu.env.sunset", out var d) ? d.GetString() : null);
        Assert.Equal("Midnight", en.TryGetProperty("menu.env.midnight", out var e) ? e.GetString() : null);
        Assert.Equal("Use Shared Environment", en.TryGetProperty("menu.env.region", out var f) ? f.GetString() : null);
        Assert.Equal("Personal Lighting...", en.TryGetProperty("menu.env.personal", out var g) ? g.GetString() : null);
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

    [Theory]
    [InlineData("env.sunrise", SkyChoice.Sunrise)]
    [InlineData("env.midday", SkyChoice.Midday)]
    [InlineData("env.sunset", SkyChoice.Sunset)]
    [InlineData("env.midnight", SkyChoice.Midnight)]
    public void Choosing_an_item_sets_that_sky_and_checks_it_alone(string id, SkyChoice want)
    {
        var (menu, cmds, choice) = Installed();
        Assert.True(menu.Activate(id, cmds));
        Assert.Equal(want, choice.Current);
        var kids = menu.Menus.First(m => m.Id == "world").Entries.First(e => e.Id == "world.environment").Children ?? [];
        Assert.Equal([id], kids.Where(k => k.IsChecked).Select(k => k.Id));
    }

    [Fact]
    public void Use_shared_environment_goes_back_to_the_region_and_is_checked_when_nothing_is_fixed()
    {
        var (menu, cmds, choice) = Installed();
        menu.Activate("env.midnight", cmds);
        Assert.True(menu.Activate("env.region", cmds));
        Assert.Equal(SkyChoice.Region, choice.Current);
        var kids = menu.Menus.First(m => m.Id == "world").Entries.First(e => e.Id == "world.environment").Children ?? [];
        Assert.Equal(["env.region"], kids.Where(k => k.IsChecked).Select(k => k.Id));
    }

    [Fact]
    public void Personal_lighting_opens_its_window()
    {
        int opened = 0;
        var (menu, cmds, _) = Installed(() => opened++);
        Assert.True(menu.Activate("env.personal", cmds));
        Assert.Equal(1, opened);
    }

    [Fact]
    public void The_cycle_command_is_what_the_f9_key_runs_and_it_moves_the_menu_check()
    {
        var (menu, cmds, choice) = Installed();
        Assert.True(cmds.Execute(EnvironmentMenu.CycleCommand));
        Assert.Equal(SkyChoice.Sunrise, choice.Current);
        var kids = menu.Menus.First(m => m.Id == "world").Entries.First(e => e.Id == "world.environment").Children ?? [];
        Assert.Equal(["env.sunrise"], kids.Where(k => k.IsChecked).Select(k => k.Id));
    }

    [Theory]
    [InlineData('O', "sunrise", SkyChoice.Sunrise)]
    [InlineData('Y', "noon", SkyChoice.Midday)]
    [InlineData('T', "legacy noon", SkyChoice.LegacyMidday)]
    [InlineData('N', "sunset", SkyChoice.Sunset)]
    [InlineData('Z', "midnight", SkyChoice.Midnight)]
    public void Each_control_shift_letter_runs_its_sky_and_the_checkmark_follows(char letter, string officialName, SkyChoice sky)
    {
        Assert.Equal(officialName, EnvironmentMenu.OfficialNameForKey(letter));
        var (menu, cmds, choice) = Installed();
        Assert.True(cmds.Execute(EnvironmentMenu.ShortcutCommand(EnvironmentMenu.OfficialNameForKey(letter)!)));
        Assert.Equal(sky, choice.Current);
        var kids = menu.Menus.First(m => m.Id == "world").Entries.First(e => e.Id == "world.environment").Children ?? [];
        Assert.Single(kids.Where(k => k.IsChecked));
    }

    [Fact]
    public void A_letter_that_is_not_one_of_the_environment_shortcuts_runs_nothing()
    {
        foreach (char letter in "ABCDEFGHIJKLMPQRSUVWX")
            Assert.Null(EnvironmentMenu.OfficialNameForKey(letter));
    }

    [Theory]
    [InlineData("env.sunrise", "sunrise")]
    [InlineData("env.midday", "noon")]
    [InlineData("env.sunset", "sunset")]
    [InlineData("env.midnight", "midnight")]
    [InlineData("env.legacymidday", "legacy noon")]
    public void The_official_shortcuts_run_the_same_commands(string id, string officialName)
    {
        // control+shift+O, Y, N, Z and T (Midday (Legacy)) in the official menu_viewer.xml
        var (_, cmds, choice) = Installed();
        Assert.True(cmds.Has(EnvironmentMenu.ShortcutCommand(officialName)));
        cmds.Execute(EnvironmentMenu.ShortcutCommand(officialName));
        Assert.Equal(SkyChoices.TryParse(officialName, out var want) ? want : SkyChoice.Region, choice.Current);
        Assert.NotNull(id);
    }
}

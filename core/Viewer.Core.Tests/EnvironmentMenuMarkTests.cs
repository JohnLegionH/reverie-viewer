// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// World > Environment as the dropdown draws it: only the sky in use shows a check mark, whichever sky that is, and the choice kept for the
/// next login is the one made last until the region's shared environment is chosen. Plain .NET; every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class EnvironmentMenuMarkTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string t) { Text = t; return true; }
    }

    private static readonly string[] SkyIds = ["env.sunrise", "env.midday", "env.legacymidday", "env.sunset", "env.midnight", "env.region"];

    /// <summary>The ids of the Environment sub-entries whose dropdown row shows a tick, as the top bar builds the row.</summary>
    private static string[] TickedRows(MenuModel menu, ShellCommands cmds)
    {
        var world = TopBarMenuMap.Build(menu, cmds).First(m => m.Id == "world");
        var kids = world.Entries.First(e => e.Id == "world.environment").Children!;
        return kids.Where(k => SkyIds.Contains(k.Id))
            .Where(k => MenuMarks.For(k.Icon, k.Checked) is { Icon: { } icon } && icon == LucideIcons.Check)
            .Select(k => k.Id).ToArray();
    }

    private static (MenuModel Menu, ShellCommands Cmds, EnvironmentChoice Choice) Installed(SettingsService? settings = null)
    {
        var menu = new MenuModel();
        var cmds = new ShellCommands();
        var choice = new EnvironmentChoice(settings ?? new SettingsService(new MemoryStore()));
        menu.AddMenu("world", "menu.world");
        EnvironmentMenu.Install(menu, cmds, choice, () => { });
        return (menu, cmds, choice);
    }

    [Theory]
    [InlineData("env.sunrise")]
    [InlineData("env.midday")]
    [InlineData("env.legacymidday")]
    [InlineData("env.sunset")]
    [InlineData("env.midnight")]
    public void Only_the_sky_in_use_shows_a_tick_in_its_row(string id)
    {
        var (menu, cmds, _) = Installed();
        Assert.True(menu.Activate(id, cmds));
        Assert.Equal([id], TickedRows(menu, cmds));
    }

    [Fact]
    public void With_the_region_s_own_environment_in_force_only_Use_Shared_Environment_shows_a_tick()
    {
        var (menu, cmds, _) = Installed();
        Assert.Equal(["env.region"], TickedRows(menu, cmds));
    }

    [Fact]
    public void Choosing_Midday_Legacy_leaves_its_own_tick_and_no_other()
    {
        var (menu, cmds, choice) = Installed();
        menu.Activate("env.midday", cmds);
        menu.Activate("env.legacymidday", cmds);
        Assert.Equal(SkyChoice.LegacyMidday, choice.Current);
        Assert.Equal(["env.legacymidday"], TickedRows(menu, cmds));
    }

    [Fact]
    public void A_row_with_an_icon_of_its_own_keeps_it_and_is_a_check_item()
    {
        Assert.Equal(new MenuMark(LucideIcons.Gestures, true), MenuMarks.For(LucideIcons.Gestures, false));
        Assert.Equal(new MenuMark(LucideIcons.Gestures, true), MenuMarks.For(LucideIcons.Gestures, true));
        Assert.Equal(new MenuMark(LucideIcons.Gestures, false), MenuMarks.For(LucideIcons.Gestures, null));
        Assert.Equal(new MenuMark(null, false), MenuMarks.For(null, null));
    }

    [Fact]
    public void The_sky_picked_is_remembered_by_default_across_logins()
    {
        var store = new MemoryStore();
        var (menu, cmds, _) = Installed(new SettingsService(store));
        menu.Activate("env.legacymidday", cmds);
        var next = new EnvironmentChoice(new SettingsService(new MemoryStore(store.Text)));
        Assert.Equal(SkyChoice.LegacyMidday, next.Current);
    }

    [Fact]
    public void Choosing_the_shared_environment_ends_the_remembered_sky()
    {
        var store = new MemoryStore();
        var (menu, cmds, _) = Installed(new SettingsService(store));
        menu.Activate("env.midnight", cmds);
        menu.Activate("env.region", cmds);
        Assert.Equal(SkyChoice.Region, new EnvironmentChoice(new SettingsService(new MemoryStore(store.Text))).Current);
    }
}

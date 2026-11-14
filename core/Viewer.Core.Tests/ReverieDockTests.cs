// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Reverie top bar's menus as the running viewer fills them, the dock's contents and rules (built-in buttons, add-on items, the
/// names shown under the buttons) and the context slot's choice. Pure models; every member builds its own objects, so the class
/// runs in parallel.
/// </summary>
public class ReverieDockTests
{
    private static MenuModel Menus(out ShellCommands commands)
    {
        commands = new ShellCommands();
        foreach (var c in new[] { "app.quit", "floater.about", "floater.preferences", "floater.addons", "app.check_updates", "app.logout",
                     "floater.teleport", "travel.home", "travel.set_home", "floater.map", "floater.minimap", "floater.chat", "floater.inventory" })
            commands.Register(c, () => { });
        var m = new MenuModel();
        m.AddMenu("me", "menu.me");
        m.AddEntry("me", new MenuEntry("me.quit", "menu.me.quit", "app.quit", Order: 1000));
        m.AddEntry("me", new MenuEntry("me.preferences", "menu.me.preferences", "floater.preferences", Order: 10));
        m.AddMenu("help", "menu.help");
        m.AddEntry("help", new MenuEntry("help.about", "menu.help.about", "floater.about"));
        m.AddMenu("world", "menu.world");
        m.AddEntry("world", new MenuEntry("world.teleport", "menu.world.teleport", "floater.teleport"));
        m.AddEntry("world", new MenuEntry("world.home", "menu.world.home", "travel.home"));
        m.AddEntry("world", new MenuEntry("world.set_home", "menu.world.set_home", "travel.set_home"));
        m.AddEntry("world", new MenuEntry("world.map", "menu.world.map", "floater.map"));
        m.AddEntry("world", new MenuEntry("world.minimap", "menu.world.minimap", "floater.minimap"));
        m.AddMenu("communicate", "menu.communicate");
        m.AddEntry("communicate", new MenuEntry("communicate.chat", "menu.communicate.chat", "floater.chat"));
        m.AddMenu("content", "menu.content");
        m.AddEntry("content", new MenuEntry("content.inventory", "menu.content.inventory", "floater.inventory"));
        m.AddMenu("build", "menu.build");
        m.AddEntry("build", new MenuEntry("build.create", "menu.build.create", "build.create"));
        return m;
    }

    // ---------------- the top bar's menus ----------------

    [Fact]
    public void The_app_menu_comes_first_with_about_preferences_addons_updates_log_out_and_quit()
    {
        var menus = TopBarMenuMap.Build(Menus(out var commands), commands);
        Assert.Equal(TopBarMenuMap.AppMenuId, menus[0].Id);
        Assert.True(menus[0].IsApp);
        Assert.Equal("topbar.app", menus[0].TitleKey);
        Assert.Equal(new[] { "menu.app.about", "menu.app.preferences", "menu.app.addons", "menu.app.updates", "menu.app.logout", "menu.app.quit" },
            menus[0].Entries.Select(e => e.LabelKey));
        Assert.Equal(new[] { "floater.about", "floater.preferences", "floater.addons", "app.check_updates", "app.logout", "app.quit" },
            menus[0].Entries.Select(e => e.CommandId));
        Assert.All(menus[0].Entries, e => Assert.True(e.Enabled));
    }

    [Fact]
    public void The_official_menus_follow_in_their_order_with_every_entry_the_model_holds()
    {
        var model = Menus(out var commands);
        var menus = TopBarMenuMap.Build(model, commands);
        Assert.Equal(new[] { "app", "me", "communicate", "world", "build", "content", "help" }, menus.Select(m => m.Id));
        foreach (var def in model.Menus)
            Assert.Equal(def.Entries.Select(e => e.Id), menus.Single(m => m.Id == def.Id).Entries.Select(e => e.Id));
        // an entry whose command is not there is shown disabled, not dropped
        Assert.False(menus.Single(m => m.Id == "build").Entries.Single().Enabled);
    }

    [Fact]
    public void Every_world_entry_has_an_icon_from_the_vendored_set()
    {
        var menus = TopBarMenuMap.Build(Menus(out var commands), commands);
        foreach (var e in menus.Single(m => m.Id == "world").Entries)
        {
            Assert.NotNull(e.Icon);
            Assert.Contains(e.Icon, LucideIcons.All);
        }
        foreach (var id in new[] { "communicate.chat", "communicate.conversations", "communicate.people", "communicate.notifications", "content.inventory",
                     "content.outfits", "me.appearance", "me.addons", "build.create" })
            Assert.Contains(TopBarMenuMap.IconFor(id), LucideIcons.All);
    }

    // ---------------- the dock ----------------

    [Fact]
    public void The_standard_dock_has_the_spec_groups_buttons_and_icons_in_order()
    {
        var dock = DockModel.Standard();
        Assert.Equal(new[] { "talk", "world", "me", "make", "alerts" }, dock.Groups.Select(g => g.Group));
        Assert.Equal(new[] { "chat", "convos", "people", "teleport", "map", "minimap", "inventory", "outfits", "appearance", "build", "notices" },
            dock.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        Assert.Equal(new[] { LucideIcons.Chat, LucideIcons.Conversations, LucideIcons.People, LucideIcons.Teleport, LucideIcons.Map, LucideIcons.MiniMap,
            LucideIcons.Inventory, LucideIcons.Outfits, LucideIcons.Appearance, LucideIcons.Build, LucideIcons.Notices },
            dock.Groups.SelectMany(g => g.Items).Select(i => i.Icon));
        Assert.All(dock.Groups.SelectMany(g => g.Items), i => Assert.NotNull(i.CommandId));
    }

    [Fact]
    public void Names_under_buttons_follow_the_spec_for_plain_toggle_and_attention()
    {
        var en = StringTable.English();
        var dock = DockModel.Standard();
        Assert.Equal("Chat", dock.Item("chat")!.Shown(en));
        Assert.Equal("Convos", dock.Item("convos")!.Shown(en));
        Assert.Equal("Mini-map", dock.Item("minimap")!.Shown(en));
        Assert.Equal("Notices", dock.Item("notices")!.Shown(en));
        dock.SetAttention(dock.Item("notices")!, true, 3);
        Assert.Equal("3 notices", dock.Item("notices")!.Shown(en));
        Assert.True(dock.Item("notices")!.Attention);
        var ao = dock.AddAddonItem("ao", "use", "AO", DockItemKind.ToggleWithMenu, LucideIcons.Overrider)!;
        Assert.Equal("AO · Off", ao.Shown(en));
        dock.SetOn(ao, true);
        Assert.Equal("AO · On", ao.Shown(en));
    }

    [Fact]
    public void Addon_items_go_in_the_make_group_after_build_in_the_order_added_two_at_most_and_labels_are_cut()
    {
        var dock = DockModel.Standard();
        int changes = 0;
        dock.Changed += () => changes++;
        Assert.NotNull(dock.AddAddonItem("first", "a", "One", DockItemKind.Action));
        Assert.NotNull(dock.AddAddonItem("second", "b", "Two", DockItemKind.Toggle, "not-an-icon"));
        Assert.NotNull(dock.AddAddonItem("first", "c", "A very long label", DockItemKind.Action));
        Assert.Null(dock.AddAddonItem("first", "d", "Three", DockItemKind.Action));   // a third item of one add-on is refused
        Assert.Null(dock.AddAddonItem("second", "b", "Again", DockItemKind.Action));   // the same id again is refused
        var make = dock.Groups.Single(g => g.Group == "make").Items;
        Assert.Equal(new[] { "build", "a", "b", "c" }, make.Select(i => i.Id));
        Assert.Equal("A very lo…", make[3].Label);
        Assert.True(make[3].Label.Length <= DockModel.LabelMax);
        Assert.Equal(LucideIcons.AddOn, make[1].Icon);   // no icon given
        Assert.Equal(LucideIcons.AddOn, make[2].Icon);   // a name the set does not have
        Assert.Equal(3, changes);
    }

    [Fact]
    public void An_addon_turned_off_takes_its_items_with_it_at_once_and_one_item_can_be_removed()
    {
        var dock = DockModel.Standard();
        dock.AddAddonItem("first", "a", "One", DockItemKind.Action);
        dock.AddAddonItem("first", "b", "Two", DockItemKind.Action);
        dock.AddAddonItem("second", "c", "Three", DockItemKind.Action);
        Assert.True(dock.RemoveAddonItem("second", "c"));
        Assert.False(dock.RemoveAddonItem("second", "c"));
        Assert.Equal(2, dock.RemoveAddon("first"));
        Assert.Equal(new[] { "build" }, dock.Groups.Single(g => g.Group == "make").Items.Select(i => i.Id));
        Assert.Null(dock.Item("a", "first"));
    }

    [Fact]
    public void An_item_keeps_its_menu_and_state_as_the_addon_sets_them()
    {
        var dock = DockModel.Standard();
        var item = dock.AddAddonItem("ao", "use", "AO", DockItemKind.ToggleWithMenu, menu: new[] { new DockMenuEntry("Set one", Checked: true) })!;
        Assert.Single(item.Menu);
        dock.SetMenu(item, new[] { new DockMenuEntry("Set one"), new DockMenuEntry("", Separator: true), new DockMenuEntry("Edit sets...") });
        Assert.Equal(3, item.Menu.Count);
        dock.SetAttention(item, true);
        Assert.True(item.Attention);
        dock.SetAttention(item, false);
        Assert.False(item.Attention);
        Assert.Same(item, dock.Item("use", "ao"));
    }

    // ---------------- the context slot ----------------

    [Fact]
    public void The_context_slot_shows_stand_up_then_stop_flying_then_back_to_for_a_minute_after_a_teleport()
    {
        Assert.Equal(ContextSlotKind.StandUp, ContextSlot.Pick(sitting: true, flying: true, secondsSinceTeleport: 1, hasPrevious: true));
        Assert.Equal(ContextSlotKind.StopFlying, ContextSlot.Pick(false, true, 1, true));
        Assert.Equal(ContextSlotKind.BackTo, ContextSlot.Pick(false, false, 59.9, true));
        Assert.Equal(ContextSlotKind.None, ContextSlot.Pick(false, false, 60.1, true));
        Assert.Equal(ContextSlotKind.None, ContextSlot.Pick(false, false, 1, false));
    }
}

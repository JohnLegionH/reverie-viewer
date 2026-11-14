// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The menu bar's order is the design's, whatever order feature code adds its menus in: Me, Communicate, World, Build,
/// Content, Help. A menu with no place in that list follows, in the order it was added. Within a menu an entry with a
/// lower order comes first and entries of equal order keep the order they were added in. Plain .NET, every member independent.
/// </summary>
public class MenuOrderTests
{
    [Fact]
    public void Menus_are_listed_in_the_order_of_the_design_whatever_order_they_were_added_in()
    {
        var m = new MenuModel();
        foreach (var id in new[] { "help", "content", "world", "communicate", "me", "build" })
            m.AddMenu(id, "menu." + id);
        Assert.Equal(["me", "communicate", "world", "build", "content", "help"], m.Menus.Select(x => x.Id));
    }

    [Fact]
    public void A_menu_with_no_place_in_the_design_follows_the_others_in_the_order_it_was_added()
    {
        var m = new MenuModel();
        foreach (var id in new[] { "zeta", "help", "alpha", "me" })
            m.AddMenu(id, "menu." + id);
        Assert.Equal(["me", "help", "zeta", "alpha"], m.Menus.Select(x => x.Id));
    }

    [Fact]
    public void Entries_with_a_lower_order_come_first_and_equal_orders_keep_their_added_order()
    {
        var m = new MenuModel();
        m.AddMenu("me", "menu.me");
        m.AddEntry("me", new MenuEntry("me.quit", "menu.me.quit", "c1", Order: 1000));
        m.AddEntry("me", new MenuEntry("me.b", "menu.me.b", "c2"));
        m.AddEntry("me", new MenuEntry("me.a", "menu.me.a", "c3"));
        m.AddEntry("me", new MenuEntry("me.first", "menu.me.first", "c4", Order: 1));
        Assert.Equal(["me.first", "me.b", "me.a", "me.quit"], m.Menus[0].Entries.Select(e => e.Id));
    }
}

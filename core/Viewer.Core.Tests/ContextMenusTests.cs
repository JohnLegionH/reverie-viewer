// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Interaction;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Which entries each right-click menu has and which are enabled. Pure data; independent members, run in parallel.</summary>
public class ContextMenusTests
{
    private static PrimInteraction I(byte action = 0, uint flags = 0) => new(action, flags);
    private static string[] Ids(IReadOnlyList<ContextEntry> e) => e.Select(x => x.Id).ToArray();
    private static ContextEntry Entry(IReadOnlyList<ContextEntry> e, string id) => e.Single(x => x.Id == id);

    [Fact]
    public void An_object_menu_lists_touch_sit_open_inspect_and_take_in_that_order()
    {
        var m = ContextMenus.ForObject(new(I(), I(), false, false, false));
        Assert.Equal(["touch", "sit", "open", "inspect", "take", "delete", "attach", "attach_hud", "block", "return"], Ids(m));
    }

    [Fact]
    public void Touch_is_enabled_only_for_an_object_with_a_touch_handler()
    {
        Assert.False(Entry(ContextMenus.ForObject(new(I(), I(), false, false, false)), "touch").Enabled);
        Assert.True(Entry(ContextMenus.ForObject(new(I(flags: ObjectFlagBits.HandleTouch), I(), false, false, false)), "touch").Enabled);
        Assert.True(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.HandleTouch), false, false, false)), "touch").Enabled);
    }

    [Fact]
    public void Sit_turns_into_stand_while_seated_on_that_object()
    {
        var seated = ContextMenus.ForObject(new(I(), I(), false, SittingOnIt: true, Sitting: true));
        Assert.Contains("stand", Ids(seated));
        Assert.DoesNotContain("sit", Ids(seated));
    }

    [Fact]
    public void Sit_stays_available_while_seated_on_something_else()
    {
        var m = ContextMenus.ForObject(new(I(), I(), false, SittingOnIt: false, Sitting: true));
        Assert.True(Entry(m, "sit").Enabled);
    }

    [Fact]
    public void Open_follows_the_open_rule()
    {
        Assert.False(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.InventoryEmpty | ObjectFlagBits.YouOwner), false, false, false)), "open").Enabled);
        Assert.True(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.YouOwner), false, false, false)), "open").Enabled);
    }

    [Fact]
    public void Take_needs_ownership_or_modify_rights_and_is_not_offered_for_an_attachment()
    {
        Assert.False(Entry(ContextMenus.ForObject(new(I(), I(), false, false, false)), "take").Enabled);
        Assert.True(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.YouOwner), false, false, false)), "take").Enabled);
        Assert.False(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.Modify), false, false, false)), "take").Enabled);   // modify alone: not without transfer (llviewermenu.cpp enable_take)
        Assert.True(Entry(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.Modify | ObjectFlagBits.Transfer), false, false, false)), "take").Enabled);
        Assert.DoesNotContain("take", Ids(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.YouOwner), true, false, false))));
    }

    [Fact]
    public void Take_copy_appears_only_when_the_copy_permission_is_there()
    {
        Assert.DoesNotContain("take_copy", Ids(ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.YouOwner), false, false, false))));
        var m = ContextMenus.ForObject(new(I(), I(flags: ObjectFlagBits.YouOwner | ObjectFlagBits.Copy), false, false, false));
        Assert.Equal("take_copy", Ids(m)[Ids(m).ToList().IndexOf("take") + 1]);
        Assert.True(Entry(m, "take_copy").Enabled);
    }

    [Fact]
    public void An_avatar_menu_has_im_add_friend_offer_and_request_teleport_and_inspect()
    {
        Assert.Equal(["im", "add_friend", "offer_teleport", "request_teleport", "inspect", "block"], Ids(ContextMenus.ForAvatar(new(IsSelf: false, IsFriend: false))));
    }

    [Fact]
    public void Add_friend_is_not_offered_for_someone_who_is_already_a_friend()
    {
        Assert.DoesNotContain("add_friend", Ids(ContextMenus.ForAvatar(new(false, true))));
    }

    [Fact]
    public void Your_own_avatar_has_no_social_entries_only_the_appearance_window()
    {
        Assert.Equal(["appearance"], Ids(ContextMenus.ForAvatar(new(IsSelf: true, IsFriend: false))));
        Assert.Equal(["appearance"], Ids(ContextMenus.ForAvatar(new(IsSelf: true, IsFriend: true))));
    }

    [Fact]
    public void A_land_menu_has_the_official_items_Reverie_can_do_in_the_official_order()
    {
        var m = ContextMenus.ForLand(new(PointKnown: true, WithinSitReach: true));
        // the official ground menu (menu_land.xml): About Land, Sit Here, Buy This Land, Buy Pass, Build, Edit Terrain, Block Particle Owner;
        // the ones Reverie cannot do yet (buying land, terrain, blocking a particle owner) are left out
        Assert.Equal(["about_land", "sit_here", "build"], Ids(m));
        Assert.True(Entry(m, "sit_here").Enabled);
    }

    [Fact]
    public void Sit_here_on_land_is_greyed_out_out_of_reach_or_without_a_point()
    {
        Assert.False(Entry(ContextMenus.ForLand(new(true, false)), "sit_here").Enabled);
        Assert.False(Entry(ContextMenus.ForLand(new(false, true)), "sit_here").Enabled);
        Assert.True(Entry(ContextMenus.ForLand(new(false, true)), "about_land").Enabled);
    }
}

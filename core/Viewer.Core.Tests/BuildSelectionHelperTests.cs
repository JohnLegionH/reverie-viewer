// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Select Only My Objects, Select Next Part (Ctrl+.) and Select Previous Part (Ctrl+,) with their menu entries, and taking the selection into
/// a chosen folder one object at a time (Firestorm's Z-take). In-memory world; each test builds its own, so the class runs in parallel.
/// </summary>
public class BuildSelectionHelperTests
{
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    [Fact]
    public void Select_Only_My_Objects_passes_over_others_objects_on_a_click_and_a_drag_select()
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 20, 30), ObjectFlagBits.Move | ObjectFlagBits.Modify);   // someone else's, modifiable
        r.C.Click(r.At(20, 20), shift: false);
        Assert.Equal([2u], r.C.Selection);   // off: anything may be selected
        r.C.SelectOwnedOnly = true;
        r.C.Click(r.At(20, 20), shift: false);
        Assert.Empty(r.C.Selection);
        r.C.Click(r.At(10, 10), shift: false);
        Assert.Equal([1u], r.C.Selection);
        r.C.Click(r.At(20, 20), shift: true);
        Assert.Equal([1u], r.C.Selection);
        r.C.SelectMany([1u, 2u], shift: false);
        Assert.Equal([1u], r.C.Selection);
    }

    [Fact]
    public void Next_and_previous_part_go_round_the_linkset_in_link_order_with_Edit_Linked_on()
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(10, new Vector3(10, 10, 30), Own);
        r.Prim(12, new Vector3(0, 6, 0), Own, parent: 10);
        r.Prim(11, new Vector3(0, -6, 0), Own, parent: 10);
        r.Select(10, 10);
        Assert.False(r.C.CanSelectNextPart);   // Edit Linked is off
        Assert.False(r.C.SelectNextPart(true));
        Assert.Equal([10u], r.C.Selection);
        r.C.EditLinked = true;
        Assert.True(r.C.CanSelectNextPart);
        Assert.True(r.C.SelectNextPart(true));
        Assert.Equal([11u], r.C.Selection);
        Assert.Contains(r.Sent.OfType<DeselectObjectsIntent>(), d => d.LocalIds.Contains(10u));
        Assert.True(r.C.SelectNextPart(true));
        Assert.Equal([12u], r.C.Selection);
        Assert.True(r.C.SelectNextPart(true));
        Assert.Equal([10u], r.C.Selection);   // round to the root
        Assert.True(r.C.SelectNextPart(false));
        Assert.Equal([12u], r.C.Selection);
    }

    [Fact]
    public void With_several_parts_selected_next_takes_the_first_in_link_order_and_previous_the_last()
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(10, new Vector3(10, 10, 30), Own);
        r.Prim(11, new Vector3(0, -6, 0), Own, parent: 10);
        r.Prim(12, new Vector3(0, 6, 0), Own, parent: 10);
        r.C.EditLinked = true;
        r.C.SelectMany([12u, 11u], shift: false);
        Assert.True(r.C.SelectNextPart(true));
        Assert.Equal([11u], r.C.Selection);
        r.C.SelectMany([11u, 12u], shift: false);
        Assert.True(r.C.SelectNextPart(false));
        Assert.Equal([12u], r.C.Selection);
    }

    [Fact]
    public void The_Build_menu_has_Select_Next_and_Previous_Part_on_Ctrl_dot_and_Ctrl_comma_Select_Only_My_Objects_in_Options_and_Take_Into_Folder()
    {
        var menus = new MenuModel();
        var r = new BuildFeaturesTabTests.Rig();
        BuildMenu.InstallEditing(menus, () => true, () => r.C, () => r.C.Selection.Count > 0);
        BuildMenu.InstallSelectionHelpers(menus, () => true, () => r.C);
        var next = menus.ForShortcut("control|.");
        var previous = menus.ForShortcut("control|,");
        Assert.Equal(BuildMenu.NextPartCommand, next?.CommandId);
        Assert.Equal(BuildMenu.PreviousPartCommand, previous?.CommandId);
        var build = menus.Menus.Single(m => m.Id == BuildMenu.MenuId);
        var order = build.Entries.Select(e => e.Id).ToList();
        Assert.True(order.IndexOf(BuildMenu.EditLinkedId) < order.IndexOf(BuildMenu.NextPartId));
        Assert.True(order.IndexOf(BuildMenu.NextPartId) < order.IndexOf(BuildMenu.PreviousPartId));
        Assert.True(order.IndexOf(BuildMenu.PreviousPartId) < order.IndexOf(BuildMenu.LinkId));
        Assert.False(next!.IsEnabled);   // nothing selected
        var options = build.Entries.Single(e => e.Id == BuildMenu.OptionsId).Children!;
        var owned = options.Single(e => e.Id == BuildMenu.OwnedOnlyId);
        Assert.False(owned.IsChecked);
        r.C.SelectOwnedOnly = true;
        Assert.True(owned.IsChecked);
        var take = build.Entries.Single(e => e.Id == BuildMenu.TakeIntoFolderId);
        Assert.False(take.IsEnabled);
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Select(10, 10);
        Assert.True(take.IsEnabled);
        r.C.EditLinked = true;
        Assert.True(next.IsEnabled);
    }

    [Fact]
    public void Take_Into_Folder_takes_each_selected_root_into_the_folder_one_per_pump_and_leaves_what_may_not_be_taken()
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 20, 30), Own);
        r.Prim(3, new Vector3(30, 30, 30), ObjectFlagBits.Move);   // not ours, not modify and transfer: may not be taken
        r.Prim(21, new Vector3(0, 3, 0), Own, parent: 2);
        r.C.EditLinked = true;
        r.Select(10, 10);
        r.C.SelectMany([21u, 3u], shift: true);   // a part of 2 counts as 2
        var folder = G(500);
        Assert.Equal(2, r.C.TakeIntoFolder(folder));
        Assert.Equal(2, r.C.Pending);
        Assert.True(r.C.PumpQueues());
        Assert.Equal(new TakeIntoFolderIntent(1, folder), r.Last<TakeIntoFolderIntent>());
        Assert.True(r.C.PumpQueues());
        Assert.Equal([1u, 2u], r.Sent.OfType<TakeIntoFolderIntent>().Select(t => t.LocalId));
        Assert.False(r.C.PumpQueues());
        Assert.Equal([3u], r.C.Selection);   // the taken ones leave the selection
        Assert.Equal(0, r.C.TakeIntoFolder(Guid.Empty));
    }

    [Fact]
    public void Take_into_a_folder_is_DeRezObject_taking_into_the_agents_inventory_with_that_folder()
    {
        var m = Viewer.Core.Protocol.LmvBuild.DeRez(new LibreMetaverse.UUID(G(1)), new LibreMetaverse.UUID(G(2)), 7,
            Viewer.Core.Building.BuildRules.DeRezTakeIntoInventory, new LibreMetaverse.UUID(G(500)), LibreMetaverse.UUID.Zero);
        Assert.Equal((Viewer.Core.Building.BuildRules.DeRezTakeIntoInventory, new LibreMetaverse.UUID(G(500))), (m.AgentBlock.Destination, m.AgentBlock.DestinationID));
        var sent = Assert.IsType<LibreMetaverse.Packets.DeRezObjectPacket>(Viewer.Core.Protocol.LmvFeatures.Packet(new LibreMetaverse.UUID(G(1)), new LibreMetaverse.UUID(G(2)),
            LibreMetaverse.UUID.Zero, new TakeIntoFolderIntent(7, G(500))));
        Assert.Equal((Viewer.Core.Building.BuildRules.DeRezTakeIntoInventory, new LibreMetaverse.UUID(G(500)), 7u),
            (sent.AgentBlock.Destination, sent.AgentBlock.DestinationID, sent.ObjectData[0].ObjectLocalID));
    }
}

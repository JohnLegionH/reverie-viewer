// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The Build window's Content tab: the prim's items as the region lists them, New Script, an item or a whole folder dropped in (the folder
/// one item at a time, as Firestorm's Z-drop), Delete and Copy to inventory, and the messages New Script and Delete become. In-memory world;
/// each test builds its own, so the class runs in parallel.
/// </summary>
public class BuildContentTabTests
{
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly UUID Agent = new("1a2b3c4d-0000-4000-8000-000000000001"), Session = new("1a2b3c4d-0000-4000-8000-000000000002");

    private static TaskItem Item(uint n, string name, int type = 7) => new(G(n), name, type, type, Guid.Empty, 0x7FFFFFFF, 0);

    private static BuildFeaturesTabTests.Rig Selected(uint flags = Own)
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), flags);
        r.Select(10, 10);
        return r;
    }

    [Fact]
    public void The_contents_are_asked_for_and_listed_by_name_once_the_region_answers_for_the_selected_prim()
    {
        var r = Selected();
        Assert.False(r.C.ContentsKnown);
        Assert.True(r.C.RequestContents());
        Assert.Equal(new RequestContentsIntent(1, G(1)), r.Last<RequestContentsIntent>());
        r.C.Receive(new TaskInventoryReceived(G(9), [Item(90, "other")]));   // another object's answer
        Assert.False(r.C.ContentsKnown);
        r.C.Receive(new TaskInventoryReceived(G(1), [Item(11, "beta"), Item(12, "Alpha"), Item(13, "gamma")]));
        Assert.True(r.C.ContentsKnown);
        Assert.Equal(["Alpha", "beta", "gamma"], r.C.Contents.Select(i => i.Name));
    }

    [Fact]
    public void A_card_saved_into_the_prim_takes_its_new_asset_and_the_contents_are_read_again()
    {
        // llpreviewnotecard.cpp finishTaskUpload: the window takes the new asset id from the upload's answer and reads the card again
        var r = Selected();
        r.C.Receive(new TaskInventoryReceived(G(1), [Item(11, "card") with { AssetId = G(50) }, Item(12, "other") with { AssetId = G(60) }]));
        int sent = r.Sent.Count;
        Assert.True(r.C.ItemSaved(G(1), G(11), G(51)));
        Assert.Equal(G(51), r.C.ContentItem(G(11))!.AssetId);
        Assert.Equal(G(60), r.C.ContentItem(G(12))!.AssetId);
        Assert.Equal(new RequestContentsIntent(1, G(1)), Assert.IsType<RequestContentsIntent>(r.Sent[^1]));
        Assert.True(r.Sent.Count > sent);
        // a save into another object changes nothing here
        Assert.False(r.C.ItemSaved(G(9), G(11), G(52)));
        Assert.Equal(G(51), r.C.ContentItem(G(11))!.AssetId);
    }

    [Fact]
    public void An_answer_with_nothing_in_it_shows_an_empty_list_even_one_the_region_could_not_read()
    {
        var r = Selected();
        r.C.Receive(new TaskInventoryReceived(G(1), [Item(11, "card")]));
        Assert.Equal(["card"], r.C.Contents.Select(i => i.Name));
        // after the last item is deleted the region has no contents file for the prim, and the read ends with nothing
        r.C.Receive(new TaskInventoryReceived(G(1), [], Ok: false));
        Assert.True(r.C.ContentsKnown);
        Assert.Empty(r.C.Contents);
    }

    [Fact]
    public void New_Script_goes_into_a_prim_that_may_be_modified_and_the_list_is_asked_for_again()
    {
        var r = Selected();
        Assert.True(r.C.NewScript("New Script"));
        Assert.Contains(new NewScriptIntent(1, G(1), "New Script"), r.Sent);
        Assert.IsType<RequestContentsIntent>(r.Sent[^1]);
        var ro = Selected(ObjectFlagBits.Move);
        Assert.False(ro.C.NewScript("New Script"));
        Assert.DoesNotContain(ro.Sent, i => i is NewScriptIntent);
    }

    [Fact]
    public void An_item_dropped_on_the_tab_goes_into_the_prim_a_script_as_a_script()
    {
        var r = Selected();
        Assert.True(r.C.DropItem(G(50), script: false));
        Assert.True(r.C.DropItem(G(51), script: true));
        Assert.Equal([new GiveItemToObjectIntent(1, G(50), false), new GiveItemToObjectIntent(1, G(51), true)], r.Sent.OfType<GiveItemToObjectIntent>());
        var ro = Selected(ObjectFlagBits.Move);
        Assert.False(ro.C.DropItem(G(50), script: false));
    }

    [Fact]
    public void A_dropped_folder_waits_in_a_queue_and_goes_in_one_item_per_pump_in_its_order()
    {
        var r = Selected();
        Assert.Equal(3, r.C.DropFolder([(G(60), false), (G(61), true), (G(62), false)]));
        Assert.Equal(3, r.C.Pending);
        Assert.DoesNotContain(r.Sent, i => i is GiveItemToObjectIntent);
        Assert.True(r.C.PumpQueues());
        Assert.Equal(new GiveItemToObjectIntent(1, G(60), false), Assert.Single(r.Sent.OfType<GiveItemToObjectIntent>()));
        Assert.True(r.C.PumpQueues());
        Assert.True(r.C.PumpQueues());
        Assert.Equal([G(60), G(61), G(62)], r.Sent.OfType<GiveItemToObjectIntent>().Select(g => g.ItemId));
        Assert.True(r.Sent.OfType<GiveItemToObjectIntent>().ElementAt(1).Script);
        Assert.Equal(0, r.C.Pending);
        Assert.False(r.C.PumpQueues());
    }

    [Fact]
    public void Delete_and_Copy_to_inventory_name_a_listed_item_and_an_unknown_item_sends_nothing()
    {
        var r = Selected();
        r.C.Receive(new TaskInventoryReceived(G(1), [Item(11, "card", type: 7), Item(12, "tex", type: 0)]));
        Assert.True(r.C.CopyItemToInventory(G(12)));
        Assert.Equal(new CopyObjectItemIntent(1, G(12), 0), r.Last<CopyObjectItemIntent>());
        Assert.True(r.C.DeleteItem(G(11)));
        Assert.Contains(new RemoveObjectItemIntent(1, G(11)), r.Sent);
        int sent = r.Sent.Count;
        Assert.False(r.C.DeleteItem(G(99)));
        Assert.False(r.C.CopyItemToInventory(G(99)));
        Assert.Equal(sent, r.Sent.Count);
    }

    [Fact]
    public void New_Script_is_RezScript_with_no_item_the_prim_as_its_folder_and_the_script_types_and_Delete_is_RemoveTaskInventory()
    {
        var m = LmvFeatures.NewScript(Agent, Session, UUID.Zero, 7, new UUID(G(1)), "New Script");
        Assert.Equal((7u, true), (m.UpdateBlock.ObjectLocalID, m.UpdateBlock.Enabled));
        Assert.Equal(UUID.Zero, m.InventoryBlock.ItemID);
        Assert.Equal(new UUID(G(1)), m.InventoryBlock.FolderID);
        Assert.Equal(Agent, m.InventoryBlock.OwnerID);
        Assert.Equal((LmvFeatures.TypeLsl, LmvFeatures.InvTypeLsl), (m.InventoryBlock.Type, m.InventoryBlock.InvType));
        Assert.Equal("New Script", Utils.BytesToString(m.InventoryBlock.Name));
        var d = LmvFeatures.Remove(Agent, Session, 7, new UUID(G(11)));
        Assert.Equal((7u, new UUID(G(11))), (d.InventoryData.LocalID, d.InventoryData.ItemID));
    }

    [Fact]
    public void Open_on_a_listed_script_gives_the_editor_the_script_and_the_prim_it_is_in()
    {
        var r = Selected();
        var script = new TaskItem(G(11), "New Script", 10, 10, G(50), 0x7FFFFFFF, 0, G(60), G(50), 0x7FFFFFFF, 0, 0);
        r.C.Receive(new TaskInventoryReceived(G(1), [script, Item(12, "card")]));
        var a = r.C.ScriptToOpen(G(11), G(50));
        Assert.NotNull(a);
        Assert.Equal(G(1), a!.ObjectId);
        Assert.Equal(1u, a.LocalId);
        Assert.Equal(G(60), a.AssetId);
        Assert.Equal(Viewer.Core.Inventory.ScriptView.Editable, Viewer.Core.Inventory.ScriptRules.ViewOf(a));
        Assert.Null(r.C.ScriptToOpen(G(12), G(50)));   // a notecard
        Assert.Null(r.C.ScriptToOpen(G(99), G(50)));   // not listed
    }

    [Fact]
    public void Ossl_words_are_for_grids_other_than_second_life()
    {
        Assert.True(Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid("https://login.agni.lindenlab.com/cgi-bin/login.cgi"));
        Assert.True(Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid("https://login.aditi.lindenlab.com/cgi-bin/login.cgi"));
        Assert.False(Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid("http://grid.example.org:8002/"));
        Assert.False(Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid("http://lindenlab.com.example.org/"));
        Assert.False(Viewer.Shell.Inventory.ScriptEditing.IsSecondLifeGrid(null));
    }
}

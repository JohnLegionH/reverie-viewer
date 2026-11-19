// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Renaming an inventory item the way the official viewer does (llviewerinventory.cpp updateServer): the whole item goes in an
/// UpdateInventoryItem message, with the rights, owner and sale the grid described, and only for an item the agent may modify
/// (LLItemBridge::isItemRenameable). The item's rights come from the library's item. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class InventoryItemRenameTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Notes = Guid.Parse("00000000-0000-4000-8000-0000000000a2");
    private static readonly Guid LibOwner = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid LibRoot = Guid.Parse("00000000-0000-4000-8000-0000000000a3");

    private const uint Full = 0x7fffffff;
    private const uint NoModify = Full & ~ItemPermissions.Modify;

    private static ItemPermissions Rights(uint owner, uint everyone = 0, Guid? ownerId = null) =>
        new(Full, owner, 0x1000, everyone, 0x2000, Guid.Parse("00000000-0000-4000-8000-0000000000c1"), ownerId ?? Owner, Guid.Parse("00000000-0000-4000-8000-0000000000c2"), false, 2, 25);

    private static InventoryItemData Card(int n, ItemPermissions? rights) =>
        new(Guid.Parse($"00000000-0000-4000-8000-{n:D12}"), Notes, "Old name", ItemKind.Notecard, 7, 7, 0x44, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), null, "A note", null, rights);

    private static (InventoryActions Actions, List<ActionIntent> Sent) Rig(params InventoryItemData[] items)
    {
        var model = new InventoryModel((_, _) => { });
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            [new InventoryFolderData(Root, Guid.Empty, "My Inventory", FolderKind.Root, 1, -1), new InventoryFolderData(Notes, Root, "Notecards", FolderKind.Notecard, 1, 0)],
            [new InventoryFolderData(LibRoot, Guid.Empty, "Library", FolderKind.Root, 1, 0)]));
        model.Apply(new InventoryFolderFetched(Notes, true, 2, items.Length, [], items));
        var sent = new List<ActionIntent>();
        return (new InventoryActions(model, i => { sent.Add(i); return new ActionResult(ActionStatus.Sent, 1, null); }), sent);
    }

    [Fact]
    public void An_item_the_owner_may_modify_is_renamed_by_updating_the_whole_item()
    {
        var card = Card(10, Rights(Full));
        var (actions, sent) = Rig(card);
        Assert.Null(actions.WhyNotRename(card.Id));
        Assert.True(actions.Rename(card.Id, "New name").Submitted);
        var update = Assert.IsType<InventoryItemUpdateIntent>(Assert.Single(sent));
        Assert.Equal("New name", update.Item.Name);
        Assert.Equal(card.Id, update.Item.Id);
        Assert.Equal(Notes, update.Item.ParentId);
        Assert.Equal(card.Permissions, update.Item.Permissions);   // the rights, owner and sale go back as they were
        Assert.Equal("A note", update.Item.Description);
    }

    [Fact]
    public void An_item_the_agent_may_not_modify_is_not_renamed_and_nothing_is_sent()
    {
        var card = Card(11, Rights(NoModify));
        var (actions, sent) = Rig(card);
        Assert.Equal("inventory.no_modify", actions.WhyNotRename(card.Id));
        var result = actions.Rename(card.Id, "New name");
        Assert.False(result.Submitted);
        Assert.Equal("inventory.no_modify", result.Code);
        Assert.Empty(sent);
    }

    [Fact]
    public void Someone_elses_item_is_judged_by_the_everyone_mask_not_the_owners()
    {
        var yours = Card(12, Rights(Full, everyone: 0, ownerId: Other));   // the owner mask is full, but the owner is someone else
        var open = Card(13, Rights(Full, everyone: ItemPermissions.Modify, ownerId: Other));
        var (actions, _) = Rig(yours, open);
        Assert.Equal("inventory.no_modify", actions.WhyNotRename(yours.Id));
        Assert.Null(actions.WhyNotRename(open.Id));
    }

    [Fact]
    public void An_item_without_described_rights_still_goes_by_a_named_move()
    {
        var card = Card(14, null);
        var (actions, sent) = Rig(card);
        Assert.True(actions.Rename(card.Id, "Memo").Submitted);
        Assert.Equal(new InventoryMoveIntent(card.Id, Notes, "Memo"), Assert.Single(sent));
    }

    [Fact]
    public void The_update_intent_is_refused_without_rights_or_with_a_bad_name_and_becomes_an_update_command()
    {
        var card = Card(15, Rights(Full));
        Assert.Contains(new InventoryItemUpdateIntent(Card(16, null)).Validate(), i => i.Code == "inventory.no_permissions");
        Assert.Contains(new InventoryItemUpdateIntent(card with { Name = "" }).Validate(), i => i.Code == "inventory.bad_name");
        Assert.Empty(new InventoryItemUpdateIntent(card with { Name = "Fine" }).Validate());
        var command = Assert.IsType<UpdateInventoryItemCommand>(new InventoryItemUpdateIntent(card with { Name = "  Spaced  " }).ToCommand());
        Assert.Equal("Spaced", command.Item.Name);
    }

    [Fact]
    public void The_message_is_one_UpdateInventoryItem_block_carrying_the_name_and_the_whole_item()
    {
        var card = Card(17, Rights(Full)) with { Name = "Renamed" };
        var agent = new UUID("6f1c2a64-1d5b-4d2e-9a43-0b8f2d6c7e11");
        var session = new UUID("2b7d9e10-4c3a-4f58-8e21-5a6b7c8d9e02");
        var p = InventoryMovePackets.Update(agent, session, card);
        Assert.Equal(agent, p.AgentData.AgentID);
        Assert.Equal(session, p.AgentData.SessionID);
        Assert.Equal(UUID.Zero, p.AgentData.TransactionID);
        var b = Assert.Single(p.InventoryData);
        var rights = card.Permissions!;
        Assert.Equal(new UUID(card.Id), b.ItemID);
        Assert.Equal(new UUID(card.ParentId), b.FolderID);
        Assert.Equal("Renamed", Utils.BytesToString(b.Name));
        Assert.Equal("A note", Utils.BytesToString(b.Description));
        Assert.Equal((rights.BaseMask, rights.OwnerMask, rights.GroupMask, rights.EveryoneMask, rights.NextOwnerMask), (b.BaseMask, b.OwnerMask, b.GroupMask, b.EveryoneMask, b.NextOwnerMask));
        Assert.Equal(new UUID(rights.CreatorId), b.CreatorID);
        Assert.Equal(new UUID(rights.OwnerId), b.OwnerID);
        Assert.Equal(new UUID(rights.GroupId), b.GroupID);
        Assert.Equal((byte)2, b.SaleType);
        Assert.Equal(25, b.SalePrice);
        Assert.Equal((sbyte)7, b.Type);
        Assert.Equal((sbyte)7, b.InvType);
        Assert.Equal(0x44u, b.Flags);
        Assert.Equal(1_700_000_000, b.CreationDate);
        Assert.True(p.Header.Reliable);
    }

    [Fact]
    public void An_item_read_from_the_library_carries_its_rights_owner_and_sale()
    {
        var item = new InventoryItem(new UUID("00000000-0000-4000-8000-000000000020"))
        {
            Name = "Card", ParentUUID = new UUID(Notes), AssetType = AssetType.Notecard, InventoryType = InventoryType.Notecard, CreatorID = new UUID("00000000-0000-4000-8000-0000000000c1"),
            OwnerID = new UUID(Owner), GroupID = new UUID("00000000-0000-4000-8000-0000000000c2"), GroupOwned = true, SalePrice = 99, SaleType = SaleType.Copy,
            Permissions = new Permissions(Full, 0x1000, 0x2000, 0x4000, 0x8000),
        };
        var data = LmvTransport.ToItem(item);
        var rights = Assert.IsType<ItemPermissions>(data.Permissions);
        Assert.Equal(Owner, rights.OwnerId);
        Assert.Equal(Guid.Parse("00000000-0000-4000-8000-0000000000c1"), rights.CreatorId);
        Assert.True(rights.GroupOwned);
        Assert.Equal(99, rights.SalePrice);
        Assert.Equal((int)SaleType.Copy, rights.SaleType);
        Assert.Equal(Full, rights.BaseMask);
    }
}

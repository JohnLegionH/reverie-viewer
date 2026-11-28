// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An item's properties: which fields may be changed (the official viewer's item profile rules) and what a change sends. A change
/// goes as the whole item, rebuilt from the item the inventory holds, so only the name, the description and the next-owner
/// permissions can differ. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class ItemPropertiesTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Things = Guid.Parse("00000000-0000-4000-8000-0000000000a2");
    private static readonly Guid Trash = Guid.Parse("00000000-0000-4000-8000-0000000000a4");
    private static readonly Guid LibOwner = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid LibRoot = Guid.Parse("00000000-0000-4000-8000-0000000000a3");

    private const uint M = ItemPermissions.Modify, C = ItemPermissions.Copy, T = ItemPermissions.Transfer;
    private const uint Move = 0x80000, Full = M | C | T | Move;

    private static ItemPermissions Rights(uint owner = Full, uint next = Full, uint @base = Full, Guid? ownerId = null, bool group = false) =>
        new(@base, owner, 0x11, 0x22, next, Guid.Parse("00000000-0000-4000-8000-0000000000c1"), ownerId ?? Owner,
            Guid.Parse("00000000-0000-4000-8000-0000000000c2"), group, 2, 25);

    private static InventoryItemData Item(ItemPermissions? rights, ItemKind kind = ItemKind.Notecard, int invType = 7, Guid? parent = null, int n = 10) =>
        new(Guid.Parse($"00000000-0000-4000-8000-{n:D12}"), parent ?? Things, "Old name", kind, 7, invType, 0x44,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), null, "Old words", null, rights);

    private static InventoryModel Model(params InventoryItemData[] items)
    {
        var model = new InventoryModel((_, _) => { });
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            [new InventoryFolderData(Root, Guid.Empty, "My Inventory", FolderKind.Root, 1, -1), new InventoryFolderData(Things, Root, "Things", FolderKind.Notecard, 1, 0),
             new InventoryFolderData(Trash, Root, "Trash", FolderKind.Trash, 1, 0)],
            [new InventoryFolderData(LibRoot, Guid.Empty, "Library", FolderKind.Root, 1, 0)]));
        foreach (var group in items.GroupBy(i => i.ParentId))
            model.Apply(new InventoryFolderFetched(group.Key, true, 2, group.Count(), [], group.ToArray()));
        return model;
    }

    private static ItemEditRequest Same(InventoryItemData i) => new(i.Name, i.Description,
        (i.Permissions!.NextOwnerMask & M) != 0, (i.Permissions.NextOwnerMask & C) != 0, (i.Permissions.NextOwnerMask & T) != 0);

    private static ItemEditRights Of(InventoryItemData item) => ItemEdit.RightsFor(Model(item), item);

    [Fact]
    public void The_owner_of_a_full_item_may_change_every_field() =>
        Assert.Equal(new ItemEditRights(true, true, true, true, true), Of(Item(Rights())));

    [Fact]
    public void A_no_modify_item_keeps_its_name_and_description_but_its_next_owner_boxes_stay_open() =>
        Assert.Equal(new ItemEditRights(false, false, false, true, true), Of(Item(Rights(owner: C | T, @base: C | T, next: C | T))));

    [Fact]
    public void A_no_transfer_item_has_every_next_owner_box_greyed() =>
        Assert.Equal(new ItemEditRights(true, true, false, false, false), Of(Item(Rights(owner: M | C, @base: M | C, next: C))));

    [Fact]
    public void Transfer_may_be_changed_only_while_the_next_owner_may_copy() =>
        Assert.False(Of(Item(Rights(next: T))).NextTransfer);

    [Fact]
    public void A_no_copy_item_cannot_offer_copy_to_the_next_owner() =>
        Assert.Equal(new ItemEditRights(true, true, true, false, false), Of(Item(Rights(owner: M | T, @base: M | T, next: M | T))));

    [Fact]
    public void A_permission_the_owner_lost_is_not_offered_even_when_the_base_still_has_it() =>
        Assert.False(Of(Item(Rights(owner: C | T, @base: Full, next: C | T))).NextModify);

    [Theory]
    [InlineData(ItemKind.CallingCard, 2, false)]
    [InlineData(ItemKind.Landmark, 3, true)]
    public void Calling_cards_and_landmarks_never_change_next_owner_rights_and_a_calling_card_keeps_its_name(ItemKind kind, int invType, bool name) =>
        Assert.Equal(new ItemEditRights(name, true, false, false, false), Of(Item(Rights(), kind, invType)));

    [Fact]
    public void A_settings_item_cannot_change_next_owner_copy() => Assert.False(Of(Item(Rights(), ItemKind.Settings, 25)).NextCopy);

    [Fact]
    public void Someone_elses_item_a_group_owned_item_a_trashed_item_a_library_item_and_a_link_have_greyed_next_owner_boxes()
    {
        Assert.Equal(ItemEditRights.None, Of(Item(Rights(ownerId: Other))));
        Assert.Equal(new ItemEditRights(true, true, false, false, false), Of(Item(Rights(group: true))));
        Assert.Equal(ItemEditRights.None, Of(Item(Rights(), parent: Trash)));
        Assert.Equal(ItemEditRights.None, Of(Item(Rights(), parent: LibRoot)));
        Assert.Equal(ItemEditRights.None, Of(Item(Rights(), ItemKind.Link)));
        Assert.Equal(ItemEditRights.None, Of(Item(null)));
        Assert.True(Of(Item(Rights())).Name);   // the control: the same item, the agent's own, is open
    }

    [Fact]
    public void A_change_alters_only_the_name_the_description_and_the_next_owner_mask()
    {
        var item = Item(Rights());
        var done = ItemEdit.Apply(Model(item), item, new ItemEditRequest("New name", "New words", false, true, true));
        Assert.Equal(item with { Name = "New name", Description = "New words", Permissions = item.Permissions! with { NextOwnerMask = C | T | Move } }, done);
    }

    [Fact]
    public void Greyed_fields_ignore_what_is_asked()
    {
        var item = Item(Rights(owner: C | T, @base: C | T, next: C | T));
        var done = ItemEdit.Apply(Model(item), item, new ItemEditRequest("Sneaky", "Sneaky", true, true, false));
        Assert.Equal(item with { Permissions = item.Permissions! with { NextOwnerMask = C } }, done);   // transfer off was allowed; nothing else
    }

    [Fact]
    public void No_request_ever_gives_a_bit_the_person_does_not_hold_or_touches_another_mask()
    {
        uint[] sets = [0, M, C, T, M | C, M | T, C | T, M | C | T];
        int added = 0;
        foreach (var @base in sets)
        foreach (var owner in sets)
        foreach (var next in sets)
        foreach (var want in sets)
        {
            var item = Item(Rights(owner: owner, @base: @base, next: next), ItemKind.Object, 6);
            var done = ItemEdit.Apply(Model(item), item, new ItemEditRequest(item.Name, item.Description, (want & M) != 0, (want & C) != 0, (want & T) != 0));
            var p = done.Permissions!;
            uint gained = p.NextOwnerMask & ~next;
            Assert.Equal(0u, gained & ~(@base & owner));
            Assert.Equal(item.Permissions! with { NextOwnerMask = p.NextOwnerMask }, p);
            if (gained != 0) added++;
        }
        Assert.True(added > 0);
    }

    [Fact]
    public void Taking_copy_away_from_the_next_owner_turns_transfer_on()
    {
        var item = Item(Rights(next: C));
        var done = ItemEdit.Apply(Model(item), item, new ItemEditRequest(item.Name, item.Description, false, false, false));
        Assert.Equal(T, done.Permissions!.NextOwnerMask & (C | T));
    }

    [Fact]
    public void New_next_owner_rights_on_an_object_are_flagged_to_apply_when_it_is_rezzed_and_a_rename_is_not()
    {
        var item = Item(Rights(), ItemKind.Object, 6);
        var model = Model(item);
        Assert.Equal(0x44u | ItemEdit.SlamPermissions, ItemEdit.Apply(model, item, Same(item) with { NextModify = false }).Flags);
        var renamed = ItemEdit.Apply(model, item, Same(item) with { Name = "Box" });
        Assert.Equal(("Box", 0x44u), (renamed.Name, renamed.Flags));
    }

    [Fact]
    public void A_blank_name_is_kept_and_a_long_description_is_cut_to_127_bytes_without_splitting_a_letter()
    {
        var item = Item(Rights());
        var done = ItemEdit.Apply(Model(item), item, Same(item) with { Name = "   ", Description = new string('a', 126) + "é tail" });
        Assert.Equal("Old name", done.Name);
        Assert.Equal(new string('a', 126), done.Description);
        Assert.True(Encoding.UTF8.GetByteCount(done.Description) <= ItemEdit.MaxDescriptionBytes);
    }

    [Fact]
    public void A_window_not_refilled_since_the_item_changed_elsewhere_asks_for_nothing_the_user_did_not_change()
    {
        var item = Item(Rights());
        var shown = Same(item);
        var elsewhere = item with { Name = "Renamed elsewhere", Description = "Changed elsewhere", Permissions = item.Permissions! with { NextOwnerMask = C | T | Move } };
        Assert.Equal(Same(elsewhere), ItemEdit.Changes(elsewhere, shown, shown));
        Assert.Equal(Same(elsewhere) with { NextCopy = false }, ItemEdit.Changes(elsewhere, shown, shown with { NextCopy = false }));
    }

    // ---------------- saving ----------------

    private static (InventoryActions Actions, List<ActionIntent> Sent, InventoryModel Model) Rig(InventoryItemData item, ActionStatus status = ActionStatus.Sent)
    {
        var model = Model(item);
        var sent = new List<ActionIntent>();
        return (new InventoryActions(model, i => { sent.Add(i); return new ActionResult(status, 1, null); }), sent, model);
    }

    [Fact]
    public void Saving_sends_one_whole_item_update_and_shows_it_at_once()
    {
        var item = Item(Rights());
        var (actions, sent, model) = Rig(item);
        Assert.True(actions.SaveProperties(item.Id, Same(item) with { Description = "Kept", NextCopy = false }).Submitted);
        var update = Assert.IsType<InventoryItemUpdateIntent>(Assert.Single(sent));
        Assert.Equal("Kept", update.Item.Description);
        Assert.Equal(update.Item, model.Item(item.Id));
    }

    [Fact]
    public void Saving_with_nothing_changed_sends_nothing_and_a_refused_send_leaves_the_item_as_it_was()
    {
        var item = Item(Rights());
        var (actions, sent, _) = Rig(item);
        var none = actions.SaveProperties(item.Id, Same(item));
        Assert.Equal((false, (string?)null, 0), (none.Submitted, none.Code, sent.Count));
        var (refusing, tried, model) = Rig(item, ActionStatus.Invalid);
        Assert.False(refusing.SaveProperties(item.Id, Same(item) with { Name = "No" }).Submitted);
        Assert.Single(tried);
        Assert.Equal(item, model.Item(item.Id));
    }

    [Fact]
    public void Saving_an_unknown_item_is_refused()
    {
        var item = Item(Rights());
        var (actions, sent, _) = Rig(item);
        Assert.Equal("inventory.unknown", actions.SaveProperties(Guid.NewGuid(), Same(item)).Code);
        Assert.Empty(sent);
    }
}

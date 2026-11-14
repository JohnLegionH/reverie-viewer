// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The folder actions of the inventory window: the rules that refuse a change before it is offered, what each allowed change
/// submits, and the read that follows. A fake submit stands in for the pipeline. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class InventoryActionsTests
{
    private static readonly Guid Elsewhere = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Landmarks = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
    private static readonly Guid LostFound = Guid.Parse("00000000-0000-4000-8000-0000000000e3");
    private static readonly Guid Inside = Guid.Parse("00000000-0000-4000-8000-0000000000e4");

    private sealed class Rig
    {
        public List<ActionIntent> Submitted = new();
        public ActionStatus Answer = ActionStatus.Sent;
        public List<(Guid Folder, Guid Owner)> Requests = new();
        public InventoryModel Model;
        public InventoryActions Actions;

        public Rig()
        {
            Model = new InventoryModel((f, o) => Requests.Add((f, o)));
            Model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
                new[]
                {
                    F(Root, Guid.Empty, "My Inventory", FolderKind.Root), F(Textures, Root, "Textures", FolderKind.Texture), F(Trash, Root, "Trash", FolderKind.Trash),
                    F(Mine, Root, "Mine"), F(MineSub, Mine, "Sub"), F(Elsewhere, Root, "Elsewhere"), F(Landmarks, Root, "Landmarks", FolderKind.Landmark),
                    F(LostFound, Root, "Lost And Found", FolderKind.LostAndFound), F(Inside, MineSub, "Inside"),
                },
                new[] { F(LibRoot, Guid.Empty, "Library", FolderKind.Root), F(LibObjects, LibRoot, "Objects", FolderKind.Object) }));
            foreach (var f in Model.AllFolders().ToList())
                Model.Apply(new InventoryFolderFetched(f.Id, true, 1, 0, Model.ChildFolders(f.Id).ToList(), Model.ChildItems(f.Id).ToList()));
            Requests.Clear();
            Actions = new InventoryActions(Model, intent =>
            {
                Submitted.Add(intent);
                return new ActionResult(Answer, 1, null);
            });
        }

        /// <summary>The grid answers every outstanding request with what the model already holds.</summary>
        public void Settle()
        {
            foreach (var (folder, _) in Requests.ToList())
                Model.Apply(new InventoryFolderFetched(folder, true, 2, 0, Model.ChildFolders(folder).ToList(), Model.ChildItems(folder).ToList()));
            Requests.Clear();
        }

        public void Put(InventoryItemData item) =>
            Model.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { item }, Array.Empty<Guid>(), Array.Empty<Guid>()));
    }

    // ---------------- new folder ----------------

    [Fact]
    public void A_new_folder_goes_under_the_selected_folder_or_under_the_folder_of_the_selected_item()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.NewFolder(Mine, "New Folder").Submitted);
        Assert.True(r.Actions.NewFolder(item.Id, "New Folder").Submitted);
        Assert.Equal(new ActionIntent[] { new InventoryFolderCreateIntent(Mine, "New Folder"), new InventoryFolderCreateIntent(Mine, "New Folder") }, r.Submitted.ToArray());
    }

    [Fact]
    public void With_nothing_selected_a_new_folder_goes_under_my_inventory()
    {
        var r = new Rig();
        Assert.True(r.Actions.NewFolder(Guid.Empty, "New Folder").Submitted);
        Assert.Equal(new InventoryFolderCreateIntent(Root, "New Folder"), Assert.Single(r.Submitted));
    }

    [Fact]
    public void Nothing_can_be_made_in_the_library()
    {
        var r = new Rig();
        Assert.Equal("inventory.library", r.Actions.WhyNotCreate(LibObjects));
        var result = r.Actions.NewFolder(LibObjects, "x");
        Assert.False(result.Submitted);
        Assert.Equal("inventory.library", result.Code);
        Assert.Empty(r.Submitted);
    }

    // ---------------- rename ----------------

    [Fact]
    public void A_plain_folder_and_an_item_can_be_renamed_and_the_folders_parent_and_kind_go_with_it()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.Rename(Mine, "Work").Submitted);
        Assert.True(r.Actions.Rename(item.Id, "Memo").Submitted);
        Assert.Equal(new ActionIntent[] { new InventoryFolderRenameIntent(Mine, Root, -1, "Work"), new InventoryMoveIntent(item.Id, Mine, "Memo") }, r.Submitted.ToArray());
    }

    [Fact]
    public void System_folders_the_library_and_calling_cards_cannot_be_renamed()
    {
        var r = new Rig();
        var card = I(2, Mine, "Friend", ItemKind.CallingCard);
        var libItem = I(3, LibObjects, "Chair", ItemKind.Object);
        r.Put(card); r.Put(libItem);
        Assert.Equal("inventory.protected", r.Actions.WhyNotRename(Textures));
        Assert.Equal("inventory.protected", r.Actions.WhyNotRename(Trash));
        Assert.Equal("inventory.protected", r.Actions.WhyNotRename(Root));
        Assert.Equal("inventory.library", r.Actions.WhyNotRename(LibObjects));
        Assert.Equal("inventory.library", r.Actions.WhyNotRename(libItem.Id));
        Assert.Equal("inventory.not_renameable", r.Actions.WhyNotRename(card.Id));
        Assert.Equal("inventory.unknown", r.Actions.WhyNotRename(Guid.NewGuid()));
        Assert.False(r.Actions.Rename(Textures, "x").Submitted);
        Assert.Empty(r.Submitted);
    }

    // ---------------- move ----------------

    [Fact]
    public void An_item_or_a_plain_folder_can_be_dropped_into_another_folder()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.Move(item.Id, Elsewhere).Submitted);
        Assert.True(r.Actions.Move(MineSub, Elsewhere).Submitted);
        Assert.Equal(new ActionIntent[] { new InventoryMoveIntent(item.Id, Elsewhere), new InventoryFolderMoveIntent(MineSub, Elsewhere) }, r.Submitted.ToArray());
    }

    [Fact]
    public void A_folder_cannot_go_into_itself_or_into_something_inside_it()
    {
        var r = new Rig();
        Assert.Equal("inventory.into_self", r.Actions.WhyNotMove(Mine, Mine));
        Assert.Equal("inventory.into_own_child", r.Actions.WhyNotMove(Mine, MineSub));
        Assert.Equal("inventory.into_own_child", r.Actions.WhyNotMove(Mine, Inside));
        Assert.Null(r.Actions.WhyNotMove(Inside, Mine));
    }

    [Fact]
    public void System_folders_cannot_be_moved_and_a_folder_holding_one_cannot_either()
    {
        var r = new Rig();
        Assert.Equal("inventory.protected", r.Actions.WhyNotMove(Textures, Mine));
        Assert.Equal("inventory.protected", r.Actions.WhyNotMove(Trash, Mine));
        r.Model.Apply(new InventoryChanged(new[] { F(Guid.Parse("00000000-0000-4000-8000-0000000000e5"), Elsewhere, "Cards", FolderKind.CallingCard) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Equal("inventory.protected_inside", r.Actions.WhyNotMove(Elsewhere, Mine));
    }

    [Fact]
    public void Nothing_goes_into_or_out_of_the_library_and_some_folders_take_nothing_dropped()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        var libItem = I(2, LibObjects, "Chair", ItemKind.Object);
        r.Put(item); r.Put(libItem);
        Assert.Equal("inventory.library", r.Actions.WhyNotMove(item.Id, LibObjects));
        Assert.Equal("inventory.library", r.Actions.WhyNotMove(Mine, LibObjects));
        Assert.Equal("inventory.library", r.Actions.WhyNotMove(libItem.Id, Mine));
        Assert.Equal("inventory.bad_destination", r.Actions.WhyNotMove(item.Id, LostFound));
        Assert.Equal("inventory.bad_destination", r.Actions.WhyNotMove(Mine, LostFound));
        Assert.Equal("inventory.same_parent", r.Actions.WhyNotMove(item.Id, Mine));
        Assert.Equal("inventory.same_parent", r.Actions.WhyNotMove(MineSub, Mine));
        Assert.Equal("inventory.bad_destination", r.Actions.WhyNotMove(item.Id, item.Id));   // an item is not a folder
        Assert.Equal("inventory.unknown", r.Actions.WhyNotMove(item.Id, Guid.NewGuid()));
        Assert.Empty(r.Submitted);
    }

    // ---------------- trash ----------------

    [Fact]
    public void Trashing_moves_to_the_trash_folder_only_when_confirmed_and_never_purges()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.Trash(item.Id, confirmed: true).Submitted);
        Assert.True(r.Actions.Trash(MineSub, confirmed: true).Submitted);
        Assert.Equal(new ActionIntent[] { new InventoryTrashIntent(item.Id, Trash, true), new InventoryFolderTrashIntent(MineSub, Trash, true) }, r.Submitted.ToArray());
        var unconfirmed = r.Actions.Trash(Elsewhere, confirmed: false);
        Assert.False(unconfirmed.Submitted);
        Assert.Equal("inventory.not_confirmed", unconfirmed.Code);
        Assert.Equal(2, r.Submitted.Count);
    }

    [Fact]
    public void System_folders_the_library_and_things_already_in_the_trash_are_not_trashed()
    {
        var r = new Rig();
        var inTrash = I(3, Trash, "Old");
        var libItem = I(2, LibObjects, "Chair", ItemKind.Object);
        r.Put(inTrash); r.Put(libItem);
        Assert.Equal("inventory.protected", r.Actions.WhyNotTrash(Textures));
        Assert.Equal("inventory.protected", r.Actions.WhyNotTrash(Trash));
        Assert.Equal("inventory.library", r.Actions.WhyNotTrash(libItem.Id));
        Assert.Equal("inventory.library", r.Actions.WhyNotTrash(LibObjects));
        Assert.Equal("inventory.already_trash", r.Actions.WhyNotTrash(inTrash.Id));
        Assert.Equal("inventory.unknown", r.Actions.WhyNotTrash(Guid.NewGuid()));
        Assert.Null(r.Actions.WhyNotTrash(Mine));
    }

    [Fact]
    public void Without_a_trash_folder_nothing_is_trashed()
    {
        var model = new InventoryModel();
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot, new[] { F(Root, Guid.Empty, "r", FolderKind.Root), F(Mine, Root, "Mine") }, Array.Empty<InventoryFolderData>()));
        var actions = new InventoryActions(model, _ => throw new InvalidOperationException("must not submit"));
        Assert.Equal("inventory.no_trash", actions.WhyNotTrash(Mine));
    }

    // ---------------- what the pipeline says, and the read after ----------------

    [Fact]
    public void A_submitted_change_reads_the_folders_it_touches_again()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        r.Actions.Move(item.Id, Elsewhere);
        Assert.Contains((Mine, Owner), r.Requests);
        Assert.Contains((Elsewhere, Owner), r.Requests);
    }

    [Fact]
    public void A_new_folder_and_a_rename_read_the_parent_again_and_a_trash_reads_the_trash_too()
    {
        var r = new Rig();
        r.Actions.NewFolder(Mine, "New Folder");
        Assert.Contains((Mine, Owner), r.Requests);
        r.Settle();
        r.Actions.Rename(MineSub, "Renamed");
        Assert.Contains((Mine, Owner), r.Requests);
        r.Settle();
        r.Actions.Trash(MineSub, true);
        Assert.Contains((Mine, Owner), r.Requests);
        Assert.Contains((Trash, Owner), r.Requests);
    }

    [Fact]
    public void A_change_the_pipeline_did_not_send_is_reported_and_reads_nothing()
    {
        var r = new Rig { Answer = ActionStatus.Failed };
        var result = r.Actions.Move(MineSub, Elsewhere);
        Assert.False(result.Submitted);
        Assert.Equal(ActionStatus.Failed, result.Pipeline?.Status);
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void An_outcome_that_is_not_known_is_submitted_and_the_read_settles_it()
    {
        var r = new Rig { Answer = ActionStatus.OutcomeUnknown };
        var result = r.Actions.Move(MineSub, Elsewhere);
        Assert.True(result.Submitted);
        Assert.Contains((Elsewhere, Owner), r.Requests);
    }

    [Fact]
    public void Names_are_cleaned_by_the_pipeline_and_an_empty_name_is_refused_there()
    {
        var r = new Rig { Answer = ActionStatus.Invalid };
        var result = r.Actions.Rename(Mine, "   ");
        Assert.False(result.Submitted);
        Assert.Equal(ActionStatus.Invalid, result.Pipeline?.Status);
    }
}

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

    // A move is shown at once and not read back, as the official viewer does (LLInventoryModel::changeItemParent updates its
    // own model and sends the move; it does not fetch either folder again). Reading both folders straight after the move
    // raced the move itself: an answer the grid gave before it applied the move put the item back where it was.

    /// <summary>A folder the grid knows of but whose contents were never read: a closed folder that was never opened.</summary>
    private static readonly Guid Closed = Guid.Parse("00000000-0000-4000-8000-0000000000e5");

    private static void AddClosed(Rig r) =>
        r.Model.Apply(new InventoryChanged(new[] { F(Closed, Root, "Closed") }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));

    [Fact]
    public void A_moved_item_shows_in_an_open_folder_at_once_and_neither_folder_is_read_again()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.Move(item.Id, Elsewhere).Submitted);
        Assert.Equal(Elsewhere, r.Model.Item(item.Id)!.ParentId);
        Assert.Contains(r.Model.ChildItems(Elsewhere), i => i.Id == item.Id);
        Assert.DoesNotContain(r.Model.ChildItems(Mine), i => i.Id == item.Id);
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void A_moved_item_shows_in_a_closed_folder_at_once_and_the_folder_stays_closed()
    {
        var r = new Rig();
        AddClosed(r);
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Actions.Move(item.Id, Closed).Submitted);
        Assert.Equal(Closed, r.Model.Item(item.Id)!.ParentId);
        Assert.DoesNotContain(r.Model.ChildItems(Mine), i => i.Id == item.Id);
        Assert.Equal(FolderLoad.NotLoaded, r.Model.LoadState(Closed));
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void A_moved_folder_shows_under_its_new_parent_at_once_open_or_closed()
    {
        var r = new Rig();
        AddClosed(r);
        Assert.True(r.Actions.Move(MineSub, Elsewhere).Submitted);
        Assert.Equal(Elsewhere, r.Model.Folder(MineSub)!.ParentId);
        Assert.Contains(r.Model.ChildFolders(Elsewhere), f => f.Id == MineSub);
        Assert.DoesNotContain(r.Model.ChildFolders(Mine), f => f.Id == MineSub);
        Assert.True(r.Actions.Move(MineSub, Closed).Submitted);
        Assert.Equal(Closed, r.Model.Folder(MineSub)!.ParentId);
        Assert.True(r.Model.IsDescendantOf(Inside, Closed));
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void Several_items_move_together_each_with_its_own_request_and_a_refused_one_stays()
    {
        var r = new Rig();
        AddClosed(r);
        var a = I(1, Mine, "A");
        var b = I(2, Mine, "B");
        var c = I(3, Elsewhere, "C");
        r.Put(a); r.Put(b); r.Put(c);
        var results = r.Actions.MoveMany(new[] { a.Id, b.Id, c.Id, MineSub }, Closed);
        Assert.Equal(4, results.Count);
        Assert.All(results, x => Assert.True(x.Submitted));
        Assert.Equal(new ActionIntent[]
        {
            new InventoryMoveIntent(a.Id, Closed), new InventoryMoveIntent(b.Id, Closed), new InventoryMoveIntent(c.Id, Closed), new InventoryFolderMoveIntent(MineSub, Closed),
        }, r.Submitted.ToArray());
        Assert.Equal(new[] { a.Id, b.Id, c.Id }.OrderBy(x => x), r.Model.ChildItems(Closed).Select(i => i.Id).OrderBy(x => x));
        Assert.Contains(r.Model.ChildFolders(Closed), f => f.Id == MineSub);
        Assert.Empty(r.Requests);

        // into an open folder, with one the rules refuse (it is already there): the others still move
        var d = I(4, Mine, "D");
        var e = I(5, Elsewhere, "E");
        r.Put(d); r.Put(e);
        r.Submitted.Clear();
        results = r.Actions.MoveMany(new[] { d.Id, e.Id }, Elsewhere);
        Assert.True(results[0].Submitted);
        Assert.False(results[1].Submitted);
        Assert.Equal("inventory.same_parent", results[1].Code);
        Assert.Equal(new ActionIntent[] { new InventoryMoveIntent(d.Id, Elsewhere) }, r.Submitted.ToArray());
        Assert.Equal(Elsewhere, r.Model.Item(d.Id)!.ParentId);
    }

    [Fact]
    public void A_drag_carries_the_whole_selection_when_the_dragged_row_is_in_it_and_only_that_row_otherwise()
    {
        Guid a = Guid.Parse("3f0a4c2e-7b1d-4e69-8a52-c0d1e2f3a4b5"), b = Guid.Parse("4a1b5d3f-8c2e-4f7a-9b63-d1e2f3a4b5c6"), c = Guid.Parse("5b2c6e4a-9d3f-4a8b-8c74-e2f3a4b5c6d7");
        Assert.Equal(new[] { a, b, c }, InventoryDragSet.For(b, new[] { a, b, c }));
        Assert.Equal(new[] { c }, InventoryDragSet.For(c, new[] { a, b }));
        Assert.Equal(new[] { a }, InventoryDragSet.For(a, Array.Empty<Guid>()));
        Assert.Equal(new[] { a, b }, InventoryDragSet.For(a, new[] { a, b, a }));
        Assert.Empty(InventoryDragSet.For(Guid.Empty, new[] { a }));
        Assert.Equal(new[] { a, b, c }, InventoryDragSet.Decode(InventoryDragSet.Encode(new[] { a, b, c })));
        Assert.Equal(new[] { a }, InventoryDragSet.Decode(a.ToString()));   // a single id, as a drag from before carried it
        Assert.Empty(InventoryDragSet.Decode("not an id"));
        Assert.Empty(InventoryDragSet.Decode(null));
    }

    [Fact]
    public void A_folder_answer_that_left_before_the_move_does_not_put_the_item_back()
    {
        var r = new Rig();
        var item = I(1, Mine, "Note");
        r.Put(item);
        Assert.True(r.Model.Invalidate(Mine));   // a read of Mine is on its way when the drop happens
        var before = r.Model.ChildItems(Mine).ToList();
        r.Requests.Clear();
        Assert.True(r.Actions.Move(item.Id, Elsewhere).Submitted);
        // the grid's answer to the read that left before the move still lists the item in Mine
        r.Model.Apply(new InventoryFolderFetched(Mine, true, 2, 0, r.Model.ChildFolders(Mine).ToList(), before));
        Assert.Equal(Elsewhere, r.Model.Item(item.Id)!.ParentId);
        Assert.DoesNotContain(r.Model.ChildItems(Mine), i => i.Id == item.Id);
        Assert.Contains((Mine, Owner), r.Requests);   // that answer is set aside and the folder is asked for once more
        // the next answer is the grid's word on the folder
        r.Model.Apply(new InventoryFolderFetched(Mine, true, 3, 0, r.Model.ChildFolders(Mine).ToList(), Array.Empty<InventoryItemData>()));
        Assert.Equal(FolderLoad.Loaded, r.Model.LoadState(Mine));
        Assert.Equal(Elsewhere, r.Model.Item(item.Id)!.ParentId);
    }

    [Fact]
    public void A_new_folder_and_a_rename_read_the_parent_again_and_a_trash_shows_in_the_trash_at_once()
    {
        var r = new Rig();
        r.Actions.NewFolder(Mine, "New Folder");
        Assert.Contains((Mine, Owner), r.Requests);
        r.Settle();
        r.Actions.Rename(MineSub, "Renamed");
        Assert.Contains((Mine, Owner), r.Requests);
        r.Settle();
        r.Actions.Trash(MineSub, true);
        Assert.Equal(Trash, r.Model.Folder(MineSub)!.ParentId);
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void A_change_the_pipeline_did_not_send_is_reported_and_reads_nothing()
    {
        var r = new Rig { Answer = ActionStatus.Failed };
        var result = r.Actions.Move(MineSub, Elsewhere);
        Assert.False(result.Submitted);
        Assert.Equal(ActionStatus.Failed, result.Pipeline?.Status);
        Assert.Empty(r.Requests);
        Assert.Equal(Mine, r.Model.Folder(MineSub)!.ParentId);
    }

    [Fact]
    public void A_move_whose_outcome_is_not_known_is_shown_as_sent()
    {
        var r = new Rig { Answer = ActionStatus.OutcomeUnknown };
        var result = r.Actions.Move(MineSub, Elsewhere);
        Assert.True(result.Submitted);
        Assert.Equal(Elsewhere, r.Model.Folder(MineSub)!.ParentId);
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

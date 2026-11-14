// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The inventory model: the login skeleton, lazy folder loading, the grid's answers and pushes. No grid: the model is fed
/// the transport's records and asks for folders through a recording delegate. Every member builds its own model, so the
/// class runs in parallel.
/// </summary>
public class InventoryModelTests
{
    internal static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal static readonly Guid LibOwner = Guid.Parse("99999999-8888-7777-6666-555555555555");
    internal static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-000000000001");
    internal static readonly Guid Textures = Guid.Parse("00000000-0000-4000-8000-000000000002");
    internal static readonly Guid Trash = Guid.Parse("00000000-0000-4000-8000-000000000003");
    internal static readonly Guid Clothing = Guid.Parse("00000000-0000-4000-8000-000000000004");
    internal static readonly Guid Mine = Guid.Parse("00000000-0000-4000-8000-000000000005");
    internal static readonly Guid MineSub = Guid.Parse("00000000-0000-4000-8000-000000000006");
    internal static readonly Guid LibRoot = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    internal static readonly Guid LibObjects = Guid.Parse("00000000-0000-4000-8000-0000000000a2");

    internal static InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None, int version = 1, int count = -1) =>
        new(id, parent, name, kind, version, count);

    internal static InventoryItemData I(int n, Guid parent, string name, ItemKind kind = ItemKind.Notecard, DateTimeOffset? at = null, Guid? link = null) =>
        new(Guid.Parse($"00000000-0000-4000-8000-1000000000{n:x2}"), parent, name, kind, 7, 7, 0, at ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(n), link);

    internal static InventorySkeletonReceived Skeleton() => new(Owner, Root, LibOwner, LibRoot,
        new[]
        {
            F(Root, Guid.Empty, "My Inventory", FolderKind.Root, 5),
            F(Textures, Root, "Textures", FolderKind.Texture, 3),
            F(Trash, Root, "Trash", FolderKind.Trash, 2),
            F(Clothing, Root, "Clothing", FolderKind.Clothing, 4),
            F(Mine, Root, "Mine", FolderKind.None, 6),
            F(MineSub, Mine, "Sub", FolderKind.None, 1),
        },
        new[]
        {
            F(LibRoot, Guid.Empty, "Library", FolderKind.Root, 1),
            F(LibObjects, LibRoot, "Objects", FolderKind.Object, 1),
        });

    private static (InventoryModel Model, List<(Guid Folder, Guid Owner)> Requests) New(int max = 4)
    {
        var requests = new List<(Guid, Guid)>();
        var model = new InventoryModel((f, o) => { lock (requests) requests.Add((f, o)); }, max);
        model.Apply(Skeleton());
        return (model, requests);
    }

    private static InventoryFolderFetched Fetched(Guid folder, IEnumerable<InventoryFolderData>? folders = null, IEnumerable<InventoryItemData>? items = null,
        bool ok = true, int version = 9) =>
        new(folder, ok, version, (folders?.Count() ?? 0) + (items?.Count() ?? 0), (folders ?? Array.Empty<InventoryFolderData>()).ToList(), (items ?? Array.Empty<InventoryItemData>()).ToList());

    // ---------------- the skeleton ----------------

    [Fact]
    public void The_skeleton_gives_both_roots_and_every_folder_but_no_items()
    {
        var (m, requests) = New();
        Assert.True(m.HasSkeleton);
        Assert.Equal(Root, m.RootId);
        Assert.Equal(LibRoot, m.LibraryRootId);
        Assert.Equal(Owner, m.OwnerId);
        Assert.Equal(LibOwner, m.LibraryOwnerId);
        Assert.Equal(new[] { "Clothing", "Mine", "Textures", "Trash" }, m.ChildFolders(Root).Select(f => f.Name).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { Objects() }, m.ChildFolders(LibRoot).Select(f => f.Id).ToArray());
        Assert.Empty(m.AllItems());
        Assert.Empty(requests);   // nothing is fetched until a folder is opened
        static Guid Objects() => LibObjects;
    }

    [Fact]
    public void Before_the_skeleton_nothing_is_known_and_nothing_can_be_requested()
    {
        var requests = new List<(Guid, Guid)>();
        var m = new InventoryModel((f, o) => requests.Add((f, o)));
        Assert.False(m.HasSkeleton);
        Assert.False(m.EnsureLoaded(Root));
        Assert.Null(m.Folder(Root));
        Assert.Empty(requests);
    }

    [Fact]
    public void A_folder_is_not_loaded_until_asked_for()
    {
        var (m, _) = New();
        Assert.Equal(FolderLoad.NotLoaded, m.LoadState(Textures));
        Assert.Equal(8, m.UnloadedFolderCount);
    }

    // ---------------- lazy loading ----------------

    [Fact]
    public void Opening_a_folder_requests_it_once_with_the_agent_as_owner()
    {
        var (m, requests) = New();
        Assert.True(m.EnsureLoaded(Textures));
        Assert.True(m.EnsureLoaded(Textures));   // already on its way: no second request
        Assert.Equal(FolderLoad.Loading, m.LoadState(Textures));
        Assert.Single(requests);
        Assert.Equal((Textures, Owner), requests[0]);
    }

    [Fact]
    public void A_library_folder_is_requested_with_the_library_owner()
    {
        var (m, requests) = New();
        Assert.True(m.EnsureLoaded(LibObjects));
        Assert.Equal((LibObjects, LibOwner), Assert.Single(requests));
    }

    [Fact]
    public void An_unknown_folder_is_never_requested()
    {
        var (m, requests) = New();
        Assert.False(m.EnsureLoaded(Guid.NewGuid()));
        Assert.False(m.EnsureLoaded(Guid.Empty));
        Assert.Empty(requests);
    }

    [Fact]
    public void The_grids_answer_fills_the_folder_and_marks_it_loaded()
    {
        var (m, _) = New();
        m.EnsureLoaded(Mine);
        var sub2 = F(Guid.Parse("00000000-0000-4000-8000-000000000007"), Mine, "Second", FolderKind.None, 1);
        m.Apply(Fetched(Mine, new[] { F(MineSub, Mine, "Sub"), sub2 }, new[] { I(1, Mine, "Alpha"), I(2, Mine, "Beta") }));
        Assert.Equal(FolderLoad.Loaded, m.LoadState(Mine));
        Assert.Equal(2, m.ChildFolders(Mine).Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, m.ChildItems(Mine).Select(i => i.Name).OrderBy(n => n).ToArray());
        Assert.Equal("Alpha", m.Item(I(1, Mine, "x").Id)?.Name);
    }

    [Fact]
    public void The_grids_answer_is_the_whole_truth_for_that_folder()
    {
        var (m, _) = New();
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Mine, "Stale") }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        m.EnsureLoaded(Mine);
        m.Apply(Fetched(Mine, new[] { F(MineSub, Mine, "Sub") }, new[] { I(2, Mine, "Fresh") }));
        Assert.Equal(new[] { "Fresh" }, m.ChildItems(Mine).Select(i => i.Name).ToArray());
        Assert.Null(m.Item(I(1, Mine, "x").Id));
    }

    [Fact]
    public void A_failed_answer_marks_the_folder_failed_and_it_can_be_tried_again()
    {
        var (m, requests) = New();
        m.EnsureLoaded(Mine);
        m.Apply(Fetched(Mine, ok: false));
        Assert.Equal(FolderLoad.Failed, m.LoadState(Mine));
        Assert.False(m.EnsureLoaded(Mine) && requests.Count > 1);   // opening again does not hammer the grid by itself
        Assert.True(m.Retry(Mine));
        Assert.Equal(FolderLoad.Loading, m.LoadState(Mine));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public void Only_a_few_requests_are_in_flight_and_the_rest_wait_their_turn()
    {
        var (m, requests) = New(max: 2);
        m.EnsureLoaded(Textures);
        m.EnsureLoaded(Trash);
        m.EnsureLoaded(Clothing);
        m.EnsureLoaded(Mine);
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, m.InFlight);
        Assert.Equal(FolderLoad.Loading, m.LoadState(Mine));   // waiting counts as on its way
        m.Apply(Fetched(Textures));
        Assert.Equal(3, requests.Count);
        m.Apply(Fetched(Trash));
        Assert.Equal(4, requests.Count);
        Assert.Equal(new[] { Textures, Trash, Clothing, Mine }, requests.Select(r => r.Folder).ToArray());
    }

    [Fact]
    public void An_answer_nobody_asked_for_is_still_taken_when_the_folder_is_known()
    {
        var (m, requests) = New();
        m.Apply(Fetched(Textures, items: new[] { I(3, Textures, "Pic", ItemKind.Texture) }));
        Assert.Equal(FolderLoad.Loaded, m.LoadState(Textures));
        Assert.Empty(requests);
        m.Apply(Fetched(Guid.NewGuid()));   // a folder the model has never heard of changes nothing
        Assert.Equal(1, m.ChildItems(Textures).Count);
    }

    // ---------------- pushes ----------------

    [Fact]
    public void A_pushed_item_appears_in_a_loaded_folder_and_a_move_takes_it_out_of_the_old_one()
    {
        var (m, _) = New();
        m.EnsureLoaded(Mine); m.Apply(Fetched(Mine, new[] { F(MineSub, Mine, "Sub") }));
        m.EnsureLoaded(Textures); m.Apply(Fetched(Textures));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Mine, "Alpha") }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Single(m.ChildItems(Mine));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Textures, "Alpha") }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Empty(m.ChildItems(Mine));
        Assert.Single(m.ChildItems(Textures));
    }

    [Fact]
    public void A_pushed_rename_changes_the_name_and_nothing_else()
    {
        var (m, _) = New();
        m.Apply(new InventoryChanged(new[] { F(Mine, Root, "Renamed", FolderKind.None, 7) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Equal("Renamed", m.Folder(Mine)?.Name);
        Assert.Equal(Root, m.Folder(Mine)?.ParentId);
        Assert.Equal(new[] { MineSub }, m.ChildFolders(Mine).Select(f => f.Id).ToArray());
    }

    [Fact]
    public void A_pushed_new_folder_appears_under_its_parent()
    {
        var (m, _) = New();
        var fresh = Guid.Parse("00000000-0000-4000-8000-0000000000f1");
        m.Apply(new InventoryChanged(new[] { F(fresh, Mine, "New Folder") }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Contains(fresh, m.ChildFolders(Mine).Select(f => f.Id));
        Assert.False(m.IsInLibrary(fresh));
    }

    [Fact]
    public void Removing_a_folder_removes_everything_under_it()
    {
        var (m, _) = New();
        m.EnsureLoaded(MineSub); m.Apply(Fetched(MineSub, items: new[] { I(4, MineSub, "Deep") }));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>(), new[] { Mine }, Array.Empty<Guid>()));
        Assert.Null(m.Folder(Mine));
        Assert.Null(m.Folder(MineSub));
        Assert.Null(m.Item(I(4, MineSub, "x").Id));
        Assert.Empty(m.AllItems());
    }

    [Fact]
    public void Removing_an_item_removes_just_that_item()
    {
        var (m, _) = New();
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Mine, "A"), I(2, Mine, "B") }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), new[] { I(1, Mine, "A").Id }));
        Assert.Equal(new[] { "B" }, m.ChildItems(Mine).Select(i => i.Name).ToArray());
    }

    [Fact]
    public void A_push_into_a_folder_not_yet_opened_is_kept_but_does_not_make_it_loaded()
    {
        var (m, _) = New();
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Clothing, "Shirt", ItemKind.Clothing) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Equal(FolderLoad.NotLoaded, m.LoadState(Clothing));
        Assert.Single(m.ChildItems(Clothing));
    }

    [Fact]
    public void Applying_the_same_change_twice_is_harmless_and_raises_nothing_the_second_time()
    {
        var (m, _) = New();
        int changes = 0;
        m.Changed += () => changes++;
        var change = new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(1, Mine, "A") }, Array.Empty<Guid>(), Array.Empty<Guid>());
        m.Apply(change);
        int rev = m.Revision;
        m.Apply(change);
        Assert.Equal(1, changes);
        Assert.Equal(rev, m.Revision);
        Assert.Single(m.ChildItems(Mine));
    }

    [Fact]
    public void Every_real_change_raises_changed_and_moves_the_revision_on()
    {
        var (m, _) = New();
        int changes = 0;
        m.Changed += () => changes++;
        int r0 = m.Revision;
        m.EnsureLoaded(Mine);
        m.Apply(Fetched(Mine, items: new[] { I(1, Mine, "A") }));
        Assert.True(changes >= 1);
        Assert.True(m.Revision > r0);
    }

    // ---------------- lookups ----------------

    [Fact]
    public void Special_folders_are_found_by_kind_in_the_agents_own_tree()
    {
        var (m, _) = New();
        Assert.Equal(Trash, m.FolderOfKind(FolderKind.Trash));
        Assert.Equal(Textures, m.FolderOfKind(FolderKind.Texture));
        Assert.Equal(Guid.Empty, m.FolderOfKind(FolderKind.Landmark));   // none in this skeleton
        Assert.NotEqual(LibObjects, m.FolderOfKind(FolderKind.Object));  // the library's folders do not count
    }

    [Fact]
    public void System_folders_are_the_ones_with_a_purpose_and_the_roots()
    {
        var (m, _) = New();
        Assert.True(m.IsSystemFolder(Root));
        Assert.True(m.IsSystemFolder(Trash));
        Assert.True(m.IsSystemFolder(Textures));
        Assert.False(m.IsSystemFolder(Mine));
        Assert.False(m.IsSystemFolder(MineSub));
        Assert.False(m.IsSystemFolder(Guid.NewGuid()));
    }

    [Fact]
    public void Library_membership_and_descent_follow_the_parents()
    {
        var (m, _) = New();
        Assert.True(m.IsInLibrary(LibObjects));
        Assert.True(m.IsInLibrary(LibRoot));
        Assert.False(m.IsInLibrary(Mine));
        Assert.True(m.IsDescendantOf(MineSub, Mine));
        Assert.True(m.IsDescendantOf(MineSub, Root));
        Assert.False(m.IsDescendantOf(Mine, MineSub));
        Assert.False(m.IsDescendantOf(Mine, Mine));
    }

    [Fact]
    public void Reading_and_applying_from_many_threads_does_not_corrupt_the_model()
    {
        var (m, _) = New();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
                m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(w * 40 + (i % 40), Mine, "N" + i) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) { _ = m.ChildItems(Mine).Count; _ = m.AllItems().Count; }
        }));
        Task.WaitAll(writers.Concat(readers).ToArray());
        Assert.Equal(m.AllItems().Count, m.ChildItems(Mine).Count);
        Assert.InRange(m.ChildItems(Mine).Count, 1, 160);
    }

    // ---------------- refreshing and the whole tree ----------------

    [Fact]
    public void Invalidating_a_loaded_folder_asks_for_it_again()
    {
        var (m, requests) = New();
        m.EnsureLoaded(Mine);
        m.Apply(Fetched(Mine, items: new[] { I(1, Mine, "A") }));
        Assert.Single(requests);
        Assert.True(m.Invalidate(Mine));
        Assert.Equal(2, requests.Count);
        Assert.Equal(FolderLoad.Loading, m.LoadState(Mine));
        Assert.Single(m.ChildItems(Mine));   // what is held stays visible until the answer
        Assert.False(m.Invalidate(Guid.NewGuid()));
    }

    [Fact]
    public void Everything_not_loaded_can_be_asked_for_for_a_search()
    {
        var (m, requests) = New(max: 3);
        m.EnsureLoaded(Textures); m.Apply(Fetched(Textures));
        int queued = m.EnsureAllLoaded();
        Assert.Equal(7, queued);                       // 8 folders in the skeleton, one already loaded
        Assert.Equal(1 + 3, requests.Count);           // the limit still holds
        Assert.Equal(0, m.EnsureAllLoaded());          // nothing more to queue
    }

    [Fact]
    public void Kinds_with_a_purpose_are_protected_and_plain_outfit_and_marketplace_folders_are_not()
    {
        foreach (var k in new[] { FolderKind.Texture, FolderKind.Trash, FolderKind.Root, FolderKind.CurrentOutfit, FolderKind.MyOutfits, FolderKind.Inbox, FolderKind.Settings, FolderKind.LostAndFound })
            Assert.True(InventoryModel.IsProtected(k), k.ToString());
        foreach (var k in new[] { FolderKind.None, FolderKind.Outfit, FolderKind.Other, FolderKind.MarketplaceListings, FolderKind.MarketplaceStock, FolderKind.MarketplaceVersion })
            Assert.False(InventoryModel.IsProtected(k), k.ToString());
    }
}

/// <summary>The protocol-number mapping (pure).</summary>
public class InventoryKindsTests
{
    [Theory]
    [InlineData(-1, FolderKind.None)]
    [InlineData(0, FolderKind.Texture)]
    [InlineData(8, FolderKind.Root)]
    [InlineData(14, FolderKind.Trash)]
    [InlineData(46, FolderKind.CurrentOutfit)]
    [InlineData(47, FolderKind.Outfit)]
    [InlineData(26, FolderKind.Other)]      // an old "ensemble" folder is a plain folder here
    [InlineData(9999, FolderKind.Other)]
    public void Folder_numbers_map_to_kinds(int number, FolderKind kind) => Assert.Equal(kind, InventoryKinds.FolderKindOf(number));

    [Theory]
    [InlineData(0, ItemKind.Texture)]
    [InlineData(1, ItemKind.Sound)]
    [InlineData(5, ItemKind.Clothing)]
    [InlineData(6, ItemKind.Object)]
    [InlineData(10, ItemKind.Script)]
    [InlineData(13, ItemKind.Bodypart)]
    [InlineData(24, ItemKind.Link)]
    [InlineData(25, ItemKind.LinkFolder)]
    [InlineData(49, ItemKind.Mesh)]
    [InlineData(255, ItemKind.Other)]
    [InlineData(-1, ItemKind.Other)]
    public void Asset_numbers_map_to_item_kinds(int number, ItemKind kind) => Assert.Equal(kind, InventoryKinds.ItemKindOf(number));
}

/// <summary>A folder changed while its fetch was on the way is fetched once more (the answer may predate the change).</summary>
public class InventoryRefetchTests
{
    [Fact]
    public void A_folder_changed_while_its_request_is_out_is_asked_for_again_when_the_answer_arrives()
    {
        var requests = new List<Guid>();
        var m = new InventoryModel((f, _) => requests.Add(f));
        m.Apply(InventoryModelTests.Skeleton());
        m.EnsureLoaded(InventoryModelTests.Mine);
        Assert.True(m.Invalidate(InventoryModelTests.Mine));        // a change was made meanwhile
        Assert.Single(requests);
        m.Apply(new InventoryFolderFetched(InventoryModelTests.Mine, true, 1, 0, new List<InventoryFolderData>(), new List<InventoryItemData>()));
        Assert.Equal(2, requests.Count);
        Assert.Equal(FolderLoad.Loading, m.LoadState(InventoryModelTests.Mine));
        m.Apply(new InventoryFolderFetched(InventoryModelTests.Mine, true, 2, 0, new List<InventoryFolderData>(), new List<InventoryItemData>()));
        Assert.Equal(2, requests.Count);
        Assert.Equal(FolderLoad.Loaded, m.LoadState(InventoryModelTests.Mine));
    }
}

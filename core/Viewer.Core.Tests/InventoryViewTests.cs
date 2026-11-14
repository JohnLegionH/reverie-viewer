// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The inventory window's logic without the engine: the lines in view, their order, expansion, search, the recent list,
/// icons and the status line. The model is fed the transport's records directly. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class InventoryViewTests
{
    private static (InventoryViewModel View, InventoryModel Model, List<(Guid Folder, Guid Owner)> Requests) New(int max = 8)
    {
        var requests = new List<(Guid, Guid)>();
        var model = new InventoryModel((f, o) => { lock (requests) requests.Add((f, o)); }, max);
        model.Apply(Skeleton());
        return (new InventoryViewModel(model, "My Inventory", "Library"), model, requests);
    }

    private static InventoryFolderFetched Fetched(Guid folder, IEnumerable<InventoryFolderData>? folders = null, IEnumerable<InventoryItemData>? items = null) =>
        new(folder, true, 9, 0, (folders ?? Array.Empty<InventoryFolderData>()).ToList(), (items ?? Array.Empty<InventoryItemData>()).ToList());

    private static string[] Names(IEnumerable<InventoryRow> rows) => rows.Select(r => r.Name).ToArray();

    // ---------------- the first picture ----------------

    [Fact]
    public void The_tree_starts_with_my_inventory_open_and_the_library_closed()
    {
        var (v, _, requests) = New();
        var rows = v.Rows();
        Assert.Equal("My Inventory", rows.FirstOrDefault()?.Name);
        Assert.True(rows.FirstOrDefault()?.Expanded == true);
        Assert.Equal(0, rows.FirstOrDefault()?.Depth);
        Assert.Contains(rows, r => r.Name == "Library" && !r.Expanded && r.IsLibrary && r.Depth == 0);
        Assert.DoesNotContain(rows, r => r.Name == "Objects");   // the library is closed
        Assert.Equal(Root, Assert.Single(requests).Folder);      // opening my inventory asked for it, once
        v.Rows();
        Assert.Single(requests);
    }

    [Fact]
    public void Before_the_skeleton_there_is_nothing_to_show()
    {
        var v = new InventoryViewModel(new InventoryModel(), "My Inventory", "Library");
        Assert.Empty(v.Rows());
        Assert.Equal("inventory.status.waiting", v.Status().Key);
    }

    // ---------------- order ----------------

    [Fact]
    public void Folders_come_system_first_then_trash_then_the_rest_each_by_name_then_items()
    {
        var (v, m, _) = New();
        m.Apply(Fetched(Root, new[] { F(Textures, Root, "Textures", FolderKind.Texture), F(Trash, Root, "Trash", FolderKind.Trash), F(Clothing, Root, "Clothing", FolderKind.Clothing), F(Mine, Root, "mine", FolderKind.None) },
            new[] { I(1, Root, "banana"), I(2, Root, "Apple") }));
        var under = Names(v.Rows().Where(r => r.Depth == 1));
        Assert.Equal(new[] { "Clothing", "Textures", "Trash", "mine", "Apple", "banana" }, under);
    }

    [Fact]
    public void Items_sort_by_name_ignoring_case_or_by_date_newest_first_and_folders_keep_their_name_order()
    {
        var (v, m, _) = New();
        var t = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        m.Apply(Fetched(Root, new[] { F(Mine, Root, "Zed"), F(MineSub, Root, "Alpha") },
            new[] { I(1, Root, "b-old", at: t), I(2, Root, "A-new", at: t.AddDays(2)), I(3, Root, "c-mid", at: t.AddDays(1)) }));
        v.Sort = InventorySort.ByName;
        Assert.Equal(new[] { "Alpha", "Zed", "A-new", "b-old", "c-mid" }, Names(v.Rows().Where(r => r.Depth == 1)));
        v.Sort = InventorySort.ByDate;
        Assert.Equal(new[] { "Alpha", "Zed", "A-new", "c-mid", "b-old" }, Names(v.Rows().Where(r => r.Depth == 1)));
    }

    [Fact]
    public void Items_with_the_same_date_fall_back_to_name_order()
    {
        var (v, m, _) = New();
        var t = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        m.Apply(Fetched(Root, items: new[] { I(1, Root, "b", at: t), I(2, Root, "a", at: t) }));
        v.Sort = InventorySort.ByDate;
        Assert.Equal(new[] { "a", "b" }, Names(v.Rows().Where(r => !r.IsFolder)));
    }

    // ---------------- expanding ----------------

    [Fact]
    public void Opening_a_folder_asks_for_it_shows_it_loading_and_then_its_contents()
    {
        var (v, m, requests) = New();
        v.Rows();
        v.Expand(Textures);
        Assert.Contains((Textures, Owner), requests);
        var row = v.Rows().One(r => r.Id == Textures);
        Assert.True(row.Expanded);
        Assert.Equal(FolderLoad.Loading, row.Load);
        m.Apply(Fetched(Textures, items: new[] { I(1, Textures, "Pic", ItemKind.Texture) }));
        var rows = v.Rows();
        Assert.Equal(FolderLoad.Loaded, rows.One(r => r.Id == Textures).Load);
        Assert.Contains(rows, r => r.Name == "Pic" && r.Depth == 2 && r.ItemKind == ItemKind.Texture);
    }

    [Fact]
    public void Closing_a_folder_hides_what_is_in_it_and_opening_again_does_not_ask_again()
    {
        var (v, m, requests) = New();
        v.Rows();
        v.Expand(Textures);
        m.Apply(Fetched(Textures, items: new[] { I(1, Textures, "Pic") }));
        v.Collapse(Textures);
        Assert.DoesNotContain(v.Rows(), r => r.Name == "Pic");
        int asked = requests.Count;
        v.Toggle(Textures);
        Assert.Contains(v.Rows(), r => r.Name == "Pic");
        Assert.Equal(asked, requests.Count);
        Assert.True(v.IsExpanded(Textures));
    }

    [Fact]
    public void A_loaded_empty_folder_cannot_be_expanded_and_a_failed_one_says_so()
    {
        var (v, m, _) = New();
        v.Rows();
        v.Expand(Textures); m.Apply(Fetched(Textures));
        v.Expand(Trash); m.Apply(new InventoryFolderFetched(Trash, false, -1, -1, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));
        var rows = v.Rows();
        Assert.False(rows.One(r => r.Id == Textures).CanExpand);
        Assert.Equal(FolderLoad.Failed, rows.One(r => r.Id == Trash).Load);
        Assert.True(rows.One(r => r.Id == Mine).CanExpand);   // not looked at yet: might hold something
    }

    [Fact]
    public void Row_flags_say_which_folders_are_system_trash_or_library()
    {
        var (v, _, _) = New();
        v.Expand(LibRoot);
        var rows = v.Rows();
        Assert.True(rows.One(r => r.Id == Trash).IsTrash);
        Assert.True(rows.One(r => r.Id == Trash).IsSystem);
        Assert.False(rows.One(r => r.Id == Mine).IsSystem);
        Assert.True(rows.One(r => r.Id == LibObjects).IsLibrary);
        Assert.False(rows.One(r => r.Id == Mine).IsLibrary);
    }

    // ---------------- search ----------------

    private static (InventoryViewModel V, InventoryModel M, List<(Guid Folder, Guid Owner)> R) Stocked()
    {
        var (v, m, r) = New();
        m.Apply(Fetched(Root, new[] { F(Textures, Root, "Textures", FolderKind.Texture), F(Mine, Root, "Mine") }, new[] { I(1, Root, "Alpha Plans") }));
        m.Apply(Fetched(Textures, items: new[] { I(2, Textures, "Beta plans", ItemKind.Texture), I(3, Textures, "Gamma") }));
        m.Apply(Fetched(Mine, new[] { F(MineSub, Mine, "Sub") }, new[] { I(4, Mine, "PLANS draft") }));
        m.Apply(Fetched(MineSub, items: new[] { I(5, MineSub, "Deep plans note") }));
        m.Apply(Fetched(Clothing)); m.Apply(Fetched(Trash));
        return (v, m, r);
    }

    [Fact]
    public void Search_finds_names_by_a_case_blind_part_and_keeps_the_folders_that_lead_to_them()
    {
        var (v, _, _) = Stocked();
        v.SearchText = "plans";
        var rows = v.Rows();
        Assert.Equal(new[] { "Alpha Plans", "Beta plans", "Deep plans note", "PLANS draft" }, Names(rows.Where(r => !r.IsFolder)).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(rows, r => r.Name == "Gamma");
        Assert.Contains(rows, r => r.Name == "Sub" && r.Expanded);        // the way to "Deep plans note"
        Assert.Contains(rows, r => r.Name == "Textures" && r.Expanded);
        Assert.DoesNotContain(rows, r => r.Name == "Clothing");           // nothing in it matches
    }

    [Fact]
    public void Search_with_several_parts_joined_by_plus_needs_all_of_them()
    {
        var (v, _, _) = Stocked();
        v.SearchText = "deep+note";
        Assert.Equal(new[] { "Deep plans note" }, Names(v.Rows().Where(r => !r.IsFolder)));
        v.SearchText = "deep+gamma";
        Assert.Empty(v.Rows().Where(r => !r.IsFolder));
    }

    [Fact]
    public void Search_ignores_leading_space_and_clearing_it_brings_the_tree_back()
    {
        var (v, _, _) = Stocked();
        v.SearchText = "   gamma";
        Assert.Equal(new[] { "Gamma" }, Names(v.Rows().Where(r => !r.IsFolder)));
        v.SearchText = "";
        Assert.Contains(v.Rows(), r => r.Name == "Alpha Plans" && r.Depth == 1);
        Assert.DoesNotContain(v.Rows(), r => r.Name == "Gamma");   // Textures was not opened by the user
    }

    [Fact]
    public void Searching_asks_for_every_folder_not_yet_loaded_and_says_it_is_still_looking()
    {
        var (v, m, requests) = New(max: 2);
        v.Rows();
        requests.Clear();
        v.SearchText = "plans";
        v.Rows();
        Assert.NotEmpty(requests);
        Assert.True(m.InFlight <= 2);
        var status = v.Status();
        Assert.Equal("inventory.status.searching", status.Key);
    }

    [Fact]
    public void A_finished_search_reports_how_many_it_found()
    {
        var (v, _, _) = Stocked();
        v.SearchText = "plans";
        v.Rows();
        // the library's folder is still unloaded, so the search says it may be incomplete
        Assert.Equal("inventory.status.searching", v.Status().Key);
    }

    // ---------------- recent ----------------

    [Fact]
    public void Recent_lists_the_newest_items_of_the_agents_own_inventory_flat()
    {
        var (v, m, _) = New();
        var t = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        m.Apply(Fetched(Root, items: new[] { I(1, Root, "older", at: t), I(2, Root, "newest", at: t.AddDays(5)) }));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(3, LibObjects, "library thing", at: t.AddDays(9)) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        v.Tab = InventoryTab.Recent;
        var rows = v.Rows();
        Assert.Equal(new[] { "newest", "older" }, Names(rows));
        Assert.All(rows, r => Assert.Equal(0, r.Depth));
    }

    [Fact]
    public void Recent_with_no_stored_logout_time_lists_every_item_newest_first_and_search_narrows_it()
    {
        // The official viewer's "since logoff" filter compares with a stored time that starts at 0, so with none stored every item passes
        // (llinventoryfilter.cpp, setDateRangeLastLogoff and the date test in checkAgainstFilterType).
        const int many = 80;
        var (v, m, _) = New();
        var t = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        m.Apply(Fetched(Root, items: Enumerable.Range(1, many).Select(n => I(n, Root, "item " + n, at: t.AddMinutes(n)))));
        v.Tab = InventoryTab.Recent;
        Assert.Null(v.RecentSince);
        Assert.Equal(many, v.Rows().Count);
        Assert.Equal("item " + many, v.Rows().FirstOrDefault()?.Name);
        Assert.Equal("item 1", v.Rows().LastOrDefault()?.Name);
        v.SearchText = "item 7";
        Assert.NotEmpty(v.Rows());
        Assert.All(v.Rows(), r => Assert.Contains("item 7", r.Name));
    }

    // ---------------- icons and status ----------------

    [Fact]
    public void Every_kind_has_an_icon_and_the_main_kinds_have_different_ones()
    {
        foreach (var k in Enum.GetValues<FolderKind>())
        {
            Assert.False(string.IsNullOrEmpty(InventoryIcons.ForFolder(k, false)), k.ToString());
            Assert.False(string.IsNullOrEmpty(InventoryIcons.ForFolder(k, true)), k.ToString());
        }
        foreach (var k in Enum.GetValues<ItemKind>())
            Assert.False(string.IsNullOrEmpty(InventoryIcons.ForItem(k, false)), k.ToString());
        var main = new[] { ItemKind.Texture, ItemKind.Sound, ItemKind.CallingCard, ItemKind.Landmark, ItemKind.Clothing, ItemKind.Object, ItemKind.Notecard, ItemKind.Script, ItemKind.Bodypart, ItemKind.Animation, ItemKind.Gesture, ItemKind.Mesh };
        Assert.Equal(main.Length, main.Select(k => InventoryIcons.ForItem(k, false)).Distinct().Count());
        Assert.NotEqual(InventoryIcons.ForItem(ItemKind.Notecard, false), InventoryIcons.ForItem(ItemKind.Notecard, true));
        Assert.NotEqual(InventoryIcons.ForFolder(FolderKind.None, false), InventoryIcons.ForFolder(FolderKind.Trash, false));
        Assert.NotEqual(InventoryIcons.ForFolder(FolderKind.None, false), InventoryIcons.ForFolder(FolderKind.None, true));
        Assert.Equal(InventoryIcons.All.Count, InventoryIcons.All.Distinct().Count());
        Assert.Contains(InventoryIcons.ForItem(ItemKind.Link, false), InventoryIcons.All);
    }

    [Fact]
    public void Rows_carry_the_icon_for_what_they_are()
    {
        var (v, m, _) = New();
        m.Apply(Fetched(Root, new[] { F(Trash, Root, "Trash", FolderKind.Trash) }, new[] { I(1, Root, "L", ItemKind.Notecard, link: Guid.NewGuid()) }));
        var rows = v.Rows();
        Assert.Equal(InventoryIcons.ForFolder(FolderKind.Trash, false), rows.One(r => r.Id == Trash).IconKey);
        Assert.Equal(InventoryIcons.ForFolder(FolderKind.Root, true), rows.FirstOrDefault()?.IconKey);
        Assert.True(rows.One(r => r.Name == "L").IsLink);
        Assert.Equal(InventoryIcons.ForItem(ItemKind.Notecard, true), rows.One(r => r.Name == "L").IconKey);
    }

    [Fact]
    public void The_status_line_says_fetching_while_requests_are_out_and_complete_when_everything_is_here()
    {
        var (v, m, _) = New();
        v.Rows();
        Assert.Equal("inventory.status.fetching", v.Status().Key);
        foreach (var f in m.AllFolders().ToList())
        {
            m.EnsureLoaded(f.Id);
            m.Apply(Fetched(f.Id));
        }
        for (int i = 0; i < 10 && m.UnloadedFolderCount > 0; i++)
            foreach (var f in m.AllFolders().ToList()) { m.Apply(Fetched(f.Id)); }
        Assert.Equal("inventory.status.complete", v.Status().Key);
    }
}

internal static class RowAsserts
{
    /// <summary>The one line matching, as an assertion (a missing or doubled line fails the test, it does not throw from a lookup).</summary>
    public static InventoryRow One(this IEnumerable<InventoryRow> rows, Func<InventoryRow, bool> match) => Assert.Single(rows.Where(match));
}

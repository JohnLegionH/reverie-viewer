// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Viewer.Shell.Outfit;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Worn tab: exactly the items the avatar wears (the current outfit's links) or has attached (what the world says it wears),
/// under the folders that hold them. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class InventoryWornTabTests
{
    private static Guid G(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");
    private static readonly Guid Owner = G(1), Root = G(2), LibOwner = G(3), LibRoot = G(4), Cof = G(5), Clothes = G(6), Objects = G(7), Inner = G(8), Empty = G(9);
    private static readonly Guid Shirt = G(20), Shape = G(21), Hat = G(22), Ring = G(23), Spare = G(24), Wave = G(25);

    private static InventoryItemData I(Guid id, Guid parent, string name, ItemKind kind, Guid? link = null) =>
        new(id, parent, name, kind, 0, 0, 0, null, link);

    private static InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None) => new(id, parent, name, kind, 1, 1);

    private sealed record Rig(InventoryModel Model, InventoryViewModel View, HashSet<Guid> Attached, List<Guid> Asked);

    private static Rig Make(bool clothesLoaded = true)
    {
        var asked = new List<Guid>();
        var model = new InventoryModel((folder, _) => asked.Add(folder));
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            [F(Root, Guid.Empty, "My Inventory", FolderKind.Root), F(Cof, Root, "Current Outfit", FolderKind.CurrentOutfit), F(Clothes, Root, "Clothing", FolderKind.Clothing),
             F(Objects, Root, "Objects", FolderKind.Object), F(Inner, Objects, "Jewellery"), F(Empty, Root, "Photos")],
            [F(LibRoot, Guid.Empty, "Library", FolderKind.Root)]));
        void Load(Guid folder, params InventoryItemData[] items) => model.Apply(new InventoryFolderFetched(folder, true, 2, items.Length, [], items));
        model.Apply(new InventoryFolderFetched(Root, true, 2, 4,
            [F(Cof, Root, "Current Outfit", FolderKind.CurrentOutfit), F(Clothes, Root, "Clothing", FolderKind.Clothing), F(Objects, Root, "Objects", FolderKind.Object), F(Empty, Root, "Photos")], []));
        Load(Cof, I(G(30), Cof, "Shirt", ItemKind.Link, Shirt), I(G(31), Cof, "Shape", ItemKind.Link, Shape), I(G(32), Cof, "Hat", ItemKind.Link, Hat),
            I(G(33), Cof, "Wave", ItemKind.Link, Wave));
        if (clothesLoaded)
            Load(Clothes, I(Shirt, Clothes, "Shirt", ItemKind.Clothing), I(Shape, Clothes, "Shape", ItemKind.Bodypart), I(Spare, Clothes, "Spare coat", ItemKind.Clothing),
                I(Wave, Clothes, "Wave", ItemKind.Gesture));
        model.Apply(new InventoryFolderFetched(Objects, true, 2, 2, [F(Inner, Objects, "Jewellery")], [I(Hat, Objects, "Hat", ItemKind.Object)]));
        Load(Inner, I(Ring, Inner, "Ring", ItemKind.Object));
        var attached = new HashSet<Guid> { Ring };   // put on from the ground: no link in the current outfit
        var view = new InventoryViewModel(model, "My Inventory", "Library") { Tab = InventoryTab.Worn, WornItems = () => WornList.Items(model, attached) };
        return new Rig(model, view, attached, asked);
    }

    private static Guid[] Items(InventoryViewModel v) => v.Rows().Where(r => !r.IsFolder).Select(r => r.Id).OrderBy(g => g).ToArray();

    [Fact]
    public void It_lists_exactly_the_worn_and_the_attached_items() =>
        Assert.Equal(new[] { Shirt, Shape, Hat, Ring }, Items(Make().View));

    [Fact]
    public void Only_the_folders_that_hold_a_worn_item_are_shown_and_they_are_open()
    {
        var folders = Make().View.Rows().Where(r => r.IsFolder).ToList();
        Assert.Equal(new[] { "My Inventory", "Clothing", "Objects", "Jewellery" }, folders.Select(f => f.Name));
        Assert.All(folders, f => Assert.True(f.Expanded));
    }

    [Fact]
    public void Taking_off_and_wearing_change_the_list()
    {
        var rig = Make();
        Assert.Contains(Ring, Items(rig.View));
        rig.Attached.Remove(Ring);
        rig.Model.Apply(new InventoryChanged([], [], [], [G(30)]));   // the shirt's link goes
        Assert.Equal(new[] { Shape, Hat }, Items(rig.View));
        rig.Model.Apply(new InventoryChanged([], [I(G(34), Cof, "Spare coat", ItemKind.Link, Spare)], [], []));
        Assert.Equal(new[] { Shape, Hat, Spare }, Items(rig.View));
    }

    [Fact]
    public void A_search_narrows_the_worn_list()
    {
        var rig = Make();
        rig.View.SearchText = "sh";
        Assert.Equal(new[] { Shirt, Shape }, Items(rig.View));
    }

    [Fact]
    public void A_worn_item_not_fetched_yet_asks_for_the_folders_and_says_it_is_loading()
    {
        var rig = Make(clothesLoaded: false);
        rig.View.Rows();
        Assert.Contains(Clothes, rig.Asked);
        Assert.Equal("inventory.status.worn_loading", rig.View.Status().Key);
    }

    [Fact]
    public void With_everything_loaded_the_status_counts_the_worn_items_and_asks_for_no_folder()
    {
        var rig = Make();
        rig.View.Rows();
        Assert.Empty(rig.Asked);
        Assert.Equal("inventory.status.worn", rig.View.Status().Key);
    }

    [Fact]
    public void The_other_tabs_do_not_ask_what_is_worn()
    {
        var rig = Make();
        int calls = 0;
        rig.View.WornItems = () => { calls++; return new HashSet<Guid>(); };
        rig.View.Tab = InventoryTab.All;
        rig.View.Rows();
        rig.View.Tab = InventoryTab.Recent;
        rig.View.Rows();
        Assert.Equal(0, calls);
        rig.View.Tab = InventoryTab.Worn;
        rig.View.Rows();
        Assert.True(calls > 0);
    }

    [Fact]
    public void The_automation_names_the_worn_tab()
    {
        Assert.True(InventoryTabNames.TryParse(" Worn ", out var tab));
        Assert.Equal(InventoryTab.Worn, tab);
    }
}

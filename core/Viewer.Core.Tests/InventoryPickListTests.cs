// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Build window's material list finds every material: it lists every item of the material type in the whole inventory, fetching the
/// folders not yet loaded (the official picker starts a fetch of the whole inventory, lltexturectrl.cpp, read as behaviour), with a search,
/// and says "No materials in your inventory" when there are none. A fake inventory holds a material three folders deep in an ordinary folder.
/// No engine and no grid; each test builds its own model, so the class runs in parallel.
/// </summary>
public class InventoryPickListTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-4000-8000-0000000000f0");
    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid Materials = Guid.Parse("00000000-0000-4000-8000-000000000002");
    private static readonly Guid Trash = Guid.Parse("00000000-0000-4000-8000-000000000003");
    private static readonly Guid A = Guid.Parse("00000000-0000-4000-8000-000000000011");
    private static readonly Guid B = Guid.Parse("00000000-0000-4000-8000-000000000012");
    private static readonly Guid C = Guid.Parse("00000000-0000-4000-8000-000000000013");
    private static readonly Guid LibOwner = Guid.Parse("00000000-0000-4000-8000-0000000000f1");
    private static readonly Guid LibRoot = Guid.Parse("00000000-0000-4000-8000-0000000000a1");

    private static InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None) => new(id, parent, name, kind, 1, -1);

    private static InventoryItemData Mat(int n, Guid parent, string name) =>
        new(Guid.Parse($"00000000-0000-4000-8000-2000000000{n:x2}"), parent, name, ItemKind.Material, 57, 57, 0, null, AssetId: Guid.Parse($"00000000-0000-4000-8000-3000000000{n:x2}"));

    private static (InventoryModel Model, List<Guid> Requested) New()
    {
        var requested = new List<Guid>();
        var m = new InventoryModel((f, _) => requested.Add(f), 64);
        m.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            [F(Root, Guid.Empty, "My Inventory", FolderKind.Root), F(Materials, Root, "Materials", FolderKind.Material), F(Trash, Root, "Trash", FolderKind.Trash),
             F(A, Root, "Builds"), F(B, A, "House"), F(C, B, "Kitchen")],
            [F(LibRoot, Guid.Empty, "Library", FolderKind.Root)]));
        return (m, requested);
    }

    private static InventoryFolderFetched Fetched(Guid folder, params InventoryItemData[] items) => new(folder, true, 9, items.Length, [], items.ToList());

    [Fact]
    public void Every_folder_not_yet_loaded_is_fetched_not_only_the_Materials_folder()
    {
        var (m, requested) = New();
        var list = new InventoryPickList(m, ItemKind.Material);
        list.Fetch();
        Assert.Contains(C, requested);
        Assert.Contains(A, requested);
        Assert.Contains(Materials, requested);
    }

    [Fact]
    public void A_material_three_folders_deep_is_listed_once_its_folder_arrives_and_the_search_finds_it()
    {
        var (m, _) = New();
        var list = new InventoryPickList(m, ItemKind.Material);
        list.Fetch();
        m.Apply(Fetched(C, Mat(1, C, "Brushed steel")));
        m.Apply(Fetched(Materials, Mat(2, Materials, "Oak")));
        Assert.Equal(["Brushed steel", "Oak"], list.Rows("").Select(r => r.Name));
        Assert.Equal(["Brushed steel"], list.Rows("steel").Select(r => r.Name));
    }

    [Fact]
    public void An_item_in_the_Trash_or_of_another_kind_is_not_listed()
    {
        var (m, _) = New();
        var list = new InventoryPickList(m, ItemKind.Material);
        list.Fetch();
        m.Apply(Fetched(Trash, Mat(3, Trash, "Thrown away")));
        m.Apply(Fetched(B, new InventoryItemData(Guid.NewGuid(), B, "A texture", ItemKind.Texture, 0, 0, 0, null, AssetId: Guid.NewGuid())));
        Assert.Empty(list.Rows(""));
    }

    [Fact]
    public void With_none_it_says_so_once_everything_is_loaded()
    {
        var (m, _) = New();
        var list = new InventoryPickList(m, ItemKind.Material);
        list.Fetch();
        Assert.False(list.AllLoaded);
        foreach (var f in new[] { Root, Materials, Trash, A, B, C, LibRoot })
            m.Apply(Fetched(f));
        Assert.True(list.AllLoaded);
        Assert.Empty(list.Rows(""));
        Assert.Equal("edit.tex.material.none", list.EmptyKey);
        Assert.Equal("No materials in your inventory", Viewer.Shell.StringTable.English().Get(list.EmptyKey!));
    }
}

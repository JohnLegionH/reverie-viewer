// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A hypergrid suitcase folder ("My Suitcase") holds its own copies of the system folders (Current Outfit, Trash and the rest), and the
/// login skeleton lists them with the real ones. The viewer's own folder of a kind is the one directly under My Inventory, as the
/// official viewer finds it (LLInventoryModel::findCategoryUUIDForType looks only at the root's children), so the outfit code never
/// reads or refreshes the suitcase's copy. The tree is drawn again only when what it shows changed, so a folder that is read again
/// with the same answer does not make the window jump. Pure, runs in parallel.
/// </summary>
public class InventorySuitcaseTests
{
    private static readonly Guid Owner = Guid.NewGuid(), Root = Guid.NewGuid(), LibOwner = Guid.NewGuid(), LibRoot = Guid.NewGuid();
    private static readonly Guid Suitcase = Guid.NewGuid(), SuitcaseCof = Guid.NewGuid(), SuitcaseTrash = Guid.NewGuid();
    private static readonly Guid Cof = Guid.NewGuid(), Trash = Guid.NewGuid(), Objects = Guid.NewGuid();

    private static InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None) => new(id, parent, name, kind, 1, 0);

    /// <summary>A skeleton that lists the suitcase and its copies before the real system folders (the grid's order is not fixed).</summary>
    private static (InventoryModel Model, List<Guid> Asked) Model()
    {
        var asked = new List<Guid>();
        var m = new InventoryModel((folder, _) => asked.Add(folder));
        m.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot, new[]
        {
            F(Root, Guid.Empty, "My Inventory", FolderKind.Root),
            F(Suitcase, Root, "My Suitcase", InventoryKinds.FolderKindOf(100)),
            F(SuitcaseCof, Suitcase, "Current Outfit", FolderKind.CurrentOutfit),
            F(SuitcaseTrash, Suitcase, "Trash", FolderKind.Trash),
            F(Cof, Root, "Current Outfit", FolderKind.CurrentOutfit),
            F(Trash, Root, "Trash", FolderKind.Trash),
            F(Objects, Root, "Objects", FolderKind.Object),
        }, new[] { F(LibRoot, Guid.Empty, "Library", FolderKind.Root) }));
        return (m, asked);
    }

    [Fact]
    public void The_own_folder_of_a_kind_is_the_one_under_My_Inventory_not_the_suitcases_copy()
    {
        var (m, _) = Model();
        Assert.Equal(Cof, m.FolderOfKind(FolderKind.CurrentOutfit));
        Assert.Equal(Trash, m.FolderOfKind(FolderKind.Trash));
    }

    [Fact]
    public void A_kind_found_only_below_the_top_level_is_still_found()
    {
        var m = new InventoryModel();
        Guid nested = Guid.NewGuid(), plain = Guid.NewGuid();
        m.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot, new[]
        {
            F(Root, Guid.Empty, "My Inventory", FolderKind.Root), F(plain, Root, "Stuff"), F(nested, plain, "Outfits", FolderKind.MyOutfits),
        }, Array.Empty<InventoryFolderData>()));
        Assert.Equal(nested, m.FolderOfKind(FolderKind.MyOutfits));
    }

    [Fact]
    public void Refreshing_the_current_outfit_never_reads_the_suitcases_copy()
    {
        var (m, asked) = Model();
        m.Invalidate(m.FolderOfKind(FolderKind.CurrentOutfit));
        Assert.Contains(Cof, asked);
        Assert.DoesNotContain(SuitcaseCof, asked);
    }

    private static InventoryFolderFetched SuitcaseAnswer() =>
        new(Suitcase, true, 3, 2, new[] { F(SuitcaseCof, Suitcase, "Current Outfit", FolderKind.CurrentOutfit), F(SuitcaseTrash, Suitcase, "Trash", FolderKind.Trash) }, Array.Empty<InventoryItemData>());

    [Fact]
    public void The_tree_is_drawn_again_only_when_its_rows_changed()
    {
        var (m, _) = Model();
        var view = new InventoryViewModel(m, "My Inventory", "Library");
        var redraw = new InventoryTreeRedraw();
        Assert.True(redraw.Needs(view.Rows()));     // the first picture
        Assert.False(redraw.Needs(view.Rows()));    // nothing changed
        view.Expand(Suitcase);
        m.Apply(SuitcaseAnswer());
        Assert.True(redraw.Needs(view.Rows()));     // the suitcase opened with its contents
        for (int i = 0; i < 5; i++)
        {
            m.Apply(SuitcaseAnswer());              // read again with the same answer
            Assert.False(redraw.Needs(view.Rows()));
        }
        redraw.Reset();
        Assert.True(redraw.Needs(view.Rows()));     // after a reset (a new session) the tree is drawn
    }

    [Fact]
    public void The_suitcase_opens_and_closes_cleanly_while_it_is_read_again_and_again()
    {
        var (m, _) = Model();
        var view = new InventoryViewModel(m, "My Inventory", "Library");
        var redraw = new InventoryTreeRedraw();
        redraw.Needs(view.Rows());
        view.Toggle(Suitcase);
        m.Apply(SuitcaseAnswer());
        var open = view.Rows();
        Assert.True(open.Single(r => r.Id == Suitcase).Expanded);
        Assert.Contains(open, r => r.Id == SuitcaseCof);
        Assert.True(redraw.Needs(open));
        view.Toggle(Suitcase);
        for (int i = 0; i < 5; i++)
        {
            m.Invalidate(Suitcase);
            m.Apply(SuitcaseAnswer());
        }
        var closed = view.Rows();
        Assert.False(view.IsExpanded(Suitcase));
        Assert.False(closed.Single(r => r.Id == Suitcase).Expanded);
        Assert.DoesNotContain(closed, r => r.Id == SuitcaseCof);
        Assert.True(redraw.Needs(closed));          // closing is drawn once
        Assert.False(redraw.Needs(view.Rows()));    // and the reads after it draw nothing
    }

    [Fact]
    public void A_loaded_folder_read_again_keeps_its_rows_while_the_answer_is_on_its_way()
    {
        var (m, _) = Model();
        var view = new InventoryViewModel(m, "My Inventory", "Library");
        view.Expand(Suitcase);
        m.Apply(SuitcaseAnswer());
        var redraw = new InventoryTreeRedraw();
        redraw.Needs(view.Rows());
        m.Invalidate(Suitcase);                     // asked again; the answer has not come yet
        Assert.Equal(FolderLoad.Loading, m.LoadState(Suitcase));
        Assert.False(redraw.Needs(view.Rows()));    // the folder still shows what it had, with no "loading" line
        m.Apply(SuitcaseAnswer());
        Assert.False(redraw.Needs(view.Rows()));
    }

    [Fact]
    public void A_folder_read_for_the_first_time_shows_that_it_is_loading()
    {
        var (m, _) = Model();
        var view = new InventoryViewModel(m, "My Inventory", "Library");
        view.Expand(Suitcase);
        Assert.Equal(FolderLoad.Loading, view.Rows().Single(r => r.Id == Suitcase).Load);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// An item of the Library is copied into the account's inventory, into the folder for its kind, before it is worn, as the
/// official viewer does (LLAppearanceMgr::wearItemsOnAvatar copies a Library item and wears the copy). The planner marks the
/// copies, the intent checks them, and the change names the copies once the grid has made them. Offline; every member builds its
/// own model, so the class runs in parallel.
/// </summary>
public class OutfitLibraryCopyTests
{
    private const int LibShirt = 99, LibHat = 98, LibShape = 97;

    private static Rig WithLibrary()
    {
        var r = new Rig();
        r.Model.Apply(new InventoryFolderFetched(LibObjects, true, 2, 3, Array.Empty<InventoryFolderData>(), new[]
        {
            Thing(LibShirt, LibObjects, "library shirt", ItemKind.Clothing, WearableType.Shirt),
            Thing(LibHat, LibObjects, "library hat", ItemKind.Object),
            Thing(LibShape, LibObjects, "library shape", ItemKind.Bodypart, WearableType.Shape),
        }));
        return r;
    }

    [Fact]
    public void A_library_clothing_item_is_worn_after_a_copy_into_the_clothing_folder()
    {
        var r = WithLibrary();
        var p = OutfitPlanner.Wear(r.Model, Id(LibShirt), add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LibraryCopy(Id(LibShirt), ClothF) }, p.Change!.Copies.ToArray());
        var link = Assert.Single(p.Change.Create);
        Assert.Equal(Id(LibShirt), link.TargetId);
        Assert.Equal("@400", link.Description);   // the layer note is worked out from the item, as for an item of the account
    }

    [Fact]
    public void A_library_object_is_attached_after_a_copy_into_the_objects_folder()
    {
        var r = WithLibrary();
        var p = OutfitPlanner.Attach(r.Model, Id(LibHat), 0, add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LibraryCopy(Id(LibHat), ObjF) }, p.Change!.Copies.ToArray());
        Assert.Equal(Id(LibHat), Assert.Single(p.Change.Attach).ItemId);
    }

    [Fact]
    public void A_library_body_part_replaces_the_worn_one_after_a_copy_into_the_body_parts_folder()
    {
        var r = WithLibrary();
        r.SetCof(LinkTo(1, ShapeS1, "shape one"));
        var p = OutfitPlanner.Wear(r.Model, Id(LibShape), add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LibraryCopy(Id(LibShape), BodyF) }, p.Change!.Copies.ToArray());
        Assert.Equal(new[] { Id(1001) }, p.Change.RemoveLinks.ToArray());
    }

    [Fact]
    public void An_item_of_the_account_needs_no_copy()
    {
        var r = WithLibrary();
        var p = OutfitPlanner.Wear(r.Model, Id(ShirtA), add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Empty(p.Change!.Copies);
    }

    [Fact]
    public void The_menus_offer_wearing_a_library_item()
    {
        var r = WithLibrary();
        Assert.True(OutfitPlanner.Wear(r.Model, Id(LibShirt), add: false).Ok);
        Assert.True(OutfitPlanner.Wear(r.Model, Id(LibShirt), add: true).Ok);
        Assert.True(OutfitPlanner.Attach(r.Model, Id(LibHat), 3, add: true).Ok);
    }

    [Fact]
    public void The_change_names_the_copies_in_place_of_the_library_items_and_leaves_the_rest()
    {
        var copy = Id(500);
        var change = new OutfitChange(CofF,
            new[] { new NewOutfitLink(Id(LibShirt), "library shirt", "@400"), new NewOutfitLink(Id(PantsC), "pants c", "@500") },
            new[] { Id(1001) },
            new[] { new AttachRequest(Id(LibHat), 3, true) },
            new[] { Id(ObjO1) },
            new[] { new LibraryCopy(Id(LibShirt), ClothF), new LibraryCopy(Id(LibHat), ObjF) });
        var made = change.WithCopies(new Dictionary<Guid, Guid> { [Id(LibShirt)] = copy, [Id(LibHat)] = Id(501) });

        Assert.Equal(new[] { copy, Id(PantsC) }, made.Create.Select(l => l.TargetId).ToArray());
        Assert.Equal(new[] { "library shirt", "pants c" }, made.Create.Select(l => l.Name).ToArray());   // names and notes stay
        Assert.Equal(Id(501), Assert.Single(made.Attach).ItemId);
        Assert.Equal(3, made.Attach[0].Point);
        Assert.Equal(change.RemoveLinks, made.RemoveLinks);
        Assert.Equal(change.Detach, made.Detach);
    }

    [Fact]
    public void The_intent_refuses_a_copy_with_no_item_or_no_folder_and_a_copy_that_nothing_uses()
    {
        OutfitChange With(params LibraryCopy[] copies) => new(CofF, new[] { new NewOutfitLink(Id(LibShirt), "s", "@400") }, [], [], [], copies);
        Assert.Empty(new OutfitChangeIntent(With(new LibraryCopy(Id(LibShirt), ClothF))).Validate());
        Assert.Contains(new OutfitChangeIntent(With(new LibraryCopy(Guid.Empty, ClothF))).Validate(), i => i.Code == "outfit.no_item");
        Assert.Contains(new OutfitChangeIntent(With(new LibraryCopy(Id(LibShirt), Guid.Empty))).Validate(), i => i.Code == "outfit.no_item");
        Assert.Contains(new OutfitChangeIntent(With(new LibraryCopy(Id(LibShirt), ClothF), new LibraryCopy(Id(LibHat), ObjF))).Validate(), i => i.Code == "outfit.no_item");
    }
}

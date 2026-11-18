// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Inventory › New: the entries in the official viewer's order with its default names, the asset and inventory types each new
/// item is made with, and the default file of a new clothing or body part item. Pure, runs in parallel.
/// </summary>
public class NewItemTests
{
    [Fact]
    public void The_entries_follow_the_official_menu_order()
    {
        Assert.Equal(new[] { "folder", "script", "notecard", "gesture" },
            NewItemCatalog.Entries.Where(e => e.Group == NewItemGroup.Top).Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "shirt", "pants", "shoes", "socks", "jacket", "skirt", "gloves", "undershirt", "underpants", "alpha", "tattoo", "universal", "physics" },
            NewItemCatalog.Entries.Where(e => e.Group == NewItemGroup.Clothing).Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "shape", "skin", "hair", "eyes" },
            NewItemCatalog.Entries.Where(e => e.Group == NewItemGroup.BodyParts).Select(e => e.Id).ToArray());
        // groups come in that order too: the top entries, then clothing, then body parts
        var groups = NewItemCatalog.Entries.Select(e => e.Group).ToArray();
        Assert.Equal(groups.OrderBy(g => g).ToArray(), groups);
    }

    [Fact]
    public void New_items_get_the_official_default_names()
    {
        Assert.Equal("New Folder", NewItemCatalog.Of("folder")!.DefaultName);
        Assert.Equal("New Script", NewItemCatalog.Of("script")!.DefaultName);
        Assert.Equal("New Note", NewItemCatalog.Of("notecard")!.DefaultName);
        Assert.Equal("New Gesture", NewItemCatalog.Of("gesture")!.DefaultName);
        Assert.Equal("New Skirt", NewItemCatalog.Of("skirt")!.DefaultName);
        Assert.Equal("New Universal", NewItemCatalog.Of("universal")!.DefaultName);
        Assert.Equal("New Shape", NewItemCatalog.Of("shape")!.DefaultName);
        Assert.Null(NewItemCatalog.Of("material"));
    }

    [Fact]
    public void Each_entry_is_made_with_the_official_asset_and_inventory_types()
    {
        Assert.Equal((10, 10, ItemKind.Script), Types("script"));
        Assert.Equal((7, 7, ItemKind.Notecard), Types("notecard"));
        Assert.Equal((21, 20, ItemKind.Gesture), Types("gesture"));
        Assert.Equal((5, 18, ItemKind.Clothing), Types("skirt"));
        Assert.Equal((13, 18, ItemKind.Bodypart), Types("eyes"));
        Assert.Equal(WearableType.Skirt, NewItemCatalog.Of("skirt")!.Wearable);
        Assert.Equal(WearableType.Physics, NewItemCatalog.Of("physics")!.Wearable);
        Assert.Equal(WearableType.None, NewItemCatalog.Of("notecard")!.Wearable);
        Assert.True(NewItemCatalog.Of("folder")!.IsFolder);
    }

    private static (int, int, ItemKind) Types(string id)
    {
        var e = NewItemCatalog.Of(id)!;
        return (e.AssetType, e.InventoryType, e.Kind);
    }

    [Fact]
    public void Scripts_notecards_and_gestures_take_the_default_next_owner_rights()
    {
        // create_new_item overrides the menu's value with LLFloaterPerms::getNextOwnerPerms (llviewerinventory.cpp), whose defaults are
        // move, copy, modify and transfer (llfloaterperms.cpp; settings.xml *NextOwnerCopy, *NextOwnerModify, *NextOwnerTransfer = 1)
        const uint all = 0x80000u | 0x8000u | 0x4000u | 0x2000u;
        Assert.Equal(all, NewItemCatalog.Of("script")!.NextOwnerMask);
        Assert.Equal(all, NewItemCatalog.Of("notecard")!.NextOwnerMask);
        Assert.Equal(all, NewItemCatalog.Of("gesture")!.NextOwnerMask);
    }

    [Fact]
    public void A_new_item_is_described_by_the_time_and_its_kind()
    {
        // generateDescriptionFor (llviewerassettype.cpp): "%Y-%m-%d %H:%M:%S " and the type's readable name (llassettype.cpp)
        var at = new DateTime(2026, 3, 4, 5, 6, 7);
        Assert.Equal("2026-03-04 05:06:07 note card", NewItemCatalog.DescriptionFor(NewItemCatalog.Of("notecard")!, at));
        Assert.Equal("2026-03-04 05:06:07 lsl2 script", NewItemCatalog.DescriptionFor(NewItemCatalog.Of("script")!, at));
        Assert.Equal("2026-03-04 05:06:07 gesture", NewItemCatalog.DescriptionFor(NewItemCatalog.Of("gesture")!, at));
        Assert.Equal("2026-03-04 05:06:07 clothing", NewItemCatalog.DescriptionFor(NewItemCatalog.Of("skirt")!, at));
        Assert.Equal("2026-03-04 05:06:07 body part", NewItemCatalog.DescriptionFor(NewItemCatalog.Of("eyes")!, at));
    }

    [Fact]
    public void A_new_wearable_file_has_its_type_name_and_every_parameter_at_its_default()
    {
        var catalog = EditFixture.Catalog();
        Assert.NotEmpty(catalog.ParamsOf(WearableType.Shirt));
        byte[] file = NewItemCatalog.DefaultWearable(WearableType.Shirt, "New Shirt", catalog);
        Assert.True(WearableAsset.TryParse(file, out var asset, out string error), error);
        Assert.Equal(WearableType.Shirt, asset.Type);
        Assert.Equal("New Shirt", asset.Name);
        foreach (var p in catalog.ParamsOf(WearableType.Shirt))
            Assert.Equal(p.Default, asset.Params[p.Id], 3);
        Assert.DoesNotContain(33, asset.Params.Keys);   // a shape parameter is not in a shirt
    }
}

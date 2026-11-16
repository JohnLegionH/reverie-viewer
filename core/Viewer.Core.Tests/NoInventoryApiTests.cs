// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Inventory on a grid without the inventory API (no InventoryAPIv3 capability): the older item update carries the wearable upload's
/// transaction on the item (Firestorm's UpdateInventoryItem, llviewerinventory.cpp packMessage), an outfit change in a region that does
/// not bake is finished by the viewer's own bake instead of the region's rebuild (Firestorm llappearancemgr.cpp, updateAppearanceFromCOF
/// asks the region only when it bakes centrally), and a wait for a reply the grid may never send ends. Pure values; runs in parallel.
/// </summary>
public class NoInventoryApiTests
{
    [Fact]
    public void The_older_item_update_carries_the_transaction_on_the_item()
    {
        var item = new InventoryItem(UUID.Random())
        {
            ParentUUID = UUID.Random(), Name = "Old shirt", Description = "worn", AssetType = AssetType.Clothing, InventoryType = InventoryType.Wearable,
            AssetUUID = UUID.Random(), OwnerID = UUID.Random(), CreatorID = UUID.Random(), Flags = 5,
        };
        var transaction = Guid.NewGuid();
        var update = LmvTransport.LegacyItemUpdate(item, "New shirt", transaction);
        Assert.Equal(new UUID(transaction), update.TransactionID);
        Assert.Equal("New shirt", update.Name);
        Assert.Equal(item.UUID, update.UUID);
        Assert.Equal(item.ParentUUID, update.ParentUUID);
        Assert.Equal(item.AssetType, update.AssetType);
        Assert.Equal(item.InventoryType, update.InventoryType);
        Assert.Equal(item.Flags, update.Flags);
        Assert.Equal(item.OwnerID, update.OwnerID);
        Assert.Equal("worn", update.Description);
    }

    [Fact]
    public void A_region_that_does_not_bake_has_its_look_made_by_the_viewer()
    {
        Assert.Equal(LmvTransport.RebuildPlan.ViewerBakes, LmvTransport.PlanRebuild(AppearanceMode.Client, regionHasCapability: false));
        Assert.Equal(LmvTransport.RebuildPlan.ViewerBakes, LmvTransport.PlanRebuild(AppearanceMode.Client, regionHasCapability: true));
    }

    [Fact]
    public void A_baking_region_is_asked_to_rebuild()
    {
        Assert.Equal(LmvTransport.RebuildPlan.AskRegion, LmvTransport.PlanRebuild(AppearanceMode.Server, regionHasCapability: true));
        Assert.Equal(LmvTransport.RebuildPlan.AskRegion, LmvTransport.PlanRebuild(AppearanceMode.Unknown, regionHasCapability: true));
    }

    [Fact]
    public void A_baking_region_without_the_capability_has_no_service()
    {
        Assert.Equal(LmvTransport.RebuildPlan.NoService, LmvTransport.PlanRebuild(AppearanceMode.Server, regionHasCapability: false));
        Assert.Equal(LmvTransport.RebuildPlan.NoService, LmvTransport.PlanRebuild(AppearanceMode.Unknown, regionHasCapability: false));
    }

    [Fact]
    public async Task A_reply_that_never_comes_ends_the_wait_at_the_limit()
    {
        bool started = false;
        var limit = TimeSpan.FromMilliseconds(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool finished = await LmvTransport.WithinAsync(async ct => { started = true; await Task.Delay(Timeout.Infinite, ct); }, limit);
        Assert.True(started);
        Assert.False(finished);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Work_that_finishes_in_time_is_reported_finished()
    {
        bool ran = false;
        bool finished = await LmvTransport.WithinAsync(_ => { ran = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(30));
        Assert.True(ran);
        Assert.True(finished);
    }
}

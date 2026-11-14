// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Shell.Overrider;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the viewer gives the animation overrider: the inventory model read as folders, animation links and the assets
/// the links lead to, and the message that asks the region to start or stop an animation. Pure, runs in parallel.
/// </summary>
public class OverriderHostTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid Sets = Guid.Parse("00000000-0000-4000-8000-000000000002");
    private static readonly Guid Standing = Guid.Parse("00000000-0000-4000-8000-000000000003");
    private static readonly Guid Animations = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly Guid Library = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid LinkId = Guid.Parse("00000000-0000-4000-8000-100000000001");
    private static readonly Guid AnimationItem = Guid.Parse("00000000-0000-4000-8000-100000000002");
    private static readonly Guid Asset = Guid.Parse("00000000-0000-4000-8000-200000000001");

    private const int AnimationAssetType = 20, LinkAssetType = 24, AnimationInventoryType = 19, NotecardInventoryType = 7;

    private static (InventoryModel Model, List<(Guid Folder, Guid Owner)> Requests) New()
    {
        var requests = new List<(Guid, Guid)>();
        var model = new InventoryModel((folder, owner) => requests.Add((folder, owner)));
        model.Apply(new InventorySkeletonReceived(Owner, Root, Guid.NewGuid(), Library,
            [
                new InventoryFolderData(Root, Guid.Empty, "My Inventory", FolderKind.Root, 1),
                new InventoryFolderData(Sets, Root, "Sets", FolderKind.None, 1),
                new InventoryFolderData(Standing, Sets, "Standing:CY", FolderKind.None, 1),
                new InventoryFolderData(Animations, Root, "Animations", FolderKind.Animation, 1),
            ],
            [new InventoryFolderData(Library, Guid.Empty, "Library", FolderKind.Root, 1)]));
        return (model, requests);
    }

    private static InventoryItemData Link(int inventoryType = AnimationInventoryType) =>
        new(LinkId, Standing, "stand", ItemKind.Link, LinkAssetType, inventoryType, 0, null, AnimationItem, "3");

    private static InventoryFolderFetched Fetched(Guid folder, params InventoryItemData[] items) => new(folder, true, 1, items.Length, [], items);

    [Fact]
    public void The_overrider_sees_the_inventorys_folders_and_asks_for_contents_that_are_not_loaded()
    {
        var (model, requests) = New();
        var inventory = new OverriderInventory(model);

        Assert.Equal(Root, inventory.RootId);
        Assert.Equal(["Sets", "Animations"], inventory.Folders(Root).Select(f => f.Name));
        Assert.Equal(Standing, Assert.Single(inventory.Folders(Sets)).Id);
        Assert.False(inventory.IsComplete(Standing));
        inventory.Fetch(Standing);
        Assert.Contains((Standing, Owner), requests);

        model.Apply(Fetched(Standing, Link()));
        Assert.True(inventory.IsComplete(Standing));
    }

    [Fact]
    public void A_link_to_an_animation_carries_the_animations_asset_once_the_animation_itself_is_known()
    {
        var (model, _) = New();
        var inventory = new OverriderInventory(model);
        model.Apply(Fetched(Standing, Link()));

        var link = Assert.Single(inventory.Items(Standing));
        Assert.True(link.IsLink);
        Assert.True(link.IsAnimation);
        Assert.Equal(AnimationItem, link.LinkedId);
        Assert.Equal("3", link.Description);
        Assert.Equal(Guid.Empty, link.AssetId);
        Assert.Equal(Guid.Empty, inventory.AssetOf(LinkId));

        model.Apply(Fetched(Animations, new InventoryItemData(AnimationItem, Animations, "a stand", ItemKind.Animation, AnimationAssetType, AnimationInventoryType, 0, null, AssetId: Asset)));
        Assert.Equal(Asset, inventory.AssetOf(LinkId));
        Assert.Equal(Asset, Assert.Single(inventory.Items(Standing)).AssetId);
    }

    [Fact]
    public void A_link_to_something_else_and_an_animation_that_is_not_a_link_are_told_apart()
    {
        var (model, _) = New();
        var inventory = new OverriderInventory(model);
        var plainId = Guid.NewGuid();   // the link points at an item that is not known, so its own type decides
        model.Apply(Fetched(Standing, Link(NotecardInventoryType),
            new InventoryItemData(plainId, Standing, "a stand", ItemKind.Animation, AnimationAssetType, AnimationInventoryType, 0, null, AssetId: Asset)));

        var items = inventory.Items(Standing);
        Assert.Equal(2, items.Count);
        var link = items.Single(i => i.Id == LinkId);
        Assert.True(link.IsLink);
        Assert.False(link.IsAnimation);
        var plain = items.Single(i => i.Id == plainId);
        Assert.False(plain.IsLink);
        Assert.True(plain.IsAnimation);
        Assert.Equal(Asset, plain.AssetId);
    }

    [Fact]
    public void Requests_made_together_go_out_as_one_message_in_the_order_they_were_made()
    {
        Guid agent = Guid.NewGuid(), session = Guid.NewGuid(), next = Guid.NewGuid(), old = Guid.NewGuid();

        var packet = LmvTransport.BuildAgentAnimations(new LibreMetaverse.UUID(agent), new LibreMetaverse.UUID(session), [(next, true), (old, false), (Guid.Empty, true)]);

        // one message, so the region answers once with the list as it is after both: two messages sent in the same instant
        // can be answered in either order, and the stale answer then stands until the next change
        Assert.Equal([(next, true), (old, false)], (packet.AnimationList ?? []).Select(b => (b.AnimID.Guid, b.StartAnim)));
        Assert.Equal(agent, packet.AgentData.AgentID.Guid);
        Assert.Equal(session, packet.AgentData.SessionID.Guid);
        Assert.True(packet.Header.Reliable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_animation_request_is_a_reliable_AgentAnimation_message_with_one_entry(bool start)
    {
        Guid agent = Guid.NewGuid(), session = Guid.NewGuid(), animation = Guid.NewGuid();

        var packet = LmvTransport.BuildAgentAnimation(new LibreMetaverse.UUID(agent), new LibreMetaverse.UUID(session), new LibreMetaverse.UUID(animation), start);

        var entry = Assert.Single(packet.AnimationList ?? []);
        Assert.Equal(animation, entry.AnimID.Guid);
        Assert.Equal(start, entry.StartAnim);
        Assert.Equal(agent, packet.AgentData.AgentID.Guid);
        Assert.Equal(session, packet.AgentData.SessionID.Guid);
        Assert.True(packet.Header.Reliable);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Xunit;
using LmvStore = LibreMetaverse.Inventory;
using static Viewer.Core.Tests.InventoryModelTests;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// An attach that would put more objects on the avatar than the grid allows is refused with a reason. The limit is the login
/// reply's account benefit "attachment_limit", where the official viewer takes it (LLAgentBenefits::getAttachmentLimit, checked in
/// LLVOAvatar::canAttachMoreObjects); when the grid sends none the official viewer's default is -1, which that check reads as
/// no client-side limit. Offline; every member builds its own model, so the class runs in parallel.
/// </summary>
public class OutfitAttachmentLimitTests
{
    private const int Hat3 = 30, Hat4 = 31;

    private static Rig WithObjectsWorn(int limit)
    {
        var r = new Rig(attachmentLimit: limit);
        r.Put(ObjF, Thing(ObjO1, ObjF, "hat", ItemKind.Object), Thing(ObjO2, ObjF, "wand", ItemKind.Object), Thing(Hat3, ObjF, "cape", ItemKind.Object), Thing(Hat4, ObjF, "belt", ItemKind.Object));
        r.SetCof(LinkTo(1, ObjO1, "hat"), LinkTo(2, ObjO2, "wand"));
        return r;
    }

    [Fact]
    public void An_attach_past_the_grids_limit_is_refused_with_a_reason()
    {
        var r = WithObjectsWorn(limit: 2);
        var p = OutfitPlanner.Attach(r.Model, Id(Hat3), 0, add: true);
        Assert.False(p.Ok);
        Assert.Equal("outfit.attach_limit", p.Refusal);
    }

    [Fact]
    public void Wearing_an_object_is_refused_the_same_way()
    {
        var r = WithObjectsWorn(limit: 2);
        Assert.Equal("outfit.attach_limit", OutfitPlanner.Wear(r.Model, Id(Hat3), add: false).Refusal);
        Assert.Equal("outfit.attach_limit", OutfitPlanner.Wear(r.Model, Id(Hat3), add: true).Refusal);
    }

    [Fact]
    public void An_attach_that_fits_under_the_limit_is_planned()
    {
        var r = WithObjectsWorn(limit: 3);
        var p = OutfitPlanner.Attach(r.Model, Id(Hat3), 0, add: true);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(Id(Hat3), Assert.Single(p.Change!.Attach).ItemId);
    }

    [Fact]
    public void When_the_grid_sends_no_limit_none_is_enforced()
    {
        var r = WithObjectsWorn(limit: -1);
        Assert.True(OutfitPlanner.Attach(r.Model, Id(Hat3), 0, add: true).Ok);
    }

    [Fact]
    public void Wearing_an_outfit_with_more_objects_than_the_limit_is_refused_and_one_that_fits_is_planned()
    {
        var r = WithObjectsWorn(limit: 2);
        r.Put(OutfitF, Rig.InOutfit(OutfitF, 1, ObjO1, "hat"), Rig.InOutfit(OutfitF, 2, Hat3, "cape"), Rig.InOutfit(OutfitF, 3, Hat4, "belt"));
        var tooMany = OutfitPlanner.WearOutfit(r.Model, OutfitF);
        Assert.False(tooMany.Ok);
        Assert.Equal("outfit.attach_limit", tooMany.Refusal);

        var fits = WithObjectsWorn(limit: 3);
        fits.Put(OutfitF, Rig.InOutfit(OutfitF, 1, ObjO1, "hat"), Rig.InOutfit(OutfitF, 2, Hat3, "cape"), Rig.InOutfit(OutfitF, 3, Hat4, "belt"));
        Assert.True(OutfitPlanner.WearOutfit(fits.Model, OutfitF).Ok);
    }

    [Fact]
    public void The_limit_in_the_skeleton_is_kept_and_a_skeleton_without_one_leaves_it_unset()
    {
        var folders = new[] { new InventoryFolderData(Guid.NewGuid(), Guid.Empty, "", FolderKind.Root, 1) };
        var with = new InventoryModel();
        with.Apply(new InventorySkeletonReceived(Guid.NewGuid(), folders[0].Id, Guid.NewGuid(), Guid.NewGuid(), folders, [], 7));
        Assert.Equal(7, with.AttachmentLimit);
        var without = new InventoryModel();
        without.Apply(new InventorySkeletonReceived(Guid.NewGuid(), folders[0].Id, Guid.NewGuid(), Guid.NewGuid(), folders, []));
        Assert.Equal(-1, without.AttachmentLimit);
    }

    [Fact]
    public void The_login_reply_s_attachment_limit_reaches_the_skeleton_and_none_means_unset()
    {
        using var client = new GridClient();
        var agent = new UUID("00000000-0000-4000-8000-0000000000a1");
        var root = new UUID("00000000-0000-4000-8000-0000000000a2");
        var lib = new UUID("00000000-0000-4000-8000-0000000000a3");
        var store = new LmvStore(client, agent);
        store.RootFolder = new InventoryFolder(root) { Name = "", ParentUUID = UUID.Zero };
        store.LibraryFolder = new InventoryFolder(lib) { Name = "", ParentUUID = UUID.Zero };
        var data = new LoginResponseData { AgentID = agent, InventoryRoot = root, LibraryRoot = lib, LibraryOwner = agent };
        Assert.Equal(-1, LmvTransport.BuildSkeleton(data, store).AttachmentLimit);

        data.AccountLevelBenefits = new AccountLevelBenefits(new OSDMap { ["attachment_limit"] = OSD.FromInteger(12) });
        Assert.Equal(12, LmvTransport.BuildSkeleton(data, store).AttachmentLimit);
    }
}

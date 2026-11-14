// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An avatar's group title (the object update's Title name value) reaches the world state, for the name tag. Every member builds its own objects, so the class runs in parallel.</summary>
public class AvatarTitleTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Agent = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    [Fact]
    public void The_adapter_reads_the_title_from_the_librarys_avatar()
    {
        var a = new LibreMetaverse.Avatar { LocalID = 7, ID = new LibreMetaverse.UUID(Agent), NameValues = [new LibreMetaverse.NameValue("Title STRING RW SV Member of Things")] };
        Assert.Equal("Member of Things", LmvTransport.ToAvatarData(a).Title);
        Assert.Equal("", LmvTransport.ToAvatarData(new LibreMetaverse.Avatar { LocalID = 8 }).Title);
    }

    [Fact]
    public void The_world_keeps_the_title_through_updates_and_moves_and_follows_a_change()
    {
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        world.BeginRegion(Here, new RegionInfo("Example Region", 1000, 1000, 256, 256, true));
        world.ApplyAvatar(Here, new AvatarData(7, Agent, "Test User", new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.8f), Title: "Member"));
        Assert.Equal("Member", world.AvatarOf(Agent)!.Title);
        world.ApplyMove(Here, 7, true, new Vector3(11, 10, 30), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        Assert.Equal("Member", world.AvatarOf(Agent)!.Title);
        world.ApplyAvatar(Here, new AvatarData(7, Agent, "Test User", new Vector3(11, 10, 30), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.8f), Title: "Officer"));
        Assert.Equal("Officer", world.AvatarOf(Agent)!.Title);
    }
}

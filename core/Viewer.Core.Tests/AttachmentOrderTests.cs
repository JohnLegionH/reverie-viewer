// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle, ported from the web gateway (web-viewer bb91ba5, gateway/tests/Gateway.Tests/AttachmentOrderTests.cs,
/// all five). W-2: an attachment's ObjectUpdate can arrive before its wearer's avatar object; it is kept without an
/// attachment point and given the point when the wearer arrives. WV-2b (56dcf3c): if the simulator already holds the
/// wearer the session missed, it is adopted at once. Adapted: the gateway asserted on the wire updates it sent; here the
/// same assertions are made on the world state after each change the state raised for the prim.
/// </summary>
public class AttachmentOrderTests
{
    private const uint Avatar = 900;
    private const uint Root = 501;
    private const int Chest = 1;
    private static readonly Guid WearerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly object Sim = new SimRef(256000UL << 32 | 256000UL, "Here");

    private static (WorldState World, List<ObjectState> RootStates) World()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        var states = new List<ObjectState>();
        w.Changed += c =>
        {
            if (c.Id.LocalId == Root && c.Kind is WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged)
                states.Add(w.Object(c.Id)!);
        };
        return (w, states);
    }

    private static PrimData AttachmentRoot() =>
        new(Root, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000501"), Avatar, Chest, Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static AvatarData Wearer() =>
        new(Avatar, WearerId, "Test Wearer", new Vector3(128, 128, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f));

    [Fact]
    public void Attachment_before_its_wearer_is_sent_again_with_its_point()
    {
        var (w, sent) = World();

        w.ApplyObject(Sim, AttachmentRoot(), eventIsAttachment: true, simParentAvatar: null);
        w.ApplyAvatar(Sim, Wearer());

        Assert.Equal(2, sent.Count);
        Assert.Null(sent[0].AttachmentPoint);
        Assert.Equal(Chest, sent[1].AttachmentPoint);
        Assert.Equal(WearerId, sent[1].WearerAgentId);
        Assert.Equal(Avatar, sent[1].ParentLocalId);
    }

    [Fact]
    public void Attachment_after_its_wearer_is_sent_once_with_its_point()
    {
        var (w, sent) = World();

        w.ApplyAvatar(Sim, Wearer());
        w.ApplyObject(Sim, AttachmentRoot(), eventIsAttachment: true, simParentAvatar: Wearer());
        w.ApplyAvatar(Sim, Wearer());   // a later avatar update re-sends nothing

        var only = Assert.Single(sent);
        Assert.Equal(Chest, only.AttachmentPoint);
    }

    [Fact]
    public void A_wearer_the_session_missed_is_adopted_from_the_simulator()
    {
        var (w, sent) = World();
        var avatarChanges = new List<WorldChange>();
        w.Changed += c => { if (c.Kind == WorldChangeKind.AvatarAdded) avatarChanges.Add(c); };

        w.ApplyObject(Sim, AttachmentRoot(), eventIsAttachment: true, simParentAvatar: Wearer());

        var adopted = Assert.Single(avatarChanges);
        Assert.Equal(Avatar, adopted.Id.LocalId);
        Assert.Equal(WearerId, w.Avatar(adopted.Id)!.AgentId);

        var only = Assert.Single(sent);
        Assert.Equal(Chest, only.AttachmentPoint);
        Assert.Equal(WearerId, only.WearerAgentId);
    }

    [Fact]
    public void A_killed_attachment_is_not_resent_when_its_wearer_arrives()
    {
        var (w, sent) = World();

        w.ApplyObject(Sim, AttachmentRoot(), eventIsAttachment: true, simParentAvatar: null);
        w.Kill(Sim, [Root]);
        w.ApplyAvatar(Sim, Wearer());

        Assert.Single(sent);   // only the first, point-less one (before the kill)
    }

    [Fact]
    public void A_region_change_drops_pending_roots()
    {
        var (w, sent) = World();
        var region = new RegionInfo("Here", 1000, 1000, 256, 256, false);
        w.BeginRegion(Sim, region);

        w.ApplyObject(Sim, AttachmentRoot(), eventIsAttachment: true, simParentAvatar: null);
        var next = new SimRef(257000UL << 32 | 256000UL, "There");
        w.BeginRegion(next, region with { Name = "There" });
        w.ApplyAvatar(next, Wearer());

        Assert.Single(sent);
    }
}

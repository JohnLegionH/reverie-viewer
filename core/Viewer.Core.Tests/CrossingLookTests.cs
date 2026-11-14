// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The agent's own look across a region crossing. The agent is one avatar that moves into the next region, so its appearance
/// and what it wears are kept; a new look is taken only from an appearance message that carries data.
/// </summary>
public class CrossingLookTests
{
    private static SimRef Cell(int x, int y, string name) => new((ulong)(x * 256L) << 32 | (ulong)(y * 256L), name);

    private static readonly SimRef Home = Cell(1000, 1000, "Home");
    private static readonly SimRef East = Cell(1001, 1000, "East");

    private static readonly Guid Agent = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid HeadBake = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BoxItem = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private const int RightHand = 6;

    private static PrimData Worn(uint local, uint parent, Guid full) =>
        new(local, full, parent, RightHand, new Vector3(0.1f, 0, 0), Quaternion.Identity, Vector3.One);

    private static AvatarData Me(uint local) => new(local, Agent, "Test User", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One);

    /// <summary>A session standing in Home with its look and one worn box, and East connected as a server-baked neighbour that already
    /// holds a child presence of the agent.</summary>
    private static async Task<(ViewerSession S, FakeTransport T)> HomeWithLookAndEastNearby()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Home);
        t.Raise(new AvatarUpdated(Home, Me(5)));
        t.Raise(new AvatarAppearanceReceived(Home, ServerAppearanceTests.Look(Agent, 1, (8, HeadBake))));
        t.Raise(new ObjectUpdated(Home, Worn(9, 5, BoxItem), true, null));
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(East, 256, 256, true));
        t.Raise(new AvatarUpdated(East, Me(50)));
        return (s, t);
    }

    private static void Cross(FakeTransport t)
    {
        t.CurrentSim = East;
        t.Raise(new SimConnected(East, 256, 256, true));
    }

    private static IEnumerable<ObjectState> WornBy(ViewerSession s, Guid wearer) =>
        s.World.Snapshot().Objects.Values.Where(o => o.WearerAgentId == wearer);

    [Fact]
    public async Task The_destinations_first_self_appearance_with_no_data_leaves_the_look_the_agent_had()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;

        t.Raise(new AvatarAppearanceReceived(East, ServerAppearanceTests.Look(Agent, 0)));   // version 0, no AppearanceData block
        Cross(t);

        var look = s.World.Appearance(Agent);
        Assert.NotNull(look);
        Assert.True(look!.IsSelf);
        Assert.Contains(HeadBake, look.Bakes.Values);
        Assert.Equal(1, look.AppearanceVersion);
    }

    [Fact]
    public async Task The_look_is_kept_when_the_refused_message_comes_after_the_crossing()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;

        Cross(t);
        t.Raise(new AvatarAppearanceReceived(East, ServerAppearanceTests.Look(Agent, 0)));

        var look = s.World.Appearance(Agent);
        Assert.NotNull(look);
        Assert.Contains(HeadBake, look!.Bakes.Values);
    }

    [Fact]
    public async Task What_the_agent_wears_is_kept_across_the_crossing()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;

        t.Raise(new AvatarAppearanceReceived(East, ServerAppearanceTests.Look(Agent, 0)));
        Cross(t);

        var worn = Assert.Single(WornBy(s, Agent));
        Assert.Equal(BoxItem, worn.FullId);
        Assert.Equal(RightHand, worn.AttachmentPoint);
        Assert.Equal(s.World.AvatarOf(Agent)!.Id.LocalId, worn.ParentLocalId);   // it hangs on the avatar the destination holds
    }

    [Fact]
    public async Task A_new_look_with_data_replaces_the_kept_one()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;
        var newHead = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        Cross(t);
        t.Raise(new AvatarAppearanceReceived(East, ServerAppearanceTests.Look(Agent, 2, (8, newHead))));

        var look = s.World.Appearance(Agent)!;
        Assert.Contains(newHead, look.Bakes.Values);
        Assert.DoesNotContain(HeadBake, look.Bakes.Values);
    }

    [Fact]
    public async Task Attachments_the_destination_sends_replace_the_kept_ones_and_are_not_drawn_twice()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;

        Cross(t);
        Assert.Single(WornBy(s, Agent));   // the kept one
        t.Raise(new ObjectUpdated(East, Worn(700, s.World.AvatarOf(Agent)!.Id.LocalId, BoxItem), true, null));

        var worn = Assert.Single(WornBy(s, Agent));
        Assert.Equal(700u, worn.Id.LocalId);
    }

    [Fact]
    public async Task Another_avatars_look_is_not_carried()
    {
        var (s, t) = await HomeWithLookAndEastNearby();
        using var _s = s;
        var other = Guid.NewGuid();
        t.Raise(new AvatarUpdated(Home, new AvatarData(6, other, "Someone", new Vector3(8, 8, 8), Quaternion.Identity, Vector3.One)));
        t.Raise(new AvatarAppearanceReceived(Home, ServerAppearanceTests.Look(other, 1, (8, Guid.NewGuid()))));

        Cross(t);

        Assert.Null(s.World.Appearance(other));
    }
}

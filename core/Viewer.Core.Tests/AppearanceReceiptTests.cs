// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Appearance receipt is passive and, like self motion, scoped to the region epoch.
/// </summary>
public class AppearanceReceiptTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef There = new(256256UL << 32 | 256000UL, "There");

    [Fact]
    public void Appearance_receipt_never_sends_anything_in_either_mode()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        // Client-bake region: nobody textures us, and the core still does nothing about it.
        ServerAppearanceTests.Arrive(t, Here, serverBaked: false);
        t.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(ServerAppearanceTests.Me, 0)));
        t.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(Guid.NewGuid(), 0, (8, Guid.NewGuid()))));
        // Server-bake region, self appearance refused and accepted.
        t.CurrentSim = There;
        t.Raise(new SimConnected(There, 256, 256, true));
        t.Raise(new AvatarAppearanceReceived(There, ServerAppearanceTests.Look(ServerAppearanceTests.Me, 0)));
        t.Raise(new AvatarAppearanceReceived(There, ServerAppearanceTests.Look(ServerAppearanceTests.Me, 1, (8, Guid.NewGuid()))));

        Assert.Single(t.Logins);          // the login is the only call the session made on its transport
        Assert.Empty(t.DrawDistances);
        Assert.Equal(0, t.Logouts);
        // ISessionTransport has no member that could carry an appearance out.
        Assert.DoesNotContain(typeof(ISessionTransport).GetMethods(), m => m.Name.Contains("Appearance", StringComparison.OrdinalIgnoreCase));
        // And no method body in the core calls a LibreMetaverse appearance send or bake (the new receipt code included).
        Assert.Empty(SessionInvariantTests.AppearanceCalls(TestPaths.CoreDll, includeSettingFalse: false).Where(c => !SessionInvariantTests.ClientBakePath.Any(c.Contains)));   // the client-bake path is the one reviewed place
        Assert.NotNull(s.World.Appearance(ServerAppearanceTests.Me));
    }

    [Fact]
    public void Appearances_and_self_motion_are_scoped_to_the_region_epoch()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var other = Guid.NewGuid();
        t.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(other, 1, (8, Guid.NewGuid()))));
        t.Raise(new SelfMoved(Here, new SelfMotion(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, 10, default)));
        Assert.NotNull(s.World.Appearance(other));
        Assert.NotNull(s.World.LastSelfMotion);
        int firstEpoch = s.World.Epoch;

        // Region change: the old region's appearances and self sample go with its epoch.
        t.CurrentSim = There;
        t.Raise(new SimConnected(There, 256, 256, true));
        Assert.NotEqual(firstEpoch, s.World.Epoch);
        Assert.Null(s.World.Appearance(other));
        Assert.Null(s.World.LastSelfMotion);

        // Late inputs from the old simulator are dropped, not applied to the new epoch.
        s.World.ApplyAppearance(Here, ServerAppearanceTests.Look(other, 1, (8, Guid.NewGuid())), ServerAppearanceTests.Me);
        s.World.ApplySelfMotion(Here, new SelfMotion(new Vector3(9, 9, 9), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, 20, default));
        Assert.Null(s.World.Appearance(other));
        Assert.Null(s.World.LastSelfMotion);
        Assert.Equal(2, s.World.Dropped(WorldDrop.StaleEpoch));

        // The session's own stored state is per session, not per region: the sim's bakes for us survive.
        t.Raise(new AvatarAppearanceReceived(There, ServerAppearanceTests.Look(ServerAppearanceTests.Me, 1, (8, Guid.NewGuid()))));
        Assert.Single(s.World.Self.ServerBakes);
    }

    [Fact]
    public void A_duplicated_appearance_changes_nothing_and_raises_nothing()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var look = ServerAppearanceTests.Look(Guid.NewGuid(), 1, (8, Guid.NewGuid()));
        t.Raise(new AvatarAppearanceReceived(Here, look));
        t.Raise(new AvatarAppearanceReceived(Here, look));
        var c = Assert.Single(changes);
        Assert.Equal(WorldChangeKind.AppearanceChanged, c.Kind);
        Assert.Equal(look.AgentId, c.AgentId);
    }

    [Fact]
    public void Two_sessions_keep_their_own_appearance_and_self_motion()
    {
        var (a, ta, _) = ServerAppearanceTests.LoggedIn();
        var (b, tb, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(ta, Here, serverBaked: true);
        ServerAppearanceTests.Arrive(tb, Here, serverBaked: false);
        ta.Raise(new AvatarAppearanceReceived(Here, ServerAppearanceTests.Look(ServerAppearanceTests.Me, 1, (8, Guid.NewGuid()))));
        ta.Raise(new SelfMoved(Here, new SelfMotion(Vector3.One, Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, 1, default)));

        Assert.Equal(AppearanceMode.Server, a.World.AppearanceMode);
        Assert.Equal(AppearanceMode.Client, b.World.AppearanceMode);
        Assert.Single(a.World.Self.ServerBakes);
        Assert.Empty(b.World.Self.ServerBakes);
        Assert.Null(b.World.Appearance(ServerAppearanceTests.Me));
        Assert.Null(b.World.LastSelfMotion);
        a.Dispose();
        Assert.NotEqual(AppearanceMode.Unknown, b.World.AppearanceMode);
    }
}

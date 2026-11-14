// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A session the grid ended unasked, below the interface: how the library's disconnect kinds are told apart, what the session records
/// about the drop (why, the grid's words, where the agent was), and the reconnect plan (the start locations, the attempt limit, the
/// refusals that end it). No grid: a fake transport. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class ReconnectCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static (ViewerSession Session, FakeTransport Fake) InWorld(bool withSelfAvatar)
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        if (withSelfAvatar)
            session.World.ApplyAvatar(Here, new Viewer.Core.World.AvatarData(5, Self, "Test User", new Vector3(100.4f, 50.6f, 31f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 2f)));
        return (session, fake);
    }

    // ---------------- the adapter tells the library's disconnect kinds apart ----------------

    [Theory]
    [InlineData(LibreMetaverse.NetworkManager.DisconnectType.ClientInitiated, DisconnectKind.ClientInitiated)]
    [InlineData(LibreMetaverse.NetworkManager.DisconnectType.NetworkTimeout, DisconnectKind.NetworkTimeout)]
    [InlineData(LibreMetaverse.NetworkManager.DisconnectType.ServerInitiated, DisconnectKind.ServerInitiated)]
    [InlineData(LibreMetaverse.NetworkManager.DisconnectType.SimShutdown, DisconnectKind.SimShutdown)]
    public void The_librarys_disconnect_kinds_become_the_cores(LibreMetaverse.NetworkManager.DisconnectType type, DisconnectKind kind) =>
        Assert.Equal(kind, LmvTransport.ToDisconnected(type, "text").Kind);

    [Fact]
    public void Only_a_server_initiated_end_carries_the_grids_words()
    {
        Assert.Equal("You were removed by an administrator.", LmvTransport.ToDisconnected(LibreMetaverse.NetworkManager.DisconnectType.ServerInitiated, "You were removed by an administrator.").Message);
        Assert.Equal("", LmvTransport.ToDisconnected(LibreMetaverse.NetworkManager.DisconnectType.NetworkTimeout, "Timed out").Message);
        Assert.Equal("", LmvTransport.ToDisconnected(LibreMetaverse.NetworkManager.DisconnectType.ServerInitiated, null).Message);
    }

    // ---------------- what the session records ----------------

    [Fact]
    public void A_circuit_that_goes_quiet_ends_the_session_as_a_drop_with_the_place_the_agent_was_at()
    {
        var (session, fake) = InWorld(withSelfAvatar: true);
        SessionEndReason? ended = null;
        session.Ended += r => ended = r;
        fake.Raise(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout));
        Assert.Equal(SessionEndReason.Disconnected, ended);
        var drop = Assert.IsType<DropInfo>(session.Drop);
        Assert.Equal(DisconnectKind.NetworkTimeout, drop.Kind);
        Assert.Equal("", drop.Message);
        Assert.True(drop.HasPlace);
        Assert.Equal("Example Region", drop.Region);
        Assert.Equal(256, drop.RegionSizeX);
        Assert.Equal(new Vector3(100.4f, 50.6f, 30f), drop.Position);   // the soles: the box's middle less half its height
    }

    [Fact]
    public void A_kick_carries_the_grids_reason_and_a_drop_before_the_avatar_is_known_has_no_place()
    {
        var (session, fake) = InWorld(withSelfAvatar: false);
        fake.Raise(new Disconnected("ServerInitiated", DisconnectKind.ServerInitiated, "You have been logged out by the grid."));
        var drop = Assert.IsType<DropInfo>(session.Drop);
        Assert.Equal(DisconnectKind.ServerInitiated, drop.Kind);
        Assert.Equal("You have been logged out by the grid.", drop.Message);
        Assert.False(drop.HasPlace);
    }

    [Fact]
    public void The_users_own_logout_is_not_a_drop()
    {
        var (session, fake) = InWorld(withSelfAvatar: true);
        session.LogoutAsync(CancellationToken.None).Wait();
        fake.Raise(new Disconnected("ClientInitiated", DisconnectKind.ClientInitiated));
        Assert.Null(session.Drop);
        Assert.Equal(SessionEndReason.LoggedOut, session.EndReason);
    }

    [Fact]
    public void A_second_disconnect_report_does_not_change_what_was_recorded()
    {
        var (session, fake) = InWorld(withSelfAvatar: true);
        fake.Raise(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout));
        var first = session.Drop;
        fake.Raise(new Disconnected("ServerInitiated", DisconnectKind.ServerInitiated, "late"));
        Assert.Same(first, session.Drop);
    }

    // ---------------- the plan ----------------

    [Fact]
    public void The_same_spot_is_the_login_location_form_with_the_spot_kept_inside_the_region()
    {
        Assert.Equal("uri:Example Region&100&50&30", ReconnectPlan.SameSpot("Example Region", new Vector3(100.4f, 50.2f, 30.4f), 256, 256));
        Assert.Equal("uri:Example Region&255&1&0", ReconnectPlan.SameSpot("Example Region", new Vector3(300f, -5f, -9f), 256, 256));
        Assert.Equal("uri:Big Region&1000&2000&4096", ReconnectPlan.SameSpot("Big Region", new Vector3(1000f, 2000f, 9000f), 4096, 4096));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Bad&Name")]
    [InlineData("Bad:Name")]
    [InlineData("Bad\nName")]
    public void A_region_name_the_location_form_cannot_carry_gives_no_same_spot(string region) =>
        Assert.Equal("", ReconnectPlan.SameSpot(region, new Vector3(10, 10, 10), 256, 256));

    [Fact]
    public void The_start_locations_are_the_same_spot_then_the_last_location_and_never_more_than_the_limit()
    {
        var drop = new DropInfo(DisconnectKind.NetworkTimeout, "", "Example Region", 256, 256, new Vector3(100, 50, 30), true);
        var starts = ReconnectPlan.StartLocations(drop);
        Assert.Equal(["uri:Example Region&100&50&30", "last"], starts);
        Assert.Equal(2, ReconnectPlan.MaxAttempts);
        Assert.True(starts.Count <= ReconnectPlan.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), ReconnectPlan.Pause);
    }

    [Fact]
    public void With_no_known_place_or_an_unusable_name_the_only_start_is_the_last_location()
    {
        Assert.Equal(["last"], ReconnectPlan.StartLocations(null));
        Assert.Equal(["last"], ReconnectPlan.StartLocations(new DropInfo(DisconnectKind.ServerInitiated, "x", "", 256, 256, default, false)));
        Assert.Equal(["last"], ReconnectPlan.StartLocations(new DropInfo(DisconnectKind.NetworkTimeout, "", "A&B", 256, 256, new Vector3(1, 1, 1), true)));
    }

    [Theory]
    [InlineData("key", true)]
    [InlineData("mfa_challenge", true)]
    [InlineData("tos", true)]
    [InlineData("update", true)]
    [InlineData("presence", false)]
    [InlineData("failed", false)]
    [InlineData("exception", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_refusal_of_the_credentials_or_the_terms_ends_the_reconnect_but_an_ordinary_failure_does_not(string? code, bool stops) =>
        Assert.Equal(stops, ReconnectPlan.StopsReconnect(code));
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Who is online when the agent logs in. The grid sends the friend list in the login reply with nobody online, and tells the client who
/// already is a moment later, as online notices (a live capture: the notice came about one second after the circuit opened). The
/// interface attaches to the session on its own schedule, so a notice that arrives before anyone listens must still be known to the one
/// that attaches: it is handed the list as it stands now, not as it was in the login reply. A fake transport; no grid. Every member builds
/// its own objects, so the class runs in parallel.
/// </summary>
public class FriendsPresenceAtLoginTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid One = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Two = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");

    private static (ViewerSession Session, FakeTransport Fake) InWorld()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake);
    }

    private static FriendsListed List(params (Guid Id, bool Online)[] friends) =>
        new(friends.Select(f => new FriendEntry(f.Id, "A Friend", f.Online, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)).ToArray());

    private static List<TransportEvent> Attach(ViewerSession session)
    {
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        return seen;
    }

    [Fact]
    public void A_friend_the_grid_said_was_online_before_anyone_listened_is_online_in_the_list_the_first_subscriber_gets()
    {
        var (session, fake) = InWorld();
        fake.Raise(List((One, false), (Two, false)));          // the login reply
        fake.Raise(new FriendPresenceChanged(One, true));      // the online notice, before the interface attached
        var listed = Assert.Single(Attach(session).OfType<FriendsListed>());
        Assert.True(listed.Friends.Single(f => f.Id == One).Online);
        Assert.False(listed.Friends.Single(f => f.Id == Two).Online);
    }

    [Fact]
    public void An_online_notice_followed_by_an_offline_one_leaves_the_friend_offline()
    {
        var (session, fake) = InWorld();
        fake.Raise(List((One, false)));
        fake.Raise(new FriendPresenceChanged(One, true));
        fake.Raise(new FriendPresenceChanged(One, false));
        Assert.False(Assert.Single(Attach(session).OfType<FriendsListed>()).Friends.Single().Online);
    }

    [Fact]
    public void The_list_in_the_login_reply_decides_for_the_friends_it_names_over_notices_that_came_before_it()
    {
        // the protocol library merges an early notice into the list it reports, so the list is at least as new as any notice before it
        var (session, fake) = InWorld();
        fake.Raise(new FriendPresenceChanged(One, true));
        fake.Raise(List((One, false)));
        Assert.False(Assert.Single(Attach(session).OfType<FriendsListed>()).Friends.Single().Online);
    }

    [Fact]
    public void A_friend_who_ended_the_friendship_before_anyone_listened_is_not_in_the_list()
    {
        var (session, fake) = InWorld();
        fake.Raise(List((One, true), (Two, false)));
        fake.Raise(new FriendshipEnded(One));
        Assert.Equal(Two, Assert.Single(Attach(session).OfType<FriendsListed>().Single().Friends).Id);
    }

    [Fact]
    public void A_notice_after_the_interface_attached_is_delivered_as_an_event_and_not_as_a_second_list()
    {
        var (session, fake) = InWorld();
        fake.Raise(List((One, false)));
        var seen = Attach(session);
        fake.Raise(new FriendPresenceChanged(One, true));
        Assert.Single(seen.OfType<FriendsListed>());
        Assert.Equal(new FriendPresenceChanged(One, true), Assert.Single(seen.OfType<FriendPresenceChanged>()));
    }

    [Fact]
    public void With_no_list_yet_a_subscriber_is_told_nothing_and_a_disposed_session_forgets_the_list()
    {
        var (session, fake) = InWorld();
        fake.Raise(new FriendPresenceChanged(One, true));
        Assert.Empty(Attach(session));
        fake.Raise(List((One, false)));
        session.Dispose();
        var after = new List<TransportEvent>();
        session.Communication += after.Add;
        Assert.Empty(after);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The friend list comes with the login reply, before the interface has had a frame to attach to the session. The session keeps the list
/// and hands it to whoever subscribes later, so the list is not empty at start-up when the grid has friends. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class FriendsListLateSubscriberTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid FriendId = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid OtherId = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");

    private static (ViewerSession Session, FakeTransport Fake) InWorld()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake);
    }

    private static FriendsListed List(params Guid[] ids) =>
        new(ids.Select(i => new FriendEntry(i, "A Friend", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)).ToArray());

    [Fact]
    public void A_list_that_came_before_anyone_listened_is_handed_to_the_first_subscriber()
    {
        var (session, fake) = InWorld();
        fake.Raise(List(FriendId));
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        var listed = Assert.Single(seen.OfType<FriendsListed>());
        Assert.Equal(FriendId, Assert.Single(listed.Friends).Id);
    }

    [Fact]
    public void A_later_list_replaces_the_kept_one_and_is_not_handed_twice_to_a_subscriber_that_was_there()
    {
        var (session, fake) = InWorld();
        var early = new List<TransportEvent>();
        session.Communication += early.Add;
        fake.Raise(List(FriendId));
        fake.Raise(List(FriendId, OtherId));
        Assert.Equal(2, early.OfType<FriendsListed>().Count());          // live, once each
        var late = new List<TransportEvent>();
        session.Communication += late.Add;
        var listed = Assert.Single(late.OfType<FriendsListed>());        // the newest one only
        Assert.Equal(2, listed.Friends.Count);
    }

    [Fact]
    public void Nothing_is_handed_over_when_no_list_came_and_an_unsubscribed_handler_hears_nothing_more()
    {
        var (session, fake) = InWorld();
        var seen = new List<TransportEvent>();
        void Handler(TransportEvent e) => seen.Add(e);
        session.Communication += Handler;
        Assert.Empty(seen.OfType<FriendsListed>());
        session.Communication -= Handler;
        fake.Raise(List(FriendId));
        Assert.Empty(seen.OfType<FriendsListed>());
    }
}

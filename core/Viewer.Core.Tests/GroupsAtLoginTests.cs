// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The group list is asked for as the session reaches the world, and the answer can come before the interface has attached. Like the
/// friend list, what the grid said about the agent's groups (the list, and which group is active) must reach an interface that attaches
/// after it, or the list stays empty for the whole session although the agent is in groups. A fake transport; no grid. Every member builds
/// its own objects, so the class runs in parallel.
/// </summary>
public class GroupsAtLoginTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid One = Guid.Parse("6a6a6a6a-0000-4000-8000-0000000000a1");
    private static readonly Guid Two = Guid.Parse("6a6a6a6a-0000-4000-8000-0000000000a2");

    private static (ViewerSession Session, FakeTransport Fake) InWorld()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake);
    }

    private static GroupsListed List(params Guid[] ids) => new(ids.Select(i => new GroupEntry(i, "A Group", true, true)).ToArray());

    private static List<TransportEvent> Attach(ViewerSession session)
    {
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        return seen;
    }

    [Fact]
    public void A_group_list_that_came_before_anyone_listened_is_handed_to_the_first_subscriber()
    {
        var (session, fake) = InWorld();
        fake.Raise(List(One, Two));
        var listed = Assert.Single(Attach(session).OfType<GroupsListed>());
        Assert.Equal([One, Two], listed.Groups.Select(g => g.Id).ToArray());
    }

    [Fact]
    public void The_active_group_that_was_stated_before_anyone_listened_is_handed_over_too()
    {
        var (session, fake) = InWorld();
        fake.Raise(List(One));
        fake.Raise(new ActiveGroupChanged(One, "A Group", "Member"));
        var seen = Attach(session);
        Assert.Equal(One, Assert.Single(seen.OfType<ActiveGroupChanged>()).GroupId);
        Assert.Single(seen.OfType<GroupsListed>());
    }

    [Fact]
    public void A_later_list_replaces_the_kept_one_and_the_newest_active_group_wins()
    {
        var (session, fake) = InWorld();
        fake.Raise(List(One));
        fake.Raise(List(Two));
        fake.Raise(new ActiveGroupChanged(One, "A Group", "Member"));
        fake.Raise(new ActiveGroupChanged(Two, "A Group", "Member"));
        var seen = Attach(session);
        Assert.Equal(Two, Assert.Single(Assert.Single(seen.OfType<GroupsListed>()).Groups).Id);
        Assert.Equal(Two, Assert.Single(seen.OfType<ActiveGroupChanged>()).GroupId);
    }

    [Fact]
    public void A_subscriber_already_attached_hears_each_change_once_and_a_second_subscriber_is_handed_the_state()
    {
        var (session, fake) = InWorld();
        var first = Attach(session);
        fake.Raise(List(One));
        Assert.Single(first.OfType<GroupsListed>());
        var second = Attach(session);
        Assert.Single(second.OfType<GroupsListed>());
        Assert.Single(first.OfType<GroupsListed>());   // the first was not told again
    }

    [Fact]
    public void With_no_list_a_subscriber_is_told_nothing_about_groups()
    {
        var (session, _) = InWorld();
        Assert.Empty(Attach(session).OfType<GroupsListed>());
    }

    [Fact]
    public void A_disposed_session_forgets_the_groups()
    {
        var (session, fake) = InWorld();
        fake.Raise(List(One));
        session.Dispose();
        Assert.Empty(Attach(session));
    }
}

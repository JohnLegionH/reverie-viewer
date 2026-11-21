// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.NameTags;
using Viewer.Shell.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Hypergrid friends and visitors as the home grid sends them. A person from another grid has a first name of "First.Last" (the
/// first and last names joined with a dot) and a last name of "@host:port" (the home grid's address); the grid sends that pair in
/// its name replies, in a visitor's object update and in chat. The login reply's buddy list names a Hypergrid friend by plain id,
/// and online and offline notices carry the id alone. A fake transport; no grid. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class HypergridPeopleTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Visitor = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000e1");
    private static readonly Guid Local = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000e2");
    private const string VisitorName = "Test.Visitor @grid.example.org:8002";

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly NameCache Names = new();
        public readonly FriendsModel Friends;
        public readonly List<FriendNotice> Notices = new();

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Friends = new FriendsModel(Self, Names, Pipeline.Submit, () => true);
            Friends.Notice += Notices.Add;
        }
    }

    [Fact]
    public void A_friend_listed_by_id_alone_shows_the_home_grid_name_once_the_grid_names_it()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendsListed([new FriendEntry(Visitor, "", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
        r.Names.Learn(new AgentNamesReceived(new Dictionary<Guid, string> { [Visitor] = VisitorName }));
        Assert.Equal(VisitorName, Assert.Single(r.Friends.Friends).Name);
    }

    [Fact]
    public void An_online_notice_for_a_friend_the_list_names_raises_a_notice_with_the_friend_id()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendsListed([new FriendEntry(Visitor, VisitorName, false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
        r.Friends.Receive(new FriendPresenceChanged(Visitor, true));
        Assert.Equal(new FriendNotice(FriendNoticeKind.Online, Visitor), Assert.Single(r.Notices));
        Assert.True(Assert.Single(r.Friends.Friends).Online);
    }

    [Fact]
    public void A_friend_already_online_in_the_first_list_raises_an_online_notice_as_if_it_had_just_arrived()
    {
        // the interface attaches after the grid's online notices: the list it is handed already says who is online
        var r = new Rig();
        r.Friends.Receive(new FriendsListed([new FriendEntry(Visitor, VisitorName, true, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline),
            new FriendEntry(Local, "Test Local", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
        Assert.Equal(new FriendNotice(FriendNoticeKind.Online, Visitor), Assert.Single(r.Notices));
    }

    [Fact]
    public void A_second_list_with_the_same_friend_online_raises_no_second_notice_and_notices_off_raise_none()
    {
        var r = new Rig();
        var online = new FriendsListed([new FriendEntry(Visitor, VisitorName, true, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]);
        r.Friends.Receive(online);
        r.Friends.Receive(online);
        Assert.Single(r.Notices);
        var quiet = new FriendsModel(Self, new NameCache(), r.Pipeline.Submit, () => false);
        var seen = new List<FriendNotice>();
        quiet.Notice += seen.Add;
        quiet.Receive(online);
        Assert.Empty(seen);
    }

    [Fact]
    public void A_name_pair_from_the_home_grid_is_one_name_with_the_account_name_dotted()
    {
        // "First.Last" + "@host:port" is what the grid sends; as a full name it must keep both parts
        Assert.Equal(VisitorName, NameTagModel.FullName("Test.Visitor", "@grid.example.org:8002"));
        Assert.Equal("test.visitor @grid.example.org:8002", NameTagModel.UserName(VisitorName));
    }

    [Fact]
    public void A_visitor_tag_shows_the_name_with_its_home_grid()
    {
        var lines = NameTagModel.Lines(new NameTagInput(Visitor, new AvatarNames(VisitorName), "", default, System.Numerics.Quaternion.Identity,
            new System.Numerics.Vector3(0.45f, 0.6f, 1.9f), false, false, false, []), new NameTagSettings(NameTagMode.Always, true, true, true, true, true), k => k);
        Assert.Equal(VisitorName, Assert.Single(lines).Text);
    }

    [Fact]
    public void A_chat_line_from_a_visitor_teaches_the_name_cache_the_full_name()
    {
        var cache = new NameCache();
        cache.Learn(new ChatReceived(null, Visitor, Visitor, VisitorName, ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, default, new MessageBody("hello", MessageChannel.LocalChat)));
        Assert.Equal(VisitorName, cache.Get(Visitor));
    }
}

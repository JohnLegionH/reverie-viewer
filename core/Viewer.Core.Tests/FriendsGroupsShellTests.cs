// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The friend list, presence notices, offers, rights, group chat, group notices and the active group and title as data,
/// without the engine or a grid. Sends go through the real action pipeline over a fake transport, so a refusal is the
/// pipeline's own. Times come from a test clock. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class FriendsGroupsShellTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Bob = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000002");
    private static readonly Guid Cy = Guid.Parse("c1c1c1c1-0000-4000-8000-000000000003");
    private static readonly Guid Offer = Guid.Parse("0ffe0ffe-0000-4000-8000-000000000001");
    private static readonly Guid GroupA = Guid.Parse("6a6a6a6a-0000-4000-8000-0000000000a1");
    private static readonly Guid GroupB = Guid.Parse("6b6b6b6b-0000-4000-8000-0000000000b2");
    private static readonly Guid Role1 = Guid.Parse("7e7e7e7e-0000-4000-8000-0000000000a2");
    private static readonly Guid Role2 = Guid.Parse("7e7e7e7e-0000-4000-8000-0000000000a3");
    private static readonly DateTimeOffset T0 = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);
    private const FriendRightsFlags Online = FriendRightsFlags.SeeOnline;

    private sealed class Rig
    {
        public DateTimeOffset Now = T0;
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly NameCache Names = new();
        public readonly FriendsModel Friends;
        public readonly GroupsModel Groups;
        public readonly Conversations Ims;
        public readonly List<FriendNotice> Notices = new();
        public readonly List<string> SystemLines = new();
        public readonly List<Guid> TitleRequests = new();
        public bool NoticesOn = true;
        public bool InWorld = true;
        public int Changes, GroupChanges;

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => InWorld, new CoreLog(new ListSink(), "test"));
            Friends = new FriendsModel(Self, Names, Pipeline.Submit, () => NoticesOn);
            Friends.Notice += Notices.Add;
            Friends.Changed += () => Changes++;
            Groups = new GroupsModel(Pipeline.Submit, () => Now, TitleRequests.Add);
            Groups.Changed += () => GroupChanges++;
            Ims = new Conversations(Self, Names, Pipeline.Submit, () => 500, () => true, () => Now, SystemLines.Add);
        }

        public IEnumerable<T> Sent<T>() where T : TransportCommand => Transport.Commands.OfType<T>();
    }


    private static T At<T>(IReadOnlyList<T> list, int i)
    {
        Assert.True(i >= 0 && list.Count > i, $"expected at least {i + 1} items, found {list.Count}");
        return list[i];
    }

    private static T Last<T>(IReadOnlyList<T> list)
    {
        Assert.NotEmpty(list);
        return list[^1];
    }

    private static Conversation G(Conversation? c)
    {
        Assert.NotNull(c);
        return c!;
    }

    private static FriendEntry Entry(Guid id, string name, bool online = false, FriendRightsFlags granted = Online, FriendRightsFlags received = Online) =>
        new(id, name, online, granted, received);

    private static Rig WithFriends(params FriendEntry[] friends)
    {
        var r = new Rig();
        r.Friends.Receive(new FriendsListed(friends));
        return r;
    }

    private static MessageBody Body(string t) => new(t, MessageChannel.InstantMessage);

    // ================= negative channels =================

    [Fact]
    public void The_chat_bar_sends_a_negative_channel_line_through_the_pipeline_and_clears_the_input()
    {
        var r = new Rig();
        var chat = new NearbyChat(Self, r.Pipeline.Submit, () => 500, () => true, () => T0);
        Assert.True(chat.Send("/-5 hello"));
        Assert.Equal(new SayCommand("hello", -5, ChatKind.Say), Assert.Single(r.Sent<SayCommand>()));
        Assert.Null(chat.Status);
        Assert.True(chat.Send("//again"));                                   // the repeated channel is the negative one
        Assert.Equal(new SayCommand("again", -5, ChatKind.Say), r.Sent<SayCommand>().Last());
    }

    // ================= friends =================

    [Fact]
    public void The_list_is_the_logged_in_friends_sorted_by_name_ignoring_case_and_starts_offline_with_their_rights()
    {
        var r = WithFriends(Entry(Bob, "bob Example"), Entry(Ann, "Ann Example", false, Online | FriendRightsFlags.SeeOnMap, Online | FriendRightsFlags.ModifyObjects));
        Assert.Equal([Ann, Bob], r.Friends.Friends.Select(f => f.Id));
        var ann = At(r.Friends.Friends, 0);
        Assert.Equal("Ann Example", ann.Name);
        Assert.False(ann.Online);
        Assert.Equal(Online | FriendRightsFlags.SeeOnMap, ann.Granted);
        Assert.Equal(Online | FriendRightsFlags.ModifyObjects, ann.Received);
    }

    [Fact]
    public void A_friend_without_a_name_shows_the_cached_name_then_the_short_id_and_follows_the_cache()
    {
        var r = WithFriends(Entry(Ann, ""));
        Assert.Equal(NameCache.Placeholder(Ann), At(r.Friends.Friends, 0).Name);
        r.Names.Learn(Ann, "Learned Resident");
        Assert.Equal("Learned Resident", At(r.Friends.Friends, 0).Name);
    }

    [Fact]
    public void Presence_changes_the_mark_and_tells_the_user_once_per_change()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));
        Assert.True(At(r.Friends.Friends, 0).Online);
        Assert.Equal([new FriendNotice(FriendNoticeKind.Online, Ann)], r.Notices);
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));       // nothing new
        Assert.Single(r.Notices);
        r.Friends.Receive(new FriendPresenceChanged(Ann, false));
        Assert.False(At(r.Friends.Friends, 0).Online);
        Assert.Equal(FriendNoticeKind.Offline, Last(r.Notices).Kind);
        Assert.Equal(2, r.Notices.Count);
    }

    [Fact]
    public void Turning_the_notices_off_keeps_the_marks_but_stops_the_lines()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        r.NoticesOn = false;
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));
        Assert.True(At(r.Friends.Friends, 0).Online);
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void An_online_notice_for_someone_not_yet_listed_is_kept_and_shows_when_they_join_the_list()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));       // the notice arrives before the list
        Assert.Empty(r.Friends.Friends);
        r.Friends.Receive(new FriendsListed([Entry(Ann, "Ann Example")]));
        Assert.True(At(r.Friends.Friends, 0).Online);
    }

    [Fact]
    public void Rights_changes_update_the_row_and_the_user_is_told_when_the_modify_right_they_received_changes()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        r.Friends.Receive(new FriendRightsChanged(Ann, Online | FriendRightsFlags.SeeOnMap, Online));
        Assert.Equal(Online | FriendRightsFlags.SeeOnMap, At(r.Friends.Friends, 0).Granted);
        Assert.Empty(r.Notices);                                        // our own grant echoed: nothing to say
        r.Friends.Receive(new FriendRightsChanged(Ann, Online | FriendRightsFlags.SeeOnMap, Online | FriendRightsFlags.ModifyObjects));
        Assert.Equal([new FriendNotice(FriendNoticeKind.ModifyGranted, Ann)], r.Notices);
        r.Friends.Receive(new FriendRightsChanged(Ann, Online | FriendRightsFlags.SeeOnMap, Online));
        Assert.Equal(FriendNoticeKind.ModifyRevoked, Last(r.Notices).Kind);
    }

    [Fact]
    public void An_offer_is_kept_for_an_answer_with_its_name_message_and_whether_it_came_online_and_a_second_replaces_the_first()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Offer, true, Body("be my friend")));
        var offer = Assert.Single(r.Friends.Offers);
        Assert.Equal(new PendingOffer(Ann, "Ann Example", "be my friend", Offer, true), offer);
        Assert.Empty(r.Transport.Commands);                             // never answered by itself
        var second = Guid.Parse("0ffe0ffe-0000-4000-8000-000000000009");
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", second, false, Body("again")));
        offer = Assert.Single(r.Friends.Offers);
        Assert.Equal(second, offer.OfferId);
        Assert.Equal("again", offer.Message);
        Assert.False(offer.Online);
    }

    [Fact]
    public void Accepting_answers_through_the_pipeline_adds_the_friend_with_the_default_relationship_and_removes_the_offer()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Offer, true, Body("hi")));
        Assert.True(r.Friends.Accept(Ann));
        Assert.Equal(new FriendshipAnswerCommand(Ann, Offer, true, false), Assert.Single(r.Sent<FriendshipAnswerCommand>()));
        Assert.Empty(r.Friends.Offers);
        var f = Assert.Single(r.Friends.Friends);
        Assert.Equal((Ann, "Ann Example", false, Online, Online), (f.Id, f.Name, f.Online, f.Granted, f.Received));
    }

    [Fact]
    public void An_offer_stored_while_away_is_answered_through_the_capability()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Offer, false, Body("hi")));
        Assert.True(r.Friends.Accept(Ann));
        Assert.True(Assert.Single(r.Sent<FriendshipAnswerCommand>()).ViaCapability);
        r.Friends.Receive(new FriendshipOffered(Bob, "Bob Example", Offer, false, Body("hi")));
        Assert.True(r.Friends.Decline(Bob));
        Assert.True(r.Sent<FriendshipAnswerCommand>().Last().ViaCapability);
    }

    [Fact]
    public void Declining_answers_and_removes_the_offer_without_adding_a_friend()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Offer, true, Body("hi")));
        Assert.True(r.Friends.Decline(Ann));
        Assert.Equal(new FriendshipAnswerCommand(Ann, Offer, false, false), Assert.Single(r.Sent<FriendshipAnswerCommand>()));
        Assert.Empty(r.Friends.Offers);
        Assert.Empty(r.Friends.Friends);
    }

    [Fact]
    public void An_answer_the_pipeline_refuses_leaves_the_offer_and_says_why()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Offer, true, Body("hi")));
        r.InWorld = false;
        Assert.False(r.Friends.Accept(Ann));
        Assert.Single(r.Friends.Offers);
        Assert.Empty(r.Friends.Friends);
        Assert.Equal("status.unavailable", r.Friends.Status?.Key);
        r.InWorld = true;
        Assert.True(r.Friends.Accept(Ann));
        Assert.Null(r.Friends.Status);
    }

    [Fact]
    public void Answering_an_offer_that_is_not_waiting_sends_nothing()
    {
        var r = new Rig();
        Assert.False(r.Friends.Accept(Ann));
        Assert.False(r.Friends.Decline(Ann));
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Our_offer_goes_through_the_pipeline_and_adds_nobody_until_they_accept()
    {
        var r = new Rig();
        Assert.True(r.Friends.Offer(Ann, "  would you?  "));
        Assert.Equal(new FriendshipOfferCommand(Ann, "would you?"), Assert.Single(r.Sent<FriendshipOfferCommand>()));
        Assert.Empty(r.Friends.Friends);
        r.Friends.Receive(new FriendshipAnswered(Ann, "Ann Example", true));
        var f = Assert.Single(r.Friends.Friends);
        Assert.Equal((Ann, "Ann Example", Online, Online), (f.Id, f.Name, f.Granted, f.Received));
        Assert.Equal([new FriendNotice(FriendNoticeKind.Accepted, Ann)], r.Notices);
    }

    [Fact]
    public void A_declined_offer_tells_the_user_and_adds_nobody()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipAnswered(Ann, "Ann Example", false));
        Assert.Empty(r.Friends.Friends);
        Assert.Equal([new FriendNotice(FriendNoticeKind.Declined, Ann)], r.Notices);
    }

    [Fact]
    public void An_offer_the_pipeline_refuses_shows_its_reason()
    {
        var r = new Rig();
        Assert.False(r.Friends.Offer(Guid.Empty, "hi"));
        Assert.Equal("status.friend.no_target", r.Friends.Status?.Key);
        Assert.False(r.Friends.Offer(Self, "hi"));                       // not oneself
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Ending_a_friendship_needs_the_confirmation_and_then_removes_the_friend()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"), Entry(Bob, "Bob Example"));
        Assert.False(r.Friends.End(Ann, confirmed: false));
        Assert.Equal(2, r.Friends.Friends.Count);
        Assert.Empty(r.Transport.Commands);
        Assert.Equal("status.friend.not_confirmed", r.Friends.Status?.Key);
        Assert.True(r.Friends.End(Ann, confirmed: true));
        Assert.Equal(new FriendshipEndCommand(Ann), Assert.Single(r.Sent<FriendshipEndCommand>()));
        Assert.Equal([Bob], r.Friends.Friends.Select(f => f.Id));
    }

    [Fact]
    public void When_the_other_person_ends_it_the_friend_leaves_and_the_user_is_told()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        r.Friends.Receive(new FriendshipEnded(Ann));
        Assert.Empty(r.Friends.Friends);
        Assert.Equal([new FriendNotice(FriendNoticeKind.Ended, Ann)], r.Notices);
        r.Friends.Receive(new FriendshipEnded(Ann));                     // already gone: nothing more
        Assert.Single(r.Notices);
    }

    [Fact]
    public void Rights_are_sent_whole_and_the_row_changes_only_when_the_grid_echoes_them()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        Assert.True(r.Friends.SetRights(Ann, Online | FriendRightsFlags.SeeOnMap, confirmedModify: false));
        Assert.Equal(new FriendRightsCommand(Ann, Online | FriendRightsFlags.SeeOnMap), Assert.Single(r.Sent<FriendRightsCommand>()));
        Assert.Equal(Online, At(r.Friends.Friends, 0).Granted);              // not yet
        r.Friends.Receive(new FriendRightsChanged(Ann, Online | FriendRightsFlags.SeeOnMap, Online));
        Assert.Equal(Online | FriendRightsFlags.SeeOnMap, At(r.Friends.Friends, 0).Granted);
    }

    [Fact]
    public void The_map_right_goes_only_with_the_online_right()
    {
        var r = WithFriends(Entry(Ann, "Ann Example"));
        Assert.True(r.Friends.SetRights(Ann, FriendRightsFlags.SeeOnMap, confirmedModify: false));
        Assert.Equal(FriendRightsFlags.None, Assert.Single(r.Sent<FriendRightsCommand>()).Rights);
    }

    [Fact]
    public void Giving_the_modify_right_newly_needs_a_confirmation_and_an_unchanged_one_does_not()
    {
        var r = WithFriends(Entry(Ann, "Ann Example", false, Online | FriendRightsFlags.ModifyObjects), Entry(Bob, "Bob Example"));
        Assert.False(r.Friends.SetRights(Bob, Online | FriendRightsFlags.ModifyObjects, confirmedModify: false));
        Assert.Empty(r.Transport.Commands);
        Assert.Equal("status.friend.modify_not_confirmed", r.Friends.Status?.Key);
        Assert.True(r.Friends.SetRights(Bob, Online | FriendRightsFlags.ModifyObjects, confirmedModify: true));
        Assert.True(r.Friends.SetRights(Ann, Online | FriendRightsFlags.ModifyObjects | FriendRightsFlags.SeeOnMap, confirmedModify: false));   // Ann already has it
        Assert.Equal(2, r.Sent<FriendRightsCommand>().Count());
        Assert.True(r.Friends.SetRights(Ann, Online, confirmedModify: false));                          // taking it away needs none
    }

    [Fact]
    public void Rights_for_someone_who_is_not_a_friend_are_not_sent()
    {
        var r = new Rig();
        Assert.False(r.Friends.SetRights(Ann, Online, confirmedModify: false));
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void The_model_says_when_anything_shown_changed()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendsListed([Entry(Ann, "Ann Example")]));
        int afterList = r.Changes;
        Assert.True(afterList > 0);
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));
        Assert.True(r.Changes > afterList);
    }

    // ================= group chat =================

    [Fact]
    public void Opening_a_group_chat_joins_it_once_and_shows_it_as_joining_under_the_groups_id()
    {
        var r = new Rig();
        var c = r.Ims.OpenGroup(GroupA, "Example Group");
        Assert.NotNull(c);
        Assert.Equal(GroupA, c!.SessionId);
        Assert.Equal("Example Group", c.Name);
        Assert.True(c.IsGroup);
        Assert.Equal(GroupChatState.Joining, c.Group);
        Assert.Equal(new GroupChatJoinCommand(GroupA), Assert.Single(r.Sent<GroupChatJoinCommand>()));
        Assert.Same(c, r.Ims.OpenGroup(GroupA, "Example Group"));        // showing it again does not join again
        Assert.Single(r.Sent<GroupChatJoinCommand>());
        Assert.Single(r.Ims.Open);
        Assert.Equal(GroupA, r.Ims.Active);
    }

    [Fact]
    public void The_grids_answer_makes_the_chat_joined_or_failed()
    {
        var r = new Rig();
        var a = G(r.Ims.OpenGroup(GroupA, "A"));
        var b = G(r.Ims.OpenGroup(GroupB, "B"));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupA, true));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupB, false));
        Assert.Equal(GroupChatState.Joined, a.Group);
        Assert.Equal(GroupChatState.Failed, b.Group);
        Assert.Equal("status.group.join_failed", b.Status?.Key);
        r.Ims.ReceiveJoined(new GroupChatJoined(Cy, true));              // not open: ignored
        Assert.Equal(2, r.Ims.Open.Count);
    }

    [Fact]
    public void A_join_the_pipeline_refuses_leaves_the_tab_failed_with_the_reason()
    {
        var r = new Rig { InWorld = false };
        var c = G(r.Ims.OpenGroup(GroupA, "A"));
        Assert.Equal(GroupChatState.Failed, c.Group);
        Assert.Equal("status.unavailable", c.Status?.Key);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Several_friends_start_one_conference_under_a_new_session_that_is_joined_when_the_grid_answers()
    {
        var r = new Rig();
        var c = r.Ims.OpenConference([Ann, Bob, Cy, Self, Ann]);   // this agent and a repeat are left out
        Assert.NotNull(c);
        Assert.True(c!.IsGroup);
        Assert.Equal(GroupChatState.Joining, c.Group);
        Assert.NotEqual(Guid.Empty, c.SessionId);
        var start = Assert.Single(r.Sent<StartConferenceCommand>());
        Assert.Equal(c.SessionId, start.SessionId);
        Assert.Equal([Ann, Bob, Cy], start.Participants);
        Assert.Equal(c.SessionId, r.Ims.Active);
        r.Ims.ReceiveJoined(new GroupChatJoined(c.SessionId, true));
        Assert.Equal(GroupChatState.Joined, c.Group);
        Assert.True(r.Ims.Send(c.SessionId, "hello all"));
        Assert.Equal(new GroupMessageCommand(c.SessionId, "hello all"), Assert.Single(r.Sent<GroupMessageCommand>()));
    }

    [Fact]
    public void One_friend_is_the_ordinary_conversation_and_none_starts_nothing()
    {
        var r = new Rig();
        var one = r.Ims.OpenConference([Ann, Ann]);
        Assert.NotNull(one);
        Assert.False(one!.IsGroup);
        Assert.Equal(Ann, one.Other);
        Assert.Null(r.Ims.OpenConference([Self, Guid.Empty]));
        Assert.Empty(r.Sent<StartConferenceCommand>());
        Assert.Single(r.Ims.Open);
    }

    [Fact]
    public void A_conference_the_pipeline_refuses_stays_failed_with_the_reason()
    {
        var r = new Rig { InWorld = false };
        var c = G(r.Ims.OpenConference([Ann, Bob]));
        Assert.Equal(GroupChatState.Failed, c.Group);
        Assert.Equal("status.unavailable", c.Status?.Key);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void A_conference_needs_two_others_and_a_session_id()
    {
        Assert.Contains(new ConferenceStartIntent(Guid.Empty, [Ann, Bob]).Validate(), i => i.Code == "conference.no_session");
        Assert.Contains(new ConferenceStartIntent(Guid.NewGuid(), [Ann, Ann, Guid.Empty]).Validate(), i => i.Code == "conference.too_few");
        Assert.Empty(new ConferenceStartIntent(Guid.NewGuid(), [Ann, Bob]).Validate());
    }

    [Fact]
    public void Nothing_is_sent_to_a_group_until_it_is_joined_and_then_the_text_goes_through_the_pipeline_with_an_own_line()
    {
        var r = new Rig();
        var c = G(r.Ims.OpenGroup(GroupA, "A"));
        Assert.False(r.Ims.Send(c.SessionId, "too early"));
        Assert.Empty(r.Sent<GroupMessageCommand>());
        Assert.Equal("status.group.not_joined", c.Status?.Key);
        Assert.Empty(c.Lines);
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupA, true));
        Assert.True(r.Ims.Send(c.SessionId, "  hello all  "));
        Assert.Equal(new GroupMessageCommand(GroupA, "hello all"), Assert.Single(r.Sent<GroupMessageCommand>()));
        var line = Assert.Single(c.Lines);
        Assert.Equal((ChatLineStyle.Own, "hello all"), (line.Style, line.Text));
        Assert.Null(c.Status);
        Assert.Empty(r.Sent<InstantMessageCommand>());                  // never as a one-to-one message
    }

    private static InstantMessageReceived GroupIm(Guid group, Guid from, string name, string text, bool offline = false, DateTimeOffset? sentAt = null) =>
        new(from, name, group, group, ImKind.GroupMessage, 17, offline, sentAt, true, Body(text));

    [Fact]
    public void A_group_message_for_an_open_session_is_shown_and_counted_unread_unless_that_tab_is_on_screen()
    {
        var r = new Rig();
        var a = G(r.Ims.OpenGroup(GroupA, "A"));
        var b = G(r.Ims.OpenGroup(GroupB, "B"));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupA, true));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupB, true));
        r.Ims.Receive(GroupIm(GroupB, Ann, "Ann Example", "to B"));
        Assert.Equal((1, "to B"), (b.Unread, Assert.Single(b.Lines).Text));
        Assert.Equal(ChatLineStyle.Agent, At(b.Lines, 0).Style);
        Assert.Equal("Ann Example", At(b.Lines, 0).Name);
        r.Ims.Visible = true;
        r.Ims.Activate(GroupA);
        r.Ims.Receive(GroupIm(GroupA, Ann, "Ann Example", "to A"));      // A is the shown tab on a visible panel
        Assert.Equal(0, a.Unread);
        Assert.Equal(1, b.Unread);
    }

    [Fact]
    public void Group_messages_for_a_session_that_is_not_open_are_dropped_and_counted()
    {
        var r = new Rig();
        r.Ims.Receive(GroupIm(GroupA, Ann, "Ann Example", "nobody asked"));
        Assert.Empty(r.Ims.Open);
        Assert.Equal(1, r.Ims.Ignored[17]);
    }

    [Fact]
    public void A_group_message_from_this_agent_is_not_added_a_second_time()
    {
        var r = new Rig();
        var c = G(r.Ims.OpenGroup(GroupA, "A"));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupA, true));
        r.Ims.Send(c.SessionId, "mine");
        r.Ims.Receive(GroupIm(GroupA, Self, "Self Name", "mine"));
        Assert.Single(c.Lines);
    }

    [Fact]
    public void A_stored_group_message_is_marked_saved_with_its_time_and_names_are_learned_from_messages()
    {
        var r = new Rig();
        var c = G(r.Ims.OpenGroup(GroupA, "A"));
        var sent = new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);
        r.Ims.Receive(GroupIm(GroupA, Ann, "Ann Example", "while away", offline: true, sentAt: sent));
        var line = Assert.Single(c.Lines);
        Assert.True(line.Saved);
        Assert.Equal(sent, line.SavedAt);
    }

    [Fact]
    public void Closing_a_group_tab_leaves_the_chat_session_and_a_one_to_one_close_leaves_nothing()
    {
        var r = new Rig();
        r.Ims.OpenGroup(GroupA, "A");
        r.Ims.OpenWith(Ann);
        r.Ims.Close(ImSessionId.For(Self, Ann));
        Assert.Empty(r.Sent<GroupChatLeaveCommand>());
        r.Ims.Close(GroupA);
        Assert.Equal(new GroupChatLeaveCommand(GroupA), Assert.Single(r.Sent<GroupChatLeaveCommand>()));
        Assert.Empty(r.Ims.Open);
    }

    [Fact]
    public void A_group_tab_sends_no_typing_traffic()
    {
        var r = new Rig();
        var c = G(r.Ims.OpenGroup(GroupA, "A"));
        r.Ims.ReceiveJoined(new GroupChatJoined(GroupA, true));
        r.Ims.InputChanged(c.SessionId, "typing...");
        r.Now += TimeSpan.FromSeconds(30);
        r.Ims.Tick();
        Assert.Empty(r.Sent<ImTypingCommand>());
    }

    // ================= groups, active group and title =================

    [Fact]
    public void The_groups_are_listed_by_name_and_the_active_group_and_title_follow_the_region()
    {
        var r = new Rig();
        r.Groups.Receive(new GroupsListed([new GroupEntry(GroupB, "beta group", true, true), new GroupEntry(GroupA, "Alpha Group", false, true)]));
        Assert.Equal([GroupA, GroupB], r.Groups.Groups.Select(g => g.Id));
        Assert.Equal(Guid.Empty, r.Groups.Active.Id);
        r.Groups.Receive(new ActiveGroupChanged(GroupB, "beta group", "Member"));
        Assert.Equal((GroupB, "beta group", "Member"), r.Groups.Active);
        r.Groups.Receive(new ActiveGroupChanged(Guid.Empty, "", ""));
        Assert.Equal(Guid.Empty, r.Groups.Active.Id);
    }

    [Fact]
    public void Activating_a_group_sends_one_message_and_the_marker_moves_only_on_the_regions_update()
    {
        var r = new Rig();
        r.Groups.Receive(new GroupsListed([new GroupEntry(GroupA, "Alpha Group", true, true)]));
        Assert.True(r.Groups.Activate(GroupA));
        Assert.Equal(new ActiveGroupCommand(GroupA), Assert.Single(r.Sent<ActiveGroupCommand>()));
        Assert.Equal(Guid.Empty, r.Groups.Active.Id);
        r.Groups.Receive(new ActiveGroupChanged(GroupA, "Alpha Group", "Owner"));
        Assert.Equal(GroupA, r.Groups.Active.Id);
        Assert.True(r.Groups.Activate(Guid.Empty));                       // none
        Assert.Equal(new ActiveGroupCommand(Guid.Empty), r.Sent<ActiveGroupCommand>().Last());
    }

    [Fact]
    public void Titles_are_asked_for_once_per_group_until_they_arrive_and_choosing_one_goes_through_the_pipeline()
    {
        var r = new Rig();
        r.Groups.LoadTitles(GroupA);
        r.Groups.LoadTitles(GroupA);
        Assert.Equal([GroupA], r.TitleRequests);
        Assert.Empty(r.Groups.TitlesOf(GroupA));
        r.Groups.Receive(new GroupTitlesListed(GroupA, [new GroupTitleEntry(Role1, "Member", true), new GroupTitleEntry(Role2, "Officer", false)]));
        Assert.Equal([new GroupTitleRow(Role1, "Member", true), new GroupTitleRow(Role2, "Officer", false)], r.Groups.TitlesOf(GroupA));
        Assert.True(r.Groups.SetTitle(GroupA, Role2));
        Assert.Equal(new ActiveTitleCommand(GroupA, Role2), Assert.Single(r.Sent<ActiveTitleCommand>()));
        r.Groups.LoadTitles(GroupA);                                       // known now: not asked again
        Assert.Single(r.TitleRequests);
    }

    [Fact]
    public void A_title_the_pipeline_refuses_says_why()
    {
        var r = new Rig();
        Assert.False(r.Groups.SetTitle(GroupA, Guid.Empty));
        Assert.Equal("status.group.no_role", r.Groups.Status?.Key);
        Assert.Empty(r.Transport.Commands);
    }

    // ================= group notices =================

    private static GroupNoticeReceived Notice(string subject = "Subject", string text = "Body", bool attachment = false, bool requested = false,
        DateTimeOffset? at = null, Guid? offer = null) =>
        new(GroupA, Ann, "Ann Example", Body(subject), Body(text), attachment, attachment ? "A Gift" : "", attachment ? 6 : 0, offer ?? Offer, at, requested);

    [Fact]
    public void A_notice_is_kept_unread_newest_first_with_its_time_and_the_message_time_wins_over_now()
    {
        var r = new Rig();
        r.Groups.Receive(Notice("first"));
        var sent = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        r.Groups.Receive(Notice("second", at: sent, offer: Guid.NewGuid()));
        Assert.Equal(["second", "first"], r.Groups.Notices.Select(n => n.Subject));
        Assert.Equal(sent, At(r.Groups.Notices, 0).At);
        Assert.Equal(T0, At(r.Groups.Notices, 1).At);
        Assert.Equal(2, r.Groups.UnreadNotices);
        r.Groups.MarkRead(At(r.Groups.Notices, 0).Id);
        Assert.Equal(1, r.Groups.UnreadNotices);
        Assert.Equal(("Ann Example", GroupA, Ann), (At(r.Groups.Notices, 1).FromName, At(r.Groups.Notices, 1).Group, At(r.Groups.Notices, 1).Sender));
    }

    [Fact]
    public void A_notice_the_user_asked_for_is_listed_but_not_unread()
    {
        var r = new Rig();
        r.Groups.Receive(Notice(requested: true));
        Assert.Single(r.Groups.Notices);
        Assert.Equal(0, r.Groups.UnreadNotices);
    }

    [Fact]
    public void An_attachment_is_offered_and_never_answered_by_itself()
    {
        var r = new Rig();
        r.Groups.Receive(Notice(attachment: true));
        var n = Assert.Single(r.Groups.Notices);
        Assert.Equal((true, "A Gift", AttachmentState.Offered), (n.HasAttachment, n.AttachmentName, n.Attachment));
        Assert.Empty(r.Transport.Commands);
        r.Groups.Receive(Notice(attachment: false, offer: Guid.NewGuid()));
        Assert.Equal(AttachmentState.None, At(r.Groups.Notices, 0).Attachment);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Accepting_an_attachment_sends_the_answer_once_and_then_the_offer_is_answered()
    {
        var r = new Rig();
        r.Groups.Receive(Notice(attachment: true));
        var id = At(r.Groups.Notices, 0).Id;
        Assert.True(r.Groups.AnswerAttachment(id, true));
        Assert.Equal(new GroupNoticeAnswerCommand(Ann, Offer, true, 6), Assert.Single(r.Sent<GroupNoticeAnswerCommand>()));
        Assert.Equal(AttachmentState.Accepted, At(r.Groups.Notices, 0).Attachment);
        Assert.False(r.Groups.AnswerAttachment(id, false));               // the transaction id is usable once
        Assert.Single(r.Sent<GroupNoticeAnswerCommand>());
    }

    [Fact]
    public void Declining_an_attachment_sends_the_decline()
    {
        var r = new Rig();
        r.Groups.Receive(Notice(attachment: true));
        Assert.True(r.Groups.AnswerAttachment(At(r.Groups.Notices, 0).Id, false));
        Assert.Equal(new GroupNoticeAnswerCommand(Ann, Offer, false, 6), Assert.Single(r.Sent<GroupNoticeAnswerCommand>()));
        Assert.Equal(AttachmentState.Declined, At(r.Groups.Notices, 0).Attachment);
    }

    [Fact]
    public void An_answer_that_did_not_leave_keeps_the_offer_open_so_it_can_be_tried_again()
    {
        var r = new Rig();
        r.Groups.Receive(Notice(attachment: true));
        var id = At(r.Groups.Notices, 0).Id;
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("no_folder");
        Assert.False(r.Groups.AnswerAttachment(id, true));
        Assert.Equal(AttachmentState.Offered, At(r.Groups.Notices, 0).Attachment);
        Assert.Equal("status.failed", r.Groups.Status?.Key);
        r.Transport.OnCommand = null;
        Assert.True(r.Groups.AnswerAttachment(id, true));
        Assert.Equal(AttachmentState.Accepted, At(r.Groups.Notices, 0).Attachment);
    }

    [Fact]
    public void A_notice_with_no_attachment_or_an_unknown_notice_cannot_be_answered()
    {
        var r = new Rig();
        r.Groups.Receive(Notice());
        Assert.False(r.Groups.AnswerAttachment(At(r.Groups.Notices, 0).Id, true));
        Assert.False(r.Groups.AnswerAttachment(Guid.NewGuid(), true));
        Assert.Empty(r.Transport.Commands);
    }

    // ================= the hub =================

    private sealed class HubRig
    {
        public DateTimeOffset Now = T0;
        public readonly FakeTransport Transport = new();
        public readonly NameCache Names = new();
        public readonly List<GroupTitleRequest> TitleRequests = new();
        public readonly CommsHub Hub;
        public readonly SettingsService Settings = new(null);
        public readonly List<int> Requested = new();

        public readonly record struct GroupTitleRequest(Guid Group);

        public HubRig(Func<FriendNotice, string?>? noticeText = null)
        {
            var pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Hub = new CommsHub(Self, Names, pipeline.Submit, Settings, () => Now, null, noticeText, g => TitleRequests.Add(new GroupTitleRequest(g)));
        }
    }

    [Fact]
    public void The_hub_routes_friend_and_group_events_to_their_models_on_the_main_thread()
    {
        var h = new HubRig();
        h.Hub.Enqueue(new FriendsListed([Entry(Ann, "Ann Example")]));
        h.Hub.Enqueue(new FriendPresenceChanged(Ann, true));
        h.Hub.Enqueue(new FriendshipOffered(Bob, "Bob Example", Offer, true, Body("hi")));
        h.Hub.Enqueue(new GroupsListed([new GroupEntry(GroupA, "Alpha Group", true, true)]));
        h.Hub.Enqueue(new ActiveGroupChanged(GroupA, "Alpha Group", "Member"));
        h.Hub.Enqueue(Notice());
        Assert.Empty(h.Hub.Friends.Friends);                                // nothing before the frame drains the queue
        Assert.Equal(6, h.Hub.Pump());
        Assert.True(At(h.Hub.Friends.Friends, 0).Online);
        Assert.Single(h.Hub.Friends.Offers);
        Assert.Single(h.Hub.Groups.Groups);
        Assert.Equal(GroupA, h.Hub.Groups.Active.Id);
        Assert.Single(h.Hub.Groups.Notices);
    }

    [Fact]
    public void The_hub_opens_a_group_chat_that_the_grid_then_answers_and_shows_its_messages()
    {
        var h = new HubRig();
        var c = G(h.Hub.Ims.OpenGroup(GroupA, "Alpha Group"));
        h.Hub.Enqueue(new GroupChatJoined(GroupA, true));
        h.Hub.Enqueue(GroupIm(GroupA, Ann, "Ann Example", "hello group"));
        h.Hub.Pump();
        Assert.Equal(GroupChatState.Joined, c.Group);
        Assert.Equal("hello group", Assert.Single(c.Lines).Text);
    }

    [Fact]
    public void A_friend_notice_becomes_a_system_line_in_nearby_chat_and_in_that_friends_open_conversation()
    {
        HubRig h = null!;
        h = new HubRig(n => $"{n.Kind}:{h.Names.Get(n.Friend) ?? "?"}");
        h.Names.Learn(Ann, "Ann Example");
        h.Hub.Ims.OpenWith(Ann);
        h.Hub.Enqueue(new FriendsListed([Entry(Ann, "Ann Example")]));
        h.Hub.Enqueue(new FriendPresenceChanged(Ann, true));
        h.Hub.Pump();
        Assert.Contains(h.Hub.Nearby.Lines, l => l.Style == ChatLineStyle.System && l.Text == "Online:Ann Example");
        Assert.Contains(G(h.Hub.Ims.Find(ImSessionId.For(Self, Ann))).Lines, l => l.Style == ChatLineStyle.System && l.Text == "Online:Ann Example");
    }

    [Fact]
    public void A_name_that_arrives_late_refreshes_the_friend_list()
    {
        var h = new HubRig();
        h.Hub.Enqueue(new FriendsListed([Entry(Ann, "")]));
        h.Hub.Pump();
        int changes = 0;
        h.Hub.Friends.Changed += () => changes++;
        var names = new AgentNamesReceived(new Dictionary<Guid, string> { [Ann] = "Late Resident" });
        h.Names.Learn(names);
        h.Hub.Enqueue(names);
        h.Hub.Pump();
        Assert.Equal("Late Resident", At(h.Hub.Friends.Friends, 0).Name);
        Assert.True(changes > 0);
    }

    [Fact]
    public void Opening_an_im_from_the_list_opens_the_conversation_with_that_friend()
    {
        var h = new HubRig();
        h.Hub.Enqueue(new FriendsListed([Entry(Ann, "Ann Example")]));
        h.Hub.Pump();
        var c = h.Hub.Ims.OpenWith(At(h.Hub.Friends.Friends, 0).Id);
        Assert.Equal(ImSessionId.For(Self, Ann), G(c).SessionId);
        Assert.Equal(c.SessionId, h.Hub.Ims.Active);
    }

    [Fact]
    public void The_online_notice_setting_defaults_on_and_the_hub_follows_it()
    {
        var h = new HubRig(n => n.Kind.ToString());
        Assert.True(h.Settings.Get(Settings.FriendOnlineNotices));
        h.Settings.Set(Settings.FriendOnlineNotices, false);
        h.Hub.Enqueue(new FriendsListed([Entry(Ann, "Ann Example")]));
        h.Hub.Enqueue(new FriendPresenceChanged(Ann, true));
        h.Hub.Pump();
        Assert.DoesNotContain(h.Hub.Nearby.Lines, l => l.Style == ChatLineStyle.System);
        Assert.True(At(h.Hub.Friends.Friends, 0).Online);
    }
}

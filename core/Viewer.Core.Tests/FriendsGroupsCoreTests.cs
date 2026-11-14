// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Negative chat channels, friends and presence, group chat, group notices and the active group, below the interface: the
/// intents' checks, what each becomes on the command side, the adapter's translation of the library's messages, the
/// session's communication channel and the rule that what people write reaches neither a log nor a capture. No grid: a fake
/// transport and synthetic events. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class FriendsGroupsCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Group = Guid.Parse("6a6a6a6a-0000-4000-8000-0000000000a1");
    private static readonly Guid Offer = Guid.Parse("0ffe0ffe-0000-4000-8000-000000000001");
    private static readonly Guid Role = Guid.Parse("7e7e7e7e-0000-4000-8000-0000000000a2");
    private const string SecretText = "a private line nobody may log 7f3c";
    private const string SecretName = "Secretive Resident";

    private sealed class Rig
    {
        public FakeTransport Transport = new();
        public ActionPipeline Pipeline;
        public Rig() => Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
    }

    // ---------------- negative channels ----------------

    [Fact]
    public void A_negative_channel_is_a_valid_say_and_reaches_the_command_side_with_its_channel()
    {
        Assert.Empty(new SayIntent("hello", -5).Validate());
        Assert.Empty(new SayIntent("hello", int.MinValue).Validate());
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new SayIntent("  hello  ", -5, ChatKind.Say)).Status);
        Assert.Equal(new SayCommand("hello", -5, ChatKind.Say), Assert.Single(r.Transport.Commands));
    }

    [Fact]
    public void A_negative_channel_line_is_cut_by_the_protocol_at_254_bytes_so_a_longer_one_is_refused_not_cut()
    {
        Assert.Empty(new SayIntent(new string('a', 254), -1).Validate());
        Assert.Contains(new SayIntent(new string('a', 255), -1).Validate(), i => i.Code == "say.too_long");
        Assert.Contains(new SayIntent(new string('é', 128), -1).Validate(), i => i.Code == "say.too_long");   // 256 bytes
        Assert.Empty(new SayIntent(new string('a', 1023), 0).Validate());                                    // other channels keep the full limit
        Assert.Empty(new SayIntent(new string('a', 1023), 7).Validate());
    }

    // ---------------- the intents' own checks ----------------

    public static TheoryData<ActionIntent, string> BadIntents => new()
    {
        { new FriendshipOfferIntent(Guid.Empty, "hi"), "friend.no_target" },
        { new FriendshipOfferIntent(Friend, new string('a', 1024)), "friend.message_too_long" },
        { new FriendshipOfferIntent(Friend, "a\u0000b"), "friend.message_control_chars" },
        { new FriendshipAnswerIntent(Guid.Empty, Offer, true, false), "friend.no_target" },
        { new FriendshipAnswerIntent(Friend, Guid.Empty, true, false), "friend.no_offer" },
        { new FriendshipEndIntent(Guid.Empty, true), "friend.no_target" },
        { new FriendshipEndIntent(Friend, false), "friend.not_confirmed" },
        { new FriendRightsIntent(Guid.Empty, FriendRightsFlags.SeeOnline, false), "friend.no_target" },
        { new FriendRightsIntent(Friend, (FriendRightsFlags)8, false), "friend.bad_rights" },
        { new FriendRightsIntent(Friend, FriendRightsFlags.SeeOnMap, false), "friend.map_needs_online" },
        { new FriendRightsIntent(Friend, FriendRightsFlags.SeeOnline | FriendRightsFlags.ModifyObjects, false), "friend.modify_not_confirmed" },
        { new GroupChatJoinIntent(Guid.Empty), "group.no_group" },
        { new GroupChatLeaveIntent(Guid.Empty), "group.no_group" },
        { new GroupMessageIntent(Guid.Empty, "hi"), "group.no_group" },
        { new GroupMessageIntent(Group, " "), "group.empty" },
        { new GroupMessageIntent(Group, new string('a', 1024)), "group.too_long" },
        { new GroupMessageIntent(Group, "a\u0007b"), "group.control_chars" },
        { new ActiveTitleIntent(Guid.Empty, Role), "group.no_group" },
        { new ActiveTitleIntent(Group, Guid.Empty), "group.no_role" },
        { new GroupNoticeAnswerIntent(Guid.Empty, Offer, true, 3), "group.no_sender" },
        { new GroupNoticeAnswerIntent(Friend, Guid.Empty, true, 3), "group.no_offer" },
        { new GroupNoticeAnswerIntent(Friend, Offer, true, -1), "group.bad_asset_type" },
    };

    [Theory]
    [MemberData(nameof(BadIntents))]
    public void A_bad_intent_is_refused_with_its_reason_and_nothing_is_sent(ActionIntent intent, string code)
    {
        Assert.Contains(intent.Validate(), i => i.Code == code);
        var r = new Rig();
        var result = r.Pipeline.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Good_friend_and_group_intents_pass_their_checks_including_the_limits_and_the_empty_group_for_none()
    {
        ActionIntent[] good =
        [
            new FriendshipOfferIntent(Friend, ""),                                      // the offer's message may be empty
            new FriendshipOfferIntent(Friend, new string('a', 1023)),
            new FriendshipAnswerIntent(Friend, Offer, false, true),
            new FriendshipEndIntent(Friend, true),
            new FriendRightsIntent(Friend, FriendRightsFlags.None, false),
            new FriendRightsIntent(Friend, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap, false),
            new FriendRightsIntent(Friend, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap | FriendRightsFlags.ModifyObjects, true),
            new GroupChatJoinIntent(Group),
            new GroupMessageIntent(Group, new string('a', 1023)),
            new ActiveGroupIntent(Guid.Empty),                                          // "no active group" is the empty id
            new ActiveGroupIntent(Group),
            new ActiveTitleIntent(Group, Role),
            new GroupNoticeAnswerIntent(Friend, Offer, false, 0),
        ];
        foreach (var intent in good)
            Assert.Empty(intent.Validate());
    }

    [Fact]
    public void Each_friend_and_group_intent_becomes_its_command_with_its_text_normalised()
    {
        var r = new Rig();
        var cases = new (ActionIntent Intent, TransportCommand Command)[]
        {
            (new FriendshipOfferIntent(Friend, "  be friends?  "), new FriendshipOfferCommand(Friend, "be friends?")),
            (new FriendshipAnswerIntent(Friend, Offer, true, false), new FriendshipAnswerCommand(Friend, Offer, true, false)),
            (new FriendshipAnswerIntent(Friend, Offer, false, true), new FriendshipAnswerCommand(Friend, Offer, false, true)),
            (new FriendshipEndIntent(Friend, true), new FriendshipEndCommand(Friend)),
            (new FriendRightsIntent(Friend, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap, false), new FriendRightsCommand(Friend, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap)),
            (new GroupChatJoinIntent(Group), new GroupChatJoinCommand(Group)),
            (new GroupChatLeaveIntent(Group), new GroupChatLeaveCommand(Group)),
            (new GroupMessageIntent(Group, " hello all\nsecond line "), new GroupMessageCommand(Group, "hello all\nsecond line")),
            (new ActiveGroupIntent(Group), new ActiveGroupCommand(Group)),
            (new ActiveGroupIntent(Guid.Empty), new ActiveGroupCommand(Guid.Empty)),
            (new ActiveTitleIntent(Group, Role), new ActiveTitleCommand(Group, Role)),
            (new GroupNoticeAnswerIntent(Friend, Offer, true, 6), new GroupNoticeAnswerCommand(Friend, Offer, true, 6)),
        };
        foreach (var (intent, command) in cases)
        {
            Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(intent).Status);
            Assert.Equal(command, r.Transport.Commands[^1]);
        }
        Assert.Equal(cases.Length, r.Transport.Commands.Count);
    }

    [Fact]
    public void Ending_a_friendship_and_answering_a_notice_attachment_wait_for_the_user_and_are_not_repeated_by_the_pipeline()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new FriendshipEndIntent(Friend, false)).Status);
        Assert.Empty(r.Transport.Commands);
        r.Pipeline.Submit(new FriendshipEndIntent(Friend, true));
        Assert.Single(r.Transport.Commands);
    }

    // ---------------- the adapter's translation ----------------

    private static InstantMessage LibIm(InstantMessageDialog dialog, InstantMessageOnline offline = InstantMessageOnline.Online, byte[]? bucket = null,
        string message = SecretText, bool group = false, Guid? session = null) => new()
    {
        FromAgentID = new UUID(Friend),
        FromAgentName = SecretName,
        ToAgentID = new UUID(Self),
        IMSessionID = new UUID(session ?? Offer),
        Dialog = dialog,
        Offline = offline,
        Timestamp = default,
        GroupIM = group,
        Message = message,
        BinaryBucket = bucket ?? [],
    };

    [Fact]
    public void A_friendship_offer_message_becomes_an_offer_with_its_conversation_id_and_whether_it_arrived_online()
    {
        var online = Assert.IsType<FriendshipOffered>(LmvTransport.ToFriendEvent(LibIm(InstantMessageDialog.FriendshipOffered)));
        Assert.Equal(Friend, online.From);
        Assert.Equal(SecretName, online.FromName);
        Assert.Equal(Offer, online.OfferId);
        Assert.True(online.Online);
        Assert.Equal(SecretText, online.Message.Reveal());
        var stored = Assert.IsType<FriendshipOffered>(LmvTransport.ToFriendEvent(LibIm(InstantMessageDialog.FriendshipOffered, InstantMessageOnline.Offline)));
        Assert.False(stored.Online);
    }

    [Fact]
    public void Accepted_and_declined_messages_become_answers_and_other_dialogs_are_not_friend_events()
    {
        var yes = Assert.IsType<FriendshipAnswered>(LmvTransport.ToFriendEvent(LibIm(InstantMessageDialog.FriendshipAccepted)));
        Assert.True(yes.Accepted);
        Assert.Equal(Friend, yes.From);
        var no = Assert.IsType<FriendshipAnswered>(LmvTransport.ToFriendEvent(LibIm(InstantMessageDialog.FriendshipDeclined)));
        Assert.False(no.Accepted);
        foreach (var dialog in new[] { InstantMessageDialog.MessageFromAgent, InstantMessageDialog.GroupNotice, InstantMessageDialog.SessionSend, InstantMessageDialog.StartTyping })
            Assert.Null(LmvTransport.ToFriendEvent(LibIm(dialog)));
    }

    [Fact]
    public void A_friend_is_translated_with_the_rights_given_and_received_the_right_way_round()
    {
        // the library's "their rights" are what this agent gave them, and "my rights" what they gave this agent
        var e = LmvTransport.ToFriend(Friend, "Some Friend", true, FriendRights.CanSeeOnline | FriendRights.CanSeeOnMap, FriendRights.CanSeeOnline | FriendRights.CanModifyObjects);
        Assert.Equal(new FriendEntry(Friend, "Some Friend", true, FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap, FriendRightsFlags.SeeOnline | FriendRightsFlags.ModifyObjects), e);
        Assert.Equal("", LmvTransport.ToFriend(Friend, null, false, FriendRights.None, FriendRights.None).Name);
    }

    [Fact]
    public void A_session_send_message_for_a_group_chat_session_is_a_group_message_and_otherwise_stays_other()
    {
        var inGroup = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.SessionSend, session: Group), groupSession: true);
        Assert.Equal(ImKind.GroupMessage, inGroup.Kind);
        Assert.True(inGroup.Group);
        Assert.Equal(Group, inGroup.SessionId);
        var flagged = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.SessionSend, group: true, session: Group));
        Assert.Equal(ImKind.GroupMessage, flagged.Kind);
        Assert.Equal(ImKind.Other, LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.SessionSend)).Kind);   // a conference, not a group
        Assert.Equal(ImKind.Message, LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.MessageFromAgent, group: true)).Kind);
    }

    private static byte[] NoticeBucket(bool attachment, byte assetType, Guid group, string itemName)
    {
        var bytes = new List<byte> { (byte)(attachment ? 1 : 0), assetType };
        bytes.AddRange(new UUID(group).GetBytes());
        bytes.AddRange(Encoding.UTF8.GetBytes(itemName));
        bytes.Add(0);
        return bytes.ToArray();
    }

    [Fact]
    public void A_group_notice_is_read_from_its_subject_text_and_binary_bucket()
    {
        var n = Assert.IsType<GroupNoticeReceived>(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: NoticeBucket(true, 6, Group, "A Gift"), message: "The subject|The body text")));
        Assert.Equal(Group, n.GroupId);
        Assert.Equal(Friend, n.Sender);
        Assert.Equal(SecretName, n.FromName);
        Assert.Equal("The subject", n.Subject.Reveal());
        Assert.Equal("The body text", n.Text.Reveal());
        Assert.True(n.HasAttachment);
        Assert.Equal("A Gift", n.AttachmentName);
        Assert.Equal(6, n.AssetType);
        Assert.Equal(Offer, n.OfferId);
        Assert.False(n.Requested);
    }

    [Fact]
    public void A_notice_without_attachment_a_bar_in_the_body_and_a_requested_notice_are_read_correctly()
    {
        var plain = Assert.IsType<GroupNoticeReceived>(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: NoticeBucket(false, 0, Group, ""), message: "S|a|b")));
        Assert.False(plain.HasAttachment);
        Assert.Equal("S", plain.Subject.Reveal());
        Assert.Equal("a|b", plain.Text.Reveal());                        // only the first bar splits
        var onlySubject = Assert.IsType<GroupNoticeReceived>(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: NoticeBucket(false, 0, Group, ""), message: "just a subject")));
        Assert.Equal("just a subject", onlySubject.Subject.Reveal());
        Assert.Equal("", onlySubject.Text.Reveal());
        var asked = Assert.IsType<GroupNoticeReceived>(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNoticeRequested, bucket: NoticeBucket(false, 0, Group, ""), message: "S|T")));
        Assert.True(asked.Requested);
    }

    [Fact]
    public void A_malformed_notice_is_dropped_and_other_dialogs_are_not_notices()
    {
        Assert.Null(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: [])));
        Assert.Null(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: [1, 6, 0, 0])));
        var unterminated = NoticeBucket(true, 6, Group, "Name");
        Assert.Null(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: unterminated[..^1])));
        Assert.Null(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.GroupNotice, bucket: NoticeBucket(false, 0, Guid.Empty, ""))));
        Assert.Null(LmvTransport.ToGroupNotice(LibIm(InstantMessageDialog.MessageFromAgent, bucket: NoticeBucket(false, 0, Group, ""))));
    }

    // ---------------- never in a capture ----------------

    [Fact]
    public void None_of_the_friend_or_group_records_is_written_to_a_capture()
    {
        TransportEvent[] events =
        [
            new FriendsListed([new FriendEntry(Friend, "N", false, FriendRightsFlags.None, FriendRightsFlags.None)]),
            new FriendPresenceChanged(Friend, true),
            new FriendRightsChanged(Friend, FriendRightsFlags.SeeOnline, FriendRightsFlags.None),
            new FriendshipOffered(Friend, "N", Offer, true, new MessageBody("x", MessageChannel.InstantMessage)),
            new FriendshipAnswered(Friend, "N", true),
            new FriendshipEnded(Friend),
            new GroupsListed([new GroupEntry(Group, "G", true, true)]),
            new ActiveGroupChanged(Group, "G", "T"),
            new GroupTitlesListed(Group, [new GroupTitleEntry(Role, "T", true)]),
            new GroupChatJoined(Group, true),
            new GroupNoticeReceived(Group, Friend, "N", new MessageBody("s", MessageChannel.InstantMessage), new MessageBody("t", MessageChannel.InstantMessage), false, "", 0, Offer, null, false),
        ];
        foreach (var e in events)
            Assert.False(CaptureFile.IsRecorded(e), e.GetType().Name);
    }

    // ---------------- the session's communication channel ----------------

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld()
    {
        var fake = new FakeTransport();
        var log = new ListSink();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(log, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, log);
    }

    [Fact]
    public void Friend_and_group_events_reach_the_communication_channel_after_the_names_have_learned_from_them()
    {
        var (session, fake, _) = InWorld();
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        TransportEvent[] events =
        [
            new FriendsListed([new FriendEntry(Friend, "Listed Friend", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]),
            new FriendPresenceChanged(Friend, true),
            new FriendRightsChanged(Friend, FriendRightsFlags.None, FriendRightsFlags.None),
            new FriendshipOffered(Self, "Offering Resident", Offer, true, new MessageBody("hi", MessageChannel.InstantMessage)),
            new FriendshipAnswered(Friend, "Answering Resident", true),
            new FriendshipEnded(Friend),
            new GroupsListed([new GroupEntry(Group, "A Group", true, true)]),
            new ActiveGroupChanged(Group, "A Group", "Member"),
            new GroupTitlesListed(Group, []),
            new GroupChatJoined(Group, true),
            new GroupNoticeReceived(Group, Friend, "Notice Sender", new MessageBody("s", MessageChannel.InstantMessage), new MessageBody("t", MessageChannel.InstantMessage), false, "", 0, Offer, null, false),
        ];
        foreach (var e in events)
            fake.Raise(e);
        Assert.Equal(events.Length, seen.Count);
        Assert.Equal("Notice Sender", session.Names.Get(Friend));   // the latest name wins
        Assert.Equal("Offering Resident", session.Names.Get(Self));
    }

    [Fact]
    public void Names_are_learned_from_the_friend_list_offers_answers_and_notices_but_not_from_empty_names()
    {
        var names = new NameCache();
        names.Learn(new FriendsListed([new FriendEntry(Friend, "Listed Friend", false, FriendRightsFlags.None, FriendRightsFlags.None), new FriendEntry(Self, "", false, FriendRightsFlags.None, FriendRightsFlags.None)]));
        Assert.Equal("Listed Friend", names.Get(Friend));
        Assert.Null(names.Get(Self));
        names.Learn(new FriendshipOffered(Self, "Offerer", Offer, true, new MessageBody("", MessageChannel.InstantMessage)));
        Assert.Equal("Offerer", names.Get(Self));
        names.Learn(new FriendshipAnswered(Group, "Answerer", false));
        Assert.Equal("Answerer", names.Get(Group));
        var other = Guid.Parse("00000000-0000-4000-8000-0000000000f0");
        names.Learn(new GroupNoticeReceived(Group, other, "Notice Sender", new MessageBody("", MessageChannel.InstantMessage), new MessageBody("", MessageChannel.InstantMessage), false, "", 0, Offer, null, false));
        Assert.Equal("Notice Sender", names.Get(other));
        Assert.NotEqual("Notice Sender", names.Get(Group));                // the group's id is not a person
    }

    [Fact]
    public void The_groups_are_asked_for_once_when_the_session_reaches_the_world()
    {
        var (_, fake, _) = InWorld();
        Assert.Equal(1, fake.GroupRequests);
    }

    [Fact]
    public void Group_titles_are_asked_for_through_the_session_and_only_while_it_runs()
    {
        var (session, fake, _) = InWorld();
        session.RequestGroupTitles(Group);
        Assert.Equal([Group], fake.TitleRequests);
        session.Dispose();
        session.RequestGroupTitles(Group);
        Assert.Single(fake.TitleRequests);
    }

    [Fact]
    public void No_name_text_message_or_other_persons_id_reaches_the_log()
    {
        var (session, fake, log) = InWorld();
        fake.Raise(new FriendsListed([new FriendEntry(Friend, SecretName, true, FriendRightsFlags.SeeOnline, FriendRightsFlags.None)]));
        fake.Raise(new FriendshipOffered(Friend, SecretName, Offer, true, new MessageBody(SecretText, MessageChannel.InstantMessage)));
        fake.Raise(new GroupNoticeReceived(Group, Friend, SecretName, new MessageBody(SecretText, MessageChannel.InstantMessage), new MessageBody(SecretText, MessageChannel.InstantMessage), true, "Secret Item", 6, Offer, null, false));
        fake.Raise(new GroupChatJoined(Group, true));
        fake.Raise(new InstantMessageReceived(Friend, SecretName, Self, Group, ImKind.GroupMessage, 17, false, null, true, new MessageBody(SecretText, MessageChannel.InstantMessage)));
        session.Dispose();
        string all = log.Text;
        Assert.DoesNotContain(SecretText, all);
        Assert.DoesNotContain(SecretName, all);
        Assert.DoesNotContain("Secret Item", all);
        Assert.DoesNotContain(Friend.ToString(), all);
        Assert.DoesNotContain(Group.ToString(), all);
    }
}

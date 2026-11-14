// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;
using ChatType = LibreMetaverse.ChatType;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The incoming side for chat and instant messages: the adapter's translation, the session's communication channel, the
/// name cache, the offline-message request, the typing commands, and the rule that what people write reaches neither a log
/// nor a capture. No grid: a fake transport and synthetic events. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class CommsFoundationTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef Elsewhere = new(1099511628032512UL, "Other Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private const string SecretText = "a private line nobody may log 7f3c";

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld()
    {
        var fake = new FakeTransport();
        var log = new ListSink();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(log, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, log);
    }

    private static ChatReceived Chat(SimRef? sim, Guid from, string name, ChatSource source = ChatSource.Agent, ChatLineType type = ChatLineType.Say,
        string text = SecretText, ChatAudible audible = ChatAudible.Fully) =>
        new(sim, from, from, name, source, type, audible, new Vector3(1, 2, 3), new MessageBody(text, MessageChannel.LocalChat));

    private static InstantMessageReceived Im(Guid from, string name, ImKind kind = ImKind.Message, bool offline = false, string text = SecretText) =>
        new(from, name, Self, ImSessionId.For(Self, from), kind, 0, offline, null, false, new MessageBody(text, MessageChannel.InstantMessage));

    // ---------------- conversation ids ----------------

    [Fact]
    public void A_one_to_one_conversation_id_is_the_two_ids_xored_and_the_same_from_either_side()
    {
        var id = ImSessionId.For(Self, Friend);
        var a = Self.ToByteArray(); var b = Friend.ToByteArray();
        Assert.Equal(new Guid(a.Zip(b, (x, y) => (byte)(x ^ y)).ToArray()), id);
        Assert.Equal(id, ImSessionId.For(Friend, Self));
    }

    [Fact]
    public void A_conversation_with_oneself_uses_ones_own_id() => Assert.Equal(Self, ImSessionId.For(Self, Self));

    [Fact]
    public void The_conversation_id_matches_the_librarys_own_rule() =>
        Assert.Equal((new UUID(Self) ^ new UUID(Friend)).Guid, ImSessionId.For(Self, Friend));

    // ---------------- name cache ----------------

    [Fact]
    public void An_unknown_name_is_null_and_is_asked_for_once_in_one_batch()
    {
        var cache = new NameCache();
        var fake = new FakeTransport();
        var other = Guid.NewGuid();
        Assert.Null(cache.Get(Friend));
        Assert.Null(cache.Get(other));
        Assert.Null(cache.Get(Friend));
        Assert.Equal(2, cache.Flush(fake));
        Assert.Single(fake.NameRequests);
        Assert.Equal(new[] { Friend, other }.OrderBy(g => g), fake.NameRequests[0].OrderBy(g => g));
        Assert.Null(cache.Get(Friend));
        Assert.Equal(0, cache.Flush(fake));   // still outstanding: not asked again
        Assert.Single(fake.NameRequests);
    }

    [Fact]
    public void An_unanswered_name_is_asked_for_again_only_after_the_retry_time()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cache = new NameCache(() => now);
        var fake = new FakeTransport();
        cache.Get(Friend);
        Assert.Equal(1, cache.Flush(fake));
        now += NameCache.RetryAfter - TimeSpan.FromSeconds(1);
        cache.Get(Friend);
        Assert.Equal(0, cache.Flush(fake));
        now += TimeSpan.FromSeconds(2);
        cache.Get(Friend);
        Assert.Equal(1, cache.Flush(fake));
        Assert.Equal(2, fake.NameRequests.Count);
    }

    [Fact]
    public void Requests_are_split_into_batches_of_the_largest_size()
    {
        var cache = new NameCache();
        var fake = new FakeTransport();
        for (int i = 0; i < NameCache.MaxBatch + 5; i++)
            cache.Get(Guid.NewGuid());
        Assert.Equal(NameCache.MaxBatch + 5, cache.Flush(fake));
        Assert.Equal([NameCache.MaxBatch, 5], fake.NameRequests.Select(r => r.Count));
    }

    [Fact]
    public void A_learned_name_is_returned_raises_one_change_and_is_not_asked_for()
    {
        var cache = new NameCache();
        var changes = new List<(Guid, string)>();
        cache.NameChanged += (id, n) => changes.Add((id, n));
        cache.Learn(Friend, "Example Resident");
        cache.Learn(Friend, "Example Resident");
        Assert.Equal("Example Resident", cache.Get(Friend));
        Assert.Equal([(Friend, "Example Resident")], changes);
        Assert.Equal(0, cache.Flush(new FakeTransport()));
        cache.Learn(Friend, "Renamed Resident");
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_name_or_empty_id_is_ignored(string? name)
    {
        var cache = new NameCache();
        cache.Learn(Friend, name);
        cache.Learn(Guid.Empty, "Someone");
        Assert.Null(cache.Get(Friend));
        Assert.Null(cache.Get(Guid.Empty));
    }

    [Fact]
    public void Names_are_learned_from_an_agents_chat_an_instant_message_and_a_name_reply_but_not_from_object_chat()
    {
        var cache = new NameCache();
        var objectId = Guid.NewGuid();
        var third = Guid.NewGuid();
        cache.Learn(Chat(Here, Friend, "Chatty Resident"));
        cache.Learn(Chat(Here, objectId, "A Box", ChatSource.Object));
        cache.Learn(Im(third, "Messaging Resident"));
        cache.Learn(new AgentNamesReceived(new Dictionary<Guid, string> { [objectId] = "Replied Name" }));
        Assert.Equal("Chatty Resident", cache.Get(Friend));
        Assert.Equal("Messaging Resident", cache.Get(third));
        Assert.Equal("Replied Name", cache.Get(objectId));   // only the reply named it, not the object chat
    }

    [Fact]
    public void An_object_chat_line_does_not_name_its_id()
    {
        var cache = new NameCache();
        var objectId = Guid.NewGuid();
        cache.Learn(Chat(Here, objectId, "A Box", ChatSource.Object));
        Assert.Null(cache.Get(objectId));
    }

    [Fact]
    public void The_placeholder_is_the_ids_first_eight_hex_digits() =>
        Assert.Equal("0a1b2c3d", NameCache.Placeholder(Friend));

    // ---------------- the session's communication channel ----------------

    [Fact]
    public void Chat_from_the_sessions_region_reaches_the_communication_channel_after_the_names_learn_it()
    {
        var (session, fake, _) = InWorld();
        string? nameAtRaise = null;
        var seen = new List<TransportEvent>();
        session.Communication += e => { seen.Add(e); nameAtRaise = session.Names.Get(Friend); };
        fake.Raise(Chat(Here, Friend, "Chatty Resident"));
        Assert.IsType<ChatReceived>(Assert.Single(seen));
        Assert.Equal("Chatty Resident", nameAtRaise);
    }

    [Fact]
    public void Chat_from_another_region_is_dropped()
    {
        var (session, fake, _) = InWorld();
        fake.ConnectedSimulatorCount = 2;
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(Chat(Elsewhere, Friend, "Far Resident"));
        Assert.Empty(seen);
    }

    [Fact]
    public void Instant_messages_and_name_replies_reach_the_channel_whatever_the_region()
    {
        var (session, fake, _) = InWorld();
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(Im(Friend, "Messaging Resident"));
        fake.Raise(new AgentNamesReceived(new Dictionary<Guid, string> { [Guid.NewGuid()] = "Replied" }));
        Assert.Equal(2, seen.Count);
        Assert.Equal("Messaging Resident", session.Names.Get(Friend));
    }

    [Fact]
    public void World_events_do_not_reach_the_communication_channel()
    {
        var (session, fake, _) = InWorld();
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(new ObjectsKilled(Here, [5]));
        Assert.Empty(seen);
    }

    [Fact]
    public void Stored_messages_are_asked_for_once_when_the_session_is_first_in_world()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, null));
        fake.ConnectedSimulatorCount = 1;
        fake.Raise(new SimConnected(Here, 256, 256));
        fake.CurrentSim = Here;
        fake.Raise(new EventQueueRunning(Here));
        Assert.Equal(0, fake.OfflineRequests);   // not yet in world
        fake.Raise(new MovementComplete(Here));
        Assert.Equal(1, fake.OfflineRequests);
        // a teleport landing later starts the region afresh but is not a new login
        fake.Raise(new SimConnected(Elsewhere, 256, 256));
        fake.CurrentSim = Elsewhere;
        fake.Raise(new EventQueueRunning(Elsewhere));
        fake.Raise(new MovementComplete(Elsewhere));
        Assert.Equal(1, fake.OfflineRequests);
    }

    [Fact]
    public void The_session_asks_the_grid_for_the_names_its_cache_queued()
    {
        var (session, fake, _) = InWorld();
        session.Names.Get(Friend);
        Assert.Equal(1, session.RequestQueuedNames());
        Assert.Equal([Friend], Assert.Single(fake.NameRequests));
    }

    [Fact]
    public void What_people_write_and_their_names_never_reach_the_log()
    {
        var (session, fake, log) = InWorld();
        session.Communication += _ => { };
        fake.Raise(Chat(Here, Friend, "Chatty Resident"));
        fake.Raise(Im(Friend, "Messaging Resident", offline: true));
        fake.Raise(new AgentNamesReceived(new Dictionary<Guid, string> { [Friend] = "Replied Resident" }));
        session.Dispose();
        Assert.DoesNotContain(SecretText, log.Text);
        Assert.DoesNotContain("Resident", log.Text);
        Assert.DoesNotContain(Friend.ToString(), log.Text);
    }

    // ---------------- captures ----------------

    [Fact]
    public void Communication_events_are_never_recorded_in_a_capture_and_world_events_still_are()
    {
        Assert.False(CaptureFile.IsRecorded(Chat(Here, Friend, "Chatty Resident")));
        Assert.False(CaptureFile.IsRecorded(Im(Friend, "Messaging Resident")));
        Assert.False(CaptureFile.IsRecorded(new AgentNamesReceived(new Dictionary<Guid, string>())));
        Assert.True(CaptureFile.IsRecorded(new ObjectsKilled(Here, [1])));
        Assert.True(CaptureFile.IsRecorded(new EventQueueRunning(Here)));
    }

    [Fact]
    public void A_recording_session_writes_no_chat_or_message_into_its_capture_file()
    {
        string path = TestTemp.NewFilePath("comms-capture", ".jsonl.gz");
        var fake = new FakeTransport();
        using (var writer = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "comms", Guid.Empty, 0, 0, "test")))
        {
            var rec = new RecordingTransport(fake, writer, new CaptureFile.Aliases(Guid.Empty));
            fake.Raise(new ObjectsKilled(Here, [1]));
            fake.Raise(Chat(Here, Friend, "Chatty Resident"));
            fake.Raise(Im(Friend, "Messaging Resident"));
            Assert.Equal(1, rec.Recorded);
        }
        var c = CaptureFile.Read(path);
        Assert.IsType<ObjectsKilled>(Assert.Single(c.Records).Event);
        TestTemp.Delete(path);
    }

    // ---------------- typing commands through the pipeline ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Local_typing_goes_through_the_pipeline_as_one_typing_command(bool started)
    {
        var (session, fake, _) = InWorld();
        Assert.Equal(ActionStatus.Sent, session.Actions.Submit(new ChatTypingIntent(started)).Status);
        Assert.Equal(new ChatTypingCommand(started), Assert.Single(fake.Commands));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Conversation_typing_goes_through_the_pipeline_to_its_agent(bool started)
    {
        var (session, fake, _) = InWorld();
        Assert.Equal(ActionStatus.Sent, session.Actions.Submit(new ImTypingIntent(Friend, started)).Status);
        Assert.Equal(new ImTypingCommand(Friend, started), Assert.Single(fake.Commands));
    }

    [Fact]
    public void Conversation_typing_with_no_agent_is_refused_and_nothing_is_sent()
    {
        var (session, fake, _) = InWorld();
        var r = session.Actions.Submit(new ImTypingIntent(Guid.Empty, true));
        Assert.Equal(ActionStatus.Invalid, r.Status);
        Assert.Contains(r.Issues!, i => i.Code == "im.no_target");
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public void A_restriction_on_typing_refuses_it()
    {
        var (session, fake, _) = InWorld();
        var restrictions = new RestrictionSet();
        restrictions.Restrict(ActionKind.ChatTyping);
        session.Actions.AddVeto(restrictions);
        Assert.Equal(ActionStatus.Vetoed, session.Actions.Submit(new ChatTypingIntent(true)).Status);
        Assert.Empty(fake.Commands);
    }

    // ---------------- the adapter's translation ----------------

    [Theory]
    [InlineData(ChatType.Whisper, ChatLineType.Whisper)]
    [InlineData(ChatType.Normal, ChatLineType.Say)]
    [InlineData(ChatType.Shout, ChatLineType.Shout)]
    [InlineData(ChatType.StartTyping, ChatLineType.StartTyping)]
    [InlineData(ChatType.StopTyping, ChatLineType.StopTyping)]
    [InlineData(ChatType.Debug, ChatLineType.Debug)]
    [InlineData(ChatType.OwnerSay, ChatLineType.OwnerSay)]
    [InlineData(ChatType.RegionSayTo, ChatLineType.RegionSayTo)]
    [InlineData(ChatType.RegionSay, ChatLineType.RegionSay)]
    [InlineData((ChatType)3, ChatLineType.Other)]
    public void Library_chat_types_map_to_the_cores(ChatType type, ChatLineType expected)
    {
        var e = new ChatEventArgs(null!, "hello", ChatAudibleLevel.Fully, type, ChatSourceType.Agent, "Chatty Resident", new UUID(Friend), new UUID(Friend), new LibreMetaverse.Vector3(1, 2, 3));
        Assert.Equal(expected, LmvTransport.ToChat(Here, e).Type);
    }

    [Fact]
    public void A_library_chat_event_keeps_its_speaker_kind_hearing_position_and_text_as_a_message_body()
    {
        var owner = Guid.NewGuid();
        var e = new ChatEventArgs(null!, SecretText, ChatAudibleLevel.Barely, ChatType.Normal, ChatSourceType.Object, "A Box", new UUID(Friend), new UUID(owner), new LibreMetaverse.Vector3(1, 2, 3));
        var c = LmvTransport.ToChat(Here, e);
        Assert.Same(Here, c.Sim);
        Assert.Equal(Friend, c.SourceId);
        Assert.Equal(owner, c.OwnerId);
        Assert.Equal("A Box", c.FromName);
        Assert.Equal(ChatSource.Object, c.Source);
        Assert.Equal(ChatAudible.Barely, c.Audible);
        Assert.Equal(new Vector3(1, 2, 3), c.Position);
        Assert.Equal(SecretText, c.Text.Reveal());
        Assert.Equal(MessageChannel.LocalChat, c.Text.Channel);
        Assert.DoesNotContain(SecretText, c.ToString());   // a record's printout shows the body redacted
    }

    [Theory]
    [InlineData(ChatAudibleLevel.Not, ChatAudible.Not)]
    [InlineData(ChatAudibleLevel.Fully, ChatAudible.Fully)]
    [InlineData((ChatAudibleLevel)5, ChatAudible.Not)]
    public void Hearing_levels_map_and_an_unknown_one_counts_as_not_heard(ChatAudibleLevel level, ChatAudible expected)
    {
        var e = new ChatEventArgs(null!, "x", level, ChatType.Normal, ChatSourceType.System, "", UUID.Zero, UUID.Zero, LibreMetaverse.Vector3.Zero);
        Assert.Equal(expected, LmvTransport.ToChat(null, e).Audible);
    }

    private static InstantMessage LibIm(InstantMessageDialog dialog, InstantMessageOnline offline = InstantMessageOnline.Online, DateTime timestamp = default, bool group = false) => new()
    {
        FromAgentID = new UUID(Friend),
        FromAgentName = "Messaging Resident",
        ToAgentID = new UUID(Self),
        IMSessionID = new UUID(ImSessionId.For(Self, Friend)),
        Dialog = dialog,
        Offline = offline,
        Timestamp = timestamp,
        GroupIM = group,
        Message = SecretText,
        BinaryBucket = [],
    };

    [Theory]
    [InlineData(InstantMessageDialog.MessageFromAgent, ImKind.Message)]
    [InlineData(InstantMessageDialog.StartTyping, ImKind.StartTyping)]
    [InlineData(InstantMessageDialog.StopTyping, ImKind.StopTyping)]
    [InlineData(InstantMessageDialog.MessageBox, ImKind.MessageBox)]
    [InlineData(InstantMessageDialog.FriendshipOffered, ImKind.Other)]
    [InlineData(InstantMessageDialog.SessionSend, ImKind.Other)]
    public void Library_message_dialogs_map_to_the_kinds_this_viewer_handles(InstantMessageDialog dialog, ImKind expected)
    {
        var m = LmvTransport.ToInstantMessage(LibIm(dialog));
        Assert.Equal(expected, m.Kind);
        Assert.Equal((int)dialog, m.Dialog);
    }

    [Fact]
    public void A_library_instant_message_keeps_sender_session_text_and_group_flag()
    {
        var m = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.MessageFromAgent, group: true));
        Assert.Equal(Friend, m.FromAgent);
        Assert.Equal("Messaging Resident", m.FromName);
        Assert.Equal(Self, m.ToAgent);
        Assert.Equal(ImSessionId.For(Self, Friend), m.SessionId);
        Assert.True(m.Group);
        Assert.False(m.Offline);
        Assert.Equal(SecretText, m.Text.Reveal());
        Assert.Equal(MessageChannel.InstantMessage, m.Text.Channel);
    }

    [Fact]
    public void A_stored_message_is_marked_offline_with_its_time_when_the_time_is_believable()
    {
        var sent = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var m = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.MessageFromAgent, InstantMessageOnline.Offline, sent));
        Assert.True(m.Offline);
        Assert.Equal(new DateTimeOffset(sent), m.SentAt);
    }

    [Fact]
    public void A_stored_message_with_an_unbelievable_time_has_no_time_rather_than_a_wrong_one()
    {
        // the library's capability path builds the time as ticks from a seconds count: a date in year 1
        var m = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.MessageFromAgent, InstantMessageOnline.Offline, new DateTime(1_700_000_000L)));
        Assert.True(m.Offline);
        Assert.Null(m.SentAt);
    }

    [Fact]
    public void A_live_message_carries_no_stored_time()
    {
        var m = LmvTransport.ToInstantMessage(LibIm(InstantMessageDialog.MessageFromAgent, InstantMessageOnline.Online, DateTime.UtcNow));
        Assert.Null(m.SentAt);
    }

    [Theory]
    [MemberData(nameof(TypingCommands))]
    public void The_adapter_knows_the_typing_commands_and_without_a_circuit_refuses_them_for_that_reason(TransportCommand command)
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(command));
    }

    public static TheoryData<TransportCommand> TypingCommands => new()
    {
        new ChatTypingCommand(true), new ChatTypingCommand(false), new ImTypingCommand(Friend, true), new ImTypingCommand(Friend, false),
    };

    [Fact]
    public void The_adapter_can_ask_for_names_and_stored_messages_and_does_nothing_without_a_circuit()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        Assert.IsAssignableFrom<IAgentNameRequester>(t);
        Assert.IsAssignableFrom<IOfflineMessageRequester>(t);
        ((IAgentNameRequester)(object)t).RequestAgentNames([Friend]);
        ((IOfflineMessageRequester)(object)t).RequestOfflineMessages();
    }
}

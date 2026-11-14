// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The block list below the interface: the list's own rules, the intents' checks, what the session asks the grid for and sends it,
/// and the drop rule for every channel this viewer has. No grid: a fake transport and synthetic events (no grid mute entry is ever
/// added or removed by a test). Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class MuteCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Pest = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Other = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000ee");
    private static readonly Guid Thing = Guid.Parse("0b0b0b0b-0000-4000-8000-000000000001");
    private static readonly Guid Owner = Guid.Parse("0c0c0c0c-0000-4000-8000-000000000002");
    private static readonly Guid Group = Guid.Parse("6a6a6a6a-0000-4000-8000-0000000000a1");
    private static readonly Guid Offer = Guid.Parse("0ffe0ffe-0000-4000-8000-000000000001");
    private const string PestName = "Pest Resident";

    private static MessageBody Text(string t) => new(t, MessageChannel.LocalChat);
    private static MessageBody Im(string t) => new(t, MessageChannel.InstantMessage);

    // ---------------- the list's own rules ----------------

    [Fact]
    public void A_resident_an_object_and_an_object_name_can_be_added_and_each_blocks_by_its_own_key()
    {
        var list = new MuteList(Self);
        Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName)));
        Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.Object, Thing, "Noisy Box")));
        Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, "Spam Bot")));
        Assert.Equal(3, list.Count);
        Assert.True(list.IsMuted(Pest, null));
        Assert.True(list.IsMuted(Thing, "any other name"));
        Assert.True(list.IsMuted(Guid.NewGuid(), "Spam Bot"));
        Assert.False(list.IsMuted(Other, "Someone Else"));
        Assert.False(list.IsMuted(Guid.NewGuid(), "spam bot"));   // names match exactly
    }

    [Fact]
    public void A_name_blocks_only_where_names_count_never_an_avatar_known_by_id()
    {
        var list = new MuteList(Self);
        list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, PestName));
        Assert.True(list.IsMuted(Pest, PestName, nameCounts: true));
        Assert.False(list.IsMuted(Pest, PestName, nameCounts: false));
        Assert.False(list.IsMuted(Pest, "", nameCounts: true));   // an empty name is never matched
    }

    [Fact]
    public void An_entry_that_allows_text_chat_does_not_block_text_but_blocks_other_properties()
    {
        var list = new MuteList(Self);
        list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName, MuteFlag.TextChat));
        Assert.False(list.IsMuted(Pest, PestName, MuteFlag.TextChat));
        Assert.True(list.IsMuted(Pest, PestName, MuteFlag.ObjectSounds));
        Assert.True(list.IsMuted(Pest, PestName));   // asked with no property: any block counts
    }

    [Fact]
    public void Adding_again_is_a_no_op_and_a_changed_flag_is_a_change()
    {
        var list = new MuteList(Self);
        list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName));
        int changes = 0;
        list.Changed += () => changes++;
        Assert.Equal(MuteAddResult.Duplicate, list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName)));
        Assert.Equal(0, changes);
        Assert.Equal(MuteAddResult.Changed, list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName, MuteFlag.Particles)));
        Assert.Equal(1, changes);
        Assert.Equal(MuteFlag.Particles, list.Find(Pest)!.Flags);
        Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, "X")));
        Assert.Equal(MuteAddResult.Duplicate, list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, "X")));
    }

    [Theory]
    [InlineData("Linden Resident", false)]          // not a Linden: the last name is Resident
    [InlineData("Test Linden", true)]
    [InlineData("test.linden", true)]
    [InlineData("Test LINDEN", true)]
    [InlineData("Linden", false)]
    [InlineData("", false)]
    public void A_Linden_is_recognised_by_the_last_name_in_either_form(string name, bool linden) =>
        Assert.Equal(linden, MuteList.IsLinden(name));

    [Fact]
    public void Refusals_a_Linden_yourself_an_empty_name_a_name_with_an_id_an_id_less_entry_external_and_a_full_list()
    {
        var list = new MuteList(Self);
        Assert.Equal(MuteAddResult.RefusedLinden, list.Add(new MuteEntry(MuteKind.Resident, Pest, "Test Linden")));
        Assert.Equal(MuteAddResult.RefusedSelf, list.Add(new MuteEntry(MuteKind.Resident, Self, "My Self")));
        Assert.Equal(MuteAddResult.RefusedInvalid, list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, "")));
        Assert.Equal(MuteAddResult.RefusedInvalid, list.Add(new MuteEntry(MuteKind.ByName, Pest, "Named")));
        Assert.Equal(MuteAddResult.RefusedInvalid, list.Add(new MuteEntry(MuteKind.Object, Guid.Empty, "No Id")));
        Assert.Equal(MuteAddResult.RefusedInvalid, list.Add(new MuteEntry(MuteKind.External, Pest, "Outside")));
        Assert.Equal(0, list.Count);
        for (int i = 0; i < MuteList.Limit; i++)
            Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.Object, new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1), "Box " + i)));
        Assert.Equal(MuteAddResult.RefusedLimit, list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName)));
        Assert.Equal(MuteList.Limit, list.Count);
    }

    [Fact]
    public void A_Linden_whose_text_chat_is_allowed_may_still_be_listed_for_other_properties()
    {
        var list = new MuteList(Self);
        Assert.Equal(MuteAddResult.Added, list.Add(new MuteEntry(MuteKind.Resident, Pest, "Test Linden", MuteFlag.TextChat)));
    }

    [Fact]
    public void Removing_finds_an_entry_by_id_or_a_by_name_entry_by_name_and_reports_nothing_found()
    {
        var list = new MuteList(Self);
        list.Add(new MuteEntry(MuteKind.Resident, Pest, PestName));
        list.Add(new MuteEntry(MuteKind.ByName, Guid.Empty, "Spam Bot"));
        Assert.False(list.Remove(Other, "Nobody"));
        Assert.True(list.Remove(Pest, PestName));
        Assert.False(list.IsMuted(Pest, PestName));
        Assert.True(list.Remove(Guid.Empty, "Spam Bot"));
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void The_grids_list_replaces_the_local_one_and_a_null_id_or_by_name_kind_is_a_by_name_entry()
    {
        var list = new MuteList(Self);
        list.Add(new MuteEntry(MuteKind.Resident, Other, "Local Resident"));
        list.Load([new MuteEntry(MuteKind.Resident, Pest, PestName), new MuteEntry(MuteKind.Object, Guid.Empty, "Old Style"), new MuteEntry(MuteKind.ByName, Guid.NewGuid(), "Named"), new MuteEntry(MuteKind.ByName, Guid.Empty, "")]);
        Assert.Equal(MuteListState.Loaded, list.State);
        Assert.False(list.IsMuted(Other, null));
        Assert.True(list.IsMuted(Pest, null));
        Assert.True(list.IsMuted(Guid.NewGuid(), "Old Style"));
        Assert.True(list.IsMuted(Guid.NewGuid(), "Named"));
        Assert.Equal(3, list.Count);   // the nameless one is dropped
        Assert.All(list.Entries.Where(e => e.Name is "Old Style" or "Named"), e => { Assert.Equal(MuteKind.ByName, e.Kind); Assert.Equal(Guid.Empty, e.Id); });
    }

    // ---------------- the intents ----------------

    public static TheoryData<ActionIntent, string> BadIntents => new()
    {
        { new MuteIntent(MuteKind.External, Pest, "x"), "mute.bad_kind" },
        { new MuteIntent((MuteKind)9, Pest, "x"), "mute.bad_kind" },
        { new MuteIntent(MuteKind.ByName, Pest, "x"), "mute.name_with_id" },
        { new MuteIntent(MuteKind.ByName, Guid.Empty, "  "), "mute.no_name" },
        { new MuteIntent(MuteKind.Resident, Guid.Empty, "x"), "mute.no_target" },
        { new MuteIntent(MuteKind.Object, Thing, new string('a', 255)), "mute.name_too_long" },
        { new MuteIntent(MuteKind.Object, Thing, "a|b"), "mute.bad_name" },
        { new MuteIntent(MuteKind.Object, Thing, "a\u0000b"), "mute.bad_name" },
        { new MuteIntent(MuteKind.Object, Thing, "box", 16), "mute.bad_flags" },
        { new UnmuteIntent(Guid.Empty, " "), "mute.no_target" },
    };

    [Theory]
    [MemberData(nameof(BadIntents))]
    public void A_bad_block_intent_names_what_is_wrong_and_sends_nothing(ActionIntent intent, string code)
    {
        Assert.Contains(intent.Validate(), i => i.Code == code);
        var fake = new FakeTransport();
        var pipeline = new ActionPipeline(() => fake, () => true, new CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Invalid, pipeline.Submit(intent).Status);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public void A_good_block_and_unblock_reach_the_command_side_with_their_text_trimmed()
    {
        var fake = new FakeTransport();
        var pipeline = new ActionPipeline(() => fake, () => true, new CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new MuteIntent(MuteKind.Object, Thing, "  Noisy Box ")).Status);
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new UnmuteIntent(Pest, " Pest Resident ")).Status);
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new MuteIntent(MuteKind.ByName, Guid.Empty, "Spam Bot", MuteFlag.Particles)).Status);
        Assert.Equal(
            new TransportCommand[]
            {
                new MuteAddCommand(new MuteEntry(MuteKind.Object, Thing, "Noisy Box", 0)),
                new MuteRemoveCommand(Pest, "Pest Resident"),
                new MuteAddCommand(new MuteEntry(MuteKind.ByName, Guid.Empty, "Spam Bot", MuteFlag.Particles)),
            }, fake.Commands);
    }

    // ---------------- the adapter ----------------

    [Fact]
    public void The_librarys_list_becomes_the_cores_records_with_the_protocols_numbers()
    {
        var lib = new[]
        {
            new LibreMetaverse.MuteEntry { Type = LibreMetaverse.MuteType.Resident, ID = new LibreMetaverse.UUID(Pest), Name = PestName, Flags = LibreMetaverse.MuteFlags.Default },
            new LibreMetaverse.MuteEntry { Type = LibreMetaverse.MuteType.ByName, ID = LibreMetaverse.UUID.Zero, Name = "Spam Bot", Flags = (LibreMetaverse.MuteFlags)3 },
        };
        var core = LmvTransport.ToMuteEntries(lib);
        Assert.Equal(new[] { new MuteEntry(MuteKind.Resident, Pest, PestName, 0), new MuteEntry(MuteKind.ByName, Guid.Empty, "Spam Bot", 3) }, core);
    }

    [Fact]
    public void Without_a_circuit_the_adapter_refuses_the_block_commands_and_the_request_sends_nothing()
    {
        using var t = new LmvTransport(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new ListSink(), "test"));
        Assert.Equal("not_connected", ((ICommandTransport)t).Execute(new MuteAddCommand(new MuteEntry(MuteKind.Object, Thing, "x"))).Reason);
        Assert.Equal("not_connected", ((ICommandTransport)t).Execute(new MuteRemoveCommand(Thing, "x")).Reason);
        t.RequestMuteList();   // no circuit: nothing is sent and nothing throws
    }

    // ---------------- the session ----------------

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var fake = new FakeTransport();
        var log = new ListSink();
        var opts = new SessionOptions { LoginUri = "http://grid.invalid/", MuteListDelay = delay ?? Task.Delay };
        var session = new ViewerSession(opts, fake, new CoreLog(log, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, log);
    }

    [Fact]
    public void The_list_is_asked_for_once_when_the_session_reaches_the_world_and_the_grids_answer_loads_it()
    {
        var (session, fake, _) = InWorld();
        Assert.Equal(1, fake.MuteRequests);
        Assert.Equal(MuteListState.Requested, session.Mutes.State);
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(new MuteListReceived([new MuteEntry(MuteKind.Resident, Pest, PestName)]));
        Assert.Equal(MuteListState.Loaded, session.Mutes.State);
        Assert.True(session.Mutes.IsMuted(Pest, null));
        Assert.Contains(seen, e => e is MuteListReceived);
        fake.Raise(new MovementComplete(Here));
        Assert.Equal(1, fake.MuteRequests);
    }

    [Fact]
    public void A_grid_that_never_answers_leaves_the_list_unavailable_after_the_wait_and_this_sessions_entries_still_work()
    {
        var manual = new ManualDelay();
        var (session, fake, _) = InWorld(manual.Delay);
        Assert.Equal(MuteListState.Requested, session.Mutes.State);
        Assert.Contains(ViewerSession.MuteListWait, manual.Requested);
        manual.ElapseLast();
        SpinWait.SpinUntil(() => session.Mutes.State == MuteListState.Unavailable, TimeSpan.FromSeconds(60));
        Assert.Equal(MuteListState.Unavailable, session.Mutes.State);
        Assert.True(session.Block(MuteKind.Resident, Pest, PestName).Done);
        fake.Raise(new ChatReceived(Here, Pest, Pest, PestName, ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, Text("hi")));
        Assert.Equal(1, session.MutedDrops);
    }

    [Fact]
    public void An_answer_after_the_wait_still_loads_the_list()
    {
        var manual = new ManualDelay();
        var (session, fake, _) = InWorld(manual.Delay);
        manual.ElapseLast();
        SpinWait.SpinUntil(() => session.Mutes.State == MuteListState.Unavailable, TimeSpan.FromSeconds(60));
        fake.Raise(new MuteListReceived([new MuteEntry(MuteKind.Object, Thing, "Box")]));
        Assert.Equal(MuteListState.Loaded, session.Mutes.State);
        Assert.True(session.Mutes.IsMuted(Thing, null));
    }

    [Fact]
    public void Block_sends_the_add_through_the_pipeline_and_then_holds_the_entry_and_a_refusal_sends_nothing()
    {
        var (session, fake, _) = InWorld();
        var done = session.Block(MuteKind.Resident, Pest, "  " + PestName + " ");
        Assert.True(done.Done);
        Assert.Equal(new MuteAddCommand(new MuteEntry(MuteKind.Resident, Pest, PestName, 0)), Assert.Single(fake.Commands));
        Assert.True(session.Mutes.IsMuted(Pest, null));

        Assert.Equal(MuteAddResult.Duplicate, session.Block(MuteKind.Resident, Pest, PestName).Result);
        Assert.Equal(MuteAddResult.RefusedSelf, session.Block(MuteKind.Resident, Self, "Test User").Result);
        Assert.Equal(MuteAddResult.RefusedLinden, session.Block(MuteKind.Resident, Other, "Test Linden").Result);
        Assert.Single(fake.Commands);
    }

    [Fact]
    public void A_pipeline_refusal_leaves_the_list_as_it_was()
    {
        var (session, fake, _) = InWorld();
        fake.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        var outcome = session.Block(MuteKind.Object, Thing, "Box");
        Assert.False(outcome.Done);
        Assert.False(session.Mutes.IsMuted(Thing, null));
    }

    [Fact]
    public void Unblock_sends_the_remove_and_then_drops_the_entry_and_an_unknown_one_sends_nothing()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Object, Thing, "Noisy Box");
        session.Block(MuteKind.ByName, Guid.Empty, "Spam Bot");
        fake.Commands.Clear();
        Assert.False(session.Unblock(Other, "Nobody"));
        Assert.Empty(fake.Commands);
        Assert.True(session.Unblock(Thing, "Noisy Box"));
        Assert.True(session.Unblock(Guid.Empty, "Spam Bot"));
        Assert.Equal(new TransportCommand[] { new MuteRemoveCommand(Thing, "Noisy Box"), new MuteRemoveCommand(Guid.Empty, "Spam Bot") }, fake.Commands);
        Assert.Equal(0, session.Mutes.Count);
    }

    // ---------------- what a block drops, per channel ----------------

    private static ChatReceived AgentChat(Guid id, string name) =>
        new(Here, id, id, name, ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, Text("hello there"));

    private static ChatReceived ObjectChat(Guid id, string name, Guid owner) =>
        new(Here, id, owner, name, ChatSource.Object, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, Text("object line"));

    private static List<TransportEvent> Listen(ViewerSession session)
    {
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        return seen;
    }

    [Fact]
    public void Local_chat_from_a_blocked_resident_is_dropped_and_from_anyone_else_passes()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName);
        var seen = Listen(session);
        fake.Raise(AgentChat(Pest, PestName));
        fake.Raise(AgentChat(Other, "Fine Resident"));
        var only = Assert.IsType<ChatReceived>(Assert.Single(seen));
        Assert.Equal(Other, only.SourceId);
        Assert.Equal(1, session.MutedDrops);
    }

    [Fact]
    public void Local_chat_from_an_object_is_dropped_by_its_id_its_name_or_its_owner()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Object, Thing, "Noisy Box");
        session.Block(MuteKind.ByName, Guid.Empty, "Spam Bot");
        session.Block(MuteKind.Resident, Pest, PestName);
        var seen = Listen(session);
        fake.Raise(ObjectChat(Thing, "renamed", Other));                       // by id
        fake.Raise(ObjectChat(Guid.NewGuid(), "Spam Bot", Other));             // by name
        fake.Raise(ObjectChat(Guid.NewGuid(), "Some Pet", Pest));              // by owner
        fake.Raise(ObjectChat(Guid.NewGuid(), "Quiet Box", Other));            // passes
        Assert.Equal("Quiet Box", Assert.IsType<ChatReceived>(Assert.Single(seen)).FromName);
        Assert.Equal(3, session.MutedDrops);
    }

    [Fact]
    public void A_name_entry_does_not_block_an_avatar_in_view_but_blocks_one_the_region_has_not_told_us_about()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.ByName, Guid.Empty, PestName);
        var seen = Listen(session);
        fake.Raise(AgentChat(Pest, PestName));                       // not in view: the name counts
        session.World.ApplyAvatar(Here, new Viewer.Core.World.AvatarData(7, Pest, PestName, new Vector3(10, 10, 20), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.8f)));
        fake.Raise(AgentChat(Pest, PestName));                       // in view: known by id, a name entry never blocks it
        Assert.Single(seen);
        Assert.Equal(1, session.MutedDrops);
    }

    [Fact]
    public void Region_and_system_chat_is_never_blocked_and_nor_is_a_Linden()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.ByName, Guid.Empty, "Region");
        session.Block(MuteKind.Resident, Pest, "Pest Resident");
        var seen = Listen(session);
        fake.Raise(new ChatReceived(Here, Guid.Empty, Guid.Empty, "Region", ChatSource.System, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, Text("restart soon")));
        fake.Raise(AgentChat(Pest, "Test Linden"));   // a Linden's text cannot be blocked, whatever the id
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, session.MutedDrops);
    }

    [Fact]
    public void Instant_messages_and_their_typing_marks_from_a_blocked_resident_are_dropped_a_group_message_by_group_or_sender()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName);
        session.Block(MuteKind.Group, Group, "A Group");
        var seen = Listen(session);
        InstantMessageReceived M(Guid from, ImKind kind, Guid sessionId, bool group) =>
            new(from, "Someone", Self, sessionId, kind, 0, false, null, group, Im("a private line"));
        fake.Raise(M(Pest, ImKind.Message, Pest, false));
        fake.Raise(M(Pest, ImKind.StartTyping, Pest, false));
        fake.Raise(M(Pest, ImKind.StopTyping, Pest, false));
        fake.Raise(M(Other, ImKind.GroupMessage, Group, true));      // a blocked group's
        fake.Raise(M(Pest, ImKind.GroupMessage, Guid.NewGuid(), true));   // a blocked member's, in another group
        fake.Raise(M(Other, ImKind.Message, Other, false));          // passes
        Assert.Equal(Other, Assert.IsType<InstantMessageReceived>(Assert.Single(seen)).FromAgent);
        Assert.Equal(5, session.MutedDrops);
    }

    [Fact]
    public void A_friendship_offer_from_a_blocked_resident_is_declined_through_the_pipeline_and_never_shown()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName);
        fake.Commands.Clear();
        var seen = Listen(session);
        fake.Raise(new FriendshipOffered(Pest, PestName, Offer, true, Im("be my friend")));
        Assert.Empty(seen);
        Assert.Equal(new FriendshipAnswerCommand(Pest, Offer, false, false), Assert.Single(fake.Commands));
        fake.Raise(new FriendshipOffered(Pest, PestName, Offer, false, Im("stored")));   // stored while away: the capability answers
        Assert.Equal(new FriendshipAnswerCommand(Pest, Offer, false, true), fake.Commands[^1]);
        fake.Raise(new FriendshipOffered(Other, "Fine Resident", Offer, true, Im("hello")));
        Assert.IsType<FriendshipOffered>(Assert.Single(seen));
        Assert.Equal(2, fake.Commands.Count);
    }

    [Fact]
    public void Teleport_offers_and_requests_from_a_blocked_resident_are_dropped_with_no_answer()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName);
        fake.Commands.Clear();
        var seen = Listen(session);
        fake.Raise(new TeleportOffered(Pest, PestName, Offer, Im("come")));
        fake.Raise(new TeleportRequested(Pest, PestName, Im("offer me")));
        fake.Raise(new TeleportOffered(Other, "Fine Resident", Offer, Im("come")));
        fake.Raise(new TeleportRequested(Other, "Fine Resident", Im("offer me")));
        Assert.Equal(2, seen.Count);
        Assert.All(seen, e => Assert.Equal(Other, e is TeleportOffered o ? o.From : ((TeleportRequested)e).From));
        Assert.Empty(fake.Commands);
        Assert.Equal(2, session.MutedDrops);
    }

    [Fact]
    public void Group_notices_from_a_blocked_sender_or_a_blocked_group_are_dropped()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName);
        var otherGroup = Guid.NewGuid();
        session.Block(MuteKind.Group, otherGroup, "Noisy Group");
        var seen = Listen(session);
        GroupNoticeReceived N(Guid group, Guid sender) =>
            new(group, sender, "Sender", Im("subject"), Im("body"), false, "", 0, Offer, null, false);
        fake.Raise(N(Group, Pest));          // blocked sender
        fake.Raise(N(otherGroup, Other));    // blocked group
        fake.Raise(N(Group, Other));         // passes
        Assert.Equal(Other, Assert.IsType<GroupNoticeReceived>(Assert.Single(seen)).Sender);
        Assert.Equal(2, session.MutedDrops);
    }

    [Fact]
    public void An_entry_that_allows_text_chat_drops_none_of_them()
    {
        var (session, fake, _) = InWorld();
        session.Block(MuteKind.Resident, Pest, PestName, MuteFlag.TextChat);
        var seen = Listen(session);
        fake.Raise(AgentChat(Pest, PestName));
        fake.Raise(new InstantMessageReceived(Pest, PestName, Self, Pest, ImKind.Message, 0, false, null, false, Im("hi")));
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, session.MutedDrops);
    }

    [Fact]
    public void Nothing_is_dropped_before_anything_is_blocked_and_the_drop_log_names_no_one()
    {
        var (session, fake, log) = InWorld();
        var seen = Listen(session);
        fake.Raise(AgentChat(Pest, PestName));
        Assert.Single(seen);
        session.Block(MuteKind.Resident, Pest, PestName);
        fake.Raise(AgentChat(Pest, PestName));
        fake.Raise(new InstantMessageReceived(Pest, PestName, Self, Pest, ImKind.Message, 0, false, null, false, Im("a secret line 9d31")));
        Assert.Contains("mute.dropped", log.Text);
        Assert.DoesNotContain(PestName, log.Text);
        Assert.DoesNotContain("a secret line 9d31", log.Text);
        Assert.DoesNotContain("hello there", log.Text);
    }
}

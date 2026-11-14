// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Teleport, offers, map reads and coarse locations below the interface: the intents' checks, what each becomes on the command
/// side, the adapter's translation of the library's messages, the session's handling of a new region and the rule that people's
/// positions and offers reach neither a log nor a capture. No grid: a fake transport and synthetic events. Every member builds its
/// own objects, so the class runs in parallel.
/// </summary>
public class TeleportCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Lure = Guid.Parse("10101010-0000-4000-8000-000000000001");
    private const string SecretText = "a private line nobody may log 7f3c";
    private const string SecretName = "Secretive Resident";

    private sealed class Rig
    {
        public FakeTransport Transport = new();
        public ActionPipeline Pipeline;
        public Rig() => Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
    }

    // ---------------- intents ----------------

    public static TheoryData<ActionIntent, string> BadIntents => new()
    {
        { new TeleportHandleIntent(0, new Vector3(1, 1, 1)), "teleport.no_region" },
        { new TeleportHandleIntent(Here.Handle, new Vector3(-1, 1, 1)), "teleport.bad_position" },
        { new TeleportHandleIntent(Here.Handle, new Vector3(1, 5000, 1)), "teleport.bad_position" },
        { new TeleportHandleIntent(Here.Handle, new Vector3(1, 1, float.NaN)), "teleport.bad_position" },
        { new TeleportLureAnswerIntent(Guid.Empty, Lure, true), "teleport.no_target" },
        { new TeleportLureAnswerIntent(Friend, Guid.Empty, true), "teleport.no_lure" },
        { new TeleportOfferIntent(Guid.Empty, "come"), "teleport.no_target" },
        { new TeleportOfferIntent(Friend, new string('a', 1024)), "teleport.message_too_long" },
        { new TeleportOfferIntent(Friend, "a\u0000b"), "teleport.message_control_chars" },
    };

    [Theory]
    [MemberData(nameof(BadIntents))]
    public void A_bad_teleport_intent_is_refused_with_its_reason_and_nothing_is_sent(ActionIntent intent, string code)
    {
        Assert.Contains(intent.Validate(), i => i.Code == code);
        var r = new Rig();
        var result = r.Pipeline.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void Good_teleport_intents_pass_their_checks_including_the_limits_and_an_empty_offer_message()
    {
        ActionIntent[] good =
        [
            new TeleportHandleIntent(Here.Handle, new Vector3(0, 0, 0)),
            new TeleportHandleIntent(Here.Handle, new Vector3(4096, 4096, 4096)),
            new TeleportCancelIntent(),
            new TeleportLureAnswerIntent(Friend, Lure, true),
            new TeleportLureAnswerIntent(Friend, Lure, false),
            new TeleportOfferIntent(Friend, ""),
            new TeleportOfferIntent(Friend, new string('a', 1023)),
        ];
        foreach (var intent in good)
            Assert.Empty(intent.Validate());
    }

    [Fact]
    public void Each_teleport_intent_becomes_its_command_with_its_text_normalised()
    {
        var r = new Rig();
        var cases = new (ActionIntent Intent, TransportCommand Command)[]
        {
            (new TeleportHandleIntent(Here.Handle, new Vector3(10, 20, 30)), new TeleportHandleCommand(Here.Handle, new Vector3(10, 20, 30))),
            (new TeleportCancelIntent(), new TeleportCancelCommand()),
            (new TeleportLureAnswerIntent(Friend, Lure, true), new TeleportLureAnswerCommand(Friend, Lure, true)),
            (new TeleportLureAnswerIntent(Friend, Lure, false), new TeleportLureAnswerCommand(Friend, Lure, false)),
            (new TeleportOfferIntent(Friend, "  come over  "), new TeleportOfferCommand(Friend, "come over")),
        };
        foreach (var (intent, command) in cases)
        {
            Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(intent).Status);
            Assert.Equal(command, r.Transport.Commands[^1]);
        }
        Assert.Equal(cases.Length, r.Transport.Commands.Count);
    }

    [Fact]
    public void A_restriction_on_teleport_refuses_every_way_of_starting_one_and_nothing_is_sent()
    {
        var r = new Rig();
        var restrictions = new RestrictionSet();
        r.Pipeline.AddVeto(restrictions);
        foreach (var kind in new[] { ActionKind.TeleportRegion, ActionKind.TeleportHandle, ActionKind.TeleportLandmark, ActionKind.TeleportHome, ActionKind.TeleportLureAnswer })
            restrictions.Restrict(kind);
        ActionIntent[] starts =
        [
            new TeleportRegionIntent("Example Region", new Vector3(1, 1, 1)), new TeleportHandleIntent(Here.Handle, new Vector3(1, 1, 1)),
            new TeleportLandmarkIntent(Lure), new TeleportHomeIntent(), new TeleportLureAnswerIntent(Friend, Lure, true),
        ];
        foreach (var intent in starts)
            Assert.Equal(ActionStatus.Vetoed, r.Pipeline.Submit(intent).Status);
        Assert.Empty(r.Transport.Commands);
    }

    // ---------------- the adapter without a circuit ----------------

    private static LmvTransport NewAdapter() => new(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new ListSink(), "test"));

    public static TheoryData<TransportCommand> NewCommands => new()
    {
        new TeleportHandleCommand(Here.Handle, new Vector3(1, 1, 1)),
        new TeleportCancelCommand(),
        new TeleportLureAnswerCommand(Friend, Lure, true),
        new TeleportLureAnswerCommand(Friend, Lure, false),
        new TeleportOfferCommand(Friend, "come"),
    };

    [Theory]
    [MemberData(nameof(NewCommands))]
    public void Without_a_circuit_every_new_teleport_command_is_refused_with_its_reason_and_nothing_throws(TransportCommand command)
    {
        using var t = NewAdapter();
        var answer = ((ICommandTransport)t).Execute(command);
        Assert.False(answer.Sent);
        Assert.Equal("not_connected", answer.Reason);
    }

    // ---------------- the adapter's translation ----------------

    [Fact]
    public void A_teleport_start_progress_failure_finish_and_cancel_become_their_phases()
    {
        var start = LmvTransport.ToTeleportProgress(new TeleportEventArgs("Teleport started", TeleportStatus.Start, TeleportFlags.ViaLocation));
        Assert.Equal(new TeleportProgressed(TeleportPhase.Started, "", true), start);
        var noCancel = LmvTransport.ToTeleportProgress(new TeleportEventArgs("Teleport started", TeleportStatus.Start, TeleportFlags.ViaGodlikeLure | TeleportFlags.DisableCancel));
        Assert.False(noCancel!.CanCancel);
        var progress = LmvTransport.ToTeleportProgress(new TeleportEventArgs("sending_dest", TeleportStatus.Progress, TeleportFlags.Default));
        Assert.Equal(new TeleportProgressed(TeleportPhase.Progress, "sending_dest", true), progress);
        var failed = LmvTransport.ToTeleportProgress(new TeleportEventArgs("no_host", TeleportStatus.Failed, TeleportFlags.Default));
        Assert.Equal(new TeleportProgressed(TeleportPhase.Failed, "no_host", true), failed);
        var finished = LmvTransport.ToTeleportProgress(new TeleportEventArgs("Teleport finished", TeleportStatus.Finished, TeleportFlags.FinishedViaNewSim));
        Assert.Equal(TeleportPhase.Finished, finished!.Phase);
        var local = LmvTransport.ToTeleportProgress(new TeleportEventArgs("Teleport finished", TeleportStatus.Finished, TeleportFlags.FinishedViaSameSim));
        Assert.Equal(TeleportPhase.Local, local!.Phase);
        var cancelled = LmvTransport.ToTeleportProgress(new TeleportEventArgs("Cancelled", TeleportStatus.Cancelled, TeleportFlags.Default));
        Assert.Equal(TeleportPhase.Cancelled, cancelled!.Phase);
        Assert.Null(LmvTransport.ToTeleportProgress(new TeleportEventArgs("", TeleportStatus.None, TeleportFlags.Default)));
    }

    private static InstantMessage LibIm(InstantMessageDialog dialog, Guid? from = null, string message = SecretText) => new()
    {
        FromAgentID = new UUID(from ?? Friend),
        FromAgentName = SecretName,
        ToAgentID = new UUID(Self),
        IMSessionID = new UUID(Lure),
        Dialog = dialog,
        Offline = InstantMessageOnline.Online,
        Message = message,
        BinaryBucket = [],
    };

    [Fact]
    public void A_lure_message_becomes_an_offer_with_its_conversation_id_and_nothing_else_is_an_offer()
    {
        var offer = LmvTransport.ToTeleportOffer(LibIm(InstantMessageDialog.RequestTeleport));
        Assert.NotNull(offer);
        offer = offer!;
        Assert.Equal(Friend, offer.From);
        Assert.Equal(SecretName, offer.FromName);
        Assert.Equal(Lure, offer.LureId);
        Assert.Equal(SecretText, offer.Message.Reveal());
        Assert.Null(LmvTransport.ToTeleportOffer(LibIm(InstantMessageDialog.RequestTeleport, Guid.Empty)));   // an offer from nobody
        foreach (var dialog in new[] { InstantMessageDialog.MessageFromAgent, InstantMessageDialog.FriendshipOffered, InstantMessageDialog.GroupNotice, InstantMessageDialog.StartTyping })
            Assert.Null(LmvTransport.ToTeleportOffer(LibIm(dialog)));
    }

    [Fact]
    public void Coarse_locations_keep_every_avatar_with_its_position()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var e = LmvTransport.ToCoarse(Here, new Dictionary<UUID, LibreMetaverse.Vector3>
        {
            [new UUID(a)] = new(10, 20, 30), [new UUID(b)] = new(200, 100, 1020),
        });
        Assert.Same(Here, e.Sim);
        Assert.Equal(2, e.Avatars.Count);
        Assert.Contains(e.Avatars, x => x.Id == a && x.Position == new Vector3(10, 20, 30));
        Assert.Contains(e.Avatars, x => x.Id == b && x.Position == new Vector3(200, 100, 1020));
        Assert.Empty(LmvTransport.ToCoarse(Here, new Dictionary<UUID, LibreMetaverse.Vector3>()).Avatars);
    }

    [Fact]
    public void A_friend_location_answer_keeps_the_friend_the_region_handle_and_the_position()
    {
        var e = LmvTransport.ToFriendLocated(new UUID(Friend), Here.Handle, new LibreMetaverse.Vector3(12, 34, 0));
        Assert.Equal(new FriendLocated(Friend, Here.Handle, new Vector3(12, 34, 0)), e);
    }

    [Fact]
    public void A_map_block_reply_becomes_one_record_per_block_with_its_size_picture_and_access()
    {
        var picture = Guid.NewGuid();
        var p = new MapBlockReplyPacket
        {
            Data =
            [
                new MapBlockReplyPacket.DataBlock { X = 1000, Y = 1001, Name = Utils.StringToBytes("Example Region"), Access = 13, MapImageID = new UUID(picture) },
                new MapBlockReplyPacket.DataBlock { X = 1001, Y = 1001, Name = Utils.StringToBytes(""), Access = 255, MapImageID = UUID.Zero },
            ],
            Size =
            [
                new MapBlockReplyPacket.SizeBlock { SizeX = 512, SizeY = 512 },
                new MapBlockReplyPacket.SizeBlock { SizeX = 0, SizeY = 0 },
            ],
        };
        var blocks = LmvTransport.ToMapBlocks(p);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new MapBlockSize(1000, 1001, 512, 512, "Example Region", picture, 13), blocks[0]);
        Assert.Equal(255, blocks[1].Access);
        Assert.Equal("", blocks[1].Name);
        // a reply with no size block (a legacy grid) keeps the blocks with size 0
        var legacy = new MapBlockReplyPacket { Data = [new MapBlockReplyPacket.DataBlock { X = 5, Y = 6, Name = Utils.StringToBytes("Old"), Access = 13 }], Size = null! };
        Assert.Equal(new MapBlockSize(5, 6, 0, 0, "Old", Guid.Empty, 13), Assert.Single(LmvTransport.ToMapBlocks(legacy)));
    }

    [Fact]
    public void A_teleport_failure_message_keeps_the_reason_or_the_alert_key_and_the_region_the_alert_names()
    {
        var plain = new TeleportFailedPacket { Info = { Reason = Utils.StringToBytes("no_host") }, AlertInfo = [] };
        Assert.Equal(new TeleportProgressed(TeleportPhase.Failed, "no_host", true), LmvTransport.ToTeleportFailed(plain));
        var alert = new TeleportFailedPacket
        {
            Info = { Reason = Utils.StringToBytes("a sentence the grid wrote") },
            AlertInfo = [new TeleportFailedPacket.AlertInfoBlock { Message = Utils.StringToBytes("RegionTPSpecialUsageBlocked"), ExtraParams = Utils.StringToBytes("<llsd><map><key>REGION_NAME</key><string>Gaming Region</string></map></llsd>") }],
        };
        Assert.Equal(new TeleportProgressed(TeleportPhase.Failed, "RegionTPSpecialUsageBlocked", true, "Gaming Region"), LmvTransport.ToTeleportFailed(alert));
        var broken = new TeleportFailedPacket
        {
            Info = { Reason = Utils.StringToBytes("x") },
            AlertInfo = [new TeleportFailedPacket.AlertInfoBlock { Message = Utils.StringToBytes("noaccess_tport"), ExtraParams = Utils.StringToBytes("not llsd at all") }],
        };
        Assert.Equal(new TeleportProgressed(TeleportPhase.Failed, "noaccess_tport", true), LmvTransport.ToTeleportFailed(broken));   // an unreadable extra block loses only the region name
        Assert.Equal("", LmvTransport.ToTeleportFailed(new TeleportFailedPacket { Info = { Reason = [] }, AlertInfo = [] }).Message);
    }

    [Fact]
    public void A_simulator_the_library_switches_back_to_needs_a_new_arrival_but_a_first_visit_does_not()
    {
        // the library raises its connect event only when it first opens a circuit; a teleport back to a simulator it still holds only switches
        var arrivals = new SimArrivals<object>();
        var first = new object();
        var second = new object();
        Assert.False(arrivals.IsReArrival(first));      // the login or a first visit: the connect event follows by itself
        arrivals.Announced(first);
        Assert.True(arrivals.IsReArrival(first));       // switching back to it: nothing will follow
        Assert.False(arrivals.IsReArrival(second));
        arrivals.Announced(second);
        Assert.True(arrivals.IsReArrival(second));
        Assert.True(arrivals.IsReArrival(first));
        Assert.False(arrivals.IsReArrival(null));
        arrivals.Forget(first);                          // dropped by the library: a later circuit is a new simulator
        Assert.False(arrivals.IsReArrival(first));
        arrivals.Clear();
        Assert.False(arrivals.IsReArrival(second));
    }

    [Fact]
    public void Returning_to_a_region_already_visited_starts_it_afresh_like_any_other_arrival()
    {
        var (session, fake, _) = InWorld();
        var agent = Guid.NewGuid();
        fake.Raise(new ObjectUpdated(Here, new PrimData(5, Guid.NewGuid(), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One), false, null));
        fake.Raise(new SimConnected(There, 256, 256));
        fake.CurrentSim = There;
        fake.Raise(new ObjectUpdated(There, new PrimData(6, Guid.NewGuid(), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One), false, null));
        int epoch = session.World.Epoch;
        fake.Raise(new SimConnected(Here, 256, 256));    // the adapter reports the switch back as an arrival again
        fake.CurrentSim = Here;
        Assert.Equal(epoch + 1, session.World.Epoch);
        Assert.Empty(session.World.Snapshot().Objects);
        Assert.Equal("Example Region", session.Region!.Name);
        fake.Raise(new ObjectUpdated(Here, new PrimData(7, Guid.NewGuid(), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One), false, null));
        Assert.Single(session.World.Snapshot().Objects);
        fake.Raise(new ObjectUpdated(There, new PrimData(8, Guid.NewGuid(), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One), false, null));
        Assert.Single(session.World.Snapshot().Objects);  // what the region left still sends is dropped
    }

    [Fact]
    public async Task A_wait_for_a_region_name_is_answered_by_the_block_that_names_it_exactly_whatever_the_case()
    {
        var waits = new RegionNameWaits();
        var wait = waits.WaitAsync("far region", TimeSpan.FromSeconds(30), CancellationToken.None);
        waits.Offer([new MapBlockSize(5, 6, 256, 256, "Another Region", null, 13), new MapBlockSize(7, 8, 0, 0, "", null, 255), new MapBlockSize(1, 1, 256, 256, null, null, 0)]);
        Assert.False(wait.IsCompleted);                              // nothing answered it yet
        waits.Offer([new MapBlockSize(300000, 300001, 1024, 1024, "Far Region", null, 13)]);
        Assert.Equal(300000UL * 256 << 32 | 300001UL * 256, await wait);
    }

    [Fact]
    public async Task A_wait_for_a_region_name_ends_with_nothing_when_no_answer_names_it_in_time_or_the_caller_gives_up()
    {
        var waits = new RegionNameWaits();
        Assert.Null(await waits.WaitAsync("Nowhere Example", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        var wait = waits.WaitAsync("Nowhere Example", TimeSpan.FromSeconds(30), cancel.Token);
        cancel.Cancel();
        Assert.Null(await wait);
        waits.Offer([new MapBlockSize(1, 1, 256, 256, "Nowhere Example", null, 13)]);   // an answer that comes late changes nothing
    }

    [Fact]
    public async Task Two_waits_for_the_same_name_are_both_answered_and_a_wait_for_another_name_is_not()
    {
        var waits = new RegionNameWaits();
        var one = waits.WaitAsync("Far Region", TimeSpan.FromSeconds(30), CancellationToken.None);
        var two = waits.WaitAsync("FAR REGION", TimeSpan.FromSeconds(30), CancellationToken.None);
        var other = waits.WaitAsync("Other Region", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        waits.Offer([new MapBlockSize(2000, 2000, 256, 256, "Far Region", null, 13)]);
        Assert.Equal(MapMathHandle(2000, 2000), await one);
        Assert.Equal(MapMathHandle(2000, 2000), await two);
        Assert.Null(await other);
    }

    [Fact]
    public async Task An_answer_that_says_there_is_no_region_of_that_name_ends_the_wait_at_once_with_nothing()
    {
        // the grid answers a name it does not know with a block that echoes the name and has the no-region access level
        var waits = new RegionNameWaits();
        var wait = waits.WaitAsync("Nowhere Example", TimeSpan.FromSeconds(30), CancellationToken.None);
        waits.Offer([new MapBlockSize(0, 0, 256, 256, "Nowhere Example", null, 255)]);
        Assert.True(wait.IsCompleted || await Task.WhenAny(wait, Task.Delay(60_000)) == wait, "the wait ended");
        Assert.Null(await wait);
    }

    [Fact]
    public async Task A_name_the_grid_has_answered_before_is_answered_again_at_once()
    {
        var waits = new RegionNameWaits();
        waits.Offer([new MapBlockSize(2000, 2001, 256, 256, "Far Region", null, 13)]);   // an area reply the map happened to receive
        var again = waits.WaitAsync("far region", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(again.IsCompleted, "answered without waiting");
        Assert.Equal(MapMathHandle(2000, 2001), await again);
        waits.Offer([new MapBlockSize(0, 0, 256, 256, "Far Region", null, 255)]);       // the region went away
        Assert.Null(await waits.WaitAsync("Far Region", TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    private static ulong MapMathHandle(int x, int y) => Viewer.Core.Map.MapMath.HandleOf(x, y);

    // ---------------- never in a capture ----------------

    [Fact]
    public void Teleport_offers_progress_coarse_locations_and_friend_positions_are_not_written_to_a_capture()
    {
        TransportEvent[] events =
        [
            new TeleportProgressed(TeleportPhase.Progress, "sending_dest", true),
            new TeleportOffered(Friend, "N", Lure, new MessageBody("x", MessageChannel.InstantMessage)),
            new CoarseLocationsUpdated(Here, [new CoarseAvatar(Friend, new Vector3(1, 2, 3))]),
            new FriendLocated(Friend, Here.Handle, new Vector3(1, 2, 3)),
        ];
        foreach (var e in events)
            Assert.False(CaptureFile.IsRecorded(e), e.GetType().Name);
        Assert.True(CaptureFile.IsRecorded(new MapBlockSize(1, 2, 256, 256, "R", null, 13)));   // a region's own size and picture stay recorded
    }

    // ---------------- the session ----------------

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
    public void Teleport_offers_progress_friend_locations_and_map_blocks_reach_the_communication_channel_after_the_names_have_learned()
    {
        var (session, fake, _) = InWorld();
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        TransportEvent[] events =
        [
            new TeleportProgressed(TeleportPhase.Started, "", true),
            new TeleportOffered(Friend, "Offering Resident", Lure, new MessageBody("come", MessageChannel.InstantMessage)),
            new FriendLocated(Friend, Here.Handle, new Vector3(1, 2, 3)),
            new MapBlockSize(1000, 1000, 256, 256, "A Region", Guid.NewGuid(), 13),
        ];
        foreach (var e in events)
            fake.Raise(e);
        Assert.Equal(events, seen.Where(e => e is TeleportProgressed or TeleportOffered or FriendLocated or MapBlockSize).ToArray());
        Assert.Equal("Offering Resident", session.Names.Get(Friend));
    }

    [Fact]
    public void A_new_regions_connect_and_movement_reach_the_communication_channel_so_the_teleport_can_end_on_arrival()
    {
        var (session, fake, _) = InWorld();
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(new SimConnected(There, 256, 256));
        fake.CurrentSim = There;
        fake.Raise(new MovementComplete(There));
        Assert.Contains(seen, e => e is SimConnected c && ReferenceEquals(c.Sim, There));
        Assert.Contains(seen, e => e is MovementComplete m && ReferenceEquals(m.Sim, There));
    }

    [Fact]
    public void A_movement_message_that_comes_before_the_new_region_is_the_sessions_current_one_is_still_passed_on()
    {
        // on the wire the agent's movement message can come before the session has made the new region its current one; the
        // teleport ends on it, so it must not be dropped for naming a region that is not yet current
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/", Neighbours = true }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        fake.Raise(new MovementComplete(There));
        Assert.Contains(seen, e => e is MovementComplete m && ReferenceEquals(m.Sim, There));
    }

    [Fact]
    public void The_coarse_list_is_kept_replaced_by_each_update_and_only_from_the_current_region()
    {
        var (session, fake, _) = InWorld();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Empty(session.CoarseLocations);
        fake.Raise(new CoarseLocationsUpdated(Here, [new CoarseAvatar(a, new Vector3(1, 2, 3)), new CoarseAvatar(b, new Vector3(4, 5, 6))]));
        Assert.Equal(2, session.CoarseLocations.Count);
        fake.Raise(new CoarseLocationsUpdated(Here, [new CoarseAvatar(b, new Vector3(7, 8, 9))]));
        Assert.Equal(new CoarseAvatar(b, new Vector3(7, 8, 9)), Assert.Single(session.CoarseLocations));   // replaced, not added to
        var neighbour = new SimRef(257000UL << 32 | 256000UL, "Neighbour");
        fake.Raise(new CoarseLocationsUpdated(neighbour, [new CoarseAvatar(a, new Vector3(1, 1, 1))]));
        Assert.Equal(b, Assert.Single(session.CoarseLocations).Id);   // another simulator's list is not ours
    }

    [Fact]
    public void Arrival_in_a_new_region_starts_afresh_objects_avatars_terrain_animations_self_motion_and_coarse_locations_are_dropped()
    {
        var (session, fake, _) = InWorld();
        var agent = Guid.NewGuid();
        fake.Raise(new ObjectUpdated(Here, new PrimData(5, Guid.NewGuid(), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One), false, null));
        fake.Raise(new AvatarUpdated(Here, new AvatarData(7, agent, "Av", new Vector3(50, 50, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f))));
        fake.Raise(new LandPatch(Here, 0, 0, 16, new float[256]));
        fake.Raise(new AvatarAnimationsReceived(Here, agent, [new PlayingAnimation(Guid.NewGuid(), 1)]));
        fake.Raise(new CoarseLocationsUpdated(Here, [new CoarseAvatar(agent, new Vector3(1, 2, 3))]));
        Assert.Single(session.World.Snapshot().Objects);
        Assert.Single(session.World.Snapshot().Avatars);
        Assert.NotEmpty(session.World.Animations(agent));
        Assert.Equal(1, session.World.TerrainPatchCount);
        int epoch = session.World.Epoch;

        fake.Raise(new SimConnected(There, 256, 256));   // the teleport lands
        fake.CurrentSim = There;

        Assert.Equal(epoch + 1, session.World.Epoch);
        Assert.Empty(session.World.Snapshot().Objects);
        Assert.Empty(session.World.Snapshot().Avatars);
        Assert.Empty(session.World.Animations(agent));
        Assert.Equal(0, session.World.TerrainPatchCount);
        Assert.Null(session.World.LastSelfMotion);
        Assert.Empty(session.CoarseLocations);
        Assert.Equal("Far Region", session.Region!.Name);

        // what the old region still sends is dropped; what the new one sends is kept
        fake.Raise(new ObjectUpdated(Here, new PrimData(6, Guid.NewGuid(), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One), false, null));
        fake.Raise(new CoarseLocationsUpdated(Here, [new CoarseAvatar(agent, Vector3.One)]));
        Assert.Empty(session.World.Snapshot().Objects);
        Assert.Empty(session.CoarseLocations);
        fake.Raise(new CoarseLocationsUpdated(There, [new CoarseAvatar(agent, Vector3.One)]));
        Assert.Single(session.CoarseLocations);
    }

    [Fact]
    public void Map_reads_go_to_the_transport_only_while_the_session_runs_and_only_when_the_request_is_sensible()
    {
        var (session, fake, _) = InWorld();
        Assert.True(session.RequestMapBlocks(1000, 1000, 1003, 1003));
        Assert.Equal([(1000, 1000, 1003, 1003)], fake.BlockRequests);
        Assert.False(session.RequestMapBlocks(5, 5, 4, 4));                  // empty rectangle
        Assert.False(session.RequestMapBlocks(-1, 0, 3, 3));                 // outside the grid
        Assert.False(session.RequestMapBlocks(0, 0, 70000, 3));
        Assert.Single(fake.BlockRequests);
        Assert.True(session.RequestRegionNamed("  Example  "));
        Assert.Equal(["Example"], fake.NameSearches);
        Assert.False(session.RequestRegionNamed("   "));
        Assert.True(session.RequestFriendLocation(Friend));
        Assert.Equal([Friend], fake.FriendLocations);
        Assert.False(session.RequestFriendLocation(Guid.Empty));
        session.Dispose();
        Assert.False(session.RequestMapBlocks(1, 1, 2, 2));
        Assert.False(session.RequestRegionNamed("Example"));
        Assert.False(session.RequestFriendLocation(Friend));
        Assert.Single(fake.BlockRequests);
    }

    [Fact]
    public async Task A_map_tile_is_fetched_through_the_transport_and_a_dead_session_fetches_nothing()
    {
        var (session, fake, _) = InWorld();
        fake.OnTile = (_, _, _) => Viewer.Core.Assets.AssetFetchResult.Ok(new byte[] { 1, 2, 3 });
        var got = await session.FetchMapTileAsync(new Viewer.Core.Map.MapTileKey(2, 1000, 1000), CancellationToken.None);
        Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.Ok, got.Status);
        Assert.Equal([1, 2, 3], got.Bytes.ToArray());
        Assert.Equal([(2, 1000, 1000)], fake.TileRequests);
        session.Dispose();
        var dead = await session.FetchMapTileAsync(new Viewer.Core.Map.MapTileKey(1, 1, 1), CancellationToken.None);
        Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.Failed, dead.Status);
        Assert.Single(fake.TileRequests);
    }

    [Fact]
    public void No_offer_text_name_position_or_other_persons_id_reaches_the_log()
    {
        var (session, fake, log) = InWorld();
        fake.Raise(new TeleportOffered(Friend, SecretName, Lure, new MessageBody(SecretText, MessageChannel.InstantMessage)));
        fake.Raise(new CoarseLocationsUpdated(Here, [new CoarseAvatar(Friend, new Vector3(123.25f, 77.5f, 31f))]));
        fake.Raise(new FriendLocated(Friend, There.Handle, new Vector3(88.125f, 99.375f, 0)));
        fake.Raise(new TeleportProgressed(TeleportPhase.Failed, SecretText, true));
        fake.Raise(new SimConnected(There, 256, 256));
        session.Dispose();
        string all = log.Text;
        Assert.DoesNotContain(SecretText, all);
        Assert.DoesNotContain(SecretName, all);
        Assert.DoesNotContain(Friend.ToString(), all);
        Assert.DoesNotContain("123.25", all);
        Assert.DoesNotContain("88.125", all);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Vector3 = System.Numerics.Vector3;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Set home, asking a friend to offer a teleport, the grid's alerts (parcel entry denied and the rest) and the parcel collision
/// statuses, below the interface: the intents' checks, their commands, the adapter's translation, the classification of alerts,
/// the home position, and the rule that none of it reaches a capture. No grid: a fake transport and synthetic messages. Every
/// member builds its own objects, so the class runs in parallel.
/// </summary>
public class TravelAnswersCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    private sealed class Rig
    {
        public FakeTransport Transport = new();
        public ActionPipeline Pipeline;
        public Rig() => Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
    }

    // ---------------- intents ----------------

    [Fact]
    public void Set_home_and_a_teleport_request_pass_their_checks_and_become_their_commands()
    {
        var r = new Rig();
        Assert.Empty(new SetHomeIntent().Validate());
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new SetHomeIntent()).Status);
        Assert.Equal(new SetHomeCommand(), r.Transport.Commands[^1]);
        Assert.Empty(new TeleportRequestIntent(Friend, "").Validate());
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new TeleportRequestIntent(Friend, "  may I come?  ")).Status);
        Assert.Equal(new TeleportRequestCommand(Friend, "may I come?"), r.Transport.Commands[^1]);
        Assert.Equal(2, r.Transport.Commands.Count);
    }

    public static TheoryData<ActionIntent, string> BadRequests => new()
    {
        { new TeleportRequestIntent(Guid.Empty, "hi"), "teleport.no_target" },
        { new TeleportRequestIntent(Friend, new string('a', 1024)), "teleport.message_too_long" },
        { new TeleportRequestIntent(Friend, "a\u0000b"), "teleport.message_control_chars" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public void A_bad_teleport_request_is_refused_with_its_reason_and_nothing_is_sent(ActionIntent intent, string code)
    {
        var r = new Rig();
        var result = r.Pipeline.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void A_restriction_on_either_kind_refuses_it_and_nothing_is_sent()
    {
        var r = new Rig();
        var restrictions = new RestrictionSet();
        r.Pipeline.AddVeto(restrictions);
        restrictions.Restrict(ActionKind.SetHome);
        restrictions.Restrict(ActionKind.TeleportRequest);
        Assert.Equal(ActionStatus.Vetoed, r.Pipeline.Submit(new SetHomeIntent()).Status);
        Assert.Equal(ActionStatus.Vetoed, r.Pipeline.Submit(new TeleportRequestIntent(Friend, "hi")).Status);
        Assert.Empty(r.Transport.Commands);
    }

    private static LmvTransport NewAdapter() => new(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new ListSink(), "test"));

    public static TheoryData<TransportCommand> NewCommands => new() { new SetHomeCommand(), new TeleportRequestCommand(Friend, "hi") };

    [Theory]
    [MemberData(nameof(NewCommands))]
    public void Without_a_circuit_the_new_commands_are_refused_with_their_reason_and_nothing_throws(TransportCommand command)
    {
        using var t = NewAdapter();
        var answer = ((ICommandTransport)t).Execute(command);
        Assert.False(answer.Sent);
        Assert.Equal("not_connected", answer.Reason);
    }

    // ---------------- the adapter's translation ----------------

    private static InstantMessage Im(InstantMessageDialog dialog, Guid? from = null) => new()
    {
        FromAgentID = new UUID(from ?? Friend), FromAgentName = "Asking Resident", ToAgentID = new UUID(Self),
        IMSessionID = UUID.Zero, Dialog = dialog, Offline = InstantMessageOnline.Online, Message = "may I join you?", BinaryBucket = [],
    };

    [Fact]
    public void A_message_of_the_request_kind_becomes_a_teleport_request_and_nothing_else_does()
    {
        var req = LmvTransport.ToTeleportRequest(Im(InstantMessageDialog.RequestLure));
        Assert.NotNull(req);
        Assert.Equal(Friend, req!.From);
        Assert.Equal("Asking Resident", req.FromName);
        Assert.Equal("may I join you?", req.Message.Reveal());
        Assert.Null(LmvTransport.ToTeleportRequest(Im(InstantMessageDialog.RequestLure, Guid.Empty)));
        foreach (var d in new[] { InstantMessageDialog.RequestTeleport, InstantMessageDialog.MessageFromAgent, InstantMessageDialog.FriendshipOffered, InstantMessageDialog.GodLikeRequestTeleport })
            Assert.Null(LmvTransport.ToTeleportRequest(Im(d)));
        Assert.Null(LmvTransport.ToTeleportOffer(Im(InstantMessageDialog.RequestLure)));   // a request is not an offer
    }

    [Fact]
    public void Our_request_is_the_request_kind_sent_online_with_no_conversation_id_and_an_empty_bucket()
    {
        var m = LmvTransport.ToTeleportRequestMessage("may I come?");
        Assert.Equal(InstantMessageDialog.RequestLure, m.Dialog);
        Assert.Equal(InstantMessageOnline.Online, m.Offline);
        Assert.Equal(UUID.Zero, m.Session);
        Assert.Equal("may I come?", m.Text);
        Assert.Equal(0, m.BucketLength);
    }

    [Fact]
    public void A_grid_alert_keeps_its_text_key_and_modal_flag()
    {
        Assert.Equal(new ServerAlert("Home position set.", "HomePositionSet", false), LmvTransport.ToServerAlert("Home position set.", "HomePositionSet", false));
        Assert.Equal(new ServerAlert("Plain text", "", true), LmvTransport.ToServerAlert("Plain text", null, true));
    }

    [Fact]
    public void A_parcel_collision_status_becomes_the_reason_and_any_other_sequence_number_becomes_nothing()
    {
        Assert.Equal(new ParcelCollision(ParcelDenial.NotInGroup), LmvTransport.ToParcelCollision(-20000));
        Assert.Equal(new ParcelCollision(ParcelDenial.Banned), LmvTransport.ToParcelCollision(-30000));
        Assert.Equal(new ParcelCollision(ParcelDenial.NotOnList), LmvTransport.ToParcelCollision(-40000));
        foreach (var other in new[] { -10000, -50000, 0, 1, 7, int.MaxValue })
            Assert.Null(LmvTransport.ToParcelCollision(other));
    }

    // ---------------- sorting the grid's alerts ----------------

    public static TheoryData<string, string, bool, AlertKind> Alerts => new()
    {
        // keys the official viewer has a notification for
        { "x", "Cannot enter parcel: banned", false, AlertKind.ParcelBanned },
        { "x", "Cannot enter parcel: not on access list", false, AlertKind.ParcelNotOnList },
        { "x", "Cannot enter parcel: not a group member", false, AlertKind.ParcelNotInGroup },
        // the words the region server sends for the same things
        { "You are banned from parcel", "", false, AlertKind.ParcelBanned },
        { "You are banned from parcel, please leave by your own will", "", false, AlertKind.ParcelBannedWarning },
        { "You do not have access to the parcel", "", false, AlertKind.ParcelNoAccess },
        { "Cannot enter parcel, something else", "", false, AlertKind.ParcelOther },
        // set home
        { "Home position set.", "", false, AlertKind.HomeSet },
        { "x", "HomePositionSet", false, AlertKind.HomeSet },
        { "You are not allowed to set your home location in this parcel.", "", false, AlertKind.HomeNotAllowed },
        { "Set Home request failed.", "", false, AlertKind.HomeFailed },
        { "Set Home request failed. (HG visitor)", "", false, AlertKind.HomeFailed },
        // the region server puts a slash in front of every plain alert except the home confirmation, so the same words arrive with one
        { "/You are banned from parcel", "", false, AlertKind.ParcelBanned },
        { "/You are banned from parcel, please leave by your own will", "", false, AlertKind.ParcelBannedWarning },
        { "/You do not have access to the parcel", "", false, AlertKind.ParcelNoAccess },
        { "/Cannot enter parcel, something else", "", false, AlertKind.ParcelOther },
        { "/You are not allowed to set your home location in this parcel.", "", false, AlertKind.HomeNotAllowed },
        { "/Set Home request failed.", "", false, AlertKind.HomeFailed },
        { "/Set Home request failed. (User Lookup)", "", false, AlertKind.HomeFailed },
        { "//Home position set.", "", false, AlertKind.Important },
        // everything else is shown as the grid sent it
        { "/Region restart in 5 minutes", "", false, AlertKind.Important },
        { "Something went wrong", "", true, AlertKind.Error },
        { "Plain notice", "", false, AlertKind.Tip },
        { "", "", false, AlertKind.Tip },
    };

    [Theory]
    [MemberData(nameof(Alerts))]
    public void The_grids_alert_is_sorted_by_its_key_then_its_words(string text, string key, bool modal, AlertKind expected) =>
        Assert.Equal(expected, ServerAlerts.Classify(text, key, modal));

    // ---------------- the home position ----------------

    [Fact]
    public void The_home_position_is_kept_half_a_metre_inside_the_region_and_never_below_the_ground_or_above_the_top()
    {
        Assert.Equal(new Vector3(0.5f, 0.5f, 30), HomePosition.Clamp(new Vector3(-3, 0, 30), 256, 20, 4096));
        Assert.Equal(new Vector3(255.5f, 255.5f, 30), HomePosition.Clamp(new Vector3(256, 300, 30), 256, 20, 4096));
        Assert.Equal(new Vector3(100, 100, 20), HomePosition.Clamp(new Vector3(100, 100, 5), 256, 20, 4096));
        Assert.Equal(new Vector3(100, 100, 4096), HomePosition.Clamp(new Vector3(100, 100, 9000), 256, 20, 4096));
        Assert.Equal(new Vector3(100, 100, 44), HomePosition.Clamp(new Vector3(100, 100, 44), 256, 20, 4096));
        Assert.Equal(new Vector3(500, 500, 20), HomePosition.Clamp(new Vector3(500, 500, 0), 1024, 20, 4096));   // a larger region's own width
    }

    [Fact]
    public void The_place_to_send_uses_the_ground_under_the_agent_and_survives_a_ground_lookup_that_fails()
    {
        float Ground(int x, int y) => x == 100 && y == 100 ? 20f : 0f;
        Assert.Equal(new Vector3(100.7f, 100.2f, 20), HomePosition.For(new Vector3(100.7f, 100.2f, 5), 256, Ground, 4096));
        Assert.Equal(new Vector3(100.7f, 100.2f, 44), HomePosition.For(new Vector3(100.7f, 100.2f, 44), 256, Ground, 4096));
        // the library's lookup can index past its terrain while the region's patches arrive: no throw, ground counts as 0
        Assert.Equal(new Vector3(10, 10, 3), HomePosition.For(new Vector3(10, 10, 3), 256, (_, _) => throw new IndexOutOfRangeException(), 4096));
        Assert.Equal(new Vector3(10, 10, 0), HomePosition.For(new Vector3(10, 10, -5), 256, (_, _) => throw new IndexOutOfRangeException(), 4096));
        Assert.Equal(new Vector3(10, 10, 0), HomePosition.For(new Vector3(10, 10, -5), 256, (_, _) => float.NaN, 4096));
        // the position is looked up inside the region even when the agent has drifted a little past its edge
        Assert.Equal(new Vector3(255.5f, 0.5f, 7), HomePosition.For(new Vector3(260, -2, 7), 256, (x, y) => x is >= 0 and < 256 && y is >= 0 and < 256 ? 0f : throw new IndexOutOfRangeException(), 4096));
    }

    // ---------------- never in a capture, and through the session ----------------

    [Fact]
    public void The_new_records_are_not_written_to_a_capture()
    {
        TransportEvent[] events =
        [
            new TeleportRequested(Friend, "N", new MessageBody("x", MessageChannel.InstantMessage)),
            new ServerAlert("Home position set.", "", false),
            new ParcelCollision(ParcelDenial.Banned),
        ];
        foreach (var e in events)
            Assert.False(CaptureFile.IsRecorded(e), e.GetType().Name);
    }

    [Fact]
    public void The_new_records_reach_the_communication_channel_and_a_requester_is_named()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        TransportEvent[] events =
        [
            new TeleportRequested(Friend, "Asking Resident", new MessageBody("may I?", MessageChannel.InstantMessage)),
            new ServerAlert("You are banned from parcel", "", false),
            new ParcelCollision(ParcelDenial.NotOnList),
        ];
        foreach (var e in events)
            fake.Raise(e);
        Assert.Equal(events, seen.Where(e => e is TeleportRequested or ServerAlert or ParcelCollision).ToArray());
        Assert.Equal("Asking Resident", session.Names.Get(Friend));
    }
}

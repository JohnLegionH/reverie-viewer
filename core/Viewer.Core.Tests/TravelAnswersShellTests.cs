// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Map;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shell side of set home, teleport requests, the grid's notices (parcel entry denied and the rest) and the height a map click
/// asks for. Driven with synthetic events through the real action pipeline over a fake transport. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class TravelAnswersShellTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Other = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly TeleportModel Model;
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new TeleportModel(Self, new NameCache(), Pipeline.Submit, () => Now);
        }

        public void Request(Guid from, string name, string text = "may I come to you?") =>
            Model.Receive(new TeleportRequested(from, name, new MessageBody(text, MessageChannel.InstantMessage)));
    }

    // ---------------- set home ----------------

    [Fact]
    public void Set_home_sends_one_intent_and_does_not_start_a_teleport_or_wait_on_one()
    {
        var r = new Rig();
        Assert.True(r.Model.SetHome());
        Assert.Equal(new SetHomeCommand(), Assert.Single(r.Transport.Commands));
        Assert.False(r.Model.Busy);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        // allowed while a teleport goes on: it is not a teleport
        Assert.True(r.Model.ToRegion("Far Region", new Vector3(1, 2, 3)));
        Assert.True(r.Model.SetHome());
        Assert.Equal(3, r.Transport.Commands.Count);
    }

    [Fact]
    public void A_refused_set_home_says_why_and_a_circuitless_one_sends_nothing()
    {
        var r = new Rig();
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        Assert.False(r.Model.SetHome());
        Assert.NotNull(r.Model.Status);
    }

    // ---------------- teleport requests ----------------

    [Fact]
    public void A_request_is_listed_with_the_sender_and_text_and_nothing_is_sent_by_itself()
    {
        var r = new Rig();
        r.Request(Friend, "Asking Resident");
        var row = Assert.Single(r.Model.Requests);
        Assert.Equal(new TeleportRequestRow(Friend, "Asking Resident", "may I come to you?"), row);
        r.Now += TimeSpan.FromMinutes(5);
        r.Model.Tick();
        Assert.Empty(r.Transport.Commands);
        Assert.False(r.Model.Busy);
        Assert.Empty(r.Model.Offers);                      // a request is not an offer
    }

    [Fact]
    public void A_second_request_from_the_same_person_replaces_the_first_and_a_request_from_nobody_or_oneself_is_dropped()
    {
        var r = new Rig();
        r.Request(Friend, "A", "first");
        r.Request(Other, "B");
        r.Request(Friend, "A", "second");
        Assert.Equal(["may I come to you?", "second"], r.Model.Requests.Select(x => x.Message).ToArray());
        Assert.Equal(2, r.Model.Requests.Count);
        r.Request(Guid.Empty, "Nobody");
        r.Request(Self, "Me");
        Assert.Equal(2, r.Model.Requests.Count);
    }

    [Fact]
    public void Offering_in_reply_sends_one_offer_to_the_requester_and_removes_the_request_but_only_once()
    {
        var r = new Rig();
        r.Request(Friend, "Asking Resident");
        Assert.True(r.Model.OfferToRequester(Friend));
        Assert.Equal(new TeleportOfferCommand(Friend, ""), Assert.Single(r.Transport.Commands));
        Assert.Empty(r.Model.Requests);
        Assert.False(r.Model.OfferToRequester(Friend));    // answered once
        Assert.Single(r.Transport.Commands);
        Assert.False(r.Model.Busy);                         // offering is not our own teleport
    }

    [Fact]
    public void Declining_a_request_sends_nothing_and_removes_it()
    {
        var r = new Rig();
        r.Request(Friend, "Asking Resident");
        Assert.True(r.Model.DeclineRequest(Friend));
        Assert.Empty(r.Model.Requests);
        Assert.Empty(r.Transport.Commands);
        Assert.False(r.Model.DeclineRequest(Friend));
    }

    [Fact]
    public void A_failed_offer_in_reply_keeps_the_request_so_the_user_can_try_again()
    {
        var r = new Rig();
        r.Request(Friend, "Asking Resident");
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        Assert.False(r.Model.OfferToRequester(Friend));
        Assert.Single(r.Model.Requests);
    }

    [Fact]
    public void Asking_a_friend_sends_one_request_with_the_message_and_never_to_oneself()
    {
        var r = new Rig();
        Assert.True(r.Model.AskToBeTeleportedBy(Friend, "may I come?"));
        Assert.Equal(new TeleportRequestCommand(Friend, "may I come?"), Assert.Single(r.Transport.Commands));
        Assert.False(r.Model.Busy);
        Assert.False(r.Model.AskToBeTeleportedBy(Self, "me"));
        Assert.Equal(("status.teleport.no_target", null), r.Model.Status);
        Assert.False(r.Model.AskToBeTeleportedBy(Guid.Empty, "x"));
        Assert.Single(r.Transport.Commands);
    }

    // ---------------- a map click asks for the height the agent is at ----------------

    private static MapSpot ClickWith((ulong, Vector3)? own, bool search = false)
    {
        var links = new MapLinks { Own = () => own, Teleport = (_, _) => true, RequestTile = _ => MapTileState.Loading };
        var map = new WorldMapModel(links, WorldMapModel.DefaultScale);
        map.Receive(new MapBlockSize(1001, 1001, 256, 256, "Example Region", Guid.NewGuid(), 13));
        if (search)
        {
            map.Choose(map.Regions.Single());
            return map.Selected!;
        }
        map.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        Assert.True(map.Click(400, 300, 800, 600));
        return map.Selected!;
    }

    [Fact]
    public void A_click_takes_the_height_the_agent_is_at_and_zero_when_that_is_not_known()
    {
        Assert.Equal(52.5f, ClickWith((MapMath.HandleOf(1000, 1000), new Vector3(10, 10, 52.5f))).Local.Z);
        Assert.Equal(0f, ClickWith(null).Local.Z);
    }

    [Fact]
    public void Choosing_a_searched_region_keeps_the_middle_of_it_at_height_zero_whatever_height_the_agent_is_at()
    {
        var spot = ClickWith((MapMath.HandleOf(1000, 1000), new Vector3(10, 10, 52.5f)), search: true);
        Assert.Equal(new Vector3(128, 128, 0), spot.Local);
    }

    // ---------------- notices ----------------

    private static NoticeModel Notices() => new();

    [Fact]
    public void Set_home_success_and_refusals_are_worded_and_the_grids_own_words_are_kept_for_what_has_no_wording()
    {
        var n = Notices();
        n.Receive(new ServerAlert("Home position set.", "", false));
        Assert.Equal(new Notice("notice.home.set", null, AlertKind.HomeSet, NoticeSeverity.Tip), n.Latest);
        n.Receive(new ServerAlert("You are not allowed to set your home location in this parcel.", "", false));
        Assert.Equal(new Notice("notice.home.not_allowed", null, AlertKind.HomeNotAllowed, NoticeSeverity.Tip), n.Latest);
        n.Receive(new ServerAlert("Set Home request failed. (HG visitor)", "", false));
        Assert.Equal(new Notice("notice.home.failed", "Set Home request failed. (HG visitor)", AlertKind.HomeFailed, NoticeSeverity.Tip), n.Latest);
        // as the region server sends them: a slash in front of everything but the confirmation
        n.Receive(new ServerAlert("/You are not allowed to set your home location in this parcel.", "", false));
        Assert.Equal(new Notice("notice.home.not_allowed", null, AlertKind.HomeNotAllowed, NoticeSeverity.Tip), n.Latest);
        n.Receive(new ServerAlert("/Set Home request failed. (User Lookup)", "", false));
        Assert.Equal(new Notice("notice.home.failed", "Set Home request failed. (User Lookup)", AlertKind.HomeFailed, NoticeSeverity.Tip), n.Latest);
        Assert.Equal(5, n.Notices.Count);
    }

    [Theory]
    [InlineData("You are banned from parcel", "", "notice.parcel.banned", AlertKind.ParcelBanned)]
    [InlineData("You are banned from parcel, please leave by your own will", "", "notice.parcel.banned_warning", AlertKind.ParcelBannedWarning)]
    [InlineData("You do not have access to the parcel", "", "notice.parcel.no_access", AlertKind.ParcelNoAccess)]
    [InlineData("/You are banned from parcel", "", "notice.parcel.banned", AlertKind.ParcelBanned)]
    [InlineData("/You are banned from parcel, please leave by your own will", "", "notice.parcel.banned_warning", AlertKind.ParcelBannedWarning)]
    [InlineData("/You do not have access to the parcel", "", "notice.parcel.no_access", AlertKind.ParcelNoAccess)]
    [InlineData("x", "Cannot enter parcel: banned", "notice.parcel.banned", AlertKind.ParcelBanned)]
    [InlineData("x", "Cannot enter parcel: not on access list", "notice.parcel.not_on_list", AlertKind.ParcelNotOnList)]
    [InlineData("x", "Cannot enter parcel: not a group member", "notice.parcel.not_in_group", AlertKind.ParcelNotInGroup)]
    public void Each_parcel_entry_denial_the_grid_can_send_is_worded(string text, string key, string wording, AlertKind kind)
    {
        var n = Notices();
        n.Receive(new ServerAlert(text, key, false));
        Assert.Equal(new Notice(wording, null, kind, NoticeSeverity.Tip), n.Latest);
    }

    [Fact]
    public void Other_alerts_are_shown_as_the_grid_sent_them_with_the_severity_the_grid_asked_for()
    {
        var n = Notices();
        n.Receive(new ServerAlert("Plain notice", "", false));
        Assert.Equal(new Notice("notice.raw", "Plain notice", AlertKind.Tip, NoticeSeverity.Tip), n.Latest);
        n.Receive(new ServerAlert("/Region restart in 5 minutes", "", false));
        Assert.Equal(new Notice("notice.raw", "Region restart in 5 minutes", AlertKind.Important, NoticeSeverity.Important), n.Latest);
        n.Receive(new ServerAlert("Something went wrong", "", true));
        Assert.Equal(new Notice("notice.raw", "Something went wrong", AlertKind.Error, NoticeSeverity.Error), n.Latest);
        n.Receive(new ServerAlert("", "", false));
        Assert.Equal(3, n.Notices.Count);                 // an empty alert shows nothing
    }

    [Fact]
    public void Notices_are_kept_newest_last_at_most_twenty_and_can_be_dismissed()
    {
        var n = Notices();
        int changes = 0;
        n.Changed += () => changes++;
        for (int i = 0; i < 25; i++)
            n.Receive(new ServerAlert($"note {i}", "", false));
        Assert.Equal(20, n.Notices.Count);
        Assert.Equal("note 5", n.Notices[0].Arg);
        Assert.Equal("note 24", n.Latest!.Arg);
        Assert.Equal(25, changes);
        n.Dismiss();
        Assert.Empty(n.Notices);
        Assert.Null(n.Latest);
        n.Dismiss();
        Assert.Equal(26, changes);                          // nothing to dismiss: no change
    }

    [Fact]
    public void A_parcel_collision_sets_the_barrier_and_a_new_region_clears_it_and_it_shows_no_text()
    {
        var n = Notices();
        n.Receive(new ParcelCollision(ParcelDenial.NotOnList));
        Assert.Equal(ParcelDenial.NotOnList, n.Barrier);
        Assert.Empty(n.Notices);
        n.Receive(new ParcelCollision(ParcelDenial.Banned));
        Assert.Equal(ParcelDenial.Banned, n.Barrier);
        n.Receive(new SimConnected(There, 256, 256));
        Assert.Null(n.Barrier);
    }

    // ---------------- on arrival, through the hub ----------------

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static (TravelHub Hub, FakeTransport Fake) NewHub()
    {
        var fake = new FakeTransport();
        var pipeline = new ActionPipeline(() => fake, () => true, new CoreLog(new ListSink(), "test"));
        var hub = new TravelHub(Self, new NameCache(), pipeline.Submit, new SettingsService(new MemoryStore()),
            () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new MapLinks());
        return (hub, fake);
    }

    [Fact]
    public void A_denial_that_comes_with_a_teleport_arrival_is_shown_after_the_arrival_ends_the_teleport()
    {
        var (hub, fake) = NewHub();
        Assert.True(hub.Teleport.ToRegion("Far Region", new Vector3(1, 2, 3)));
        hub.Enqueue(new TeleportProgressed(TeleportPhase.Started, "", true));
        hub.Enqueue(new TeleportProgressed(TeleportPhase.Finished, "", true));
        hub.Enqueue(new SimConnected(There, 256, 256));
        hub.Enqueue(new MovementComplete(There));
        hub.Enqueue(new ServerAlert("You do not have access to the parcel", "", false));
        hub.Enqueue(new ParcelCollision(ParcelDenial.NotOnList));
        hub.Pump();
        Assert.Equal(1, hub.Teleport.Arrivals);
        Assert.Equal(TeleportStage.Idle, hub.Teleport.Stage);
        Assert.NotNull(hub.Notices.Latest);
        Assert.Equal("notice.parcel.no_access", hub.Notices.Latest!.Key);
        Assert.Equal(ParcelDenial.NotOnList, hub.Notices.Barrier);
    }

    [Fact]
    public void A_teleport_the_grid_refuses_for_a_parcel_reason_keeps_the_grids_words_as_the_failure()
    {
        var (hub, _) = NewHub();
        foreach (var reason in new[] { "Cannot regioncross into banned parcel.", "You are banned from the parcel", "The parcel is restricted", "You dont have access to the region parcels" })
        {
            hub.Teleport.Dismiss();
            Assert.True(hub.Teleport.ToRegion("Far Region", new Vector3(1, 2, 3)));
            hub.Enqueue(new TeleportProgressed(TeleportPhase.Failed, reason, true));
            hub.Pump();
            Assert.Equal(TeleportStage.Failed, hub.Teleport.Stage);
            Assert.Equal(("teleport.error.raw", reason), hub.Teleport.Failure);
        }
    }

    [Fact]
    public void Requests_and_notices_queued_from_another_thread_are_handled_only_when_the_main_thread_pumps()
    {
        var (hub, _) = NewHub();
        Task.Run(() =>
        {
            hub.Enqueue(new TeleportRequested(Friend, "Asking Resident", new MessageBody("may I?", MessageChannel.InstantMessage)));
            hub.Enqueue(new ServerAlert("Home position set.", "", false));
        }).Wait();
        Assert.Empty(hub.Teleport.Requests);
        Assert.Empty(hub.Notices.Notices);
        Assert.Equal(2, hub.Pump());
        Assert.Single(hub.Teleport.Requests);
        Assert.Single(hub.Notices.Notices);
    }
}

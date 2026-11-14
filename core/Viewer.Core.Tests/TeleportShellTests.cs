// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The teleport model: starting a teleport only one at a time, progress and cancel, every failure shown in words, arrival, the
/// timeout and the offers (never answered by themselves). Driven with synthetic events through the real action pipeline over a fake
/// transport and a test clock. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class TeleportShellTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Other = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abce");
    private static readonly Guid Lure = Guid.Parse("10101010-0000-4000-8000-000000000001");
    private static readonly Guid Lure2 = Guid.Parse("10101010-0000-4000-8000-000000000002");

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly NameCache Names = new();
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public readonly TeleportModel Model;
        public int Changes;

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new TeleportModel(Self, Names, Pipeline.Submit, () => Now);
            Model.Changed += () => Changes++;
        }

        public void Raise(TeleportPhase phase, string message = "", bool canCancel = true, string region = "") =>
            Model.Receive(new TeleportProgressed(phase, message, canCancel, region));

        public void Offer(Guid from, string name, Guid lure, string text = "come over") =>
            Model.Receive(new TeleportOffered(from, name, lure, new MessageBody(text, MessageChannel.InstantMessage)));

        /// <summary>A full teleport as far as the grid starting it.</summary>
        public void Begin()
        {
            Assert.True(Model.ToRegion("Far Region", new Vector3(10, 20, 30)));
            Raise(TeleportPhase.Started);
        }
    }

    // ---------------- starting ----------------

    [Fact]
    public void Each_way_of_starting_a_teleport_submits_its_intent_once_and_waits_for_the_grid()
    {
        var cases = new (Func<TeleportModel, bool> Start, TransportCommand Command)[]
        {
            (m => m.ToRegion("  Far Region ", new Vector3(10, 20, 30)), new TeleportRegionCommand("Far Region", new Vector3(10, 20, 30))),
            (m => m.ToHandle(There.Handle, new Vector3(1, 2, 3)), new TeleportHandleCommand(There.Handle, new Vector3(1, 2, 3))),
            (m => m.ToLandmark(Lure), new TeleportLandmarkCommand(Lure)),
            (m => m.Home(), new TeleportHomeCommand()),
        };
        foreach (var (start, command) in cases)
        {
            var r = new Rig();
            Assert.True(start(r.Model));
            Assert.Equal(command, Assert.Single(r.Transport.Commands));
            Assert.Equal(TeleportStage.Requested, r.Model.Stage);
            Assert.True(r.Model.Busy);
            Assert.True(r.Model.CanCancel);
            Assert.Equal(("teleport.progress.requesting", null), r.Model.Progress);
            Assert.Null(r.Model.Status);
            Assert.True(r.Changes > 0);
        }
    }

    [Fact]
    public void A_second_teleport_is_refused_while_one_is_going_on_and_nothing_is_sent()
    {
        var r = new Rig();
        r.Begin();
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
        int sent = r.Transport.Commands.Count;
        Assert.False(r.Model.ToRegion("Other Region", new Vector3(1, 1, 1)));
        Assert.False(r.Model.ToHandle(There.Handle, new Vector3(1, 1, 1)));
        Assert.False(r.Model.ToLandmark(Lure));
        Assert.False(r.Model.Home());
        Assert.Equal(("status.teleport.busy", null), r.Model.Status);
        Assert.Equal(sent, r.Transport.Commands.Count);
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
    }

    [Fact]
    public void A_teleport_the_pipeline_refuses_leaves_the_model_idle_with_the_reason()
    {
        var r = new Rig();
        Assert.False(r.Model.ToRegion("", new Vector3(1, 1, 1)));
        Assert.Equal(("status.teleport.no_region", null), r.Model.Status);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Busy);
        Assert.Empty(r.Transport.Commands);
        Assert.False(r.Model.ToRegion("Far", new Vector3(-1, 1, 1)));
        Assert.Equal("status.teleport.bad_position", r.Model.Status!.Value.Key);
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        Assert.False(r.Model.ToRegion("Far", new Vector3(1, 1, 1)));
        Assert.Equal("status.failed", r.Model.Status!.Value.Key);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);   // nothing left the viewer, so nothing is going on
        r.Transport.OnCommand = null;
        Assert.True(r.Model.ToRegion("Far", new Vector3(1, 1, 1)));
        Assert.Null(r.Model.Status);                         // a send that went through clears the old refusal
    }

    // ---------------- progress, cancel ----------------

    [Fact]
    public void Progress_texts_the_viewer_knows_are_shown_in_its_words_and_unknown_ones_as_the_grid_sent_them()
    {
        var r = new Rig();
        r.Begin();
        Assert.Equal(("teleport.progress.starting", null), r.Model.Progress);
        r.Raise(TeleportPhase.Progress, "sending_dest");
        Assert.Equal(("teleport.progress.sending_dest", null), r.Model.Progress);
        r.Raise(TeleportPhase.Progress, "Some new grid text {0}");
        Assert.Equal(("teleport.progress.raw", "Some new grid text {0}"), r.Model.Progress);
        foreach (var key in TeleportModel.KnownProgress)
        {
            r.Raise(TeleportPhase.Progress, key);
            Assert.Equal(("teleport.progress." + key, null), r.Model.Progress);
        }
        Assert.Contains("sending_dest", TeleportModel.KnownProgress);
        Assert.Contains("arriving", TeleportModel.KnownProgress);
        Assert.Equal(11, TeleportModel.KnownProgress.Count);
    }

    [Fact]
    public void A_progress_message_when_no_teleport_is_going_on_is_ignored()
    {
        var r = new Rig();
        r.Raise(TeleportPhase.Progress, "sending_dest");
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Progress);
        Assert.Equal(0, r.Changes);
    }

    [Fact]
    public void A_teleport_the_grid_starts_by_itself_shows_progress_too()
    {
        var r = new Rig();
        r.Raise(TeleportPhase.Started, canCancel: false);   // being sent home
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
        Assert.True(r.Model.Busy);
        Assert.False(r.Model.CanCancel);
    }

    [Fact]
    public void Cancel_sends_the_cancel_ends_the_teleport_and_is_offered_only_while_the_grid_allows_it()
    {
        var r = new Rig();
        Assert.False(r.Model.Cancel());                      // nothing going on: nothing sent
        Assert.Empty(r.Transport.Commands);
        r.Begin();
        Assert.True(r.Model.CanCancel);
        Assert.True(r.Model.Cancel());
        Assert.Equal(new TeleportCancelCommand(), r.Transport.Commands[^1]);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Busy);

        var locked = new Rig();
        locked.Begin();
        locked.Raise(TeleportPhase.Progress, "sending_dest", canCancel: false);
        Assert.False(locked.Model.CanCancel);
        int sent = locked.Transport.Commands.Count;
        Assert.False(locked.Model.Cancel());
        Assert.Equal(sent, locked.Transport.Commands.Count);
        Assert.Equal(TeleportStage.Active, locked.Model.Stage);
        locked.Raise(TeleportPhase.Progress, "contacting", canCancel: true);
        Assert.True(locked.Model.CanCancel);
    }

    [Fact]
    public void The_grids_cancel_answer_ends_the_teleport_and_a_new_one_can_start()
    {
        var r = new Rig();
        r.Begin();
        r.Raise(TeleportPhase.Cancelled);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
        Assert.True(r.Model.Home());
    }

    // ---------------- failure ----------------

    [Fact]
    public void Every_failure_key_the_viewer_knows_is_shown_in_words_and_ends_the_teleport()
    {
        Assert.Equal(16, TeleportModel.KnownErrors.Count);
        foreach (var key in TeleportModel.KnownErrors)
        {
            var r = new Rig();
            r.Begin();
            r.Raise(TeleportPhase.Failed, key);
            Assert.Equal(TeleportStage.Failed, r.Model.Stage);
            Assert.False(r.Model.Busy);
            Assert.Equal("teleport.error." + key, r.Model.Failure!.Value.Key);
            Assert.Null(r.Model.Progress);
        }
        foreach (var key in new[] { "invalid_tport", "invalid_region_handoff", "blocked_tport", "nolandmark_tport", "timeout_tport", "NoHelpIslandTP", "noaccess_tport",
                     "missing_attach_tport", "too_many_uploads_tport", "expired_tport", "expired_region_handoff", "preexisting_tport", "no_host", "no_inventory_host",
                     "MustGetAgeRegion", "RegionTPSpecialUsageBlocked" })
            Assert.Contains(key, TeleportModel.KnownErrors);
    }

    [Fact]
    public void An_unknown_reason_is_shown_as_the_grid_sent_it_and_a_named_region_fills_the_sentence()
    {
        var r = new Rig();
        r.Begin();
        r.Raise(TeleportPhase.Failed, "Region says: no room");
        Assert.Equal(("teleport.error.raw", "Region says: no room"), r.Model.Failure);
        var named = new Rig();
        named.Begin();
        named.Raise(TeleportPhase.Failed, "RegionTPSpecialUsageBlocked", region: "Gaming Region");
        Assert.Equal(("teleport.error.RegionTPSpecialUsageBlocked", "Gaming Region"), named.Model.Failure);
    }

    [Fact]
    public void A_failure_with_no_teleport_going_on_is_still_shown_and_dismissing_it_clears_it()
    {
        var r = new Rig();
        r.Raise(TeleportPhase.Failed, "no_host");
        Assert.Equal(TeleportStage.Failed, r.Model.Stage);
        Assert.Equal("teleport.error.no_host", r.Model.Failure!.Value.Key);
        r.Model.Dismiss();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
    }

    [Fact]
    public void After_a_failure_a_new_teleport_may_start_at_once_and_clears_the_failure()
    {
        var r = new Rig();
        r.Begin();
        r.Raise(TeleportPhase.Failed, "blocked_tport");
        Assert.True(r.Model.Home());
        Assert.Null(r.Model.Failure);
        Assert.Equal(TeleportStage.Requested, r.Model.Stage);
    }

    // ---------------- arrival ----------------

    [Fact]
    public void Every_arrival_raises_the_arrived_event_once_whether_to_a_new_region_or_inside_the_region()
    {
        var far = new Rig();
        int farArrived = 0;
        far.Model.Arrived += () => farArrived++;
        far.Begin();
        far.Model.Receive(new SimConnected(There, 256, 256));
        far.Model.Receive(new MovementComplete(There));
        far.Raise(TeleportPhase.Finished);
        Assert.Equal(1, far.Model.Arrivals);
        Assert.Equal(1, farArrived);

        var local = new Rig();
        int localArrived = 0;
        local.Model.Arrived += () => localArrived++;
        local.Begin();
        local.Raise(TeleportPhase.Local);
        Assert.Equal(1, local.Model.Arrivals);
        Assert.Equal(1, localArrived);

        var started = new Rig();
        int startedArrived = 0;
        started.Model.Arrived += () => startedArrived++;
        started.Begin();
        Assert.Equal(0, startedArrived);                         // a teleport that has only begun has not arrived
    }

    [Fact]
    public void A_teleport_to_a_new_region_ends_when_the_region_has_connected_and_the_agents_movement_there_is_complete()
    {
        var r = new Rig();
        r.Begin();
        r.Raise(TeleportPhase.Progress, "contacting");
        r.Model.Receive(new SimConnected(There, 256, 256));
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.Equal(("teleport.progress.arriving", null), r.Model.Progress);
        r.Raise(TeleportPhase.Finished);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);   // not yet: the agent is not in place
        r.Model.Receive(new MovementComplete(There));
        Assert.Equal(1, r.Model.Arrivals);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);   // the progress stays up for the arrival delay
        r.Now += TeleportModel.ArrivalDelay;
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Busy);
        Assert.Null(r.Model.Progress);
        Assert.Equal(1, r.Model.Arrivals);
    }

    [Fact]
    public void The_movement_may_complete_before_the_finish_message_and_the_arrival_still_ends_once()
    {
        var r = new Rig();
        r.Begin();
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Model.Receive(new MovementComplete(There));
        r.Now += TeleportModel.ArrivalDelay;
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        r.Raise(TeleportPhase.Finished);                        // late, with nothing going on: no second arrival, no stage change
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Equal(1, r.Model.Arrivals);
    }

    [Fact]
    public void A_teleport_inside_the_region_ends_with_the_local_finish_or_a_finish_with_no_new_region()
    {
        var local = new Rig();
        local.Begin();
        local.Raise(TeleportPhase.Local);
        Assert.Equal(TeleportStage.Idle, local.Model.Stage);
        Assert.Equal(1, local.Model.Arrivals);
        var plain = new Rig();
        plain.Begin();
        plain.Raise(TeleportPhase.Finished);                    // no region connected in between: it was local
        Assert.Equal(TeleportStage.Idle, plain.Model.Stage);
    }

    [Fact]
    public void The_agents_movement_may_complete_before_the_region_connect_is_reported_and_the_arrival_still_ends()
    {
        // on the wire the movement message comes first, as it does at login; the model must not depend on the order
        var r = new Rig();
        r.Begin();
        r.Model.Receive(new MovementComplete(There));
        Assert.Equal(TeleportStage.Active, r.Model.Stage);       // not in place until the region is known
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Now += TeleportModel.ArrivalDelay;
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Equal(1, r.Model.Arrivals);
        r.Raise(TeleportPhase.Finished);                          // the library's finish comes last: nothing more happens
        Assert.Equal(1, r.Model.Arrivals);
    }

    [Fact]
    public void A_movement_message_ends_the_arrival_whichever_region_reference_it_carries()
    {
        // official process_agent_movement_complete: the state alone decides, no region is compared
        var r = new Rig();
        r.Begin();
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Model.Receive(new MovementComplete(Here));
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.Equal(1, r.Model.Arrivals);
        r.Now += TeleportModel.ArrivalDelay;
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void An_arrival_whose_movement_message_never_comes_is_given_up_silently_at_the_official_time()
    {
        var r = new Rig();
        r.Begin();
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Now += TimeSpan.FromSeconds(14);
        r.Model.Tick();
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.Equal(0, r.Model.Arrivals);
        r.Now += TimeSpan.FromSeconds(2);
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
        Assert.Equal(0, r.Model.Arrivals);
    }

    [Fact]
    public void A_region_connect_or_movement_with_no_teleport_going_on_is_a_login_and_changes_nothing()
    {
        var r = new Rig();
        r.Model.Receive(new SimConnected(Here, 256, 256));
        r.Model.Receive(new MovementComplete(Here));
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Equal(0, r.Model.Arrivals);
        Assert.Equal(0, r.Changes);
    }

    [Fact]
    public void The_session_ending_ends_the_teleport()
    {
        var r = new Rig();
        r.Begin();
        r.Model.Receive(new Disconnected("gone"));
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Busy);
    }

    // ---------------- the give-up ----------------

    [Fact]
    public void A_teleport_the_grid_goes_quiet_about_is_given_up_after_fifteen_seconds_without_a_failure_and_no_message_restarts_the_clock()
    {
        var r = new Rig();
        r.Begin();
        r.Now += TimeSpan.FromSeconds(14);
        r.Model.Tick();
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
        r.Raise(TeleportPhase.Progress, "relaying");
        r.Now += TimeSpan.FromSeconds(2);
        r.Model.Tick();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
        Assert.False(r.Model.Busy);
        var idle = new Rig();
        idle.Now += TimeSpan.FromHours(1);
        idle.Model.Tick();                                      // nothing going on: nothing to give up
        Assert.Equal(TeleportStage.Idle, idle.Model.Stage);
    }

    // ---------------- offers ----------------

    [Fact]
    public void An_offer_is_kept_with_who_sent_it_and_what_they_wrote_and_nothing_is_sent_by_itself()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure, "come over");
        var offer = Assert.Single(r.Model.Offers);
        Assert.Equal(new TeleportOfferRow(Friend, "Friendly Resident", "come over", Lure), offer);
        Assert.Empty(r.Transport.Commands);                      // never accepted by itself
        Assert.Equal(0, r.Transport.ExecuteCalls);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Busy);
        r.Model.Tick();
        r.Now += TimeSpan.FromHours(5);
        r.Model.Tick();
        Assert.Empty(r.Transport.Commands);                      // nor by waiting
        Assert.Single(r.Model.Offers);
    }

    [Fact]
    public void A_second_offer_from_the_same_person_replaces_the_first_and_an_offer_from_nobody_is_ignored()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure, "first");
        r.Offer(Other, "Other Resident", Lure2, "other");
        r.Offer(Friend, "Friendly Resident", Lure2, "second");
        Assert.Equal(2, r.Model.Offers.Count);
        Assert.Equal("second", r.Model.Offers.Single(o => o.From == Friend).Message);
        Assert.Equal(Lure2, r.Model.Offers.Single(o => o.From == Friend).LureId);
        r.Offer(Guid.Empty, "Nobody", Lure, "x");
        Assert.Equal(2, r.Model.Offers.Count);
    }

    [Fact]
    public void An_offer_without_a_name_shows_the_cached_name_then_the_short_id()
    {
        var r = new Rig();
        r.Names.Learn(Friend, "Cached Name");
        r.Offer(Friend, "", Lure);
        Assert.Single(r.Model.Offers);
        Assert.Equal("Cached Name", r.Model.Offers[0].Name);
        r.Offer(Other, "", Lure2);
        Assert.Equal(NameCache.Placeholder(Other), r.Model.Offers.Single(o => o.From == Other).Name);
    }

    [Fact]
    public void Accepting_an_offer_sends_the_acceptance_once_starts_the_teleport_and_removes_the_offer()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure);
        Assert.True(r.Model.Accept(Friend));
        Assert.Equal(new TeleportLureAnswerCommand(Friend, Lure, true), Assert.Single(r.Transport.Commands));
        Assert.Empty(r.Model.Offers);
        Assert.Equal(TeleportStage.Requested, r.Model.Stage);
        Assert.False(r.Model.Accept(Friend));                    // an answered offer cannot be answered again
        Assert.Single(r.Transport.Commands);
    }

    [Fact]
    public void Accepting_while_another_teleport_goes_on_is_refused_and_the_offer_stays()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure);
        r.Begin();
        int sent = r.Transport.Commands.Count;
        Assert.False(r.Model.Accept(Friend));
        Assert.Equal(("status.teleport.busy", null), r.Model.Status);
        Assert.Equal(sent, r.Transport.Commands.Count);
        Assert.Single(r.Model.Offers);
        Assert.True(r.Model.Decline(Friend));                    // declining is not a teleport: allowed
        Assert.Equal(new TeleportLureAnswerCommand(Friend, Lure, false), r.Transport.Commands[^1]);
        Assert.Empty(r.Model.Offers);
    }

    [Fact]
    public void Declining_an_offer_sends_the_decline_once_removes_the_offer_and_starts_no_teleport()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure);
        r.Offer(Other, "Other Resident", Lure2);
        Assert.True(r.Model.Decline(Friend));
        Assert.Equal(new TeleportLureAnswerCommand(Friend, Lure, false), Assert.Single(r.Transport.Commands));
        Assert.Equal(Other, Assert.Single(r.Model.Offers).From);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.Decline(Friend));
        Assert.False(r.Model.Accept(Guid.NewGuid()));            // an offer nobody made
        Assert.Single(r.Transport.Commands);
    }

    [Fact]
    public void An_answer_that_did_not_leave_keeps_the_offer_open()
    {
        var r = new Rig();
        r.Offer(Friend, "Friendly Resident", Lure);
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        Assert.False(r.Model.Accept(Friend));
        Assert.Single(r.Model.Offers);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Equal("status.failed", r.Model.Status!.Value.Key);
    }

    [Fact]
    public void Offering_a_teleport_goes_through_the_pipeline_and_shows_a_refusal()
    {
        var r = new Rig();
        Assert.True(r.Model.OfferTo(Friend, " come to my place "));
        Assert.Equal(new TeleportOfferCommand(Friend, "come to my place"), Assert.Single(r.Transport.Commands));
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);          // offering a teleport is not teleporting
        Assert.False(r.Model.OfferTo(Guid.Empty, "x"));
        Assert.Equal("status.teleport.no_target", r.Model.Status!.Value.Key);
        Assert.False(r.Model.OfferTo(Friend, new string('a', 1024)));
        Assert.Equal("status.teleport.message_too_long", r.Model.Status!.Value.Key);
        Assert.False(r.Model.OfferTo(Self, "me"));                 // to oneself: nothing sent
        Assert.Equal(1, r.Transport.Commands.Count);
    }

    // ---------------- the words ----------------

    [Fact]
    public void Every_text_the_model_can_show_exists_in_the_string_table()
    {
        var en = StringTable.English();
        var keys = new List<string>
        {
            "teleport.progress.starting", "teleport.progress.requesting", "teleport.progress.raw", "teleport.error.raw", "teleport.error.timeout",
            "status.teleport.busy", "status.teleport.no_region", "status.teleport.region_too_long", "status.teleport.bad_region",
            "status.teleport.bad_position", "status.teleport.no_landmark", "status.teleport.no_target", "status.teleport.no_lure",
            "status.teleport.message_too_long", "status.teleport.message_control_chars",
        };
        keys.AddRange(TeleportModel.KnownProgress.Select(k => "teleport.progress." + k));
        keys.AddRange(TeleportModel.KnownErrors.Select(k => "teleport.error." + k));
        foreach (var key in keys)
            Assert.True(en.Has(key), key);
        Assert.NotEmpty(TeleportModel.KnownProgress);
    }
}

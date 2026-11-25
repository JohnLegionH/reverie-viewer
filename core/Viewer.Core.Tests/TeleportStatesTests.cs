// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The teleport progress view follows the official viewer's teleport states and timing: a teleport to another region shows the
/// progress until the agent is in place and a short arrival delay has passed; a teleport inside the region never shows it and
/// ignores a late "teleport started" for a short while; a failure closes the progress and shows its notice. Each test replays the
/// grid's messages in order against a test clock. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class TeleportStatesTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public readonly TeleportModel Model;

        public Rig(int attachments = 0)
        {
            var pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new TeleportModel(Self, new NameCache(), pipeline.Submit, () => Now, () => (Here.Handle, Here.Name), () => attachments);
        }

        public void Raise(TeleportPhase phase, string message = "", bool canCancel = true, string region = "") =>
            Model.Receive(new TeleportProgressed(phase, message, canCancel, region));

        public void Wait(double seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Model.Tick();
        }
    }

    [Fact]
    public void The_delays_are_the_official_viewers()
    {
        // TeleportLocalDelay 1 s, TeleportArrivalDelay 2 s (settings.xml); the give-up is TELEPORT_EXPIRY, 15 s, plus
        // TELEPORT_EXPIRY_PER_ATTACHMENT, 3 s, for each attachment worn (llviewerdisplay.cpp)
        Assert.Equal(TimeSpan.FromSeconds(1), TeleportModel.LocalDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), TeleportModel.ArrivalDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), TeleportModel.Expiry);
        Assert.Equal(TimeSpan.FromSeconds(3), TeleportModel.ExpiryPerAttachment);
    }

    [Fact]
    public void A_teleport_to_another_region_shows_the_progress_until_the_arrival_delay_after_the_agent_is_in_place()
    {
        var r = new Rig();
        Assert.True(r.Model.ToRegion("Far Region", new Vector3(10, 20, 30)));
        Assert.Equal(TeleportStage.Requested, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Started);
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Progress, "contacting");
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        r.Model.Receive(new MovementComplete(There));          // official: TELEPORT_START_ARRIVAL, then ARRIVING for the arrival delay
        Assert.Equal(1, r.Model.Arrivals);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        Assert.False(r.Model.CanCancel);                        // official: the Cancel button is hidden from the arrival on
        r.Wait(1.9);
        Assert.True(r.Model.ProgressVisible);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);       // official: TELEPORT_NONE
        Assert.False(r.Model.ProgressVisible);
        Assert.False(r.Model.Busy);
        Assert.Equal(1, r.Model.Arrivals);
    }

    [Fact]
    public void A_teleport_inside_the_region_never_shows_the_progress_and_ignores_a_late_start_for_the_local_delay()
    {
        var r = new Rig();
        Assert.True(r.Model.ToRegion("example region", new Vector3(10, 20, 30)));   // the region's own name, any case
        Assert.Equal(TeleportStage.Local, r.Model.Stage);       // official: TELEPORT_LOCAL, the progress is active but not shown
        Assert.False(r.Model.ProgressVisible);
        Assert.True(r.Model.Busy);                               // official: teleportCore refuses another teleport meanwhile
        Assert.False(r.Model.CanCancel);
        Assert.Null(r.Model.Progress);
        r.Raise(TeleportPhase.Started);                          // the grid's start message does not bring the progress up
        r.Raise(TeleportPhase.Progress, "contacting");
        Assert.Equal(TeleportStage.Local, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Local);                            // the agent is placed
        Assert.Equal(1, r.Model.Arrivals);
        Assert.False(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Started);                          // a start message after the finish (official SVC-5290 case)
        Assert.False(r.Model.ProgressVisible);
        r.Wait(0.9);
        Assert.Equal(TeleportStage.Local, r.Model.Stage);
        r.Raise(TeleportPhase.Started);
        Assert.False(r.Model.ProgressVisible);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);       // official: TELEPORT_NONE after TeleportLocalDelay
        Assert.False(r.Model.Busy);
        Assert.Equal(1, r.Model.Arrivals);
    }

    [Fact]
    public void A_teleport_by_handle_to_the_agents_own_region_is_a_teleport_inside_the_region()
    {
        var r = new Rig();
        Assert.True(r.Model.ToHandle(Here.Handle, new Vector3(1, 2, 3)));
        Assert.Equal(TeleportStage.Local, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Finished);                         // a finish with no new region is the local finish too
        Assert.Equal(1, r.Model.Arrivals);
        r.Wait(1.1);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void A_teleport_inside_the_region_the_grid_never_answers_is_dropped_silently_after_the_official_give_up()
    {
        var r = new Rig();
        Assert.True(r.Model.ToRegion("Example Region", new Vector3(10, 20, 30)));
        r.Wait(14.9);
        Assert.Equal(TeleportStage.Local, r.Model.Stage);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
        Assert.Equal(0, r.Model.Arrivals);
    }

    [Fact]
    public void A_teleport_inside_the_region_that_turns_out_to_move_to_a_new_region_shows_the_progress()
    {
        var r = new Rig();
        Assert.True(r.Model.ToRegion("Example Region", new Vector3(10, 20, 30)));
        r.Model.Receive(new SimConnected(There, 256, 256));      // the grid sent the agent elsewhere
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        r.Model.Receive(new MovementComplete(There));
        r.Wait(2.1);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void A_teleport_to_a_region_that_does_not_exist_closes_the_progress_and_shows_the_failure()
    {
        var r = new Rig();
        Assert.True(r.Model.ToRegion("Nowhere Region", new Vector3(10, 20, 30)));
        Assert.True(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Failed, "no_host");
        Assert.Equal(TeleportStage.Failed, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);                   // official: the progress is hidden, the failure is a notice
        Assert.Equal(("teleport.error.no_host", null), r.Model.Failure);
        Assert.False(r.Model.Busy);
        r.Model.Dismiss();
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void A_failed_teleport_inside_the_region_and_a_failure_during_the_arrival_close_the_progress()
    {
        var local = new Rig();
        Assert.True(local.Model.ToRegion("Example Region", new Vector3(10, 20, 30)));
        local.Raise(TeleportPhase.Failed, "blocked_tport");
        Assert.Equal(TeleportStage.Failed, local.Model.Stage);
        Assert.False(local.Model.ProgressVisible);

        var far = new Rig();
        Assert.True(far.Model.ToRegion("Far Region", new Vector3(10, 20, 30)));
        far.Raise(TeleportPhase.Started);
        far.Raise(TeleportPhase.Failed, "invalid_tport");
        Assert.False(far.Model.ProgressVisible);
        far.Wait(5);                                              // a late arrival or start does not bring the progress back
        far.Model.Receive(new SimConnected(There, 256, 256));
        Assert.False(far.Model.ProgressVisible);
        Assert.Equal(TeleportStage.Failed, far.Model.Stage);
    }

    [Fact]
    public void A_local_finish_for_a_teleport_not_asked_inside_the_region_closes_the_progress_at_once_and_holds_off_a_late_start()
    {
        var r = new Rig();
        Assert.True(r.Model.Home());                             // go home: not a teleport inside the region as far as the viewer knows
        Assert.True(r.Model.ProgressVisible);
        r.Raise(TeleportPhase.Started);
        r.Raise(TeleportPhase.Local);                            // but the grid says it was inside the region
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);       // official: the state goes to TELEPORT_NONE at once
        Assert.False(r.Model.ProgressVisible);
        Assert.Equal(1, r.Model.Arrivals);
        r.Raise(TeleportPhase.Started);                          // a late start must not leave the progress stuck on screen
        Assert.False(r.Model.ProgressVisible);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        r.Wait(1.1);
        r.Raise(TeleportPhase.Started);                          // after the delay a start is a new teleport again
        Assert.True(r.Model.ProgressVisible);
    }

    // The sequence the grid gave in the live check of a teleport to the neighbouring region: the teleport started, progress, the new
    // region connected, the grid's finish, and the agent's movement message, which names a region reference other than the one the
    // connect named. The box has to close two seconds after the movement message, not at a time-out.
    [Fact]
    public void The_recorded_cross_region_sequence_closes_the_progress_two_seconds_after_the_movement_message()
    {
        var r = new Rig();
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Wait(0.2);
        r.Raise(TeleportPhase.Progress, "contacting");
        r.Wait(1.9);
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Model.Receive(new MovementComplete(new SimRef(There.Handle, There.Name)));   // same region, another reference
        Assert.Equal(1, r.Model.Arrivals);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.False(r.Model.CanCancel);
        r.Wait(1.9);
        Assert.True(r.Model.ProgressVisible);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);
        Assert.Null(r.Model.Failure);
    }

    [Fact]
    public void The_movement_message_may_come_before_the_connect_and_before_the_finish_with_any_region_reference()
    {
        var r = new Rig();
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new MovementComplete(new SimRef(There.Handle, There.Name)));
        Assert.Equal(0, r.Model.Arrivals);                        // not in place until the new region is known
        r.Model.Receive(new SimConnected(There, 256, 256));
        Assert.Equal(1, r.Model.Arrivals);
        r.Raise(TeleportPhase.Finished);
        Assert.Equal(1, r.Model.Arrivals);
        r.Wait(2.1);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void An_arrival_whose_movement_message_never_comes_is_not_ended_by_a_grace_period_only_by_the_give_up()
    {
        var r = new Rig();
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Wait(14.9);
        Assert.Equal(TeleportStage.Arriving, r.Model.Stage);
        Assert.True(r.Model.ProgressVisible);
        Assert.Equal(0, r.Model.Arrivals);                        // no grace made it an arrival
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);          // official: Giving up, TELEPORT_NONE and the message cleared
        Assert.Null(r.Model.Failure);
        Assert.Null(r.Model.Progress);
        Assert.False(r.Model.Busy);
    }

    [Fact]
    public void The_give_up_comes_fifteen_seconds_after_the_start_with_no_attachments_and_the_progress_just_closes()
    {
        var r = new Rig(attachments: 0);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Wait(14.9);
        Assert.True(r.Model.ProgressVisible);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);
        Assert.Null(r.Model.Failure);
        Assert.Null(r.Model.Progress);
    }

    [Fact]
    public void The_give_up_is_three_seconds_longer_for_each_attachment_worn()
    {
        var r = new Rig(attachments: 20);                         // 15 s + 20 x 3 s = 75 s
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Wait(74.9);
        Assert.True(r.Model.ProgressVisible);
        Assert.Equal(TeleportStage.Active, r.Model.Stage);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.Null(r.Model.Failure);
    }

    [Fact]
    public void A_progress_message_does_not_restart_the_give_up_clock()
    {
        var r = new Rig();
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Wait(10);
        r.Raise(TeleportPhase.Progress, "contacting");
        r.Wait(4.9);
        Assert.True(r.Model.ProgressVisible);
        r.Wait(0.2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);          // the clock runs from the start of the teleport (gTeleportDisplayTimer)
    }

    [Fact]
    public void A_movement_message_with_no_teleport_going_on_is_a_login_or_a_region_crossing_and_leaves_the_state_at_none()
    {
        var r = new Rig();
        r.Model.Receive(new MovementComplete(Here));              // login or a crossing (official: state not MOVING, TELEPORT_NONE)
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Model.Receive(new MovementComplete(There));
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        Assert.False(r.Model.ProgressVisible);
        Assert.False(r.Model.Busy);
        Assert.Equal(0, r.Model.Arrivals);
        Assert.True(r.Model.ToRegion("Far Region", new Vector3(1, 2, 3)));   // and the next teleport starts clean
        Assert.Equal(TeleportStage.Requested, r.Model.Stage);
        r.Wait(0.5);
        Assert.Equal(0, r.Model.Arrivals);
    }

    // ---------------- every teleport is an arrival, once ----------------

    private static readonly SimRef Third = new(300001UL * 256 << 32 | 300000UL * 256, "Third Region");

    private static int[] Counting(Rig r)
    {
        var heard = new int[1];
        r.Model.Arrived += () => heard[0]++;
        return heard;
    }

    private static void CrossRegion(Rig r, SimRef to)
    {
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new SimConnected(to, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Model.Receive(new MovementComplete(to));
    }

    [Fact]
    public void A_teleport_inside_the_region_that_the_grid_makes_while_the_one_before_is_arriving_is_an_arrival_too()
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        CrossRegion(r, There);
        r.Wait(0.5);
        r.Raise(TeleportPhase.Started);
        r.Raise(TeleportPhase.Local);
        Assert.Equal(2, heard[0]);
        Assert.Equal(2, r.Model.Arrivals);
        r.Wait(2);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);          // what is shown is as before: the first one's progress closes on time
    }

    [Fact]
    public void A_teleport_to_another_region_that_the_grid_makes_while_the_one_before_is_arriving_is_an_arrival_too()
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        CrossRegion(r, There);
        r.Wait(0.5);
        CrossRegion(r, Third);
        Assert.Equal(2, heard[0]);
    }

    [Fact]
    public void A_teleport_the_grid_makes_within_the_local_delay_is_an_arrival_too()
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(Here.Handle, new Vector3(10, 10, 30)));
        r.Raise(TeleportPhase.Local);
        r.Wait(0.3);
        r.Raise(TeleportPhase.Started);
        r.Raise(TeleportPhase.Local);
        Assert.Equal(2, heard[0]);

        var closed = new Rig();                                   // the same after one that closed the progress at once
        var again = Counting(closed);
        Assert.True(closed.Model.Home());
        closed.Raise(TeleportPhase.Local);
        closed.Wait(0.3);
        closed.Raise(TeleportPhase.Local);
        Assert.Equal(2, again[0]);
        Assert.False(closed.Model.ProgressVisible);
    }

    [Fact]
    public void A_move_inside_the_region_the_grid_makes_without_a_start_message_is_an_arrival()
    {
        var r = new Rig();
        var heard = Counting(r);
        r.Raise(TeleportPhase.Local);
        Assert.Equal(1, heard[0]);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void A_teleport_that_ends_after_the_viewer_gave_up_on_it_is_an_arrival_once()
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Wait(15.1);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Model.Receive(new MovementComplete(There));
        Assert.Equal(1, heard[0]);
        Assert.Equal(TeleportStage.Idle, r.Model.Stage);
    }

    [Fact]
    public void A_teleport_the_grid_finished_whose_movement_message_comes_after_the_give_up_is_an_arrival_once()
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Wait(15.1);
        Assert.Equal(0, heard[0]);                                // giving up alone is silent
        r.Model.Receive(new MovementComplete(There));
        r.Wait(5);
        r.Model.Receive(new MovementComplete(There));             // a later one (a crossing) is not a teleport
        Assert.Equal(1, heard[0]);
    }

    [Theory]
    [InlineData("later")]
    [InlineData("failed")]
    public void A_movement_message_long_after_a_given_up_teleport_or_after_its_failure_is_not_an_arrival(string how)
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new SimConnected(There, 256, 256));
        r.Raise(TeleportPhase.Finished);
        r.Wait(15.1);
        if (how == "later")
            r.Wait(15.1);
        else
            r.Raise(TeleportPhase.Failed, "timeout_tport");
        r.Model.Receive(new MovementComplete(There));
        Assert.Equal(0, heard[0]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    public void A_finish_that_comes_after_the_agent_is_already_in_place_is_the_same_teleport(double later)
    {
        var r = new Rig();
        var heard = Counting(r);
        Assert.True(r.Model.ToHandle(There.Handle, new Vector3(128, 128, 30)));
        r.Raise(TeleportPhase.Started);
        r.Model.Receive(new MovementComplete(There));
        r.Model.Receive(new SimConnected(There, 256, 256));
        Assert.Equal(1, heard[0]);
        r.Wait(later);
        r.Raise(TeleportPhase.Finished);
        Assert.Equal(1, heard[0]);
        r.Raise(TeleportPhase.Started);                           // and the next one counts again
        r.Raise(TeleportPhase.Local);
        Assert.Equal(2, heard[0]);
    }
}

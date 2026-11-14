// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle: the gateway's SelfPositionForwardTests (web-viewer bb91ba5, W-30), all four, adapted from the wire body
/// to the core's self-motion samples (sim position, velocity, arrival stamp, movement flags). One event produces
/// one sample stamped with the event's own arrival time; an event that changes nothing produces nothing.
/// </summary>
public class SelfPositionForwardTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Quaternion Facing = Quaternion.Identity;
    private static readonly MovementFlags Walking = new(true, false, false, false);

    private static SelfMotion M(Vector3 pos, Vector3 vel, double arrivalMs = 1, Quaternion? rot = null) =>
        new(pos, rot ?? Facing, vel, Vector3.Zero, Vector3.Zero, arrivalMs, Walking);

    [Fact]
    public void A_terse_update_for_self_produces_one_position_stamped_with_the_event_time()
    {
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        var samples = new List<SelfMotionSample>();
        world.SelfMoved += samples.Add;
        // The arrival time is the EVENT's (stamped by the adapter on the packet thread), not when anyone read it.
        const double arrivedMs = 1234.5;

        world.ApplySelfMotion(Sim, new SelfMotion(new Vector3(128, 128, 26), Facing, new Vector3(2.455f, 0.104f, 0),
            new Vector3(0, 0, 0.5f), new Vector3(0.1f, 0, 0), arrivedMs, Walking));

        var s = Assert.Single(samples);
        Assert.Equal(world.Epoch, s.Epoch);
        Assert.Equal(128, s.Motion.Position.X);
        Assert.Equal(2.455f, s.Motion.Velocity.X);
        Assert.Equal(0.5f, s.Motion.AngularVelocity.Z);
        Assert.Equal(0.1f, s.Motion.Acceleration.X);
        Assert.Equal(arrivedMs, s.Motion.ArrivalMs);
        Assert.Equal(Walking, s.Motion.Movement);
        Assert.Same(s, world.LastSelfMotion);
    }

    [Fact]
    public void An_update_that_changes_nothing_sends_nothing()
    {
        var gate = new PositionGate();
        var pos = new Vector3(128, 128, 26);
        var vel = Vector3.Zero;

        Assert.True(gate.ShouldPass(pos, Facing, vel));    // first sample always goes
        Assert.False(gate.ShouldPass(pos, Facing, vel));   // a standing avatar re-reported: silence
        Assert.False(gate.ShouldPass(pos, Facing, vel));

        Assert.True(gate.ShouldPass(new Vector3(128.5f, 128, 26), Facing, vel));
        Assert.True(gate.ShouldPass(new Vector3(128.5f, 128, 26), Facing, new Vector3(2.455f, 0, 0)));
        Assert.False(gate.ShouldPass(new Vector3(128.5f, 128, 26), Facing, new Vector3(2.455f, 0, 0)));

        // A rotation alone is a change: turning on the spot moves nothing and still has to reach the consumer.
        Assert.True(gate.ShouldPass(new Vector3(128.5f, 128, 26), new Quaternion(0, 0, 0.7071f, 0.7071f), new Vector3(2.455f, 0, 0)));

        // The same rule inside the world state: a repeated sample raises nothing.
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        int raised = 0;
        world.SelfMoved += _ => raised++;
        world.ApplySelfMotion(Sim, M(pos, vel, 1));
        world.ApplySelfMotion(Sim, M(pos, vel, 2));
        Assert.Equal(1, raised);
        Assert.Equal(1, world.LastSelfMotion!.Motion.ArrivalMs);
    }

    [Fact]
    public void Reset_forces_the_next_sample_through()
    {
        var gate = new PositionGate();
        var pos = new Vector3(10, 10, 20);
        Assert.True(gate.ShouldPass(pos, Facing, Vector3.Zero));
        Assert.False(gate.ShouldPass(pos, Facing, Vector3.Zero));
        gate.Reset();   // teleport, region crossing, new consumer
        Assert.True(gate.ShouldPass(pos, Facing, Vector3.Zero));

        // In the world state: an explicit reset (new consumer), and a new region epoch, each let the same sample through.
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        int raised = 0;
        world.SelfMoved += _ => raised++;
        world.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
        world.ApplySelfMotion(Sim, M(pos, Vector3.Zero));
        world.ApplySelfMotion(Sim, M(pos, Vector3.Zero));
        world.ResetSelfMotionGate();
        world.ApplySelfMotion(Sim, M(pos, Vector3.Zero));
        Assert.Equal(2, raised);

        var there = new SimRef(256256UL << 32 | 256000UL, "There");
        world.BeginRegion(there, new RegionInfo("There", 1001, 1000, 256, 256, false));
        Assert.Null(world.LastSelfMotion);   // the previous region's sample went with its epoch
        world.ApplySelfMotion(there, M(pos, Vector3.Zero));
        Assert.Equal(3, raised);
        Assert.Equal(world.Epoch, world.LastSelfMotion!.Epoch);
    }

    [Fact]
    public void The_movement_flags_snapshot_says_what_the_agent_was_told_to_do()
    {
        using var client = new LibreMetaverse.GridClient();   // in-process, no network
        var mv = client.Self.Movement;

        mv.AtPos = true;
        mv.AtNeg = false;
        mv.AlwaysRun = false;
        mv.Fly = false;
        var walking = LmvTransport.MovementOf(mv);
        Assert.True(walking.Forward);
        Assert.False(walking.Back);
        Assert.False(walking.Run);
        Assert.False(walking.Fly);

        // Run is either of the two ways the sim can be running us: the toggle, or a double-tapped forward key.
        mv.AlwaysRun = true;
        Assert.True(LmvTransport.MovementOf(mv).Run);
        mv.AlwaysRun = false;
        mv.FastAt = true;
        Assert.True(LmvTransport.MovementOf(mv).Run);

        mv.AtPos = false;
        mv.AtNeg = true;
        mv.Fly = true;
        var backFlying = LmvTransport.MovementOf(mv);
        Assert.False(backFlying.Forward);
        Assert.True(backFlying.Back);
        Assert.True(backFlying.Fly);
    }
}

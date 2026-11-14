// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Objects that are not avatars between the region's updates, against the official viewer's rules (llviewerobject.cpp idleUpdate,
/// interpolateLinearMotion and applyAngularVelocity; see Viewer.Motion's NOTICE). Updates are replayed into a world state and the
/// frames are stepped on the test's own clock. Each test builds its own world: no process-wide state, so the class runs in parallel
/// with the others.
/// </summary>
public class ObjectInterpolationTests
{
    private const double Hz = 60;
    private const uint Local = 41;
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region256 = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Vector3 Start = new(10, 20, 30);

    private static PrimData Prim(Vector3 position, Vector3 velocity = default, Vector3 acceleration = default, Vector3 spin = default) =>
        new(Local, new Guid((int)Local, 0, 0, new byte[8]), 0, 0, position, Quaternion.Identity, Vector3.One, velocity, spin, acceleration);

    /// <summary>A region with one object, received in full at time 0.</summary>
    private static (WorldState World, ObjectInterpolator Moving) Scene(PrimData prim)
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Sim, Region256);
        w.ApplyObject(Sim, prim);
        var m = new ObjectInterpolator(w);
        m.Note(new ObjectId(w.Epoch, Local), full: true, now: 0);
        return (w, m);
    }

    /// <summary>Steps the frames from <paramref name="from"/> to <paramref name="to"/> seconds and gives where the object is drawn then.</summary>
    private static (Vector3 Position, Quaternion Rotation) Run(ObjectInterpolator m, double from, double to)
    {
        int first = (int)Math.Round(from * Hz) + 1, last = (int)Math.Round(to * Hz);
        for (int i = first; i <= last; i++) m.Frame(i / Hz);
        Assert.True(m.TryPlace(Local, out var p, out var r), "the object is not carried between updates");
        return (p, r);
    }

    private static float AngleBetween(Quaternion a, Quaternion b) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0f, 1f));

    [Fact]
    public void A_moving_object_is_carried_by_its_velocity_between_updates_and_an_update_sets_it_at_once()
    {
        var (w, m) = Scene(Prim(Start, velocity: new Vector3(2, 0, 0)));
        var at1 = Run(m, 0, 1).Position;
        Assert.InRange(at1.X - Start.X, 1.99f, 2.01f);
        Assert.Equal(Start.Y, at1.Y, 3);
        // while the circuit is alive the motion is not tapered, however long no update comes
        var at6 = Run(m, 1, 6).Position;
        Assert.InRange(at6.X - Start.X, 11.95f, 12.05f);

        w.ApplyMove(Sim, Local, false, new Vector3(50, 20, 30), Quaternion.Identity, new Vector3(0, 1, 0));
        m.Note(new ObjectId(w.Epoch, Local), full: false, now: 6);
        Assert.True(m.TryPlace(Local, out var set, out _));
        Assert.Equal(new Vector3(50, 20, 30), set);
        var after = Run(m, 6, 6.5).Position;
        Assert.InRange(after.Y - 20, 0.49f, 0.51f);
        Assert.Equal(50f, after.X, 3);
    }

    [Fact]
    public void An_accelerating_object_is_carried_by_its_velocity_and_acceleration()
    {
        var (_, m) = Scene(Prim(Start, acceleration: new Vector3(0, 0, 1)));
        float rise = Run(m, 0, 1).Position.Z - Start.Z;
        // half a metre in a second, less the official correction of one physics step (1/45 s) in each frame's velocity
        Assert.InRange(rise, 0.47f, 0.50f);
    }

    [Fact]
    public void A_moving_object_stops_being_predicted_three_seconds_after_its_last_update_when_the_circuit_is_quiet()
    {
        var (_, m) = Scene(Prim(Start, velocity: new Vector3(2, 0, 0)));
        m.CircuitQuietSeconds = 10;
        float at2 = Run(m, 0, 2).Position.X;
        Assert.InRange(at2 - Start.X, 3.99f, 4.01f);   // full speed for the first two seconds
        float at3 = Run(m, 2, 3).Position.X;
        float at6 = Run(m, 3, 6).Position.X;
        Assert.True(at6 - at3 < 0.001f, $"still predicted after three seconds: {at6 - at3:F4} m");
        Assert.True(at3 > at2, "nothing was carried between two and three seconds");
    }

    [Fact]
    public void Between_two_and_three_seconds_the_carried_step_tapers_off()
    {
        var (_, m) = Scene(Prim(Start, velocity: new Vector3(2, 0, 0)));
        m.CircuitQuietSeconds = 10;
        double dt = 1 / Hz;
        float before = Run(m, 0, 2.5).Position.X;
        float after = Run(m, 2.5, 2.5 + dt).Position.X;
        // the official factor once a step has been tapered: (3 - time since the update) / (3 - time since the last frame)
        double sinceUpdate = 2.5 + dt;
        double expected = 2 * dt * (3 - sinceUpdate) / (3 - dt);
        Assert.InRange(after - before, (float)(expected * 0.9), (float)(expected * 1.1));
        // and over the whole taper the object goes on less than it would at full speed, but does go on
        float at2 = Run(Scene(Prim(Start, velocity: new Vector3(2, 0, 0))).Moving, 0, 2).Position.X;
        float at3 = Run(m, 2.5 + dt, 3).Position.X;
        Assert.InRange(at3 - at2, 0.2f, 0.6f);
    }

    [Fact]
    public void An_object_predicted_into_a_neighbouring_region_is_carried_for_one_second_more_and_then_stops()
    {
        var (_, m) = Scene(Prim(new Vector3(250, 20, 30), velocity: new Vector3(10, 0, 0)));
        m.HasRegionAt = _ => true;
        // it leaves the region 0.6 s in; one second after the first frame that predicts it outside, it is stopped
        float at3 = Run(m, 0, 3).Position.X;
        Assert.InRange(at3, 265.5f, 267f);
        float at5 = Run(m, 3, 5).Position.X;
        Assert.Equal(at3, at5, 3);
    }

    [Fact]
    public void An_object_predicted_off_the_edge_where_there_is_no_region_stops_at_the_edge()
    {
        var (_, m) = Scene(Prim(new Vector3(250, 20, 30), velocity: new Vector3(10, 0, 0)));
        var at2 = Run(m, 0, 2).Position;
        Assert.InRange(at2.X, 255.9f, 256f);
        Assert.Equal(20f, at2.Y, 2);
        Assert.Equal(at2.X, Run(m, 2, 4).Position.X, 3);
    }

    [Fact]
    public void A_spinning_object_turns_about_its_axis_at_its_rate()
    {
        var (w, m) = Scene(Prim(Start, spin: new Vector3(0, 0, 1.5f)));
        var (position, rotation) = Run(m, 0, 2);
        Assert.Equal(Start, position);
        Assert.InRange(AngleBetween(rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 3f)), 0f, 0.01f);
        Assert.InRange(AngleBetween(rotation, Quaternion.Identity), 2.9f, 3.1f);

        // a terse update that only stops the spin leaves the object turned as far as it got (the official viewer keeps the turn it added)
        w.ApplyMove(Sim, Local, false, Start, Quaternion.Identity, Vector3.Zero);
        m.Note(new ObjectId(w.Epoch, Local), full: false, now: 2);
        var stopped = Run(m, 2, 3).Rotation;
        Assert.InRange(AngleBetween(stopped, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 3f)), 0f, 0.01f);

        // an update with a new rotation sets it, and the turn starts again from there
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        w.ApplyMove(Sim, Local, false, Start, turned, Vector3.Zero, default, new Vector3(0, 0, 1.5f));
        m.Note(new ObjectId(w.Epoch, Local), full: false, now: 3);
        Assert.True(m.TryPlace(Local, out _, out var set));
        Assert.InRange(AngleBetween(set, turned), 0f, 0.001f);
        var again = Run(m, 3, 4).Rotation;
        Assert.InRange(AngleBetween(again, turned), 1.49f, 1.51f);
    }

    [Fact]
    public void The_regions_time_dilation_scales_the_carried_step_and_the_spin()
    {
        var (w, m) = Scene(Prim(Start, velocity: new Vector3(2, 0, 0), spin: new Vector3(0, 0, 1f)));
        w.SetTimeDilation(Sim, 0.5f);
        Assert.Equal(0.5f, w.TimeDilation);
        var (position, rotation) = Run(m, 0, 2);
        Assert.InRange(position.X - Start.X, 1.99f, 2.01f);   // two seconds at half time: 2 m, not 4
        Assert.InRange(AngleBetween(rotation, Quaternion.Identity), 0.99f, 1.01f);
    }

    [Fact]
    public void An_avatar_sitting_on_a_moving_object_is_drawn_where_the_object_is_carried_to()
    {
        var (w, m) = Scene(Prim(Start, velocity: new Vector3(2, 0, 0)));
        double now = 0;
        var scene = new AvatarScene(w, Guid.NewGuid(), () => now) { Carried = m };
        w.ApplyAvatar(Sim, new AvatarData(7, new Guid(7, 0, 0, new byte[8]), "Test User", new Vector3(0, 0, 1), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), ParentLocalId: Local));
        scene.Apply(new WorldChange(WorldChangeKind.AvatarAdded, new ObjectId(w.Epoch, 7)));
        var seated = scene.ByLocalId(7);
        Assert.NotNull(seated);
        Run(m, 0, 1);
        now = 1;
        Assert.True(scene.Place(seated!, out var soles, out _));
        Assert.InRange(soles.X - Start.X, 1.95f, 2.05f);   // the seat has gone 2 m on since its last update, and the avatar with it
    }

    [Fact]
    public void An_object_at_rest_is_not_carried_and_clearing_forgets_the_moving_ones()
    {
        var (_, m) = Scene(Prim(Start));
        m.Frame(1);
        Assert.Equal(0, m.Count);
        var moving = Scene(Prim(Start, velocity: new Vector3(1, 0, 0))).Moving;
        Assert.Equal(1, moving.Count);
        moving.Clear();
        Assert.Equal(0, moving.Count);
    }
}

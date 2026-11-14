// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// How avatars stop and how fast they are drawn, against update sequences shaped like a region's, without the engine.
/// A small region model steps every 1/11 s (OpenSim's default frame rate) and sends an avatar's motion as OpenSim's
/// ScenePresence.Update does: when its velocity changes by more than 0.1 m/s, when it stops, or when it has gone 5 m
/// since the last update it sent; and, as live captures show, now and then for other reasons (a turn), 0.3 to 1.4 s
/// apart. Each update reaches the viewer a network trip later, and the viewer draws at 60 frames a second.
/// For the user's own avatar the model also takes the controls the viewer sends: a held move key moves the avatar; a
/// release with no stop bit keeps it moving for a quarter of a second more (OpenSim's delayed stop, ScenePresence's
/// HandleAgentUpdate), a release with the stop bit stops it at the next frame. The viewer sends no stop bit for a released key.
/// </summary>
public class MotionStopTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Other = Guid.Parse("5b0e7a52-3c41-4d8e-9f16-2a7c0d9e4b13");
    private const double Tick = 1.0 / 11, Frame = 1.0 / 60;
    private const uint SelfLocal = 800, OtherLocal = 900;

    private sealed class Clock { public double Now; }

    private sealed record Packet(double Arrives, Vector3 Position, Vector3 Velocity, bool First);

    /// <summary>The region's side: one avatar's motion stepped each frame and sent as OpenSim sends it.</summary>
    private sealed class Region(Random rng, double latency, double jitter, int lateEvery = 0, double lateBy = 0)
    {
        public Vector3 Position = new(100, 100, 25);
        public Vector3 Velocity;
        private Vector3 _sentPosition, _sentVelocity;
        private double _nextExtra = 0.3 + rng.NextDouble() * 1.1, _lastArrival;
        private int _sent;
        public readonly Queue<Packet> Wire = new();

        public void Step(double now)
        {
            Position += Velocity * (float)Tick;
            bool send = _sent == 0
                || Vector3.Distance(Velocity, _sentVelocity) > 0.1f
                || (Velocity == Vector3.Zero && _sentVelocity != Vector3.Zero)
                || Vector3.Distance(Position, _sentPosition) > 5f;
            if (now >= _nextExtra)
            {
                send = true;
                _nextExtra = now + 0.3 + rng.NextDouble() * 1.1;
            }
            if (!send) return;
            double arrives = now + latency + rng.NextDouble() * jitter;
            if (lateEvery > 0 && _sent % lateEvery == lateEvery - 1) arrives += lateBy;
            arrives = Math.Max(arrives, _lastArrival);   // a packet never overtakes an earlier one
            _lastArrival = arrives;
            Wire.Enqueue(new Packet(arrives, Position, Velocity, _sent == 0));
            _sentPosition = Position;
            _sentVelocity = Velocity;
            _sent++;
        }
    }

    private static AvatarData Av(uint local, Guid agent, Vector3 pos, Vector3 vel) =>
        new(local, agent, "Test User", pos, Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), vel, 0);

    private static (FakeTransport T, AvatarScene Scene, Clock Clock, List<WorldChange> Changes, WorldState World) Setup()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        return (t, scene, clock, changes, s.World);
    }

    private static void Deliver(FakeTransport t, AvatarScene scene, List<WorldChange> changes, Region region, uint local, Guid agent, double now)
    {
        while (region.Wire.TryPeek(out var p) && p.Arrives <= now)
        {
            region.Wire.Dequeue();
            if (p.First) t.Raise(new AvatarUpdated(Here, Av(local, agent, p.Position, p.Velocity)));
            else t.Raise(new ObjectMoved(Here, local, true, p.Position, Quaternion.Identity, p.Velocity, Vector3.Zero, Vector3.Zero));
        }
        foreach (var c in changes.ToList()) scene.Apply(c);
        changes.Clear();
    }

    /// <summary>How far past the stop point the drawn avatar went, and how far it then came back.</summary>
    private static (float Beyond, float Back, Vector3 Final) StopFigures(List<(double At, Vector3 Soles)> drawn, double from, Vector3 stopSoles, Vector3 dir)
    {
        float most = float.MinValue, last = 0;
        foreach (var (at, soles) in drawn.Where(d => d.At >= from))
        {
            float along = Vector3.Dot(soles - stopSoles, dir);
            most = MathF.Max(most, along);
            last = along;
        }
        return (MathF.Max(0, most), most - last, drawn[^1].Soles);
    }

    // ---------------- other avatars: the stop ----------------

    [Theory]
    [InlineData(2.46f, 0.02, 0.03, 3)]    // the region's walk without the fast bit
    [InlineData(4.1f, 0.04, 0.03, 5)]     // its walk with the fast bit
    [InlineData(9.83f, 0.04, 0.03, 7)]    // its flying speed
    [InlineData(4.1f, 0.06, 0.04, 9)]     // a slower network
    public void Another_avatar_that_stops_is_not_drawn_past_its_stop_and_does_not_slide_back(float speed, double latency, double jitter, int seed)
    {
        var (t, scene, clock, changes, _) = Setup();
        var region = new Region(new Random(seed), latency, jitter);
        var dir = Vector3.UnitX;
        var drawn = new List<(double, Vector3)>();
        double regionAt = 0, startAt = 0.5, stopAt = 4.0 + seed * 0.013;
        Vector3 stop = default;
        bool stopped = false;
        for (int frame = 0; frame < 6 * 60; frame++)
        {
            double now = frame * Frame;
            clock.Now = now;
            while (regionAt <= now)
            {
                if (regionAt >= startAt && !stopped) region.Velocity = dir * speed;
                if (regionAt >= stopAt && !stopped) { region.Velocity = Vector3.Zero; stopped = true; region.Step(regionAt); stop = region.Position; }
                else region.Step(regionAt);
                regionAt += Tick;
            }
            Deliver(t, scene, changes, region, OtherLocal, Other, now);
            var e = scene.Entries.SingleOrDefault(x => !x.IsSelf);
            if (e is not null && scene.Place(e, out var soles, out _)) drawn.Add((now, soles));
        }
        var stopSoles = stop - new Vector3(0, 0, 0.95f);
        var (beyond, back, final) = StopFigures(drawn, stopAt - 1, stopSoles, dir);
        // the drawn avatar may come to rest no more than 10 cm past where the region stopped it, and never come back
        // more than 5 cm (a slide back is what shows); at the end it is where the region put it
        Assert.True(beyond <= 0.10f, $"at {speed} m/s the drawn avatar went {beyond:F3} m past its stop");
        Assert.True(back <= 0.05f, $"at {speed} m/s the drawn avatar slid back {back:F3} m after its stop");
        Assert.True(Vector3.Distance(final, stopSoles) < 0.01f, $"at rest {Vector3.Distance(final, stopSoles):F3} m from the region's position");
    }

    // ---------------- other avatars: drawn speed ----------------

    /// <summary>
    /// Another avatar flying at 9.83 m/s: the region sends an update every 5 m (about 0.5 s); every fifth packet arrives 0.2 s late (as one did in a live
    /// capture), and the ones behind it arrive as soon as they can. The official viewer takes each where it says when it arrives (so a late one puts the
    /// avatar back and the next one forward); this viewer must draw it where the official viewer does, in every frame.
    /// </summary>
    [Fact]
    public void A_flying_avatar_whose_updates_arrive_late_is_drawn_as_the_official_viewer_draws_it()
    {
        const float speed = 9.83f;
        var (t, scene, clock, changes, _) = Setup();
        var region = new Region(new Random(21), 0.03, 0.02, lateEvery: 5, lateBy: 0.2);
        var official = new OfficialOwnAvatarReplay();
        double regionAt = 0, worst = 0, last = double.NaN;
        string where = "";
        for (int frame = 0; frame < 10 * 60; frame++)
        {
            double now = frame * Frame, dt = double.IsNaN(last) ? Frame : now - last;
            last = now;
            clock.Now = now;
            while (regionAt <= now)
            {
                if (regionAt >= 0.5) region.Velocity = new Vector3(-speed, 0, 0);
                region.Step(regionAt);
                regionAt += Tick;
            }
            foreach (var p in region.Wire.TakeWhile(p => p.Arrives <= now)) official.Statement(p.Position, p.Velocity, Vector3.Zero, 4096, now, dt, 1f);
            Deliver(t, scene, changes, region, OtherLocal, Other, now);
            var e = scene.Entries.SingleOrDefault(x => !x.IsSelf);
            if (e is null || !scene.Place(e, out var soles, out _)) continue;
            var drawnOfficial = official.Frame(now, dt, 1f, _ => true);
            float gap = new Vector2(soles.X - drawnOfficial.X, soles.Y - drawnOfficial.Y).Length();
            if (gap > worst) { worst = gap; where = $"{now:0.000} s: drawn x {soles.X:0.000}, official {drawnOfficial.X:0.000}"; }
        }
        Assert.True(worst <= 0.1, $"drawn {worst:0.000} m from the official viewer ({where})");
        Assert.Equal(1, scene.Entries.Single().Motion.Snaps);   // only the first placement was taken at once
    }

    // ---------------- the motion trace ----------------

    [Fact]
    public void The_motion_trace_records_each_placed_avatar_only_while_started_and_hands_each_sample_over_once()
    {
        var (t, scene, clock, changes, world) = Setup();
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new(110, 100, 25), new(2, 0, 0))));
        foreach (var c in changes) scene.Apply(c);
        changes.Clear();
        var trace = new MotionTrace();
        trace.Sample(scene, world, 0);
        Assert.Empty(trace.Take());   // off until started

        trace.Start();
        clock.Now = 0.5;
        trace.Sample(scene, world, 0.5);
        var s = Assert.Single(trace.Take());
        Assert.Equal(OtherLocal, s.LocalId);
        Assert.False(s.Self);
        Assert.Equal(new Vector3(110, 100, 25 - 0.95f), s.Stated);   // the stated centre as soles, as the drawn one
        Assert.True(scene.Place(scene.Entries.Single(), out var soles, out _));
        Assert.Equal(soles, s.Drawn);
        Assert.Equal(new Vector3(2, 0, 0), s.Velocity);
        Assert.Equal(1, s.Updates);
        Assert.Empty(trace.Take());   // each sample is handed over once

        trace.Stop();
        trace.Sample(scene, world, 0.6);
        Assert.Empty(trace.Take());
        Assert.Equal(0, trace.Dropped);
    }

    [Fact]
    public void A_motion_sample_carries_the_time_its_avatar_was_placed_at_as_well_as_when_it_was_written()
    {
        var (t, scene, clock, changes, world) = Setup();
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new(110, 100, 25), new(2, 0, 0))));
        foreach (var c in changes) scene.Apply(c);
        var trace = new MotionTrace();
        trace.Start();
        clock.Now = 0.5;   // the scene's clock, which the motion is placed on
        trace.Sample(scene, world, 0.523);   // written a little later in the frame, on another clock
        var s = Assert.Single(trace.Take());
        Assert.Equal(0.523, s.At);
        Assert.Equal(0.5, s.Placed);
    }

    // ---------------- the user's own avatar ----------------

    [Fact]
    public void The_own_avatar_coasts_after_its_key_is_released_and_follows_the_regions_updates_to_its_stop()
    {
        var (t, scene, clock, changes, world) = Setup();
        var region = new Region(new Random(31), 0.02, 0.02);
        var toRegion = new Queue<(double Arrives, uint Controls)>();
        var sent = new List<(double At, uint Controls)>();
        var driver = new OwnAvatarDriver(scene, world, u => { toRegion.Enqueue((clock.Now + 0.03, u.Controls)); sent.Add((clock.Now, u.Controls)); return true; });
        uint controls = 0;
        bool moving = false;
        double delayedStop = -1, regionAt = 0, releaseAt = 3.0, regionStoppedAt = -1;
        Vector3? atRelease = null;
        int heldFrames = 0;
        var drawn = new List<(double At, Vector3 Soles)>();
        for (int frame = 0; frame < 5 * 60; frame++)
        {
            double now = frame * Frame;
            clock.Now = now;
            if (frame == 60) driver.Input.Press(MoveCommand.Forward, now);
            if (now >= releaseAt && driver.Input.IsHeld(MoveCommand.Forward)) driver.Input.Release(MoveCommand.Forward);
            while (regionAt <= now)
            {
                while (toRegion.TryPeek(out var c) && c.Arrives <= regionAt) controls = toRegion.Dequeue().Controls;
                const uint move = AgentControlFlags.AtPos | AgentControlFlags.NudgeAtPos;
                if ((controls & move) != 0) { moving = true; delayedStop = -1; region.Velocity = new Vector3((controls & AgentControlFlags.FastAt) != 0 ? 4.1f : 2.46f, 0, 0); }
                else if (moving && (controls & AgentControlFlags.Stop) != 0) { moving = false; region.Velocity = Vector3.Zero; }
                else if (moving && delayedStop < 0) delayedStop = regionAt + 0.25;
                if (delayedStop >= 0 && regionAt >= delayedStop) { moving = false; delayedStop = -1; region.Velocity = Vector3.Zero; }
                if (!moving && regionStoppedAt < 0 && regionAt >= releaseAt) regionStoppedAt = regionAt;
                region.Step(regionAt);
                regionAt += Tick;
            }
            Deliver(t, scene, changes, region, SelfLocal, ServerAppearanceTests.Me, now);
            driver.Frame(now, Frame, default, Vector3.UnitX);
            if (scene.Self is { } self && scene.Place(self, out var soles, out _))
            {
                // carried on by the region's last stated velocity, which is still the walk until the region's stop arrives
                if (now >= releaseAt && regionStoppedAt < 0 && self.Motion.Velocity(now).Length() < 1f) heldFrames++;
                drawn.Add((now, soles));
                if (now >= releaseAt && atRelease is null) atRelease = soles;
            }
        }
        Assert.NotNull(atRelease);
        // nothing but cleared movement bits goes to the region after the key comes up: no stop bit in any update
        var stops = sent.Where(s => s.At >= releaseAt && (s.Controls & AgentControlFlags.Stop) != 0).ToList();
        Assert.True(stops.Count == 0, $"{stops.Count} updates after the release carried the stop bit (the first at {(stops.Count > 0 ? stops[0].At : 0):F3} s)");
        // the drawn avatar is not held: it is drawn from the region's updates as at any other time
        Assert.True(heldFrames == 0, $"the drawn avatar was held for {heldFrames} frames while the region still moved it");
        var final = drawn[^1].Soles;
        var regionFinal = region.Position - new Vector3(0, 0, 0.95f);
        float travel = Vector3.Dot(final - atRelease!.Value, Vector3.UnitX);
        var (beyond, back, _) = StopFigures(drawn, releaseAt, final, Vector3.UnitX);
        // it keeps moving for as long as the region moves it (the region's quarter of a second after the bits clear, about a
        // metre at 4.1 m/s), is carried past the region's stop by no more than one network trip at its speed, and is at rest
        // where the region put it
        Assert.True(regionStoppedAt > releaseAt + 0.2, $"the region stopped the avatar {regionStoppedAt - releaseAt:F3} s after the release");
        Assert.True(travel >= 0.8f, $"the own avatar went on only {travel:F2} m after its key was released");
        Assert.True(back <= 0.25f && beyond <= 0.25f, $"the own avatar came back {back:F3} m (beyond its rest {beyond:F3} m)");
        Assert.True(Vector3.Distance(final, regionFinal) < 0.01f, $"at rest {Vector3.Distance(final, regionFinal):F3} m from the region's position");
    }
}

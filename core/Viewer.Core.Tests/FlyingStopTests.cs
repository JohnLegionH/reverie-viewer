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
/// The user's own avatar in flight, against a region modelled as OpenSim moves a flying avatar, without the engine.
/// The region steps every 1/11 s and sends the avatar's motion when its velocity changes, when it stops, or when it has
/// gone 5 m (OpenSim's ScenePresence.Update). Flying, the up key moves it up at 16.38 m/s from the next frame; forward
/// moves it at 9.83 m/s while only the nudge bit is held and at 16.38 m/s with the move and fast bits (the speeds a live
/// region reports). A release with no stop bit keeps it moving for another quarter of a second (OpenSim's delayed stop,
/// ScenePresence's HandleAgentUpdate); with the stop bit it stops at the next frame. Controls reach the region and
/// updates reach the viewer a network trip later, and the viewer draws at 60 frames a second. The viewer sends no stop
/// bit for a released key, so the avatar coasts: it is carried on until the region's stop arrives and settles there.
/// </summary>
public class FlyingStopTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private const double Tick = 1.0 / 11, Frame = 1.0 / 60;
    private const uint SelfLocal = 800;
    private const float Fast = 16.38f, Nudged = 9.83f;
    /// <summary>The limit: how much faster than the region reports the avatar may be drawn over 95 % of the tenth-of-a-second
    /// windows in flight. (How far past the region's stop it may be carried, and so come back, is one trip of an update at its
    /// speed: see the stop test.)</summary>
    private const float MostSpeedRatio = 1.2f;   // was 1.1 for the earlier own-avatar smoothing; the official interpolation (an update's position taken at once, damped over 0.06 s) measures 1.15 at the 95th percentile with 50 ms of jitter

    private sealed class Clock { public double Now; }

    private sealed record Packet(double Arrives, Vector3 Position, Vector3 Velocity, bool First);

    /// <summary>The region's side: the avatar stepped each frame and sent as OpenSim sends it.</summary>
    private sealed class Region(Random rng, double latency, double jitter)
    {
        public Vector3 Position = new(100, 100, 60);
        public Vector3 Velocity;
        private Vector3 _sentPosition, _sentVelocity;
        private double _lastArrival;
        private int _sent;
        public readonly Queue<Packet> Wire = new();

        public void Step(double now)
        {
            Position += Velocity * (float)Tick;
            bool send = _sent == 0
                || Vector3.Distance(Velocity, _sentVelocity) > 0.1f
                || (Velocity == Vector3.Zero && _sentVelocity != Vector3.Zero)
                || Vector3.Distance(Position, _sentPosition) > 5f;
            if (!send) return;
            double arrives = Math.Max(now + latency + rng.NextDouble() * jitter, _lastArrival);
            _lastArrival = arrives;
            Wire.Enqueue(new Packet(arrives, Position, Velocity, _sent == 0));
            _sentPosition = Position;
            _sentVelocity = Velocity;
            _sent++;
        }
    }

    /// <summary>One press of a key, held from <see cref="At"/> for <see cref="Hold"/> seconds.</summary>
    private sealed record Press(MoveCommand Key, double At, double Hold, Vector3 Direction);

    /// <summary>A stop: from the key's release to the next press, the region's stop position and the drawn soles.</summary>
    private sealed record Stop(MoveCommand Key, Vector3 Direction, Vector3 RegionSoles, List<Vector3> Drawn);

    private sealed record Flight(List<Stop> Stops, List<float> SpeedRatios, Vector3 FinalDrawn, Vector3 FinalRegion);

    private static readonly Press[] Moves =
    [
        new(MoveCommand.Jump, 1.0, 1.5, Vector3.UnitZ),        // rise and release
        new(MoveCommand.Jump, 4.5, 0.8, Vector3.UnitZ),        // rise again and release
        new(MoveCommand.Forward, 7.5, 3.0, Vector3.UnitX),     // fly forward and release
        new(MoveCommand.Forward, 12.5, 1.5, Vector3.UnitX),    // and again, shorter
    ];

    private static Flight Fly(int seed, double down, double up, double jitter)
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var region = new Region(new Random(seed), down, jitter);
        var rng = new Random(seed + 1000);
        var toRegion = new Queue<(double Arrives, uint Controls)>();
        var driver = new OwnAvatarDriver(scene, s.World, u => { toRegion.Enqueue((clock.Now + up + rng.NextDouble() * jitter, u.Controls)); return true; });
        driver.Input.Press(MoveCommand.ToggleFly, 0);   // in the air, flying
        uint controls = 0;
        double regionAt = 0, delayedStop = -1;
        bool moving = false;
        var stops = new List<Stop>();
        var ratios = new List<float>();
        var drawn = new List<(double At, Vector3 Soles, float Reported)>();
        Stop? current = null;
        var phase = new int[Moves.Length];   // each press: 0 to come, 1 held, 2 released
        for (int frame = 0; frame < 16 * 60; frame++)
        {
            double now = frame * Frame;
            clock.Now = now;
            for (int k = 0; k < Moves.Length; k++)
            {
                var m = Moves[k];
                if (now >= m.At && phase[k] == 0)
                {
                    driver.Input.Press(m.Key, now, synthetic: true);
                    phase[k] = 1;
                    current = null;
                }
                if (now >= m.At + m.Hold && phase[k] == 1)
                {
                    driver.Input.Release(m.Key);
                    phase[k] = 2;
                    current = new Stop(m.Key, m.Direction, default, new List<Vector3>());
                    stops.Add(current);
                }
            }
            while (regionAt <= now)
            {
                while (toRegion.TryPeek(out var c) && c.Arrives <= regionAt) controls = toRegion.Dequeue().Controls;
                Vector3 v = Vector3.Zero;
                if ((controls & AgentControlFlags.UpPos) != 0) v += Vector3.UnitZ * Fast;
                if ((controls & AgentControlFlags.AtPos) != 0) v += Vector3.UnitX * Fast;
                else if ((controls & AgentControlFlags.NudgeAtPos) != 0) v += Vector3.UnitX * Nudged;
                if (v != Vector3.Zero) { moving = true; delayedStop = -1; region.Velocity = v; }
                else if (moving && (controls & AgentControlFlags.Stop) != 0) { moving = false; region.Velocity = Vector3.Zero; }
                else if (moving && delayedStop < 0) delayedStop = regionAt + 0.25;
                if (delayedStop >= 0 && regionAt >= delayedStop) { moving = false; delayedStop = -1; region.Velocity = Vector3.Zero; }
                region.Step(regionAt);
                regionAt += Tick;
            }
            while (region.Wire.TryPeek(out var p) && p.Arrives <= now)
            {
                region.Wire.Dequeue();
                if (p.First) t.Raise(new AvatarUpdated(Here, new AvatarData(SelfLocal, ServerAppearanceTests.Me, "Test User", p.Position, Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), p.Velocity, 0)));
                else t.Raise(new ObjectMoved(Here, SelfLocal, true, p.Position, Quaternion.Identity, p.Velocity, Vector3.Zero, Vector3.Zero));
            }
            foreach (var c in changes.ToList()) scene.Apply(c);
            changes.Clear();
            driver.Frame(now, Frame, default, Vector3.UnitX);
            if (scene.Self is { } self && scene.Place(self, out var soles, out _) && s.World.Avatar(self.Id) is { } stated)
            {
                drawn.Add((now, soles, stated.Velocity.Length()));
                current?.Drawn.Add(soles);
            }
            if (current is not null && !moving) stops[^1] = current = current with { RegionSoles = region.Position - new Vector3(0, 0, 0.95f) };
        }
        // the drawn speed over each tenth of a second in flight, against the speed the region last reported at its start
        var fl = drawn.Where(d => d.Reported > 5).ToList();
        for (int i = 0, j = 0; i < fl.Count; i++)
        {
            while (j < fl.Count && fl[j].At - fl[i].At < 0.1 - 1e-9) j++;
            if (j >= fl.Count) break;
            double dt = fl[j].At - fl[i].At;
            if (dt > 0.2) continue;   // not in flight in between
            ratios.Add(Vector3.Distance(fl[i].Soles, fl[j].Soles) / (float)dt / fl[i].Reported);
        }
        return new Flight(stops, ratios, drawn[^1].Soles, region.Position - new Vector3(0, 0, 0.95f));
    }

    public static readonly TheoryData<int, double, double, double> Networks = new()
    {
        { 41, 0.02, 0.02, 0.03 },   // a near grid
        { 43, 0.03, 0.03, 0.05 },   // as live on a local network: updates arrive 0.33 to 0.38 s apart in flight
        { 47, 0.06, 0.06, 0.05 },   // a farther one
    };

    [Theory]
    [MemberData(nameof(Networks))]
    public void A_flying_stop_of_the_own_avatar_is_carried_no_more_than_one_update_trip_past_the_regions_stop_and_rests_there(int seed, double down, double up, double jitter)
    {
        var f = Fly(seed, down, up, jitter);
        // the region's stop reaches the viewer one trip (down, plus its jitter) after the region stopped; until then the avatar is carried on
        float mostBeyond = Fast * (float)(down + jitter), mostBack = mostBeyond;
        Assert.Equal(Moves.Length, f.Stops.Count);
        foreach (var stop in f.Stops)
        {
            float most = float.MinValue, last = 0;
            foreach (var soles in stop.Drawn)
            {
                float along = Vector3.Dot(soles - stop.RegionSoles, stop.Direction);
                most = MathF.Max(most, along);
                last = along;
            }
            float beyond = MathF.Max(0, most), back = most - last;
            output.WriteLine($"{stop.Key} released: beyond {beyond:F3} m, back {back:F3} m, drawn path after release {Path(stop.Drawn):F3} m");
            Assert.True(beyond <= mostBeyond, $"{stop.Key} released: drawn {beyond:F3} m past where the region stopped it (limit {mostBeyond:F2} m)");
            Assert.True(back <= mostBack || beyond <= 0, $"{stop.Key} released: came back {back:F3} m after its stop (limit {mostBack:F2} m)");
            Assert.True(Path(stop.Drawn) > Fast * 0.2f, $"{stop.Key} released: the drawn avatar went on only {Path(stop.Drawn):F2} m (it did not coast)");
            Assert.True(Vector3.Distance(stop.Drawn[^1], stop.RegionSoles) < 0.01f, $"{stop.Key} released: at rest {Vector3.Distance(stop.Drawn[^1], stop.RegionSoles):F3} m from the region's position");
        }
        Assert.True(Vector3.Distance(f.FinalDrawn, f.FinalRegion) < 0.01f);
    }

    [Theory]
    [MemberData(nameof(Networks))]
    public void The_own_avatar_in_flight_is_drawn_no_faster_than_the_region_reports_over_95_percent_of_windows(int seed, double down, double up, double jitter)
    {
        var f = Fly(seed, down, up, jitter);
        Assert.True(f.SpeedRatios.Count > 200, $"only {f.SpeedRatios.Count} windows in flight");
        var sorted = f.SpeedRatios.OrderBy(r => r).ToList();
        float p95 = sorted[(int)(sorted.Count * 0.95)];
        output.WriteLine($"windows {sorted.Count}, p50 {sorted[sorted.Count / 2]:F3}, p95 {p95:F3}, max {sorted[^1]:F3}");
        Assert.True(p95 <= MostSpeedRatio, $"drawn at {p95:F3} times the reported speed over 95 % of {sorted.Count} windows (max {sorted[^1]:F3}; limit {MostSpeedRatio})");
    }

    private static float Path(List<Vector3> drawn) => drawn.Zip(drawn.Skip(1), Vector3.Distance).Sum();

    [Fact]
    public void Releasing_up_or_down_in_flight_sends_no_stop_and_neither_does_releasing_a_jump()
    {
        // as the official viewer: a key-up sends nothing, its bits are just no longer set
        var input = new AgentControlInput();
        input.Press(MoveCommand.ToggleFly, 0);
        input.Press(MoveCommand.Jump, 0.0);
        input.Frame(0.0, Frame, inAir: true, groundSpeed: 0);
        input.Release(MoveCommand.Jump);
        uint c = input.Frame(0.1, Frame, inAir: true, groundSpeed: 0);
        Assert.Equal(AgentControlFlags.Fly, c);

        input.Press(MoveCommand.Down, 0.5);
        input.Frame(0.5, Frame, inAir: true, groundSpeed: 0);
        input.Release(MoveCommand.Down);
        c = input.Frame(0.6, Frame, inAir: true, groundSpeed: 0);
        Assert.Equal(AgentControlFlags.Fly, c);

        // walking, a jump's release sends nothing either
        var walker = new AgentControlInput();
        walker.Press(MoveCommand.Jump, 0.0);
        walker.Frame(0.0, Frame, inAir: false, groundSpeed: 0);
        walker.Release(MoveCommand.Jump);
        c = walker.Frame(0.1, Frame, inAir: true, groundSpeed: 0);
        Assert.Equal(0u, c);
    }
}

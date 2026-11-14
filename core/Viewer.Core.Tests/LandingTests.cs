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
/// How a landing avatar is drawn, against update sequences shaped like a region's, without the engine. A small region
/// model steps every 1/11 s (OpenSim's default frame rate) and sends an avatar's motion as OpenSim's ScenePresence.Update
/// does: when its velocity changes by more than 0.1 m/s, when it stops, or when it has gone 5 m since the last update it
/// sent, and now and then for other reasons, 0.3 to 1.4 s apart. Two descents, as live traces show them: flying down at a
/// steady 9.83 m/s (an update every 5 m), and falling under gravity (the velocity changes every frame, so an update every
/// frame). At touchdown the region sends the resting position with the falling velocity still set, and the zero velocity
/// a frame later. Each update reaches the viewer a network trip later, and the viewer draws at 60 frames a second.
/// </summary>
public class LandingTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Other = Guid.Parse("7c41e0d2-95ab-4f63-8e2d-1b6a3f9c0e57");
    private const double Tick = 1.0 / 11, Frame = 1.0 / 60;
    private const uint SelfLocal = 800, OtherLocal = 900;
    private const float Half = 0.95f;   // half the avatar's 1.9 m height: the region places it by its centre
    /// <summary>How far below its resting height a landing avatar may be drawn at any moment (it does not show).</summary>
    private const float Limit = 0.05f;

    private sealed class Clock { public double Now; }

    private sealed record Packet(double Arrives, Vector3 Position, Vector3 Velocity, bool First);

    /// <summary>The region's side: one avatar hovering, then descending to a floor at <paramref name="floorSoles"/>.</summary>
    private sealed class Region(Random rng, double latency, double jitter, float floorSoles, bool gravity)
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public bool Descending;
        private int _landing;   // 0 in the air, 1 touched down this frame (the stop follows), 2 at rest
        private Vector3 _sentPosition, _sentVelocity;
        private double _nextExtra = 0.3 + rng.NextDouble() * 1.1, _lastArrival;
        private int _sent;
        public readonly Queue<Packet> Wire = new();

        public void Step(double now)
        {
            bool contact = false;
            if (_landing == 1) { Velocity = Vector3.Zero; _landing = 2; }
            else if (Descending && _landing == 0)
            {
                Velocity = gravity ? Velocity - new Vector3(0, 0, 9.8f * (float)Tick) : new Vector3(0, 0, -9.83f);
                Position += Velocity * (float)Tick;
                if (Position.Z - Half <= floorSoles)
                {
                    Position.Z = floorSoles + Half;
                    contact = true;
                    _landing = 1;
                }
            }
            bool send = _sent == 0 || contact
                || Vector3.Distance(Velocity, _sentVelocity) > 0.1f
                || (Velocity == Vector3.Zero && _sentVelocity != Vector3.Zero)
                || Vector3.Distance(Position, _sentPosition) > 5f;
            if (now >= _nextExtra)
            {
                send = true;
                _nextExtra = now + 0.3 + rng.NextDouble() * 1.1;
            }
            if (!send) return;
            double arrives = Math.Max(now + latency + rng.NextDouble() * jitter, _lastArrival);   // never overtakes
            _lastArrival = arrives;
            Wire.Enqueue(new Packet(arrives, Position, Velocity, _sent == 0));
            _sentPosition = Position;
            _sentVelocity = Velocity;
            _sent++;
        }
    }

    private static AvatarData Av(uint local, Guid agent, Vector3 pos, Vector3 vel) =>
        new(local, agent, "Test User", pos, Quaternion.Identity, new Vector3(0.45f, 0.6f, 2 * Half), vel, 0);

    /// <summary>A logged-in session in a 256 m region whose terrain is flat at <paramref name="terrain"/> metres.</summary>
    private static (FakeTransport T, AvatarScene Scene, Clock Clock, List<WorldChange> Changes) Setup(float terrain)
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var heights = Enumerable.Repeat(terrain, 16 * 16).ToArray();
        for (int px = 0; px < 16; px++)
            for (int py = 0; py < 16; py++)
                t.Raise(new LandPatch(Here, px, py, 16, heights));
        var clock = new Clock();
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        return (t, scene, clock, changes);
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

    /// <summary>Runs one landing and returns the lowest drawn soles' height and where the drawn soles end.</summary>
    private static (float Lowest, Vector3 Final) Land(bool self, bool gravity, float drop, float terrain, float floor, int seed, double latency, double jitter)
    {
        var (t, scene, clock, changes) = Setup(terrain);
        var region = new Region(new Random(seed), latency, jitter, floor, gravity) { Position = new Vector3(100, 100, floor + drop + Half) };
        uint local = self ? SelfLocal : OtherLocal;
        var agent = self ? ServerAppearanceTests.Me : Other;
        float lowest = float.MaxValue;
        Vector3 final = default;
        double regionAt = 0, descendAt = 1.0 + seed * 0.011;
        for (int frame = 0; frame < (int)((descendAt + drop / 2 + 4) * 60); frame++)
        {
            double now = frame * Frame;
            clock.Now = now;
            while (regionAt <= now)
            {
                if (regionAt >= descendAt) region.Descending = true;
                region.Step(regionAt);
                regionAt += Tick;
            }
            Deliver(t, scene, changes, region, local, agent, now);
            var e = scene.Entries.SingleOrDefault(x => x.IsSelf == self);
            if (e is not null && scene.Place(e, out var soles, out _))
            {
                lowest = MathF.Min(lowest, soles.Z);
                final = soles;
            }
        }
        return (lowest, final);
    }

    [Theory]
    [InlineData(false, false, 10f, 3, 0.03, 0.03)]   // another avatar flying down 10 m
    [InlineData(false, false, 30f, 5, 0.04, 0.03)]   // ... 30 m
    [InlineData(false, false, 30f, 7, 0.06, 0.04)]   // ... on a slower network
    [InlineData(false, true, 4f, 9, 0.04, 0.03)]     // another avatar falling 4 m
    [InlineData(true, false, 10f, 11, 0.02, 0.02)]   // the user's own avatar flying down 10 m
    [InlineData(true, false, 30f, 13, 0.02, 0.02)]   // ... 30 m
    [InlineData(true, true, 4f, 15, 0.02, 0.02)]     // the user's own avatar falling 4 m
    public void A_landing_avatar_is_never_drawn_below_where_it_comes_to_rest(bool self, bool gravity, float drop, int seed, double latency, double jitter)
    {
        const float ground = 24.15f;
        var (lowest, final) = Land(self, gravity, drop, ground, ground, seed, latency, jitter);
        string who = self ? "the own avatar" : "another avatar";
        Assert.True(lowest >= ground - Limit, $"{who} landing from {drop} m was drawn {ground - lowest:F3} m below its resting height");
        Assert.True(MathF.Abs(final.Z - ground) < 0.01f, $"{who} at rest {final.Z - ground:F3} m from the region's height");
    }

    /// <summary>
    /// Another avatar landing on a floor (a building's, 3 m above the terrain). The official viewer keeps a carried object only above the land
    /// (llworld.cpp getMinAllowedZ), so until the region's stop arrives it carries the avatar on down past the floor and then eases it back
    /// (LLDrawable::updateXform); this viewer must draw it where the official viewer does in every frame, and at rest where the region puts it.
    /// </summary>
    [Theory]
    [InlineData(10f, 17, 0.04, 0.03)]
    [InlineData(30f, 19, 0.04, 0.03)]
    public void Another_avatar_landing_on_a_floor_above_the_terrain_is_drawn_as_the_official_viewer_draws_it(float drop, int seed, double latency, double jitter)
    {
        const float terrain = 21.15f, floor = 24.15f;
        var (t, scene, clock, changes) = Setup(terrain);
        var region = new Region(new Random(seed), latency, jitter, floor, false) { Position = new Vector3(100, 100, floor + drop + Half) };
        var official = new OfficialOwnAvatarReplay();
        double regionAt = 0, descendAt = 1.0 + seed * 0.011, worst = 0, last = double.NaN;
        string where = "";
        Vector3 final = default;
        for (int frame = 0; frame < (int)((descendAt + drop / 2 + 4) * 60); frame++)
        {
            double now = frame * Frame, dt = double.IsNaN(last) ? Frame : now - last;
            last = now;
            clock.Now = now;
            while (regionAt <= now)
            {
                if (regionAt >= descendAt) region.Descending = true;
                region.Step(regionAt);
                regionAt += Tick;
            }
            foreach (var p in region.Wire.TakeWhile(p => p.Arrives <= now)) official.Statement(p.Position, p.Velocity, Vector3.Zero, 256, now, dt, 1f);
            Deliver(t, scene, changes, region, OtherLocal, Other, now);
            var e = scene.Entries.SingleOrDefault(x => !x.IsSelf);
            if (e is null || !scene.Place(e, out var soles, out _)) continue;
            final = soles;
            var drawnOfficial = official.Frame(now, dt, 1f, _ => false);
            float gap = (soles + new Vector3(0, 0, Half) - drawnOfficial).Length();
            if (gap > worst) { worst = gap; where = $"{now:0.000} s: soles z {soles.Z:0.000}, official's {drawnOfficial.Z - Half:0.000}"; }
        }
        Assert.True(worst <= 0.1, $"landing on a floor from {drop} m it was drawn {worst:0.000} m from the official viewer ({where})");
        Assert.True(MathF.Abs(final.Z - floor) < 0.01f, $"at rest {final.Z - floor:F3} m from the region's height");
    }

    [Fact]
    public void An_avatar_the_region_rests_a_little_below_the_terrain_samples_is_drawn_where_the_region_puts_it()
    {
        // the region's own avatar height can differ a little from the one stated, so its soles sit 8 cm low
        var (t, scene, clock, changes) = Setup(24.15f);
        clock.Now = 1;
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new Vector3(100, 100, 24.07f + Half), Vector3.Zero)));
        foreach (var c in changes) scene.Apply(c);
        Assert.True(scene.Place(scene.Entries.Single(), out var soles, out _));
        Assert.Equal(24.07f, soles.Z, 3);
    }

    [Theory]
    [InlineData(0.00f, 0.00f)]
    [InlineData(0.40f, 0.40f)]    // a region that rests the avatar 40 cm above the land: a ground sit still goes down to the land
    [InlineData(-0.08f, -0.08f)]  // and one that rests it a little low
    [InlineData(3.00f, 3.00f)]    // well above the land and the region sent no foot plane: the land is the ground, as in the official viewer
    public void The_gap_between_the_drawn_soles_and_the_land_is_read_for_an_avatar_not_on_a_seat(float aboveTerrain, float expected)
    {
        var (t, scene, clock, changes) = Setup(24.15f);
        clock.Now = 1;
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new Vector3(100, 100, 24.15f + aboveTerrain + Half), Vector3.Zero)));
        foreach (var c in changes) scene.Apply(c);
        var e = scene.Entries.Single();
        Assert.True(scene.Place(e, out var soles, out _));
        Assert.Equal(expected, scene.GroundGap(e, soles), 3);
    }

    [Theory]
    [InlineData(0.50f)]   // a low platform
    [InlineData(3.00f)]   // a deck
    public void An_avatar_on_a_floor_the_region_names_has_the_floor_as_its_ground(float floorAboveTerrain)
    {
        var (t, scene, clock, changes) = Setup(24.15f);
        clock.Now = 1;
        float floor = 24.15f + floorAboveTerrain;
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new Vector3(100, 100, floor + Half), Vector3.Zero) with { FootPlane = new(0, 0, 1, floor) }));
        foreach (var c in changes) scene.Apply(c);
        var e = scene.Entries.Single();
        Assert.True(scene.Place(e, out var soles, out _));
        // the official ground is the plane less its 5 cm allowance, so the gap is that allowance
        Assert.Equal(OfficialGround.PlaneAllowance, scene.GroundGap(e, soles), 3);
    }

    [Fact]
    public void The_floor_comes_from_the_terse_updates_too_and_is_forgotten_when_the_region_sends_none()
    {
        var (t, scene, clock, changes) = Setup(24.15f);
        clock.Now = 1;
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new Vector3(100, 100, 27.15f + Half), Vector3.Zero)));
        t.Raise(new ObjectMoved(Here, OtherLocal, true, new Vector3(100, 100, 27.15f + Half), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, new Vector4(0, 0, 1, 27.15f)));
        foreach (var c in changes) scene.Apply(c);
        var e = scene.Entries.Single();
        Assert.True(scene.Place(e, out var soles, out _));
        Assert.Equal(OfficialGround.PlaneAllowance, scene.GroundGap(e, soles), 3);
        changes.Clear();
        t.Raise(new ObjectMoved(Here, OtherLocal, true, new Vector3(100, 100, 27.15f + Half), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero));
        foreach (var c in changes) scene.Apply(c);
        Assert.Equal(3.0f, scene.GroundGap(e, soles), 3);
    }

    [Fact]
    public void The_gap_to_the_land_is_zero_where_no_terrain_has_arrived()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(Here, Av(OtherLocal, Other, new Vector3(100, 100, 30f), Vector3.Zero)));
        foreach (var c in changes) scene.Apply(c);
        var e = scene.Entries.Single();
        Assert.True(scene.Place(e, out var soles, out _));
        Assert.Equal(0f, scene.GroundGap(e, soles));
    }

    [Fact]
    public void The_ground_is_read_between_the_terrain_samples_and_is_unknown_where_no_patch_has_arrived()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var heights = new float[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                heights[y * 16 + x] = 20 + x;   // rising a metre a metre eastward within the patch
        t.Raise(new LandPatch(Here, 0, 0, 16, heights));
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        Assert.Equal(23.5f, scene.Ground(3.5f, 2.25f)!.Value, 3);
        Assert.Equal(20f, scene.Ground(0, 0)!.Value, 3);
        Assert.Null(scene.Ground(40, 40));     // no patch there yet
        Assert.Null(scene.Ground(-1, 3));      // outside the region
    }
}

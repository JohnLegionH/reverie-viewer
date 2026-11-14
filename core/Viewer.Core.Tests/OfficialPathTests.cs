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
/// Every avatar, the user's own and others, on the official viewer's object path (release 26.4: LLVOAvatar::idleUpdate calls LLViewerObject::idleUpdate,
/// which calls interpolateLinearMotion; processUpdateMessage takes an update where it says; LLDrawable::updateXform damps what is drawn; the camera's
/// focus is the drawn place, LLVOAvatar::updateCharacter: gAgent.setPositionAgent(getRenderPosition())), compared frame by frame with the official
/// model (<see cref="OfficialOwnAvatarReplay"/>). Each test builds its own worlds and scenes on its own clock: no process-wide state, so the class runs
/// in parallel with the others.
/// </summary>
public class OfficialPathTests
{
    private static readonly Guid Avatar = Guid.Parse("cccccccc-0000-0000-0000-0000000000a1");
    private static readonly Guid Watcher = Guid.Parse("cccccccc-0000-0000-0000-0000000000a2");
    private static readonly Vector3 Size = new(0.45f, 0.6f, 1.9f);

    // ---------------- the official model's ping term ----------------

    [Fact]
    public void The_official_model_takes_a_statement_where_it_says_as_PingInterpolate_is_off_by_default()
    {
        var official = new OfficialOwnAvatarReplay { PingSeconds = 0.12 };
        official.Statement(new Vector3(100, 100, 30), new Vector3(16, 0, 0), Vector3.Zero, 1024, 1.0, 1.0 / 60, 1f);
        Assert.False(official.PingInterpolate);
        Assert.Equal(100f, official.Place.X, 4);
    }

    [Fact]
    public void With_PingInterpolate_on_the_official_model_moves_a_statement_on_by_half_the_ping_and_a_frame_scaled_by_time_dilation()
    {
        var official = new OfficialOwnAvatarReplay { PingSeconds = 0.12, PingInterpolate = true };
        official.Statement(new Vector3(100, 100, 30), new Vector3(16, 0, 0), Vector3.Zero, 1024, 1.0, 0.02, 0.9f);
        Assert.Equal(100f + 16f * 0.5f * 0.9f * (0.12f + 0.02f), official.Place.X, 4);
    }

    // ---------------- a frame stall ----------------

    /// <summary>
    /// A steady flier (16 m/s, the region stating it each 5 m) and one 250 ms frame, after which the update that came during it is taken late. The
    /// official viewer takes it where it says, which puts the avatar back by its speed times its lateness: the step back is the official behaviour
    /// after a stall. This viewer must draw the same in every frame.
    /// </summary>
    [Fact]
    public void After_a_250_ms_frame_stall_a_late_update_is_drawn_as_the_official_viewer_draws_it()
    {
        var ours = new ObjectMotion { Region = new MotionRegion(Vector3.Zero, 1024, null) };
        var official = new OfficialOwnAvatarReplay();
        var v = new Vector3(16, 0, 0);
        double t = 0, last = double.NaN, nextStatement = 0;
        float x0 = 100;
        double worst = 0;
        string where = "";
        bool stalled = false;
        var pending = new List<(double SentAt, Vector3 Position)>();
        while (t < 3.0)
        {
            double dt = !stalled && t >= 1.5 ? 0.25 : 1.0 / 60;   // the one long frame
            if (dt > 0.1) stalled = true;
            t += dt;
            // the region states the avatar where it is each time it has gone 5 m; the statement reaches the viewer 40 ms later
            while (nextStatement + 0.04 <= t) { pending.Add((nextStatement, new Vector3(x0 + 16f * (float)nextStatement, 128, 40))); nextStatement += 5.0 / 16; }
            foreach (var (_, p) in pending)
            {
                ours.Update(p, v, Quaternion.Identity, t);
                official.Statement(p, v, Vector3.Zero, 1024, t, double.IsNaN(last) ? 1.0 / 60 : t - last, 1f);
            }
            pending.Clear();
            var a = ours.At(t).Position;
            var b = official.Frame(t, double.IsNaN(last) ? 1.0 / 60 : t - last, 1f, _ => false);
            last = t;
            if ((a - b).Length() > worst) { worst = (a - b).Length(); where = $"{t:0.000} s: ours x {a.X:0.000}, official {b.X:0.000}"; }
        }
        Assert.True(worst <= 0.1, $"ours is {worst:0.000} m from the official viewer after the stall ({where})");
    }

    // ---------------- one time a frame ----------------

    /// <summary>Every avatar's motion in a frame is timed on one time, latched when the frame starts (the official frame time): a read later in
    /// the frame gives the same time, the next frame's latch a new one.</summary>
    [Fact]
    public void The_frame_clock_gives_one_time_for_the_whole_frame()
    {
        double source = 1.0;
        var clock = new FrameClock(() => source);
        Assert.Equal(1.0, clock.Now);   // before the first frame: the source itself
        clock.Latch();
        source = 1.004;   // the camera reads after the regions' frames, later on the wall clock
        Assert.Equal(1.0, clock.Now);
        source = 1.017;
        clock.Latch();
        Assert.Equal(1.017, clock.Now);
    }

    // ---------------- the shared body and the camera at a crossing ----------------

    private sealed class Region
    {
        public required WorldState World;
        public required AvatarScene Scene;
        public required SimRef Sim;
        public readonly List<WorldChange> Changes = new();
    }

    private static (IdentityHub Hub, Region[] Regions) TwoRegions(CrossingReplayTests.Crossing c, Guid self, Func<double> clock, OwnMotionShare? share)
    {
        var hub = new IdentityHub { SelfAgent = self };
        var regions = new Region[2];
        for (int i = 0; i < 2; i++)
        {
            var (origin, width) = c.Regions[i];
            int gx = 1000 + (int)(origin.X / 256), gy = 999 + (int)(origin.Y / 256);
            var sim = new SimRef(((ulong)gx * 256UL) << 32 | (ulong)gy * 256UL, $"r{i}");
            var w = new WorldState(new CoreLog(new ListSink(), "test"));
            w.AttachIdentity(hub);
            w.SetOriginOffset(origin);
            w.BeginRegion(sim, new RegionInfo($"r{i}", gx, gy, (int)width, (int)width, true));
            var scene = new AvatarScene(w, self, clock) { HoldHandoffs = true, OwnMotion = share };
            var o = origin;
            scene.HasRegionAt = local => c.Known(local + o);
            var r = new Region { World = w, Scene = scene, Sim = sim };
            w.Changed += r.Changes.Add;
            regions[i] = r;
        }
        hub.Current = regions[0].World;
        return (hub, regions);
    }

    /// <summary>A region's frame, as its presenter does it: its changes taken, and the held entries following the region the agent is in.</summary>
    private static void Take(Region r, IdentityHub hub, Guid agent)
    {
        foreach (var ch in r.Changes) r.Scene.Apply(ch);
        r.Changes.Clear();
        foreach (var held in r.Scene.Held) if (hub.CurrentAvatar(agent) is { } now) r.Scene.Follow(held, now);
    }

    /// <summary>A region's part in placing the shared body, as the presenter's driver rule has it: one entry places it, a region's live entry for the
    /// agent, or, while no region has one live, the entry a region holds for it (the region left until the region entered takes the agent).</summary>
    private static void Drive(Region[] regions, Region r, Guid agent, ref Vector3? body)
    {
        if (body is not null || r.Scene.Entries.FirstOrDefault(x => x.AgentId == agent) is not { } e) return;
        bool otherLive = regions.Any(x => x != r && x.Scene.Entries.Any(o => o.AgentId == agent && !x.Scene.IsHeld(o)));
        if (r.Scene.IsHeld(e) && otherLive) return;
        if (r.Scene.Place(e, out var soles, out _)) body = Centre(e, soles);
    }

    private static Vector3 Centre(AvatarEntry e, Vector3 soles) => soles + new Vector3(0, 0, (e.Size.Z > 0.1f ? e.Size.Z : AvatarScene.DefaultHeight) / 2);

    /// <summary>
    /// The recorded crossing in which neither region stated the flying agent for 1.28 s and the region entered then stated it about 13 m behind where
    /// it had been carried (fixtures/crossings/own-avatar-crossing-long-gap.txt). The two regions share the own avatar's one object and drawn body; the
    /// region the session started in is framed first, as the viewer frames its regions. In every frame the body must be placed within 0.1 m of where the
    /// camera then centres on the avatar (the camera reads it after the regions' frames, on the same frame time: one time a frame, as the official gFrameTimeSeconds), and within 0.1 m of where the
    /// official viewer draws it on the same statements: the official viewer takes every region's updates before it moves anything.
    /// </summary>
    [Fact]
    public void In_a_crossing_with_a_long_gap_the_body_the_camera_and_the_official_viewer_agree_in_every_frame()
    {
        var c = CrossingReplayTests.Load("own-avatar-crossing-long-gap.txt").Single();
        double clock = 0;
        var share = new OwnMotionShare(() => clock);
        var (hub, regions) = TwoRegions(c, Avatar, () => clock, share);
        var official = new OfficialOwnAvatarReplay();
        double last = double.NaN, worstCamera = 0, worstOfficial = 0;
        string whereCamera = "", whereOfficial = "";
        bool started = false;
        foreach (var f in c.Frames)
        {
            double dt = double.IsNaN(last) ? 1.0 / 60 : f.Time - last;
            last = f.Time;
            clock = f.Time;
            foreach (var s in f.Taken)
            {
                var r = regions[s.Region];
                if (s.Region == 1) hub.Current = r.World;
                r.World.ApplyAvatar(r.Sim, new AvatarData((uint)(900 + s.Region), Avatar, "Test User", s.Position - c.Regions[s.Region].Origin, Quaternion.Identity, Size, s.Velocity, 0));
                official.Statement(s.Position, s.Velocity, c.Regions[s.Region].Origin, c.Regions[s.Region].Width, f.Time, dt, f.TimeDilation);
                started = true;
            }
            foreach (var r in regions) r.World.SetTimeDilation(r.Sim, f.TimeDilation);
            Vector3? body = null;
            SharedBodyFrame.Run(regions, r => Take(r, hub, Avatar), r => Drive(regions, r, Avatar, ref body));
            if (!started) continue;
            Assert.NotNull(body);
            // the camera reads the avatar after the regions' frames, at the frame's time (the viewer's frame clock)
            var own = AvatarScene.FirstSelf(regions[1].Scene, regions[0].Scene);
            Assert.True(own is { } o && o.Scene.Place(o.Self, out _, out _));
            own!.Value.Scene.Place(own.Value.Self, out var camSoles, out _);
            var anchor = Centre(own.Value.Self, camSoles);
            var drawnOfficial = official.Frame(f.Time, dt, f.TimeDilation, c.Known);
            if ((body.Value - anchor).Length() > worstCamera) { worstCamera = (body.Value - anchor).Length(); whereCamera = $"{f.Time:0.000} s: body y {body.Value.Y:0.00}, camera's centre y {anchor.Y:0.00}"; }
            var flat = new Vector3(body.Value.X, body.Value.Y, drawnOfficial.Z);   // heights: the scene keeps the soles on the land; this crossing is in the air
            if ((flat - drawnOfficial).Length() > worstOfficial) { worstOfficial = (flat - drawnOfficial).Length(); whereOfficial = $"{f.Time:0.000} s: body y {body.Value.Y:0.00}, official y {drawnOfficial.Y:0.00}"; }
        }
        Assert.True(worstCamera <= 0.1, $"the body and the camera's centre parted by {worstCamera:0.000} m ({whereCamera})");
        Assert.True(worstOfficial <= 0.1, $"the body is {worstOfficial:0.000} m from the official viewer ({whereOfficial})");
    }

    // ---------------- a statement repeated ----------------

    /// <summary>
    /// A region holding an agent at a border states it again where it stated it before (seen live: the region entered stated the flying agent at the same
    /// place two and three times). The official viewer takes every update: processUpdateMessage sets the place whenever it differs from the object's
    /// carried place (llviewerobject.cpp 2313-2325), so the avatar is put back where the region says. A terse update and a full one alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_region_that_states_an_avatar_again_where_it_did_before_puts_the_carried_avatar_back_there(bool full)
    {
        var sim = new SimRef(256000UL << 32 | 256000UL, "Here");
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(sim, new RegionInfo("Here", 1000, 1000, 256, 256, true));
        double clock = 0;
        var scene = new AvatarScene(w, Watcher, () => clock);
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        var at = new Vector3(100, 128, 40);
        var v = new Vector3(16, 0, 0);
        void Take() { foreach (var c in changes) scene.Apply(c); changes.Clear(); }
        w.ApplyAvatar(sim, new AvatarData(700, Avatar, "Other User", at, Quaternion.Identity, Size, v, 0));
        Take();
        var e = scene.Entries.Single();
        for (int i = 1; i <= 18; i++) { clock = i / 60.0; scene.Place(e, out _, out _); }   // carried 0.3 s on: 4.8 m
        clock = 19 / 60.0;
        if (full) w.ApplyAvatar(sim, new AvatarData(700, Avatar, "Other User", at, Quaternion.Identity, Size, v, 0));
        else w.ApplyMove(sim, 700, true, at, Quaternion.Identity, v);
        Take();
        for (int i = 19; i <= 30; i++) { clock = i / 60.0; scene.Place(e, out _, out _); }
        // the object is back at the stated place and carried 0.2 s from it (3.2 m); what is drawn has closed on it
        Assert.True(scene.Place(e, out var soles, out _));
        Assert.InRange(soles.X, 100f + 16f * (11 / 60f) - 1.5f, 100f + 16f * (11 / 60f) + 0.05f);
    }

    // ---------------- other avatars ----------------

    /// <summary>A region's statements of another avatar that walks east, stops, turns and walks back west, then on east across the border
    /// x = 256 into the next region, which lies east of it (places in region 0's frame).</summary>
    private static List<(double At, Vector3 Position, Vector3 Velocity, Quaternion Rotation)> WalkStopTurnAndCross()
    {
        var list = new List<(double, Vector3, Vector3, Quaternion)>();
        var east = Quaternion.Identity;
        var west = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        float x = 230;
        double t = 0;
        void Walk(float speed, double seconds, Quaternion facing)
        {
            // a region states a moving avatar when its velocity changes and each time it has gone about 5 m (here about every 1.6 s at a walk)
            double end = t + seconds;
            list.Add((t, new Vector3(x, 128, 22), new Vector3(speed, 0, 0), facing));
            while (t < end)
            {
                double step = Math.Min(5.0 / Math.Max(Math.Abs(speed), 0.1), end - t);
                x += speed * (float)step;
                t += step;
                if (t < end) list.Add((t, new Vector3(x, 128, 22), new Vector3(speed, 0, 0), facing));
            }
        }
        Walk(3.2f, 3.0, east);
        list.Add((t + 0.05, new Vector3(x + 0.16f, 128, 22), Vector3.Zero, east));   // the stop: stated where it stopped, a little past the last carried place
        x += 0.16f;
        t += 1.0;
        list.Add((t, new Vector3(x, 128, 22), Vector3.Zero, west));                  // the turn
        t += 0.5;
        Walk(-3.2f, 2.0, west);
        list.Add((t + 0.05, new Vector3(x - 0.16f, 128, 22), Vector3.Zero, west));
        x -= 0.16f;
        t += 1.0;
        Walk(3.2f, 12.0, east);   // on east across the border at x = 256
        return list;
    }

    /// <summary>
    /// Another avatar walking, stopping, turning, walking back and crossing into the next region, as the watching viewer receives it (each statement
    /// taken 40 ms after the region sent it, frames of 1/60 s). The official viewer draws it on the object path, as it draws the user's own: an
    /// update taken where it says, carried on along its velocity, what is drawn damped towards it (LLVOAvatar::idleUpdate). This viewer must draw it
    /// within 0.1 m of that in every frame, the crossing included (both regions draw the one body; the region left until the region entered states it).
    /// </summary>
    [Fact]
    public void Another_avatar_walking_stopping_turning_and_crossing_is_drawn_as_the_official_viewer_draws_it()
    {
        var crossing = new CrossingReplayTests.Crossing("walk", [(Vector3.Zero, 256f), (new Vector3(256, 0, 0), 256f)], []);
        double clock = 0;
        var share = new OwnMotionShare(() => clock);
        var (hub, regions) = TwoRegions(crossing, Watcher, () => clock, share);
        var official = new OfficialOwnAvatarReplay();
        var statements = WalkStopTurnAndCross();
        int next = 0;
        double last = double.NaN, worst = 0;
        string where = "";
        bool started = false, crossed = false;
        for (int frame = 0; frame * (1.0 / 60) < statements[^1].At + 1.0; frame++)
        {
            double t = frame / 60.0, dt = double.IsNaN(last) ? 1.0 / 60 : t - last;
            last = t;
            clock = t;
            while (next < statements.Count && statements[next].At + 0.04 <= t)
            {
                var s = statements[next++];
                // the region left stops stating it at the border; the region entered states it from there on
                int region = s.Position.X >= 256 ? 1 : 0;
                if (region == 1 && !crossed) { crossed = true; hub.Current = regions[1].World; }
                var r = regions[region];
                var origin = crossing.Regions[region].Origin;
                r.World.ApplyAvatar(r.Sim, new AvatarData((uint)(700 + region), Avatar, "Other User", s.Position - origin, s.Rotation, Size, s.Velocity, 0));
                official.Statement(s.Position, s.Velocity, origin, crossing.Regions[region].Width, t, dt, 1f);
                started = true;
            }
            Vector3? body = null;
            SharedBodyFrame.Run(regions, r => Take(r, hub, Avatar), r => Drive(regions, r, Avatar, ref body));
            if (!started) continue;
            Assert.NotNull(body);
            var drawnOfficial = official.Frame(t, dt, 1f, crossing.Known);
            float gap = (body.Value - drawnOfficial).Length();
            if (gap > worst) { worst = gap; where = $"{t:0.000} s: drawn x {body.Value.X:0.000}, official x {drawnOfficial.X:0.000}"; }
        }
        Assert.True(crossed, "the walk never crossed");
        Assert.True(worst <= 0.1, $"the other avatar is drawn {worst:0.000} m from the official viewer ({where})");
    }

    // ---------------- selected objects ----------------

    /// <summary>The official viewer does not carry a selected object (LLViewerObject::idleUpdate skips the interpolation when isSelected()); it
    /// stands where the region last stated it.</summary>
    [Fact]
    public void A_selected_object_stands_where_the_region_stated_it()
    {
        var sim = new SimRef(256000UL << 32 | 256000UL, "Here");
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
        w.ApplyObject(sim, new PrimData(41, new Guid(41, 0, 0, new byte[8]), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One, new Vector3(2, 0, 0), default, default));
        var m = new ObjectInterpolator(w) { Selected = local => local == 41 };
        m.Note(new ObjectId(w.Epoch, 41), full: true, now: 0);
        for (int i = 1; i <= 60; i++) m.Frame(i / 60.0);
        Assert.True(m.TryPlace(41, out var p, out _));
        Assert.Equal(10f, p.X, 3);
    }
}

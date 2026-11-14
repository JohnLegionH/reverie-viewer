// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Walking over a region border and back with neighbours on. After the first round trip the avatar used to go back and forth over the
/// border about once a second: the world the agent had left still held a copy of the agent's own avatar, and coming back adopted that
/// copy's heading (the way it faced when it left) instead of the heading the agent has. The official viewer keeps the agent's own heading
/// across a crossing and moves the one avatar object with the agent; so here the heading stays the agent's, and the world left behind
/// does not keep the agent's avatar.
/// </summary>
public class BorderRoundTripTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef There = new(256256UL << 32 | 256000UL, "There");
    private static readonly Guid Me = ServerAppearanceTests.Me;
    private const float East = 0f, West = MathF.PI;

    private sealed class Clock { public double Now; }

    private static AvatarData Self(float x, float heading) =>
        new(800, Me, "Own Avatar", new Vector3(x, 100, 21.95f), AgentControlInput.BodyRotation(heading), new Vector3(0.45f, 0.6f, 1.9f));

    private sealed class Region
    {
        public readonly SimRef Sim;
        public readonly WorldState World;
        public readonly AvatarScene Scene;

        public Region(SimRef sim, Clock clock)
        {
            Sim = sim;
            World = new WorldState(new CoreLog(new ListSink(), "test"));
            World.BeginRegion(sim, new RegionInfo(sim.Name, sim.GridX, sim.GridY, 256, 256, true));
            Scene = new AvatarScene(World, Me, () => clock.Now);
            World.Changed += Scene.Apply;
        }
    }

    /// <summary>Two regions side by side, the agent walking along x at 5 m/s in the direction of the last update it sent. The grid's
    /// side is a fake: the avatar's record in the region it is in follows it (a fresh object update two frames after an arrival),
    /// and walking past an edge is a crossing that rebinds the driver, as the host does.</summary>
    private sealed class Walk
    {
        private readonly Clock _clock = new();
        private readonly Region _here, _there;
        private Region _in;
        private float _x;
        private int _sinceArrival = 99;
        public readonly OwnAvatarDriver Driver;
        public int Crossings;

        public Walk(float startX, bool staleCopyInThere, float staleHeading)
        {
            _here = new Region(Here, _clock);
            _there = new Region(There, _clock);
            _in = _here;
            _x = startX;
            _here.World.ApplyAvatar(Here, Self(startX, East));
            if (staleCopyInThere)
                _there.World.ApplyAvatar(There, Self(3, staleHeading));   // what the agent looked like when it was last there
            Driver = new OwnAvatarDriver(_here.Scene, _here.World, _ => true);
        }

        public bool InThere => ReferenceEquals(_in, _there);
        public override string ToString() => $"x={_x:0.0} in={(InThere ? "there" : "here")} crossings={Crossings} heading={Driver.Input.Heading} last={(Driver.Last is { } u ? $"{u.Controls:x} yaw={AvatarMotion.Yaw(u.BodyRotation):0.00}" : "none")}";

        public void Seconds(double seconds)
        {
            const double dt = 0.05;
            for (double t = 0; t < seconds; t += dt)
            {
                _clock.Now += dt;
                Driver.Frame(_clock.Now, dt, Vector3.Zero, Vector3.UnitX);
                float yaw = Driver.Last is { } u ? AvatarMotion.Yaw(u.BodyRotation) : East;
                bool walking = Driver.Last is { } l && (l.Controls & (AgentControlFlags.AtPos | AgentControlFlags.NudgeAtPos)) != 0;
                if (walking)
                    _x += 5f * (float)dt * MathF.Cos(yaw);
                if (!InThere && _x >= 256) Cross(_there, _x - 256, yaw);
                else if (InThere && _x < 0) Cross(_here, _x + 256, yaw);
                else if (++_sinceArrival >= 2)
                    _in.World.ApplyAvatar(_in.Sim, Self(_x, yaw));
            }
        }

        private void Cross(Region to, float x, float yaw)
        {
            Crossings++;
            _in = to;
            _x = x;
            _sinceArrival = 0;
            Driver.Rebind(to.Scene, to.World, keepHeading: true);   // a crossing; the left world keeps whatever it held
        }
    }

    [Fact]
    public void Walking_over_the_border_and_back_crosses_it_twice()
    {
        var walk = new Walk(240, staleCopyInThere: false, staleHeading: West);
        walk.Driver.Input.AdoptHeading(East);
        walk.Driver.Input.Press(MoveCommand.Forward, 0);

        walk.Seconds(6);   // east over the border and on
        Assert.True(walk.InThere);
        Assert.Equal(1, walk.Crossings);

        walk.Driver.Input.Release(MoveCommand.Forward);
        walk.Seconds(0.5);
        walk.Driver.Input.AdoptHeading(West);   // the user turns round
        walk.Driver.Input.Press(MoveCommand.Forward, 7);
        walk.Seconds(8);   // west over the border and on

        Assert.False(walk.InThere, walk.ToString());
        Assert.Equal(2, walk.Crossings);   // not back and forth
    }

    [Fact]
    public void A_stale_copy_of_the_agent_in_the_region_it_walks_into_does_not_turn_it_round()
    {
        // the region ahead still holds the avatar as it was when the agent last left it, facing west
        var walk = new Walk(250, staleCopyInThere: true, staleHeading: West);
        walk.Driver.Input.AdoptHeading(East);
        walk.Driver.Input.Press(MoveCommand.Forward, 0);

        walk.Seconds(10);

        Assert.True(walk.InThere);
        Assert.Equal(1, walk.Crossings);
        Assert.Equal(East, walk.Driver.Input.Heading!.Value, 3);
    }

    [Fact]
    public void After_a_rebind_the_driver_keeps_the_agents_own_heading()
    {
        var clock = new Clock();
        var a = new Region(Here, clock);
        a.World.ApplyAvatar(Here, Self(250, 0.5f));
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(a.Scene, a.World, u => { sent.Add(u); return true; });
        driver.Input.Press(MoveCommand.Forward, 0);
        driver.Frame(0.016, 0.016, Vector3.Zero, Vector3.UnitX);

        var b = new Region(There, clock);
        b.World.ApplyAvatar(There, Self(3, 0.9f));   // whatever the new region's copy says
        driver.Rebind(b.Scene, b.World, keepHeading: true);
        driver.Frame(0.5, 0.484, Vector3.Zero, Vector3.UnitX);

        Assert.Equal(0.5f, driver.Input.Heading!.Value, 4);
        Assert.Equal(0.5f, AvatarMotion.Yaw(sent[^1].BodyRotation), 4);
    }

    /// <summary>The own avatar's body faces the agent's own direction, never a region's copy of it: the official viewer takes the own
    /// avatar's base direction from the agent's at-axis (llvoavatar.cpp, LLVOAvatar::updateOrientation, isSelf branch). On the way back
    /// into a region visited before, that region still holds an old copy of the agent facing the other way; the body must not be drawn
    /// from it in any frame of the crossing, whatever its length (the slow frame turns the whole way at once).</summary>
    [Theory]
    [InlineData(0.016)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.86)]
    public void The_body_faces_the_agents_heading_in_every_frame_of_a_crossing_into_a_region_holding_an_old_copy(double handOverFrame)
    {
        var clock = new Clock();
        var a = new Region(Here, clock);
        a.World.ApplyAvatar(Here, Self(250, 0.5f));
        var driver = new OwnAvatarDriver(a.Scene, a.World, _ => true);
        driver.Input.Press(MoveCommand.Forward, 0);
        clock.Now = 0.016;
        driver.Frame(clock.Now, 0.016, Vector3.Zero, Vector3.UnitX);

        var b = new Region(There, clock);
        b.World.ApplyAvatar(There, Self(3, West));   // the agent as it was when it last left this region
        driver.Rebind(b.Scene, b.World, keepHeading: true);

        // drawn before the driver's next frame: the heading is already the agent's
        Assert.True(b.Scene.Place(b.Scene.Self!, out _, out float first));
        Assert.Equal(0.5f, first, 4);

        clock.Now += handOverFrame;
        b.World.ApplyAvatar(There, Self(3, West));   // the region entered states the avatar (its copy still says west)
        driver.Frame(clock.Now, handOverFrame, Vector3.Zero, Vector3.UnitX);
        Assert.True(b.Scene.Place(b.Scene.Self!, out _, out float second));
        Assert.Equal(0.5f, second, 4);
        Assert.True(a.Scene.Place(a.Scene.Self!, out _, out float left));
        Assert.Equal(0.5f, left, 4);
    }

    // ---------------- the world left behind ----------------

    private static readonly SimRef Home = new((ulong)(1000 * 256L) << 32 | (ulong)(1000 * 256L), "Home");
    private static readonly SimRef Next = new((ulong)(1001 * 256L) << 32 | (ulong)(1000 * 256L), "Next");

    [Fact]
    public async Task The_agents_own_avatar_leaves_the_world_it_left_when_the_region_it_crossed_into_sends_it_and_the_others_stay()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(new LoginCredentials("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Home);
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(Next, 256, 256));
        t.Raise(new AvatarUpdated(Home, Self(250, East)));
        t.Raise(new AvatarUpdated(Home, new AvatarData(801, Guid.NewGuid(), "Someone", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One)));
        var left = s.World;
        Assert.Equal(2, left.Snapshot().Avatars.Count);

        t.CurrentSim = Next;
        t.Raise(new SimConnected(Next, 256, 256));
        t.Raise(new AvatarUpdated(Next, Self(250, East)));   // the region the agent crossed into sends it: the one avatar moves there

        Assert.NotSame(left, s.World);
        Assert.NotNull(s.World.AvatarOf(s.AgentId));
        var remaining = left.Snapshot().Avatars.Values;
        Assert.DoesNotContain(remaining, a => a.AgentId == s.AgentId);
        Assert.Single(remaining);
    }
}

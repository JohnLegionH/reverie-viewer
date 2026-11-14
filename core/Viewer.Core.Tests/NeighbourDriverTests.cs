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

/// <summary>The own-avatar driver across a region crossing: the camera it reports is in the region's own frame, and the driver moves
/// to the new region's scene without losing what the keys were doing.</summary>
public class NeighbourDriverTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef There = new(256256UL << 32 | 256000UL, "There");
    private sealed class Clock { public double Now; }

    private static AvatarData Self(Vector3 pos, float heading) =>
        new(800, ServerAppearanceTests.Me, "Own Avatar", pos, AgentControlInput.BodyRotation(heading), new Vector3(0.45f, 0.6f, 1.9f));

    private static (AvatarScene Scene, WorldState World) Region(SimRef sim, Vector3 at, float heading, Clock clock)
    {
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        world.BeginRegion(sim, new RegionInfo(sim.Name, sim.GridX, sim.GridY, 256, 256, true));
        var scene = new AvatarScene(world, ServerAppearanceTests.Me, () => clock.Now);
        world.Changed += scene.Apply;
        world.ApplyAvatar(sim, Self(at, heading));
        return (scene, world);
    }

    [Fact]
    public void The_camera_the_region_is_told_about_is_in_its_own_frame()
    {
        var clock = new Clock();
        var (scene, world) = Region(Here, new(100, 100, 21.95f), 0, clock);
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(scene, world, u => { sent.Add(u); return true; }) { CameraShift = new Vector3(256, 0, 0) };

        driver.Frame(0.016, 0.016, new Vector3(350, 100, 23), new Vector3(354, 100, 23));

        var u = Assert.Single(sent);
        Assert.Equal(new Vector3(94, 100, 23), u.CameraEye);
        Assert.Equal(new Vector3(98, 100, 23), u.CameraTarget);
    }

    [Fact]
    public void Without_a_shift_the_camera_is_sent_as_given()
    {
        var clock = new Clock();
        var (scene, world) = Region(Here, new(100, 100, 21.95f), 0, clock);
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(scene, world, u => { sent.Add(u); return true; });

        driver.Frame(0.016, 0.016, new Vector3(97, 100, 23), new Vector3(101, 100, 23));

        Assert.Equal(new Vector3(97, 100, 23), Assert.Single(sent).CameraEye);
    }

    [Fact]
    public void After_a_rebind_the_held_key_carries_on_in_the_new_region_and_the_count_does_not_restart()
    {
        var clock = new Clock();
        var (sceneA, worldA) = Region(Here, new(250, 100, 21.95f), 0.5f, clock);
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(sceneA, worldA, u => { sent.Add(u); return true; });
        driver.Input.Press(MoveCommand.Forward, 0);
        driver.Frame(0.016, 0.016, Vector3.Zero, Vector3.UnitX);
        Assert.Equal(1, driver.Sent);

        var (sceneB, worldB) = Region(There, new(3, 100, 21.95f), 0.9f, clock);
        driver.Rebind(sceneB, worldB);
        driver.Frame(0.5, 0.484, Vector3.Zero, Vector3.UnitX);

        Assert.Equal(0.9f, driver.Input.Heading!.Value, 4);   // taken from the region the agent is in now (a teleport back, not a crossing)
        Assert.Equal(2, driver.Sent);
        Assert.Equal(AgentControlFlags.NudgeAtPos, sent[^1].Controls & AgentControlFlags.NudgeAtPos);
        Assert.Equal(0.9f, AvatarMotion.Yaw(sent[^1].BodyRotation), 4);
    }

    [Fact]
    public void After_a_rebind_the_driver_waits_for_the_new_regions_avatar_before_sending()
    {
        var clock = new Clock();
        var (sceneA, worldA) = Region(Here, new(250, 100, 21.95f), 0.5f, clock);
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(sceneA, worldA, u => { sent.Add(u); return true; });
        driver.Frame(0.016, 0.016, Vector3.Zero, Vector3.UnitX);
        Assert.Single(sent);

        var worldB = new WorldState(new CoreLog(new ListSink(), "test"));
        worldB.BeginRegion(There, new RegionInfo("There", There.GridX, There.GridY, 256, 256, true));
        var sceneB = new AvatarScene(worldB, ServerAppearanceTests.Me, () => clock.Now);
        driver.Rebind(sceneB, worldB);
        driver.Input.Press(MoveCommand.Forward, 0.4);   // something to say, so that silence is the new region's doing
        driver.Frame(0.5, 0.484, Vector3.Zero, Vector3.UnitX);

        Assert.Single(sent);   // its avatar is not in the new region's world yet: nothing goes until it is
    }

    /// <summary>A crossing: the official viewer's agent is one object and its updates go on every frame (llagent.cpp, sendAgentUpdate), also before the
    /// region entered has described the avatar. Here the driver keeps sending from the region left's copy of the avatar until the region entered has its own,
    /// so a held key is not dropped for the time a region takes to state an arriving avatar.</summary>
    [Fact]
    public void After_a_crossing_the_held_key_is_sent_before_the_region_entered_states_the_avatar()
    {
        var clock = new Clock();
        var (sceneA, worldA) = Region(Here, new(250, 100, 21.95f), 0.5f, clock);
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(sceneA, worldA, u => { sent.Add(u); return true; });
        driver.Input.Press(MoveCommand.Forward, 0);
        driver.Frame(0.016, 0.016, Vector3.Zero, Vector3.UnitX);
        Assert.Single(sent);

        var worldB = new WorldState(new CoreLog(new ListSink(), "test"));
        worldB.BeginRegion(There, new RegionInfo("There", There.GridX, There.GridY, 256, 256, true));
        var sceneB = new AvatarScene(worldB, ServerAppearanceTests.Me, () => clock.Now);
        driver.Rebind(sceneB, worldB, keepHeading: true);
        driver.Frame(0.5, 0.484, Vector3.Zero, Vector3.UnitX);   // the region entered has not stated the avatar yet

        Assert.Equal(2, sent.Count);
        Assert.Equal(AgentControlFlags.NudgeAtPos, sent[^1].Controls & AgentControlFlags.NudgeAtPos);
        Assert.Equal(0.5f, AvatarMotion.Yaw(sent[^1].BodyRotation), 4);   // the agent's own heading carries on
    }

    /// <summary>The frame between the region left letting go of the avatar (the region entered has stated it, so the region left hands it over) and the region
    /// entered's scene taking that statement (its changes are taken later in the frame): the official viewer calls send_agent_update in every frame
    /// (llappviewer.cpp idle), and only its own rules (teleport, logout, rate) keep an update back, never a missing avatar description. The held key goes.</summary>
    [Fact]
    public void In_the_frame_the_region_left_lets_go_before_the_region_entered_is_drawn_the_held_key_is_still_sent()
    {
        var clock = new Clock();
        var hub = new IdentityHub { SelfAgent = ServerAppearanceTests.Me };
        var worldA = new WorldState(new CoreLog(new ListSink(), "test"));
        var worldB = new WorldState(new CoreLog(new ListSink(), "test"));
        worldA.AttachIdentity(hub);
        worldB.AttachIdentity(hub);
        worldA.BeginRegion(Here, new RegionInfo("Here", Here.GridX, Here.GridY, 256, 256, true));
        worldB.BeginRegion(There, new RegionInfo("There", There.GridX, There.GridY, 256, 256, true));
        hub.Current = worldA;
        var sceneA = new AvatarScene(worldA, ServerAppearanceTests.Me, () => clock.Now) { HoldHandoffs = true };
        worldA.Changed += sceneA.Apply;
        worldA.ApplyAvatar(Here, Self(new(250, 100, 21.95f), 0.5f));
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(sceneA, worldA, u => { sent.Add(u); return true; });
        driver.Input.Press(MoveCommand.Forward, 0);
        driver.Frame(0.016, 0.016, Vector3.Zero, Vector3.UnitX);
        Assert.Single(sent);

        var sceneB = new AvatarScene(worldB, ServerAppearanceTests.Me, () => clock.Now) { HoldHandoffs = true };
        var waiting = new List<WorldChange>();
        worldB.Changed += waiting.Add;   // the region entered's changes wait for its scene's turn in the frame
        hub.Current = worldB;
        driver.Rebind(sceneB, worldB, keepHeading: true);
        worldB.ApplyAvatar(There, Self(new(3, 100, 21.95f), 0.5f) with { LocalId = 900 });
        Assert.Null(worldA.AvatarOf(ServerAppearanceTests.Me));   // the region left has let go of it
        Assert.Single(sceneA.Held);
        Assert.Null(sceneB.Self);

        driver.Frame(0.5, 0.484, Vector3.Zero, Vector3.UnitX);

        Assert.Equal(2, sent.Count);
        Assert.Equal(AgentControlFlags.NudgeAtPos, sent[^1].Controls & AgentControlFlags.NudgeAtPos);
        Assert.Equal(0.5f, AvatarMotion.Yaw(sent[^1].BodyRotation), 4);
    }

    /// <summary>The camera follows the user's own avatar in every frame, as the official camera follows the one agent avatar: from the region the agent is in,
    /// or, until that region has described the avatar, from the region it came from.</summary>
    [Fact]
    public void The_own_avatar_is_found_in_the_region_left_until_the_region_entered_states_it()
    {
        var clock = new Clock();
        var (sceneA, _) = Region(Here, new(250, 100, 21.95f), 0.5f, clock);
        var worldB = new WorldState(new CoreLog(new ListSink(), "test"));
        worldB.BeginRegion(There, new RegionInfo("There", There.GridX, There.GridY, 256, 256, true));
        var sceneB = new AvatarScene(worldB, ServerAppearanceTests.Me, () => clock.Now);

        var found = AvatarScene.FirstSelf(sceneB, sceneA);
        Assert.NotNull(found);
        Assert.Same(sceneA, found!.Value.Scene);
        Assert.Same(sceneA.Self, found.Value.Self);

        worldB.Changed += sceneB.Apply;
        worldB.ApplyAvatar(There, Self(new(3, 100, 21.95f), 0.5f));
        Assert.Same(sceneB, AvatarScene.FirstSelf(sceneB, sceneA)!.Value.Scene);   // once it is stated there, the region the agent is in
        Assert.Null(AvatarScene.FirstSelf(new AvatarScene(worldB, Guid.NewGuid(), () => 0), null));
    }
}

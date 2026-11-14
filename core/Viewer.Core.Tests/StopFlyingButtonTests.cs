// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Stop Flying button. The official viewer's stand/stop-flying panel (llmoveview.cpp, LLPanelStandStopFlying) shows Stop Flying
/// while the avatar flies and is not sitting, hides both buttons in mouse-look, and pressing Stop Flying ends the flight
/// (LLAgent::setFlying(false)). No engine and no grid; each test builds its own state, so the class runs in parallel.
/// </summary>
public class StopFlyingButtonTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Guid Me = Guid.Parse("5d2c1f0a-3b64-4e0e-8a7b-2f9d4c6e1b10");

    private sealed class Rig
    {
        public bool Flying;
        public int Toggles;
        public readonly InteractionController C;

        public Rig(bool flying = false)
        {
            Flying = flying;
            var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
            world.BeginRegion(Sim, Region);
            world.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(world, _ => new ActionResult(ActionStatus.Sent, 1), Me,
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
            C.SelfHooks = new SelfHooks(() => { }, () => { }, () => [], _ => { }, () => Flying, () => { Toggles++; Flying = !Flying; });
        }
    }

    [Fact]
    public void The_button_shows_only_while_flying_and_not_sitting_and_never_in_mouselook()
    {
        var r = new Rig();
        Assert.False(r.C.StopFlyingVisible(mouselook: false));
        r.Flying = true;
        Assert.True(r.C.StopFlyingVisible(mouselook: false), "flying and standing: Stop Flying shows");
        Assert.False(r.C.StopFlyingVisible(mouselook: true), "mouse-look hides the panel");
    }

    [Fact]
    public void Pressing_it_ends_the_flight_once_and_the_button_goes_away()
    {
        var r = new Rig(flying: true);
        r.C.StopFlying();
        Assert.False(r.Flying);
        Assert.Equal(1, r.Toggles);
        Assert.False(r.C.StopFlyingVisible(mouselook: false));
    }

    [Fact]
    public void Pressing_it_when_not_flying_does_nothing()
    {
        var r = new Rig(flying: false);
        r.C.StopFlying();
        Assert.Equal(0, r.Toggles);
    }

    /// <summary>The host sets the own-tag flag from the mouse-look mode each frame (the model already hides the own tag then, see
    /// NameTagModelTests); the host's node code cannot run here, so this reads its source for the assignment.</summary>
    [Fact]
    public void The_host_feeds_the_mouselook_mode_to_the_name_tags_every_frame()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.Names.cs"));
        int assign = source.IndexOf("_mouselook = _mouseLook.Active;", StringComparison.Ordinal);
        int plan = source.IndexOf("_tagModel.Update(inputs, cam, _mouselook)", StringComparison.Ordinal);
        Assert.True(assign >= 0, "the flag is never set from the mouse-look mode");
        Assert.True(plan > assign, "the flag must be set before the tags are planned");
    }
}

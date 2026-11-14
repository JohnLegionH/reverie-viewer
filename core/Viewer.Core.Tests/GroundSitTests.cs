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
/// Sitting on the ground. The official viewer counts an avatar as sitting while it plays the ground-sit animation as well as while it
/// has a parent (llvoavatar.cpp, LLVOAvatar::processAnimationStateChanges starts and stops sitDown for the constrained ground-sit
/// animation; llmoveview.cpp, LLPanelStandStopFlying shows Stand while isSitting). A seated avatar's menu offers Stand Up and the
/// Stand button shows, and standing sends the stand-up control.
/// No engine and no grid; each test builds its own world, so the class runs in parallel.
/// </summary>
public class GroundSitTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Guid Me = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");
    private static readonly Guid GroundSit = Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");
    private static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");

    private sealed class Rig
    {
        public readonly WorldState World = new(new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        public readonly List<ActionIntent> Sent = [];
        public readonly InteractionController C;

        public Rig()
        {
            World.BeginRegion(Sim, Region);
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, Me,
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
            C.SelfHooks = new SelfHooks(() => { }, () => { }, () => [], _ => { }, () => false, () => { });
        }

        public void Plays(params Guid[] animations) =>
            World.ApplyAnimations(Sim, Me, animations.Select((a, i) => new PlayingAnimation(a, i + 1)).ToArray());
    }

    [Fact]
    public void An_avatar_that_plays_the_ground_sit_animation_is_sitting()
    {
        var r = new Rig();
        Assert.False(r.C.Sitting);
        r.Plays(GroundSit);
        Assert.True(r.C.Sitting, "the ground-sit animation is what the official viewer reads as sitting on the ground");
        r.Plays(Stand);
        Assert.False(r.C.Sitting, "the animation stopped: the avatar stands");
    }

    [Fact]
    public void The_own_menu_offers_stand_while_sitting_on_the_ground_and_choosing_it_sends_a_stand()
    {
        var r = new Rig();
        r.Plays(GroundSit);
        r.C.RightClick(ObjectPickerAt(r.World));
        Assert.Equal(MenuKind.Self, r.C.Menu);
        Assert.Contains("stand", r.C.Entries.Select(e => e.Id));
        Assert.DoesNotContain("fly", r.C.Entries.Select(e => e.Id));
        Assert.True(r.C.Choose("stand"));
        Assert.IsType<StandIntent>(Assert.Single(r.Sent));
    }

    private static Viewer.Core.Interaction.PickResult ObjectPickerAt(WorldState world) =>
        Viewer.Core.Interaction.ObjectPicker.Pick(world, new Viewer.Core.Interaction.PickRay(new Vector3(100, 100, 60), new Vector3(0, 0, -1)), (_, _) => 20f);

    [Fact]
    public void Only_the_constrained_ground_sit_animation_counts()
    {
        Assert.True(AgentAnimations.IsSittingOnGround([new PlayingAnimation(GroundSit, 1)]));
        Assert.False(AgentAnimations.IsSittingOnGround([new PlayingAnimation(Stand, 1)]));
        Assert.False(AgentAnimations.IsSittingOnGround([]));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The avatar scene when an avatar's change reaches it before the region reset of its visit (the avatars' fast lane applies avatar changes
/// ahead of the objects queued before them, and the reset still comes later in the ordinary queue).</summary>
public class AvatarSceneEpochTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly SimRef There = new(256256UL << 32 | 256000UL, "There");
    private static readonly Guid Other = Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444");

    private static AvatarData Av(uint local, Guid agent, Vector3 pos) =>
        new(local, agent, "Test User", pos, Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), default, 0);

    private static (ViewerSession S, FakeTransport T, AvatarScene Scene, List<WorldChange> Changes) Rig()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        return (s, t, scene, changes);
    }

    [Fact]
    public void A_reset_of_the_visit_the_scene_already_holds_avatars_of_does_not_clear_them()
    {
        var (s, t, scene, changes) = Rig();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(128, 128, 25))));
        foreach (var c in changes) scene.Apply(c);
        Assert.Equal(1, scene.Count);

        scene.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(s.World.Epoch, 0)));   // the same visit's reset, late

        Assert.Equal(1, scene.Count);
        Assert.Equal(0, scene.Departures);
    }

    [Fact]
    public void A_reset_of_an_earlier_visit_does_not_clear_them_either()
    {
        var (s, t, scene, changes) = Rig();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(128, 128, 25))));
        foreach (var c in changes) scene.Apply(c);

        scene.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(s.World.Epoch - 1, 0)));

        Assert.Equal(1, scene.Count);
    }

    [Fact]
    public void A_reset_of_a_later_visit_clears_them()
    {
        var (s, t, scene, changes) = Rig();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(128, 128, 25))));
        foreach (var c in changes) scene.Apply(c);

        scene.Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(s.World.Epoch + 1, 0)));

        Assert.Equal(0, scene.Count);
        Assert.Equal(1, scene.Departures);
        Assert.Equal(s.World.Epoch + 1, scene.Epoch);
    }

    [Fact]
    public void The_first_avatar_of_a_new_visit_sends_the_earlier_visits_avatars_away_without_its_reset()
    {
        var (s, t, scene, changes) = Rig();
        t.Raise(new AvatarUpdated(Here, Av(900, Other, new(128, 128, 25))));
        foreach (var c in changes) scene.Apply(c);
        changes.Clear();

        t.CurrentSim = There;
        t.Raise(new SimConnected(There, 256, 256, true));   // a new visit: the world's reset is queued, and the avatar comes after it
        t.Raise(new AvatarUpdated(There, Av(901, Other, new(10, 10, 25))));
        var avatar = changes.First(c => c.Kind == WorldChangeKind.AvatarAdded);
        scene.Apply(avatar);   // ahead of the reset

        Assert.Equal(1, scene.Count);
        Assert.Equal(1, scene.Departures);
        Assert.Equal(901u, Assert.Single(scene.Entries).Id.LocalId);
        scene.Apply(changes.First(c => c.Kind == WorldChangeKind.RegionReset));   // the reset, when its turn comes, changes nothing
        Assert.Equal(1, scene.Count);
        Assert.Equal(1, scene.Departures);
    }
}

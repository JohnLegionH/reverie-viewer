// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The presentation attaches a moment after the region's first messages. An avatar the world already holds at that point (the user's own,
/// sent with the arrival) raised its change before anyone listened; the catch-up must give the avatars those changes again, or the
/// own avatar is never placed, followed by the camera or steered. The official viewer keeps one object list, so an object that arrived
/// earlier is as present as any other (llviewerobjectlist.cpp processObjectUpdate; llstartup.cpp STATE_WORLD_INIT onward).</summary>
public class PresentationCatchUpTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");

    private static AvatarData Av(uint local, Guid agent, Vector3 pos) =>
        new(local, agent, "Test User", pos, Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), default, 0);

    [Fact]
    public void The_own_avatar_that_arrived_before_the_presentation_attached_is_in_the_scene_after_the_catch_up()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        t.Raise(new AvatarUpdated(Here, Av(900, ServerAppearanceTests.Me, new(128, 128, 25))));   // before anyone listens

        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        foreach (var c in PresentationPolicy.CatchUp(s.World.Snapshot())) scene.Apply(c);

        Assert.NotNull(scene.Self);
        Assert.Equal(1, scene.Count);
    }

    [Fact]
    public void The_catch_up_names_every_avatar_of_the_visit_once()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        t.Raise(new AvatarUpdated(Here, Av(900, ServerAppearanceTests.Me, new(128, 128, 25))));
        t.Raise(new AvatarUpdated(Here, Av(901, Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444"), new(130, 128, 25))));

        var changes = PresentationPolicy.CatchUp(s.World.Snapshot()).ToList();

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => { Assert.Equal(WorldChangeKind.AvatarAdded, c.Kind); Assert.Equal(s.World.Epoch, c.Id.Epoch); });
    }

    [Fact]
    public void The_avatar_is_not_counted_twice_when_its_own_change_comes_after_the_catch_up()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        t.Raise(new AvatarUpdated(Here, Av(900, ServerAppearanceTests.Me, new(128, 128, 25))));
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        s.World.Changed += scene.Apply;

        foreach (var c in PresentationPolicy.CatchUp(s.World.Snapshot())) scene.Apply(c);
        t.Raise(new AvatarUpdated(Here, Av(900, ServerAppearanceTests.Me, new(129, 128, 25))));

        Assert.Equal(1, scene.Count);
        Assert.Equal(1, scene.Arrivals);
    }

    [Fact]
    public void A_world_with_no_avatars_has_nothing_to_catch_up()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);

        Assert.Empty(PresentationPolicy.CatchUp(s.World.Snapshot()));
    }
}

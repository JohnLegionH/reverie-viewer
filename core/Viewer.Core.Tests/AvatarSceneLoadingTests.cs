// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An avatar the presentation marks as still loading is not drawn, and neither is what it wears (the scene's Draws answers for both); the own
/// avatar's mouse-look rule is unchanged. Each test makes its own session and scene; runs in parallel.</summary>
public class AvatarSceneLoadingTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Other = Guid.Parse("5a1e0c4f-2222-4333-8444-955555555555");

    private static AvatarData Av(uint local, Guid agent) =>
        new(local, agent, "Test User", new Vector3(128, 128, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), default, 0);

    private static AvatarScene Rig()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => 0);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(Here, Av(900, Other)));
        foreach (var c in changes) scene.Apply(c);
        return scene;
    }

    [Fact]
    public void A_loading_avatar_is_not_drawn_and_is_drawn_once_it_is_not()
    {
        var scene = Rig();
        var e = scene.ByLocalId(900)!;
        Assert.True(scene.Draws(e));
        e.Loading = true;
        Assert.False(scene.Draws(e));
        e.Loading = false;
        Assert.True(scene.Draws(e));
    }
}

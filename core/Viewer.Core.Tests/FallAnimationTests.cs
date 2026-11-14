// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The animation the region plays while the avatar falls (FALLDOWN, the official viewer's llanimationstates.cpp ANIM_AGENT_FALLDOWN).
/// The region plays it when the avatar has no ground contact and its vertical speed is below 3 m/s downwards for a moment (OpenSim's
/// ScenePresenceAnimator), as when it walks off a ledge. It is a fall, so the avatar is in the air while it lasts. Every member builds its
/// own objects: no process-wide state, so the class runs in parallel with the others.
/// </summary>
public class FallAnimationTests
{
    private static readonly Guid FallDown = Guid.Parse("666307d9-a860-572d-6fd4-c3ab8865c094");
    private static readonly Guid Walk = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");

    private static PlayingAnimation[] Playing(Guid id) => [new PlayingAnimation(id, 1)];

    [Fact]
    public void The_fall_animation_has_a_name_in_reports()
    {
        Assert.Equal(["falldown"], AgentAnimations.NamesOf(Playing(FallDown)));
        Assert.Equal("falldown", AgentAnimations.Names[FallDown]);
    }

    [Fact]
    public void An_avatar_playing_the_fall_animation_is_in_the_air_and_is_not_flying()
    {
        Assert.True(AgentAnimations.InAir(Playing(FallDown)));
        Assert.False(AgentAnimations.IsFlying(Playing(FallDown)));
        Assert.False(AgentAnimations.InAir(Playing(Walk)));   // the ordinary walk is not
    }

    [Fact]
    public void Letting_go_of_the_walk_key_in_a_fall_sends_no_stop_bit_as_in_any_other_leap()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        double now = 0;
        input.Press(MoveCommand.Forward, now);
        bool inAir = AgentAnimations.InAir(Playing(FallDown));
        for (int i = 0; i < 60; i++) input.Frame(now += 1.0 / 60, 1.0 / 60, inAir, 3.2f);
        input.Release(MoveCommand.Forward);
        uint stops = 0;
        for (int i = 0; i < 30; i++) stops |= input.Frame(now += 1.0 / 60, 1.0 / 60, inAir, 3.2f) & F.Stop;
        Assert.Equal(0u, stops);   // the stop bit would cut the fall short the way it cuts a jump short
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Collections.Immutable;
using Viewer.Core.World;

namespace Viewer.Core.Avatars;

/// <summary>The grid's built-in movement animations by name (their asset ids are the same on every grid; the official
/// viewer's llanimationstates.cpp), for reports and for telling whether the region has the avatar in the air.</summary>
public static class AgentAnimations
{
    public static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string>
    {
        [Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f")] = "stand",
        [Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0")] = "walk",
        [Guid.Parse("f5fc7433-043d-e819-8298-f519a119b688")] = "female_walk",
        [Guid.Parse("05ddbff8-aaa9-92a1-2b74-8fe77a29b445")] = "run",
        [Guid.Parse("aec4610c-757f-bc4e-c092-c6e9caf18daf")] = "fly",
        [Guid.Parse("2b5a38b2-5e00-3a97-a495-4c826bc443e6")] = "fly_slow",
        [Guid.Parse("4ae8016b-31b9-03bb-c401-b1ea941db41d")] = "hover",
        [Guid.Parse("62c5de58-cb33-5743-3d07-9e4cd4352864")] = "hover_up",
        [Guid.Parse("20f063ea-8306-2562-0b07-5c853b37b31e")] = "hover_down",
        [Guid.Parse("7a4e87fe-de39-6fcb-6223-024b00893244")] = "pre_jump",
        [Guid.Parse("2305bd75-1ca9-b03b-1faa-b176b8a8c49e")] = "jump",
        [Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f")] = "land",
        [Guid.Parse("666307d9-a860-572d-6fd4-c3ab8865c094")] = "falldown",   // the region plays it while the avatar falls (no ground contact, falling faster than 3 m/s)
        [Guid.Parse("f4f00d6e-b9fe-9292-f4cb-0ae06ea58d57")] = "medium_land",
        [Guid.Parse("3da1d753-028a-5446-24f3-9c9b856d9422")] = "standup",
        [Guid.Parse("56e0ba0d-4a9f-7f27-6117-32f2ebbf6135")] = "turn_left",
        [Guid.Parse("2d6daa51-3192-6794-8e2e-a15f8338ec30")] = "turn_right",
        [Guid.Parse("201f3fdf-cb1f-dbec-201f-7333e328ae7c")] = "crouch",
        [Guid.Parse("47f5f6fb-22e5-ae44-f871-73aaaf4a6022")] = "crouch_walk",
        [Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e")] = "sit_ground",
    };

    private static readonly ImmutableHashSet<string> WalkMotions = ["walk", "female_walk", "run", "crouch_walk", "turn_left", "turn_right"];

    /// <summary>Whether the animation is one of the cycles whose playback rate follows the avatar's ground speed (the official
    /// viewer's walk, run, crouch-walk and turn-in-place motions).</summary>
    public static bool IsWalkMotion(Guid animation) => Names.TryGetValue(animation, out var n) && WalkMotions.Contains(n);

    private static readonly ImmutableHashSet<string> Airborne = ["fly", "fly_slow", "hover", "hover_up", "hover_down", "jump", "pre_jump", "falldown"];

    /// <summary>The names of what the avatar plays (an animation of no known name by its id).</summary>
    public static IEnumerable<string> NamesOf(IReadOnlyList<PlayingAnimation> playing) =>
        playing.Select(p => Names.TryGetValue(p.AnimationId, out var n) ? n : p.AnimationId.ToString());

    /// <summary>Whether the region has the avatar in the air: it plays a flying, hovering, jumping or falling animation.</summary>
    public static bool InAir(IReadOnlyList<PlayingAnimation> playing) =>
        playing.Any(p => Names.TryGetValue(p.AnimationId, out var n) && Airborne.Contains(n));

    /// <summary>Whether the avatar plays the constrained ground-sit animation: the official viewer's sign that an avatar sits on the ground,
    /// where nothing is its parent (llvoavatar.cpp, processAnimationStateChanges: sitDown starts and stops with that animation).</summary>
    public static bool IsSittingOnGround(IReadOnlyList<PlayingAnimation> playing) =>
        playing.Any(p => p.AnimationId == SitGroundConstrained);

    /// <summary>The constrained ground-sit animation (llanimationstates.cpp, ANIM_AGENT_SIT_GROUND_CONSTRAINED).</summary>
    public static readonly Guid SitGroundConstrained = Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");

    /// <summary>Whether the region has the avatar flying (in the air, not jumping).</summary>
    public static bool IsFlying(IReadOnlyList<PlayingAnimation> playing) =>
        playing.Any(p => Names.TryGetValue(p.AnimationId, out var n) && n is "fly" or "fly_slow" or "hover" or "hover_up" or "hover_down");
}

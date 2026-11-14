// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file aoset.h
 * @brief Implementation of an Animation Overrider set of animations
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2011, Zi Ree @ Second Life
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 * $/LicenseInfo$
 */

/**
 * @file aoset.cpp
 * @brief Implementation of an Animation Overrider set of animations
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2011, Zi Ree @ Second Life
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds: the set, its states, steps and tracks, the state names and the default animation each state
// replaces, and the choice of the next step or track animation, rewritten in C# from the files named above (the Firestorm
// viewer's indra/newview/aoset.h and aoset.cpp). The timer is a plain value driven by the engine's clock.

using System.Collections.Immutable;

namespace Viewer.Overrider;

/// <summary>The states a set has an animation list for, in Firestorm's order.</summary>
public enum AoStateId
{
    Standing = 0, Walking, Running, Sitting, SittingOnGround, Crouching, CrouchWalking, Landing, SoftLanding, StandingUp, Falling,
    FlyingDown, FlyingUp, Flying, FlyingSlow, Hovering, Jumping, PreJumping, TurningRight, TurningLeft, Typing,
    Floating, SwimmingForward, SwimmingUp, SwimmingDown, AlwaysPlay,
}

/// <summary>The grid's built-in animations the overrider names (the same asset ids on every grid).</summary>
internal static class AoAnims
{
    public static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    public static readonly Guid Stand1 = Guid.Parse("15468e00-3400-bb66-cecc-646d7c14458e");
    public static readonly Guid Stand2 = Guid.Parse("370f3a20-6ca6-9971-848c-9a01bc42ae3c");
    public static readonly Guid Stand3 = Guid.Parse("42b46214-4b44-79ae-deb8-0df61424ff4b");
    public static readonly Guid Stand4 = Guid.Parse("f22fed8b-a5ed-2c93-64d5-bdd8b93c889f");
    public static readonly Guid Walk = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    public static readonly Guid WalkNew = Guid.Parse("33339176-7ddc-9397-94a4-bf3403cbc8f5");
    public static readonly Guid FemaleWalk = Guid.Parse("f5fc7433-043d-e819-8298-f519a119b688");
    public static readonly Guid FemaleWalkNew = Guid.Parse("d60c41d2-7c24-7074-d3fa-6101cea22a51");
    public static readonly Guid Run = Guid.Parse("05ddbff8-aaa9-92a1-2b74-8fe77a29b445");
    public static readonly Guid RunNew = Guid.Parse("1ab1b236-cd08-21e6-0cbc-0d923fc6eca2");
    public static readonly Guid FemaleRunNew = Guid.Parse("85995026-eade-5d78-d364-94a64512cb66");
    public static readonly Guid Sit = Guid.Parse("1a5fe8ac-a804-8a5d-7cbd-56bd83184568");
    public static readonly Guid SitFemale = Guid.Parse("b1709c8d-ecd3-54a1-4f28-d55ac0840782");
    public static readonly Guid SitGeneric = Guid.Parse("245f3c54-f1c0-bf2e-811f-46d8eeb386e7");
    public static readonly Guid SitGround = Guid.Parse("1c7600d6-661f-b87b-efe2-d7421eb93c86");
    public static readonly Guid SitGroundConstrained = Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");
    public static readonly Guid Crouch = Guid.Parse("201f3fdf-cb1f-dbec-201f-7333e328ae7c");
    public static readonly Guid CrouchWalk = Guid.Parse("47f5f6fb-22e5-ae44-f871-73aaaf4a6022");
    public static readonly Guid Land = Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f");
    public static readonly Guid MediumLand = Guid.Parse("f4f00d6e-b9fe-9292-f4cb-0ae06ea58d57");
    public static readonly Guid StandUp = Guid.Parse("3da1d753-028a-5446-24f3-9c9b856d9422");
    public static readonly Guid FallDown = Guid.Parse("666307d9-a860-572d-6fd4-c3ab8865c094");
    public static readonly Guid HoverDown = Guid.Parse("20f063ea-8306-2562-0b07-5c853b37b31e");
    public static readonly Guid HoverUp = Guid.Parse("62c5de58-cb33-5743-3d07-9e4cd4352864");
    public static readonly Guid Fly = Guid.Parse("aec4610c-757f-bc4e-c092-c6e9caf18daf");
    public static readonly Guid FlySlow = Guid.Parse("2b5a38b2-5e00-3a97-a495-4c826bc443e6");
    public static readonly Guid Hover = Guid.Parse("4ae8016b-31b9-03bb-c401-b1ea941db41d");
    public static readonly Guid Jump = Guid.Parse("2305bd75-1ca9-b03b-1faa-b176b8a8c49e");
    public static readonly Guid PreJump = Guid.Parse("7a4e87fe-de39-6fcb-6223-024b00893244");
    public static readonly Guid TurnRight = Guid.Parse("2d6daa51-3192-6794-8e2e-a15f8338ec30");
    public static readonly Guid TurnLeft = Guid.Parse("56e0ba0d-4a9f-7f27-6117-32f2ebbf6135");
    public static readonly Guid Type = Guid.Parse("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");
}

/// <summary>One animation of a set: an inventory link and the asset it leads to.</summary>
public sealed class AoAnimation
{
    public string Name = "";
    public Guid AssetId;
    public Guid InventoryId;
    public Guid OriginalId;
    public int SortOrder = -1;
}

/// <summary>One step of a state: a single animation, or a group of animations that play together.</summary>
public sealed class AoStep
{
    public string Name = "";
    public Guid InventoryId;
    public int SortOrder = -1;
    public bool IsGroup;
    public readonly List<AoAnimation> Members = [];
}

/// <summary>A track of a state: animations that play beside the state's steps, with a cycle of their own.</summary>
public sealed class AoTrack
{
    public string Name = "";
    public Guid InventoryId;
    public bool Cycle;
    public bool Random;
    public float CycleTime;
    public readonly List<AoAnimation> Animations = [];
    public int CurrentAnimation;
    public Guid CurrentAnimationId;
}

/// <summary>One state of a set.</summary>
public sealed class AoState
{
    public string Name = "";
    public IReadOnlyList<string> AlternateNames = [];
    public AoStateId Num;
    public Guid RemapId;
    public bool Cycle;
    public bool Random;
    public float CycleTime;
    public readonly List<AoStep> Steps = [];
    public readonly List<AoTrack> Tracks = [];
    public int CurrentAnimation;
    public List<Guid> CurrentAnimationIds = [];
    public Guid InventoryId;
}

/// <summary>A repeating timer read against the engine's clock: due once its period has passed since it was started or last fired.</summary>
public sealed class AoTimer
{
    public double Period = 10000;
    public bool Running;
    public double Since;

    public void Start(double period, double now)
    {
        Period = period;
        Since = now;
        Running = true;
    }

    /// <summary>Whether the timer fires at <paramref name="now"/>; a timer that fires starts its next period.</summary>
    public bool Fires(double now)
    {
        if (!Running || now - Since < Period)
            return false;
        Since = now;
        return true;
    }
}

/// <summary>One animation set.</summary>
public sealed class AoSet
{
    // ZHAO names first, alternate names following, separated by | characters; the order is the order of AoStateId
    private static readonly ImmutableArray<string> StateNames =
    [
        "Standing|Stand.1|Stand.2|Stand.3",
        "Walking|Walk.N",
        "Running",
        "Sitting|Sit.N",
        "Sitting On Ground|Sit.G",
        "Crouching|Crouch",
        "Crouch Walking|Walk.C",
        "Landing|Land.N",
        "Soft Landing",
        "Standing Up|Stand.U",
        "Falling",
        "Flying Down|Hover.D",
        "Flying Up|Hover.U",
        "Flying|Fly.N",
        "Flying Slow",
        "Hovering|Hover.N",
        "Jumping|Jump.N",
        "Pre Jumping|Jump.P",
        "Turning Right|Turn.R",
        "Turning Left|Turn.L",
        "Typing",
        "Floating|Swim.H",
        "Swimming Forward|Swim.N",
        "Swimming Up|Swim.U",
        "Swimming Down|Swim.D",
        "Always",
    ];

    // the default animation each state replaces, in the same order
    private static readonly ImmutableArray<Guid> StateIds =
    [
        AoAnims.Stand,
        AoAnims.Walk,
        AoAnims.Run,
        AoAnims.Sit,
        AoAnims.SitGroundConstrained,
        AoAnims.Crouch,
        AoAnims.CrouchWalk,
        AoAnims.Land,
        AoAnims.MediumLand,
        AoAnims.StandUp,
        AoAnims.FallDown,
        AoAnims.HoverDown,
        AoAnims.HoverUp,
        AoAnims.Fly,
        AoAnims.FlySlow,
        AoAnims.Hover,
        AoAnims.Jump,
        AoAnims.PreJump,
        AoAnims.TurnRight,
        AoAnims.TurnLeft,
        AoAnims.Type,
        AoAnims.Hover,       // needs special treatment
        AoAnims.Fly,         // needs special treatment
        AoAnims.HoverUp,     // needs special treatment
        AoAnims.HoverDown,   // needs special treatment
        Guid.Empty,          // "Always" has no motion to override
    ];

    /// <summary>How many states a set has.</summary>
    public static int StateCount => StateNames.Length;

    private readonly AoState[] _states;

    public AoSet(Guid inventoryId)
    {
        InventoryId = inventoryId;
        _states = new AoState[StateNames.Length];
        for (int index = 0; index < _states.Length; index++)
        {
            string[] names = StateNames[index].Split('|');
            _states[index] = new AoState
            {
                Name = names[0],            // for quick reference
                AlternateNames = names,     // all possible names, the first included
                Num = (AoStateId)index,
                RemapId = StateIds[index],
            };
        }
    }

    public Guid InventoryId { get; }
    public string Name { get; set; } = "** New AO Set **";
    public bool SitOverride { get; set; }
    public bool Smart { get; set; }
    public bool MouselookStandDisable { get; set; }
    public bool Complete { get; set; }

    /// <summary>The default animation the set last replaced (never the typing one).</summary>
    public Guid Motion { get; set; }

    /// <summary>The cycle timer of the state the set plays.</summary>
    public AoTimer Timer { get; } = new();

    public AoState GetState(AoStateId id) => _states[(int)id];

    public AoState? GetStateByName(string name)
    {
        foreach (var state in _states)
        {
            foreach (string alternate in state.AlternateNames)
            {
                if (string.CompareOrdinal(alternate, name) == 0)
                    return state;
            }
        }
        return null;
    }

    public AoState? GetStateByRemapId(Guid id)
    {
        // "Always" state has a null remap id and must never be picked up by motion mapping
        if (id == Guid.Empty)
            return null;

        Guid remapId = id == AoAnims.SitGround ? AoAnims.SitGroundConstrained : id;
        foreach (var state in _states)
        {
            if (state.RemapId == remapId)
                return state;
        }
        return null;
    }

    /// <summary>The asset of an animation, looked up again while it is not known.</summary>
    internal static Guid ResolveAsset(AoAnimation anim, IAoInventory inventory)
    {
        if (anim.AssetId == Guid.Empty)
            anim.AssetId = inventory.AssetOf(anim.InventoryId);
        return anim.AssetId;
    }

    /// <summary>The animations the state plays next: a cycling state moves to its next (or a random) step first.</summary>
    internal List<Guid> GetAnimationsForState(AoState? state, IAoInventory inventory, Func<double> random)
    {
        var animations = new List<Guid>();
        if (state is null)
            return animations;

        int numOfSteps = state.Steps.Count;
        if (numOfSteps > 0)
        {
            if (state.Cycle)
            {
                if (state.Random)
                {
                    state.CurrentAnimation = (int)(random() * numOfSteps);
                }
                else
                {
                    state.CurrentAnimation++;
                    if (state.CurrentAnimation >= state.Steps.Count)
                        state.CurrentAnimation = 0;
                }
            }

            // steps can shrink, keep the index in range
            if (state.CurrentAnimation >= state.Steps.Count || state.CurrentAnimation < 0)
                state.CurrentAnimation = 0;

            foreach (var anim in state.Steps[state.CurrentAnimation].Members)
            {
                Guid assetId = ResolveAsset(anim, inventory);
                if (assetId != Guid.Empty)
                    animations.Add(assetId);
            }
        }
        return animations;
    }

    /// <summary>The animation the track plays next.</summary>
    internal Guid GetAnimationForTrack(AoTrack track, IAoInventory inventory, Func<double> random)
    {
        int numOfAnimations = track.Animations.Count;
        if (numOfAnimations > 0)
        {
            if (track.Cycle)
            {
                if (track.Random)
                {
                    track.CurrentAnimation = (int)(random() * numOfAnimations);
                }
                else
                {
                    track.CurrentAnimation++;
                    if (track.CurrentAnimation >= track.Animations.Count)
                        track.CurrentAnimation = 0;
                }
            }

            if (track.CurrentAnimation >= track.Animations.Count || track.CurrentAnimation < 0)
                track.CurrentAnimation = 0;

            return ResolveAsset(track.Animations[track.CurrentAnimation], inventory);
        }
        return Guid.Empty;
    }
}

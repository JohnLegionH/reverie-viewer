// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>Which foot touched the ground.</summary>
public enum Foot { None, Left, Right }

/// <summary>
/// Footsteps as the official viewer plays them: while an avatar that is not sitting plays its walk, run or landing animation, a foot whose
/// ankle comes down to 0.05 m or less above the ground under it, having been higher, makes one step at that foot, gain 0.1, as an ambient
/// sound. The sound is the land step on the ground, or the step for the material of the object stood on.
/// </summary>
public sealed class FootstepDetector
{
    /// <summary>How close to the ground an ankle counts as down.</summary>
    public const float GroundedBelow = 0.05f;

    /// <summary>A step's gain.</summary>
    public const float StepGain = 0.1f;

    /// <summary>One frame: each ankle's height over the ground under it, whether a step animation plays, and whether the avatar sits.
    /// Returns the foot that came down this frame (the right one when both did).</summary>
    public Foot Update(float leftAboveGround, float rightAboveGround, bool stepAnimationPlaying, bool sitting)
    {
        if (sitting || !stepAnimationPlaying)
        {
            // the official viewer keeps its foot memory only while a step animation plays
            _last = Foot.None;
            return Foot.None;
        }
        bool left = leftAboveGround <= GroundedBelow, right = rightAboveGround <= GroundedBelow;
        var foot = Foot.None;
        if (left && !_wasLeft && _last != Foot.Left)
            foot = Foot.Left;
        if (right && !_wasRight && _last != Foot.Right)
            foot = Foot.Right;
        _wasLeft = left;
        _wasRight = right;
        if (foot != Foot.None)
            _last = foot;
        return foot;
    }

    // Beyond the official rule: the feet take turns. The default walk lowers a foot to the ground twice in one stride at some heights
    // (the heel, then the sole), which the official 5 cm test alone would count as two steps; a foot that stepped last cannot step
    // again until the other has.
    private bool _wasLeft, _wasRight;
    private Foot _last;
}

/// <summary>The official step sounds and the animations that make steps.</summary>
public static class StepSounds
{
    public static readonly Guid OnLand = Guid.Parse("e8af4a28-aa83-4310-a7c4-c047e15ea0df");

    /// <summary>By the object's material (the protocol's numbers: stone 0, metal 1, glass 2, wood 3, flesh 4, plastic 5, rubber 6).</summary>
    public static readonly IReadOnlyList<Guid> ByMaterial =
    [
        Guid.Parse("25a863e8-dc42-4e8a-a357-e76422ace9b5"),
        Guid.Parse("8761f73f-6cf9-4186-8aaa-0948ed002db1"),
        Guid.Parse("2de4da5a-faf8-46be-bac6-c4d74f1e5767"),
        Guid.Parse("7d1826f4-24c4-4aac-8c2e-eff45df37783"),
        Guid.Parse("68d62208-e257-4d0c-bbe2-20c9ea9760bb"),
        Guid.Parse("75cf3ade-9a5b-4c4d-bb35-f9799bda7fb2"),
        Guid.Parse("153c8bf7-fb89-4d89-b263-47e58b1b4774"),
    ];

    public static readonly Guid Walk = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    public static readonly Guid Run = Guid.Parse("05ddbff8-aaa9-92a1-2b74-8fe77a29b445");
    public static readonly Guid Land = Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f");

    /// <summary>The typing animation: when it starts on an avatar, the typing sound plays there.</summary>
    public static readonly Guid Type = Guid.Parse("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");

    /// <summary>The step sound: on land, or on an object of a material (an unknown material counts as stone).</summary>
    public static Guid For(bool onLand, int material) => onLand ? OnLand : material >= 0 && material < ByMaterial.Count ? ByMaterial[material] : ByMaterial[0];

    /// <summary>Whether a set of playing animations makes steps (walk, run or land).</summary>
    public static bool MakesSteps(IEnumerable<Guid> playing) => playing.Any(a => a == Walk || a == Run || a == Land);
}

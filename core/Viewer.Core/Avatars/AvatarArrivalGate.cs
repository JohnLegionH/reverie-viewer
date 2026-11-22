// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// Whether another avatar is drawn at all while its look is still arriving: the official viewer's "fully loaded" rule (LLVOAvatar::updateIsFullyLoaded
/// and processFullyLoadedChange, llvoavatar.cpp 8427-8576). Until an avatar is fully loaded the official viewer draws neither its body
/// (lldrawpoolavatar.cpp 706) nor anything it wears (lldrawable.cpp 1505-1540); it shows a loading cloud in its place.
/// <list type="bullet">
/// <item>Before it has been shown once, the avatar is loading while its appearance is missing a part (no shape, or no head, upper or lower bake:
/// getHasMissingParts, 8234-8247) or a bake it names is still downloading (rez status below 3, getRezzedStatus 926-936); each worn mesh that arrives
/// in that time starts the wait again (notifyAttachmentMeshLoaded, 6650-6658). It is shown once it has not been loading for 3 seconds
/// (FIRST_APPEARANCE_CLOUD_MIN_DELAY).</item>
/// <item>After it has been shown, only a missing part makes it load again, and it is shown 1 second after that ends (LOADED_DELAY).</item>
/// </list>
/// Here the cloud is not drawn: the avatar and what it wears are simply not drawn. Two additions: an avatar still loading after
/// <see cref="GreyAfterSeconds"/> is shown anyway, its parts without a bake plain grey (<see cref="BaseBodyRule"/>), as the official viewer shows a missing
/// look, where it would keep the cloud; and an avatar another region was already drawing (<see cref="TakeOver"/>) is never held back. Never used for the user's own avatar. Times are seconds on any one clock.
/// </summary>
public sealed class AvatarArrivalGate
{
    /// <summary>How long an avatar shown for the first time must have stopped loading (FIRST_APPEARANCE_CLOUD_MIN_DELAY, llvoavatar.cpp 134).</summary>
    public const double FirstDelaySeconds = 3.0;
    /// <summary>How long an avatar shown before must have stopped loading again (LOADED_DELAY, llvoavatar.cpp processFullyLoadedChange).</summary>
    public const double LaterDelaySeconds = 1.0;
    /// <summary>How long an avatar whose look is still incomplete is waited for before it is drawn as a missing look (FIRST_APPEARANCE_CLOUD_MAX_DELAY,
    /// llvoavatar.cpp 135, the longest the official viewer holds a first appearance back).</summary>
    public const double GreyAfterSeconds = 15.0;

    /// <summary>Whether the look has been incomplete for <see cref="GreyAfterSeconds"/>: the avatar is drawn anyway, and a part with no usable
    /// bake is drawn plain grey (<see cref="BaseBodyRule"/>). Ends when the look is complete.</summary>
    public bool Fallback { get; private set; }

    /// <summary>The avatar was already drawn by the region it came from: it stays drawn through the crossing, with no second wait.</summary>
    public void TakeOver() => _handedOver = true;

    /// <param name="now">When the avatar was first seen.</param>
    public AvatarArrivalGate(double now)
    {
        _seenAt = now;
        _quietSince = now;
        _stuckSince = now;
    }

    private readonly double _seenAt;
    private bool _handedOver;
    private double? _stuckSince;   // since when the look has been incomplete (missing a part, or a bake not decided); null while complete
    private double _quietSince;   // when it last stopped loading (or a worn mesh last arrived before the first showing)

    /// <summary>Whether the avatar has been shown at least once.</summary>
    public bool EverShown { get; private set; }

    /// <summary>A worn piece of the avatar arrived or its mesh came in (before the first showing, that starts the wait again).</summary>
    public void WornArrived(double now)
    {
        if (!EverShown && now > _quietSince) _quietSince = now;
    }

    /// <summary>One frame: whether the avatar is drawn. <paramref name="missingParts"/>: no shape, or no head, upper or lower bake;
    /// <paramref name="bakesPending"/>: a bake it names is not decided yet (not on the GPU, and its alpha not known to hide the part).</summary>
    public bool Update(double now, bool missingParts, bool bakesPending)
    {
        bool stuck = missingParts || bakesPending;
        if (!stuck) _stuckSince = null; else _stuckSince ??= now;
        Fallback = _stuckSince is { } since && now - since >= GreyAfterSeconds;
        bool loading = missingParts || (!EverShown && bakesPending);
        if (loading) _quietSince = now;
        if (Fallback) _quietSince = now - LaterDelaySeconds;   // drawn in the fallback: when the look completes it stays drawn, it is not hidden again
        bool shown = _handedOver || Fallback || now - _quietSince > (EverShown ? LaterDelaySeconds : FirstDelaySeconds);
        if (shown) EverShown = true;
        return shown;
    }
}

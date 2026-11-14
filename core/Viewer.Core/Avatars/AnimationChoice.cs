// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Core.Avatars;

/// <summary>
/// Which one animation an avatar is drawn with, from what the region says it plays. Of the playing animations whose
/// asset is at hand (fetched and read), the one with the highest base priority wins, and between equal priorities the
/// one the region started last (the higher sequence number). When none is at hand, or the region names none, the
/// built-in stand plays if it is at hand; otherwise the default pose. One animation at a time: blending several by their
/// joints' priorities is later work.
/// </summary>
public static class AnimationChoice
{
    /// <summary>The grid's built-in stand and walk (their asset ids are the same on every grid).</summary>
    public static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f"), Walk = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");

    /// <summary>The animation to draw, or null for the default pose. <paramref name="priorityOf"/> gives an animation's
    /// base priority when its asset is at hand, null while it is not.</summary>
    public static Guid? Choose(IReadOnlyList<PlayingAnimation> playing, Func<Guid, int?> priorityOf)
    {
        Guid? best = null;
        int bestPriority = int.MinValue, bestSequence = int.MinValue;
        foreach (var p in playing)
        {
            if (priorityOf(p.AnimationId) is not int priority) continue;
            if (priority > bestPriority || (priority == bestPriority && p.Sequence > bestSequence))
            {
                best = p.AnimationId;
                bestPriority = priority;
                bestSequence = p.Sequence;
            }
        }
        if (best is not null) return best;
        return priorityOf(Stand) is not null ? Stand : null;
    }

    /// <summary>The animations worth fetching for an avatar: those it plays, and the stand (its fallback).</summary>
    public static IEnumerable<Guid> Wanted(IReadOnlyList<PlayingAnimation> playing) =>
        playing.Select(p => p.AnimationId).Append(Stand).Distinct();
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Appearance;

/// <summary>A link in the current outfit folder: its own id, the item it points to, and its note (the layer order the outfit keeps there).</summary>
internal readonly record struct CofLink(Guid LinkId, Guid TargetId, string Note);

/// <summary>Finds the current-outfit link of an inventory item, so a save can refresh it and Save As can hand its place to the copy.</summary>
internal static class WearableLinks
{
    /// <summary>The first link that points to <paramref name="itemId"/>, or null when the item is not worn.</summary>
    public static CofLink? LinkTo(IEnumerable<CofLink> links, Guid itemId)
    {
        foreach (var l in links)
            if (l.TargetId == itemId)
                return l;
        return null;
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Shell.Outfit;

/// <summary>What the own avatar's Take Off menu lists: the current outfit's entries that can be taken off (clothing and worn objects,
/// never a body part), each with its clothing type, its layer and whether it is an object.</summary>
public static class WornList
{
    /// <summary>The inventory items the avatar wears or has attached.</summary>
    /// <remarks>The current outfit's links and the attached objects' items (one put on from the ground has no link). A link to
    /// something that is not worn, a gesture for one, is left out; an item not fetched yet is kept, so it can be asked for.</remarks>
    public static IReadOnlySet<Guid> Items(InventoryModel model, IReadOnlySet<Guid> attached) =>
        OutfitPlanner.Current(model).Select(e => e.TargetId).Concat(attached)
            .Where(id => model.Item(id) is not { } item || item.Kind is ItemKind.Clothing or ItemKind.Bodypart or ItemKind.Object)
            .ToHashSet();

    public static IReadOnlyList<WornEntry> Of(InventoryModel model) =>
        OutfitPlanner.Current(model).Where(e => e.CanTakeOff).Select(e => new WornEntry(e.TargetId, e.Name, e.Type, e.Layer, e.Section == 0)).ToArray();
}

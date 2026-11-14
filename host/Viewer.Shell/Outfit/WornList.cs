// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Shell.Outfit;

/// <summary>What the own avatar's Take Off menu lists: the current outfit's entries that can be taken off (clothing and worn objects,
/// never a body part), each with its clothing type, its layer and whether it is an object.</summary>
public static class WornList
{
    public static IReadOnlyList<WornEntry> Of(InventoryModel model) =>
        OutfitPlanner.Current(model).Where(e => e.CanTakeOff).Select(e => new WornEntry(e.TargetId, e.Name, e.Type, e.Layer, e.Section == 0)).ToArray();
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

/// <summary>Maps the protocol's numbers to this viewer's kinds. The numbers are the grid's; one this viewer does not know
/// becomes <see cref="FolderKind.Other"/> or <see cref="ItemKind.Other"/> and is shown as an ordinary folder or item.</summary>
public static class InventoryKinds
{
    public static FolderKind FolderKindOf(int number) => number switch
    {
        -1 => FolderKind.None,
        0 or 1 or 2 or 3 or 5 or 6 or 7 or 8 or 10 or 13 or 14 or 15 or 16 or 20 or 21 or 23 or 46 or 47 or 48 or 49 or 50 or 51 or 52
            or 53 or 54 or 55 or 56 or 57 => (FolderKind)number,
        _ => FolderKind.Other,
    };

    /// <summary>The kind from an item's asset type number (links keep their own kinds).</summary>
    public static ItemKind ItemKindOf(int assetType) => assetType switch
    {
        0 or 12 or 18 or 19 => ItemKind.Texture,
        1 or 17 => ItemKind.Sound,
        2 => ItemKind.CallingCard,
        3 => ItemKind.Landmark,
        4 or 10 or 11 => ItemKind.Script,
        5 => ItemKind.Clothing,
        6 => ItemKind.Object,
        7 => ItemKind.Notecard,
        13 => ItemKind.Bodypart,
        20 => ItemKind.Animation,
        21 => ItemKind.Gesture,
        24 => ItemKind.Link,
        25 => ItemKind.LinkFolder,
        49 => ItemKind.Mesh,
        56 => ItemKind.Settings,
        57 => ItemKind.Material,
        _ => ItemKind.Other,
    };
}

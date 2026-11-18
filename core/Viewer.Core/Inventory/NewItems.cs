// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Appearance;

namespace Viewer.Core.Inventory;

public enum NewItemGroup { Top, Clothing, BodyParts }

public sealed record NewItemEntry(string Id, NewItemGroup Group, string DefaultName, ItemKind Kind, int AssetType, int InventoryType, WearableType Wearable, uint NextOwnerMask)
{
    public bool IsFolder => false;
}

public static class NewItemCatalog
{
    public static IReadOnlyList<NewItemEntry> Entries { get; } = [];
    public static NewItemEntry? Of(string id) => new("", NewItemGroup.Top, "", ItemKind.Other, 0, 0, WearableType.None, 0);
    public static byte[] DefaultWearable(WearableType type, string name, EditParamCatalog catalog) => [];
}

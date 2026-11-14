// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;

namespace Viewer.Core.Appearance;

/// <summary>Reads a wearable asset from the grid, so it can be opened in the editor. Implemented by the protocol adapter.</summary>
public interface IWearableTransport
{
    /// <summary>Fetches the asset of a wearable (asset type 13 for a body part, 5 for clothing).</summary>
    Task<AssetFetchResult> FetchWearableAsync(Guid assetId, int assetType, CancellationToken ct);

    /// <summary>What the grid's copy of an inventory item says: the asset it points at, its asset type number and what the agent may do with it. Null when the item is not known.</summary>
    WearableItemInfo? ItemInfo(Guid itemId);

    /// <summary>The texture asset an inventory texture item points at (the empty id when the item is unknown or not a texture).</summary>
    Guid TextureAssetOf(Guid itemId);
}

/// <summary>An inventory wearable item as the editor needs it.</summary>
/// <param name="AssetId">The wearable file the item points at.</param>
/// <param name="CanModify">The agent may edit the item (the owner mask allows modify).</param>
/// <param name="CanCopy">The agent may copy it (needed for Save As).</param>
public sealed record WearableItemInfo(Guid AssetId, int AssetType, bool CanModify, bool CanCopy, bool CanTransfer = true)
{
    /// <summary>Every right the grid asks for before a wearable may use a texture: modify, copy and transfer.</summary>
    public bool FullRights => CanModify && CanCopy && CanTransfer;
}

/// <summary>The permission bits the editor looks at (the protocol's permission mask values).</summary>
public static class WearablePermissions
{
    public const uint Modify = 0x4000, Copy = 0x8000, Transfer = 0x2000;

    public static bool CanModify(uint ownerMask) => (ownerMask & Modify) != 0;

    public static bool CanCopy(uint ownerMask) => (ownerMask & Copy) != 0;

    public static bool CanTransfer(uint ownerMask) => (ownerMask & Transfer) != 0;
}

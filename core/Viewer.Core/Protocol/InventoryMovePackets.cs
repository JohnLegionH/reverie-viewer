// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Viewer.Core.Protocol;

/// <summary>The inventory move messages.</summary>
internal static class InventoryMovePackets
{
    public static MoveInventoryItemPacket Item(UUID agent, UUID session, Guid item, Guid folder, string? newName) =>
        new() { InventoryData = Array.Empty<MoveInventoryItemPacket.InventoryDataBlock>() };

    public static MoveInventoryFolderPacket Folder(UUID agent, UUID session, Guid folder, Guid newParent) =>
        new() { InventoryData = Array.Empty<MoveInventoryFolderPacket.InventoryDataBlock>() };
}

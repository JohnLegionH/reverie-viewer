// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Viewer.Core.Protocol;

/// <summary>
/// The inventory move messages, built as the official viewer sends them (llviewerinventory.cpp, updateParentOnServer for an
/// item and for a category): one block naming the item or folder and its new parent, Stamp false (a drag does not restamp),
/// and for an item an empty new name unless a rename rides with the move. The library's own move calls are not used: when the
/// grid offers the inventory capability they send the move as a PATCH of the item's parent instead, which a grid may answer
/// with success while leaving the item where it was.
/// </summary>
internal static class InventoryMovePackets
{
    public static MoveInventoryItemPacket Item(UUID agent, UUID session, Guid item, Guid folder, string? newName)
    {
        var p = new MoveInventoryItemPacket
        {
            AgentData = { AgentID = agent, SessionID = session, Stamp = false },
            InventoryData = new[]
            {
                new MoveInventoryItemPacket.InventoryDataBlock { ItemID = new UUID(item), FolderID = new UUID(folder), NewName = Utils.StringToBytes(newName ?? "") },
            },
        };
        p.Header.Reliable = true;
        return p;
    }

    public static MoveInventoryFolderPacket Folder(UUID agent, UUID session, Guid folder, Guid newParent)
    {
        var p = new MoveInventoryFolderPacket
        {
            AgentData = { AgentID = agent, SessionID = session, Stamp = false },
            InventoryData = new[] { new MoveInventoryFolderPacket.InventoryDataBlock { FolderID = new UUID(folder), ParentID = new UUID(newParent) } },
        };
        p.Header.Reliable = true;
        return p;
    }
}

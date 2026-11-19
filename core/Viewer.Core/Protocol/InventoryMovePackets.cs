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

    /// <summary>UpdateInventoryItem for one item, as the official viewer's updateServer sends it for a rename: the whole item, so its
    /// rights, owner, creator, sale and flags go back as the grid gave them (a zero transaction: no asset changes with it).</summary>
    public static UpdateInventoryItemPacket Update(UUID agent, UUID session, Viewer.Core.Inventory.InventoryItemData item)
    {
        var rights = item.Permissions ?? throw new ArgumentException("the item has no permissions", nameof(item));
        var p = new UpdateInventoryItemPacket
        {
            AgentData = { AgentID = agent, SessionID = session, TransactionID = UUID.Zero },
            InventoryData = new[]
            {
                new UpdateInventoryItemPacket.InventoryDataBlock
                {
                    ItemID = new UUID(item.Id), FolderID = new UUID(item.ParentId), CallbackID = 0,
                    CreatorID = new UUID(rights.CreatorId), OwnerID = new UUID(rights.OwnerId), GroupID = new UUID(rights.GroupId),
                    BaseMask = rights.BaseMask, OwnerMask = rights.OwnerMask, GroupMask = rights.GroupMask, EveryoneMask = rights.EveryoneMask,
                    NextOwnerMask = rights.NextOwnerMask, GroupOwned = rights.GroupOwned, TransactionID = UUID.Zero,
                    Type = (sbyte)item.AssetType, InvType = (sbyte)item.InventoryType, Flags = item.Flags,
                    SaleType = (byte)rights.SaleType, SalePrice = rights.SalePrice,
                    Name = Utils.StringToBytes(item.Name), Description = Utils.StringToBytes(item.Description),
                    CreationDate = item.CreatedAt is { } at ? (int)at.ToUnixTimeSeconds() : 0, CRC = 0,
                },
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

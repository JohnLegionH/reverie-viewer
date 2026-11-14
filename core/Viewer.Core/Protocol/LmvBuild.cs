// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Building;

namespace Viewer.Core.Protocol;

/// <summary>
/// Building messages, the protocol adapter's side: the create tool's ObjectAdd, rezzing an inventory object (RezObject), taking and
/// deleting (DeRezObject), and the texture upload's two capability steps. The message layouts follow the protocol's message template;
/// the values follow the official viewer's behaviour (lltoolplacer.cpp, lltooldraganddrop.cpp, llviewermenu.cpp, llselectmgr.cpp,
/// llviewerassetupload.cpp; read as behaviour only).
/// </summary>
internal static class LmvBuild
{
    /// <summary>The permission bits the protocol uses for an upload's masks.</summary>
    public const uint PermTransfer = 1u << 13, PermModify = 1u << 14, PermCopy = 1u << 15, PermMove = 1u << 19;

    /// <summary>The next owner's mask of an upload with the official defaults (UploadsNextOwnerCopy and Modify off, Transfer on).</summary>
    public const uint UploadNextOwnerMask = PermMove | PermTransfer;

    private static Vector3 L(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The create tool's message for the default box: wood, square profile on a straight path, half a metre, made selected; the
    /// placement's ray tells the region where it goes.</summary>
    public static ObjectAddPacket ObjectAdd(UUID agent, UUID session, UUID group, Placement p) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        ObjectData =
        {
            PCode = DefaultBox.PCodeVolume,
            Material = DefaultBox.MaterialWood,
            AddFlags = DefaultBox.AddFlagCreateSelected,
            PathCurve = DefaultBox.PathLine,
            ProfileCurve = DefaultBox.ProfileSquare,
            // begin and end cuts at 0 and 1 pack to 0; a ratio of 1 packs to 100; no shear, twist, taper or hollow
            PathBegin = 0, PathEnd = 0, PathScaleX = DefaultBox.PathScalePacked, PathScaleY = DefaultBox.PathScalePacked,
            PathShearX = 0, PathShearY = 0, PathTwist = 0, PathTwistBegin = 0, PathRadiusOffset = 0, PathTaperX = 0, PathTaperY = 0,
            PathRevolutions = 0, PathSkew = 0, ProfileBegin = 0, ProfileEnd = 0, ProfileHollow = 0,
            Scale = L(DefaultBox.Scale),
            Rotation = Quaternion.Identity,
            RayStart = L(p.RayStart),
            RayEnd = L(p.RayEnd),
            RayTargetID = new UUID(p.RayTargetId),
            BypassRaycast = (byte)(p.BypassRaycast ? 1 : 0),
            RayEndIsIntersection = 0,
            State = 0,
        },
    };

    /// <summary>Rezzing an inventory object where the placement's ray says (not selected); the item's own record follows, with its checksum.</summary>
    public static RezObjectPacket RezObject(UUID agent, UUID session, UUID group, Placement p, InventoryItem item, bool removeItem, UUID folderSent)
    {
        int created = (int)Utils.DateTimeToUnixTime(item.CreationDate);
        return new RezObjectPacket
        {
            AgentData = { AgentID = agent, SessionID = session, GroupID = group },
            RezData =
            {
                FromTaskID = UUID.Zero,
                BypassRaycast = (byte)(p.BypassRaycast ? 1 : 0),
                RayStart = L(p.RayStart),
                RayEnd = L(p.RayEnd),
                RayTargetID = new UUID(p.RayTargetId),
                RayEndIsIntersection = false,
                RezSelected = false,
                RemoveItem = removeItem,
                ItemFlags = item.Flags,
                GroupMask = (uint)item.Permissions.GroupMask,
                EveryoneMask = (uint)item.Permissions.EveryoneMask,
                NextOwnerMask = (uint)item.Permissions.NextOwnerMask,
            },
            InventoryData =
            {
                ItemID = item.UUID,
                FolderID = folderSent,
                CreatorID = item.CreatorID,
                OwnerID = item.OwnerID,
                GroupID = item.GroupID,
                BaseMask = (uint)item.Permissions.BaseMask,
                OwnerMask = (uint)item.Permissions.OwnerMask,
                GroupMask = (uint)item.Permissions.GroupMask,
                EveryoneMask = (uint)item.Permissions.EveryoneMask,
                NextOwnerMask = (uint)item.Permissions.NextOwnerMask,
                GroupOwned = item.GroupOwned,
                TransactionID = UUID.Zero,
                Type = (sbyte)item.AssetType,
                InvType = (sbyte)item.InventoryType,
                Flags = item.Flags,
                SaleType = (byte)item.SaleType,
                SalePrice = item.SalePrice,
                Name = Utils.StringToBytes(item.Name ?? ""),
                Description = Utils.StringToBytes(item.Description ?? ""),
                CreationDate = created,
                CRC = Helpers.InventoryCRC(created, (byte)item.SaleType, (sbyte)item.InventoryType, (sbyte)item.AssetType, item.AssetUUID, item.GroupID,
                    item.SalePrice, item.OwnerID, item.CreatorID, item.UUID, item.ParentUUID, (uint)item.Permissions.EveryoneMask, item.Flags,
                    (uint)item.Permissions.NextOwnerMask, (uint)item.Permissions.GroupMask, (uint)item.Permissions.OwnerMask),
            },
        };
    }

    /// <summary>Taking or deleting one object: the destination (take into inventory, or the trash) and the folder it goes to.</summary>
    public static DeRezObjectPacket DeRez(UUID agent, UUID session, uint localId, byte destination, UUID folder, UUID transaction) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        AgentBlock = new DeRezObjectPacket.AgentBlockBlock
        {
            GroupID = UUID.Zero, Destination = destination, DestinationID = folder, TransactionID = transaction, PacketCount = 1, PacketNumber = 0,
        },
        ObjectData = [new DeRezObjectPacket.ObjectDataBlock { ObjectLocalID = localId }],
    };

    /// <summary>The body of the upload's first step for a texture, with the official default permissions of an upload.</summary>
    public static OSDMap UploadRequestBody(UploadRequest r) => new()
    {
        ["folder_id"] = OSD.FromUUID(new UUID(r.FolderId)),
        ["asset_type"] = "texture",
        ["inventory_type"] = "texture",
        ["name"] = r.Name,
        ["description"] = r.Description,
        ["next_owner_mask"] = OSD.FromInteger((int)UploadNextOwnerMask),
        ["group_mask"] = OSD.FromInteger(0),
        ["everyone_mask"] = OSD.FromInteger(0),
        ["expected_upload_cost"] = OSD.FromInteger(r.ExpectedCost),
    };

    private static string ErrorOf(OSDMap m) =>
        m.TryGetValue("error", out var e) && e is OSDMap em && em.TryGetValue("message", out var msg) && msg.AsString() is { Length: > 0 } text ? text
        : m.TryGetValue("state", out var s) && s.AsString() is { Length: > 0 } state ? state : "refused";

    /// <summary>The first step's answer: "upload" with the uploader's address and the price, or an error.</summary>
    public static UploadTicket ParseTicket(OSD? reply)
    {
        if (reply is not OSDMap m)
            return new UploadTicket(null, 0, "no_answer");
        if (m.TryGetValue("state", out var s) && s.AsString() == "upload" && m.TryGetValue("uploader", out var u)
            && Uri.TryCreate(u.AsString(), UriKind.Absolute, out var uploader) && uploader.Scheme is "http" or "https")
            return new UploadTicket(uploader, m.TryGetValue("upload_price", out var price) ? price.AsInteger() : 0, null);
        return new UploadTicket(null, 0, ErrorOf(m));
    }

    /// <summary>The second step's answer: "complete" with the new asset and inventory item, or an error.</summary>
    public static UploadDone ParseDone(OSD? reply)
    {
        if (reply is not OSDMap m)
            return new UploadDone(Guid.Empty, Guid.Empty, "no_answer");
        if (m.TryGetValue("state", out var s) && s.AsString() == "complete" && m.TryGetValue("new_inventory_item", out var item))
            return new UploadDone(m.TryGetValue("new_asset", out var a) ? a.AsUUID().Guid : Guid.Empty, item.AsUUID().Guid, null);
        return new UploadDone(Guid.Empty, Guid.Empty, ErrorOf(m));
    }
}

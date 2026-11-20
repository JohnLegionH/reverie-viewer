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

    /// <summary>The create tool's message for a shape (the default is the box: square profile on a straight path), of the size and material given
    /// (the default half-metre wooden box unless the new-prim settings say otherwise); the placement's ray tells the region where it goes. A
    /// volume arrives selected and is made with its shape and turn (<see cref="CreateShapes"/>); a tree or grass is sent with its pcode and
    /// <paramref name="species"/> in the state field and is not selected (lltoolplacer.cpp addObject, read as behaviour).</summary>
    public static ObjectAddPacket ObjectAdd(UUID agent, UUID session, UUID group, Placement p, System.Numerics.Vector3 scale, byte material,
        CreateShape shape = CreateShape.Cube, byte species = 0)
    {
        var spec = CreateShapes.Of(shape);
        // a plant has no volume: the path and profile are the plain defaults (a straight path with a square profile, a ratio of 1)
        var b = ShapeRules.Encode(spec.Volume ?? Viewer.Core.Meshing.PrimShape.Box);
        var turn = spec.Rotation;
        return new ObjectAddPacket
        {
            AgentData = { AgentID = agent, SessionID = session, GroupID = group },
            ObjectData =
            {
                PCode = spec.PCode,
                Material = material,
                AddFlags = spec.CreateSelected ? DefaultBox.AddFlagCreateSelected : 0,
                PathCurve = b.PathCurve,
                ProfileCurve = b.ProfileCurve,
                // each parameter as the official packing quantises it (llvolumemessage.cpp packPathParams, packProfileParams)
                PathBegin = b.PathBegin, PathEnd = b.PathEnd, PathScaleX = b.PathScaleX, PathScaleY = b.PathScaleY,
                PathShearX = b.PathShearX, PathShearY = b.PathShearY, PathTwist = b.PathTwist, PathTwistBegin = b.PathTwistBegin,
                PathRadiusOffset = b.PathRadiusOffset, PathTaperX = b.PathTaperX, PathTaperY = b.PathTaperY,
                PathRevolutions = b.PathRevolutions, PathSkew = b.PathSkew, ProfileBegin = b.ProfileBegin, ProfileEnd = b.ProfileEnd,
                ProfileHollow = b.ProfileHollow,
                Scale = L(scale),
                Rotation = new Quaternion(turn.X, turn.Y, turn.Z, turn.W),
                RayStart = L(p.RayStart),
                RayEnd = L(p.RayEnd),
                RayTargetID = new UUID(p.RayTargetId),
                BypassRaycast = (byte)(p.BypassRaycast ? 1 : 0),
                RayEndIsIntersection = 0,
                State = species,
            },
        };
    }

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

    /// <summary>The most roots one DeRezObject carries (llviewermenu.cpp derez_objects: MAX_ROOTS_PER_PACKET).</summary>
    public const int MaxRootsPerDeRez = 250;

    /// <summary>Return to owner: the objects sent back to their owners' inventories (destination 9, no folder), in packets of at most
    /// <see cref="MaxRootsPerDeRez"/> roots that share one transaction and count each other; the sender's active group goes in each, as the
    /// official viewer's derez_objects sends it. The region decides which objects the agent may return.</summary>
    public static IReadOnlyList<DeRezObjectPacket> ReturnToOwner(UUID agent, UUID session, UUID group, IReadOnlyList<uint> locals, UUID transaction)
    {
        int count = (locals.Count + MaxRootsPerDeRez - 1) / MaxRootsPerDeRez;
        var packets = new List<DeRezObjectPacket>(count);
        for (int n = 0; n < count; n++)
            packets.Add(new DeRezObjectPacket
            {
                AgentData = { AgentID = agent, SessionID = session },
                AgentBlock = new DeRezObjectPacket.AgentBlockBlock
                {
                    GroupID = group, Destination = BuildRules.DeRezReturnToOwner, DestinationID = UUID.Zero, TransactionID = transaction,
                    PacketCount = (byte)count, PacketNumber = (byte)n,
                },
                ObjectData = [.. locals.Skip(n * MaxRootsPerDeRez).Take(MaxRootsPerDeRez).Select(l => new DeRezObjectPacket.ObjectDataBlock { ObjectLocalID = l })],
            });
        return packets;
    }

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

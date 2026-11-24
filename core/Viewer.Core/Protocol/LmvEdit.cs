// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Building;
using Viewer.Core.World;

namespace Viewer.Core.Protocol;

/// <summary>
/// The edit tools' messages, laid out as the official viewer sends them (message_template.msg; llselectmgr.cpp, llpanelobject.cpp and
/// llpanelpermissions.cpp read as behaviour): names and descriptions, permissions (Locked is the owner's move and modify), position,
/// rotation and size, the object's flags, its shape, Link, Unlink and Duplicate.
/// </summary>
internal static class LmvEdit
{
    /// <summary>MultipleObjectUpdate's type bits.</summary>
    public const byte UpdPosition = 0x01, UpdRotation = 0x02, UpdScale = 0x04, UpdLinkedSets = 0x08, UpdUniform = 0x10;

    /// <summary>ObjectPermissions' fields.</summary>
    public const byte FieldOwner = 0x02, FieldGroup = 0x04, FieldEveryone = 0x08, FieldNextOwner = 0x10;

    /// <summary>ObjectDuplicate's flag that makes the copy the selected one; the Move tool's Shift-drag leaves it off, so the copy stays behind.</summary>
    public const uint DuplicateCreateSelected = 1u << 1;

    public static ObjectDeselectPacket Deselect(UUID agent, UUID session, IReadOnlyList<uint> locals) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [.. locals.Select(l => new ObjectDeselectPacket.ObjectDataBlock { ObjectLocalID = l })],
    };

    public static ObjectNamePacket ObjectName(UUID agent, UUID session, uint local, string name) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new ObjectNamePacket.ObjectDataBlock { LocalID = local, Name = Utils.StringToBytes(name) }],
    };

    public static ObjectDescriptionPacket ObjectDescription(UUID agent, UUID session, uint local, string description) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new ObjectDescriptionPacket.ObjectDataBlock { LocalID = local, Description = Utils.StringToBytes(description) }],
    };

    public static ObjectPermissionsPacket ObjectPermissions(UUID agent, UUID session, IReadOnlyList<uint> locals, byte field, bool set, uint mask) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        HeaderData = { Override = false },
        ObjectData = locals.Select(l => new ObjectPermissionsPacket.ObjectDataBlock { ObjectLocalID = l, Field = field, Set = (byte)(set ? 1 : 0), Mask = mask }).ToArray(),
    };

    /// <summary>Locked: the owner's move and modify taken away (locking) or given back.</summary>
    public static ObjectPermissionsPacket Lock(UUID agent, UUID session, IReadOnlyList<uint> locals, bool locked) =>
        ObjectPermissions(agent, session, locals, FieldOwner, !locked, LmvBuild.PermMove | LmvBuild.PermModify);

    /// <summary>One object's new place, rotation and size; <paramref name="linkset"/> moves the whole linkset by its root.</summary>
    public static MultipleObjectUpdatePacket Update(UUID agent, UUID session, uint local, System.Numerics.Vector3? position, System.Numerics.Quaternion? rotation,
        System.Numerics.Vector3? scale, bool linkset, bool uniform = false)
    {
        byte type = (byte)((position is null ? 0 : UpdPosition) | (rotation is null ? 0 : UpdRotation) | (scale is null ? 0 : UpdScale)
            | (linkset ? UpdLinkedSets : 0) | (uniform && scale is not null ? UpdUniform : 0));
        var data = new List<byte>(36);
        void Put(float a, float b, float c) { data.AddRange(BitConverter.GetBytes(a)); data.AddRange(BitConverter.GetBytes(b)); data.AddRange(BitConverter.GetBytes(c)); }
        if (position is { } p)
            Put(p.X, p.Y, p.Z);
        if (rotation is { } r)
        {
            // the rotation packed to its vector part, normalised with w >= 0 (LLQuaternion::packToVector3)
            var q = System.Numerics.Quaternion.Normalize(r);
            if (q.W < 0)
                q = new System.Numerics.Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            Put(q.X, q.Y, q.Z);
        }
        if (scale is { } sc)
            Put(sc.X, sc.Y, sc.Z);
        return new MultipleObjectUpdatePacket
        {
            AgentData = { AgentID = agent, SessionID = session },
            ObjectData = [new MultipleObjectUpdatePacket.ObjectDataBlock { ObjectLocalID = local, Type = type, Data = data.ToArray() }],
        };
    }

    /// <summary>Several linksets' new places as MultipleObjectUpdate messages, one block per root with a group position, at most <paramref name="perMessage"/> blocks in each.</summary>
    public static IReadOnlyList<MultipleObjectUpdatePacket> MoveLinksets(UUID agent, UUID session, IReadOnlyList<LinksetMove> moves, int perMessage = 40)
    {
        var packets = new List<MultipleObjectUpdatePacket>();
        for (int at = 0; at < moves.Count; at += perMessage)
            packets.Add(new MultipleObjectUpdatePacket
            {
                AgentData = { AgentID = agent, SessionID = session },
                ObjectData = [.. moves.Skip(at).Take(perMessage).Select(m =>
                {
                    var data = new List<byte>(12);
                    data.AddRange(BitConverter.GetBytes(m.Position.X));
                    data.AddRange(BitConverter.GetBytes(m.Position.Y));
                    data.AddRange(BitConverter.GetBytes(m.Position.Z));
                    return new MultipleObjectUpdatePacket.ObjectDataBlock { ObjectLocalID = m.LocalId, Type = UpdPosition | UpdLinkedSets, Data = data.ToArray() };
                })],
            });
        return packets;
    }

    public static ObjectFlagUpdatePacket Flags(UUID agent, UUID session, uint local, bool physical, bool temporary, bool phantom) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, ObjectLocalID = local, UsePhysics = physical, IsTemporary = temporary, IsPhantom = phantom, CastsShadows = false },
        ExtraPhysics = [],
    };

    /// <summary>ObjectImage: the prim's whole texture entry (the copy the library holds, so media and material references stay) with the
    /// edited faces set from <paramref name="faces"/>, as the official viewer sends a face edit (LLViewerObject::sendTEUpdate).</summary>
    public static ObjectImagePacket Image(UUID agent, UUID session, uint local, Primitive.TextureEntry? current, IReadOnlyList<FaceEdit> faces)
    {
        Primitive.TextureEntry te;
        if (current is not null)
        {
            var bytes = current.GetBytes();
            te = new Primitive.TextureEntry(bytes, 0, bytes.Length);
        }
        else
            te = new Primitive.TextureEntry(new UUID(FaceRules.DefaultTexture));
        foreach (var e in faces)
        {
            if (e.Face < 0 || e.Face >= Primitive.TextureEntry.MAX_FACES)
                continue;
            var f = te.CreateFace((uint)e.Face);
            var s = e.Settings;
            f.TextureID = new UUID(s.TextureId);
            f.RGBA = new Color4(s.Colour.R, s.Colour.G, s.Colour.B, s.Colour.A);
            f.RepeatU = s.RepeatU;
            f.RepeatV = s.RepeatV;
            f.OffsetU = s.OffsetU;
            f.OffsetV = s.OffsetV;
            f.Rotation = s.Rotation;
            f.TexMapType = s.TexGen == TexGen.Planar ? MappingType.Planar : MappingType.Default;
            f.Fullbright = s.Fullbright;
            f.Glow = s.Glow;
        }
        return new ObjectImagePacket
        {
            AgentData = { AgentID = agent, SessionID = session },
            ObjectData = [new ObjectImagePacket.ObjectDataBlock { ObjectLocalID = local, MediaURL = [], TextureEntry = te.GetBytes() }],
        };
    }

    /// <summary>The ModifyMaterialParams body: one entry per face, the material asset (empty takes it off) and an override when given.</summary>
    public static OSDArray MaterialParams(UUID objectId, IReadOnlyList<int> faces, UUID material, string? overrideJson)
    {
        var a = new OSDArray();
        foreach (int side in faces)
        {
            var m = new OSDMap { ["object_id"] = OSD.FromUUID(objectId), ["side"] = OSD.FromInteger(side), ["asset_id"] = OSD.FromUUID(material) };
            if (overrideJson is not null)
                m["gltf_json"] = OSD.FromString(overrideJson);
            a.Add(m);
        }
        return a;
    }

    public static ObjectShapePacket Shape(UUID agent, UUID session, uint local, ShapeBytes b) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData =
        [
            new ObjectShapePacket.ObjectDataBlock
            {
                ObjectLocalID = local, PathCurve = b.PathCurve, ProfileCurve = b.ProfileCurve, PathBegin = b.PathBegin, PathEnd = b.PathEnd,
                PathScaleX = b.PathScaleX, PathScaleY = b.PathScaleY, PathShearX = b.PathShearX, PathShearY = b.PathShearY,
                PathTwist = b.PathTwist, PathTwistBegin = b.PathTwistBegin, PathRadiusOffset = b.PathRadiusOffset,
                PathTaperX = b.PathTaperX, PathTaperY = b.PathTaperY, PathRevolutions = b.PathRevolutions, PathSkew = b.PathSkew,
                ProfileBegin = b.ProfileBegin, ProfileEnd = b.ProfileEnd, ProfileHollow = b.ProfileHollow,
            },
        ],
    };

    /// <summary>ObjectLink with the roots in the order given (the first becomes the root).</summary>
    public static ObjectLinkPacket Link(UUID agent, UUID session, IReadOnlyList<uint> ordered) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = ordered.Select(l => new ObjectLinkPacket.ObjectDataBlock { ObjectLocalID = l }).ToArray(),
    };

    public static ObjectDelinkPacket Delink(UUID agent, UUID session, IReadOnlyList<uint> locals) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = locals.Select(l => new ObjectDelinkPacket.ObjectDataBlock { ObjectLocalID = l }).ToArray(),
    };

    /// <summary>Undo: the region steps each object back (stock OpenSim keeps a few states per prim for each update it applied).</summary>
    public static UndoPacket Undo(UUID agent, UUID session, UUID group, IReadOnlyList<Guid> objects) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        ObjectData = [.. objects.Select(o => new UndoPacket.ObjectDataBlock { ObjectID = new UUID(o) })],
    };

    /// <summary>Redo: the region repeats a change it undid on each object.</summary>
    public static RedoPacket Redo(UUID agent, UUID session, UUID group, IReadOnlyList<Guid> objects) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        ObjectData = [.. objects.Select(o => new RedoPacket.ObjectDataBlock { ObjectID = new UUID(o) })],
    };

    /// <summary>The create tool's "Copy selection": the selected roots copied to where the ray says (ObjectDuplicateOnRay; llselectmgr.cpp
    /// selectDuplicateOnRay). The create tool sends the copy unselected.</summary>
    public static ObjectDuplicateOnRayPacket DuplicateOnRay(UUID agent, UUID session, UUID group, IReadOnlyList<uint> roots, Placement p, bool copyCenters,
        bool copyRotates, uint flags) => new()
    {
        AgentData =
        {
            AgentID = agent, SessionID = session, GroupID = group,
            RayStart = new LibreMetaverse.Vector3(p.RayStart.X, p.RayStart.Y, p.RayStart.Z),
            RayEnd = new LibreMetaverse.Vector3(p.RayEnd.X, p.RayEnd.Y, p.RayEnd.Z),
            BypassRaycast = p.BypassRaycast, RayEndIsIntersection = false, CopyCenters = copyCenters, CopyRotates = copyRotates,
            RayTargetID = new UUID(p.RayTargetId), DuplicateFlags = flags,
        },
        ObjectData = roots.Select(l => new ObjectDuplicateOnRayPacket.ObjectDataBlock { ObjectLocalID = l }).ToArray(),
    };

    /// <summary>"When left-clicked": one ObjectClickAction block for each object, parts as well as roots (llselectmgr.cpp
    /// selectionSetClickAction sends to every selected object).</summary>
    public static ObjectClickActionPacket ClickAction(UUID agent, UUID session, IReadOnlyList<uint> locals, byte action) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = locals.Select(l => new ObjectClickActionPacket.ObjectDataBlock { ObjectLocalID = l, ClickAction = action }).ToArray(),
    };

    /// <summary>For sale: one ObjectSaleInfo block for each root, with the sale type and the price (llselectmgr.cpp selectionSetObjectSaleInfo).</summary>
    public static ObjectSaleInfoPacket SaleInfo(UUID agent, UUID session, IReadOnlyList<uint> roots, byte saleType, int price) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = roots.Select(l => new ObjectSaleInfoPacket.ObjectDataBlock { LocalID = l, SaleType = saleType, SalePrice = price }).ToArray(),
    };

    public static ObjectDuplicatePacket Duplicate(UUID agent, UUID session, UUID group, IReadOnlyList<uint> roots, System.Numerics.Vector3 offset, uint flags) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        SharedData = { Offset = new LibreMetaverse.Vector3(offset.X, offset.Y, offset.Z), DuplicateFlags = flags },
        ObjectData = roots.Select(l => new ObjectDuplicatePacket.ObjectDataBlock { ObjectLocalID = l }).ToArray(),
    };
}

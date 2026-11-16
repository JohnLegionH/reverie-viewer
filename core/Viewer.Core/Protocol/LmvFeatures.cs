// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Building;

namespace Viewer.Core.Protocol;

/// <summary>
/// The Features and Content tabs' messages, laid out as the official viewer sends them (message_template.msg; llpanelvolume.cpp,
/// llvovolume.cpp, llviewerobject.cpp, llpanelcontents.cpp and llpanelobjectinventory.cpp read as behaviour): a prim's flexible, light
/// and projector blocks (ObjectExtraParams), its physics settings (ObjectFlagUpdate's extra physics block), its material
/// (ObjectMaterial), a new script (RezScript with no item), an item put into it or taken out of it.
/// </summary>
internal static class LmvFeatures
{
    /// <summary>ObjectExtraParams' block types: flexible, light, the light's image.</summary>
    public const ushort ParamFlexible = 0x10, ParamLight = 0x20, ParamLightImage = 0x40;

    /// <summary>A script's asset and inventory types (LSL text).</summary>
    public const sbyte TypeLsl = 10, InvTypeLsl = 10;

    /// <summary>The flexible block on (its settings packed as the protocol has them) or off.</summary>
    public static ObjectExtraParamsPacket Flexible(UUID agent, UUID session, uint local, FlexibleParams? f) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [Block(local, ParamFlexible, f is { } v
            ? new Primitive.FlexibleData { Softness = v.Softness, Gravity = v.Gravity, Drag = v.Drag, Wind = v.Wind, Tension = v.Tension,
                Force = new LibreMetaverse.Vector3(v.Force.X, v.Force.Y, v.Force.Z) }.GetBytes()
            : null)],
    };

    /// <summary>One block: in use with its data, or not in use (the official viewer sends a block it turns off with no data).</summary>
    private static ObjectExtraParamsPacket.ObjectDataBlock Block(uint local, ushort type, byte[]? data) => new()
    {
        ObjectLocalID = local, ParamType = type, ParamInUse = data is not null, ParamData = data ?? [], ParamSize = (uint)(data?.Length ?? 0),
    };

    /// <summary>The light block, and the light image block when a projector is given (or off when it is not), as two blocks of one message.</summary>
    public static ObjectExtraParamsPacket Light(UUID agent, UUID session, uint local, LightParams? light, ProjectorParams? projector) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData =
        [
            // the intensity rides as the colour's alpha (LLLightParams packs colour and intensity as one colour)
            Block(local, ParamLight, light is { } l
                ? new Primitive.LightData { Color = new Color4(l.Color.X, l.Color.Y, l.Color.Z, l.Intensity), Intensity = l.Intensity, Radius = l.Radius,
                    Falloff = l.Falloff, Cutoff = 0f }.GetBytes()
                : null),
            Block(local, ParamLightImage, light is not null && projector is { } p
                ? new Primitive.LightImage { LightTexture = new UUID(p.Texture), Params = new LibreMetaverse.Vector3(p.Fov, p.Focus, p.Ambiance) }.GetBytes()
                : null),
        ],
    };

    /// <summary>The prim's flags with its physics settings in the extra physics block.</summary>
    public static ObjectFlagUpdatePacket Physics(UUID agent, UUID session, uint local, PhysicsParams p, bool physical, bool temporary, bool phantom) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, ObjectLocalID = local, UsePhysics = physical, IsTemporary = temporary, IsPhantom = phantom, CastsShadows = false },
        ExtraPhysics = [new ObjectFlagUpdatePacket.ExtraPhysicsBlock
        {
            PhysicsShapeType = p.ShapeType, Density = p.Density, Friction = p.Friction, Restitution = p.Restitution, GravityMultiplier = p.Gravity,
        }],
    };

    /// <summary>The prim's material.</summary>
    public static ObjectMaterialPacket Material(UUID agent, UUID session, uint local, byte material) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new ObjectMaterialPacket.ObjectDataBlock { ObjectLocalID = local, Material = material }],
    };

    /// <summary>New Script: RezScript with no item, the prim's id as the folder and the default script's name and types; the region writes
    /// its default script (llpanelcontents.cpp onClickNewScript).</summary>
    public static RezScriptPacket NewScript(UUID agent, UUID session, UUID group, uint local, UUID objectId, string name) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        UpdateBlock = new RezScriptPacket.UpdateBlockBlock { ObjectLocalID = local, Enabled = true },
        InventoryBlock = new RezScriptPacket.InventoryBlockBlock
        {
            ItemID = UUID.Zero, FolderID = objectId, CreatorID = agent, OwnerID = agent, GroupID = UUID.Zero,
            BaseMask = PermAll, OwnerMask = PermAll, GroupMask = 0, EveryoneMask = 0, NextOwnerMask = LmvBuild.PermMove | LmvBuild.PermTransfer,
            GroupOwned = false, TransactionID = UUID.Zero, Type = TypeLsl, InvType = InvTypeLsl, Flags = 0, SaleType = 0, SalePrice = 0,
            Name = Utils.StringToBytes(name), Description = Utils.StringToBytes(""), CreationDate = (int)Utils.DateTimeToUnixTime(DateTime.UtcNow), CRC = 0,
        },
    };

    /// <summary>Every permission bit, as the official viewer gives the owner of a new script.</summary>
    private const uint PermAll = 0x7FFFFFFF;

    /// <summary>An item taken out of a prim's contents.</summary>
    public static RemoveTaskInventoryPacket Remove(UUID agent, UUID session, uint local, UUID item) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        InventoryData = new RemoveTaskInventoryPacket.InventoryDataBlock { LocalID = local, ItemID = item },
    };

    /// <summary>The message an edit of the Features and Content tabs, or a take into a folder, becomes; null for one that is not theirs or that
    /// needs the agent's inventory (an item put into a prim, an item copied out).</summary>
    public static Packet? Packet(UUID agent, UUID session, UUID group, Viewer.Core.Actions.ActionIntent edit) => edit switch
    {
        Viewer.Core.Actions.SetFlexibleIntent f => Flexible(agent, session, f.LocalId, f.Flexible),
        Viewer.Core.Actions.SetLightIntent l => Light(agent, session, l.LocalId, l.Light, l.Projector),
        Viewer.Core.Actions.SetPhysicsIntent p => Physics(agent, session, p.LocalId, p.Physics, p.Physical, p.Temporary, p.Phantom),
        Viewer.Core.Actions.SetObjectMaterialIntent m => Material(agent, session, m.LocalId, m.Material),
        Viewer.Core.Actions.NewScriptIntent n => NewScript(agent, session, group, n.LocalId, new UUID(n.ObjectId), n.Name),
        Viewer.Core.Actions.RemoveObjectItemIntent r => Remove(agent, session, r.LocalId, new UUID(r.ItemId)),
        Viewer.Core.Actions.TakeIntoFolderIntent t => LmvBuild.DeRez(agent, session, t.LocalId, BuildRules.DeRezTakeIntoInventory, new UUID(t.FolderId), UUID.Random()),
        _ => null,
    };

    /// <summary>GetObjectPhysicsData's request body: the objects' ids.</summary>
    public static OSDMap PhysicsRequest(IReadOnlyList<Guid> ids) => new() { ["object_ids"] = new OSDArray([.. ids.Select(i => OSD.FromUUID(new UUID(i)))]) };

    /// <summary>GetObjectPhysicsData's answer: a map of each object's id to its shape type, density, friction, restitution and gravity multiplier.</summary>
    public static IReadOnlyList<(Guid Id, PhysicsParams Physics)> ParsePhysics(OSD? reply)
    {
        if (reply is not OSDMap map)
            return [];
        var read = new List<(Guid, PhysicsParams)>();
        foreach (var key in map.Keys)
        {
            if (!Guid.TryParse(key, out var id) || map[key] is not OSDMap v)
                continue;
            float F(string name, float fallback) => v.TryGetValue(name, out var x) ? (float)x.AsReal() : fallback;
            var d = PhysicsParams.Default;
            byte shape = v.TryGetValue("PhysicsShapeType", out var s) ? (byte)Math.Clamp(s.AsInteger(), 0, 255) : d.ShapeType;
            read.Add((id, FeatureRules.Clamp(new PhysicsParams(shape, F("GravityMultiplier", d.Gravity), F("Friction", d.Friction), F("Density", d.Density),
                F("Restitution", d.Restitution)))));
        }
        return read;
    }
}

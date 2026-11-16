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
        ObjectData = [new ObjectExtraParamsPacket.ObjectDataBlock()],
    };

    /// <summary>The light block, and the light image block when a projector is given (or off when it is not), as two blocks of one message.</summary>
    public static ObjectExtraParamsPacket Light(UUID agent, UUID session, uint local, LightParams? light, ProjectorParams? projector) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new ObjectExtraParamsPacket.ObjectDataBlock()],
    };

    /// <summary>The prim's flags with its physics settings in the extra physics block.</summary>
    public static ObjectFlagUpdatePacket Physics(UUID agent, UUID session, uint local, PhysicsParams p, bool physical, bool temporary, bool phantom) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, ObjectLocalID = local },
        ExtraPhysics = [new ObjectFlagUpdatePacket.ExtraPhysicsBlock()],
    };

    /// <summary>The prim's material.</summary>
    public static ObjectMaterialPacket Material(UUID agent, UUID session, uint local, byte material) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new ObjectMaterialPacket.ObjectDataBlock()],
    };

    /// <summary>New Script: RezScript with no item, the prim's id as the folder and the default script's name and types; the region writes
    /// its default script (llpanelcontents.cpp onClickNewScript).</summary>
    public static RezScriptPacket NewScript(UUID agent, UUID session, UUID group, uint local, UUID objectId, string name) => new()
    {
        AgentData = { AgentID = agent, SessionID = session, GroupID = group },
        UpdateBlock = new RezScriptPacket.UpdateBlockBlock(),
        InventoryBlock = new RezScriptPacket.InventoryBlockBlock(),
    };

    /// <summary>An item taken out of a prim's contents.</summary>
    public static RemoveTaskInventoryPacket Remove(UUID agent, UUID session, uint local, UUID item) => new()
    {
        AgentData = { AgentID = agent, SessionID = session },
        InventoryData = new RemoveTaskInventoryPacket.InventoryDataBlock(),
    };

    /// <summary>The message an edit of the Features and Content tabs, or a take into a folder, becomes; null for one that is not theirs or that
    /// needs the agent's inventory (an item put into a prim, an item copied out).</summary>
    public static Packet? Packet(UUID agent, UUID session, UUID group, Viewer.Core.Actions.ActionIntent edit) => null;

    /// <summary>GetObjectPhysicsData's request body: the objects' ids.</summary>
    public static OSDMap PhysicsRequest(IReadOnlyList<Guid> ids) => new();

    /// <summary>GetObjectPhysicsData's answer: a map of each object's id to its shape type, density, friction, restitution and gravity multiplier.</summary>
    public static IReadOnlyList<(Guid Id, PhysicsParams Physics)> ParsePhysics(OSD? reply) => [];
}

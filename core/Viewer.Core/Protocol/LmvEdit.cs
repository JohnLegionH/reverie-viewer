// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Building;

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

    public static ObjectNamePacket ObjectName(UUID agent, UUID session, uint local, string name) => new();

    public static ObjectDescriptionPacket ObjectDescription(UUID agent, UUID session, uint local, string description) => new();

    public static ObjectPermissionsPacket ObjectPermissions(UUID agent, UUID session, IReadOnlyList<uint> locals, byte field, bool set, uint mask) => new();

    /// <summary>Locked: the owner's move and modify taken away (locking) or given back.</summary>
    public static ObjectPermissionsPacket Lock(UUID agent, UUID session, IReadOnlyList<uint> locals, bool locked) => new();

    /// <summary>One object's new place, rotation and size; <paramref name="linkset"/> moves the whole linkset by its root.</summary>
    public static MultipleObjectUpdatePacket Update(UUID agent, UUID session, uint local, System.Numerics.Vector3? position, System.Numerics.Quaternion? rotation,
        System.Numerics.Vector3? scale, bool linkset, bool uniform = false) => new();

    public static ObjectFlagUpdatePacket Flags(UUID agent, UUID session, uint local, bool physical, bool temporary, bool phantom) => new();

    public static ObjectShapePacket Shape(UUID agent, UUID session, uint local, ShapeBytes shape) => new();

    /// <summary>ObjectLink with the roots in the order given (the first becomes the root).</summary>
    public static ObjectLinkPacket Link(UUID agent, UUID session, IReadOnlyList<uint> ordered) => new();

    public static ObjectDelinkPacket Delink(UUID agent, UUID session, IReadOnlyList<uint> locals) => new();

    public static ObjectDuplicatePacket Duplicate(UUID agent, UUID session, UUID group, IReadOnlyList<uint> roots, System.Numerics.Vector3 offset, uint flags) => new();
}

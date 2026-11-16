// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// An object's identity as the core hands it out (ADR-005: epoch-scoped stable ids). The simulator's local id is only
/// unique within one region visit; the epoch is bumped on every region change, so an id held by the presentation from
/// a previous region can never address an object of the current one.
/// </summary>
public readonly record struct ObjectId(int Epoch, uint LocalId)
{
    public override string ToString() => $"{Epoch}:{LocalId}";
}

/// <summary>A prim as the protocol adapter reports it (no LibreMetaverse type). Positions of linked children are local to their parent.</summary>
public sealed record PrimData(
    uint LocalId,
    Guid FullId,
    uint ParentLocalId,
    int AttachmentPoint,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector3 Velocity = default,
    Vector3 AngularVelocity = default,
    Vector3 Acceleration = default,
    TextureAnim? TextureAnim = null,
    Viewer.Core.Meshing.PrimShape? Shape = null,
    FaceColors? Colors = null,
    FaceTextures? Textures = null,
    FaceMaterials? Materials = null,
    PrimLight? Light = null,
    PrimProjector? Projector = null,
    PrimInteraction? Interaction = null,
    Guid AttachItemId = default,
    int Material = 0,
    Viewer.Core.Building.FlexibleParams? Flexible = null);

/// <summary>An avatar object as the protocol adapter reports it. <paramref name="ParentLocalId"/> is the object it sits on
/// (0 when standing); while seated its position and rotation are relative to that object.</summary>
public sealed record AvatarData(
    uint LocalId,
    Guid AgentId,
    string Name,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector3 Velocity = default,
    uint ParentLocalId = 0,
    string Title = "",
    Vector4 FootPlane = default);

/// <summary>A prim in the world state. For a root attachment, <see cref="AttachmentPoint"/> and <see cref="WearerAgentId"/> are set once the wearer is known.
/// <see cref="Shape"/> and <see cref="Colors"/> are null when the source did not carry them (older captures). <see cref="Materials"/>
/// is null for a prim with no material reference on any face; <see cref="MaterialOverrides"/> holds the region's glTF overrides
/// for the object (they arrive outside its updates and are kept across them).</summary>
public sealed record ObjectState(
    ObjectId Id,
    Guid FullId,
    uint ParentLocalId,
    int? AttachmentPoint,
    Guid? WearerAgentId,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector3 Velocity,
    Vector3 AngularVelocity,
    Vector3 Acceleration,
    TextureAnim? TextureAnim = null,
    Viewer.Core.Meshing.PrimShape? Shape = null,
    FaceColors? Colors = null,
    FaceTextures? Textures = null,
    FaceMaterials? Materials = null,
    MaterialOverrides? MaterialOverrides = null,
    PrimLight? Light = null,
    PrimProjector? Projector = null,
    PrimInteraction? Interaction = null,
    Guid? AttachItemId = null,
    int Material = 0,
    Viewer.Core.Building.FlexibleParams? Flexible = null)
{
    /// <summary>The angular velocity read as an llTargetOmega spin (axis + rate); null when not spinning.</summary>
    public TargetOmega? TargetOmega => MotionFields.OmegaOf(AngularVelocity);
}

/// <summary>
/// Where an attached object hangs: the wearer's local id, the attachment point, and the object's own origin frame in that
/// point's frame (<see cref="Position"/> and <see cref="Rotation"/>; a linked child's pose is carried through its root,
/// which is how the simulator states it: relative to the root, whose offset is relative to the point).
/// </summary>
public sealed record AttachedPlacement(ObjectId Root, uint WearerLocalId, Guid WearerAgentId, int Point, Vector3 Position, Quaternion Rotation);

/// <summary>An avatar in the world state.</summary>
public sealed record AvatarState(
    ObjectId Id,
    Guid AgentId,
    string Name,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector3 Velocity,
    Vector3 AngularVelocity,
    uint ParentLocalId = 0,
    string Title = "",
    Vector4 FootPlane = default);

/// <summary>One animation the region says an avatar is playing: the animation asset and the region's sequence number for it.</summary>
public readonly record struct PlayingAnimation(Guid AnimationId, int Sequence);

public enum WorldChangeKind
{
    RegionReset,
    ObjectAdded,
    ObjectChanged,
    ObjectMoved,
    ObjectRemoved,
    AvatarAdded,
    AvatarChanged,
    AvatarMoved,
    AvatarRemoved,
    TerrainPatch,
    /// <summary>An avatar's appearance was received or changed; <see cref="WorldChange.AgentId"/> names it.</summary>
    AppearanceChanged,
    /// <summary>The region served legacy materials not known before (faces naming them can now be drawn with them).</summary>
    LegacyMaterialsReceived,
    /// <summary>The animations an avatar plays changed; <see cref="WorldChange.AgentId"/> names it.</summary>
    AnimationsChanged,
}

/// <summary>One change to the world state, raised after it is applied (outside the state's lock). A <see cref="WorldChangeKind.TerrainPatch"/>
/// names its patch in <paramref name="PatchX"/>/<paramref name="PatchY"/> (the presentation rebuilds only what changed); -1 otherwise.</summary>
/// <param name="Handoff">On an added or changed object or avatar: it came from another region's world of the same session (same full id).
/// On a removed one: it left this world because another region's world took it over. A handed-over object is the same object in a new place,
/// not one that was removed and made again.</param>
/// <param name="FullId">On a removal that carries <paramref name="Handoff"/>: the full id of the object or avatar that moved on, so a presentation can tell
/// which drawn thing the region entered will take over.</param>
public sealed record WorldChange(WorldChangeKind Kind, ObjectId Id, Guid AgentId = default, int PatchX = -1, int PatchY = -1, bool Handoff = false, Guid FullId = default);

/// <summary>Why an input was not applied. Counted per session, so a replay can assert on them.</summary>
public enum WorldDrop
{
    /// <summary>From a simulator other than the one this epoch belongs to (a late event from the previous region).</summary>
    StaleEpoch,
    /// <summary>A full update for an object killed in this epoch (a late or reordered update never resurrects it).</summary>
    Tombstoned,
    /// <summary>A HUD attachment (points 31-38): never part of the 3D world.</summary>
    Hud,
    /// <summary>A terse update for an object the state never received in full.</summary>
    UnknownObject,
    /// <summary>A terrain patch whose height map is shorter than its size says.</summary>
    BadPatch,
    /// <summary>An appearance for our own avatar in server mode with no AppearanceData block (version 0): not the region's bake.</summary>
    RejectedSelfAppearance,
}

/// <summary>What an object tells a viewer about being clicked: its click action, the region's object flags (touch handler, takes money,
/// permissions, empty inventory...) and the floating text a script set on it. <paramref name="HoverColour"/> holds red, green, blue and
/// real alpha as 0-255 values, packed 0xRRGGBBAA.</summary>
public sealed record PrimInteraction(byte ClickAction, uint Flags, string? HoverText = null, uint HoverColour = 0);

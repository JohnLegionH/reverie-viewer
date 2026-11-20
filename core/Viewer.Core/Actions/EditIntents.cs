// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Building;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>The longest object name and description the official viewer lets one type (63 and 127 characters).</summary>
public static class EditText
{
    public const int MaxName = 63, MaxDescription = 127;

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared() > 1e-6f;
    internal static IReadOnlyList<ActionIssue> Object(uint local) => local == 0 ? [new("edit.no_object")] : [];
    internal static IReadOnlyList<ActionIssue> Objects(IReadOnlyList<uint> locals) => locals.Count == 0 || locals.Any(l => l == 0) ? [new("edit.no_object")] : [];
}

/// <summary>Set the Texture tab's settings on some faces of one prim (the official viewer's ObjectImage, with the prim's whole
/// texture entry and the listed faces changed).</summary>
public sealed record SetObjectFacesIntent(uint LocalId, IReadOnlyList<FaceEdit> Faces) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId), .. (Faces.Count == 0 ? [new ActionIssue("edit.no_face")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Put a PBR material on some faces of one prim, or take it off (an empty <see cref="MaterialId"/>), with an optional
/// override (a glTF document holding only what it changes), through the region's ModifyMaterialParams capability.</summary>
public sealed record SetFaceMaterialIntent(Guid ObjectId, uint LocalId, IReadOnlyList<int> Faces, Guid MaterialId, string? OverrideJson) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId), .. (Faces.Count == 0 || ObjectId == Guid.Empty ? [new ActionIssue("edit.no_face")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Rename an object.</summary>
public sealed record SetObjectNameIntent(uint LocalId, string Name) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId), .. (Name.Length > EditText.MaxName ? [new ActionIssue("edit.name_too_long")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Change an object's description.</summary>
public sealed record SetObjectDescriptionIntent(uint LocalId, string Description) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId), .. (Description.Length > EditText.MaxDescription ? [new ActionIssue("edit.description_too_long")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Set or clear permission bits on objects (by their roots): <paramref name="Field"/> is the owner (2), group (4), everyone (8)
/// or next owner (16).</summary>
public sealed record SetObjectPermissionsIntent(IReadOnlyList<uint> LocalIds, byte Field, bool Set, uint Mask) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(LocalIds);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Lock or unlock objects (the owner's move and modify).</summary>
public sealed record SetObjectLockedIntent(IReadOnlyList<uint> LocalIds, bool Locked) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(LocalIds);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Move, turn or resize an object; null parts are left as they are. <paramref name="Linkset"/> moves the whole linkset by its root.</summary>
public sealed record UpdateObjectIntent(uint LocalId, Vector3? Position, Quaternion? Rotation, Vector3? Scale, bool Linkset, bool Uniform = false) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId),
        .. (Position is null && Rotation is null && Scale is null ? [new ActionIssue("edit.nothing")] : Array.Empty<ActionIssue>()),
        .. ((Position is { } p && !EditText.Finite(p)) || (Rotation is { } r && !EditText.Finite(r)) || (Scale is { } sc && (!EditText.Finite(sc) || sc.X <= 0 || sc.Y <= 0 || sc.Z <= 0))
            ? [new ActionIssue("edit.bad_number")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Physical, temporary and phantom.</summary>
public sealed record SetObjectFlagsIntent(uint LocalId, bool Physical, bool Temporary, bool Phantom) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Object(LocalId);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>A new shape for a prim.</summary>
public sealed record SetObjectShapeIntent(uint LocalId, ShapeBytes Shape) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Object(LocalId);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Link the roots, in the order given: the first becomes the new root.</summary>
public sealed record LinkObjectsIntent(IReadOnlyList<uint> OrderedRoots) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => OrderedRoots.Count < 2 ? [new ActionIssue("link.too_few")] : EditText.Objects(OrderedRoots);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Unlink the prims given.</summary>
public sealed record DelinkObjectsIntent(IReadOnlyList<uint> LocalIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(LocalIds);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Tell the region the objects are no longer selected (ObjectDeselect): a selected physical object is held still until then.</summary>
public sealed record DeselectObjectsIntent(IReadOnlyList<uint> LocalIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(LocalIds);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Undo the region's last change to each object given (the Build menu's Undo, Ctrl+Z): the region keeps the changes and steps back.</summary>
public sealed record UndoObjectsIntent(IReadOnlyList<Guid> ObjectIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => ObjectIds.Count == 0 || ObjectIds.Any(i => i == Guid.Empty) ? [new ActionIssue("edit.no_object")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Redo a change the region undid on each object given (the Build menu's Redo, Ctrl+Y).</summary>
public sealed record RedoObjectsIntent(IReadOnlyList<Guid> ObjectIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => ObjectIds.Count == 0 || ObjectIds.Any(i => i == Guid.Empty) ? [new ActionIssue("edit.no_object")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Copy objects in place (the Move tool's Shift-drag): with no flags the copy stays where it is and the original stays selected.</summary>
public sealed record DuplicateObjectsIntent(IReadOnlyList<uint> Roots, Vector3 Offset, uint Flags = 0) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Objects(Roots), .. (EditText.Finite(Offset) ? Array.Empty<ActionIssue>() : [new ActionIssue("edit.bad_number")])];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>The create tool's "Copy selection": copies the roots to where the placement's ray says. The copy is not selected unless
/// <paramref name="Flags"/> asks (the create tool does not).</summary>
public sealed record DuplicateOnRayIntent(IReadOnlyList<uint> Roots, Placement Placement, bool CopyCenters, bool CopyRotates, uint Flags = 0) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Objects(Roots), .. PlacementRules.Check(Placement, "duplicate")];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>"When left-clicked": the action a click on each of these objects runs (<see cref="Viewer.Core.Interaction.ClickAction"/>).</summary>
public sealed record SetClickActionIntent(IReadOnlyList<uint> LocalIds, byte Action) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(LocalIds);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>For sale: the sale type (0 not for sale, 1 original, 2 copy, 3 contents) and the price of each of these roots.</summary>
public sealed record SetObjectSaleIntent(IReadOnlyList<uint> Roots, byte SaleType, int Price) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() =>
        [.. EditText.Objects(Roots), .. (SaleType > 3 || Price < 0 ? [new ActionIssue("edit.bad_number")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Return objects to their owners (the right-click menu's Return). The region decides which of them the agent may return.</summary>
public sealed record ReturnObjectsIntent(IReadOnlyList<uint> Roots) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Objects(Roots);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Ask the region for the land impact of some objects (its GetObjectCost capability).</summary>
public sealed record RequestObjectCostIntent(IReadOnlyList<Guid> ObjectIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => ObjectIds.Count == 0 || ObjectIds.Any(i => i == Guid.Empty) ? [new ActionIssue("edit.no_object")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Flexible Path on (with its settings) or off (<paramref name="Flexible"/> null) for one prim: the flexible block and the shape's
/// path, as the official Features tab sends them (LLVOVolume::setIsFlexible).</summary>
public sealed record SetFlexibleIntent(uint LocalId, FlexibleParams? Flexible) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Object(LocalId);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>A prim's light on (with its settings and, when given, its projector) or off (<paramref name="Light"/> null).</summary>
public sealed record SetLightIntent(uint LocalId, LightParams? Light, ProjectorParams? Projector) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Object(LocalId);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>A prim's physics shape type, gravity, friction, density and restitution, sent with its flags as the official viewer does
/// (ObjectFlagUpdate with its extra physics block).</summary>
public sealed record SetPhysicsIntent(uint LocalId, PhysicsParams Physics, bool Physical, bool Temporary, bool Phantom) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => EditText.Object(LocalId);
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>A prim's material (Stone 0 to Rubber 6).</summary>
public sealed record SetObjectMaterialIntent(uint LocalId, byte Material) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [.. EditText.Object(LocalId), .. (Material > 6 ? [new ActionIssue("edit.bad_material")] : Array.Empty<ActionIssue>())];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Ask the region for the physics settings of some objects (its GetObjectPhysicsData capability).</summary>
public sealed record RequestPhysicsIntent(IReadOnlyList<Guid> ObjectIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => ObjectIds.Count == 0 || ObjectIds.Any(i => i == Guid.Empty) ? [new("edit.no_object")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>The Content tab's New Script: the region makes its default script inside the prim (RezScript with no item).</summary>
public sealed record NewScriptIntent(uint LocalId, Guid ObjectId, string Name) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ObjectId == Guid.Empty ? [new("edit.no_object")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>An inventory item dropped on the Content tab goes into the prim: a script as a script (RezScript), anything else as an item
/// (UpdateTaskInventory). The region copies a copyable item and moves the others.</summary>
public sealed record GiveItemToObjectIntent(uint LocalId, Guid ItemId, bool Script) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ItemId == Guid.Empty ? [new("edit.no_item")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Delete an item from a prim's contents (RemoveTaskInventory).</summary>
public sealed record RemoveObjectItemIntent(uint LocalId, Guid ItemId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ItemId == Guid.Empty ? [new("edit.no_item")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Copy an item out of a prim into the agent's inventory (MoveTaskInventory; the region copies a copyable item and moves the
/// others): into <paramref name="FolderId"/> when one is given (the folder it was dragged onto), otherwise into the folder for its type.</summary>
public sealed record CopyObjectItemIntent(uint LocalId, Guid ItemId, int AssetType, Guid FolderId = default) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || ItemId == Guid.Empty ? [new("edit.no_item")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Change an item inside a prim, its name or description (UpdateTaskInventory with the item's other fields as the region listed
/// them; llpanelobjectinventory.cpp LLTaskInvFVBridge::renameItem sends the whole item with the new name).</summary>
public sealed record UpdateObjectItemIntent(uint LocalId, Guid ObjectId, Viewer.Core.Session.TaskItem Item) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() =>
        LocalId == 0 || ObjectId == Guid.Empty || Item.ItemId == Guid.Empty ? [new("edit.no_item")] : Item.Name.Trim().Length == 0 ? [new("edit.no_name")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Take one object into a chosen inventory folder (DeRezObject, take into the agent's inventory, with that folder).</summary>
public sealed record TakeIntoFolderIntent(uint LocalId, Guid FolderId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 || FolderId == Guid.Empty ? [new("edit.no_folder")] : [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

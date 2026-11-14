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
}

/// <summary>Rename an object.</summary>
public sealed record SetObjectNameIntent(uint LocalId, string Name) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Change an object's description.</summary>
public sealed record SetObjectDescriptionIntent(uint LocalId, string Description) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Set or clear permission bits on objects (by their roots): <paramref name="Field"/> is the owner (2), group (4), everyone (8)
/// or next owner (16).</summary>
public sealed record SetObjectPermissionsIntent(IReadOnlyList<uint> LocalIds, byte Field, bool Set, uint Mask) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Lock or unlock objects (the owner's move and modify).</summary>
public sealed record SetObjectLockedIntent(IReadOnlyList<uint> LocalIds, bool Locked) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Move, turn or resize an object; null parts are left as they are. <paramref name="Linkset"/> moves the whole linkset by its root.</summary>
public sealed record UpdateObjectIntent(uint LocalId, Vector3? Position, Quaternion? Rotation, Vector3? Scale, bool Linkset, bool Uniform = false) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Physical, temporary and phantom.</summary>
public sealed record SetObjectFlagsIntent(uint LocalId, bool Physical, bool Temporary, bool Phantom) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>A new shape for a prim.</summary>
public sealed record SetObjectShapeIntent(uint LocalId, ShapeBytes Shape) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Link the roots, in the order given: the first becomes the new root.</summary>
public sealed record LinkObjectsIntent(IReadOnlyList<uint> OrderedRoots) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Unlink the prims given.</summary>
public sealed record DelinkObjectsIntent(IReadOnlyList<uint> LocalIds) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

/// <summary>Copy objects in place (the Move tool's Shift-drag): with no flags the copy stays where it is and the original stays selected.</summary>
public sealed record DuplicateObjectsIntent(IReadOnlyList<uint> Roots, Vector3 Offset, uint Flags = 0) : ActionIntent
{
    public override ActionKind Kind => ActionKind.EditObject;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new EditObjectCommand(this);
}

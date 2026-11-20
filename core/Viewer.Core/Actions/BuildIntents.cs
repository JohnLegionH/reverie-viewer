// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Building;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

internal static class PlacementRules
{
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static IReadOnlyList<ActionIssue> Check(Placement p, string prefix)
    {
        var issues = new List<ActionIssue>();
        if (!Finite(p.RayStart) || !Finite(p.RayEnd))
            issues.Add(new($"{prefix}.bad_ray"));
        return issues;
    }
}

/// <summary>Make a shape where the ray says (Build, Create): the default half-metre wooden box, or the size and material the new-prim
/// settings give; <paramref name="Shape"/> is the Create tool's button (a box when not given) and <paramref name="Species"/> the kind of tree or
/// grass.</summary>
public sealed record CreateBoxIntent(Placement Placement, Vector3? Scale = null, byte? Material = null, CreateShape Shape = CreateShape.Cube, byte Species = 0) : ActionIntent
{
    public override ActionKind Kind => ActionKind.CreateObject;
    public override IReadOnlyList<ActionIssue> Validate() => PlacementRules.Check(Placement, "create");
    internal override TransportCommand? ToCommand() => new CreateObjectCommand(Placement, Scale ?? DefaultBox.Scale, Material ?? DefaultBox.MaterialWood, Shape, Species);
}

/// <summary>Rez an object from the agent's inventory where the ray says.</summary>
public sealed record RezItemIntent(Guid ItemId, Placement Placement) : ActionIntent
{
    public override ActionKind Kind => ActionKind.RezItem;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>(PlacementRules.Check(Placement, "rez"));
        if (ItemId == Guid.Empty)
            issues.Add(new("rez.no_item"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new RezItemCommand(ItemId, Placement);
}

/// <summary>Delete an object: it goes to the agent's Trash, from where it can be recovered.</summary>
public sealed record DeleteObjectIntent(uint LocalId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.DeleteObject;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 ? [new("delete.no_object")] : [];
    internal override TransportCommand? ToCommand() => new DeleteObjectCommand(LocalId);
}

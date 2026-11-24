// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>Which way objects are lined up: along the grid frame's X, Y or Z.</summary>
public enum ArrangeAxis { X, Y, Z }

/// <summary>Which part of each linkset's box is lined up: its low edge, its middle or its high edge along the axis.</summary>
public enum ArrangeEdge { Min, Centre, Max }

/// <summary>What the linksets line up to: the box that holds every selected linkset, or the linkset picked last (which stays put).</summary>
public enum ArrangeTarget { SelectionBox, LastSelected }

/// <summary>One selected linkset: its root's id and place, and the world box of every prim in it (the root and its children).</summary>
public sealed record ArrangeLinkset(uint Root, Vector3 RootPosition, IReadOnlyList<PivotBox> Parts);

/// <summary>A linkset's new place: its root id and the place its root goes to (sent as a group position).</summary>
public readonly record struct ArrangeMove(uint Root, Vector3 Position);

/// <summary>Why Align is not offered for the selection, or null when it is.</summary>
public enum ArrangeRefusal { None, TooFew, Attachment, NoMove }

/// <summary>
/// The Arrange section's rules as pure arithmetic. Each linkset's box is the smallest box, along the grid frame's axes, that holds every
/// corner of every prim in it (a turned prim counts by its turned corners). Align moves each linkset along one axis so that its low edge,
/// middle or high edge matches the same one of the target box. Our own code; objects move on one axis to the selection box's edge, as
/// Firestorm's Align tool does (read as behaviour, nothing copied).
/// </summary>
public static class ArrangeRules
{
    /// <summary>Whether Align is offered: two or more linksets, none worn, every one movable by the agent.</summary>
    public static ArrangeRefusal Refusal(int linksets, bool anyAttachment, bool allMovable) =>
        anyAttachment ? ArrangeRefusal.Attachment : linksets < 2 ? ArrangeRefusal.TooFew : !allMovable ? ArrangeRefusal.NoMove : ArrangeRefusal.None;

    /// <summary>The box that holds every corner of every part, along the axes of <paramref name="frame"/>.</summary>
    public static (Vector3 Min, Vector3 Max) BoxOf(IReadOnlyList<PivotBox> parts, Quaternion frame)
    {
        var inverse = Quaternion.Inverse(frame);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in parts)
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f) * p.Scale;
                var inFrame = Vector3.Transform(p.Position + Vector3.Transform(corner, p.Rotation), inverse);
                min = Vector3.Min(min, inFrame);
                max = Vector3.Max(max, inFrame);
            }
        return (min, max);
    }

    /// <summary>Where each linkset's root goes so that the chosen edge of its box sits where the target's is. The last linkset in the list
    /// is the one picked last. A linkset already in place is left out, and so is the target in <see cref="ArrangeTarget.LastSelected"/>.</summary>
    public static IReadOnlyList<ArrangeMove> Align(IReadOnlyList<ArrangeLinkset> sets, ArrangeAxis axis, ArrangeEdge edge, ArrangeTarget target, Quaternion frame)
    {
        if (sets.Count < 2)
            return [];
        var boxes = sets.Select(s => BoxOf(s.Parts, frame)).ToArray();
        var box = target == ArrangeTarget.LastSelected
            ? boxes[^1]
            : (boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));
        float At(Vector3 v) => axis switch { ArrangeAxis.X => v.X, ArrangeAxis.Y => v.Y, _ => v.Z };
        float EdgeOf((Vector3 Min, Vector3 Max) b) => edge switch { ArrangeEdge.Min => At(b.Min), ArrangeEdge.Max => At(b.Max), _ => (At(b.Min) + At(b.Max)) / 2f };
        var along = axis switch { ArrangeAxis.X => Vector3.UnitX, ArrangeAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ };
        float want = EdgeOf(box);
        var moves = new List<ArrangeMove>();
        for (int i = 0; i < sets.Count; i++)
        {
            if (target == ArrangeTarget.LastSelected && i == sets.Count - 1)
                continue;
            float d = want - EdgeOf(boxes[i]);
            if (MathF.Abs(d) < 1e-5f)
                continue;
            moves.Add(new ArrangeMove(sets[i].Root, sets[i].RootPosition + Vector3.Transform(along * d, frame)));
        }
        return moves;
    }
}

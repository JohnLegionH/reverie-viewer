// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;

namespace Viewer.Shell.Build;

public sealed partial class EditController
{
    /// <summary>The selected linksets' roots, each once, with the root of the object picked last at the end.</summary>
    private List<uint> ArrangeRoots()
    {
        var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToList();
        if (_selection.Count > 0 && RootOf(_selection[^1]) is var last && last != 0)
        {
            roots.Remove(last);
            roots.Add(last);
        }
        return roots;
    }

    /// <summary>How many linksets the selection holds (each root once, whether a root or a part of it was picked).</summary>
    public int ArrangeLinksets => _selection.Select(RootOf).Where(r => r != 0).Distinct().Count();

    /// <summary>Why Align is not offered for the selection (see <see cref="ArrangeRules.Refusal"/>), or None.</summary>
    public ArrangeRefusal ArrangeStatus
    {
        get
        {
            var roots = ArrangeRoots();
            return ArrangeRules.Refusal(roots.Count, WornInSelection(), roots.All(r => Access(r).CanPlace));
        }
    }

    /// <summary>Why Distribute is not offered for the selection (see <see cref="ArrangeRules.DistributeRefusal"/>), or None.</summary>
    public ArrangeRefusal DistributeStatus
    {
        get
        {
            var roots = ArrangeRoots();
            return ArrangeRules.DistributeRefusal(roots.Count, WornInSelection(), roots.All(r => Access(r).CanPlace));
        }
    }

    /// <summary>Distribute: the selected linksets are spaced evenly along one axis of the grid's frame, by their centres or by the gaps
    /// between their boxes; the two outermost stay put. The region gets one message with a group position per moved root. False when
    /// Distribute is refused or nothing needs to move.</summary>
    public bool Distribute(ArrangeAxis axis, DistributeMode mode)
    {
        if (DistributeStatus != ArrangeRefusal.None || LinksetsForArrange() is not { } sets)
            return false;
        return SendMoves(ArrangeRules.Distribute(sets, axis, mode, GridFrame().Turn));
    }

    /// <summary>Align: every selected linkset moves along one axis of the grid's frame so that its low edge, middle or high edge matches the
    /// target's. The region gets one message with a group position per root. False when Align is refused or nothing needs to move.</summary>
    public bool Align(ArrangeAxis axis, ArrangeEdge edge, ArrangeTarget target)
    {
        if (ArrangeStatus != ArrangeRefusal.None)
            return false;
        if (LinksetsForArrange() is not { } sets)
            return false;
        return SendMoves(ArrangeRules.Align(sets, axis, edge, target, GridFrame().Turn));
    }

    private bool WornInSelection() => _selection.Any(l => Find(RootOf(l))?.AttachmentPoint is not null || Find(l)?.AttachmentPoint is not null);

    /// <summary>Each selected linkset with the world box of every prim in it, or null when one of them is not in the scene.</summary>
    private List<ArrangeLinkset>? LinksetsForArrange()
    {
        var objects = _world.Snapshot().Objects.Values.ToList();
        var sets = new List<ArrangeLinkset>();
        foreach (var root in ArrangeRoots())
        {
            var parts = new List<PivotBox>();
            foreach (var o in objects.Where(o => o.Id.LocalId == root || o.ParentLocalId == root))
                if (WorldOf(o.Id.LocalId) is { } pose)
                    parts.Add(new PivotBox(pose.Position, pose.Rotation, o.Scale));
            if (parts.Count == 0 || Find(root) is not { } rootObject)
                return null;
            sets.Add(new ArrangeLinkset(root, rootObject.Position, parts));
        }
        return sets;
    }

    private bool SendMoves(IReadOnlyList<ArrangeMove> moves) =>
        moves.Count > 0 && Send(new MoveLinksetsIntent([.. moves.Select(m => new LinksetMove(m.Root, m.Position))]));
}

public sealed partial class EditController
{
    /// <summary>How many linksets and parts are selected, for the Build window's count: whole linksets count every prim in them; with Edit
    /// linked only the picked parts count.</summary>
    public (int Linksets, int Parts) SelectionCounts()
    {
        if (_selection.Count == 0)
            return (0, 0);
        var roots = _selection.Select(RootOf).Where(r => r != 0).ToHashSet();
        int parts = EditLinked
            ? _selection.Distinct().Count(l => Find(l) is not null)
            : _world.Snapshot().Objects.Values.Count(o => roots.Contains(o.Id.LocalId) || roots.Contains(o.ParentLocalId));
        return (roots.Count, parts);
    }
}

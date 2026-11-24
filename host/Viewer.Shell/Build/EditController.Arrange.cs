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
            bool worn = _selection.Any(l => Find(l)?.AttachmentPoint is not null);
            return ArrangeRules.Refusal(roots.Count, worn, roots.All(r => Access(r).CanPlace));
        }
    }

    /// <summary>Align: every selected linkset moves along one axis of the grid's frame so that its low edge, middle or high edge matches the
    /// target's. The region gets one message with a group position per root. False when Align is refused or nothing needs to move.</summary>
    public bool Align(ArrangeAxis axis, ArrangeEdge edge, ArrangeTarget target)
    {
        if (ArrangeStatus != ArrangeRefusal.None)
            return false;
        var objects = _world.Snapshot().Objects.Values.ToList();
        var sets = new List<ArrangeLinkset>();
        foreach (var root in ArrangeRoots())
        {
            var parts = new List<PivotBox>();
            foreach (var o in objects.Where(o => o.Id.LocalId == root || o.ParentLocalId == root))
                if (WorldOf(o.Id.LocalId) is { } pose)
                    parts.Add(new PivotBox(pose.Position, pose.Rotation, o.Scale));
            if (parts.Count == 0 || Find(root) is not { } rootObject)
                return false;
            sets.Add(new ArrangeLinkset(root, rootObject.Position, parts));
        }
        var moves = ArrangeRules.Align(sets, axis, edge, target, GridFrame().Turn);
        if (moves.Count == 0)
            return false;
        return Send(new MoveLinksetsIntent([.. moves.Select(m => new LinksetMove(m.Root, m.Position))]));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

public sealed partial class EditController
{
    private bool _snapToObjects;

    /// <summary>Snap to objects: a Move drag catches on other objects' boxes. Off by default.</summary>
    public bool SnapToObjects
    {
        get => _snapToObjects;
        set
        {
            if (_snapToObjects == value)
                return;
            _snapToObjects = value;
            Changed?.Invoke();
        }
    }

    private IReadOnlyList<(Vector3 From, Vector3 To)> _snapLines = [];

    /// <summary>The guides of what the drag's latest step caught on, as lines in the region's frame; empty outside a drag.</summary>
    public IReadOnlyList<(Vector3 From, Vector3 To)> SnapLines => _snapLines;

    /// <summary>What a drag can catch on, read once when it begins: the moving selection's box and the other boxes near it, in the grid
    /// frame the drag began in.</summary>
    private sealed record SnapScene(Quaternion Frame, SnapBox Moving, IReadOnlyList<SnapBox> Others);
    private SnapScene? _snapScene;

    /// <summary>Reads the boxes a drag can catch on again, for an object that arrived after it began (a Shift-drag's copy). The moving box
    /// and the grid frame stay those of the drag's start, which every step is measured from.</summary>
    public void ReadSnapTargetsAgain()
    {
        if (_manip is { } starts && _snapScene is { } was)
            _snapScene = ReadSnapScene(starts.Select(s => s.Local).ToArray(), was) ?? was;
    }

    /// <summary>A linkset not being moved is one box. Where a linkset's parts are being moved (Edit linked parts), each of its other prims
    /// is a box of its own. Worn objects are left out.</summary>
    private SnapScene? ReadSnapScene(IReadOnlyList<uint> moved, SnapScene? start = null)
    {
        var all = _world.Snapshot().Objects.Values.ToList();
        var byLocal = new Dictionary<uint, ObjectState>();
        foreach (var o in all)
            byLocal[o.Id.LocalId] = o;
        uint Root(ObjectState o)
        {
            for (int depth = 0; depth < 16 && o.ParentLocalId != 0 && byLocal.TryGetValue(o.ParentLocalId, out var parent); depth++)
                o = parent;
            return o.Id.LocalId;
        }
        // a root takes its whole linkset with it; a part moves alone
        var picked = moved.ToHashSet();
        var going = all.Where(o => picked.Contains(o.Id.LocalId) || picked.Contains(Root(o))).Select(o => o.Id.LocalId).ToHashSet();
        var frame = start?.Frame ?? GridFrame().Turn;
        PivotBox? BoxAt(ObjectState o) =>
            ObjectPicker.WorldPose(_world, o, out var at, out var turn) ? new PivotBox(at, turn, o.Scale) : null;
        SnapBox Round(IReadOnlyList<PivotBox> parts)
        {
            var (min, max) = ArrangeRules.BoxOf(parts, frame);
            return new SnapBox(min, max);
        }
        var movingParts = all.Where(o => going.Contains(o.Id.LocalId)).Select(BoxAt).OfType<PivotBox>().ToList();
        if (movingParts.Count == 0)
            return null;
        var moving = start?.Moving ?? Round(movingParts);
        var split = going.Select(l => Root(byLocal[l])).ToHashSet();
        var others = new List<SnapBox>();
        var still = all.Where(o => !going.Contains(o.Id.LocalId)).Select(o => (Object: o, Root: Root(o)))
            .Where(x => byLocal[x.Root] is { AttachmentPoint: null, ParentLocalId: 0 });
        foreach (var linkset in still.GroupBy(x => x.Root))
        {
            var parts = linkset.Select(x => BoxAt(x.Object)).OfType<PivotBox>().ToList();
            if (parts.Count == 0)
                continue;
            if (split.Contains(linkset.Key))
                others.AddRange(parts.Select(p => Round([p])));
            else
                others.Add(Round(parts));
        }
        others.RemoveAll(b => ObjectSnap.Apart(moving, b) > ObjectSnap.Reach);
        return new SnapScene(frame, moving, others);
    }

    /// <summary>The drag's move with Snap to objects applied. <paramref name="pointer"/> is where the pointer has taken the selection and
    /// <paramref name="snapped"/> the same after grid snap: an axis that catches on an object takes its place from the pointer's own move
    /// (so a face off the grid can be reached), the other axes keep the grid's.</summary>
    private Vector3 SnapToOthers(Vector3 pointer, Vector3 snapped, float distance, bool letGo)
    {
        _snapLines = [];
        if (!_snapToObjects || _snapScene is not { } scene)
            return snapped;
        var inverse = Quaternion.Inverse(scene.Frame);
        var result = ObjectSnap.Snap(scene.Moving, Vector3.Transform(pointer, inverse), scene.Others,
            MathF.Min(distance, ObjectSnap.MaxDistance), letGo);
        if (result.Guides.Count == 0)
            return snapped;
        var onGrid = Vector3.Transform(snapped, inverse);
        foreach (var guide in result.Guides)
        {
            if (guide.Axis == 0) onGrid.X = result.Move.X;
            else if (guide.Axis == 1) onGrid.Y = result.Move.Y;
            else onGrid.Z = result.Move.Z;
        }
        _snapLines = result.Guides.SelectMany(g => ObjectSnap.Outline(g, scene.Frame)).ToArray();
        return Vector3.Transform(onGrid, scene.Frame);
    }
}

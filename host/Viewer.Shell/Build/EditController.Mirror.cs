// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

public sealed partial class EditController
{
    /// <summary>The prims Mirror works on: every prim of each selected linkset, or, with Edit linked parts, the selected parts of a linkset
    /// (a selected root is left out then: its place cannot change without its parts coming along). Roots come before their parts.</summary>
    private List<uint> MirrorTargets()
    {
        var objects = _world.Snapshot().Objects.Values.ToList();
        var targets = new List<uint>();
        if (_editLinked)
        {
            foreach (var l in _selection.Distinct())
                if (Find(l) is { ParentLocalId: not 0 })
                    targets.Add(l);
            return targets;
        }
        foreach (var root in _selection.Select(RootOf).Where(r => r != 0).Distinct())
        {
            targets.Add(root);
            targets.AddRange(objects.Where(o => o.ParentLocalId == root).Select(o => o.Id.LocalId).OrderBy(x => x));
        }
        return targets;
    }

    /// <summary>Why Mirror is not offered for the selection (see <see cref="MirrorRules.Refusal"/>), or None.</summary>
    public ArrangeRefusal MirrorStatus
    {
        get
        {
            var targets = MirrorTargets();
            return MirrorRules.Refusal(targets.Count, WornInSelection(), targets.All(t => Access(t).CanPlace));
        }
    }

    /// <summary>Mirror: the selected linksets (or the parts picked with Edit linked parts) are reflected about a plane square to one axis of
    /// the grid's frame, through the middle of what is mirrored or through the pivot point. Each root goes first (its group position, then
    /// its own turn); each part then gets its place and turn in its root's new frame. Region undo steps one part back at a time. False when
    /// Mirror is refused or nothing needs to change.</summary>
    public bool Mirror(ArrangeAxis axis, MirrorPlane plane)
    {
        if (MirrorStatus != ArrangeRefusal.None)
            return false;
        var targets = MirrorTargets();
        if (MirrorPoses() is not { } parts || MirrorPlan(axis, plane) is not { } mirrored)
            return false;
        var intents = new List<UpdateObjectIntent>();
        foreach (var root in parts.Where(p => p.Parent == 0 || !targets.Contains(p.Parent)).Select(p => p.Parent == 0 ? p.Local : p.Parent).Distinct())
        {
            var now = mirrored.FirstOrDefault(p => p.Local == root);
            if (now.Local == 0)
            {
                // Edit linked: the root stays; its parts are placed in its present frame
                if (WorldOf(root) is not { } rootPose)
                    return false;
                foreach (var part in mirrored.Where(p => p.Parent == root))
                    intents.Add(InFrame(part, rootPose.Position, rootPose.Rotation));
                continue;
            }
            var old = parts.First(p => p.Local == root);
            var children = mirrored.Where(p => p.Parent == root).ToList();
            bool moved = Vector3.DistanceSquared(old.Position, now.Position) > 1e-10f;
            bool turned = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(old.Rotation), Quaternion.Normalize(now.Rotation))) < 1f - 1e-9f;
            if (children.Count == 0)
            {
                if (moved || turned)
                    intents.Add(new UpdateObjectIntent(root, moved ? now.Position : null, turned ? now.Rotation : null, null, true));
                continue;
            }
            if (moved)
                intents.Add(new UpdateObjectIntent(root, now.Position, null, null, true));
            if (turned)
                intents.Add(new UpdateObjectIntent(root, null, now.Rotation, null, false));   // the root alone turns: its parts keep their place
            foreach (var part in children)
                intents.Add(InFrame(part, now.Position, now.Rotation));
        }
        bool any = false;
        foreach (var intent in intents)
            any |= Send(intent);
        return any;
    }

    /// <summary>Where each prim Mirror works on stands now (place and turn in the region), or null when one is not in the scene.</summary>
    public IReadOnlyList<MirrorPart>? MirrorPoses()
    {
        var parts = new List<MirrorPart>();
        foreach (var l in MirrorTargets())
        {
            if (Find(l) is not { } o || WorldOf(l) is not { } w)
                return null;
            parts.Add(new MirrorPart(l, o.ParentLocalId, w.Position, w.Rotation));
        }
        return parts;
    }

    /// <summary>Where each prim would stand after Mirror with these choices (the rules' answer, nothing sent), or null when it cannot be worked out.</summary>
    public IReadOnlyList<MirrorPart>? MirrorPlan(ArrangeAxis axis, MirrorPlane plane)
    {
        if (MirrorPoses() is not { Count: > 0 } parts)
            return null;
        var boxes = parts.Select(p => new PivotBox(p.Position, p.Rotation, Find(p.Local)!.Scale)).ToList();
        var point = plane == MirrorPlane.Pivot && Pivot is { } pivot ? pivot : PivotRules.Centre(boxes);
        return MirrorRules.Mirror(parts, point, MirrorRules.Normal(axis, GridFrame().Turn));
    }

    /// <summary>A part's world place and turn as the place and turn in its root's frame, which is how a part is sent.</summary>
    private static UpdateObjectIntent InFrame(MirrorPart part, Vector3 rootPosition, Quaternion rootRotation)
    {
        var inverse = Quaternion.Inverse(rootRotation);
        return new UpdateObjectIntent(part.Local, Vector3.Transform(part.Position - rootPosition, inverse), Quaternion.Normalize(inverse * part.Rotation), null, false);
    }
}

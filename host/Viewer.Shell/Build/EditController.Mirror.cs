// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
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

    /// <summary>The string key of why Mirror is not offered (the button's tip), or null when it is offered.</summary>
    public string? MirrorRefusalKey => MirrorStatus switch
    {
        ArrangeRefusal.None => null,
        ArrangeRefusal.Attachment => "edit.mirror.no.attachment",
        ArrangeRefusal.NoMove => "edit.mirror.no.move",
        // Edit linked parts with only a root picked: a root cannot move on its own there, so the person is told to pick parts
        _ => _editLinked && _selection.Count > 0 ? "edit.mirror.no.parts" : "edit.mirror.no.few",
    };

    /// <summary>Mirror: the selected linksets (or the parts picked with Edit linked parts) are reflected about a plane square to one axis of
    /// the grid's frame, through the middle of what is mirrored or through the pivot point. Each root goes first (its group position, then
    /// its own turn); each part then gets its place and turn in its root's new frame, and its shape flipped to match (see
    /// <see cref="ShapeMirror"/>; a mesh, or a shape no parameters can flip, is only placed and named in <see cref="MirrorNotice"/>). Region
    /// undo steps one part back at a time. False when Mirror is refused or nothing needs to change.</summary>
    public bool Mirror(ArrangeAxis axis, MirrorPlane plane) => MirrorAbout(axis, plane, null);

    private bool MirrorAbout(ArrangeAxis axis, MirrorPlane plane, Vector3? fixedPoint)
    {
        if (MirrorStatus != ArrangeRefusal.None)
            return false;
        bool hadNotice = _mirrorNotice.Count > 0;
        _mirrorNotice.Clear();
        var targets = MirrorTargets();
        if (MirrorPoses() is not { } parts || MirrorPlan(axis, plane, fixedPoint) is not { } mirrored)
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
            bool moved = Vector3.DistanceSquared(old.Position, now.Position) > 1e-8f;   // 0.1 mm: below what the region keeps
            bool turned = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(old.Rotation), Quaternion.Normalize(now.Rotation))) < 1f - 1e-6f;   // about 0.16 degrees; a float cannot tell 1 - 1e-9 from 1
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
        any |= SendFlippedShapes(parts, MirrorRules.Normal(axis, GridFrame().Turn));
        if (_mirrorNotice.Count > 0 || hadNotice)
            Changed?.Invoke();
        return any;
    }

    /// <summary>Each part's shape flipped about the local plane its turn was chosen for (<see cref="MirrorRules.FlipAxis"/>): a prim's
    /// shape block, a sculpt's map block with its Mirror bit toggled. A part that has no flipped shape is named in the notice.</summary>
    private bool SendFlippedShapes(IReadOnlyList<MirrorPart> parts, Vector3 normal)
    {
        bool any = false;
        foreach (var part in parts)
        {
            if (Find(part.Local)?.Shape is not { } shape)
                continue;
            int flipAbout = MirrorRules.FlipAxis(part.Rotation, normal, AllowedAxes(shape));
            if (ShapeMirror.Flip(shape, flipAbout) is not { } flipped)
            {
                _mirrorNotice.Add(PropertiesOf(part.Local)?.Name is { Length: > 0 } name ? name : $"part {part.Local}");
                continue;
            }
            if (ShapeMirror.SameOnTheWire(shape, flipped))
                continue;
            if (!Access(part.Local).CanReshape)
            {
                _mirrorNotice.Add(PropertiesOf(part.Local)?.Name is { Length: > 0 } nm ? nm : $"part {part.Local}");   // may be moved but not modified: the shape cannot be sent
                continue;
            }
            any |= shape.IsSculpt
                ? Send(new SetSculptIntent(part.Local, flipped.SculptId, flipped.SculptType | flipped.SculptFlags))
                : Send(new SetObjectShapeIntent(part.Local, ShapeRules.Encode(flipped)));
        }
        return any;
    }

    /// <summary>The local axes (1 X, 2 Y, 4 Z) a shape can be flipped about; all of them when none can (the part is then only placed).</summary>
    private static int AllowedAxes(PrimShape shape)
    {
        int mask = 0;
        for (int axis = 0; axis < 3; axis++)
            if (ShapeMirror.Flip(shape, axis) is not null)
                mask |= 1 << axis;
        return mask == 0 ? 7 : mask;
    }

    // ---------------- the notice ----------------

    private readonly List<string> _mirrorNotice = new();

    /// <summary>The parts the last Mirror could only place, not flip (a mesh, or a shape no parameters can flip), until the notice is closed.</summary>
    public IReadOnlyList<string> MirrorNotice => _mirrorNotice;

    /// <summary>OK on the notice.</summary>
    public void CloseMirrorNotice()
    {
        if (_mirrorNotice.Count == 0)
            return;
        _mirrorNotice.Clear();
        Changed?.Invoke();
    }

    // ---------------- Mirror a copy ----------------

    private sealed class PendingMirrorCopy(ArrangeAxis axis, MirrorPlane plane, Vector3 point, List<(Vector3 At, int Parts, uint Copy)> originals)
    {
        public readonly ArrangeAxis Axis = axis;
        public readonly MirrorPlane Plane = plane;
        public readonly Vector3 Point = point;
        public readonly List<(Vector3 At, int Parts, uint Copy)> Originals = originals;
    }

    private PendingMirrorCopy? _mirrorCopy;

    /// <summary>Mirror a copy is offered where Mirror and Duplicate both are, for whole linksets (not with Edit linked parts).</summary>
    public bool CanMirrorCopy => !_editLinked && (_mirrorCopy is null || _clock() > _copiesUntil) && MirrorStatus == ArrangeRefusal.None && CanDuplicate
        && _selection.Select(RootOf).Where(r => r != 0).Distinct().All(r => ((Find(r)?.Interaction?.Flags ?? 0) & ObjectFlagBits.Copy) != 0);

    /// <summary>Mirror a copy: the selected linksets are copied where they stand (the copies arrive selected), and when every part of each
    /// copy is here the copies are mirrored about the plane the originals gave (through the middle of the originals, or the pivot), so the
    /// originals stay and each copy lands as their image. False when it is refused or the copy could not be asked for.</summary>
    public bool MirrorCopy(ArrangeAxis axis, MirrorPlane plane)
    {
        if (!CanMirrorCopy || MirrorPoses() is not { Count: > 0 } parts || MirrorPlanePoint(plane, parts) is not { } point)
            return false;
        var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToArray();
        var objects = _world.Snapshot().Objects.Values.ToList();
        var originals = roots.Select(r => (Find(r)!.Position, objects.Count(o => o.ParentLocalId == r), 0u)).ToList();
        if (!Send(new DuplicateObjectsIntent(roots, Vector3.Zero, LmvDuplicateCreateSelected)))
            return false;
        // the copies come selected where the originals stand; the originals are let go (as for Duplicate)
        _copiesAt.Clear();
        _copiesAt.AddRange(originals.Select(o => o.Position));
        _copiesUntil = _clock() + CopyWaitMs;
        SendDeselect(_selection);
        _selection.Clear();
        _faceSelection.Clear();
        _mirrorCopy = new PendingMirrorCopy(axis, plane, point, originals);
        Changed?.Invoke();
        return true;
    }

    /// <summary>A copy asked for by Mirror a copy, or one of its parts, arrived: once every copy has its root and all its parts, mirror them.</summary>
    private void MirrorCopyStep(PendingMirrorCopy pending, ObjectState o, bool joined)
    {
        if (_clock() > _copiesUntil)
        {
            _mirrorCopy = null;
            return;
        }
        if (joined)
        {
            int at = pending.Originals.FindIndex(x => x.Copy == 0 && Vector3.Distance(x.At, o.Position) < 0.25f);
            if (at < 0)
                at = pending.Originals.FindIndex(x => x.Copy == 0);
            if (at >= 0)
                pending.Originals[at] = pending.Originals[at] with { Copy = o.Id.LocalId };
        }
        if (pending.Originals.Any(x => x.Copy == 0))
            return;
        var objects = _world.Snapshot().Objects.Values.ToList();
        // the copies may have come in any order: compare how many parts each copy has with how many each original had
        var have = pending.Originals.Select(x => objects.Count(c => c.ParentLocalId == x.Copy)).OrderBy(n => n);
        if (!have.SequenceEqual(pending.Originals.Select(x => x.Parts).OrderBy(n => n)))
            return;
        _mirrorCopy = null;
        // whatever was picked meanwhile, the copies are what gets mirrored
        _selection.Clear();
        foreach (var x in pending.Originals)
            Add(x.Copy);
        MirrorAbout(pending.Axis, pending.Plane, pending.Point);
    }

    // ---------------- the plan ----------------

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

    /// <summary>A point of the mirror plane: the middle of the parts' boxes, or the pivot point.</summary>
    private Vector3? MirrorPlanePoint(MirrorPlane plane, IReadOnlyList<MirrorPart> parts)
    {
        if (parts.Count == 0 || parts.Any(p => Find(p.Local) is null))
            return null;
        var boxes = parts.Select(p => new PivotBox(p.Position, p.Rotation, Find(p.Local)!.Scale)).ToList();
        return plane == MirrorPlane.Pivot && Pivot is { } pivot ? pivot : PivotRules.Centre(boxes);
    }

    /// <summary>Where each prim would stand after Mirror with these choices (the rules' answer, nothing sent), or null when it cannot be worked out.</summary>
    public IReadOnlyList<MirrorPart>? MirrorPlan(ArrangeAxis axis, MirrorPlane plane) => MirrorPlan(axis, plane, null);

    private IReadOnlyList<MirrorPart>? MirrorPlan(ArrangeAxis axis, MirrorPlane plane, Vector3? fixedPoint)
    {
        if (MirrorPoses() is not { Count: > 0 } parts || (fixedPoint ?? MirrorPlanePoint(plane, parts)) is not { } point)
            return null;
        return MirrorRules.Mirror(parts, point, MirrorRules.Normal(axis, GridFrame().Turn), l => Find(l)?.Shape is { } shape ? AllowedAxes(shape) : 7);
    }

    /// <summary>A part's world place and turn as the place and turn in its root's frame, which is how a part is sent.</summary>
    private static UpdateObjectIntent InFrame(MirrorPart part, Vector3 rootPosition, Quaternion rootRotation)
    {
        var inverse = Quaternion.Inverse(rootRotation);
        return new UpdateObjectIntent(part.Local, Vector3.Transform(part.Position - rootPosition, inverse), Quaternion.Normalize(inverse * part.Rotation), null, false);
    }
}

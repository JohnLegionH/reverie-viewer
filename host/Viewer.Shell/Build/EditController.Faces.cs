// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>
/// The Texture tab, after the official viewer's LLPanelFace and LLToolFace (read as behaviour only): Select Face picks single faces
/// (Shift adds and removes, on any prim, a linked part's included); an edit goes to the selected faces, or to every face of every
/// selected prim without Select Face; Align planar faces lines the selected planar faces up with the last one selected; PBR materials
/// are put on and taken off the same faces, with their texture transforms.
/// </summary>
public sealed partial class EditController
{
    private readonly List<(uint Local, int Face)> _faceSelection = new();
    private readonly PickGeometryCache _faceGeometry = new();
    private bool _selectFaces;

    /// <summary>"Select Face": a click picks one face instead of the object (the official radio choice beside Move, Rotate and Stretch).</summary>
    public bool SelectFaces
    {
        get => _selectFaces;
        set
        {
            if (_selectFaces == value)
                return;
            _selectFaces = value;
            if (!value)
                _faceSelection.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>The selected faces in the order they were picked, the latest last.</summary>
    public IReadOnlyList<(uint Local, int Face)> SelectedFaces => _faceSelection.ToArray();

    /// <summary>A click with Select Face on (lltoolface.cpp): the face alone, or with Shift added or removed; a click on nothing clears.</summary>
    private bool ClickFace(PickResult pick, bool shift)
    {
        if (pick.Kind != PickKind.Object || pick.Hit is not { } hit || _world.Object(hit.Id) is not { } prim || prim.AttachmentPoint is not null || hit.Face < 0)
        {
            if (!shift)
                Deselect();
            return true;
        }
        uint local = prim.Id.LocalId;
        var key = (local, hit.Face);
        if (shift)
        {
            if (_faceSelection.Remove(key))
            {
                if (!_faceSelection.Any(f => f.Local == local) && _selection.Remove(local))
                    SendDeselect([local]);
            }
            else
            {
                _faceSelection.Add(key);
                if (!_selection.Contains(local))
                    Add(local);
            }
        }
        else
        {
            SendDeselect(_selection.Where(s => s != local));
            bool had = _selection.Contains(local);
            _selection.Clear();
            _faceSelection.Clear();
            _faceSelection.Add(key);
            if (had)
                _selection.Add(local);
            else
                Add(local);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>How many faces a prim has (its shape's faces; a box's six when the shape is not known).</summary>
    private int FaceCount(ObjectState o)
    {
        var faces = _faceGeometry.Faces(o.Shape);
        return faces.Count == 0 ? 0 : faces.Max(f => f.Face) + 1;
    }

    /// <summary>A face's normal on its object (the average of its geometry's normals).</summary>
    private Vector3 FaceNormal(ObjectState o, int face)
    {
        var sum = Vector3.Zero;
        foreach (var g in _faceGeometry.Faces(o.Shape).Where(f => f.Face == face))
            for (int i = 0; i + 2 < g.Normals.Length; i += 3)
                sum += new Vector3(g.Normals[i], g.Normals[i + 1], g.Normals[i + 2]);
        return sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : Vector3.UnitZ;
    }

    /// <summary>The faces an edit goes to, by prim: the selected faces with Select Face on; otherwise every face of every selected prim,
    /// a selected linkset's parts included.</summary>
    private List<(ObjectState Prim, List<int> Faces)> FaceTargets()
    {
        var result = new List<(ObjectState, List<int>)>();
        if (_selectFaces && _faceSelection.Count > 0)
        {
            foreach (var g in _faceSelection.GroupBy(f => f.Local))
                if (Find(g.Key) is { } o)
                    result.Add((o, g.Select(f => f.Face).Distinct().ToList()));
            return result;
        }
        var prims = new List<uint>();
        foreach (var l in _selection)
        {
            prims.Add(l);
            if (!_editLinked && Find(l) is { ParentLocalId: 0 })
                prims.AddRange(_world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == l).Select(o => o.Id.LocalId));
        }
        foreach (var l in prims.Distinct())
            if (Find(l) is { } o)
                result.Add((o, Enumerable.Range(0, FaceCount(o)).ToList()));
        return result;
    }

    /// <summary>Applies a Texture tab change to the faces it goes to, one message per prim with every face it changes; a prim that may
    /// not be modified is left alone. True when anything was sent.</summary>
    public bool EditFaces(FaceChange change)
    {
        bool any = false;
        foreach (var (o, faces) in FaceTargets())
        {
            if (!Access(o.Id.LocalId).CanReshape || faces.Count == 0)
                continue;
            any |= Send(new SetObjectFacesIntent(o.Id.LocalId, faces.Select(f => new FaceEdit(f, change.ApplyTo(FaceRules.Settings(o, f)))).ToArray()));
        }
        return any;
    }

    /// <summary>The face the Texture tab shows: the last selected face, or the primary prim's first face.</summary>
    public FaceSettings? FaceShown() => ShownFace() is { } f ? FaceRules.Settings(f.Prim, f.Face) : null;

    private (ObjectState Prim, int Face)? ShownFace()
    {
        if (_selectFaces && _faceSelection.Count > 0 && Find(_faceSelection[^1].Local) is { } o)
            return (o, _faceSelection[^1].Face);
        return Primary is { } p && Find(p) is { } q ? (q, 0) : null;
    }

    /// <summary>The shown face's prim size and face, for repeats per metre.</summary>
    public (Vector3 Scale, int Face)? ShownFaceSize => ShownFace() is { } f ? (f.Prim.Scale, f.Face) : null;

    /// <summary>The shown face's PBR material, or empty.</summary>
    public Guid ShownMaterial => ShownFace() is { } f ? f.Prim.Materials?[f.Face].RenderMaterialId ?? Guid.Empty : Guid.Empty;

    /// <summary>The shown face's PBR texture transform (its override's base colour transform).</summary>
    public TextureTransform ShownMaterialTransform => ShownFace() is { } f ? MaterialTransform(f.Prim, f.Face) : TextureTransform.Identity;

    private static TextureTransform MaterialTransform(ObjectState o, int face) =>
        o.MaterialOverrides?[face] is { } json ? new PbrMaterial().WithOverride(json).BaseColorTransform : TextureTransform.Identity;

    /// <summary>Repeats per metre: every face gets the repeats its own two sides of its prim need.</summary>
    public bool SetRepeatsPerMetre(float perMetre)
    {
        bool any = false;
        foreach (var (o, faces) in FaceTargets())
        {
            if (!Access(o.Id.LocalId).CanReshape || faces.Count == 0)
                continue;
            any |= Send(new SetObjectFacesIntent(o.Id.LocalId, faces.Select(f =>
            {
                var (u, v) = FaceRules.RepeatsForPerMetre(perMetre, f, o.Scale);
                return new FaceEdit(f, new FaceChange(RepeatU: u, RepeatV: v).ApplyTo(FaceRules.Settings(o, f)));
            }).ToArray()));
        }
        return any;
    }

    private FaceRules.PlacedFace? Placed(ObjectState o, int face) =>
        ObjectPicker.WorldPose(_world, o, out var p, out var r) ? new FaceRules.PlacedFace(p, r, FaceNormal(o, face)) : null;

    /// <summary>Align planar faces (the Texture tab's button): every selected planar face takes the last selected face's repeats and the
    /// rotation and offset that make its texture run on across them; PBR faces have their texture transforms lined up the same way.
    /// Faces that are not planar are left alone. True when anything was sent.</summary>
    public bool AlignPlanarFaces()
    {
        if (!_selectFaces || _faceSelection.Count < 2 || Find(_faceSelection[^1].Local) is not { } anchorPrim)
            return false;
        int anchorFace = _faceSelection[^1].Face;
        if (Placed(anchorPrim, anchorFace) is not { } anchor)
            return false;
        var anchorSettings = FaceRules.Settings(anchorPrim, anchorFace);
        Guid anchorMaterial = anchorPrim.Materials?[anchorFace].RenderMaterialId ?? Guid.Empty;
        var anchorTransform = MaterialTransform(anchorPrim, anchorFace);
        bool any = false;
        foreach (var g in _faceSelection.Take(_faceSelection.Count - 1).GroupBy(f => f.Local))
        {
            if (Find(g.Key) is not { } o || !Access(o.Id.LocalId).CanReshape)
                continue;
            var edits = new List<FaceEdit>();
            var pbr = new List<(int Face, Guid Material, TextureTransform T)>();
            foreach (int face in g.Select(f => f.Face).Distinct())
            {
                if (Placed(o, face) is not { } target)
                    continue;
                var settings = FaceRules.Settings(o, face);
                if (FaceRules.AlignPlanar(anchor, anchorSettings, target, settings) is { } aligned)
                    edits.Add(new FaceEdit(face, aligned));
                Guid material = o.Materials?[face].RenderMaterialId ?? Guid.Empty;
                if (material != Guid.Empty && anchorMaterial != Guid.Empty && settings.TexGen == TexGen.Planar && anchorSettings.TexGen == TexGen.Planar)
                    pbr.Add((face, material, FaceRules.AlignPlanarPbr(anchor, anchorTransform, target)));
            }
            if (edits.Count > 0)
                any |= Send(new SetObjectFacesIntent(o.Id.LocalId, edits));
            foreach (var p in pbr)
                any |= Send(new SetFaceMaterialIntent(o.FullId, o.Id.LocalId, [p.Face], p.Material, FaceRules.TransformOverrideJson(p.T)));
        }
        return any;
    }

    /// <summary>Puts a PBR material (a material asset) on the faces an edit goes to.</summary>
    public bool ApplyMaterial(Guid materialId) => SendMaterial(materialId);

    /// <summary>Takes the PBR material off the faces an edit goes to.</summary>
    public bool RemoveMaterial() => SendMaterial(Guid.Empty);

    private bool SendMaterial(Guid materialId)
    {
        bool any = false;
        foreach (var (o, faces) in FaceTargets())
            if (Access(o.Id.LocalId).CanReshape && faces.Count > 0)
                any |= Send(new SetFaceMaterialIntent(o.FullId, o.Id.LocalId, faces, materialId, null));
        return any;
    }

    /// <summary>The PBR texture transform (the Texture tab's PBR repeats, offset and rotation) on the faces an edit goes to that have a
    /// PBR material, sent as their override with their material kept; faces without one are left alone.</summary>
    public bool SetMaterialTransform(TextureTransform transform)
    {
        bool any = false;
        string json = FaceRules.TransformOverrideJson(transform);
        foreach (var (o, faces) in FaceTargets())
        {
            if (!Access(o.Id.LocalId).CanReshape)
                continue;
            foreach (var g in faces.GroupBy(f => o.Materials?[f].RenderMaterialId ?? Guid.Empty).Where(g => g.Key != Guid.Empty))
                any |= Send(new SetFaceMaterialIntent(o.FullId, o.Id.LocalId, g.ToArray(), g.Key, json));
        }
        return any;
    }
}

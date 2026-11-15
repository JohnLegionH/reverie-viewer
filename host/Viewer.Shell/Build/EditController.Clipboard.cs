// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>What the Object tab's copy and paste carry: position, size, rotation, all three together, or the object's parameters (its shape).</summary>
public enum ObjectClip { Position, Size, Rotation, All, Parameters }

/// <summary>
/// Copy and paste on the Object and Texture tabs, after the official viewer's LLPanelObject and LLPanelFace (read as behaviour only).
/// The Object tab copies the primary object's position, size and rotation as the window shows them (each also as text, "&lt;x, y, z&gt;",
/// for the system clipboard) and its shape; a paste sets the selected object's value as if typed, a position held inside the region
/// first. The Texture tab copies the selected faces of one object (all of its faces without Select Face) and pastes them onto the
/// selected faces of one object: one copied face onto every one, or face by face when as many were copied as are pasted onto.
/// </summary>
public sealed partial class EditController
{
    private Vector3? _clipPosition, _clipSize, _clipRotation;
    private PrimShape? _clipShape;
    private List<(FaceSettings Settings, Guid Material)>? _clipFaces;

    private static string Text(Vector3 v)
    {
        static string F(float x)
        {
            double r = Math.Round(x, 3);
            return (r == 0 ? 0 : r).ToString("G", CultureInfo.InvariantCulture);
        }
        return $"<{F(v.X)}, {F(v.Y)}, {F(v.Z)}>";
    }

    private static Vector3 Rounded(Vector3 v) => new((float)Math.Round(v.X, 3), (float)Math.Round(v.Y, 3), (float)Math.Round(v.Z, 3));

    /// <summary>Copies the primary object's value (All: position, size and rotation; it then returns the position's text). Returns the
    /// text for the system clipboard, or null when nothing was copied.</summary>
    public string? Copy(ObjectClip what)
    {
        if (Primary is not { } p || Find(p) is not { } o)
            return null;
        if (what == ObjectClip.All && (_selection.Count != 1 || !Access(p).CanPlace || !Access(p).CanReshape))
            return null;   // all three only from one object that may be moved and modified (llpanelobject.cpp menuEnableItem psr_copy)
        var rotation = Rounded(EditNumbers.ToEulerDegrees(o.Rotation));
        switch (what)
        {
            case ObjectClip.Position:
                _clipPosition = Rounded(o.Position);
                return Text(_clipPosition.Value);
            case ObjectClip.Size:
                _clipSize = Rounded(o.Scale);
                return Text(_clipSize.Value);
            case ObjectClip.Rotation:
                _clipRotation = rotation;
                return Text(rotation);
            case ObjectClip.All:
                _clipPosition = Rounded(o.Position);
                _clipSize = Rounded(o.Scale);
                _clipRotation = rotation;
                return Text(_clipPosition.Value);
            case ObjectClip.Parameters:
                // a mesh or sculpt keeps its own (the official copy leaves a mesh out; this viewer does not copy a sculpt's map)
                if (o.Shape is not { } shape || shape.SculptType != 0)
                    return null;
                _clipShape = shape;
                return null;
        }
        return null;
    }

    public bool CanPaste(ObjectClip what) => what switch
    {
        ObjectClip.Position => _clipPosition is not null,
        ObjectClip.Size => _clipSize is not null,
        ObjectClip.Rotation => _clipRotation is not null,
        ObjectClip.All => _clipPosition is not null && _clipSize is not null && _clipRotation is not null && _selection.Count == 1
            && Primary is { } p && Access(p).CanPlace && Access(p).CanReshape,
        ObjectClip.Parameters => _clipShape is not null,
        _ => false,
    };

    /// <summary>Pastes a copied value onto the primary object, as if typed into the Object tab. True when anything was sent.</summary>
    public bool Paste(ObjectClip what)
    {
        if (!CanPaste(what) || Primary is not { } p || Find(p) is not { } o)
            return false;
        switch (what)
        {
            case ObjectClip.Position:
                return PastePosition(o);
            case ObjectClip.Size:
                return PasteSize();
            case ObjectClip.Rotation:
                return SetRotation(_clipRotation!.Value);
            case ObjectClip.All:
                bool a = PastePosition(o), b = PasteSize(), c = SetRotation(_clipRotation!.Value);
                return a | b | c;
            case ObjectClip.Parameters:
                return SetShape(_clipShape!);
        }
        return false;
    }

    private bool PastePosition(ObjectState o)
    {
        var v = _clipPosition!.Value;
        if (o.AttachmentPoint is null && o.ParentLocalId == 0)
        {
            // kept within the region east and west, north and south (the height is held when it is sent)
            float w = _world.Region?.SizeX ?? 256, h = _world.Region?.SizeY ?? 256;
            v = new Vector3(Math.Clamp(v.X, 0, w), Math.Clamp(v.Y, 0, h), v.Z);
        }
        return SetPosition(v);
    }

    private bool PasteSize()
    {
        var lim = _limits();
        var v = _clipSize!.Value;
        return SetSize(new Vector3(Math.Clamp(v.X, lim.MinScale, lim.MaxScale), Math.Clamp(v.Y, lim.MinScale, lim.MaxScale), Math.Clamp(v.Z, lim.MinScale, lim.MaxScale)));
    }

    // ---------------- the Texture tab ----------------

    /// <summary>Copies the selected faces of one object that may be modified (every face of it without Select Face).</summary>
    public bool CopyFaces()
    {
        var targets = FaceTargets();
        if (targets.Count != 1 || !Access(targets[0].Prim.Id.LocalId).CanReshape || targets[0].Faces.Count == 0)
            return false;
        var (o, faces) = targets[0];
        _clipFaces = faces.Select(f => (FaceRules.Settings(o, f), o.Materials?[f].RenderMaterialId ?? Guid.Empty)).ToList();
        return true;
    }

    public bool CanPasteFaces => _clipFaces is { Count: > 0 };

    /// <summary>Pastes the copied faces onto the selected faces of one object: one copied face onto every one, else face by face when
    /// the counts match. True when anything was sent.</summary>
    public bool PasteFaces()
    {
        var targets = FaceTargets();
        if (_clipFaces is not { Count: > 0 } clip || targets.Count != 1 || !Access(targets[0].Prim.Id.LocalId).CanReshape)
            return false;
        var (o, faces) = targets[0];
        if (clip.Count != 1 && clip.Count != faces.Count)
            return false;   // as many faces must be copied as are pasted onto (the official "face count mismatch")
        var pairs = faces.Select((f, i) => (Face: f, From: clip[clip.Count == 1 ? 0 : i])).ToList();
        bool any = Send(new SetObjectFacesIntent(o.Id.LocalId, pairs.Select(x => new FaceEdit(x.Face, x.From.Settings)).ToArray()));
        foreach (var g in pairs.Where(x => x.From.Material != (o.Materials?[x.Face].RenderMaterialId ?? Guid.Empty)).GroupBy(x => x.From.Material))
            any |= Send(new SetFaceMaterialIntent(o.FullId, o.Id.LocalId, g.Select(x => x.Face).ToArray(), g.Key, null));
        return any;
    }
}

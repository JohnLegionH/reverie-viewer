// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Building;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>The Texture tab: Select Face, the face settings, Align planar faces and PBR materials.</summary>
public sealed partial class EditController
{
    /// <summary>"Select Face": a click picks one face instead of the object.</summary>
    public bool SelectFaces { get; set; }

    /// <summary>The selected faces, the latest last.</summary>
    public IReadOnlyList<(uint Local, int Face)> SelectedFaces => Array.Empty<(uint, int)>();

    /// <summary>Applies a Texture tab change to the selected faces.</summary>
    public bool EditFaces(FaceChange change) => false;

    /// <summary>The face the Texture tab shows.</summary>
    public FaceSettings? FaceShown() => null;

    /// <summary>Align planar faces.</summary>
    public bool AlignPlanarFaces() => false;

    public bool ApplyMaterial(Guid materialId) => false;

    public bool RemoveMaterial() => false;

    public bool SetMaterialTransform(TextureTransform transform) => false;
}

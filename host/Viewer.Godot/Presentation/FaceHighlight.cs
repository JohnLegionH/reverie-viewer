// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>One prim with the faces Select Face has chosen on it.</summary>
public readonly record struct HighlightedFaces(OutlinedPrim Prim, IReadOnlyCollection<int> Faces);

/// <summary>The Select Face highlight: a marker drawn over each chosen face only.</summary>
public partial class FaceHighlight : Node3D
{
    /// <summary>How many faces are highlighted now.</summary>
    public int Pieces => 0;

    /// <summary>The chosen faces to highlight (empty clears it).</summary>
    public void Update(IReadOnlyList<HighlightedFaces> faces)
    {
    }
}

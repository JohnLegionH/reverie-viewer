// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Meshing;

/// <summary>
/// The faces of a prim's shape that a Select Face highlight draws: the chosen faces' own triangles with their own texture coordinates, so
/// the marker follows each face's texture layout (the official viewer redraws a selected face's own triangles, read as behaviour from
/// pipeline.cpp renderSelectedFaces and llface.cpp renderSelected).
/// </summary>
public static class SelectedFaceGeometry
{
    /// <summary>The faces of <paramref name="faces"/> whose number is in <paramref name="chosen"/>.</summary>
    public static IReadOnlyList<FaceGeometry> Of(IReadOnlyList<FaceGeometry> faces, IReadOnlyCollection<int> chosen) =>
        chosen.Count == 0 ? [] : faces.Where(f => chosen.Contains(f.Face)).ToList();
}

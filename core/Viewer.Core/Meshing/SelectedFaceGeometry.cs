// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Meshing;

/// <summary>The faces of a prim's shape that a Select Face highlight draws.</summary>
public static class SelectedFaceGeometry
{
    /// <summary>The faces of <paramref name="faces"/> whose number is in <paramref name="chosen"/>.</summary>
    public static IReadOnlyList<FaceGeometry> Of(IReadOnlyList<FaceGeometry> faces, IReadOnlyCollection<int> chosen) => [];
}

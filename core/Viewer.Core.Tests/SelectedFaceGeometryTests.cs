// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Select Face draws its highlight on the chosen faces only (the official viewer redraws each selected face's own triangles with its face
/// texture coordinates, pipeline.cpp renderSelectedFaces and llface.cpp renderSelected, read as behaviour): the faces a highlight takes from a
/// prim's shape are exactly the chosen ones, with their texture coordinates. Pure; the class runs in parallel.
/// </summary>
public class SelectedFaceGeometryTests
{
    private static readonly IReadOnlyList<FaceGeometry> Box = new PickGeometryCache().Faces(null);

    [Fact]
    public void The_highlight_takes_only_the_chosen_faces_with_their_texture_coordinates()
    {
        var chosen = SelectedFaceGeometry.Of(Box, [0, 2]);
        Assert.Equal([0, 2], chosen.Select(f => f.Face).Order());
        Assert.Equal(Box.Where(f => f.Face is 0 or 2).Sum(f => f.Triangles), chosen.Sum(f => f.Triangles));
        Assert.All(chosen, f => Assert.Equal(f.Positions.Length / 3 * 2, f.Uvs.Length));
    }

    [Fact]
    public void No_chosen_face_or_a_face_the_prim_lacks_takes_nothing()
    {
        Assert.Empty(SelectedFaceGeometry.Of(Box, []));
        Assert.Empty(SelectedFaceGeometry.Of(Box, [42]));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A face with no geometry still has a surface of its own, one vertex and three indices that all name it, as the official viewer keeps it
/// (llvolume.cpp 2338-2347), so the surfaces after it keep their numbers. Pure, runs in parallel.</summary>
public class MeshEmptyFaceSurfaceTests
{
    private static readonly MeshFace Empty = new([], [], [], []);
    private static readonly MeshFace Tri = new([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1], [0, 1, 2]);

    [Fact]
    public void An_empty_face_keeps_its_surface_as_one_vertex_and_three_indices()
    {
        var lod = MeshSurfaceData.Prepare([Tri, Empty, Tri]);
        Assert.Equal(3, lod.Surfaces.Count);
        Assert.Equal(1, lod.Surfaces[1].VertexCount);
        Assert.Equal(3, lod.Surfaces[1].IndexCount);
    }

    [Fact]
    public void An_empty_face_of_a_rigged_mesh_encodes_without_a_weight_record()
    {
        var surface = SkinnedSurfaceData.Encode(Empty, new FaceInfluences(0, [], [], 0));
        Assert.Equal(1, surface.VertexCount);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Xunit;
using static Viewer.Core.Tests.SkinAssetBuilder;

namespace Viewer.Core.Tests;

/// <summary>
/// A face with no geometry stays a face. The official viewer's unpack (llvolume.cpp 2338-2347) gives a submesh marked NoGeometry an empty face
/// in its place and goes on, so the faces after it keep their numbers (their texture entries, their weights); a weight list that does not
/// match its face's vertices only logs a warning there (llvolume.cpp 2594-2597) and the piece is drawn. Pure, runs in parallel.
/// </summary>
public class MeshEmptyFaceTests
{
    private static readonly string[] Names = ["mPelvis", "mTorso"];
    private static Skin TwoJoints() => new(Names, [Mat(1), Mat(2)]);
    private static byte[] Weights(int vertices) => Join(Enumerable.Range(0, vertices).Select(_ => Vertex((1, 65535))).ToArray());

    private static MeshGeometry Decode(byte[] asset)
    {
        var r = LmvMesh.Decode(Guid.NewGuid(), asset);
        Assert.True(r.Ok, r.Refusal?.ToString());
        return r.Geometry!;
    }

    [Fact]
    public void An_empty_submesh_at_the_lower_levels_stays_in_its_place()
    {
        var full = new Face[] { new(2, Weights(2)), new(3, Weights(3)), new(4, Weights(4)) };
        var holed = new Face[] { new(2, Weights(2)), new(0, null, NoGeometry: true), new(4, Weights(4)) };
        var g = Decode(Asset(TwoJoints(), (0, holed), (1, holed), (3, full)));
        foreach (int lod in new[] { 0, 1 })
        {
            var faces = g.Lods[lod]!.Faces;
            Assert.Equal(3, faces.Count);
            Assert.Equal([2, 0, 4], faces.Select(f => f.Vertices).ToArray());   // the last face is still the third face
        }
        Assert.Equal(3, g.Lods[3]!.Faces.Count);
    }

    [Fact]
    public void A_rigged_piece_with_an_empty_face_at_a_lower_level_is_drawable_there()
    {
        var holed = new Face[] { new(2, Weights(2)), new(0, null, NoGeometry: true), new(4, Weights(4)) };
        var g = Decode(Asset(TwoJoints(), (0, holed), (1, holed), (3, [new(2, Weights(2)), new(3, Weights(3)), new(4, Weights(4))])));
        Assert.Equal(RiggedWhy.Drawable, RiggedAssessment.Assess(g, 0));
        Assert.Equal(RiggedWhy.Drawable, RiggedAssessment.Assess(g, 1));
        Assert.Equal(RiggedWhy.Drawable, RiggedAssessment.Assess(g, 3));
    }

    [Fact]
    public void A_weight_list_that_does_not_match_its_face_logs_a_warning_and_is_still_drawn()
    {
        // three vertices, two weight records: the official unpack warns "Vertex weight count does not match vertex count!" and goes on
        var g = Decode(Asset(TwoJoints(), (3, [new Face(3, Weights(2)), new Face(2, Weights(2))])));
        Assert.Equal(RiggedWhy.Drawable, RiggedAssessment.Assess(g, 3));
        var inf = g.Skin!.Data!.Lods[3]!;
        Assert.Equal(3, inf[0].Vertices);   // a vertex with no record is bound fully to joint 0
        Assert.Equal(inf[0].Vertices * 4, inf[0].Joints.Length);
        Assert.Contains("weight", g.Skin.Data.LodProblems[3] ?? "");
    }
}

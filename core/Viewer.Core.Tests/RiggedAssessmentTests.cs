// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

public class RiggedAssessmentTests
{
    private static MeshFace Triangle() => new(new float[9], new float[9], new float[6], [0, 1, 2]);

    private static MeshSkinBlock Block(int names = 2, int inverse = 2, bool dropped = false) => new(
        Enumerable.Range(0, names).Select(i => "joint" + i).ToArray(),
        Enumerable.Repeat(Matrix4x4.Identity, inverse).ToArray(),
        Matrix4x4.Identity, [], 0f, false, dropped);

    private static FaceInfluences Influences(int vertices) => new(vertices, new byte[vertices * 4], new float[vertices * 4], 0);

    /// <summary>A mesh with the given LODs (a face of one triangle each) and the given weights per LOD (null: none).</summary>
    private static MeshGeometry Mesh(MeshSkinBlock? block, bool[] lods, int?[] weightVertices, string? blockProblem = null)
    {
        var geometry = lods.Select(has => has ? new MeshLod([Triangle()]) : null).ToArray();
        var influences = weightVertices.Select(v => v is { } n ? (IReadOnlyList<FaceInfluences>)[Influences(n)] : null).ToArray();
        var data = new MeshSkinData(block, blockProblem, influences, new string?[4]);
        return new MeshGeometry(geometry, new MeshSkin(2, true, 3, data));
    }

    [Fact]
    public void A_mesh_with_a_skin_and_weights_for_its_level_is_drawable() =>
        Assert.Equal(RiggedWhy.Drawable, RiggedAssessment.Assess(Mesh(Block(), [true, true, true, true], [3, 3, 3, 3]), 3));

    [Fact]
    public void A_skin_the_reader_could_not_read_is_named()
    {
        var g = new MeshGeometry([null, null, null, new MeshLod([Triangle()])], new MeshSkin(2, true, 3, new MeshSkinData(null, "skin block unreadable", new IReadOnlyList<FaceInfluences>?[4], new string?[4])));
        Assert.Equal(RiggedWhy.SkinNotRead, RiggedAssessment.Assess(g, 3));
        Assert.Equal(RiggedWhy.SkinNotRead, RiggedAssessment.Assess(new MeshGeometry([null, null, null, null], new MeshSkin(2, true, 3)), 3));
    }

    [Fact]
    public void Joint_and_inverse_bind_counts_that_disagree_are_named_before_anything_else() =>
        Assert.Equal(RiggedWhy.BindingsDropped, RiggedAssessment.Assess(Mesh(Block(names: 2, inverse: 1, dropped: true), [true, true, true, true], [3, 3, 3, 3]), 3));

    [Fact]
    public void A_skin_with_no_joint_is_named() =>
        Assert.Equal(RiggedWhy.NoUsableJoints, RiggedAssessment.Assess(Mesh(Block(names: 0, inverse: 0), [true, true, true, true], [3, 3, 3, 3]), 3));

    [Fact]
    public void A_level_without_weights_or_without_geometry_is_named()
    {
        Assert.Equal(RiggedWhy.LevelWithoutWeights, RiggedAssessment.Assess(Mesh(Block(), [true, true, true, true], [3, 3, 3, null]), 3));
        Assert.Equal(RiggedWhy.LevelWithoutWeights, RiggedAssessment.Assess(Mesh(Block(), [true, true, true, false], [3, 3, 3, 3]), 3));
        Assert.Equal(RiggedWhy.LevelWithoutWeights, RiggedAssessment.Assess(Mesh(Block(), [true, true, true, true], [3, 3, 3, 3]), -1));
    }

    [Fact]
    public void Weights_that_do_not_match_the_faces_are_named() =>
        Assert.Equal(RiggedWhy.WeightsDoNotMatchFaces, RiggedAssessment.Assess(Mesh(Block(), [true, true, true, true], [3, 3, 3, 4]), 3));

    [Theory]
    [InlineData(RiggedWhy.WaitingForBody, true)]
    [InlineData(RiggedWhy.TableAtlasFull, true)]
    [InlineData(RiggedWhy.SkinNotRead, false)]
    [InlineData(RiggedWhy.BindingsDropped, false)]
    [InlineData(RiggedWhy.LevelWithoutWeights, false)]
    public void Only_a_wait_for_the_body_or_a_table_passes_by_itself(RiggedWhy why, bool temporary) =>
        Assert.Equal(temporary, RiggedAssessment.IsTemporary(why));

    [Fact]
    public void The_lod_radius_is_the_diagonal_of_the_wearers_box_so_a_taller_wearer_keeps_detail_further_out()
    {
        Assert.Equal(MathF.Sqrt(0.45f * 0.45f + 0.6f * 0.6f + 1.9f * 1.9f), RiggedAssessment.WearerRadius(new Vector3(0.45f, 0.6f, 1.9f)), 0.001f);
        float tall = RiggedAssessment.WearerRadius(new Vector3(0.6f, 0.8f, 2.6f)), standard = RiggedAssessment.WearerRadius(new Vector3(0.45f, 0.6f, 1.9f));
        Assert.True(tall > standard);
        // at 24 m the standard wearer's pieces are at a lower level than the tall wearer's
        Assert.True(MeshLodRule.Detail(24f, tall, 2.0f) > MeshLodRule.Detail(24f, standard, 2.0f));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 1f, 1f)]
    public void An_unknown_box_uses_the_default_radius(float x, float y, float z) =>
        Assert.Equal(RiggedAssessment.DefaultWearerRadius, RiggedAssessment.WearerRadius(new Vector3(x, y, z)));
}

public class RiggedDrawRulesTests
{
    private static MeshFace Triangle() => new(new float[9], new float[9], new float[6], [0, 1, 2]);
    private static MeshLod[] One() => [new MeshLod([Triangle()])];

    [Fact]
    public void A_skin_the_reader_could_not_read_is_not_a_rigged_mesh_so_it_is_drawn_as_a_plain_mesh()
    {
        var unreadable = new MeshGeometry([null, null, null, One()[0]], new MeshSkin(2, true, 3, new MeshSkinData(null, "skin block unreadable", new IReadOnlyList<FaceInfluences>?[4], new string?[4])));
        var noDataAtAll = new MeshGeometry([null, null, null, One()[0]], new MeshSkin(2, true, 3));
        Assert.False(RiggedAssessment.IsRigged(unreadable));
        Assert.False(RiggedAssessment.IsRigged(noDataAtAll));
    }

    [Fact]
    public void A_mesh_with_no_skin_is_not_rigged_and_one_with_a_read_skin_is()
    {
        Assert.False(RiggedAssessment.IsRigged(new MeshGeometry([null, null, null, One()[0]], null)));
        var block = new MeshSkinBlock(["mPelvis"], [Matrix4x4.Identity], Matrix4x4.Identity, [], 0f, false, false);
        var data = new MeshSkinData(block, null, new IReadOnlyList<FaceInfluences>?[4], new string?[4]);
        Assert.True(RiggedAssessment.IsRigged(new MeshGeometry([null, null, null, One()[0]], new MeshSkin(1, true, 3, data))));
    }

    [Fact]
    public void A_worn_piece_that_is_not_rigged_takes_its_distance_from_the_wearers_box_not_from_itself()
    {
        // a shoe at the feet of a standing avatar, the camera 3 m in front of it at chest height: the official viewer measures to the avatar's box (2.78 m),
        // not to the shoe (3.2 m)
        var centre = new Vector3(100f, 100f, 21f);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        var shoe = centre + new Vector3(0f, 0f, -0.85f);
        var camera = centre + new Vector3(3f, 0f, 0.2f);
        Assert.Equal(2.78f, RiggedAssessment.WornDistance(camera, shoe, centre, size), 0.011f);
        // the wearer's box not known yet: the piece's own distance
        Assert.Equal(Vector3.Distance(camera, shoe), RiggedAssessment.WornDistance(camera, shoe, null, size), 0.0001f);
    }

    [Fact]
    public void The_distance_of_a_rigged_mesh_is_the_camera_to_the_wearers_box_and_never_less_than_a_centimetre()
    {
        var centre = new Vector3(100f, 100f, 21f);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        // 10 m in front of the box's face along x: 10 - 0.225
        Assert.Equal(9.775f, RiggedAssessment.BoxDistance(centre + new Vector3(10f, 0f, 0f), centre, size), 0.011f);
        // inside the box: the floor
        Assert.Equal(0.01f, RiggedAssessment.BoxDistance(centre + new Vector3(0.1f, 0.1f, 0.2f), centre, size), 0.0001f);
        // off a corner: the length of the offset from the nearest corner
        float corner = RiggedAssessment.BoxDistance(centre + new Vector3(3.225f, 4.3f, 0f), centre, size);
        Assert.Equal(5f, corner, 0.011f);
    }

    [Fact]
    public void The_default_volume_detail_factor_is_the_official_highultra_graphics_level() =>
        Assert.Equal(1.75f, MeshLodRule.DefaultLodFactor);

    [Fact]
    public void A_one_metre_object_drops_a_level_at_the_distances_the_official_highultra_level_gives()
    {
        // radius of a 1 m cube is |(0.5, 0.5, 0.5)| = 0.866; factor 1.75 (official: tan = factor * radius / (distance * (1 - 0.1 * factor) * pi / 3))
        float radius = MeshLodRule.Radius(new Vector3(1f, 1f, 1f));
        Assert.Equal(3, MeshLodRule.Detail(5f, radius));
        Assert.Equal(2, MeshLodRule.Detail(10f, radius));
        Assert.Equal(1, MeshLodRule.Detail(30f, radius));
        Assert.Equal(0, MeshLodRule.Detail(60f, radius));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Xunit;
using static Viewer.Core.Tests.SkinAssetBuilder;

namespace Viewer.Core.Tests;

/// <summary>
/// The rigged-mesh skin reader: the whole skin block, and per-LOD vertex influences. Expected values come from the SL mesh
/// format (SL wiki "Mesh") for the layout, and from the official viewer's behaviour (llmodel.cpp fromLLSD, llvolume.cpp
/// unpackVolumeFaces, llskinningutil.cpp) for the clamps and the normalisation; each test says which. Pure, runs in parallel.
/// </summary>
public class MeshSkinReaderTests
{
    private static readonly string[] Names = ["mPelvis", "mTorso", "mChest", "mNeck"];
    private static Matrix4x4[] Inverse(int n) => Enumerable.Range(0, n).Select(i => Mat(i * 20)).ToArray();

    private static Skin FullSkin() => new(Names, Inverse(4), BindShape: Mat(500), Alt: Inverse(4).Select(m => m with { M41 = m.M41 + 1000 }).ToArray(), Pelvis: -0.125f, Lock: true);

    // ---- the skin block ----

    [Fact]
    public void The_skin_block_is_read_in_full()
    {
        var r = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(1, Vertex((1, 65535)))])));
        Assert.True(r.Rigged, r.BlockProblem);
        var b = r.Block!;
        Assert.Equal(Names, b.JointNames);
        Assert.Equal(Inverse(4), b.InverseBind);
        Assert.Equal(Mat(500), b.BindShape);
        Assert.Equal(4, b.AlternateInverseBind.Count);
        Assert.Equal(1000f + Mat(0).M41, b.AlternateInverseBind[0].M41);
        Assert.Equal(-0.125f, b.PelvisOffset);
        Assert.True(b.LockScaleIfJointPosition);
        Assert.False(b.BindingsDropped);
        Assert.True(b.HasJointOverrides);
        Assert.Equal(4, b.UsableJoints);
    }

    [Fact]
    public void Matrix_entries_keep_the_wires_row_major_order()
    {
        // 16 numbers a row at a time: entry [3*4+0..2] is the translation (the fourth row), as in the official viewer's mat[j][k] = list[j*4+k].
        var m = new Matrix4x4(1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 1);
        var r = MeshSkinReader.Read(Asset(new Skin(["mPelvis"], [m]), (3, [new Face(1, Vertex((0, 65535)))])));
        Assert.Equal(new Vector3(10, 11, 12), r.Block!.InverseBind[0].Translation);
        Assert.Equal(2f, r.Block.InverseBind[0].M12);
        Assert.Equal(4f, r.Block.InverseBind[0].M21);
    }

    [Fact]
    public void Absent_optional_entries_take_their_defaults()
    {
        var r = MeshSkinReader.Read(Asset(new Skin(Names, Inverse(4)), (3, [new Face(1, Vertex((0, 65535)))])));
        var b = r.Block!;
        Assert.Equal(Matrix4x4.Identity, b.BindShape);
        Assert.Empty(b.AlternateInverseBind);
        Assert.Equal(0f, b.PelvisOffset);
        Assert.False(b.LockScaleIfJointPosition);
        Assert.False(b.HasJointOverrides);
    }

    [Fact]
    public void A_count_mismatch_between_joints_and_inverse_binds_drops_the_bindings()
    {
        // llmodel.cpp fromLLSD: "Joints vs bind matrix count mismatch. Dropping joint bindings."
        var r = MeshSkinReader.Read(Asset(new Skin(Names, Inverse(3)), (3, [new Face(1, Vertex((0, 65535)))])));
        Assert.True(r.Block!.BindingsDropped);
        Assert.Empty(r.Block.JointNames);
        Assert.Empty(r.Block.InverseBind);
    }

    [Fact]
    public void Alternate_matrices_that_do_not_match_the_joint_list_give_no_overrides()
    {
        // llvoavatar.cpp addAttachmentOverridesForObject: "joint overrides will be ignored" when bindCnt != jointCnt.
        var r = MeshSkinReader.Read(Asset(new Skin(Names, Inverse(4), Alt: Inverse(2)), (3, [new Face(1, Vertex((0, 65535)))])));
        Assert.Equal(2, r.Block!.AlternateInverseBind.Count);
        Assert.False(r.Block.HasJointOverrides);
    }

    [Fact]
    public void More_than_110_joints_are_listed_but_only_110_are_usable()
    {
        var names = Enumerable.Range(0, 130).Select(i => "j" + i).ToArray();
        var r = MeshSkinReader.Read(Asset(new Skin(names, Inverse(130)), (3, [new Face(1, Vertex((120, 65535)))])));
        Assert.Equal(130, r.Block!.JointNames.Count);
        Assert.Equal(110, r.Block.UsableJoints);
        Assert.Equal(109, r.Lods[3]![0].Joints[0]);   // a joint past the usable ones is clamped to the last usable
    }

    [Fact]
    public void A_mesh_without_a_skin_block_is_not_rigged()
    {
        var r = MeshSkinReader.Read(Asset(null, (3, [new Face(2, null)])));
        Assert.False(r.Rigged);
        Assert.Null(r.BlockProblem);
        Assert.Null(r.Lods[3]);
    }

    // ---- the influences ----

    [Fact]
    public void Influences_follow_the_wire_layout_and_the_official_clamps()
    {
        var weights = Join(
            Vertex((1, 65535)),                                                   // one influence: clamped to 0.999, normalised to 1
            Vertex((1, 16384), (2, 49151)),                                       // two: 0.25 and 0.75 (sum 1)
            Vertex((0, 10000), (1, 10000), (2, 10000), (3, 10000)),               // four, no terminator
            Vertex((3, 0), (2, 65535)));                                          // a zero weight is lifted to 0.001
        var r = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(4, weights)])));
        var f = r.Lods[3]![0];
        Assert.Equal(4, f.Vertices);
        Assert.Equal(0, f.Repaired);
        Assert.Equal([1, 0, 0, 0], f.Joints[..4]);
        Assert.Equal(1f, f.Weights[0], 5);
        Assert.Equal([1, 2, 0, 0], f.Joints[4..8]);
        Assert.Equal(16384 / 65535f, f.Weights[4], 4);
        Assert.Equal(49151 / 65535f, f.Weights[5], 4);
        Assert.Equal([0, 1, 2, 3], f.Joints[8..12]);
        Assert.All(f.Weights[8..12], w => Assert.Equal(0.25f, w, 4));
        // vertex 3: weights 0.001 (joint 3) and 0.999 (joint 2), already summing to 1
        Assert.Equal([3, 2, 0, 0], f.Joints[12..16]);
        Assert.Equal(0.001f, f.Weights[12], 5);
        Assert.Equal(0.999f, f.Weights[13], 5);
    }

    [Fact]
    public void Every_vertexs_weights_add_up_to_one()
    {
        var rng = new Random(11);
        var bytes = new List<byte>();
        for (int v = 0; v < 500; v++)
            bytes.AddRange(Vertex(Enumerable.Range(0, rng.Next(1, 5)).Select(_ => ((byte)rng.Next(0, 4), (ushort)rng.Next(0, 65536))).ToArray()));
        var f = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(500, bytes.ToArray())]))).Lods[3]![0];
        for (int v = 0; v < 500; v++)
            Assert.Equal(1f, f.Weights[(v * 4)..(v * 4 + 4)].Sum(), 4);
    }

    [Fact]
    public void A_vertex_with_no_influence_is_bound_fully_to_joint_zero()
    {
        // llvolume.cpp: wsum <= 0 gives (0.999, 0, 0, 0) on joint 0.
        var f = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(2, Join(new byte[] { 0xFF }, Vertex((2, 65535))))]))).Lods[3]![0];
        Assert.Equal(1, f.Repaired);
        Assert.Equal([0, 0, 0, 0], f.Joints[..4]);
        Assert.Equal(1f, f.Weights[0], 5);
        Assert.Equal(0f, f.Weights[1]);
        Assert.Equal(2, f.Joints[4]);
    }

    [Fact]
    public void A_joint_index_beyond_the_list_is_clamped_to_the_last_joint()
    {
        // llskinningutil.cpp scrubSkinWeights: i = llclamp(i, 0, max_joints - 1).
        var f = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(1, Vertex((200, 65535)))]))).Lods[3]![0];
        Assert.Equal(3, f.Joints[0]);
        Assert.Equal(1, f.Repaired);
    }

    [Fact]
    public void Missing_or_short_weight_data_never_leaves_a_vertex_unbound()
    {
        var f = MeshSkinReader.Read(Asset(FullSkin(), (3, [new Face(5, Vertex((1, 65535)))]))).Lods[3]![0];   // data for one vertex, five wanted
        Assert.Equal(5, f.Vertices);
        for (int v = 0; v < 5; v++) Assert.Equal(1f, f.Weights[(v * 4)..(v * 4 + 4)].Sum(), 4);
        Assert.Equal(4, f.Repaired);
    }

    [Fact]
    public void Faces_without_geometry_have_no_vertices_and_each_lod_is_read_on_its_own()
    {
        var r = MeshSkinReader.Read(Asset(FullSkin(),
            (1, [new Face(3, Join(Vertex((1, 65535)), Vertex((2, 65535)), Vertex((3, 65535))))]),
            (3, [new Face(0, null, NoGeometry: true), new Face(2, Join(Vertex((0, 65535)), Vertex((1, 65535))))])));
        Assert.Null(r.Lods[0]);
        Assert.Equal(3, r.Lods[1]![0].Vertices);
        Assert.Null(r.Lods[2]);
        Assert.Equal(0, r.Lods[3]![0].Vertices);
        Assert.Equal(2, r.Lods[3]![1].Vertices);
    }

    // ---- hostile and broken assets ----

    [Fact]
    public void A_non_finite_matrix_refuses_the_skin_block_and_nothing_else()
    {
        var bad = Inverse(4);
        bad[2].M32 = float.NaN;
        var r = MeshSkinReader.Read(Asset(new Skin(Names, bad), (3, [new Face(1, Vertex((0, 65535)))])));
        Assert.False(r.Rigged);
        Assert.Contains("finite", r.BlockProblem);
        bad = Inverse(4);
        bad[0].M11 = float.PositiveInfinity;
        Assert.False(MeshSkinReader.Read(Asset(new Skin(Names, Inverse(4), BindShape: bad[0]), (3, [new Face(1, Vertex((0, 65535)))]))).Rigged);
        Assert.False(MeshSkinReader.Read(Asset(new Skin(Names, Inverse(4), Pelvis: float.NaN), (3, [new Face(1, Vertex((0, 65535)))]))).Rigged);
    }

    [Fact]
    public void Too_many_joints_or_a_short_matrix_refuse_the_block()
    {
        var many = Enumerable.Range(0, MeshSkinReader.MaxListedJoints + 1).Select(i => "j" + i).ToArray();
        var r = MeshSkinReader.Read(Asset(new Skin(many, Inverse(many.Length)), (3, [new Face(1, Vertex((0, 65535)))])));
        Assert.False(r.Rigged);
        Assert.Contains("joint names", r.BlockProblem);
    }

    [Fact]
    public void Too_many_faces_or_vertices_refuse_that_lod_only()
    {
        var limits = MeshLimits.Default with { MaxVerticesPerFace = 4 };
        var r = MeshSkinReader.Read(Asset(FullSkin(), (2, [new Face(9, Vertex((0, 65535)))]), (3, [new Face(2, Join(Vertex((0, 65535)), Vertex((1, 65535))))])), limits);
        Assert.True(r.Rigged);
        Assert.Null(r.Lods[2]);
        Assert.Contains("vertices", r.LodProblems[2]);
        Assert.NotNull(r.Lods[3]);
    }

    [Fact]
    public void Every_truncation_and_many_byte_flips_are_refused_or_read_without_throwing()
    {
        var weights = Join(Vertex((1, 16384), (2, 49151)), Vertex((0, 65535)), Vertex((0, 1), (1, 2), (2, 3), (3, 4)));
        var good = Asset(FullSkin(), (3, [new Face(3, weights)]));
        Assert.True(MeshSkinReader.Read(good).Rigged);
        for (int len = 0; len < good.Length; len++)
            MeshSkinReader.Read(good[..len]);   // must not throw
        var rng = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            var m = (byte[])good.Clone();
            for (int k = rng.Next(1, 4); k > 0; k--) m[rng.Next(m.Length)] = (byte)rng.Next(256);
            var r = MeshSkinReader.Read(m);
            if (r.Block is { } b)
            {
                Assert.True(b.InverseBind.Count == b.JointNames.Count);
                Assert.True(float.IsFinite(b.PelvisOffset));
            }
            foreach (var lod in r.Lods.Where(l => l is not null))
                foreach (var f in lod!)
                {
                    Assert.Equal(f.Vertices * 4, f.Weights.Length);
                    Assert.All(f.Weights, w => Assert.True(float.IsFinite(w) && w >= 0f));
                }
        }
    }

    [Fact]
    public void A_declared_count_larger_than_the_data_is_refused_without_allocating_it()
    {
        // A header claiming a list of 2^30 entries: the guard refuses it before anything is allocated.
        var evil = new byte[] { (byte)'{', 0, 0, 0, 1, (byte)'k', 0, 0, 0, 4, (byte)'s', (byte)'k', (byte)'i', (byte)'n', (byte)'[', 0x40, 0, 0, 0 };
        var r = MeshSkinReader.Read(evil);
        Assert.False(r.Rigged);
        Assert.NotNull(r.BlockProblem);
    }

    [Fact]
    public void The_reader_agrees_with_the_decoder_the_viewer_ships_on_names_matrices_and_weights()
    {
        // Cross-check against LibreMetaverse's own decoder (reached only through the adapter), on an independently written asset.
        var weights = Join(Vertex((1, 16384), (2, 49151)), Vertex((3, 65535)));
        var asset = Asset(FullSkin(), (3, [new Face(2, weights)]));
        var mine = MeshSkinReader.Read(asset);
        var theirs = Viewer.Core.Protocol.LmvMesh.Decode(Guid.NewGuid(), asset);
        Assert.True(theirs.Ok, theirs.Refusal?.ToString());
        Assert.Equal(Names.Length, theirs.Geometry!.Skin!.JointCount);
        Assert.True(theirs.Geometry.Skin.HasBindShapeMatrix);
        Assert.Equal(2, theirs.Geometry.Skin.WeightedVertices);
        Assert.Equal(Names.Length, mine.Block!.JointNames.Count);
        Assert.Equal(2, mine.Lods[3]![0].Vertices);
    }
}

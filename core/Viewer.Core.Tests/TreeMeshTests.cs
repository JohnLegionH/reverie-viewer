// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official viewer's tree: one mesh per tree (a trunk cylinder at each branch, four leaf quads at each branch tip), the trunk drawn with
/// 10, 5, 4 or 3 slices by distance, the branches drooping by the species' droop plus 25 degrees. The expected numbers are counted here by a
/// separate recursion over the species table, not by the code under test.
/// </summary>
public class TreeMeshTests
{
    // species table rows used here: depth, trunk depth, branches
    private static (int Depth, int TrunkDepth, float Branches) Row(int species) => species switch
    {
        0 => (1, 6, 5f), 1 => (3, 0, 4f), 3 => (1, 0, 3f), 7 => (4, 0, 3f), 14 => (4, 0, 3.6f), 15 => (1, 0, 7f), _ => throw new ArgumentOutOfRangeException(nameof(species)),
    };

    // the official recursion: a segment while depth > 0 (children: one per branch at depth - 1, and the trunk's own continuation), else a leaf
    private static (int Segments, int Leaves) Count(int depth, int trunkDepth, int branches)
    {
        if (depth <= 0) return (0, 1);
        int s = 1, l = 0;
        for (int i = 0; i < branches; i++) { var c = Count(depth - 1, 0, branches); s += c.Segments; l += c.Leaves; }
        if (trunkDepth > 0) { var c = Count(depth, trunkDepth - 1, branches); s += c.Segments; l += c.Leaves; }
        return (s, l);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 3)] [InlineData(1, 1)] [InlineData(3, 2)] [InlineData(7, 0)] [InlineData(14, 3)] [InlineData(15, 2)]
    public void A_trees_mesh_has_the_official_vertex_and_index_counts_for_each_trunk_detail(int species, int trunkLod)
    {
        var (depth, trunk, branches) = Row(species);
        var (segments, leaves) = Count(depth, trunk, (int)branches);
        int slices = TreeMesh.Slices[trunkLod];
        var raw = TreeMesh.RawGeometry(species, 0.4f);
        var lod = raw.Lods[3 - trunkLod];
        Assert.NotNull(lod);
        var face = Assert.Single(lod!.Faces);
        Assert.Equal(segments * slices * slices + leaves * 16, face.Vertices);
        Assert.Equal(segments * (slices - 1) * (slices - 1) * 6 + leaves * 24, face.Indices.Length);
    }

    [Fact]
    public void The_layout_has_the_counted_segments_and_leaves()
    {
        foreach (int sp in new[] { 0, 1, 7, 14 })
        {
            var (depth, trunk, branches) = Row(sp);
            var want = Count(depth, trunk, (int)branches);
            var (segments, leaves) = TreeMesh.Layout(sp, 0.4f);
            Assert.Equal(want, (segments.Count, leaves.Count));
        }
    }

    [Fact]
    public void Leaves_use_the_leaf_part_of_the_texture_and_the_trunk_the_bark_part()
    {
        var lod3 = TreeMesh.RawGeometry(1, 0.4f).Lods[3];
        Assert.NotNull(lod3);
        var face = lod3!.Faces[0];
        var (segments, leaves) = TreeMesh.Layout(1, 0.4f);
        int inLeafArea = 0;
        for (int i = 0; i < face.Vertices; i++)
        {
            float u = face.Uvs[i * 2], v = face.Uvs[i * 2 + 1];
            if (u >= 0.52f - 1e-4f) { inLeafArea++; Assert.InRange(u, 0.52f - 1e-4f, 0.98f + 1e-4f); Assert.InRange(v, 0.52f - 1e-4f, 1.0f + 1e-4f); }
            else Assert.InRange(u, 0f, 0.5f);
        }
        Assert.Equal(leaves.Count * 16, inLeafArea);
        Assert.True(segments.Count > 0);
    }

    [Theory]
    [InlineData(3, 25f)]   // Palm 1: droop 0 + 25
    [InlineData(0, 85f)]   // Pine 1: droop 60 + 25
    public void Branches_lean_out_by_the_species_droop_plus_25_degrees(int species, float tilt)
    {
        var (_, leaves) = TreeMesh.Layout(species, 0.4f);   // depth 1: the first leaf sits on the first branch off the trunk
        Assert.True(leaves.Count > 0, "the tree has branches");
        var first = Vector3.Transform(Vector3.UnitZ, leaves[0].Rotation);
        float firstTilt = MathF.Acos(Math.Clamp(first.Z, -1f, 1f)) * 180f / MathF.PI;
        Assert.InRange(firstTilt, tilt - 0.5f, tilt + 0.5f);
    }

    [Fact]
    public void The_trunks_base_radius_is_0_65_of_its_width_for_a_species_without_noise()
    {
        // Eucalyptus: no noise. Segment 0 is the trunk: width = length * aspect; the ring at z = 0 (one row above the base cap) has radius 0.65 * width.
        float radius = 0.4f;
        var (segments, _) = TreeMesh.Layout(14, radius);
        Assert.True(segments.Count > 0, "the tree has a trunk");
        float width = segments[0].Width;
        var lod3 = TreeMesh.RawGeometry(14, radius).Lods[3];
        Assert.NotNull(lod3);
        var face = lod3!.Faces[0];
        int rings = 0;
        for (int i = 0; i < 10 * 10; i++)
        {
            float x = face.Positions[i * 3], y = face.Positions[i * 3 + 1], z = face.Positions[i * 3 + 2];
            float r = 0.65f * (1f + 1.2f * 0.66f) * width;   // the ring at z = 0: base radius times the official start-radius factor, no noise
            if (MathF.Abs(z - (-0.1f)) < 1e-4f) { rings++; Assert.InRange(MathF.Sqrt(x * x + y * y), r * 0.999f, r * 1.001f); }
        }
        Assert.Equal(10, rings);
    }

    // the official trunk detail (llvotree.cpp idleUpdate): the apparent angle (degrees) of the largest scale at the distance less half the smallest
    // scale, times the tree factor, against 30, 20, 15 degrees; then the larger of that and the volume detail inverted
    private static int ExpectedTrunkLod(double scaleMax, double scaleMin, double diameter, double billboard, double ratio, double dist, double volumeFactor, double treeFactor)
    {
        double range = dist - scaleMin / 2;
        double angle = Math.Atan2(scaleMax, range) * 180 / Math.PI * treeFactor;
        int lod = angle > 30 ? 0 : angle > 20 ? 1 : angle > 15 ? 2 : 3;
        double distance = dist * (1 - volumeFactor * 0.1) * (Math.PI / 3);
        double tan = Math.Round(treeFactor * 64 * billboard * ratio * diameter / distance, 2);
        int detail = tan <= 0.03 ? 0 : tan <= 0.06 ? 1 : tan <= 0.24 ? 2 : 3;
        return Math.Min(Math.Max(lod, 3 - detail), 4);
    }

    [Theory]
    [InlineData(5f, 0)] [InlineData(8f, 0)] [InlineData(11f, 1)] [InlineData(15f, 2)] [InlineData(40f, 3)] [InlineData(300f, 3)]
    public void The_trunk_detail_follows_the_official_rule_by_distance(float dist, int want)
    {
        var scale = new Vector3(8, 8, 8);
        Assert.Equal(want, ExpectedTrunkLod(8, 8, scale.Length(), 10.25, 1.0, dist, 2.0, 0.5));
        Assert.Equal(want, TreeLod.TrunkLod(1, scale, dist));
    }

    [Fact]
    public void A_small_tree_far_away_takes_the_coarsest_trunk_and_detail_maps_to_the_meshs_lods()
    {
        Assert.Equal(3, TreeLod.TrunkLod(1, new Vector3(0.01f, 0.01f, 0.01f), 100f));
        Assert.Equal(3, TreeLod.MeshDetail(0));
        Assert.Equal(0, TreeLod.MeshDetail(3));
    }

    [Fact]
    public void A_trees_mesh_id_is_the_same_for_the_same_species_and_scale_and_its_geometry_is_there()
    {
        var a = TreeMesh.IdFor(1, 13.856f);
        Assert.NotEqual(Guid.Empty, a);
        Assert.Equal(a, TreeMesh.IdFor(1, 13.8561f));
        Assert.NotEqual(a, TreeMesh.IdFor(2, 13.856f));
        Assert.NotEqual(a, TreeMesh.IdFor(1, 20f));
        Assert.True(TreeMesh.TryGet(a, out var g, out _, out var size));
        Assert.NotNull(g);
        Assert.All(g!.Lods, l => Assert.NotNull(l));
        Assert.True(size.X > 0 && size.Y > 0 && size.Z > 0);
        // the geometry is stored inside the unit box around its centre
        foreach (var f in g.Lods[3]!.Faces)
            foreach (var p in f.Positions) Assert.InRange(p, -0.5001f, 0.5001f);
    }
}

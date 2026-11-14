// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llvotree.cpp
 * @brief LLVOTree class implementation
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */
// Adapted 2026 by Legion Builds. TreeMesh follows LLVOTree::updateGeometry (the leaf quads and the trunk cylinders at four detail levels),
// LLVOTree::updateMesh and genBranchPipeline (where the segments and leaves go, the droop of the branches) and TreeLod follows
// LLVOTree::idleUpdate (the trunk detail by apparent angle and by volume detail), written in C# for one tree at a time. The species numbers
// are those of trees.xml. The trunk noise is TreeNoise (its own file, the official noise routines).
// Copyright (c) 2026 Legion Builds
using System.Collections.Concurrent;
using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.World;

/// <summary>One trunk or branch cylinder of a tree: where it starts, which way it points, its length and its width (the base radius is 0.65 of the width).</summary>
public readonly record struct TreeSegment(Vector3 Position, Quaternion Rotation, float Length, float Width);

/// <summary>One leaf of a tree: where it starts, which way it points and its size (it is that tall and that wide).</summary>
public readonly record struct TreeLeaf(Vector3 Position, Quaternion Rotation, float Size);

/// <summary>
/// The official viewer's tree as one mesh: every trunk and branch is a cylinder (a ring grid of slices x slices vertices, the first and last
/// ring pulled to a point as caps), every branch tip carries four leaf quads (two crossed upright quads, each drawn from both sides). The trunk
/// is drawn with 10, 5, 4 or 3 slices by distance; the mesh holds all four so the prim layer's detail choice picks among them. The branches lean
/// out by the species' droop plus 25 degrees (the official viewer adds 25 degrees less the trunk's wind bend, which is none here). Each leaf
/// reads the leaf area of the species texture (right part of the upper half), each trunk the bark on the left.
/// </summary>
public static class TreeMesh
{
    /// <summary>Slices per ring for trunk detail 0 (best) to 3.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<int> Slices = [10, 5, 4, 3];

    /// <summary>The one material every tree face wears: the texture's alpha cuts the leaves out at one half (the official tree pass sets a minimum alpha of 0.5: lldrawpooltree.cpp) and what is left is drawn solid, no blending. The cutoff is a byte, so 128 of 255.</summary>
    public static readonly Guid LeafMaterialId = new("7ee00000-0000-7e11-6c65-61666d617401");
    public static readonly LegacyMaterial LeafMaterial = new(LeafMaterialId, default, default, default, 0, 0, LegacyAlphaMode.Mask, 128);   // specular colour and exponent zero: no specular

    private const float LeafLeft = 0.52f, LeafRight = 0.98f, LeafTop = 1.0f, LeafBottom = 0.52f, LeafWidth = 1f;
    private const float BaseRadius = 0.65f, CapNudge = 0.1f, Unit = 0.05f, Plant = 0.1f;
    private const int FractalDepth = 5;

    // trees.xml: noise magnitude and noise scale per species (the rest of the row is in TreeBuilder's table)
    private static readonly System.Collections.Immutable.ImmutableArray<(float Mag, float Scale)> Noise =
    [
        (0.5f, 2.5f), (1.2f, 4f), (1f, 1f), (0.2f, 6f), (1.5f, 2f), (1f, 1f), (0.2f, 6f), (1.2f, 1f), (1.2f, 1f), (0.5f, 2.5f), (0f, 0f),
        (0f, 2.5f), (0.75f, 2.5f), (0.75f, 2.5f), (0f, 0f), (1f, 1f), (1f, 1f), (0.5f, 0f), (1f, 2f), (0.5f, 0f), (1f, 2f),
    ];

    /// <summary>The segments and leaves of a species' tree for the scale radius <paramref name="radius"/> (the length of the object's scale times 0.05), in the tree's own frame.</summary>
    public static (List<TreeSegment> Segments, List<TreeLeaf> Leaves) Layout(int species, float radius)
    {
        var sp = TreeBuilder.SpeciesOf(species);
        var segments = new List<TreeSegment>();
        var leaves = new List<TreeLeaf>();
        Grow(sp, radius, Vector3.Zero, Quaternion.Identity, 1f, sp.TrunkDepth, sp.Depth, segments, leaves);
        return (segments, leaves);
    }

    private static void Grow(TreeSpecies sp, float radius, Vector3 pos, Quaternion rot, float scale, int trunkDepth, int depth, List<TreeSegment> segments, List<TreeLeaf> leaves)
    {
        if (depth <= 0)
        {
            leaves.Add(new TreeLeaf(pos, rot, scale * sp.LeafScale * radius));
            return;
        }
        bool trunk = trunkDepth > 0 || scale == 1f;
        float length = (trunk ? sp.TrunkLength : sp.BranchLength) * scale * radius;
        float width = length * (trunk ? sp.TrunkAspect : sp.BranchAspect);
        segments.Add(new TreeSegment(pos, rot, length, width));
        var end = pos + Vector3.Transform(new Vector3(0, 0, length), rot);
        int branches = (int)sp.Branches;
        float constantTwist = 360f / sp.Branches;
        float droop = sp.Droop + 25f;
        for (int i = 0; i < branches; i++)
        {
            float twist = (constantTwist + (i % 2 == 0 ? sp.Twist : -sp.Twist)) * i;
            // the official product applies its first factor first (llquaternion.cpp operator*); System.Numerics applies the last first,
            // so the factors are written in the opposite order: 20 degrees about the axis, then the droop, then the turn round the axis
            var turn = Rad(twist, Vector3.UnitZ) * Rad(droop, Vector3.UnitY) * Rad(20f, Vector3.UnitZ);
            Grow(sp, radius, end, Quaternion.Normalize(rot * turn), scale * sp.ScaleStep, 0, depth - 1, segments, leaves);
        }
        if (trunkDepth > 0)
            Grow(sp, radius, end, Quaternion.Normalize(rot * Rad(70.5f, Vector3.UnitZ)), scale * sp.ScaleStep, trunkDepth - 1, depth, segments, leaves);
    }

    private static Quaternion Rad(float degrees, Vector3 axis) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);

    /// <summary>
    /// The tree's mesh in its own frame (metres, base at the origin less 0.1 m so it stands in the ground, turned 90 degrees about the vertical as
    /// the official viewer does), one face, four detail levels: index d holds trunk detail 3 - d (index 3 is the best).
    /// </summary>
    public static MeshGeometry RawGeometry(int species, float radius)
    {
        var sp = TreeBuilder.SpeciesOf(species);
        var noise = Noise[Math.Clamp(species < 0 || species >= Noise.Length ? 0 : species, 0, Noise.Length - 1)];
        var (segments, leaves) = Layout(species, radius);
        var lods = new MeshLod?[MeshGeometry.LodCount];
        for (int d = 0; d < MeshGeometry.LodCount; d++)
        {
            int slices = Slices[3 - d];
            var pos = new List<float>(); var nor = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
            var unit = UnitTrunk(sp, slices, noise.Mag, noise.Scale);
            foreach (var s in segments)
            {
                int start = pos.Count / 3;
                foreach (var (p, n, t) in unit.Vertices)
                {
                    var world = s.Position + Vector3.Transform(new Vector3(p.X * s.Width, p.Y * s.Width, p.Z * s.Length), s.Rotation);
                    var normal = Vector3.Normalize(Vector3.Transform(n, s.Rotation));
                    Add(pos, nor, uv, world, normal, t);
                }
                foreach (int i in unit.Indices) idx.Add(start + i);
            }
            foreach (var leaf in leaves)
            {
                int start = pos.Count / 3;
                foreach (var (p, n, t) in LeafVertices)
                {
                    var world = leaf.Position + Vector3.Transform(p * leaf.Size, leaf.Rotation);
                    Add(pos, nor, uv, world, Vector3.Normalize(Vector3.Transform(n, leaf.Rotation)), t);
                }
                foreach (int i in LeafIndices) idx.Add(start + i);
            }
            // the whole tree is turned 90 degrees about the vertical and planted 0.1 m low
            for (int i = 0; i < pos.Count; i += 3)
            {
                (pos[i], pos[i + 1]) = (-pos[i + 1], pos[i]);
                pos[i + 2] -= Plant;
                (nor[i], nor[i + 1]) = (-nor[i + 1], nor[i]);
            }
            lods[d] = new MeshLod([new MeshFace([.. pos], [.. nor], [.. uv], [.. idx])]);
        }
        return new MeshGeometry(lods, null);
    }

    private static void Add(List<float> pos, List<float> nor, List<float> uv, Vector3 p, Vector3 n, Vector2 t)
    {
        pos.Add(p.X); pos.Add(p.Y); pos.Add(p.Z);
        nor.Add(n.X); nor.Add(n.Y); nor.Add(n.Z);
        uv.Add(t.X); uv.Add(t.Y);
    }

    // a unit trunk of the given slices: width 1, height 1, base radius 0.65 tapering to 0.65 * taper, the rings' radius moved by the noise
    private static (List<(Vector3 P, Vector3 N, Vector2 T)> Vertices, List<int> Indices) UnitTrunk(TreeSpecies sp, int slices, float noiseMag, float noiseScale)
    {
        float topRadius = BaseRadius * sp.Taper;
        float angleInc = 360f / (slices - 1);
        float zInc = slices > 3 ? 1f / (slices - 3) : 1f;
        var verts = new List<(Vector3, Vector3, Vector2)>(slices * slices);
        for (int i = 0; i < slices; i++)
        {
            float z, r0;
            if (i == 0) { z = -CapNudge; r0 = 0f; }
            else if (i == slices - 1) { z = 1f + CapNudge; r0 = 0f; }
            else { z = (i - 1) * zInc; r0 = BaseRadius + (topRadius - BaseRadius) * z; }
            for (int j = 0; j < slices; j++)
            {
                float angle = j == slices - 1 ? 0f : j * angleInc;
                float x1 = MathF.Cos(angle * MathF.PI / 180f), y1 = MathF.Sin(angle * MathF.PI / 180f);
                float start = r0 * (1f + 1.2f * MathF.Abs(z - 0.66f));
                var nvec = new Vector3(x1 * start * noiseScale, y1 * start * noiseScale, z * 4f * noiseScale);
                float rad = start + TreeNoise.Turbulence3(nvec, FractalDepth) * noiseMag;
                var tc = j == slices - 1 ? new Vector2(0.490f, (1f - z / 2f) * sp.RepeatZ) : new Vector2(angle / 360f * 0.5f, (1f - z / 2f) * sp.RepeatZ);
                verts.Add((new Vector3(x1 * rad, y1 * rad, z), new Vector3(x1, y1, 0f), tc));
            }
        }
        var idx = new List<int>((slices - 1) * (slices - 1) * 6);
        for (int i = 0; i < slices - 1; i++)
            for (int j = 0; j < slices - 1; j++)
            {
                int x1 = j + 1;
                idx.Add(j + i * slices); idx.Add(x1 + (i + 1) * slices); idx.Add(j + (i + 1) * slices);
                idx.Add(j + i * slices); idx.Add(x1 + i * slices); idx.Add(x1 + (i + 1) * slices);
            }
        return (verts, idx);
    }

    private const float Srr2 = 0.707106781f, Srr3 = 0.577350269f;

    // four leaf quads: two crossed upright quads, each with its back side (inverse winding and normals), 16 vertices, 24 indices
    private static readonly System.Collections.Immutable.ImmutableArray<(Vector3 P, Vector3 N, Vector2 T)> LeafVertices =
    [
        (new(-0.5f * LeafWidth, 0f, 0f), new(-Srr2, -Srr2, 0f), new(LeafLeft, LeafBottom)),
        (new(0.5f * LeafWidth, 0f, 1f), new(Srr3, -Srr3, Srr3), new(LeafRight, LeafTop)),
        (new(-0.5f * LeafWidth, 0f, 1f), new(-Srr3, -Srr3, Srr3), new(LeafLeft, LeafTop)),
        (new(0.5f * LeafWidth, 0f, 0f), new(Srr2, -Srr2, 0f), new(LeafRight, LeafBottom)),
        (new(-0.5f * LeafWidth, 0f, 0f), new(-Srr2, Srr2, 0f), new(LeafLeft, LeafBottom)),
        (new(0.5f * LeafWidth, 0f, 1f), new(Srr3, Srr3, Srr3), new(LeafRight, LeafTop)),
        (new(-0.5f * LeafWidth, 0f, 1f), new(-Srr3, Srr3, Srr3), new(LeafLeft, LeafTop)),
        (new(0.5f * LeafWidth, 0f, 0f), new(Srr2, Srr2, 0f), new(LeafRight, LeafBottom)),
        (new(0f, -0.5f * LeafWidth, 0f), new(Srr2, -Srr2, 0f), new(LeafLeft, LeafBottom)),
        (new(0f, 0.5f * LeafWidth, 1f), new(Srr3, Srr3, Srr3), new(LeafRight, LeafTop)),
        (new(0f, -0.5f * LeafWidth, 1f), new(Srr3, -Srr3, Srr3), new(LeafLeft, LeafTop)),
        (new(0f, 0.5f * LeafWidth, 0f), new(Srr2, Srr2, 0f), new(LeafRight, LeafBottom)),
        (new(0f, -0.5f * LeafWidth, 0f), new(-Srr2, -Srr2, 0f), new(LeafLeft, LeafBottom)),
        (new(0f, 0.5f * LeafWidth, 1f), new(-Srr3, Srr3, Srr3), new(LeafRight, LeafTop)),
        (new(0f, -0.5f * LeafWidth, 1f), new(-Srr3, -Srr3, Srr3), new(LeafLeft, LeafTop)),
        (new(0f, 0.5f * LeafWidth, 0f), new(-Srr2, Srr2, 0f), new(LeafRight, LeafBottom)),
    ];

    private static readonly System.Collections.Immutable.ImmutableArray<int> LeafIndices = [0, 1, 2, 0, 3, 1, 4, 6, 5, 4, 5, 7, 8, 9, 10, 8, 11, 9, 12, 14, 13, 12, 13, 15];

    // ---- the shared meshes: one per species and scale, registered here for the mesh source to find
    private sealed record Entry(MeshGeometry Geometry, Vector3 Centre, Vector3 Size, int Species = 0, float ScaleLength = 0f);
    private static readonly ConcurrentDictionary<Guid, Entry> Registry = new();

    /// <summary>The id of the mesh for a tree of <paramref name="species"/> whose scale has the length <paramref name="scaleLength"/> (to the centimetre); the mesh is built on the first ask.</summary>
    public static Guid IdFor(int species, float scaleLength)
    {
        int q = (int)MathF.Round(Math.Max(scaleLength, 0.01f) * 100f);
        var id = new Guid(0x7ee00000 + q, (short)species, 0x7e11, [0x74, 0x72, 0x65, 0x65, 0x6d, 0x65, 0x73, 0x68]);
        Registry.GetOrAdd(id, _ => Normalised(RawGeometry(species, q / 100f * Unit)) with { Species = species, ScaleLength = q / 100f });
        return id;
    }

    /// <summary>The species and scale length a tree mesh was made for (false for any other id).</summary>
    public static bool TryInfo(Guid id, out int species, out float scaleLength)
    {
        if (Registry.TryGetValue(id, out var e)) { (species, scaleLength) = (e.Species, e.ScaleLength); return true; }
        (species, scaleLength) = (0, 0f);
        return false;
    }

    /// <summary>Whether <paramref name="id"/> is a tree mesh made here: its geometry (inside the unit box around the centre), the centre in the tree's frame and the size the object is scaled to.</summary>
    public static bool TryGet(Guid id, out MeshGeometry? geometry, out Vector3 centre, out Vector3 size)
    {
        if (Registry.TryGetValue(id, out var e)) { (geometry, centre, size) = (e.Geometry, e.Centre, e.Size); return true; }
        geometry = null; centre = default; size = default;
        return false;
    }

    // the best level's bounding box is the object's box: positions go into -0.5..0.5 of it, normals are scaled the way a scaled mesh's are
    private static Entry Normalised(MeshGeometry raw)
    {
        var best = raw.Lods[3]!.Faces[0].Positions;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int i = 0; i < best.Length; i += 3)
        {
            var p = new Vector3(best[i], best[i + 1], best[i + 2]);
            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
        }
        var size = Vector3.Max(hi - lo, new Vector3(0.01f));
        var centre = (hi + lo) / 2f;
        var lods = new MeshLod?[MeshGeometry.LodCount];
        for (int d = 0; d < lods.Length; d++)
        {
            var f = raw.Lods[d]!.Faces[0];
            var pos = new float[f.Positions.Length]; var nor = new float[f.Normals.Length];
            for (int i = 0; i < pos.Length; i += 3)
            {
                for (int k = 0; k < 3; k++) pos[i + k] = (f.Positions[i + k] - centre[k]) / size[k];
                var n = Vector3.Normalize(new Vector3(f.Normals[i] * size.X, f.Normals[i + 1] * size.Y, f.Normals[i + 2] * size.Z));
                nor[i] = n.X; nor[i + 1] = n.Y; nor[i + 2] = n.Z;
            }
            lods[d] = new MeshLod([new MeshFace(pos, nor, f.Uvs, f.Indices)]);
        }
        return new Entry(new MeshGeometry(lods, null), centre, size);
    }
}

/// <summary>
/// The trunk detail of a tree by distance (the official viewer's rule): the apparent angle of the tree's largest scale at its distance less half
/// its smallest scale, times the tree detail factor, against 30, 20 and 15 degrees, and the volume detail from the size the tree is billboarded
/// at; the worse (larger) of the two wins. 0 is the best trunk.
/// </summary>
public static class TreeLod
{
    /// <summary>The official default of the tree detail setting.</summary>
    public const float DefaultTreeFactor = 0.5f;

    // billboard scale and ratio per species (trees.xml)
    private static readonly System.Collections.Immutable.ImmutableArray<(float Scale, float Ratio)> Billboard =
    [
        (39.5f, 1.1f), (10.25f, 1f), (5f, 1.25f), (13.25f, 1f), (10f, 1f), (4.5f, 0.9f), (13.5f, 1f), (22.5f, 1f), (25f, 0.8f), (33f, 1.35f), (10f, 1.35f),
        (12.5f, 0.6f), (12f, 0.675f), (7.5f, 0.5f), (24f, 1.3f), (3.5f, 0.85f), (3f, 1f), (2f, 1f), (4.9f, 1f), (2.5f, 1.2f), (4.9f, 1f),
    ];
    private static readonly System.Collections.Immutable.ImmutableArray<float> Angles = [30f, 20f, 15f, 0.0000001f];
    private static readonly System.Collections.Immutable.ImmutableArray<float> Thresholds = [0.03f, 0.06f, 0.24f, 3.0f];

    /// <param name="scale">The tree object's scale.</param>
    /// <param name="distance">From the camera to the tree.</param>
    /// <param name="volumeLodFactor">The detail setting for ordinary objects (the same one the prim detail uses).</param>
    /// <returns>0 to 3, or 4 for a tree not drawn at all.</returns>
    public static int TrunkLod(int species, Vector3 scale, float distance, float volumeLodFactor = 2f, float treeFactor = DefaultTreeFactor)
    {
        var b = Billboard[species < 0 || species >= Billboard.Length ? 0 : species];
        float max = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)), min = MathF.Min(scale.X, MathF.Min(scale.Y, scale.Z));
        float range = distance - min / 2f;
        float appAngle = range < 0.001f ? 180f : MathF.Atan2(max, range) * 180f / MathF.PI;
        appAngle *= treeFactor;
        int lod = 4;
        for (int j = 0; j < Angles.Length; j++)
            if (appAngle > Angles[j]) { lod = j; break; }
        float dist = distance * (1f - volumeLodFactor * 0.1f) * (MathF.PI / 3f);
        float sz = b.Scale * b.Ratio * scale.Length();
        float tan = dist <= 0f ? float.MaxValue : MathF.Floor(treeFactor * 64f * sz / dist / 0.01f + 0.5f) * 0.01f;
        int detail = 3;
        for (int i = 0; i < 3; i++)
            if (tan <= Thresholds[i]) { detail = i; break; }
        return Math.Min(Math.Max(lod, 3 - detail), 4);
    }

    /// <summary>The mesh detail (0 to 3, 3 the best) that holds trunk detail <paramref name="trunkLod"/>.</summary>
    public static int MeshDetail(int trunkLod) => 3 - Math.Clamp(trunkLod, 0, 3);
}

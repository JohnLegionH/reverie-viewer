// SPDX-License-Identifier: BSD-3-Clause
/* Copyright (c) 2008 Robert Adams
 * Copyright (c) 2021-2026, Sjofn LLC. All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * The name of the copyright holder may not be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS

/* Adapted 2026 by Legion Builds: the prim shape block and its mapping to PrimMesher parameters (PrimShape and the
 * parameter mapping) follow GeneratePrimMesh in LibreMetaverse.Rendering.MeshFoundry/MeshFoundry.cs, translated to C# for this project. */

namespace Viewer.Core.Meshing;

/// <summary>
/// A prim's shape block (the ObjectUpdate construction data), as floats in their decoded ranges. Mirrors the web viewer's
/// <c>PrimShape</c> (client/src/mesh/primmesher/prim.ts, bb91ba5) field for field, so the same fixtures drive both.
/// </summary>
public sealed record PrimShape
{
    /// <summary>0 Circle, 1 Square, 2 IsoTriangle, 3 EqualTriangle, 4 RightTriangle, 5 HalfCircle (low 3 bits).</summary>
    public int ProfileCurve { get; init; } = 1;
    /// <summary>0 Same, 16 Circle, 32 Square, 48 Triangle.</summary>
    public int ProfileHole { get; init; }
    public double ProfileBegin { get; init; }
    public double ProfileEnd { get; init; } = 1;
    public double ProfileHollow { get; init; }
    /// <summary>16 Line, 32 Circle, 48 Circle2, 64 Test, 128 Flexible.</summary>
    public int PathCurve { get; init; } = 16;
    public double PathBegin { get; init; }
    public double PathEnd { get; init; } = 1;
    public double PathScaleX { get; init; } = 1;
    public double PathScaleY { get; init; } = 1;
    public double PathShearX { get; init; }
    public double PathShearY { get; init; }
    public double PathTwist { get; init; }
    public double PathTwistBegin { get; init; }
    public double PathRadiusOffset { get; init; }
    public double PathTaperX { get; init; }
    public double PathTaperY { get; init; }
    public double PathRevolutions { get; init; } = 1;
    public double PathSkew { get; init; }
    /// <summary>Non-zero: a sculpt or mesh, not a prim; <see cref="PrimGeometry.Build"/> refuses it.</summary>
    public int SculptType { get; init; }
    /// <summary>The sculpt map or (SculptType 5) mesh asset id; empty for a prim.</summary>
    public Guid SculptId { get; init; }
    /// <summary>The sculpt's invert (0x40) and mirror (0x80) bits (<see cref="SculptMesher.FlagInvert"/>,
    /// <see cref="SculptMesher.FlagMirror"/>); 0 for prims, meshes and captures made.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SculptFlags { get; init; }

    /// <summary>A sculpted prim (SculptType 1-4), drawn by <see cref="SculptMesher"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSculpt => SculptType is > 0 and < SculptTypeMesh;

    public const int SculptTypeMesh = 5;

    public static PrimShape Box { get; } = new();
}

public enum PrimDetail { Low, Medium, High }

/// <summary>One prim face: non-indexed triangles (indices 0..n-1), in the prim's unit cube (-0.5..0.5 before scale).</summary>
public sealed record FaceGeometry(int Face, float[] Positions, float[] Normals, float[] Uvs, int[] Indices)
{
    public int Triangles => Indices.Length / 3;
}

/// <summary>
/// Shape -> PrimMesh -> per-face buffers. The parameter mapping mirrors LibreMetaverse's MeshFoundry.GeneratePrimMesh
/// (BSD-3) as the web viewer's prim.ts does; face numbering is the viewer convention (box: 0 top, 1-4 sides, 5 bottom).
/// </summary>
public static class PrimGeometry
{
    /// <summary>The detail the three coarse levels stand for: the lowest official scale, one in between, the highest.</summary>
    private static float DetailOf(PrimDetail lod) => lod == PrimDetail.Low ? 1f : lod == PrimDetail.Medium ? 2f : 4f;

    public static PrimMesh Generate(PrimShape p, PrimDetail lod = PrimDetail.High, PrimMesherRegression regressions = PrimMesherRegression.None) => GenerateCore(p, DetailOf(lod), regressions, official: false);

    /// <summary>The prim's mesh at <paramref name="detail"/> (an official detail scale, <see cref="PrimLodGroup.Scales"/>): a circle profile has floor(6 x detail) sides (a
    /// half circle half as many for each half), snapped up to a multiple of four with a square hole (llvolume.cpp 862-935); a circle path has
    /// floor(6 x detail + twist x 3.5 x (detail - 0.5)) points for each revolution, a line path floor(twist x 3.5 x (detail - 0.5)) + 2 points (1418-1440).</summary>
    public static PrimMesh Generate(PrimShape p, float detail, PrimMesherRegression regressions = PrimMesherRegression.None) => GenerateCore(p, detail, regressions, official: true);

    private static PrimMesh GenerateCore(PrimShape p, float detail, PrimMesherRegression regressions, bool official)
    {
        int circle = (int)Math.Floor(6f * detail);
        if (official && p.ProfileHollow > 0 && p.ProfileHole == 32) circle = (int)(Math.Ceiling(6f * detail / 4f) * 4);   // corners line up with a square hole
        int half = 2 * (int)Math.Floor(6f * detail * 0.5f);
        double twistMagnitude = official ? Math.Abs(p.PathTwistBegin - p.PathTwist) : 0;
        int pathCircle = Math.Max(3, (int)Math.Floor(6f * detail + twistMagnitude * 3.5 * (detail - 0.5)));
        int sides = 4, hollowSides = 4;
        double profileBegin = p.ProfileBegin, profileEnd = p.ProfileEnd;
        bool isSphere = false;
        int curve = p.ProfileCurve & 0x07;
        if (curve == 0) sides = circle;
        else if (curve == 3) sides = 3;
        else if (curve == 5)
        {
            isSphere = true;
            sides = half;
            profileBegin = 0.5 * profileBegin + 0.5;
            profileEnd = 0.5 * profileEnd + 0.5;
        }
        if (p.ProfileHole == 0) hollowSides = sides;
        else if (p.ProfileHole == 16) hollowSides = curve == 5 ? half : circle;
        else if (p.ProfileHole == 48) hollowSides = 3;

        var mesh = new PrimMesh(sides, profileBegin, profileEnd, p.ProfileHollow, hollowSides, regressions)
        {
            SphereMode = isSphere,
            HoleSizeX = p.PathScaleX,
            HoleSizeY = p.PathScaleY,
            PathCutBegin = p.PathBegin,
            PathCutEnd = p.PathEnd,
            TopShearX = p.PathShearX,
            TopShearY = p.PathShearY,
            Radius = p.PathRadiusOffset,
            Revolutions = p.PathRevolutions,
            Skew = p.PathSkew,
            StepsPerRevolution = pathCircle,
        };
        if (p.PathCurve is 16 or 128)
        {
            mesh.TaperX = 1.0 - p.PathScaleX;
            mesh.TaperY = 1.0 - p.PathScaleY;
            mesh.TwistBegin = (int)Math.Truncate(180 * p.PathTwistBegin);
            mesh.TwistEnd = (int)Math.Truncate(180 * p.PathTwist);
            if (official) mesh.LinearSteps = (int)Math.Floor(twistMagnitude * 3.5 * (detail - 0.5)) + 1;   // points - 1, the path having floor(twist x 3.5 x (detail - 0.5)) + 2
            mesh.Extrude(PathType.Linear);
        }
        else
        {
            mesh.TaperX = p.PathTaperX;
            mesh.TaperY = p.PathTaperY;
            mesh.TwistBegin = (int)Math.Truncate(360 * p.PathTwistBegin);
            mesh.TwistEnd = (int)Math.Truncate(360 * p.PathTwist);
            mesh.Extrude(PathType.Circular);
        }
        return mesh;
    }

    /// <summary>Pure: shape -> one <see cref="FaceGeometry"/> per prim face, numbered as the viewer convention.</summary>
    public static IReadOnlyList<FaceGeometry> Build(PrimShape p, float detail) => GroupFaces(WithoutEdgeFacesOfWholeSphere(p, Generate(p, detail).ViewerFaces), slTextureOrientation: true);

    public static IReadOnlyList<FaceGeometry> Build(PrimShape p, PrimDetail lod = PrimDetail.High)
    {
        if (p.SculptType != 0)
            throw new NotSupportedException("sculpts and meshes are not prims");
        return GroupFaces(WithoutEdgeFacesOfWholeSphere(p, Generate(p, lod).ViewerFaces), slTextureOrientation: true);
    }

    /// <summary>
    /// A sphere (half-circle profile on a circle path) that is whole, not hollow and not cut along its path is one face: the official viewer closes its profile
    /// and adds the two edge faces only when the profile covers less than the whole range (llvolume.cpp, LLProfile::generate, the half-circle case). The mesher
    /// numbers its two flat edge faces 1 and 2 whatever the range, and they only drop out as degenerate where the rings happen to lie on one line.
    /// </summary>
    private static IEnumerable<ViewerFace> WithoutEdgeFacesOfWholeSphere(PrimShape p, List<ViewerFace> faces) =>
        (p.ProfileCurve & 0x07) == 5 && p.ProfileEnd - p.ProfileBegin >= 1.0 && p.ProfileHollow <= 0 && p.PathBegin <= 0 && p.PathEnd >= 1.0
            ? faces.Where(f => f.PrimFaceNumber == 0)
            : faces;

    private static double Area(ViewerFace vf)
    {
        double ux = vf.V2.X - vf.V1.X, uy = vf.V2.Y - vf.V1.Y, uz = vf.V2.Z - vf.V1.Z;
        double vx = vf.V3.X - vf.V1.X, vy = vf.V3.Y - vf.V1.Y, vz = vf.V3.Z - vf.V1.Z;
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    /// <summary>
    /// Groups viewer faces by prim face number, drops degenerate triangles and renumbers the remaining groups
    /// consecutively (as the web viewer's groupFaces): a closed 3/4-sided profile's duplicate closing vertex would otherwise
    /// take a face number of its own and push the bottom cap off the texture-entry slot the viewer convention expects.
    /// </summary>
    /// <param name="slTextureOrientation">
    /// The port's UVs run v DOWN the image (its top row first) and give the
    /// bottom cap the top cap's mapping. SL's run v UP the image, and SL's bottom cap mirrors the top cap's v. So: v becomes
    /// 1 - v on every face except the bottom cap, which is already SL's as it stands. False keeps the port's UVs (parity).
    /// </param>
    public static IReadOnlyList<FaceGeometry> GroupFaces(IEnumerable<ViewerFace> viewerFaces, bool slTextureOrientation = false)
    {
        var byFace = new SortedDictionary<int, List<ViewerFace>>();
        foreach (var vf in viewerFaces)
        {
            if (Area(vf) < 1e-9)
                continue;
            if (!byFace.TryGetValue(vf.PrimFaceNumber, out var list))
                byFace[vf.PrimFaceNumber] = list = new List<ViewerFace>();
            list.Add(vf);
        }
        var output = new List<FaceGeometry>();
        int face = -1;
        foreach (var list in byFace.Values)
        {
            face++;
            int n = list.Count;
            var pos = new float[n * 9];
            var nrm = new float[n * 9];
            var uv = new float[n * 6];
            var idx = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                var v = list[i];
                int o = i * 9;
                Put(pos, o, v.V1); Put(pos, o + 3, v.V2); Put(pos, o + 6, v.V3);
                Put(nrm, o, v.N1); Put(nrm, o + 3, v.N2); Put(nrm, o + 6, v.N3);
                int u = i * 6;
                bool flipV = slTextureOrientation && !v.BottomCap;
                uv[u] = (float)v.Uv1.U; uv[u + 1] = (float)(flipV ? 1 - v.Uv1.V : v.Uv1.V);
                uv[u + 2] = (float)v.Uv2.U; uv[u + 3] = (float)(flipV ? 1 - v.Uv2.V : v.Uv2.V);
                uv[u + 4] = (float)v.Uv3.U; uv[u + 5] = (float)(flipV ? 1 - v.Uv3.V : v.Uv3.V);
                idx[i * 3] = i * 3; idx[i * 3 + 1] = i * 3 + 1; idx[i * 3 + 2] = i * 3 + 2;
            }
            output.Add(new FaceGeometry(face, pos, nrm, uv, idx));
        }
        return output;
    }

    private static void Put(float[] a, int o, Coord c)
    {
        a[o] = (float)c.X;
        a[o + 1] = (float)c.Y;
        a[o + 2] = (float)c.Z;
    }
}

/// <summary>
/// The detail levels a volume is built at, and the size its distance rule uses. Ported as behaviour from the official viewer (LLVolumeLODGroup, llvolumemgr.cpp
/// 41: the four detail scales; LLVolume, llvolume.cpp 2055-2071: the scale bias of the level-of-detail radius), not as code.
/// </summary>
public static class PrimLodGroup
{
    /// <summary>The detail each of the four levels is built at.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<float> Scales = [1f, 1.5f, 2.5f, 4f];

    /// <summary>The factor each axis of the scale is multiplied by before the level-of-detail rule takes its length: 0.5, but 0.6 on x and y (z ignored) for a
    /// cylinder (a line path with a circle profile) and 0.6 on all three for a circle path.</summary>
    public static System.Numerics.Vector3 ScaleBias(PrimShape shape)
    {
        if (shape.PathCurve == 16 && (shape.ProfileCurve & 0x07) == 0) return new(0.6f, 0.6f, 0f);
        if (shape.PathCurve == 32) return new(0.6f, 0.6f, 0.6f);
        return new(0.5f, 0.5f, 0.5f);
    }

    /// <summary>The radius the level-of-detail rule uses for a prim of this shape and scale.</summary>
    public static float Radius(PrimShape shape, System.Numerics.Vector3 scale) => (scale * ScaleBias(shape)).Length();
}

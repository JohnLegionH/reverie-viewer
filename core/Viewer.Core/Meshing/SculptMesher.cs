// SPDX-License-Identifier: BSD-3-Clause AND LGPL-2.1-only
/*
 * C# port of SculptMap / SculptMesh, via the web viewer's TypeScript port (client/src/mesh/sculpt.ts, web-viewer
 * bb91ba5, last changed 5d66b43), which ports PrimMesher/SculptMap.cs and PrimMesher/SculptMesh.cs from LibreMetaverse
 * (https://github.com/cinderblocks/libremetaverse), itself derived from the OpenSimulator PrimMesher by Dahlia Trimble.
 *
 * Copyright (c) Contributors
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * Adapted 2026 by Legion Builds: C# port of PrimMesher/SculptMap.cs and PrimMesher/SculptMesh.cs (BuildReference, MapToRows,
 * Triangulate), with the changes listed below. BuildOfficial, OfficialGrid and Mesh are adapted from the Second Life
 * viewer source; its notice follows this comment.
 *
 * additions (not in the port): the LOD rule, the official sampler, the placeholders, the surface-area
 * check and the degenerate-map guards. They are written from the behaviour spec in Docs/SCULPT-SAMPLER-SPEC.md
 * (those parts follow the official viewer's behaviour as a reference; the code translated from llvolume.cpp is the part named in the Adapted line above).
 */

/**
 * @file llvolume.cpp
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

/* Adapted 2026 by Legion Builds: BuildOfficial, OfficialGrid and Mesh are adapted from sculptGenerateMapVertices,
 * sculpt_calc_mesh_resolution and the sculpt mesh build in llvolume.cpp, translated to C#. */
namespace Viewer.Core.Meshing;

/// <summary>What <see cref="SculptMesher.Mesh"/> produced.</summary>
public enum SculptOutcome
{
    /// <summary>Geometry from the map.</summary>
    Map,
    /// <summary>The official viewer's visible stand-in (a radius-0.3 sphere): the map is missing, or its surface area is out of range.</summary>
    SpherePlaceholder,
    /// <summary>The official viewer's empty placeholder (all points at the origin): nothing is drawn.</summary>
    Empty,
}

/// <summary>A sculpt's single face (sculpts have one face, face 0) and how it was made.</summary>
public sealed record SculptResult(SculptOutcome Outcome, MeshFace? Face, string? Reason);

public static class SculptMesher
{
    public const int TypeMask = 0x07;
    public const int Sphere = 1, Torus = 2, Plane = 3, Cylinder = 4;
    /// <summary>LL_SCULPT_FLAG_INVERT and LL_SCULPT_FLAG_MIRROR (llvolume.h).</summary>
    public const int FlagInvert = 0x40, FlagMirror = 0x80;

    /// <summary>SCULPT_MIN_AREA and SCULPT_MAX_AREA (llvolume.cpp); not checked at the lowest LOD (DEV-33670, legacy content).</summary>
    public const float MinArea = 0.002f, MaxArea = 384f;
    /// <summary>The visible stand-in's radius (spec section 5).</summary>
    public const float PlaceholderRadius = 0.3f;

    /// <summary>The official viewer's volume detail for LOD 0-3 (llvolumemgr.cpp mDetailScales).</summary>
    public static float DetailOf(int lod) => lod switch { <= 0 => 1f, 1 => 1.5f, 2 => 2.5f, _ => 4f };

    /// <summary>The side budget by detail (spec section 1): 6, 8, 16 or 32.</summary>
    public static int Sides(float detail) => detail <= 1f ? 6 : detail <= 2f ? 8 : detail <= 3f ? 16 : 32;

    /// <summary>The side budget for a LOD (0-3).</summary>
    public static int SidesForLod(int lod) => Sides(DetailOf(lod));

    /// <summary>
    /// The LOD radius bias the official viewer gives a sculpt (LLVolume::generate, which runs before sculpt): 0.5 on
    /// every axis; a line path with a circle profile (0.6, 0.6, 0); a circle path 0.6. Radius = |bias * scale|.
    /// </summary>
    public static System.Numerics.Vector3 LodBias(PrimShape s)
    {
        int path = s.PathCurve & 0xF0, profile = s.ProfileCurve & 0x07;
        if (path == 16 && profile == 0) return new(0.6f, 0.6f, 0f);
        if (path == 32) return new(0.6f, 0.6f, 0.6f);
        return new(0.5f, 0.5f, 0.5f);
    }

    /// <summary>
    /// The sculpt at a LOD (0-3), as the official viewer decides it (LLVolume::sculpt): no map (missing asset) gives the
    /// sphere placeholder; a refused map (the decode guard said no: invalid or hostile) gives the empty placeholder,
    /// as a failed validation does; a map whose surface is out of [<see cref="MinArea"/>, <see cref="MaxArea"/>] above
    /// the lowest LOD gives the sphere placeholder. <paramref name="type"/> is the sculpt type with its flags.
    /// </summary>
    public static SculptResult Mesh(ReadOnlySpan<byte> rgba, int width, int height, int type, int lod, bool missing = false)
    {
        int sides = SidesForLod(lod);
        if (missing)
            return new(SculptOutcome.SpherePlaceholder, SpherePlaceholder(sides), "map missing");
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4)
            return new(SculptOutcome.Empty, null, "no map data");
        var face = BuildOfficial(rgba, width, height, type & TypeMask, (type & FlagMirror) != 0, (type & FlagInvert) != 0, sides);
        float area = SurfaceArea(face);
        if (lod > 0 && (area < MinArea || area > MaxArea))
            return new(SculptOutcome.SpherePlaceholder, SpherePlaceholder(sides), $"surface area {area:0.####} out of range");
        if (area == 0f)
            return new(SculptOutcome.Empty, null, "zero surface area");   // the lowest LOD is not area-checked: nothing to draw
        return new(SculptOutcome.Map, face, null);
    }

    /// <summary>
    /// The sculpt from its map as the texture pipeline delivered it: decoded through the guard (<paramref name="map"/>),
    /// refused by the guard (<paramref name="map"/> null: the empty placeholder), or missing from the asset service
    /// (<paramref name="missing"/>: the sphere placeholder, as the official viewer does for isMissingAsset).
    /// </summary>
    public static SculptResult FromMap(Viewer.Core.Imaging.DecodedTexture? map, bool missing, int type, int lod) =>
        map is null ? Mesh([], 0, 0, type, lod, missing) : Mesh(map.Rgba, map.Width, map.Height, type, lod);

    /// <summary>The visible stand-in: a sphere of radius 0.3 on the (sides + 1) x (sides + 1) grid, u down the path, v across the profile.</summary>
    public static MeshFace SpherePlaceholder(int sides)
    {
        var rows = new List<Vec[]>(sides + 1);
        for (int s = 0; s <= sides; s++)
        {
            var row = new Vec[sides + 1];
            double u = (double)s / sides;
            for (int t = 0; t <= sides; t++)
            {
                double v = (double)t / sides;
                row[t] = new(Math.Sin(Math.PI * v) * Math.Cos(2 * Math.PI * u) * PlaceholderRadius,
                    Math.Sin(Math.PI * v) * Math.Sin(2 * Math.PI * u) * PlaceholderRadius, Math.Cos(Math.PI * v) * PlaceholderRadius);
            }
            rows.Add(row);
        }
        return Triangulate(rows, invert: true, seamNormals: true);
    }

    /// <summary>Sum of triangle areas, in map units (before the object's scale), like sculptGetSurfaceArea.</summary>
    public static float SurfaceArea(MeshFace f)
    {
        double area = 0;
        var p = f.Positions;
        for (int i = 0; i < f.Indices.Length; i += 3)
        {
            int a = f.Indices[i] * 3, b = f.Indices[i + 1] * 3, c = f.Indices[i + 2] * 3;
            double e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
            double e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            area += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        }
        return (float)area;
    }

    private readonly record struct Vec(double X, double Y, double Z);

    /// <summary>
    /// The grid for a map at a side budget (spec sections 1-2): <c>S</c> segments down the map (rows), <c>T</c> across
    /// (columns); the vertex grid is (S + 1) x (T + 1) and S * T never exceeds sides * sides.
    /// </summary>
    public static (int S, int T) OfficialGrid(int width, int height, int sides)
    {
        int budget = sides * sides;
        long fromMap = (long)Math.Max(width, 0) * Math.Max(height, 0) / 4;
        if (fromMap > 0 && fromMap < budget)
            budget = (int)fromMap;
        float ratio = width <= 0 || height <= 0 ? 1f : (float)width / height;
        int s = Math.Max((int)MathF.Sqrt(budget / ratio), 4);
        int t = Math.Max(budget / s, 4);
        s = Math.Max(budget / t, 1);   // the path always has at least one segment (a map of 1-3 pixels)
        return (s, t);
    }

    /// <summary>
    /// The official sampler (spec section 3) over the official grid, triangulated and smoothed by the port's code
    /// (spec section 4). The map must hold width x height RGBA pixels.
    /// </summary>
    public static MeshFace BuildOfficial(ReadOnlySpan<byte> rgba, int width, int height, int type, bool mirror, bool invert, int sides)
    {
        type &= TypeMask;
        var (segS, segT) = OfficialGrid(width, height, sides);
        bool reverse = invert != mirror;
        bool wrapX = type is Sphere or Torus or Cylinder;
        const double pixScale = 1.0 / 255;
        var rows = new List<Vec[]>(segS + 1);
        for (int i = 0; i <= segS; i++)
        {
            var row = new Vec[segT + 1];
            for (int j = 0; j <= segT; j++)
            {
                int jj = reverse ? segT - j : j;
                int x = (int)((float)jj / segT * width), y = (int)((float)i / segS * height);   // single precision, truncated
                if (y == 0 && type == Sphere) x = width / 2;
                if (y >= height)
                {
                    y = type == Torus ? 0 : height - 1;
                    if (type == Sphere) x = width / 2;
                }
                if (x >= width) x = wrapX ? 0 : width - 1;
                int k = ((height - 1 - y) * width + x) * 4;   // spec 3: row 0 is the map's BOTTOM row (images are stored bottom-up)
                double r = rgba[k] * pixScale - 0.5, g = rgba[k + 1] * pixScale - 0.5, b = rgba[k + 2] * pixScale - 0.5;
                row[j] = new(mirror ? -r : r, g, b);
            }
            rows.Add(row);
        }
        // spec 4: SL's winding for rows read bottom-up is the port's inverted order (a real map made for SL faces outward)
        return Triangulate(rows, invert: true, seamNormals: type != Plane);
    }

    /// <summary>
    /// The port, unchanged in behaviour (the web viewer's sculpt.ts, <c>buildSculptGeometry(img, type, mirror, invert, lod)</c>):
    /// kept as the documented REFERENCE for fixtures/sculpts, not used for drawing.
    /// Null where the port would produce NaN or read out of range (a map that samples to fewer than 2 x 2 points, or a
    /// dimension halved to 0).
    /// </summary>
    public static MeshFace? BuildReference(ReadOnlySpan<byte> rgba, int width, int height, int type, bool mirror, bool invert, int lod)
    {
        var rows = MapToRows(rgba, width, height, lod, mirror);
        if (rows is null)
            return null;
        type &= TypeMask;
        if (mirror) invert = !invert;
        int w = rows[0].Length;
        if (type != Plane)
        {
            if (rows.Count % 2 == 0)
                for (int r = 0; r < rows.Count; r++) { var row = rows[r]; Array.Resize(ref row, row.Length + 1); row[^1] = row[0]; rows[r] = row; }
            else
            {
                int lastIndex = rows[0].Length - 1;
                foreach (var row in rows) row[0] = row[lastIndex];
            }
        }
        var topPole = rows[0][w >> 1];
        var bottomPole = rows[^1][w >> 1];
        if (type == Sphere)
        {
            int count = rows[0].Length;
            if (rows.Count % 2 == 0)
            {
                rows.Insert(0, Enumerable.Repeat(topPole, count).ToArray());
                rows.Add(Enumerable.Repeat(bottomPole, count).ToArray());
            }
            else
                for (int i = 0; i < count; i++) { rows[0][i] = topPole; rows[^1][i] = bottomPole; }
        }
        if (type == Torus) rows.Add(rows[0]);
        if (rows.Count < 2 || rows[0].Length < 2)
            return null;
        return Triangulate(rows, invert, seamNormals: type != Plane);
    }

    private static MeshFace Triangulate(List<Vec[]> rows, bool invert, bool seamNormals)
    {
        int down = rows.Count, across = rows[0].Length;
        double widthUnit = 1.0 / (across - 1), heightUnit = 1.0 / (down - 1);
        var positions = new float[down * across * 3];
        var uvs = new float[down * across * 2];
        var indices = new List<int>((down - 1) * (across - 1) * 6);
        for (int y = 0; y < down; y++)
        {
            int rowOffset = y * across;
            for (int x = 0; x < across; x++)
            {
                int p4 = rowOffset + x, p3 = p4 - 1, p2 = p4 - across, p1 = p3 - across;
                var c = rows[y][x];
                positions[p4 * 3] = (float)c.X;
                positions[p4 * 3 + 1] = (float)c.Y;
                positions[p4 * 3 + 2] = (float)c.Z;
                uvs[p4 * 2] = (float)(widthUnit * x);
                uvs[p4 * 2 + 1] = (float)(heightUnit * y);
                if (y > 0 && x > 0)
                {
                    if (invert) { indices.Add(p1); indices.Add(p4); indices.Add(p3); indices.Add(p1); indices.Add(p2); indices.Add(p4); }
                    else { indices.Add(p1); indices.Add(p3); indices.Add(p4); indices.Add(p1); indices.Add(p4); indices.Add(p2); }
                }
            }
        }
        // vertex normals: sum of adjacent surface normals (in double, from the float positions, as the port's Float32Array), then normalised
        var n = new double[positions.Length];
        for (int i = 0; i < indices.Count; i += 3)
        {
            int a = indices[i] * 3, b = indices[i + 1] * 3, cc = indices[i + 2] * 3;
            double e1x = positions[b] - (double)positions[a], e1y = positions[b + 1] - (double)positions[a + 1], e1z = positions[b + 2] - (double)positions[a + 2];
            double e2x = positions[cc] - (double)positions[a], e2y = positions[cc + 1] - (double)positions[a + 1], e2z = positions[cc + 2] - (double)positions[a + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
            foreach (int v in (ReadOnlySpan<int>)[a, b, cc])
            {
                // the port accumulates into a Float32Array: round after each add
                n[v] = (float)(n[v] + nx); n[v + 1] = (float)(n[v + 1] + ny); n[v + 2] = (float)(n[v + 2] + nz);
            }
        }
        var normals = new float[positions.Length];
        for (int v = 0; v < n.Length; v += 3)
        {
            double len = Math.Sqrt(n[v] * n[v] + n[v + 1] * n[v + 1] + n[v + 2] * n[v + 2]);
            if (len > 1e-12) { n[v] /= len; n[v + 1] /= len; n[v + 2] /= len; }
            normals[v] = (float)n[v]; normals[v + 1] = (float)n[v + 1]; normals[v + 2] = (float)n[v + 2];
        }
        if (seamNormals)
            for (int y = 0; y < down; y++)
            {
                int first = y * across * 3, last = (y * across + across - 1) * 3;
                double nx = (double)normals[first] + normals[last], ny = (double)normals[first + 1] + normals[last + 1], nz = (double)normals[first + 2] + normals[last + 2];
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
                normals[first] = normals[last] = (float)nx;
                normals[first + 1] = normals[last + 1] = (float)ny;
                normals[first + 2] = normals[last + 2] = (float)nz;
            }
        return new MeshFace(positions, normals, uvs, indices.ToArray());
    }

    /// <summary>SculptMap: sample the image to the LOD budget and convert to rows of coordinates (null: degenerate).</summary>
    private static List<Vec[]>? MapToRows(ReadOnlySpan<byte> rgba, int bmW, int bmH, int lod, bool mirror)
    {
        if (bmW <= 0 || bmH <= 0 || lod <= 0)
            return null;
        long numLodPixels = (long)lod * 2 * (lod * 2);
        bool smallMap = (long)bmW * bmH <= (long)lod * lod;
        int width = bmW, height = bmH;
        bool needsScaling = false;
        while ((long)width * height > numLodPixels)
        {
            width >>= 1;
            height >>= 1;
            needsScaling = true;
        }
        if (width == 0 || height == 0)
            return null;   // a very thin map halves one side to nothing (the port would read out of range)
        byte[] src;
        int srcW, srcH;
        if (needsScaling) { src = Resize(rgba, bmW, bmH, width, height); srcW = width; srcH = height; }
        else { src = rgba[..(bmW * bmH * 4)].ToArray(); srcW = bmW; srcH = bmH; }
        if ((long)width * height > (long)lod * lod)
        {
            width >>= 1;
            height >>= 1;
        }
        var samples = new List<(byte R, byte G, byte B)>();
        if (smallMap)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    samples.Add(Pixel(src, srcW, x, y));
        }
        else
        {
            for (int y = 0; y <= height; y++)
            {
                int sy = y < height ? y * 2 : y * 2 - 1;
                for (int x = 0; x <= width; x++)
                {
                    int sx = x < width ? x * 2 : x * 2 - 1;
                    int px = Math.Min(sx, srcW - 1), py = Math.Min(sy, srcH - 1);
                    if (px < 0 || py < 0)
                        return null;
                    samples.Add(Pixel(src, srcW, px, py));
                }
            }
            width++;
            height++;
        }
        if (width == 0 || height == 0)
            return null;
        const double pixScale = 1.0 / 255;
        var rows = new List<Vec[]>(height);
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            var row = new Vec[width];
            for (int x = 0; x < width; x++, i++)
            {
                double r = samples[i].R * pixScale - 0.5, g = samples[i].G * pixScale - 0.5, b = samples[i].B * pixScale - 0.5;
                row[x] = mirror ? new Vec(-r, g, b) : new Vec(r, g, b);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static (byte, byte, byte) Pixel(byte[] img, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (img[i], img[i + 1], img[i + 2]);
    }

    /// <summary>The port's bilinear resample (replaces ManagedImage.ResizeBilinear); Math.round is half-up.</summary>
    private static byte[] Resize(ReadOnlySpan<byte> img, int iw, int ih, int w, int h)
    {
        var o = new byte[w * h * 4];
        double sx = (double)iw / w, sy = (double)ih / h;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Min(ih - 1, y * sy);
            int y0 = (int)Math.Floor(fy), y1 = Math.Min(ih - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Min(iw - 1, x * sx);
                int x0 = (int)Math.Floor(fx), x1 = Math.Min(iw - 1, x0 + 1);
                double tx = fx - x0;
                int oi = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double p00 = img[(y0 * iw + x0) * 4 + c], p10 = img[(y0 * iw + x1) * 4 + c];
                    double p01 = img[(y1 * iw + x0) * 4 + c], p11 = img[(y1 * iw + x1) * 4 + c];
                    double v = (p00 * (1 - tx) + p10 * tx) * (1 - ty) + (p01 * (1 - tx) + p11 * tx) * ty;
                    o[oi + c] = (byte)Math.Floor(v + 0.5);
                }
                o[oi + 3] = 255;
            }
        }
        return o;
    }
}

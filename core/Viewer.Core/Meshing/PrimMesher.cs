// SPDX-License-Identifier: BSD-3-Clause
/*
 * C# port of PrimMesher, via the web viewer's TypeScript port (client/src/mesh/primmesher/primmesher.ts, web-viewer
 * bb91ba5), which ports PrimMesher from LibreMetaverse (https://github.com/cinderblocks/libremetaverse,
 * PrimMesher/PrimMesher.cs), itself derived from the OpenSimulator PrimMesher by Dahlia Trimble.
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
 * Adapted 2026 by Legion Builds: C# port of PrimMesher/PrimMesher.cs (viewer-mode path only), with the changes listed
 * in the port notes below.
 *
 * Port notes:
 *  - Only the viewer-mode path (viewer faces with per-vertex normals and UVs), as in the TS port.
 *  - The three LibreMetaverse 3.1.4 regressions are NOT reproduced (ADR-007 patch layer):
 *    (1) HasProfileCut / HasHollow are computed in the PrimMesh constructor (3.1.4 never assigns them);
 *    (2) twist begin/end are degrees, converted to radians in Extrude (3.1.4 feeds degrees into radian math);
 *    (3) the bottom end cap is emitted (3.1.4 never emits it).
 *    <see cref="PrimMesherRegression"/> re-enables each one, for the negative-control tests ONLY.
 *  - Arithmetic is double, with single-precision rounding (Fr) at exactly the TS port's Math.fround points, so loop and
 *    layer counts match both the TS port and the single-precision reference.
 */
using System.Collections.Immutable;

namespace Viewer.Core.Meshing;

/// <summary>The 3.1.4 regressions, switchable back on ONLY to prove the regression tests detect them (negative controls).</summary>
[Flags]
public enum PrimMesherRegression
{
    None = 0,
    ProfileFlagsNeverSet = 1,
    TwistDegreesAsRadians = 2,
    NoBottomCap = 4,
}

public readonly record struct Coord(double X, double Y, double Z)
{
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Coord Invert() => new(-X, -Y, -Z);

    public Coord Normalize()
    {
        double mag = Length();
        if (mag > 0.0000001)
        {
            double oomag = 1 / mag;
            return new(X * oomag, Y * oomag, Z * oomag);
        }
        return new(0, 0, 0);
    }

    public static Coord Cross(Coord c1, Coord c2) => new(c1.Y * c2.Z - c2.Y * c1.Z, c1.Z * c2.X - c2.Z * c1.X, c1.X * c2.Y - c2.X * c1.Y);

    /// <summary>v * q (rotate this vector by the quaternion), as in the reference.</summary>
    public Coord MulQuat(Quat q) => new(
        q.W * q.W * X + 2 * q.Y * q.W * Z - 2 * q.Z * q.W * Y + q.X * q.X * X + 2 * q.Y * q.X * Y + 2 * q.Z * q.X * Z - q.Z * q.Z * X - q.Y * q.Y * X,
        2 * q.X * q.Y * X + q.Y * q.Y * Y + 2 * q.Z * q.Y * Z + 2 * q.W * q.Z * X - q.Z * q.Z * Y + q.W * q.W * Y - 2 * q.X * q.W * Z - q.X * q.X * Y,
        2 * q.X * q.Z * X + 2 * q.Y * q.Z * Y + q.Z * q.Z * Z - 2 * q.W * q.Y * X - q.Y * q.Y * Z + 2 * q.W * q.X * Y - q.X * q.X * Z + q.W * q.W * Z);
}

public readonly record struct Quat(double X, double Y, double Z, double W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);

    public static Quat FromAxisAngle(Coord axis, double angle)
    {
        axis = axis.Normalize();
        angle *= 0.5;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new Quat(axis.X * s, axis.Y * s, axis.Z * s, c).Normalize();
    }

    public Quat Normalize()
    {
        double mag = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        if (mag > 0.0000001)
        {
            double oomag = 1 / mag;
            return new(X * oomag, Y * oomag, Z * oomag, W * oomag);
        }
        return Identity;
    }

    public Quat Mul(Quat q2) => new(
        W * q2.X + X * q2.W + Y * q2.Z - Z * q2.Y,
        W * q2.Y - X * q2.Z + Y * q2.W + Z * q2.X,
        W * q2.Z + X * q2.Y - Y * q2.X + Z * q2.W,
        W * q2.W - X * q2.X - Y * q2.Y - Z * q2.Z);
}

public readonly record struct UVCoord(double U, double V)
{
    public UVCoord Flip() => new(1 - U, 1 - V);
}

internal readonly record struct ProfileFace(int V1, int V2, int V3);

/// <summary>One output triangle of a prim face, with per-vertex normals and UVs and the indices of its coordinates.</summary>
public sealed class ViewerFace(int primFaceNumber)
{
    public int PrimFaceNumber { get; set; } = primFaceNumber;
    public Coord V1, V2, V3;
    public Coord N1, N2, N3;
    public UVCoord Uv1, Uv2, Uv3;
    public int CoordIndex1 = -1, CoordIndex2 = -1, CoordIndex3 = -1;
    /// <summary>This triangle belongs to the bottom end cap. Geometry and UVs are unchanged;
    /// <see cref="PrimGeometry.Build"/> uses it to map the port's UVs to SL's texture orientation.</summary>
    public bool BottomCap;

    public void CalcSurfaceNormal()
    {
        var e1 = new Coord(V2.X - V1.X, V2.Y - V1.Y, V2.Z - V1.Z);
        var e2 = new Coord(V3.X - V1.X, V3.Y - V1.Y, V3.Z - V1.Z);
        var n = Coord.Cross(e1, e2).Normalize();
        N1 = N2 = N3 = n;
    }
}

internal readonly record struct Angle(double A, double X, double Y);

internal static class F
{
    /// <summary>JS Math.fround: round to single precision, keep computing in double.</summary>
    public static double Fr(double v) => (float)v;
    public static readonly double TwoPi = Fr(2 * Math.PI);
    public static int Trunc(double v) => (int)Math.Truncate(v);
}

internal sealed class AngleList
{
    // Immutable: process-wide constant tables, never session state (ADR-006, SessionInvariantTests).
    private static readonly ImmutableArray<Angle> Angles3 =
    [
        new(0.0, 1.0, 0.0), new(0.33333333333333333, -0.5, 0.86602540378443871), new(0.66666666666666667, -0.5, -0.86602540378443837), new(1.0, 1.0, 0.0),
    ];
    // Immutable: process-wide constant tables, never session state (ADR-006, SessionInvariantTests).
    private static readonly ImmutableArray<Coord> Normals3 =
    [
        new Coord(0.25, 0.4330127019, 0.0).Normalize(), new Coord(-0.5, 0.0, 0.0).Normalize(), new Coord(0.25, -0.4330127019, 0.0).Normalize(), new Coord(0.25, 0.4330127019, 0.0).Normalize(),
    ];
    // Immutable: process-wide constant tables, never session state (ADR-006, SessionInvariantTests).
    private static readonly ImmutableArray<Angle> Angles4 = [new(0.0, 1.0, 0.0), new(0.25, 0.0, 1.0), new(0.5, -1.0, 0.0), new(0.75, 0.0, -1.0), new(1.0, 1.0, 0.0)];
    // Immutable: process-wide constant tables, never session state (ADR-006, SessionInvariantTests).
    private static readonly ImmutableArray<Coord> Normals4 =
    [
        new Coord(0.5, 0.5, 0.0).Normalize(), new Coord(-0.5, 0.5, 0.0).Normalize(), new Coord(-0.5, -0.5, 0.0).Normalize(), new Coord(0.5, -0.5, 0.0).Normalize(), new Coord(0.5, 0.5, 0.0).Normalize(),
    ];
    // Immutable: process-wide constant tables, never session state (ADR-006, SessionInvariantTests).
    private static readonly ImmutableArray<Angle> Angles24 =
    [
        new(0.0, 1.0, 0.0), new(0.041666666666666664, 0.96592582628906831, 0.25881904510252074), new(0.083333333333333329, 0.86602540378443871, 0.5),
        new(0.125, 0.70710678118654757, 0.70710678118654746), new(0.16666666666666667, 0.5, 0.8660254037844386), new(0.20833333333333331, 0.25881904510252096, 0.9659258262890682),
        new(0.25, 0.0, 1.0), new(0.29166666666666663, -0.25881904510252063, 0.96592582628906831), new(0.33333333333333333, -0.5, 0.86602540378443871),
        new(0.375, -0.70710678118654746, 0.70710678118654757), new(0.41666666666666663, -0.86602540378443849, 0.5), new(0.45833333333333331, -0.9659258262890682, 0.25881904510252102),
        new(0.5, -1.0, 0.0), new(0.54166666666666663, -0.96592582628906842, -0.25881904510252035), new(0.58333333333333326, -0.86602540378443882, -0.5),
        new(0.62499999999999989, -0.70710678118654791, -0.70710678118654713), new(0.66666666666666667, -0.5, -0.86602540378443837), new(0.70833333333333326, -0.25881904510252152, -0.96592582628906809),
        new(0.75, 0.0, -1.0), new(0.79166666666666663, 0.2588190451025203, -0.96592582628906842), new(0.83333333333333326, 0.5, -0.86602540378443904),
        new(0.875, 0.70710678118654735, -0.70710678118654768), new(0.91666666666666663, 0.86602540378443837, -0.5), new(0.95833333333333326, 0.96592582628906809, -0.25881904510252157),
        new(1.0, 1.0, 0.0),
    ];

    public List<Angle> Angles = new();
    public List<Coord> Normals = new();
    private double _iX, _iY;

    private static Angle Interpolate(double newPoint, Angle p1, Angle p2)
    {
        double m = (newPoint - p1.A) / (p2.A - p1.A);
        return new(newPoint, p1.X + m * (p2.X - p1.X), p1.Y + m * (p2.Y - p1.Y));
    }

    private void Intersection(double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4)
    {
        double denom = (y4 - y3) * (x2 - x1) - (x4 - x3) * (y2 - y1);
        double uaNumerator = (x4 - x3) * (y1 - y3) - (y4 - y3) * (x1 - x3);
        if (denom != 0.0)
        {
            double ua = uaNumerator / denom;
            _iX = F.Fr(x1 + ua * (x2 - x1));
            _iY = F.Fr(y1 + ua * (y2 - y1));
        }
    }

    public void MakeAngles(int sides, double startAngle, double stopAngle)
    {
        Angles = new();
        Normals = new();
        double twoPi = Math.PI * 2.0;
        double twoPiInv = F.Fr(1.0 / twoPi);
        if (sides < 1) throw new ArgumentException("number of sides not greater than zero");
        if (stopAngle <= startAngle) throw new ArgumentException("stopAngle not greater than startAngle");

        if (sides is 3 or 4 or 24)
        {
            startAngle = F.Fr(startAngle * twoPiInv);
            stopAngle = F.Fr(stopAngle * twoPiInv);
            var source = sides == 3 ? Angles3 : sides == 4 ? Angles4 : Angles24;
            int startIndex = F.Trunc(startAngle * sides);
            int endIndex = source.Length - 1;
            if (stopAngle < 1.0) endIndex = F.Trunc(stopAngle * sides) + 1;
            if (endIndex == startIndex) endIndex++;
            for (int i = startIndex; i < endIndex + 1; i++)
            {
                Angles.Add(source[i]);
                if (sides == 3) Normals.Add(Normals3[i]);
                else if (sides == 4) Normals.Add(Normals4[i]);
            }
            if (startAngle > 0.0) Angles[0] = Interpolate(startAngle, Angles[0], Angles[1]);
            if (stopAngle < 1.0)
            {
                int last = Angles.Count - 1;
                Angles[last] = Interpolate(stopAngle, Angles[last - 1], Angles[last]);
            }
        }
        else
        {
            double stepSize = twoPi / sides;
            int startStep = F.Trunc(startAngle / stepSize);
            double angle = stepSize * startStep;
            int step = startStep;
            double stopAngleTest = stopAngle;
            if (stopAngle < twoPi)
            {
                stopAngleTest = stepSize * (F.Trunc(stopAngle / stepSize) + 1);
                if (stopAngleTest < stopAngle) stopAngleTest += stepSize;
                if (stopAngleTest > twoPi) stopAngleTest = twoPi;
            }
            while (angle <= stopAngleTest)
            {
                Angles.Add(new(F.Fr(angle), F.Fr(Math.Cos(F.Fr(angle))), F.Fr(Math.Sin(F.Fr(angle)))));
                step += 1;
                angle = stepSize * step;
            }
            if (startAngle > Angles[0].A)
            {
                Intersection(Angles[0].X, Angles[0].Y, Angles[1].X, Angles[1].Y, 0.0, 0.0, Math.Cos(startAngle), Math.Sin(startAngle));
                Angles[0] = new(startAngle, _iX, _iY);
            }
            int index = Angles.Count - 1;
            if (stopAngle < Angles[index].A)
            {
                Intersection(Angles[index - 1].X, Angles[index - 1].Y, Angles[index].X, Angles[index].Y, 0.0, 0.0, Math.Cos(stopAngle), Math.Sin(stopAngle));
                Angles[index] = new(stopAngle, _iX, _iY);
            }
        }
    }
}

/// <summary>Generates a profile for extrusion.</summary>
internal sealed class Profile
{
    public int BottomFaceNumber;
    public List<Coord> Coords = new();
    public Coord CutNormal1, CutNormal2;
    public string? ErrorMessage;
    public Coord FaceNormal = new(0.0, 0.0, 1.0);
    public List<int> FaceNumbers = new();
    public List<ProfileFace> Faces = new();
    public List<UVCoord> FaceUVs = new();
    public int HollowFaceNumber = -1;
    public int NumHollowVerts, NumOuterVerts, NumPrimFaces;
    public int OuterFaceNumber = -1;
    public List<double> Us = new();
    public List<Coord> VertexNormals = new();

    public Profile(int sides, double profileStart, double profileEnd, double hollow, int hollowSides)
    {
        var center = new Coord(0.0, 0.0, 0.0);
        var hollowCoords = new List<Coord>();
        var hollowNormals = new List<Coord>();
        var hollowUs = new List<double>();
        bool hasHollow = hollow > 0.0;
        bool hasProfileCut = profileStart > 0.0 || profileEnd < 1.0;
        var angles = new AngleList();
        var hollowAngles = new AngleList();
        double xScale = 0.5, yScale = 0.5;
        if (sides == 4)
        {
            xScale = 0.707107;   // corners of a square are sqrt(2) from center
            yScale = 0.707107;
        }
        double startAngle = F.Fr(profileStart * F.TwoPi);
        double stopAngle = F.Fr(profileEnd * F.TwoPi);
        try { angles.MakeAngles(sides, startAngle, stopAngle); }
        catch (ArgumentException ex) { ErrorMessage = "makeAngles failed: " + ex.Message; return; }

        NumOuterVerts = angles.Angles.Count;
        bool simpleFace = sides < 5 && !hasHollow && !hasProfileCut;   // as few triangles as possible for 3/4 sides

        if (hasHollow)
        {
            if (sides == hollowSides) hollowAngles = angles;
            else
            {
                try { hollowAngles.MakeAngles(hollowSides, startAngle, stopAngle); }
                catch (ArgumentException ex) { ErrorMessage = "makeAngles failed: " + ex.Message; return; }
            }
            NumHollowVerts = hollowAngles.Angles.Count;
        }
        else if (!simpleFace)
        {
            Coords.Add(center);
            VertexNormals.Add(new Coord(0.0, 0.0, 1.0));
            Us.Add(0.0);
        }

        const double z = 0.0;
        if (hasHollow && hollowSides != sides)
        {
            for (int i = 0; i < hollowAngles.Angles.Count; i++)
            {
                var angle = hollowAngles.Angles[i];
                hollowCoords.Add(new Coord(hollow * xScale * angle.X, hollow * yScale * angle.Y, z));
                hollowNormals.Add(hollowSides < 5 ? hollowAngles.Normals[i].Invert() : new Coord(-angle.X, -angle.Y, 0.0));
                hollowUs.Add(hollowSides == 4 ? angle.A * hollow * 0.707107 : angle.A * hollow);
            }
        }

        int index = 0;
        for (int i = 0; i < angles.Angles.Count; i++)
        {
            var angle = angles.Angles[i];
            var newVert = new Coord(angle.X * xScale, angle.Y * yScale, z);
            Coords.Add(newVert);
            VertexNormals.Add(sides < 5 ? angles.Normals[i] : new Coord(angle.X, angle.Y, 0.0));
            Us.Add(angle.A);
            if (hasHollow)
            {
                if (hollowSides == sides)
                {
                    hollowCoords.Add(new Coord(newVert.X * hollow, newVert.Y * hollow, z));
                    hollowNormals.Add(sides < 5 ? angles.Normals[i].Invert() : new Coord(-angle.X, -angle.Y, 0.0));
                    hollowUs.Add(angle.A * hollow);
                }
            }
            else if (!simpleFace && angle.A > 0.0001)
            {
                Faces.Add(new ProfileFace(0, index, index + 1));
            }
            index += 1;
        }

        if (hasHollow)
        {
            hollowCoords.Reverse();
            hollowNormals.Reverse();
            hollowUs.Reverse();
            int numTotalVerts = NumOuterVerts + NumHollowVerts;
            if (NumOuterVerts == NumHollowVerts)
            {
                for (int c = 0; c < NumOuterVerts - 1; c++)
                {
                    Faces.Add(new ProfileFace(c, c + 1, numTotalVerts - c - 1));
                    Faces.Add(new ProfileFace(c + 1, numTotalVerts - c - 2, numTotalVerts - c - 1));
                }
            }
            else if (NumOuterVerts < NumHollowVerts)
            {
                int j = 0, maxJ = NumOuterVerts - 1;   // j indexes outer vertices, i inner
                for (int i = 0; i < NumHollowVerts; i++)
                {
                    if (j < maxJ && angles.Angles[j + 1].A - hollowAngles.Angles[i].A < hollowAngles.Angles[i].A - angles.Angles[j].A + 0.000001)
                    {
                        Faces.Add(new ProfileFace(numTotalVerts - i - 1, j, j + 1));
                        j += 1;
                    }
                    Faces.Add(new ProfileFace(j, numTotalVerts - i - 2, numTotalVerts - i - 1));
                }
            }
            else
            {
                int j = 0, maxJ = NumHollowVerts - 1;   // j indexes inner vertices, i outer
                for (int i = 0; i < NumOuterVerts; i++)
                {
                    if (j < maxJ && hollowAngles.Angles[j + 1].A - angles.Angles[i].A < angles.Angles[i].A - hollowAngles.Angles[j].A + 0.000001)
                    {
                        Faces.Add(new ProfileFace(i, numTotalVerts - j - 2, numTotalVerts - j - 1));
                        j += 1;
                    }
                    Faces.Add(new ProfileFace(numTotalVerts - j - 1, i, i + 1));
                }
            }
            Coords.AddRange(hollowCoords);
            VertexNormals.AddRange(hollowNormals);
            Us.AddRange(hollowUs);
        }

        if (simpleFace)
        {
            if (sides == 3) Faces.Add(new ProfileFace(0, 1, 2));
            else if (sides == 4)
            {
                Faces.Add(new ProfileFace(0, 1, 2));
                Faces.Add(new ProfileFace(0, 2, 3));
            }
        }

        if (hasProfileCut)
        {
            int lastOuter = NumOuterVerts - 1;
            if (hasHollow)
            {
                CutNormal1 = new Coord(Coords[0].Y - Coords[^1].Y, -(Coords[0].X - Coords[^1].X), 0);
                CutNormal2 = new Coord(Coords[lastOuter + 1].Y - Coords[lastOuter].Y, -(Coords[lastOuter + 1].X - Coords[lastOuter].X), 0);
            }
            else
            {
                CutNormal1 = new Coord(VertexNormals[1].Y, -VertexNormals[1].X, 0);
                CutNormal2 = new Coord(-VertexNormals[^2].Y, VertexNormals[^2].X, 0);
            }
            CutNormal1 = CutNormal1.Normalize();
            CutNormal2 = CutNormal2.Normalize();
        }

        MakeFaceUVs();

        // prim face numbers, in the order top, outer, hollow, bottom, start cut, end cut
        int faceNum = 1;
        OuterFaceNumber = faceNum;
        int startVert = hasProfileCut && !hasHollow ? 1 : 0;
        if (startVert > 0) FaceNumbers.Add(-1);
        for (int i = 0; i < NumOuterVerts - 1; i++) FaceNumbers.Add(sides < 5 && i <= sides ? faceNum++ : faceNum);
        FaceNumbers.Add(hasProfileCut ? -1 : faceNum++);
        if (sides > 4 && (hasHollow || hasProfileCut)) faceNum++;
        if (sides < 5 && (hasHollow || hasProfileCut) && NumOuterVerts < sides) faceNum++;
        if (hasHollow)
        {
            for (int i = 0; i < NumHollowVerts; i++) FaceNumbers.Add(faceNum);
            HollowFaceNumber = faceNum++;
        }
        BottomFaceNumber = faceNum++;
        if (hasHollow && hasProfileCut) FaceNumbers.Add(faceNum++);
        for (int i = 0; i < FaceNumbers.Count; i++) if (FaceNumbers[i] == -1) FaceNumbers[i] = faceNum++;
        NumPrimFaces = faceNum;
    }

    public void MakeFaceUVs()
    {
        FaceUVs = Coords.Select(c => new UVCoord(1.0 - (0.5 + c.X), 1.0 - (0.5 - c.Y))).ToList();
    }

    public void AddRot(Quat q)
    {
        for (int i = 0; i < Coords.Count; i++) Coords[i] = Coords[i].MulQuat(q);
        for (int i = 0; i < VertexNormals.Count; i++) VertexNormals[i] = VertexNormals[i].MulQuat(q);
        FaceNormal = FaceNormal.MulQuat(q);
        CutNormal1 = CutNormal1.MulQuat(q);
        CutNormal2 = CutNormal2.MulQuat(q);
    }
}

public enum PathType { Linear = 0, Circular = 1, Flexible = 2 }

internal sealed class PathNode
{
    public Coord Position;
    public Quat Rotation = Quat.Identity;
    public double XScale = 1, YScale = 1, PercentOfPath;
}

internal sealed class PrimPath
{
    public double DimpleBegin, DimpleEnd = 1.0, HoleSizeX = 1.0, HoleSizeY = 0.25, PathCutBegin, PathCutEnd = 1.0;
    public double Radius, Revolutions = 1.0, Skew, TaperX, TaperY, TopShearX, TopShearY;
    public int StepsPerRevolution = 24;
    public double TwistBegin, TwistEnd;   // radians
    public readonly List<PathNode> Nodes = new();

    /// <summary>The steps given to <see cref="Create"/> are the whole count of a line path (no steps added for the twist).</summary>
    public bool StepsAreFinal;
    public void Create(PathType pathType, int steps)
    {
        TaperX = Math.Clamp(TaperX, -0.999, 0.999);
        TaperY = Math.Clamp(TaperY, -0.999, 0.999);
        if (pathType is PathType.Linear or PathType.Flexible)
        {
            int step = 0;
            double length = F.Fr(PathCutEnd - PathCutBegin);
            double twistTotal = F.Fr(TwistEnd - TwistBegin);
            double twistTotalAbs = Math.Abs(twistTotal);
            if (twistTotalAbs > 0.01 && !StepsAreFinal) steps += F.Trunc(twistTotalAbs * 3.66);   // dahlia's magic number (the official count, when given, is the whole)
            const double start = -0.5;
            double stepSize = F.Fr(length / steps);
            double percentOfPathMultiplier = F.Fr(stepSize * 0.999999);
            double xOffset = F.Fr(TopShearX * PathCutBegin);
            double yOffset = F.Fr(TopShearY * PathCutBegin);
            double xInc = F.Fr(TopShearX * length / steps);
            double yInc = F.Fr(TopShearY * length / steps);
            double percentOfPath = F.Fr(PathCutBegin);
            double zOffset = F.Fr(start + percentOfPath);
            bool done = false;
            while (!done)
            {
                var n = new PathNode
                {
                    XScale = TaperX == 0.0 ? 1.0 : TaperX > 0.0 ? 1.0 - percentOfPath * TaperX : 1.0 + (1.0 - percentOfPath) * TaperX,
                    YScale = TaperY == 0.0 ? 1.0 : TaperY > 0.0 ? 1.0 - percentOfPath * TaperY : 1.0 + (1.0 - percentOfPath) * TaperY,
                };
                double twist = TwistBegin + twistTotal * percentOfPath;
                n.Rotation = Quat.FromAxisAngle(new Coord(0.0, 0.0, 1.0), twist);
                n.Position = new Coord(xOffset, yOffset, zOffset);
                n.PercentOfPath = percentOfPath;
                Nodes.Add(n);
                if (step < steps)
                {
                    step += 1;
                    percentOfPath = F.Fr(percentOfPath + percentOfPathMultiplier);
                    xOffset = F.Fr(xOffset + xInc);
                    yOffset = F.Fr(yOffset + yInc);
                    zOffset = F.Fr(zOffset + stepSize);
                    if (percentOfPath > PathCutEnd) done = true;
                }
                else done = true;
            }
        }
        else
        {
            double twistTotal = F.Fr(TwistEnd - TwistBegin);
            double twistTotalAbs = Math.Abs(twistTotal);   // a lot of twist: more layers, or they may overlap
            if (twistTotalAbs > 0.01)
            {
                if (twistTotalAbs > Math.PI * 1.5) steps *= 2;
                if (twistTotalAbs > Math.PI * 3.0) steps *= 2;
            }
            double yPathScale = F.Fr(HoleSizeY * 0.5);
            double pathLength = F.Fr(PathCutEnd - PathCutBegin);
            double totalSkew = F.Fr(Skew * 2.0 * pathLength);
            double skewStart = F.Fr(PathCutBegin * 2.0 * Skew - Skew);
            double xOffsetTopShearXFactor = F.Fr(TopShearX * (0.25 + 0.5 * (0.5 - HoleSizeY)));
            double yShearCompensation = F.Fr(1.0 + Math.Abs(TopShearY) * 0.25);
            double startAngle = F.Fr(F.TwoPi * PathCutBegin * Revolutions - TopShearY * 0.9);
            double endAngle = F.Fr(F.TwoPi * PathCutEnd * Revolutions - TopShearY * 0.9);
            double stepSize = F.Fr(F.TwoPi / StepsPerRevolution);
            int step = F.Trunc(startAngle / stepSize);
            double angle = startAngle;
            bool done = false;
            while (!done)
            {
                var n = new PathNode();
                double xProfileScale = F.Fr((1.0 - Math.Abs(Skew)) * HoleSizeX);
                double yProfileScale = HoleSizeY;
                double percentOfPath = F.Fr(angle / (F.TwoPi * Revolutions));
                double percentOfAngles = F.Fr((angle - startAngle) / (endAngle - startAngle));
                if (TaperX > 0.01) xProfileScale *= 1.0 - percentOfPath * TaperX;
                else if (TaperX < -0.01) xProfileScale *= 1.0 + (1.0 - percentOfPath) * TaperX;
                if (TaperY > 0.01) yProfileScale *= 1.0 - percentOfPath * TaperY;
                else if (TaperY < -0.01) yProfileScale *= 1.0 + (1.0 - percentOfPath) * TaperY;
                n.XScale = xProfileScale;
                n.YScale = yProfileScale;
                double radiusScale = 1.0;
                if (Radius > 0.001) radiusScale = 1.0 - Radius * percentOfPath;
                else if (Radius < 0.001) radiusScale = 1.0 + Radius * (1.0 - percentOfPath);
                double twist = TwistBegin + twistTotal * percentOfPath;
                double xOffset = 0.5 * (skewStart + totalSkew * percentOfAngles);
                xOffset += Math.Sin(angle) * xOffsetTopShearXFactor;
                double yOffset = yShearCompensation * Math.Cos(angle) * (0.5 - yPathScale) * radiusScale;
                double zOffset = Math.Sin(angle + TopShearY) * (0.5 - yPathScale) * radiusScale;
                n.Position = new Coord(xOffset, yOffset, zOffset);
                // orient the profile layer to its position on the path, then apply the twist
                n.Rotation = Quat.FromAxisAngle(new Coord(1.0, 0.0, 0.0), angle + TopShearY);
                if (twistTotal != 0.0 || TwistBegin != 0.0) n.Rotation = n.Rotation.Mul(Quat.FromAxisAngle(new Coord(0.0, 0.0, 1.0), twist));
                n.PercentOfPath = percentOfPath;
                Nodes.Add(n);
                if (angle >= endAngle - 0.01) done = true;
                else
                {
                    step += 1;
                    angle = F.Fr(stepSize * step);
                    if (angle > endAngle) angle = endAngle;
                }
            }
        }
    }
}

/// <summary>A prim's mesh: a profile extruded along a path, producing <see cref="ViewerFaces"/>.</summary>
public sealed class PrimMesh
{
    private readonly double _hollow, _profileEnd, _profileStart;
    private readonly int _hollowSides, _sides;
    private readonly PrimMesherRegression _regressions;

    public double DimpleBegin, DimpleEnd = 1.0, HoleSizeX = 1.0, HoleSizeY = 0.25, PathCutBegin, PathCutEnd = 1.0;
    public double Radius, Revolutions = 1.0, Skew, TaperX, TaperY, TopShearX, TopShearY;
    public int StepsPerRevolution = 24;
    /// <summary>Steps of a line path; null: from the twist (the mesher's own rule).</summary>
    public int? LinearSteps;
    public bool SphereMode;
    /// <summary>Degrees (integer, as the prim mapping produces).</summary>
    public int TwistBegin, TwistEnd;

    public List<Coord> Coords { get; private set; } = new();
    public List<Coord> Normals { get; private set; } = new();
    public List<ViewerFace> ViewerFaces { get; private set; } = new();
    public string ErrorMessage { get; private set; } = "";
    public int NumPrimFaces { get; private set; }
    public int ProfileOuterFaceNumber { get; private set; } = -1;
    public int ProfileHollowFaceNumber { get; private set; } = -1;
    /// <summary>Prim face number of the bottom end cap (only when end faces were generated), else -1.</summary>
    public int BottomFaceNumber { get; private set; } = -1;
    public bool HasProfileCut { get; }
    public bool HasHollow { get; }

    public PrimMesh(int sides, double profileStart, double profileEnd, double hollow, int hollowSides, PrimMesherRegression regressions = PrimMesherRegression.None)
    {
        _regressions = regressions;
        _sides = sides < 3 ? 3 : sides;
        _hollowSides = hollowSides < 3 ? 3 : hollowSides;
        _profileStart = profileStart < 0.0 ? 0.0 : profileStart;
        _profileEnd = profileEnd > 1.0 ? 1.0 : profileEnd;
        if (_profileEnd < 0.02) _profileEnd = 0.02;
        if (_profileStart >= _profileEnd) _profileStart = _profileEnd - 0.02;
        _hollow = hollow > 0.99 ? 0.99 : hollow < 0.0 ? 0.0 : hollow;
        // Fix (1): the original semantics. 3.1.4 never assigned these (both stayed false).
        if (!regressions.HasFlag(PrimMesherRegression.ProfileFlagsNeverSet))
        {
            HasProfileCut = _profileStart > 0.0 || _profileEnd < 1.0;
            HasHollow = _hollow > 0.0;
        }
    }

    /// <summary>Extrudes the profile along a path (viewer mode, with vertex normals).</summary>
    public void Extrude(PathType pathType)
    {
        Coords = new();
        ViewerFaces = new();
        Normals = new();
        int steps = 1;

        // Fix (2): PrimMesh twist fields are degrees; the path works in radians. 3.1.4 used the degrees as radians.
        bool degreesAsRadians = _regressions.HasFlag(PrimMesherRegression.TwistDegreesAsRadians);
        double twistBegin = degreesAsRadians ? TwistBegin : F.Fr(TwistBegin / 360.0 * F.TwoPi);
        double twistEnd = degreesAsRadians ? TwistEnd : F.Fr(TwistEnd / 360.0 * F.TwoPi);
        double twistTotal = F.Fr(twistEnd - twistBegin);
        double twistTotalAbs = Math.Abs(twistTotal);
        if (twistTotalAbs > 0.01) steps += F.Trunc(twistTotalAbs * 3.66);   // dahlia's magic number
        if (LinearSteps is { } officialSteps && pathType == PathType.Linear) steps = Math.Max(1, officialSteps);   // the official viewer's count for the level of detail (PrimGeometry)

        double hollow = _hollow;
        bool needEndFaces = pathType != PathType.Circular
            || PathCutBegin != 0.0 || PathCutEnd != 1.0 || TaperX != 0.0 || TaperY != 0.0 || Skew != 0.0 || twistTotal != 0.0 || Radius != 0.0;

        // sanity checks
        double initialProfileRot = 0.0;
        if (pathType == PathType.Circular)
        {
            switch (_sides)
            {
                case 3:
                    initialProfileRot = Math.PI;
                    if (_hollowSides == 4) { if (hollow > 0.7) hollow = 0.7; hollow *= 0.707; }
                    else hollow *= 0.5;
                    break;
                case 4:
                    initialProfileRot = 0.25 * Math.PI;
                    if (_hollowSides != 4) hollow *= 0.707;
                    break;
                default:
                    if (_sides > 4)
                    {
                        initialProfileRot = Math.PI;
                        if (_hollowSides == 4) { if (hollow > 0.7) hollow = 0.7; hollow /= 0.7; }
                    }
                    break;
            }
        }
        else
        {
            switch (_sides)
            {
                case 3:
                    if (_hollowSides == 4) { if (hollow > 0.7) hollow = 0.7; hollow *= 0.707; }
                    else hollow *= 0.5;
                    break;
                case 4:
                    initialProfileRot = 1.25 * Math.PI;
                    if (_hollowSides != 4) hollow *= 0.707;
                    break;
                case 24:
                    if (_hollowSides == 4) hollow *= 1.414;
                    break;
            }
        }
        hollow = F.Fr(hollow);

        var profile = new Profile(_sides, _profileStart, _profileEnd, hollow, _hollowSides);
        ErrorMessage = profile.ErrorMessage ?? "";
        NumPrimFaces = profile.NumPrimFaces;
        bool emitBottom = !_regressions.HasFlag(PrimMesherRegression.NoBottomCap);   // Fix (3)
        BottomFaceNumber = needEndFaces && emitBottom ? profile.BottomFaceNumber : -1;

        int cut1FaceNumber = profile.BottomFaceNumber + 1;
        int cut2FaceNumber = cut1FaceNumber + 1;
        if (!needEndFaces)
        {
            cut1FaceNumber -= 2;
            cut2FaceNumber -= 2;
        }
        ProfileOuterFaceNumber = profile.OuterFaceNumber - (needEndFaces ? 0 : 1);
        if (HasHollow)
            ProfileHollowFaceNumber = profile.HollowFaceNumber - (needEndFaces ? 0 : 1);

        int cut1Vert = -1, cut2Vert = -1;
        if (HasProfileCut)
        {
            cut1Vert = HasHollow ? profile.Coords.Count - 1 : 0;
            cut2Vert = HasHollow ? profile.NumOuterVerts - 1 : profile.NumOuterVerts;
        }

        if (initialProfileRot != 0.0)
        {
            profile.AddRot(Quat.FromAxisAngle(new Coord(0.0, 0.0, 1.0), initialProfileRot));
            profile.MakeFaceUVs();
        }

        Coord lastCutNormal1 = default, lastCutNormal2 = default;
        double lastV = 0.0;

        var path = new PrimPath
        {
            TwistBegin = twistBegin, TwistEnd = twistEnd, TopShearX = TopShearX, TopShearY = TopShearY, PathCutBegin = PathCutBegin,
            PathCutEnd = PathCutEnd, DimpleBegin = DimpleBegin, DimpleEnd = DimpleEnd, Skew = Skew, HoleSizeX = HoleSizeX, HoleSizeY = HoleSizeY,
            TaperX = TaperX, TaperY = TaperY, Radius = Radius, Revolutions = Revolutions, StepsPerRevolution = StepsPerRevolution, StepsAreFinal = LinearSteps is not null,
        };
        path.Create(pathType, steps);

        var normals = Normals;
        var coords = Coords;
        for (int nodeIndex = 0; nodeIndex < path.Nodes.Count; nodeIndex++)
        {
            var node = path.Nodes[nodeIndex];
            int profileCount = profile.Coords.Count;
            int coordsLen = coords.Count;
            var tmp = new Coord[profileCount];
            for (int ci = 0; ci < profileCount; ci++)
            {
                var p = profile.Coords[ci];
                var v = new Coord(p.X * node.XScale, p.Y * node.YScale, p.Z).MulQuat(node.Rotation);
                tmp[ci] = new Coord(v.X + node.Position.X, v.Y + node.Position.Y, v.Z + node.Position.Z);
            }
            coords.AddRange(tmp);
            foreach (var vn in profile.VertexNormals) normals.Add(vn.MulQuat(node.Rotation));

            // side faces between layers
            int numVerts = profileCount;
            double thisV = 1.0 - node.PercentOfPath;
            if (nodeIndex > 0)
            {
                int startVert = coordsLen + 1;
                int endVert = coords.Count;
                if (_sides < 5 || HasProfileCut || HasHollow) startVert--;
                for (int i = startVert; i < endVert; i++)
                {
                    int iNext = i == endVert - 1 ? startVert : i + 1;
                    int whichVert = i - startVert;
                    int f1v1 = i, f1v2 = i - numVerts, f1v3 = iNext;
                    int f2v1 = iNext, f2v2 = i - numVerts, f2v3 = iNext - numVerts;

                    int primFaceNum = 0;
                    if (whichVert >= 0 && whichVert < profile.FaceNumbers.Count) primFaceNum = profile.FaceNumbers[whichVert];
                    else if (profile.FaceNumbers.Count > 0) primFaceNum = profile.FaceNumbers[0];
                    if (!needEndFaces) primFaceNum = Math.Max(0, primFaceNum - 1);
                    var vf1 = new ViewerFace(primFaceNum);
                    var vf2 = new ViewerFace(primFaceNum);

                    int uIndex = whichVert;
                    if (!HasHollow && _sides > 4 && uIndex < profile.Us.Count - 1) uIndex++;
                    if (profile.Us.Count > 0)
                    {
                        uIndex = Math.Clamp(uIndex, 0, profile.Us.Count - 1);
                        double u1 = profile.Us[uIndex];
                        double u2 = uIndex < profile.Us.Count - 1 ? profile.Us[uIndex + 1] : 1.0;
                        if (whichVert == cut1Vert || whichVert == cut2Vert)
                        {
                            u1 = 0;
                            u2 = 1;
                        }
                        else if (_sides < 5 && whichVert < profile.NumOuterVerts)
                        {
                            u1 *= _sides;
                            u2 *= _sides;
                            u2 -= F.Trunc(u1);
                            u1 -= F.Trunc(u1);
                            if (u2 < 0.1) u2 = 1;
                        }
                        if (SphereMode && whichVert != cut1Vert && whichVert != cut2Vert)
                        {
                            u1 = u1 * 2 - 1;
                            u2 = u2 * 2 - 1;
                            if (whichVert >= profile.NumOuterVerts)
                            {
                                u1 -= hollow;
                                u2 -= hollow;
                            }
                        }
                        vf1.Uv1 = new UVCoord(u1, thisV);
                        vf1.Uv2 = new UVCoord(u1, lastV);
                        vf1.Uv3 = new UVCoord(u2, thisV);
                        vf2.Uv1 = new UVCoord(u2, thisV);
                        vf2.Uv2 = new UVCoord(u1, lastV);
                        vf2.Uv3 = new UVCoord(u2, lastV);
                    }

                    vf1.V1 = coords[f1v1]; vf1.V2 = coords[f1v2]; vf1.V3 = coords[f1v3];
                    vf2.V1 = coords[f2v1]; vf2.V2 = coords[f2v2]; vf2.V3 = coords[f2v3];
                    vf1.CoordIndex1 = f1v1; vf1.CoordIndex2 = f1v2; vf1.CoordIndex3 = f1v3;
                    vf2.CoordIndex1 = f2v1; vf2.CoordIndex2 = f2v2; vf2.CoordIndex3 = f2v3;

                    if (whichVert == cut1Vert)
                    {
                        var tcn1 = profile.CutNormal1.MulQuat(node.Rotation);
                        vf1.PrimFaceNumber = vf2.PrimFaceNumber = cut1FaceNumber;
                        vf1.N1 = tcn1; vf1.N2 = lastCutNormal1; vf1.N3 = lastCutNormal1;
                        vf2.N1 = tcn1; vf2.N3 = tcn1; vf2.N2 = lastCutNormal1;
                    }
                    else if (whichVert == cut2Vert)
                    {
                        var tcn2 = profile.CutNormal2.MulQuat(node.Rotation);
                        vf1.PrimFaceNumber = vf2.PrimFaceNumber = cut2FaceNumber;
                        vf1.N1 = tcn2; vf1.N2 = lastCutNormal2; vf1.N3 = lastCutNormal2;
                        vf2.N1 = tcn2; vf2.N3 = tcn2; vf2.N2 = lastCutNormal2;
                    }
                    else
                    {
                        bool perVertex = (_sides < 5 && whichVert < profile.NumOuterVerts) || (_hollowSides < 5 && whichVert >= profile.NumOuterVerts);
                        if (perVertex || normals.Count == 0)
                        {
                            vf1.CalcSurfaceNormal();
                            vf2.CalcSurfaceNormal();
                        }
                        else
                        {
                            Coord Pick(int idx) => normals[idx < 0 || idx >= normals.Count ? 0 : idx];
                            vf1.N1 = Pick(f1v1); vf1.N2 = Pick(f1v2); vf1.N3 = Pick(f1v3);
                            vf2.N1 = Pick(f2v1); vf2.N2 = Pick(f2v2); vf2.N3 = Pick(f2v3);
                        }
                    }
                    ViewerFaces.Add(vf1);
                    ViewerFaces.Add(vf2);
                }
            }

            lastCutNormal1 = profile.CutNormal1;
            lastCutNormal2 = profile.CutNormal2;
            lastV = thisV;

            // end caps: bottom on the first layer, top on the last
            bool isTop = nodeIndex == path.Nodes.Count - 1;
            if (needEndFaces && (nodeIndex == 0 || isTop) && (isTop || emitBottom))
            {
                var faceNormal = isTop ? profile.FaceNormal.MulQuat(node.Rotation) : profile.FaceNormal.MulQuat(node.Rotation).Invert();
                int primFace = isTop ? 0 : profile.BottomFaceNumber;
                foreach (var f in profile.Faces)
                {
                    var nv = new ViewerFace(primFace);
                    // the bottom cap winds the other way so it faces outward
                    int a = isTop ? f.V1 : f.V3, c = isTop ? f.V3 : f.V1;
                    nv.V1 = tmp[a]; nv.V2 = tmp[f.V2]; nv.V3 = tmp[c];
                    nv.CoordIndex1 = a + coordsLen; nv.CoordIndex2 = f.V2 + coordsLen; nv.CoordIndex3 = c + coordsLen;
                    nv.Uv1 = profile.FaceUVs[a]; nv.Uv2 = profile.FaceUVs[f.V2]; nv.Uv3 = profile.FaceUVs[c];
                    nv.N1 = nv.N2 = nv.N3 = faceNormal;
                    nv.BottomCap = !isTop;
                    if (pathType == PathType.Linear)
                    {
                        nv.Uv1 = nv.Uv1.Flip();
                        nv.Uv2 = nv.Uv2.Flip();
                        nv.Uv3 = nv.Uv3.Flip();
                    }
                    ViewerFaces.Add(nv);
                }
            }
        }
    }
}

// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llvovolume.cpp
 * @brief LLVOVolume class implementation
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
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

/**
 * @file llvolumemgr.cpp
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

/* Adapted 2026 by Legion Builds: MeshLodRule.Detail is adapted from calcLOD and computeLODDetail in llvovolume.cpp and the detail thresholds in
 * llvolumemgr.cpp, translated to C#. */

namespace Viewer.Core.Meshing;

/// <summary>One mesh face (a texture-entry slot): object-space positions (SL axes, roughly -0.5..0.5), normals, UVs, triangle indices.</summary>
public sealed record MeshFace(float[] Positions, float[] Normals, float[] Uvs, int[] Indices)
{
    public int Vertices => Positions.Length / 3;
    public int Triangles => Indices.Length / 3;
}

/// <summary>One level of detail: its faces in texture-entry order.</summary>
public sealed record MeshLod(IReadOnlyList<MeshFace> Faces)
{
    public int Triangles => Faces.Sum(f => f.Triangles);
}

/// <summary>Skin data: three counts for every rigged mesh, and, when the decoder read it, the whole skin (joint names, bind matrices
/// and each face's influences, <see cref="MeshSkinReader"/>), which is what a rigged mesh worn on an avatar is drawn from.</summary>
public sealed record MeshSkin(int JointCount, bool HasBindShapeMatrix, int WeightedVertices, MeshSkinData? Data = null);

/// <summary>
/// A decoded SL mesh asset: up to four LODs, index 0 = lowest... 3 = high (the official viewer's detail levels
/// 0-3); a missing LOD is null. Plain core type: no LibreMetaverse type (ADR-005).
/// </summary>
public sealed record MeshGeometry(IReadOnlyList<MeshLod?> Lods, MeshSkin? Skin)
{
    public const int LodCount = 4;

    /// <summary>The LOD to draw for a wanted detail: that one if present, else the nearest higher, else the nearest lower.</summary>
    public int Resolve(int detail)
    {
        detail = Math.Clamp(detail, 0, LodCount - 1);
        for (int d = detail; d < LodCount; d++)
            if (Lods[d] is not null) return d;
        for (int d = detail - 1; d >= 0; d--)
            if (Lods[d] is not null) return d;
        return -1;
    }

    /// <summary>Which LODs the asset has, bit d for LOD d (kept after its decoded geometry is let go,
    /// so the LOD rule still resolves without decoding it again).</summary>
    public byte LodMask
    {
        get
        {
            byte m = 0;
            for (int d = 0; d < LodCount; d++)
                if (Lods[d] is not null) m |= (byte)(1 << d);
            return m;
        }
    }

    /// <summary><see cref="Resolve(int)"/> from the LODs an asset has (<see cref="LodMask"/>): the same answer.</summary>
    public static int Resolve(byte lods, int detail)
    {
        detail = Math.Clamp(detail, 0, LodCount - 1);
        for (int d = detail; d < LodCount; d++)
            if ((lods & (1 << d)) != 0) return d;
        for (int d = detail - 1; d >= 0; d--)
            if ((lods & (1 << d)) != 0) return d;
        return -1;
    }
}

/// <summary>
/// SL's LOD choice, as the official viewer computes it (secondlife/viewer: LLVOVolume::calcLOD + computeLODDetail,
/// LLVolumeLODGroup::getDetailFromTan in llvolumemgr.cpp, the distance factor in llappviewer.cpp; read):
///   lodFactor = the RenderVolumeLODFactor setting, clamped to 0.01..4;
///   radius = |scale * lodScaleBias| (bias 0.5 for meshes, LLVolume::generate), distance = to the camera;
///   distance *= 1 - 0.1 * lodFactor (the distance factor); near ramp: if distance &lt; 2 * lodFactor,
///   distance = (distance / ramp)^2 * ramp; distance *= pi / 3; both rounded to 0.01;
///   the factor is scaled by the default field of view (60 degrees) over the camera's (zoom keeps detail);
///   tan = factor * radius / distance, rounded to 0.01;
///   detail = the first i with tan &lt;= threshold[i] of {0.03, 0.06, 0.24, 3.0}, else 3.
/// The default factor here is 1.75, the official viewer's HighUltra graphics level (its feature table: Low and LowMid 1.125, Mid 1.25, MidHigh 1.375,
/// High 1.5, HighUltra 1.75, Ultra 2.0; the level a graphics card of the top class gets), used until the viewer has a rule that picks the level from the graphics card; a viewer setting changes it.
/// </summary>
public static class MeshLodRule
{
    public const float DefaultLodFactor = 1.75f;
    public const float MinLodFactor = 0.01f, MaxLodFactor = 4f;
    /// <summary>The official viewer's default vertical field of view (60 degrees), in radians.</summary>
    public const float DefaultFovY = MathF.PI / 3f;
    public const float MeshScaleBias = 0.5f;
    private static readonly System.Collections.Immutable.ImmutableArray<float> Thresholds = [0.03f, 0.06f, 0.24f, 3.0f];   // immutable

    public static float Radius(System.Numerics.Vector3 scale, float bias = MeshScaleBias) => (scale * bias).Length();

    /// <summary>The factor as the official viewer clamps it (0.01..4; not a number: the default).</summary>
    public static float Clamp(float lodFactor) => float.IsNaN(lodFactor) ? DefaultLodFactor : Math.Clamp(lodFactor, MinLodFactor, MaxLodFactor);

    /// <summary>A factor from a setting's text (invariant culture), clamped; null when it is absent or not a number.</summary>
    public static float? Parse(string? text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && !float.IsNaN(f) ? Clamp(f) : null;

    /// <param name="fovY">The camera's vertical field of view in radians (0 or less: the default, no scaling).</param>
    public static int Detail(float distance, float radius, float lodFactor = DefaultLodFactor, float fovY = DefaultFovY)
    {
        if (distance <= 0f || radius <= 0f)
            return MeshGeometry.LodCount - 1;
        lodFactor = Clamp(lodFactor);
        distance *= 1f - lodFactor * 0.1f;
        float ramp = lodFactor * 2f;
        if (distance < ramp)
        {
            distance /= ramp;
            distance *= distance;
            distance *= ramp;
        }
        distance *= MathF.PI / 3f;
        float scaled = fovY > 0f ? lodFactor * (DefaultFovY / fovY) : lodFactor;
        float d = Round(distance), r = Round(radius);
        if (d <= 0f)
            return MeshGeometry.LodCount - 1;
        float tan = Round(scaled * r / d);
        for (int i = 0; i < MeshGeometry.LodCount - 1; i++)
            if (tan <= Thresholds[i])
                return i;
        return MeshGeometry.LodCount - 1;
    }

    /// <summary>ll_round(v, 0.01): to the nearest multiple of 0.01.</summary>
    private static float Round(float v) => MathF.Floor(v / 0.01f + 0.5f) * 0.01f;
}

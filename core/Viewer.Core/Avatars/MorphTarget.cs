// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llpolymorph.cpp
 * @brief Implementation of LLPolyMesh class
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
 * @file llpolymesh.cpp
 * @brief Implementation of LLPolyMesh class
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
 * @file llpolyskeletaldistortion.cpp
 * @brief Implementation of LLPolySkeletalDistortion classes
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

// Adapted 2026 by Legion Builds. The morph section of a body mesh file (LLPolyMorphData::loadBinary, the section loop of
// LLPolyMeshSharedData::loadMesh) and the copies the loader makes of five morphs for the physics-driven parameters
// (clone_morph_param_duplicate, _direction and _cleavage) follow the files named above, rewritten in C#. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Viewer.Core.Avatars;

/// <summary>
/// One morph of a body mesh: for each vertex it moves, the offset added to the vertex's position per unit of weight, the
/// offset added to its normal (before softening) and its texture-coordinate offset. Read from the morph section that follows the
/// skin joint names of a full-detail body mesh file: records of a 64-byte name and a vertex count, then per vertex a 32-bit index, the position, normal and
/// binormal offsets (three floats each) and a UV offset (two floats), until the name "End Morphs".
/// </summary>
public sealed class MorphTarget
{
    public const int NameBytes = 64, MaxVertexIndex = 10000;
    public const string EndName = "End Morphs";

    public required string Name { get; init; }
    public required int[] Indices { get; init; }
    public required Vector3[] Coords { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }

    /// <summary>The morphs from <paramref name="b"/> (the bytes after the skin joint names), with the loader's copies for the physics-driven
    /// parameters. Fewer bytes than a name left, or the end marker, ends the list; a record cut short or a vertex index past
    /// <see cref="MaxVertexIndex"/> (which the official loader rejects) makes the file unreadable.</summary>
    internal static Dictionary<string, MorphTarget> ReadAll(ReadOnlySpan<byte> b)
    {
        var result = new Dictionary<string, MorphTarget>(StringComparer.Ordinal);
        int o = 0;
        while (b.Length - o >= NameBytes)
        {
            var raw = b.Slice(o, NameBytes);
            o += NameBytes;
            int z = raw.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(z < 0 ? raw : raw[..z]);
            if (name == EndName) break;
            if (b.Length - o < 4) throw new InvalidDataException($"morph '{name}' truncated before its vertex count");
            int count = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4));
            o += 4;
            if (count < 0 || (long)count * RecordBytes > b.Length - o) throw new InvalidDataException($"morph '{name}' truncated ({count} vertices wanted, {b.Length - o} bytes left)");
            var t = new MorphTarget
            {
                Name = name,
                Indices = new int[count],
                Coords = new Vector3[count],
                Normals = new Vector3[count],
                Uvs = new Vector2[count],
            };
            for (int i = 0; i < count; i++)
            {
                var r = b.Slice(o, RecordBytes);
                o += RecordBytes;
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(r);
                if (index > MaxVertexIndex) throw new InvalidDataException($"morph '{name}' vertex {i}: bad vertex index {index}");
                t.Indices[i] = (int)index;
                t.Coords[i] = Vec(r[4..]);
                t.Normals[i] = Vec(r[16..]);
                // 12 bytes of binormal offsets follow: the body is not shaded with binormals
                t.Uvs[i] = new(BinaryPrimitives.ReadSingleLittleEndian(r[40..]), BinaryPrimitives.ReadSingleLittleEndian(r[44..]));
            }
            result[name] = t;
            AddDrivenCopies(result, t);
        }
        return result;
    }

    /// <summary>One vertex record: an index, three offsets, three normal offsets, three binormal offsets and two uv offsets.</summary>
    private const int RecordBytes = 4 + 12 + 12 + 12 + 8;

    private static Vector3 Vec(ReadOnlySpan<byte> r) => new(
        BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..]));

    /// <summary>The copies the loader makes of a few morphs, which the physics parameters of the breasts, belly and butt use.</summary>
    private static void AddDrivenCopies(Dictionary<string, MorphTarget> all, MorphTarget m)
    {
        switch (m.Name)
        {
            case "Breast_Female_Cleavage":
                all["Breast_Physics_LeftRight_Driven"] = Scaled(m, 0.75f, "Breast_Physics_LeftRight_Driven");
                all["Breast_Physics_InOut_Driven"] = Duplicate(m, "Breast_Physics_InOut_Driven");
                break;
            case "Breast_Gravity": all["Breast_Physics_UpDown_Driven"] = Duplicate(m, "Breast_Physics_UpDown_Driven"); break;
            case "Big_Belly_Torso": all["Belly_Physics_Torso_UpDown_Driven"] = Direction(m, new(0, 0, 0.05f), "Belly_Physics_Torso_UpDown_Driven"); break;
            case "Big_Belly_Legs": all["Belly_Physics_Legs_UpDown_Driven"] = Direction(m, new(0, 0, 0.05f), "Belly_Physics_Legs_UpDown_Driven"); break;
            case "skirt_belly": all["Belly_Physics_Skirt_UpDown_Driven"] = Direction(m, new(0, 0, 0.05f), "Belly_Physics_Skirt_UpDown_Driven"); break;
            case "Small_Butt":
                all["Butt_Physics_UpDown_Driven"] = Direction(m, new(0, 0, 0.05f), "Butt_Physics_UpDown_Driven");
                all["Butt_Physics_LeftRight_Driven"] = Direction(m, new(0, 0.03f, 0), "Butt_Physics_LeftRight_Driven");
                break;
        }
    }

    private static MorphTarget Duplicate(MorphTarget m, string name) => With(m, name, (Vector3[])m.Coords.Clone(), (Vector3[])m.Normals.Clone());

    /// <summary>Every vertex moves by the same offset and its normal does not change.</summary>
    private static MorphTarget Direction(MorphTarget m, Vector3 offset, string name) =>
        With(m, name, Enumerable.Repeat(offset, m.Coords.Length).ToArray(), new Vector3[m.Normals.Length]);

    /// <summary>Offsets and normal offsets times <paramref name="scale"/>, with the y component mirrored where the vertex's own offset has a negative y.</summary>
    private static MorphTarget Scaled(MorphTarget m, float scale, string name)
    {
        var c = new Vector3[m.Coords.Length];
        var n = new Vector3[m.Normals.Length];
        for (int i = 0; i < c.Length; i++)
        {
            var s = m.Coords[i].Y < 0 ? new Vector3(scale, -scale, scale) : new Vector3(scale);
            c[i] = m.Coords[i] * s;
            n[i] = m.Normals[i] * s;
        }
        return With(m, name, c, n);
    }

    private static MorphTarget With(MorphTarget m, string name, Vector3[] coords, Vector3[] normals) =>
        new() { Name = name, Indices = m.Indices, Coords = coords, Normals = normals, Uvs = m.Uvs };
}

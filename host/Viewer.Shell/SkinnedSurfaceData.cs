// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>
/// A rigged mesh face in the engine's own surface layout (<see cref="MeshSurfaceData"/>), for a face shader that skins it from a
/// texture of joint matrices: the ordinary face, plus each vertex's four joint indices (custom channel 0) and four weights (custom
/// channel 1), each channel four half floats, appended to the vertex's attribute data after its UV (the engine's attribute order:
/// colour, UV, second UV, custom 0 to 3). The joint indices count into the mesh's joint list (110 at most, exact in half
/// precision); a weight of 0 marks an unused slot. The engine's own skinning data (bones and weights arrays) is not used.
/// Pure computation on the face's arrays; safe on any thread.
/// </summary>
public static class SkinnedSurfaceData
{
    // the engine's surface format flags (RenderingServer.ArrayFormat): custom 0 and 1 present, and each one's format in three bits
    // from bit 13 (custom 0) and bit 16 (custom 1); format 3 is four half floats
    private const ulong FormatCustom0 = 1UL << 6, FormatCustom1 = 1UL << 7, Custom0Shift = 13, Custom1Shift = 16, RgbaHalf = 3;
    public const ulong CustomFormat = FormatCustom0 | FormatCustom1 | (RgbaHalf << (int)Custom0Shift) | (RgbaHalf << (int)Custom1Shift);

    private const int StrideBefore = 8, Stride = StrideBefore + 8 + 8;   // UV, joints, weights

    public static PreparedSurface Encode(MeshFace face, FaceInfluences influences)
    {
        if (influences.Vertices != face.Vertices || influences.Joints.Length < face.Vertices * 4 || influences.Weights.Length < face.Vertices * 4)
            throw new ArgumentException("the influences are not for this face's vertices");
        var plain = MeshSurfaceData.Encode(face);
        int n = plain.VertexCount;
        var attribute = new byte[n * Stride];
        Viewer.Core.Diagnostics.AllocationMeter.Large(attribute.Length);   // (measurement only)
        for (int i = 0; i < n; i++)
        {
            var at = attribute.AsSpan(i * Stride);
            plain.AttributeData.AsSpan(i * StrideBefore, StrideBefore).CopyTo(at);
            for (int k = 0; k < 4; k++)
            {
                bool has = i < influences.Vertices;   // the vertex of a face with no geometry has no record: joint 0
                BinaryPrimitives.WriteHalfLittleEndian(at[(StrideBefore + k * 2)..], (Half)(has ? influences.Joints[i * 4 + k] : 0));
                BinaryPrimitives.WriteHalfLittleEndian(at[(StrideBefore + 8 + k * 2)..], (Half)(has ? influences.Weights[i * 4 + k] : (k == 0 ? 1f : 0f)));
            }
        }
        return plain with { Format = plain.Format | CustomFormat, AttributeData = attribute };
    }
}

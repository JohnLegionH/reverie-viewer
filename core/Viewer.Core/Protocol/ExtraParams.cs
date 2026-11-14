// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.World;

namespace Viewer.Core.Protocol;

/// <summary>
/// Reads the PBR material ids from an object update's extra parameters, which LibreMetaverse 3.1.6 skips. The extra
/// parameter block is a count byte, then per parameter a 16-bit type, a 32-bit length and that many bytes (little
/// endian). The render material parameter (type 0x80) holds a count byte, then per entry a face index byte and a
/// 16-byte material id; a face's PBR material is the id of its entry, and a face with no entry has none. Every read is
/// bounds-checked; a block that runs past its data gives null.
/// </summary>
public static class ExtraParams
{
    public const ushort RenderMaterialType = 0x80;

    /// <summary>
    /// The render material entries in the extra parameter block at <paramref name="pos"/>: null when the block has no
    /// render material parameter or is malformed, else its entries (possibly none).
    /// </summary>
    public static IReadOnlyList<RenderMaterialEntry>? RenderMaterials(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < 0 || pos >= data.Length)
            return null;
        int count = data[pos++];
        for (int k = 0; k < count; k++)
        {
            if (pos + 6 > data.Length)
                return null;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 2)..]);
            pos += 6;
            if (length > (uint)(data.Length - pos))
                return null;
            if (type == RenderMaterialType)
                return Entries(data.Slice(pos, (int)length));
            pos += (int)length;
        }
        return null;
    }

    private static IReadOnlyList<RenderMaterialEntry>? Entries(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1)
            return null;
        int n = p[0];
        if (p.Length < 1 + n * 17)
            return null;
        var list = new List<RenderMaterialEntry>(n);
        for (int e = 0; e < n; e++)
        {
            var at = p.Slice(1 + e * 17, 17);
            list.Add(new RenderMaterialEntry(at[0], UuidFromBytes(at[1..])));
        }
        return list;
    }

    /// <summary>A UUID as it is sent on the wire (its 16 bytes in reading order, big endian).</summary>
    public static Guid UuidFromBytes(ReadOnlySpan<byte> b) => new(b[..16], bigEndian: true);

    /// <summary>
    /// For a compressed object update's data: its local id, its object kind (pcode) and where its extra parameter block
    /// starts; false when the data is too short or malformed. The compressed layout: full id (16), local id (4), pcode (1),
    /// state (1), CRC (4), material (1), click action (1), scale, position and rotation (12 each), flags (4), owner id
    /// (16); then, by flag, angular velocity (0x80, 12 bytes), parent id (0x20, 4), tree species (0x02, 1) or a
    /// scratch pad (0x01, a size byte and that many), floating text and its colour (0x04, a NUL-terminated string and 4),
    /// a media URL (0x200, NUL-terminated), particles (0x08, 86); then the extra parameter block.
    /// </summary>
    public static bool CompressedLayout(ReadOnlySpan<byte> d, out uint localId, out byte pcode, out int extraParamsAt)
    {
        localId = 0;
        pcode = 0;
        extraParamsAt = -1;
        const int fixedPart = 16 + 4 + 1 + 1 + 4 + 1 + 1 + 12 + 12 + 12 + 4 + 16;
        if (d.Length < fixedPart)
            return false;
        localId = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        pcode = d[20];
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d[(fixedPart - 20)..]);
        int i = fixedPart;
        if ((flags & 0x80) != 0) i += 12;
        if ((flags & 0x20) != 0) i += 4;
        if ((flags & 0x02) != 0) i += 1;
        else if ((flags & 0x01) != 0)
        {
            if (i >= d.Length) return false;
            i += 1 + d[i];
        }
        if ((flags & 0x04) != 0)
        {
            if (!SkipNul(d, ref i)) return false;
            i += 4;
        }
        if ((flags & 0x200) != 0 && !SkipNul(d, ref i))
            return false;
        if ((flags & 0x08) != 0) i += 86;
        if (i >= d.Length)
            return false;
        extraParamsAt = i;
        return true;
    }

    private static bool SkipNul(ReadOnlySpan<byte> d, ref int i)
    {
        if (i >= d.Length) return false;
        int n = d[i..].IndexOf((byte)0);
        if (n < 0) return false;
        i += n + 1;
        return true;
    }
}

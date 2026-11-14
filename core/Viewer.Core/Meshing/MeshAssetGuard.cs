// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Viewer.Core.Meshing;

/// <summary>Hard limits for a mesh asset. Every refusal is bounded in time and memory by these.</summary>
public sealed record MeshLimits
{
    public int MaxAssetBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxInflatedPartBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxInflatedTotalBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxLlsdDepth { get; init; } = 32;
    public int MaxLlsdNodes { get; init; } = 2_000_000;
    public int MaxHeaderParts { get; init; } = 32;
    public int MaxFacesPerLod { get; init; } = 8;                  // SL's texture-entry face limit for meshes
    public int MaxVerticesPerFace { get; init; } = 65536;          // indices are 16-bit on the wire
    public int MaxTrianglesPerLod { get; init; } = 1_000_000;

    public static MeshLimits Default { get; } = new();
}

/// <summary>Why an asset was refused (the only thing reported; never the bytes).</summary>
public sealed record MeshRefusal(string Reason)
{
    public override string ToString() => Reason;
}

/// <summary>
/// Validates a mesh asset BEFORE LibreMetaverse's decoder sees it. That decoder (AssetMesh.Decode, 3.1.6) allocates
/// <c>new byte[size]</c> straight from the header and inflates every part with no bound, so a hostile asset could force a
/// huge allocation or a zlib bomb. This guard, pure and bounded:
///   - walks the binary LLSD header with counts checked against the bytes that remain (a declared count can never exceed
///     what the data could hold), bounded depth and node count;
///   - checks every part's offset and size against the asset (the decoder reads every map-valued part it finds);
///   - inflates each part into a capped buffer (per part and in total) and walks the inflated LLSD the same way.
/// </summary>
public static class MeshAssetGuard
{
    public sealed record Part(string Name, int Offset, int Size);

    public static MeshRefusal? Validate(ReadOnlySpan<byte> asset, MeshLimits? limits = null) => Validate(asset, limits, out _);

    public static MeshRefusal? Validate(ReadOnlySpan<byte> asset, MeshLimits? limits, out IReadOnlyList<Part> parts)
    {
        limits ??= MeshLimits.Default;
        parts = [];
        if (asset.Length == 0) return new("empty asset");
        if (asset.Length > limits.MaxAssetBytes) return new($"asset larger than {limits.MaxAssetBytes} bytes");
        var header = new Walker(asset, limits);
        var headerParts = new List<Part>();
        if (!header.TryReadHeader(headerParts, out var why)) return new("header: " + why);
        int start = header.Position;
        long inflatedTotal = 0;
        foreach (var p in headerParts)
        {
            if (p.Offset < 0 || p.Size == 0) continue;   // the decoder skips these
            if (p.Size < 0) return new($"part {p.Name}: negative size");
            if ((long)start + p.Offset + p.Size > asset.Length) return new($"part {p.Name}: offset + size beyond the asset");
            var inflated = Inflate(asset.Slice(start + p.Offset, p.Size), limits.MaxInflatedPartBytes, out why);
            if (inflated is null) return new($"part {p.Name}: {why}");
            inflatedTotal += inflated.Length;
            if (inflatedTotal > limits.MaxInflatedTotalBytes) return new($"inflated parts exceed {limits.MaxInflatedTotalBytes} bytes");
            var w = new Walker(inflated, limits);
            if (!w.TryWalkValue(0, out why) || w.Position != inflated.Length && !OnlyPadding(inflated, w.Position))
                return new($"part {p.Name}: {why ?? "trailing data"}");
        }
        parts = headerParts;
        return null;
    }

    /// <summary>Where the header map ends, which is where the parts' offsets start; 0 when the header does not walk.</summary>
    internal static int HeaderLength(ReadOnlySpan<byte> asset, MeshLimits? limits = null)
    {
        var header = new Walker(asset, limits ?? MeshLimits.Default);
        return header.TryReadHeader([], out _) ? header.Position : 0;
    }

    private static bool OnlyPadding(byte[] b, int from)
    {
        for (int i = from; i < b.Length; i++) if (b[i] != 0) return false;
        return true;
    }

    /// <summary>zlib-inflates into at most <paramref name="cap"/> bytes; null (with a reason) on bad data or when over the cap.</summary>
    internal static byte[]? Inflate(ReadOnlySpan<byte> data, int cap, out string? why)
    {
        why = null;
        try
        {
            using var z = new ZLibStream(new MemoryStream(data.ToArray(), writable: false), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
            {
                if (output.Length + n > cap) { why = $"inflates beyond {cap} bytes"; return null; }
                output.Write(buf, 0, n);
            }
            if (output.Length == 0) { why = "inflates to nothing"; return null; }
            return output.ToArray();
        }
        catch (InvalidDataException) { why = "bad zlib data"; return null; }
    }

    /// <summary>A bounded binary-LLSD walker (types ! 1 0 i r u b s l d k [ ] { }).</summary>
    private ref struct Walker(ReadOnlySpan<byte> data, MeshLimits limits)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _nodes;
        public int Position;

        private int Remaining => _d.Length - Position;

        private bool Len(out int len)
        {
            len = 0;
            if (Remaining < 4) return false;
            len = BinaryPrimitives.ReadInt32BigEndian(_d.Slice(Position, 4));
            Position += 4;
            return len >= 0 && len <= Remaining;
        }

        public bool TryWalkValue(int depth, out string? why)
        {
            why = null;
            if (depth > limits.MaxLlsdDepth) { why = "nesting too deep"; return false; }
            if (++_nodes > limits.MaxLlsdNodes) { why = "too many nodes"; return false; }
            if (Remaining < 1) { why = "truncated"; return false; }
            byte t = _d[Position++];
            switch ((char)t)
            {
                case '!': case '1': case '0': return true;
                case 'i': return Skip(4, out why);
                case 'r': case 'd': return Skip(8, out why);
                case 'u': return Skip(16, out why);
                case 'b': case 's': case 'l':
                    if (!Len(out int n)) { why = "string/binary length beyond the data"; return false; }
                    Position += n;
                    return true;
                case '[':
                {
                    // every element takes at least one byte, so a count larger than what remains is a lie
                    if (!Len(out int count)) { why = "array count beyond the data"; return false; }
                    for (int i = 0; i < count; i++) if (!TryWalkValue(depth + 1, out why)) return false;
                    return Expect(']', out why);
                }
                case '{':
                {
                    if (!Len(out int count)) { why = "map count beyond the data"; return false; }
                    for (int i = 0; i < count; i++)
                    {
                        if (!Key(out _, out why)) return false;
                        if (!TryWalkValue(depth + 1, out why)) return false;
                    }
                    return Expect('}', out why);
                }
                default:
                    why = $"unknown LLSD type 0x{t:X2}";
                    return false;
            }
        }

        private bool Skip(int n, out string? why)
        {
            why = null;
            if (Remaining < n) { why = "truncated"; return false; }
            Position += n;
            return true;
        }

        private bool Expect(char c, out string? why)
        {
            why = null;
            if (Remaining < 1 || _d[Position] != (byte)c) { why = $"missing '{c}'"; return false; }
            Position++;
            return true;
        }

        private bool Key(out string key, out string? why)
        {
            key = "";
            why = null;
            if (Remaining < 1 || _d[Position] != (byte)'k') { why = "map key expected"; return false; }
            Position++;
            if (!Len(out int n) || n > 256) { why = "map key length bad"; return false; }
            key = Encoding.UTF8.GetString(_d.Slice(Position, n));
            Position += n;
            return true;
        }

        /// <summary>The top-level header map; collects each map-valued entry's integer offset/size (absent = 0, as the decoder reads it).</summary>
        public bool TryReadHeader(List<Part> parts, out string? why)
        {
            why = null;
            if (Remaining < 1 || _d[Position] != (byte)'{') { why = "not a map"; return false; }
            Position++;
            if (!Len(out int count)) { why = "map count beyond the data"; return false; }
            if (count > limits.MaxHeaderParts) { why = "too many header entries"; return false; }
            for (int i = 0; i < count; i++)
            {
                if (!Key(out var key, out why)) return false;
                if (Remaining < 1) { why = "truncated"; return false; }
                if (_d[Position] != (byte)'{')
                {
                    if (!TryWalkValue(1, out why)) return false;
                    continue;
                }
                Position++;
                if (!Len(out int n)) { why = $"{key}: map count beyond the data"; return false; }
                int offset = 0, size = 0;
                for (int j = 0; j < n; j++)
                {
                    if (!Key(out var k, out why)) return false;
                    if (k is "offset" or "size")
                    {
                        // Only integers: the decoder's implicit conversion would also parse strings and reals.
                        if (Remaining < 5 || _d[Position] != (byte)'i') { why = $"{key}.{k} is not an integer"; return false; }
                        int v = BinaryPrimitives.ReadInt32BigEndian(_d.Slice(Position + 1, 4));
                        Position += 5;
                        if (k == "offset") offset = v; else size = v;
                    }
                    else if (!TryWalkValue(2, out why)) return false;
                }
                if (!Expect('}', out why)) return false;
                parts.Add(new Part(key, offset, size));
            }
            return Expect('}', out why);
        }
    }
}

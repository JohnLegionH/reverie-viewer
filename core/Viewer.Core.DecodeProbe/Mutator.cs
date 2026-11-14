// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.Protocol;

namespace Viewer.Core.DecodeProbe;

/// <summary>Our own codestreams (generated here, no third-party content) for the fuzz sources.</summary>
public static class Own
{
    private static byte[] Rgba(int w, int h, Func<int, int, (byte, byte, byte, byte)> px)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl, a) = px(x, y);
                int i = (y * w + x) * 4;
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = a;
            }
        return b;
    }

    // the same pixels as the tests' TextureFixtures
    public static byte[] Opaque64() => LmvTexture.Encode(64, 64, Rgba(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 128, 255)), withAlpha: false);
    public static byte[] Blended64() => LmvTexture.Encode(64, 64, Rgba(64, 64, (x, y) => (20, 200, 90, (byte)(x * 4))), withAlpha: true);

    /// <summary>A detailed image (a smooth ramp plus seeded noise), so the codestream has real packet data at every level.</summary>
    public static byte[] Detailed(int size, int seed)
    {
        var rng = new Random(seed);
        return Rgba(size, size, (x, y) => (
            (byte)Math.Clamp(x * 255 / size + rng.Next(-40, 41), 0, 255),
            (byte)Math.Clamp(y * 255 / size + rng.Next(-40, 41), 0, 255),
            (byte)Math.Clamp(128 + rng.Next(-40, 41), 0, 255), 255));
    }

    public static IEnumerable<(string Name, byte[] Bytes)> Sources()
    {
        yield return ("own-64-opaque", Opaque64());
        yield return ("own-64-blended", Blended64());
        yield return ("own-256-lmv", LmvTexture.Encode(256, 256, Detailed(256, 1), withAlpha: false));
        yield return ("own-512-5layers", LmvTexture.EncodeLayered(512, 512, Detailed(512, 2), "0.0625 0.25 0.5 1.0 2.0", 2.0));
        yield return ("own-1024-5layers", LmvTexture.EncodeLayered(1024, 1024, Detailed(1024, 3), "0.0625 0.25 0.5 1.0 2.0", 2.0));
    }
}

/// <summary>
/// Seeded mutations of a codestream. The seed picks the kind and everything else, so a case is rebuilt from
/// (source, seed) alone. Kinds: runs of 0xFF or 0x00 in the packet data, bit flips (anywhere, or in the packet data),
/// header field edits (COD layers, decomposition levels, code-block size and style, precinct sizes, SIZ tile size),
/// truncation, repeated or misplaced markers, and two of these at once.
/// </summary>
public static class Mutator
{
    public static readonly string[] Kinds = ["ff-run", "zero-run", "flip-any", "flip-data", "header", "truncate", "marker", "combo"];

    public static byte[] Make(byte[] source, int seed, out string kind)
    {
        var rng = new Random(seed);
        int k = rng.Next(Kinds.Length);
        kind = Kinds[k];
        var b = (byte[])source.Clone();
        if (k == 7)
        {
            b = Apply(b, rng.Next(7), rng, out var k1);
            b = Apply(b, rng.Next(7), rng, out var k2);
            kind = $"combo({k1}+{k2})";
            return b;
        }
        return Apply(b, k, rng, out kind);
    }

    private static byte[] Apply(byte[] b, int k, Random rng, out string kind)
    {
        kind = Kinds[k];
        int data = DataStart(b), dataLen = Math.Max(1, b.Length - 2 - data);
        switch (k)
        {
            case 0:
            case 1:
            {
                byte fill = k == 0 ? (byte)0xFF : (byte)0x00;
                int len = rng.Next(4) == 0 ? dataLen : 1 + rng.Next(Math.Min(dataLen, 1 + rng.Next(256)));
                int at = data + rng.Next(Math.Max(1, dataLen - len + 1));
                for (int i = at; i < Math.Min(b.Length - 2, at + len); i++) b[i] = fill;
                kind += $"@{at}+{len}";
                return b;
            }
            case 2:
            case 3:
            {
                int n = 1 + rng.Next(8);
                for (int i = 0; i < n; i++)
                {
                    int at = k == 2 ? rng.Next(b.Length) : data + rng.Next(dataLen);
                    b[Math.Min(at, b.Length - 1)] ^= (byte)(1 << rng.Next(8));
                }
                return b;
            }
            case 4:
            {
                int cod = Find(b, 0xFF, 0x52);
                int field = rng.Next(6);
                if (cod < 0 || field == 5)
                {
                    // SIZ tile size (XTsiz, YTsiz) made small: many tiles
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), (uint)(1 + rng.Next(64)));
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28), (uint)(1 + rng.Next(64)));
                    kind = "header-tilesize";
                    return b;
                }
                switch (field)
                {
                    case 0: BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(cod + 6), (ushort)(1 + rng.Next(65535))); kind = "header-layers"; break;
                    case 1: b[cod + 9] = (byte)rng.Next(34); kind = "header-levels"; break;
                    case 2: b[cod + 10] = (byte)rng.Next(16); b[cod + 11] = (byte)rng.Next(16); kind = "header-codeblock"; break;
                    case 3: b[cod + 12] = (byte)rng.Next(256); kind = "header-cbstyle"; break;
                    default:
                        // precincts: set the "precincts defined" bit; the bytes after the transform are read as sizes
                        b[cod + 4] |= 0x01;
                        if (cod + 14 < b.Length) b[cod + 14] = (byte)rng.Next(256);
                        kind = "header-precincts";
                        break;
                }
                return b;
            }
            case 5:
                return b[..(1 + rng.Next(b.Length - 1))];
            default:
            {
                // a marker repeated or misplaced inside the packet data
                int at = data + rng.Next(dataLen);
                byte[] insert = rng.Next(5) switch
                {
                    0 => Segment(b, 0x52),                     // a copy of COD
                    1 => [0xFF, 0x90, 0, 10, 0, 0, 0, 0, 0, 0, 0, 1],   // a stray SOT
                    2 => [0xFF, 0xD9],                         // EOC early
                    3 => [0xFF, 0x93],                         // SOD again
                    _ => Segment(b, 0x5C),                     // a copy of QCD
                };
                kind = $"marker@{at}";
                return [.. b[..at], .. insert, .. b[at..]];
            }
        }
    }

    /// <summary>Just after the first SOD (where the packet data starts); the whole file when there is none.</summary>
    private static int DataStart(byte[] b)
    {
        int sot = Find(b, 0xFF, 0x90);
        int sod = sot < 0 ? -1 : Find(b, 0xFF, 0x93, sot);
        return sod < 0 ? 2 : Math.Min(sod + 2, b.Length - 2);
    }

    private static int Find(byte[] b, byte x, byte y, int from = 0)
    {
        for (int i = from; i + 1 < b.Length; i++) if (b[i] == x && b[i + 1] == y) return i;
        return -1;
    }

    private static byte[] Segment(byte[] b, byte marker)
    {
        int at = Find(b, 0xFF, marker);
        if (at < 0 || at + 4 > b.Length) return [0xFF, marker, 0, 2];
        int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 2));
        return b[at..Math.Min(b.Length, at + 2 + len)];
    }
}

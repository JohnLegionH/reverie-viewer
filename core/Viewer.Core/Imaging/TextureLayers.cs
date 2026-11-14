// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Imaging;

/// <summary>
/// The quality layers a codestream declares, as the guard reads them (the main header's COD, from
/// the whole file or any prefix that holds the whole main header). For the live check's layer counts and the decode
/// probe's <c>layers</c> command; nothing is decoded.
/// </summary>
public static class TextureLayers
{
    /// <summary>The line the layer report draws: the same count as <see cref="TextureByteRange.MaxLayers"/>, above which a
    /// codestream is fetched whole (the partial-fetch byte estimate does not hold for it). It has no effect on decoding.</summary>
    public const int Threshold = 8;

    /// <summary>The guard's reading of the main header (null with the refusal when it cannot be read).</summary>
    public static (J2kHeader? Header, string? Refusal) Read(ReadOnlySpan<byte> codestream)
    {
        string? refusal = J2kGuard.Validate(codestream, null, out var header, allowTruncated: true);
        return refusal is null ? (header, null) : (null, refusal);
    }

    /// <summary>The limits a MEASUREMENT reads under: the viewer's own, with the component count opened to the standard's
    /// maximum (the grid's bakes declare 5 components, which the viewer's guard refused until),
    /// and the quality layers to the standard's (the report counts what is there). Never used to decode.</summary>
    private static readonly J2kLimits Measurement = J2kLimits.Default with { MaxComponents = 16384, MaxQualityLayers = 65535 };

    /// <summary>The main header of a raw codestream ("j2c") or of the codestream inside a
    /// JP2 file's jp2c box ("jp2"), read under <see cref="Measurement"/>; when neither reads, the first bytes in hex.</summary>
    public static (J2kHeader? Header, string? Refusal, string Container, string Magic) Measure(ReadOnlySpan<byte> data)
    {
        string magic = Convert.ToHexString(data[..Math.Min(8, data.Length)]);
        string? refusal = J2kGuard.Validate(data, Measurement, out var header, allowTruncated: true);
        if (refusal is null) return (header, null, "j2c", magic);
        if (data.Length >= 12 && data[4] == 0x6A && data[5] == 0x50 && data[6] == 0x20 && data[7] == 0x20)
        {
            // JP2 boxes: length (4, big-endian; 1 = a 64-bit length follows; 0 = to the end), type (4)
            int p = 0;
            while (p + 8 <= data.Length)
            {
                long len = (uint)((data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]);
                int head = 8;
                if (len == 1 && p + 16 <= data.Length) { len = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[(p + 8)..]); head = 16; }
                if (data[p + 4] == 'j' && data[p + 5] == 'p' && data[p + 6] == '2' && data[p + 7] == 'c')
                {
                    string? inner = J2kGuard.Validate(data[(p + head)..], Measurement, out header, allowTruncated: true);
                    return inner is null ? (header, null, "jp2", magic) : (null, "jp2: " + inner, "jp2", magic);
                }
                if (len < head) break;
                p += (int)Math.Min(len, int.MaxValue - p);
            }
            return (null, "jp2 without a readable jp2c box", "jp2", magic);
        }
        return (null, refusal, "unknown", magic);
    }

    /// <summary>Counts by layer count; an unreadable header counts under -1.</summary>
    public static SortedDictionary<int, int> Histogram(IEnumerable<int?> layers)
    {
        var h = new SortedDictionary<int, int>();
        foreach (var l in layers)
        {
            int k = l ?? -1;
            h[k] = h.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        return h;
    }

    /// <summary>How many declare more than <see cref="Threshold"/> layers.</summary>
    public static int OverThreshold(IEnumerable<int?> layers) => layers.Count(l => l > Threshold);
}

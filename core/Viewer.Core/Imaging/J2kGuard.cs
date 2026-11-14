// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;

namespace Viewer.Core.Imaging;

/// <summary>Hard limits for a JPEG 2000 texture. Every refusal is bounded by these.</summary>
public sealed record J2kLimits
{
    public int MaxBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxWidth { get; init; } = 2048;
    public int MaxHeight { get; init; } = 2048;
    /// <summary>5, for avatar bakes (R, G, B, A and the morph mask); the image is components 0-3.
    /// The official viewer's own ceiling is 8 (it decodes components 0-3 as the image and reads 4 as the morph mask); 5 is
    /// the least that takes the bakes.</summary>
    public int MaxComponents { get; init; } = 5;
    public int MaxBitDepth { get; init; } = 16;
    public int MaxTiles { get; init; } = 1024;
    public int MaxDecompositionLevels { get; init; } = 32;
    /// <summary>A resource limit, not a rule of the standard (which allows 65,535): a COD declaring
    /// 20,658 layers made one 1024x1024 decode allocate 389 MiB. Real textures declare 1-7, the grid's bakes
    /// 14-19, LibreMetaverse's encoder 27, CoreJ2K's lossless 32.</summary>
    public int MaxQualityLayers { get; init; } = 64;
    public int MaxMainHeaderMarkers { get; init; } = 256;
    public int MaxTileParts { get; init; } = 8192;

    public static J2kLimits Default { get; } = new();
}

/// <summary>What the codestream header declares (after validation). <see cref="Truncated"/>: the
/// bytes are a prefix of a longer codestream (a partial fetch); only accepted when the caller allows it.</summary>
public sealed record J2kHeader(int Width, int Height, int Components, int MaxBitDepth, int TilesX, int TilesY, int DecompositionLevels, int Layers, int TileParts)
{
    public bool Truncated { get; init; }
}

/// <summary>
/// Validates a raw JPEG 2000 codestream (SL textures are raw J2C, not JP2 boxes) BEFORE the decoder sees it
/// (the lesson): SOC, then SIZ with its declared image and tile geometry, then every main-header marker segment and
/// every tile-part, with each declared length checked against the bytes present. Refuses dimensions above the limit,
/// more than 5 components (a bake's morph mask is the fifth), absurd tile counts, and sizes the bytes cannot support. Pure, bounded, no allocation beyond
/// the result.
/// With <c>allowTruncated</c>, a PREFIX of a codestream is accepted: the whole main header and at
/// least one complete tile-part header (SOT) must be present and pass every check above; only the tail may be missing
/// (the last tile-part's declared length may run past the bytes, or the next SOT may be cut). The result is marked
/// <see cref="J2kHeader.Truncated"/>. Everything refused today is still refused, with or without it.
/// </summary>
public static class J2kGuard
{
    private const ushort Soc = 0xFF4F, Siz = 0xFF51, Cod = 0xFF52, Sot = 0xFF90, Sod = 0xFF93, Eoc = 0xFFD9, Ppm = 0xFF60, Ppt = 0xFF61;
    private const int ScodSop = 0x02, ScodEph = 0x04;

    public static string? Validate(ReadOnlySpan<byte> d, J2kLimits? limits, out J2kHeader? header) => Validate(d, limits, out header, allowTruncated: false);

    public static string? Validate(ReadOnlySpan<byte> d, J2kLimits? limits, out J2kHeader? header, bool allowTruncated)
    {
        limits ??= J2kLimits.Default;
        header = null;
        if (d.Length == 0) return "empty";
        if (d.Length > limits.MaxBytes) return $"larger than {limits.MaxBytes} bytes";
        if (d.Length < 4 || U16(d, 0) != Soc) return "not a raw JPEG 2000 codestream (no SOC)";
        if (U16(d, 2) != Siz) return "SIZ is not the first marker";
        if (d.Length < 6) return "truncated SIZ";
        int lsiz = U16(d, 4);
        if (lsiz < 41 || 4 + lsiz > d.Length) return "SIZ length beyond the data";
        int p = 6;
        p += 2;   // Rsiz
        long xsiz = U32(d, p), ysiz = U32(d, p + 4), xo = U32(d, p + 8), yo = U32(d, p + 12);
        long xt = U32(d, p + 16), yt = U32(d, p + 20), xto = U32(d, p + 24), yto = U32(d, p + 28);
        int csiz = U16(d, p + 32);
        if (lsiz != 38 + 3 * csiz) return "SIZ length does not match its component count";
        if (csiz < 1 || csiz > limits.MaxComponents) return $"{csiz} components (limit {limits.MaxComponents})";
        long w = xsiz - xo, h = ysiz - yo;
        if (w <= 0 || h <= 0) return "empty image area";
        if (w > limits.MaxWidth || h > limits.MaxHeight) return $"{w}x{h} exceeds {limits.MaxWidth}x{limits.MaxHeight}";
        if (xt <= 0 || yt <= 0) return "zero tile size";
        if (xto > xo || yto > yo || xto + xt <= xo || yto + yt <= yo) return "tile grid does not cover the image origin";
        long tilesX = (xsiz - xto + xt - 1) / xt, tilesY = (ysiz - yto + yt - 1) / yt;
        if (tilesX * tilesY > limits.MaxTiles) return $"{tilesX * tilesY} tiles (limit {limits.MaxTiles})";
        int maxDepth = 0;
        for (int c = 0; c < csiz; c++)
        {
            int o = p + 34 + 3 * c;
            int depth = (d[o] & 0x7F) + 1;
            if (depth > limits.MaxBitDepth) return $"component {c}: {depth}-bit (limit {limits.MaxBitDepth})";
            if (d[o + 1] == 0 || d[o + 2] == 0) return $"component {c}: zero subsampling";
            maxDepth = Math.Max(maxDepth, depth);
        }

        // Main header: marker segments up to the first SOT, each length inside the data.
        int pos = 4 + lsiz, markers = 0, levels = -1, layers = 0, mainScod = MainScod(d, pos);
        var ppm = new PackedHeaderScan(lengthPrefixed: true);
        while (true)
        {
            if (++markers > limits.MaxMainHeaderMarkers) return "too many main-header markers";
            if (pos + 4 > d.Length) return "main header truncated";
            int m = U16(d, pos);
            if ((m & 0xFF00) != 0xFF00) return "marker expected in the main header";
            if (m == Sot) break;
            if (m is Soc or Sod or Eoc) return $"unexpected marker 0x{m:X4} in the main header";
            int len = U16(d, pos + 2);
            if (len < 2 || pos + 2 + len > d.Length) return $"marker 0x{m:X4} length beyond the data";
            if (m == Cod)
            {
                if (len < 12) return "COD too short";
                layers = U16(d, pos + 6);
                levels = d[pos + 9];
                if (layers < 1) return "COD declares no quality layers";
                if (layers > limits.MaxQualityLayers) return $"{layers} quality layers (limit {limits.MaxQualityLayers})";
                if (levels > limits.MaxDecompositionLevels) return $"{levels} decomposition levels (limit {limits.MaxDecompositionLevels})";
            }
            else if (m == Ppm && len >= 3 && ppm.Feed(d.Slice(pos + 5, len - 3), (mainScod & ScodEph) != 0) is { } bad)
                return "PPM packed packet headers: " + bad;
            pos += 2 + len;
        }
        if (levels < 0) return "no COD in the main header";

        // Tile-parts: each SOT's index inside the grid and its declared length inside the data.
        int parts = 0, scod = mainScod;
        bool truncated = false;
        while (pos + 2 <= d.Length)
        {
            int m = U16(d, pos);
            if (m == Eoc) break;
            if (m != Sot) return $"tile-part expected at {pos}, found 0x{m:X4}";
            if (pos + 12 > d.Length)
            {
                // a cut SOT: only the tail of a prefix, after at least one complete tile-part header
                if (allowTruncated && parts > 0) { truncated = true; break; }
                return "SOT truncated";
            }
            if (++parts > limits.MaxTileParts) return "too many tile-parts";
            if (U16(d, pos + 2) != 10) return "SOT length is not 10";
            int isot = U16(d, pos + 4);
            long psot = U32(d, pos + 6);
            if (isot >= tilesX * tilesY) return $"tile index {isot} outside the {tilesX * tilesY}-tile grid";
            if (psot != 0 && psot < 14) return "tile-part length beyond the data";
            bool cut = psot != 0 && pos + psot > d.Length;
            if (cut && !allowTruncated) return "tile-part length beyond the data";
            // the packet data of this tile-part (only the bytes present) obeys the bit-stuffing rule
            int end = psot == 0 || cut ? d.Length : pos + (int)psot;
            if (TilePartPackets(d, pos + 12, end, ref scod) is { } bad)
                return bad;
            if (psot == 0) { pos = d.Length; break; }   // the last tile-part runs to the end
            if (cut) { truncated = true; break; }       // this tile-part's tail is missing
            pos += (int)psot;
        }
        if (parts == 0) return "no tile-parts";
        header = new J2kHeader((int)w, (int)h, csiz, maxDepth, (int)tilesX, (int)tilesY, levels, layers, parts) { Truncated = truncated };
        return null;
    }

    /// <summary>The main header's COD style byte (Scod), read ahead so a PPM before the COD is judged by it too. 0 when
    /// there is none (the main pass then refuses the file).</summary>
    private static int MainScod(ReadOnlySpan<byte> d, int pos)
    {
        for (int k = 0; k < 256 && pos + 5 <= d.Length; k++)
        {
            int m = U16(d, pos);
            if (m == Cod) return d[pos + 4];
            if ((m & 0xFF00) != 0xFF00 || m == Sot) return 0;
            int len = U16(d, pos + 2);
            if (len < 2) return 0;
            pos += 2 + len;
        }
        return 0;
    }

    /// <summary>
    /// The bit-stuffing rule of JPEG 2000 Part 1 (ITU-T T.800 | ISO/IEC 15444-1), checked over one
    /// tile-part: its header from <paramref name="start"/> (just after SOT) up to SOD, then its packet data up to
    /// <paramref name="end"/> (the bytes present). Packet headers (in the data, or packed into PPM/PPT) stuff a zero bit
    /// after every 0xFF byte, the arithmetic coder never writes a byte above 0x8F after a 0xFF, and raw (bypass) segments
    /// stuff like packet headers, so inside packet data a 0xFF byte is never followed by a byte above 0x8F. The only
    /// exceptions are the SOP (0xFF91, a 6-byte segment) and EPH (0xFF92) markers, and only when a COD enables them (Scod
    /// bits 1 and 2; COC does not carry them). Here a COD in the main header or in any tile-part header read so far
    /// enables them (<paramref name="scod"/> collects them), which is never narrower than the standard. Linear, no
    /// allocation. The pattern it refuses (0xFF 0xFF) sends CoreJ2K's packet-header reader into an endless loop.
    /// A tile-part cut short (a prefix) is checked as far as its bytes go.
    /// </summary>
    private static string? TilePartPackets(ReadOnlySpan<byte> d, int start, int end, ref int scod)
    {
        int p = start;
        var ppt = new PackedHeaderScan(lengthPrefixed: false);
        while (true)
        {
            if (p + 2 > end) return end == d.Length ? null : "tile-part without SOD";   // a cut header: the tail is missing
            int m = U16(d, p);
            if (m == Sod) { p += 2; break; }
            if ((m & 0xFF00) != 0xFF00) return "marker expected in a tile-part header";
            if (p + 4 > end) return end == d.Length ? null : "tile-part header truncated";
            int len = U16(d, p + 2);
            if (len < 2) return $"marker 0x{m:X4} length too short in a tile-part header";
            if (p + 2 + len > end) return end == d.Length ? null : $"marker 0x{m:X4} length beyond its tile-part";
            if (m == Cod && len >= 3) scod |= d[p + 4];
            else if (m == Ppt && len >= 3 && ppt.Feed(d.Slice(p + 5, len - 3), (scod & ScodEph) != 0) is { } bad)
                return "PPT packed packet headers: " + bad;
            p += 2 + len;
        }
        if (end == d.Length && end - 2 >= p && U16(d, end - 2) == Eoc) end -= 2;   // a last tile-part running to EOC
        bool sop = (scod & ScodSop) != 0, eph = (scod & ScodEph) != 0;
        while (p + 1 < end)
        {
            int k = d.Slice(p, end - 1 - p).IndexOf((byte)0xFF);
            if (k < 0) break;
            int i = p + k, n = d[i + 1];
            p = i + 1;
            if (n <= 0x8F) continue;
            if (n == 0x91 && sop)
            {
                if (i + 6 > end) break;   // cut inside the SOP segment: only a prefix's tail
                if (U16(d, i + 2) != 4) return $"packet data: SOP length is not 4 at byte {i}";
                p = i + 6;
                continue;
            }
            if (n == 0x92 && eph) { p = i + 2; continue; }
            return $"packet data: 0xFF followed by 0x{n:X2} at byte {i} (JPEG 2000 bit stuffing never allows it)";
        }
        return null;
    }

    /// <summary>Packed packet headers (PPM: 4-byte Nppm lengths each followed by that many header bytes, across PPM
    /// segments; PPT: header bytes only), checked with the same rule, carrying the last byte across segments.</summary>
    private struct PackedHeaderScan(bool lengthPrefixed)
    {
        private long _remaining, _length;
        private int _lengthBytes;
        private bool _prevFF;

        public string? Feed(ReadOnlySpan<byte> s, bool eph)
        {
            foreach (byte b in s)
            {
                if (lengthPrefixed && _remaining == 0)
                {
                    // reading the next 4-byte Nppm; a new tile-part's headers start afresh
                    _length = (_length << 8) | b;
                    if (++_lengthBytes == 4) { _remaining = _length; _length = 0; _lengthBytes = 0; _prevFF = false; }
                    continue;
                }
                if (_prevFF && b > 0x8F && !(eph && b == 0x92))
                    return $"0xFF followed by 0x{b:X2} (JPEG 2000 bit stuffing never allows it)";
                _prevFF = b == 0xFF;
                if (lengthPrefixed) _remaining--;
            }
            return null;
        }
    }

    private static int U16(ReadOnlySpan<byte> d, int o) => o + 2 <= d.Length ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o, 2)) : -1;
    private static long U32(ReadOnlySpan<byte> d, int o) => o + 4 <= d.Length ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4)) : -1;
}

/// <summary>How a texture uses alpha (for alpha modes): opaque, a mask (binary), or blended.</summary>
public enum AlphaClass { Opaque, Binary, Blended }

/// <summary>A decoded texture: RGBA8, row-major, top row first.</summary>
public sealed record DecodedTexture(int Width, int Height, byte[] Rgba, AlphaClass Alpha, int SourceComponents)
{
    private readonly int _fullWidth, _fullHeight;

    /// <summary>Whether the alpha is "all or nothing" enough to draw a face with this texture as a cut-out (<see cref="AlphaMask"/>);
    /// worked out only for a texture that is not <see cref="AlphaClass.Opaque"/>.</summary>
    public bool IsAlphaMask { get; init; }

    /// <summary>The discard level this image is (0: full resolution) and how many the codestream offers.</summary>
    public int Level { get; init; }
    public int LevelCount { get; init; } = 1;
    public int FullWidth { get => _fullWidth > 0 ? _fullWidth : Width; init => _fullWidth = value; }
    public int FullHeight { get => _fullHeight > 0 ? _fullHeight : Height; init => _fullHeight = value; }
    /// <summary>The whole mipmap chain (<see cref="MipChain"/>), built with the decode when asked for; null otherwise.</summary>
    public byte[]? Mips { get; init; }

    /// <summary>The pool <see cref="Rgba"/> and <see cref="Mips"/> came from (null: plain arrays).</summary>
    public PixelPool? Pool { get; init; }

    private int _released;

    /// <summary>Hands <see cref="Rgba"/> and <see cref="Mips"/> back to their pool once nothing
    /// reads them (the viewer: once the level's engine image exists). Once only; after it, neither may be read.</summary>
    public void ReleasePixels()
    {
        if (Pool is not { } pool || Interlocked.Exchange(ref _released, 1) != 0) return;
        pool.Return(Rgba);
        pool.Return(Mips);
    }

    /// <summary>
    /// Declared tolerance: JPEG 2000 is lossy, so a binary mask comes back near 0 and near 255 (measured: values in
    /// 0-15 and 240-255 after a round trip). Alpha at or above <see cref="OpaqueFrom"/> counts as opaque, at or below
    /// <see cref="ClearTo"/> as clear; anything in between makes the texture blended.
    /// </summary>
    public const byte OpaqueFrom = 240, ClearTo = 15;

    /// <summary>The cut-out analysis (<see cref="AlphaMask"/>) of a picture whose alpha class is already known; a picture with no alpha to speak of is not analysed.</summary>
    public static bool AnalyzeMask(AlphaClass alpha, ReadOnlySpan<byte> rgba, int width, int height) =>
        alpha != AlphaClass.Opaque && AlphaMask.Analyze(rgba, width, height);

    public static AlphaClass Classify(ReadOnlySpan<byte> rgba)
    {
        bool anyClear = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            byte a = rgba[i];
            if (a >= OpaqueFrom) continue;
            if (a > ClearTo) return AlphaClass.Blended;
            anyClear = true;
        }
        return anyClear ? AlphaClass.Binary : AlphaClass.Opaque;
    }
}

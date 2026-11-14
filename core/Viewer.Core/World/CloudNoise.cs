// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The cloud noise picture: one 8-bit channel (the red channel of a colour-mapped or true-colour TGA), sampled with linear filtering and
/// wrapping, as the official cloud shader samples its noise texture. Rows are kept bottom first (texture coordinate v = 0 is the picture's bottom).
/// The bundled picture is the official viewer's default cloud noise (see THIRD-PARTY-NOTICES.md).
/// </summary>
public sealed class CloudNoise
{
    private readonly byte[] _red;

    private CloudNoise(int width, int height, byte[] red) { Width = width; Height = height; _red = red; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The red channel, row after row, the bottom row first (what the renderer uploads).</summary>
    public byte[] RedBottomUp => _red;

    /// <summary>The bundled default picture.</summary>
    public static CloudNoise Default { get; } = Load();

    private static CloudNoise Load()
    {
        using var s = typeof(CloudNoise).Assembly.GetManifestResourceStream("cloud-noise.tga") ?? throw new InvalidOperationException("the bundled cloud noise picture is missing");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return FromTga(m.ToArray());
    }

    /// <summary>Reads an uncompressed colour-mapped (8 bit) or true-colour (24 or 32 bit) TGA.</summary>
    public static CloudNoise FromTga(byte[] b)
    {
        if (b.Length < 18) throw new InvalidDataException("cloud noise: not a TGA");
        int idLen = b[0], mapType = b[1], type = b[2], mapLen = b[5] | (b[6] << 8), mapDepth = b[7];
        int w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8), depth = b[16];
        bool topFirst = (b[17] & 0x20) != 0;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new InvalidDataException("cloud noise: bad size");
        int pos = 18 + idLen;
        var red = new byte[w * h];
        if (type == 1 && mapType == 1 && depth == 8 && (mapDepth == 24 || mapDepth == 32))
        {
            int entry = mapDepth / 8, pix = pos + mapLen * entry;
            if (b.Length < pix + w * h) throw new InvalidDataException("cloud noise: truncated");
            for (int i = 0; i < w * h; i++) red[i] = b[pos + b[pix + i] * entry + 2];   // entries are stored blue, green, red
        }
        else if (type == 2 && (depth == 24 || depth == 32))
        {
            int step = depth / 8;
            if (b.Length < pos + w * h * step) throw new InvalidDataException("cloud noise: truncated");
            for (int i = 0; i < w * h; i++) red[i] = b[pos + i * step + 2];
        }
        else throw new InvalidDataException("cloud noise: unsupported TGA type");
        if (topFirst)
        {
            var flipped = new byte[red.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(red, y * w, flipped, (h - 1 - y) * w, w);
            red = flipped;
        }
        return new(w, h, red);
    }

    /// <summary>The picture at texture coordinate <paramref name="uv"/> (0..1 is one copy; it repeats), bilinear between texel centres, 0..1.</summary>
    public float Sample(Vector2 uv)
    {
        double x = uv.X * (double)Width - 0.5, y = uv.Y * (double)Height - 0.5;
        double fx = Math.Floor(x), fy = Math.Floor(y), tx = x - fx, ty = y - fy;
        int x0 = Wrap((int)fx, Width), x1 = Wrap((int)fx + 1, Width), y0 = Wrap((int)fy, Height), y1 = Wrap((int)fy + 1, Height);
        double a = _red[y0 * Width + x0], c = _red[y0 * Width + x1], d = _red[y1 * Width + x0], e = _red[y1 * Width + x1];
        return (float)(((a + (c - a) * tx) * (1 - ty) + (d + (e - d) * tx) * ty) / 255.0);
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;
}

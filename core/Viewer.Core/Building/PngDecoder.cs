// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;

namespace Viewer.Core.Building;

/// <summary>
/// A small PNG reader for the images a model file carries, so a model's textures can be checked and prepared without the engine:
/// non-interlaced images of 8 bits per sample in grey, grey with alpha, RGB, RGBA or a palette (the PNG specification, chapters 6-11).
/// Anything else returns null and the caller's own decoder is tried.
/// </summary>
public static class PngDecoder
{
    /// <summary>The largest side read (larger images are refused as the upload refuses them).</summary>
    public const int MaxSide = UploadImageRules.MaxSourceDimension;

    public static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    /// <summary>The image as RGBA (8 bits each), or null when it is not a PNG this reader takes.</summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(byte[] png)
    {
        try { return DecodeCore(png); }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException) { return null; }
    }

    private static (int, int, byte[])? DecodeCore(byte[] png)
    {
        if (!IsPng(png))
            return null;
        int at = 8, width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        while (at + 8 <= png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            if (length < 0 || at + 12 + length > png.Length)
                return null;
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int data = at + 8;
            switch (type)
            {
                case "IHDR":
                    width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                    height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                    depth = png[data + 8]; colour = png[data + 9]; interlace = png[data + 12];
                    break;
                case "PLTE": palette = png[data..(data + length)]; break;
                case "tRNS": paletteAlpha = png[data..(data + length)]; break;
                case "IDAT": idat.Write(png, data, length); break;
                case "IEND": at = png.Length; continue;
            }
            at += 12 + length;
        }
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || depth != 8 || interlace != 0)
            return null;
        int channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || (colour == 3 && palette is null))
            return null;
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0) return null;
                read += n;
            }
        }
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? pixels[dst + x - channels] : 0;
                int b = y > 0 ? pixels[prev + x] : 0;
                int c = x >= channels && y > 0 ? pixels[prev + x - channels] : 0;
                int v = raw[src + x];
                pixels[dst + x] = (byte)(filter switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + ((a + b) >> 1),
                    4 => v + Paeth(a, b, c),
                    _ => throw new InvalidDataException("filter"),
                });
            }
        }
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            switch (colour)
            {
                case 0: rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[i]; rgba[o + 3] = 255; break;
                case 4: rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[i * 2]; rgba[o + 3] = pixels[i * 2 + 1]; break;
                case 2: rgba[o] = pixels[i * 3]; rgba[o + 1] = pixels[i * 3 + 1]; rgba[o + 2] = pixels[i * 3 + 2]; rgba[o + 3] = 255; break;
                case 6: Buffer.BlockCopy(pixels, o, rgba, o, 4); break;
                case 3:
                    int p = pixels[i];
                    if (p * 3 + 2 >= palette!.Length) return null;
                    rgba[o] = palette[p * 3]; rgba[o + 1] = palette[p * 3 + 1]; rgba[o + 2] = palette[p * 3 + 2];
                    rgba[o + 3] = paletteAlpha is not null && p < paletteAlpha.Length ? paletteAlpha[p] : (byte)255;
                    break;
            }
        }
        return (width, height, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

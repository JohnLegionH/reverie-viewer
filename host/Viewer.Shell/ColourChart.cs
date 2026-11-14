// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// Colour-accuracy fixture: a 256x64 chart of 16 x 4 patches (16x16 texels each) on the FULLBRIGHT top face of a
/// 4 m x 1 m box, seen from straight above by an orthographic camera; three 0.6 m boxes below it: a white texture x a
/// tint, a mid-grey texture x a mid-grey tint (SL composes texture x tint in sRGB space), and the chart on a LIT face
/// (sanity only). The images are encoded losslessly (fixtures/colour/*.j2c) and reach the GPU through the guarded
/// decoder. The fixture is fixtures/colour/colour-chart.colourchart.json.
/// </summary>
public static class ColourChart
{
    public const string Format = "nv-colourchart-1";
    public static readonly Guid Chart = new("4e56007d-0000-4000-8000-000000000001");
    public static readonly Guid White = new("4e56007d-0000-4000-8000-000000000002");
    public static readonly Guid Grey = new("4e56007d-0000-4000-8000-000000000003");

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [Chart] = "colour-chart.j2c", [White] = "white.j2c", [Grey] = "grey-128.j2c",
    };

    public const int Columns = 16, Rows = 4, Patch = 16;
    public static readonly Vector3 Origin = new(60, 60, 25);
    /// <summary>The tint on the white texture, in 8-bit sRGB (the expected output).</summary>
    public static readonly (byte R, byte G, byte B) Tint = (204, 77, 140);
    /// <summary>Grey 128 texture x grey 128 tint, composed in sRGB: round(128 * 128 / 255).</summary>
    public const byte GreyTimesGrey = 64;

    /// <summary>The patch colour (8-bit sRGB) at column c, row r (row 0 is the image's top row).</summary>
    public static (byte R, byte G, byte B) PatchColour(int c, int r)
    {
        switch (r)
        {
            case 0: { byte g = (byte)(c * 17); return (g, g, g); }   // grey ramp 0..255
            case 1:
                return c switch
                {
                    0 => (255, 0, 0), 1 => (0, 255, 0), 2 => (0, 0, 255), 3 => (0, 255, 255), 4 => (255, 0, 255), 5 => (255, 255, 0),
                    6 => (255, 255, 255), 7 => (0, 0, 0), 8 => (128, 128, 128), 9 => (64, 64, 64), 10 => (192, 192, 192), 11 => (200, 120, 80),
                    12 => (80, 160, 200), 13 => (128, 0, 0), 14 => (0, 128, 0), _ => (0, 0, 128),
                };
            case 2: { byte g = (byte)Math.Min(255, 8 + c * 17); return (g, g, g); }   // the ramp's in-between values
            default:
            {
                // mid-tones: a fixed spread of saturated and unsaturated colours
                int k = c * 37 + 11;
                return ((byte)(40 + (k * 7) % 180), (byte)(40 + (k * 13) % 180), (byte)(40 + (k * 29) % 180));
            }
        }
    }

    public static (int W, int H) Size(Guid id) => id == Chart ? (Columns * Patch, Rows * Patch) : (32, 32);

    public static byte[] Pixels(Guid id)
    {
        var (w, h) = Size(id);
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl) = id == Chart ? PatchColour(x / Patch, y / Patch) : id == White ? ((byte)255, (byte)255, (byte)255) : ((byte)128, (byte)128, (byte)128);
                int i = (y * w + x) * 4;
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = 255;
            }
        return b;
    }

    public static IReadOnlyList<ZooPrim> Make()
    {
        var o = Origin;
        var white = new FaceColors(Rgba.White);
        var flat = new Vector3(0.6f, 0.6f, 0.1f);
        FaceTextures T(Guid id, bool fullbright) => new(FaceTexture.Plain(id) with { Fullbright = fullbright });
        return
        [
            new(7101, 0, "chart-fullbright", o + new Vector3(0, 0.45f, 0), Quaternion.Identity, new Vector3(4f, 1f, 0.1f), PrimShape.Box, white, T(Chart, true)),
            new(7102, 0, "white-x-tint", o + new Vector3(-1.2f, -0.6f, 0), Quaternion.Identity, flat, PrimShape.Box,
                new FaceColors(new Rgba(Tint.R / 255f, Tint.G / 255f, Tint.B / 255f, 1)), T(White, true)),
            new(7103, 0, "grey-x-grey", o + new Vector3(0, -0.6f, 0), Quaternion.Identity, flat, PrimShape.Box,
                new FaceColors(new Rgba(128 / 255f, 128 / 255f, 128 / 255f, 1)), T(Grey, true)),
            new(7104, 0, "chart-lit", o + new Vector3(1.2f, -0.6f, 0), Quaternion.Identity, flat, PrimShape.Box, white, T(Chart, false)),
        ];
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = Format;
        var images = new JsonObject();
        foreach (var (id, f) in Images) images[id.ToString()] = f;
        root["images"] = images;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    public static (IReadOnlyList<ZooPrim> Prims, IReadOnlyDictionary<Guid, string> Images) Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if ((string?)root["format"] != Format) throw new InvalidDataException("colour chart: unknown format");
        var images = root["images"]!.AsObject().ToDictionary(kv => Guid.Parse(kv.Key), kv => (string)kv.Value!);
        root["format"] = PrimZoo.Format;
        root.Remove("images");
        return (PrimZoo.Parse(root.ToJsonString()), images);
    }

    // ---- checking a render (plain math on RGBA8 pixels; the host passes the captured image) ----

    /// <summary>The orthographic camera: straight down on <see cref="Origin"/>, north up; this vertical extent (m).</summary>
    public const float OrthoSize = 2.36f;

    public sealed record PatchError(int Column, int Row, (int R, int G, int B) Expected, (int R, int G, int B) Got, int Error);

    public sealed record Report(string Orientation, int ChartMaxError, PatchError Worst, (int R, int G, int B) TintGot, int TintError,
        (int R, int G, int B) GreyGot, int GreyError, double LitMean, int FullbrightTolerance);

    /// <summary>
    /// Measures a width x height RGBA8 render. The colour error is taken for the best-matching of the four flips so a wrong
    /// orientation shows as such (named in <see cref="Report.Orientation"/>) rather than as a colour error; the harness
    /// then requires "upright" exactly.
    /// </summary>
    public static Report Measure(byte[] rgba, int width, int height, int tolerance = 2)
    {
        float extentY = OrthoSize, extentX = OrthoSize * width / height;
        (int R, int G, int B) Sample(float sx, float sy)   // SL metres relative to Origin -> median of a 7x7 block
        {
            int px = (int)MathF.Round(sx / extentX * width + width / 2f), py = (int)MathF.Round(height / 2f - sy / extentY * height);
            var rs = new List<int>(); var gs = new List<int>(); var bs = new List<int>();
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int x = Math.Clamp(px + dx, 0, width - 1), y = Math.Clamp(py + dy, 0, height - 1), i = (y * width + x) * 4;
                    rs.Add(rgba[i]); gs.Add(rgba[i + 1]); bs.Add(rgba[i + 2]);
                }
            rs.Sort(); gs.Sort(); bs.Sort();
            return (rs[24], gs[24], bs[24]);
        }
        static int Err((int R, int G, int B) a, (int R, int G, int B) b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        string bestName = "";
        PatchError? bestWorst = null;
        foreach (var (name, flipU, flipV) in new[] { ("upright", false, false), ("mirrored", true, false), ("upside-down", false, true), ("rotated-180", true, true) })
        {
            PatchError? worst = null;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                {
                    float u = (c + 0.5f) / Columns, v = (r + 0.5f) / Rows;
                    if (flipU) u = 1 - u;
                    if (flipV) v = 1 - v;
                    var got = Sample(-2f + 4f * u, 0.95f - v);
                    var (er, eg, eb) = PatchColour(c, r);
                    var e = Err(got, (er, eg, eb));
                    if (worst is null || e > worst.Error) worst = new(c, r, (er, eg, eb), got, e);
                }
            if (bestWorst is null || worst!.Error < bestWorst.Error) { bestWorst = worst; bestName = name; }
        }
        var tint = Sample(-1.2f, -0.6f);
        var grey = Sample(0f, -0.6f);
        var lit = Sample(1.2f, -0.6f);
        return new(bestName, bestWorst!.Error, bestWorst, tint, Err(tint, (Tint.R, Tint.G, Tint.B)), grey, Err(grey, (GreyTimesGrey, GreyTimesGrey, GreyTimesGrey)),
            (lit.R + lit.G + lit.B) / 3.0, tolerance);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed colour-accuracy fixture (lossless images) and the checker's own maths.</summary>
public class ColourChartTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "colour");

    [Fact]
    public void The_committed_colour_chart_matches_its_generator_and_decodes_exactly()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_COLOUR_CHART") == "1")
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "colour-chart.colourchart.json"), ColourChart.Serialise(ColourChart.Make()));
            foreach (var (id, file) in ColourChart.Images)
            {
                var (w, h) = ColourChart.Size(id);
                File.WriteAllBytes(Path.Combine(Dir, file), LmvTexture.EncodeLossless(w, h, ColourChart.Pixels(id)));
            }
        }
        Assert.Equal(ColourChart.Serialise(ColourChart.Make()), File.ReadAllText(Path.Combine(Dir, "colour-chart.colourchart.json")).Replace("\r\n", "\n"));
        var (prims, images) = ColourChart.Parse(File.ReadAllText(Path.Combine(Dir, "colour-chart.colourchart.json")));
        Assert.Equal(4, prims.Count);
        Assert.Equal(3, prims.Count(p => p.Textures!.Default.Fullbright));
        foreach (var (id, file) in images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            Assert.Equal(ColourChart.Pixels(id), d.Texture!.Rgba);   // lossless: the exact source values
        }
        // the chart covers the grey ramp 0..255, the primaries and secondaries, and mid-tones
        Assert.Equal((0, 0, 0), ((int, int, int))ColourChart.PatchColour(0, 0));
        Assert.Equal((255, 255, 255), ((int, int, int))ColourChart.PatchColour(15, 0));
        Assert.Contains(Enumerable.Range(0, 16), c => ColourChart.PatchColour(c, 1) == (255, 0, 0));
    }

    [Fact]
    public void The_checker_finds_a_perfect_render_perfect_and_a_shifted_one_wrong()
    {
        // synthesise what an exact render would contain (upright chart, the tints as expected), then shift one patch by 3
        const int w = 640, h = 360;
        var img = Synth(w, h, (-1, -1));   // no patch shifted
        var m = ColourChart.Measure(img, w, h);
        Assert.Equal("upright", m.Orientation);
        Assert.Equal(0, m.ChartMaxError);
        Assert.Equal(0, m.TintError);
        Assert.Equal(0, m.GreyError);
        var bad = Synth(w, h, (5, 2));
        Assert.Equal(3, ColourChart.Measure(bad, w, h).ChartMaxError);
    }

    private static byte[] Synth(int w, int h, (int C, int R) shifted)
    {
        var img = new byte[w * h * 4];
        float ey = ColourChart.OrthoSize, ex = ey * w / h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sx = (x - w / 2f) / w * ex, sy = (h / 2f - y) / h * ey;
                (int, int, int) c = (100, 100, 100);
                if (sx >= -2 && sx < 2 && sy > -0.05f && sy <= 0.95f)
                {
                    int col = Math.Clamp((int)((sx + 2) / 4 * 16), 0, 15), row = Math.Clamp((int)((0.95f - sy) * 4), 0, 3);
                    var (r, g, b) = ColourChart.PatchColour(col, row);
                    c = (col, row) == shifted ? (r > 128 ? r - 3 : r + 3, g, b) : (r, g, b);
                }
                else if (Math.Abs(sy + 0.6f) < 0.3f && Math.Abs(sx + 1.2f) < 0.3f) c = (ColourChart.Tint.R, ColourChart.Tint.G, ColourChart.Tint.B);
                else if (Math.Abs(sy + 0.6f) < 0.3f && Math.Abs(sx) < 0.3f) c = (ColourChart.GreyTimesGrey, ColourChart.GreyTimesGrey, ColourChart.GreyTimesGrey);
                int i = (y * w + x) * 4;
                img[i] = (byte)c.Item1; img[i + 1] = (byte)c.Item2; img[i + 2] = (byte)c.Item3; img[i + 3] = 255;
            }
        return img;
    }
}

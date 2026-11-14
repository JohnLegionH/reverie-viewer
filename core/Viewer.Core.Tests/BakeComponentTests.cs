// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The grid's server-side bakes are 5-component codestreams: R, G, B, A and a morph mask (Tranquillity's
/// SSB, read for behaviour only). The official viewer decodes components 0-3 as the image and reads component 4 as the
/// morph mask of head, upper and lower bakes (llimagej2c, llimageworker, llvoavatar; behaviour only). Here: the guard
/// accepts 5 components (refuses 6), the image is components 0-3 at every level, and the fifth is not in it. The fixture
/// is our own content in a codestream built here.
/// </summary>
public class BakeComponentTests(ITestOutputHelper output)
{
    private static (byte[] Rgba, byte[][] Planes) Bake(int size, int planes)
    {
        var rng = new Random(5);
        var rgba = new byte[size * size * 4];
        var p = Enumerable.Range(0, planes).Select(_ => new byte[size * size]).ToArray();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                byte r = (byte)Math.Clamp(x * 255 / size + rng.Next(-20, 21), 0, 255), g = (byte)(y * 255 / size), b = 90;
                byte a = x < size / 2 ? (byte)255 : (byte)(y * 255 / size);
                byte m = (x / 64 + y / 64) % 2 == 0 ? (byte)0 : (byte)255;   // the mask: a coarse checker, unlike any colour plane
                var v = new[] { r, g, b, a, m, (byte)(255 - r) };
                for (int c = 0; c < planes; c++) p[c][i] = v[c];
                rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
            }
        return (rgba, p);
    }

    private static double Mad(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    [Fact]
    public void A_bake_with_a_morph_mask_decodes_to_its_first_four_components_at_every_level()
    {
        var (rgba, planes) = Bake(1024, 5);
        var cs = LmvTexture.EncodePlanes(1024, 1024, planes);
        Assert.Null(J2kGuard.Validate(cs, J2kLimits.Default with { MaxComponents = 16384 }, out var h));
        output.WriteLine($"{cs.Length} bytes, {h!.Components} components, {h.Layers} layers, {h.DecompositionLevels} levels");
        Assert.Equal(5, h.Components);
        var l0 = LmvTexture.Decode(cs);
        Assert.True(l0.Ok, l0.Refusal);
        var t = l0.Texture!;
        Assert.Equal((1024, 1024, 5, AlphaClass.Blended), (t.Width, t.Height, t.SourceComponents, t.Alpha));
        double d = Mad(t.Rgba, rgba);
        output.WriteLine($"level 0: {d:F2} from the source RGBA");
        Assert.True(d < 4, $"{d:F2}");
        // the mask is not in the image: alpha is the fourth plane, not the checker
        var alphaOnly = t.Rgba.Where((_, i) => i % 4 == 3).ToArray();
        Assert.True(Mad(alphaOnly, planes[3]) < 4 && Mad(alphaOnly, planes[4]) > 40);
        int count = LmvTexture.DecodeLevel(cs, 0).Texture!.LevelCount;
        Assert.Equal(6, count);
        for (int level = 1; level < count; level++)
        {
            var r = LmvTexture.DecodeLevel(cs, level);
            Assert.True(r.Ok, $"level {level}: {r.Refusal}");
            Assert.Equal((TextureLevels.Size(1024, level), 5), (r.Texture!.Width, r.Texture.SourceComponents));
            var lvl = LevelDownscale.ToLevel(1024, 1024, rgba, level, out _, out _);
            double dl = Mad(r.Texture.Rgba, lvl);
            output.WriteLine($"level {level}: {dl:F2} from the source scaled down");
            Assert.True(dl < 4, $"level {level}: {dl:F2}");
        }
    }

    [Fact]
    public void Six_components_are_still_refused()
    {
        var (_, planes) = Bake(64, 6);
        var cs = LmvTexture.EncodePlanes(64, 64, planes);
        Assert.Equal("6 components (limit 5)", J2kGuard.Validate(cs, null, out _));
        Assert.False(LmvTexture.Decode(cs).Ok);
    }
}

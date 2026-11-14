// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed orientation fixture (lossless u/v test texture) and the gradient reader.</summary>
public class OrientationZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "orientation");

    [Fact]
    public void The_committed_orientation_zoo_matches_its_generator_and_decodes_exactly()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_ORIENTATION") == "1")
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "orientation-zoo.orientation.json"), OrientationZoo.Serialise(OrientationZoo.Make()));
            foreach (var (id, file) in OrientationZoo.Images)
                File.WriteAllBytes(Path.Combine(Dir, file), LmvTexture.EncodeLossless(OrientationZoo.Size, OrientationZoo.Size, OrientationZoo.Pixels(id)));
        }
        Assert.Equal(OrientationZoo.Serialise(OrientationZoo.Make()), File.ReadAllText(Path.Combine(Dir, "orientation-zoo.orientation.json")).Replace("\r\n", "\n"));
        var (prims, images, meshes) = OrientationZoo.Parse(File.ReadAllText(Path.Combine(Dir, "orientation-zoo.orientation.json")));
        Assert.Equal(14, prims.Count);
        Assert.All(meshes.Values, f => Assert.True(File.Exists(Path.Combine(Dir, f))));
        foreach (var (id, file) in images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            Assert.Equal(OrientationZoo.Pixels(id), d.Texture!.Rgba);
        }
        // red is u (left 0 -> right 255), green is v (bottom row 0 -> top row 255)
        var px = OrientationZoo.Pixels(OrientationZoo.Texture);
        Assert.Equal((0, 255), (px[0], px[1]));                                       // top-left: u 0, v 255
        Assert.Equal((255, 0), (px[(255 * 256 + 255) * 4], px[(255 * 256 + 255) * 4 + 1]));   // bottom-right: u 255, v 0
        Assert.True(OrientationZoo.Checks().Count >= 17);
    }

    [Theory]
    [InlineData(false, false, true)]   // u right, v up: not mirrored
    [InlineData(true, false, false)]   // u left, v up: mirrored
    [InlineData(false, true, false)]   // u right, v down: mirrored
    [InlineData(true, true, true)]     // turned 180 degrees: not mirrored
    public void The_gradient_reader_tells_mirrored_from_turned(bool flipU, bool flipV, bool unmirrored)
    {
        const int w = 64, h = 64;
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int u = flipU ? 255 - x * 4 : x * 4, v = flipV ? y * 4 : 255 - y * 4;
                int i = (y * w + x) * 4;
                img[i] = (byte)Math.Clamp(u, 0, 255); img[i + 1] = (byte)Math.Clamp(v, 0, 255); img[i + 3] = 255;
            }
        var g = OrientationZoo.Gradient(img, w, h, 32, 32)!.Value;
        Assert.Equal(unmirrored, g.DuRight * g.DvUp - g.DuUp * g.DvRight > 0);
    }
}

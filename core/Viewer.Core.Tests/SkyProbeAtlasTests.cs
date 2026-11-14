// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The irradiance map reaches the face shaders as one flat picture (the host may not make an engine collection, which a cube texture needs): the six faces side by side, each with a
/// one-texel border holding what the neighbouring face has there, so a plain bilinear read inside a face gives the seamless cube read at its edges. These tests read the picture
/// the way the shader does (the cube face of the direction, then the atlas position, then two-by-two bilinear) and compare it with the cube read of the map itself.
/// </summary>
public class SkyProbeAtlasTests
{
    private static readonly SkyFrame Midday = new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f), null, 0.5f);

    private static Vector3 ReadLikeTheShader(float[] atlas, Vector3 v)
    {
        int s = SkyProbe.IrradianceSize, w = SkyProbe.AtlasWidth, h = SkyProbe.AtlasHeight, cell = s + 2;
        var (face, fs, ft) = SkyProbe.ToFace(v);
        float x = face * cell + 1 + fs * s, y = 1 + ft * s;   // pixel coordinates, texel centres at +0.5
        float u = x - 0.5f, t = y - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(t);
        float fx = u - x0, fy = t - y0;
        var acc = Vector3.Zero;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int px = Math.Clamp(x0 + i, 0, w - 1), py = Math.Clamp(y0 + j, 0, h - 1);
                int k = (py * w + px) * 3;
                acc += new Vector3(atlas[k], atlas[k + 1], atlas[k + 2]) * ((i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy));
            }
        return acc;
    }

    [Fact]
    public void The_atlas_read_like_the_shader_equals_the_cube_read_everywhere_including_the_seams()
    {
        var irr = SkyProbe.Irradiance(Midday);
        var atlas = SkyProbe.Atlas(irr);
        Assert.Equal(SkyProbe.AtlasWidth * SkyProbe.AtlasHeight * 3, atlas.Length);
        var rng = new Random(11);
        double worst = 0; string where = "";
        for (int n = 0; n < 4000; n++)
        {
            var d = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
            if (d.Length() < 0.1f) continue;
            if (n % 4 == 0) d.X = d.Y * 1.0001f;   // close to the edges between faces
            if (n % 7 == 0) d.Z = -d.Y * 0.9999f;
            d = Vector3.Normalize(d);
            var want = SkyProbe.SampleIrradiance(irr, d);
            var got = ReadLikeTheShader(atlas, d);
            double err = (want - got).Length();
            if (err > worst) { worst = err; where = $"dir {d}: want {want}, got {got}"; }
        }
        Assert.True(worst <= 1e-4, $"worst difference {worst:E2} ({where})");
    }
}

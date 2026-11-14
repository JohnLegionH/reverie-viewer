// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Viewer.Core.Residency;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// A synthetic heavy scene that measures the texture budget. 400 boxes of 1-8 m spread over
/// a 256 m region, textured with 600 distinct texture ids, each used on two object faces: 400 ids at 1024x1024 and 200 at
/// 512x512, so everything at full resolution with mipmaps needs about 2.34 GiB (GPU memory is counted per id). The ids
/// share six generated base images (our own content: patterns, not grid content), so nothing large is committed: the
/// generator is the fixture. Deterministic: the same seed gives the same scene.
/// </summary>
public static class HeavyScene
{
    public const int ObjectCount = 400, TextureCount = 600, Bases = 6;
    public const string FixtureName = "synthetic-heavy";

    public static Guid TextureId(int k) => new($"4e56008b-0000-4000-8000-{k:x12}");

    /// <summary>The base image behind texture <paramref name="k"/>: bases 0-3 are 1024x1024, 4-5 are 512x512.</summary>
    public static int BaseOf(int k) => k % Bases;
    public static int SizeOfBase(int b) => b < 4 ? 1024 : 512;

    public static long FullResolutionBytes =>
        Enumerable.Range(0, TextureCount).Sum(k => TextureFootprint.Bytes(SizeOfBase(BaseOf(k)), SizeOfBase(BaseOf(k)), true));

    /// <summary>Base image <paramref name="b"/> (RGBA8, opaque): a distinct pattern per base, with detail at every scale
    /// so each discard level looks different from the next.</summary>
    public static byte[] BasePixels(int b)
    {
        int n = SizeOfBase(b);
        var px = new byte[n * n * 4];
        var hue = new (byte R, byte G, byte B)[] { (200, 90, 60), (70, 150, 210), (90, 180, 90), (210, 190, 80), (170, 100, 190), (120, 120, 120) }[b];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double u = (double)x / n, v = (double)y / n;
                double pattern = b switch
                {
                    0 => ((x / 64 + y / 64) % 2) * 0.6 + ((x / 8 + y / 8) % 2) * 0.25,               // nested checkers
                    1 => 0.5 + 0.5 * Math.Sin(u * 40) * Math.Cos(v * 23),                             // waves
                    2 => ((x ^ y) & 0x3F) / 63.0,                                                     // xor texture
                    3 => (Math.Abs(Math.Sin(Math.Sqrt((u - .5) * (u - .5) + (v - .5) * (v - .5)) * 90)) > 0.5 ? 0.85 : 0.2), // rings
                    4 => (y / 32 % 2 == 0 ? u : 1 - u),                                               // banded gradient
                    _ => ((x % 16 == 0 || y % 16 == 0) ? 1.0 : 0.35),                                 // grid lines
                };
                int i = (y * n + x) * 4;
                px[i] = (byte)Math.Clamp(hue.R * (0.35 + 0.65 * pattern), 0, 255);
                px[i + 1] = (byte)Math.Clamp(hue.G * (0.35 + 0.65 * pattern), 0, 255);
                px[i + 2] = (byte)Math.Clamp(hue.B * (0.35 + 0.65 * pattern), 0, 255);
                px[i + 3] = 255;
            }
        return px;
    }

    /// <summary>The objects: seeded positions and sizes; object i's six faces use textures 3i, 3i+1, 3i+2 (mod 600), two faces each.</summary>
    public static IReadOnlyList<ZooPrim> Make(int seed = 17)
    {
        var rnd = new Random(seed);
        var white = new FaceColors(Rgba.White);
        var list = new List<ZooPrim>(ObjectCount);
        for (int i = 0; i < ObjectCount; i++)
        {
            float size = 1f + rnd.NextSingle() * 7f;
            var pos = new Vector3(8 + rnd.NextSingle() * 240, 8 + rnd.NextSingle() * 240, 20 + size / 2 + rnd.NextSingle() * 4);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rnd.NextSingle() * MathF.PI);
            var scale = new Vector3(size, size * (0.5f + rnd.NextSingle()), size * (0.5f + rnd.NextSingle()));
            FaceTexture T(int j) => FaceTexture.Plain(TextureId((3 * i + j) % TextureCount));
            var faces = new FaceTexture?[] { T(0), T(1), T(2), T(0), T(1), T(2) };
            list.Add(new ZooPrim((uint)(80000 + i), 0, $"heavy-{i}", pos, rot, scale, PrimShape.Box, white, new FaceTextures(T(0), faces)));
        }
        return list;
    }
}

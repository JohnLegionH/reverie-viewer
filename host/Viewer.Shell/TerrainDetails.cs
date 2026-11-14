// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// Our own procedural terrain detail textures (64x64, tiling): dirt, grass, mountain, rock tones. Used when a
/// region sets no detail texture (or one cannot be fetched), and as the committed synthetic terrain fixture's textures
/// (fixtures/terrain/detail-N.j2c, lossless). The official viewer's built-in defaults are Linden textures and are not used
/// (Docs/TERRAIN-COMPOSITION-SPEC.md 5).
/// </summary>
public static class TerrainDetails
{
    public const int Size = 64;
    public static readonly Guid Synthetic0 = new("4e56007d-0000-4000-8000-000000000020");
    public static readonly Guid Synthetic1 = new("4e56007d-0000-4000-8000-000000000021");
    public static readonly Guid Synthetic2 = new("4e56007d-0000-4000-8000-000000000022");
    public static readonly Guid Synthetic3 = new("4e56007d-0000-4000-8000-000000000023");

    public static IReadOnlyDictionary<Guid, string> SyntheticFiles => new Dictionary<Guid, string>
    {
        [Synthetic0] = "detail-0.j2c", [Synthetic1] = "detail-1.j2c", [Synthetic2] = "detail-2.j2c", [Synthetic3] = "detail-3.j2c",
    };

    /// <summary>The synthetic fixture's settings: start heights rising from the south-west, a 40 m range everywhere.</summary>
    public static RegionTerrain SyntheticTerrain => new([Synthetic0, Synthetic1, Synthetic2, Synthetic3], [4f, 10f, 8f, 16f], [40f, 40f, 40f, 40f]);

    /// <summary>Layer 0-3 pixels (RGBA8, top row first): a base tone with a tiling speckle (period 64).</summary>
    public static byte[] Pixels(int layer)
    {
        var (r, g, b) = layer switch { 0 => (120, 92, 60), 1 => (70, 118, 48), 2 => (128, 120, 108), _ => (150, 150, 155) };
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double n = Math.Sin(x * Math.PI * 2 / 16) * Math.Cos(y * Math.PI * 2 / 32) * 0.5 + Math.Sin((x * (layer + 1) + y * 3) * Math.PI * 2 / 64) * 0.5;   // tiles with period 64
                int d = (int)(n * 22);
                int i = (y * Size + x) * 4;
                px[i] = (byte)Math.Clamp(r + d, 0, 255); px[i + 1] = (byte)Math.Clamp(g + d, 0, 255); px[i + 2] = (byte)Math.Clamp(b + d, 0, 255); px[i + 3] = 255;
            }
        return px;
    }
}

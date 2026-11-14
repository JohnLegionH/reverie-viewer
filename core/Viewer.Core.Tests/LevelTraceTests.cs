// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The exact action trace for six textures seen at 305-503 px across (spec 4.2 and 4.4), as a record.</summary>
public class LevelTraceTests
{
    [Fact]
    public void Six_textures_load_coarsest_first_then_step_straight_to_the_level_their_size_needs()
    {
        var cam = new ResidencyCamera(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30, MaxLoadsPerFrame = 64 });
        var diameters = new List<int>();
        for (int i = 1; i <= 6; i++)
        {
            var b = new Bounds(new(3 + i * 0.5f, (i - 3.5f) * 0.5f, 0), 3f);
            r.SetUser(new(TextureUserKind.Face, (uint)i, 0), new Guid(i, 0, 0, new byte[8]), b);
            diameters.Add((int)cam.ScreenDiameter(b));
        }
        Assert.Equal(new[] { 503, 459, 415, 373, 336, 305 }, diameters);
        var trace = new List<string>();
        for (int f = 0; f < 5; f++)
            foreach (var a in r.Update(cam, TestClock.Frames(f)))
            {
                trace.Add($"{f}:{a.Kind}:{a.Texture.ToString()[7]}:{(a.Level == TextureResidency.Coarsest ? "coarsest" : a.Level)}");
                int lv = Math.Min(a.Level, 5);
                r.Uploaded(a.Texture, lv, 6, 1024, 1024, TextureFootprint.Bytes(TextureLevels.Size(1024, lv), TextureLevels.Size(1024, lv), true), 0, TestClock.Frames(f));
            }
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"0:Load:{i}:coarsest").Concat(Enumerable.Range(1, 6).Select(i => $"1:Level:{i}:0")), trace);
        Assert.All(r.All, id => Assert.Equal((0, 0, -1), (r.LevelOf(id), r.DesiredLevelOf(id), r.PendingLevelOf(id))));   // near the view's centre within 16 m: the camera boost wants full resolution
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The real region A sculpt maps (local-only cache) were made to look right in the official viewer, so
/// the official sampler with SL's row order must turn them into outward-facing closed surfaces.
/// </summary>
public class SculptRealMapTests(ITestOutputHelper output)
{
    [LocalFixturesFact("capture-region-a-v3.jsonl.gz", "assets")]
    public void The_real_region_a_sculpts_face_outward()
    {
        var cache = new LocalAssetCache(LiveCaptures.PathOf("assets"));
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v3.jsonl.gz")));
        var sculpts = s.World.Snapshot().Objects.Values.Where(o => o.Shape is { IsSculpt: true }).Select(o => o.Shape!).DistinctBy(x => (x.SculptId, x.SculptType, x.SculptFlags)).ToList();
        Assert.NotEmpty(sculpts);
        foreach (var sh in sculpts)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(cache.TexturePath(sh.SculptId)));
            Assert.True(d.Ok, d.Refusal);
            var f = SculptMesher.Mesh(d.Texture!.Rgba, d.Texture.Width, d.Texture.Height, sh.SculptType | sh.SculptFlags, 3).Face!;
            double v = 0;
            for (int t = 0; t < f.Indices.Length; t += 3)
            {
                Vector3 P(int k) => new(f.Positions[f.Indices[t + k] * 3], f.Positions[f.Indices[t + k] * 3 + 1], f.Positions[f.Indices[t + k] * 3 + 2]);
                v += Vector3.Dot(P(0), Vector3.Cross(P(1), P(2))) / 6.0;
            }
            output.WriteLine($"type {sh.SculptType} flags {sh.SculptFlags} map {d.Texture.Width}x{d.Texture.Height}: signed volume {v:0.0000}");
            Assert.True(v > 0, $"sculpt type {sh.SculptType} ({d.Texture.Width}x{d.Texture.Height}) faces inward: signed volume {v:0.0000}");
        }
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed sculpt zoo (our own maps, encoded with LibreMetaverse's encoder) and its layout.</summary>
public class SculptZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "sculpts");

    [Fact]
    public void The_committed_sculpt_zoo_matches_its_generator_and_its_maps_decode_and_mesh()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_SCULPT_ZOO") == "1")
        {
            Directory.CreateDirectory(Path.Combine(Dir, "zoo"));
            File.WriteAllText(Path.Combine(Dir, "sculpt-zoo.sculptzoo.json"), SculptZoo.Serialise(SculptZoo.Make()));
            foreach (var id in SculptZoo.Images.Keys.Where(k => k != TextureZoo.Grid))
            {
                var (w, h) = SculptZoo.Size(id);
                File.WriteAllBytes(Path.Combine(Dir, SculptZoo.Images[id]), LmvTexture.EncodeLossless(w, h, SculptZoo.Pixels(id)));   // lossless
            }
        }
        Assert.Equal(SculptZoo.Serialise(SculptZoo.Make()), File.ReadAllText(Path.Combine(Dir, "sculpt-zoo.sculptzoo.json")).Replace("\r\n", "\n"));
        var (prims, images) = SculptZoo.Parse(File.ReadAllText(Path.Combine(Dir, "sculpt-zoo.sculptzoo.json")));
        Assert.Equal(8, prims.Count);
        Assert.All(prims, p => Assert.True(p.Shape.IsSculpt));
        Assert.DoesNotContain(SculptZoo.MissingMap, images.Keys);
        // every type, both flags, a long thin map, a missing map
        foreach (int t in new[] { SculptMesher.Sphere, SculptMesher.Torus, SculptMesher.Plane, SculptMesher.Cylinder })
            Assert.Contains(prims, p => p.Shape.SculptType == t);
        Assert.Contains(prims, p => p.Shape.SculptFlags == SculptMesher.FlagMirror);
        Assert.Contains(prims, p => p.Shape.SculptFlags == SculptMesher.FlagInvert);
        foreach (var (id, file) in images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            if (id == TextureZoo.Grid) continue;
            Assert.Equal(SculptZoo.Size(id), (d.Texture!.Width, d.Texture.Height));
            // the committed maps are lossless: they decode to exactly the generator's pixels
            var src = SculptZoo.Pixels(id);
            for (int i = 0; i < src.Length; i += 4)
                Assert.True(src[i] == d.Texture.Rgba[i] && src[i + 1] == d.Texture.Rgba[i + 1] && src[i + 2] == d.Texture.Rgba[i + 2], $"{file}: pixel {i / 4} differs");
            var type = prims.First(p => p.Shape.SculptId == id).Shape.SculptType;
            for (int lod = 0; lod < 4; lod++)
                Assert.Equal(SculptOutcome.Map, SculptMesher.FromMap(d.Texture, false, type, lod).Outcome);
        }
        Assert.Equal(SculptOutcome.SpherePlaceholder, SculptMesher.FromMap(null, missing: true, SculptMesher.Torus, 2).Outcome);
    }
}

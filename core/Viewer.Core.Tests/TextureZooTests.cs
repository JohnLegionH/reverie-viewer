// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed texture zoo (our own images, encoded with LibreMetaverse's encoder) and its layout.</summary>
public class TextureZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "textures");

    [Fact]
    public void The_committed_texture_zoo_matches_its_generator_and_its_images_decode()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_TEXTURE_ZOO") == "1")
        {
            File.WriteAllText(Path.Combine(Dir, "texture-zoo.texzoo.json"), TextureZoo.Serialise(TextureZoo.Make()));
            foreach (var (id, file) in TextureZoo.Images)
            {
                var (rgba, alpha) = TextureZoo.Pixels(id);
                File.WriteAllBytes(Path.Combine(Dir, file), LmvTexture.Encode(64, 64, rgba, alpha));
            }
        }
        Assert.Equal(TextureZoo.Serialise(TextureZoo.Make()), File.ReadAllText(Path.Combine(Dir, "texture-zoo.texzoo.json")).Replace("\r\n", "\n"));
        var (prims, images, meshes) = TextureZoo.Parse(File.ReadAllText(Path.Combine(Dir, "texture-zoo.texzoo.json")));
        Assert.Equal(10, prims.Count);
        Assert.All(prims, p => Assert.NotNull(p.Textures));
        Assert.All(meshes.Values, f => Assert.True(File.Exists(Path.Combine(Dir, f))));
        foreach (var (id, file) in images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            Assert.Equal(id == TextureZoo.AlphaBlend ? AlphaClass.Blended : AlphaClass.Opaque, d.Texture!.Alpha);
        }
        // Covered: repeats, offset, rotation, planar, tint, fullbright, alpha texture, colour alpha, a mesh.
        Assert.Contains(prims, p => p.Textures!.Default.RepeatU == 2);
        Assert.Contains(prims, p => p.Textures!.Default.OffsetU != 0);
        Assert.Contains(prims, p => p.Textures!.Default.Rotation != 0);
        Assert.Contains(prims, p => p.Textures!.Default.TexGen == Viewer.Core.World.TexGen.Planar);
        Assert.Contains(prims, p => p.Textures!.Default.Fullbright);
        Assert.Contains(prims, p => p.Colors.Default.A < 1);
        Assert.Contains(prims, p => p.Shape.SculptType == Viewer.Core.Meshing.PrimShape.SculptTypeMesh);
    }

    [Fact]
    public void Earlier_zoo_fixtures_serialise_exactly_as_before()
    {
        // Textures are written only when present: the prim and mesh zoos (no textures) are byte-for-byte unchanged.
        Assert.DoesNotContain("\"tex\"", PrimZoo.Serialise(PrimZoo.Make()));
        Assert.DoesNotContain("\"tex\"", MeshZoo.Serialise(MeshZoo.Make()));
    }
}

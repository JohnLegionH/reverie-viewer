// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed detail-at-a-distance scene (our own image, encoded with LibreMetaverse's encoder) and its layout.</summary>
public class DetailZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "textures");
    private const string File_ = "detail-distance.texzoo.json";

    [Fact]
    public void The_committed_scene_matches_its_generator_and_its_image_decodes_at_every_level()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_DETAIL_ZOO") == "1")
        {
            File.WriteAllText(Path.Combine(Dir, File_), DetailZoo.Serialise(DetailZoo.Make()));
            File.WriteAllBytes(Path.Combine(Dir, DetailZoo.ImageFile), LmvTexture.Encode(DetailZoo.Size, DetailZoo.Size, DetailZoo.Pixels(), false));
        }
        Assert.Equal(DetailZoo.Serialise(DetailZoo.Make()), File.ReadAllText(Path.Combine(Dir, File_)).Replace("\r\n", "\n"));
        var (prims, images, meshes) = TextureZoo.Parse(File.ReadAllText(Path.Combine(Dir, File_)));
        Assert.Equal(8, prims.Count);
        Assert.Equal(4, images.Count);
        Assert.All(meshes.Values, f => Assert.True(File.Exists(Path.Combine(Dir, f))));
        var bytes = File.ReadAllBytes(Path.Combine(Dir, DetailZoo.ImageFile));
        var d = LmvTexture.Decode(bytes);
        Assert.True(d.Ok, d.Refusal);
        Assert.Equal((DetailZoo.Size, DetailZoo.Size), (d.Texture!.Width, d.Texture.Height));
        // the walls face the camera: each is in view and nothing nearer stands in front of it
        foreach (var p in prims)
            Assert.InRange(MathF.Abs(MathF.Atan2(p.Position.Y - DetailZoo.Camera.Y, p.Position.X - DetailZoo.Camera.X)) * 180f / MathF.PI, 2f, 33f);
    }
}

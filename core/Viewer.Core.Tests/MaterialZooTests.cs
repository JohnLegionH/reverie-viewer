// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed material zoo (our own images and material assets) and the material source that reads them.</summary>
public class MaterialZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "materials");

    [Fact]
    public void The_committed_material_zoo_matches_its_generator_and_its_images_and_assets_read()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_MATERIAL_ZOO") == "1")
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "material-zoo.matzoo.json"), MaterialZoo.Serialise(MaterialZoo.Make()));
            foreach (var (id, file) in MaterialZoo.Images)
                File.WriteAllBytes(Path.Combine(Dir, file), LmvTexture.Encode(64, 64, MaterialZoo.Pixels(id), false));
            foreach (var (id, file) in MaterialZoo.Materials)
                File.WriteAllText(Path.Combine(Dir, file), MaterialZoo.Asset(id));
        }
        Assert.Equal(MaterialZoo.Serialise(MaterialZoo.Make()), File.ReadAllText(Path.Combine(Dir, "material-zoo.matzoo.json")).Replace("\r\n", "\n"));
        var z = MaterialZoo.Parse(File.ReadAllText(Path.Combine(Dir, "material-zoo.matzoo.json")));
        Assert.Equal(10, z.Prims.Count);
        Assert.Equal(41, z.RenderMaterials.Values.Sum(v => v.Count));
        Assert.All(z.Meshes.Values, f => Assert.True(File.Exists(Path.Combine(Dir, f))));
        foreach (var (_, file) in z.Images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            Assert.Equal(AlphaClass.Opaque, d.Texture!.Alpha);
        }
        foreach (var (id, file) in z.Materials)
        {
            Assert.Equal(MaterialZoo.Asset(id), File.ReadAllText(Path.Combine(Dir, file)).Replace("\r\n", "\n"));
            var gltf = LmvMaterials.GltfOfMaterialAsset(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.NotNull(gltf);
            var m = PbrMaterial.FromGltf(gltf!);
            Assert.NotNull(m);
            Assert.All(m!.Textures(), t => Assert.True(z.Images.ContainsKey(t.Id), file + " names a texture the zoo lacks"));
        }
    }

    [Fact]
    public void The_committed_alpha_zoo_matches_its_generator_and_covers_every_alpha_mode()
    {
        const string name = "alpha-zoo.matzoo.json";
        if (Environment.GetEnvironmentVariable("NV_WRITE_MATERIAL_ZOO") == "1")
        {
            File.WriteAllText(Path.Combine(Dir, name), MaterialZoo.SerialiseAlpha());
            foreach (var id in new[] { MaterialZoo.AlphaTex, MaterialZoo.SpecTex })
                File.WriteAllBytes(Path.Combine(Dir, MaterialZoo.AlphaImages[id]), LmvTexture.Encode(64, 64, MaterialZoo.Pixels(id), MaterialZoo.HasAlpha(id)));
            foreach (var (id, file) in MaterialZoo.AlphaMaterials)
                File.WriteAllText(Path.Combine(Dir, file), MaterialZoo.Asset(id));
        }
        Assert.Equal(MaterialZoo.SerialiseAlpha(), File.ReadAllText(Path.Combine(Dir, name)).Replace("\r\n", "\n"));
        var z = MaterialZoo.Parse(File.ReadAllText(Path.Combine(Dir, name)));
        Assert.Equal(12, z.Prims.Count);
        Assert.Equal(MaterialZoo.LegacyMaterials(), z.LegacyMaterials);
        Assert.Equal(MaterialZoo.AlphaFaceMaterials(), z.FaceMaterials);
        // every legacy alpha mode, a normal and a specular map, and every PBR alpha mode, single- and double-sided
        Assert.Equal(Enum.GetValues<LegacyAlphaMode>().ToHashSet(), z.LegacyMaterials.Select(m => m.DiffuseAlphaMode).ToHashSet());
        Assert.Contains(z.LegacyMaterials, m => m.Normal.TextureId != Guid.Empty && m.Specular.TextureId != Guid.Empty);
        Assert.Contains(z.LegacyMaterials, m => m.Normal.TextureId != Guid.Empty && m.Specular.TextureId == Guid.Empty);
        var pbr = z.Materials.ToDictionary(kv => kv.Key, kv => PbrMaterial.FromGltf(LmvMaterials.GltfOfMaterialAsset(File.ReadAllBytes(Path.Combine(Dir, kv.Value)))!)!);
        Assert.Equal(new HashSet<PbrAlphaMode> { PbrAlphaMode.Blend, PbrAlphaMode.Mask }, pbr.Values.Select(m => m.AlphaMode).ToHashSet());
        Assert.Contains(pbr.Values, m => m.AlphaMode == PbrAlphaMode.Mask && m.DoubleSided);
        Assert.Contains(pbr.Values, m => m.AlphaMode == PbrAlphaMode.Mask && !m.DoubleSided && Math.Abs(m.AlphaCutoff - 0.55f) < 1e-6);
        foreach (var (id, file) in z.Images)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, file)));
            Assert.True(d.Ok, file + ": " + d.Refusal);
            Assert.Equal(MaterialZoo.HasAlpha(id) ? AlphaClass.Blended : AlphaClass.Opaque, d.Texture!.Alpha);
        }
    }

    [Fact]
    public void The_zoo_covers_each_part_of_a_pbr_material()
    {
        var m = MaterialZoo.Materials.Keys.ToDictionary(id => id, id => PbrMaterial.FromGltf(MaterialZoo.Gltf(id))!);
        Assert.Contains(m.Values, x => x.BaseColorTexture != Guid.Empty);
        Assert.Contains(m.Values, x => x.NormalTexture != Guid.Empty);
        Assert.Contains(m.Values, x => x.MetallicRoughnessTexture != Guid.Empty);
        Assert.Contains(m.Values, x => x.EmissiveTexture != Guid.Empty && x.Emissive != new Rgba(0, 0, 0, 1));
        Assert.Contains(m.Values, x => x.Textures().Count() == 0 && x.Metallic == 1);
        Assert.Contains(m.Values, x => x.BaseColorTransform != TextureTransform.Identity);
        // the override box: the base texture, base colour and rotation replaced, the rest kept
        var o = m[MaterialZoo.MBase].WithOverride(MaterialZoo.OverrideJson);
        Assert.Equal(MaterialZoo.AltBaseTex, o.BaseColorTexture);
        Assert.Equal(new Rgba(1, 0.85f, 0.6f, 1), o.BaseColor);
        Assert.Equal(1.5707964f, o.BaseColorTransform.Rotation);
        Assert.Equal(m[MaterialZoo.MBase].Roughness, o.Roughness);
    }

    [Fact]
    public void The_material_source_reads_an_asset_off_the_main_thread_and_reports_its_arrival()
    {
        int fetches = 0;
        using var src = new MaterialSource((id, _) =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(MaterialZoo.Materials.ContainsKey(id)
                ? AssetFetchResult.Ok(System.Text.Encoding.UTF8.GetBytes(MaterialZoo.Asset(id)))
                : AssetFetchResult.Of(AssetFetchStatus.NotFound));
        });
        var missing = Guid.NewGuid();
        Assert.Equal(Viewer.Shell.MeshAvailability.Pending, src.TryGet(MaterialZoo.MNormal, out _));
        Assert.Equal(Viewer.Shell.MeshAvailability.Pending, src.TryGet(missing, out _));
        Assert.Equal(Viewer.Shell.MeshAvailability.Pending, src.TryGet(MaterialZoo.MNormal, out _));   // one fetch per id
        var arrived = new HashSet<Guid>();
        SpinWait.SpinUntil(() => { while (src.TryTakeArrival(out var a)) arrived.Add(a); return arrived.Count == 2; }, TimeSpan.FromSeconds(30));
        Assert.Equal(new HashSet<Guid> { MaterialZoo.MNormal, missing }, arrived);
        Assert.Equal(2, fetches);
        Assert.Equal(Viewer.Shell.MeshAvailability.Ready, src.TryGet(MaterialZoo.MNormal, out var m));
        Assert.Equal(MaterialZoo.NormalTex, m!.NormalTexture);
        Assert.Equal(Viewer.Shell.MeshAvailability.Unavailable, src.TryGet(missing, out var none));
        Assert.Null(none);
        Assert.Equal(1, src.Ready);
        Assert.Equal(1, src.Unavailable);
    }

    [Theory]
    [InlineData("not llsd")]
    [InlineData("<?xml version=\"1.0\" ?><llsd><map><key>version</key><string>1.1</string><key>type</key><string>GLTF 2.0</string><key>data</key><string>{}</string></map></llsd>")]
    public void An_asset_that_is_not_a_material_is_unavailable(string body)
    {
        using var src = new MaterialSource((_, _) => Task.FromResult(AssetFetchResult.Ok(System.Text.Encoding.UTF8.GetBytes(body))));
        var id = Guid.NewGuid();
        src.TryGet(id, out _);
        SpinWait.SpinUntil(() => src.TryTakeArrival(out _), TimeSpan.FromSeconds(30));
        Assert.Equal(Viewer.Shell.MeshAvailability.Unavailable, src.TryGet(id, out _));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A PBR material read from its glTF document and a face's glTF override applied over it, as the official viewer
/// applies one (behaviour: llgltfmaterial.cpp, applyOverride and applyOverrideLLSD).
/// </summary>
public class PbrMaterialTests
{
    private static readonly Guid Base = Guid.Parse("7b1e0c52-3f4a-4d6e-9b21-0a5c8e3f1d01");
    private static readonly Guid Orm = Guid.Parse("7b1e0c52-3f4a-4d6e-9b21-0a5c8e3f1d02");
    private static readonly Guid Normal = Guid.Parse("7b1e0c52-3f4a-4d6e-9b21-0a5c8e3f1d03");
    private static readonly Guid Glow = Guid.Parse("7b1e0c52-3f4a-4d6e-9b21-0a5c8e3f1d04");
    private static readonly Guid Other = Guid.Parse("7b1e0c52-3f4a-4d6e-9b21-0a5c8e3f1d05");

    private static string Gltf(string material) =>
        "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"" + Base + "\"},{\"uri\":\"" + Orm + "\"},{\"uri\":\"" + Normal + "\"},{\"uri\":\"" + Glow + "\"}]," +
        "\"textures\":[{\"source\":0},{\"source\":1},{\"source\":2},{\"source\":3}],\"materials\":[" + material + "]}";

    private const string Full =
        "{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.5,0.25,1,0.75],\"metallicFactor\":0.2,\"roughnessFactor\":0.6," +
        "\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":1}},\"normalTexture\":{\"index\":2}," +
        "\"occlusionTexture\":{\"index\":1},\"emissiveTexture\":{\"index\":3},\"emissiveFactor\":[0.25,0.5,1]," +
        "\"alphaMode\":\"MASK\",\"alphaCutoff\":0.3,\"doubleSided\":true}";

    [Fact]
    public void A_material_reads_its_factors_textures_and_flags_from_the_gltf_document()
    {
        var m = PbrMaterial.FromGltf(Gltf(Full))!;
        Assert.Equal(new Rgba(0.5f, 0.25f, 1, 0.75f), m.BaseColor);
        Assert.Equal(0.2f, m.Metallic);
        Assert.Equal(0.6f, m.Roughness);
        Assert.Equal(new Rgba(0.25f, 0.5f, 1, 1), m.Emissive);
        Assert.Equal(PbrAlphaMode.Mask, m.AlphaMode);
        Assert.Equal(0.3f, m.AlphaCutoff);
        Assert.True(m.DoubleSided);
        Assert.Equal(Base, m.BaseColorTexture);
        Assert.Equal(Orm, m.MetallicRoughnessTexture);
        Assert.Equal(Normal, m.NormalTexture);
        Assert.Equal(Glow, m.EmissiveTexture);
        Assert.Equal(4, m.Textures().Count());
    }

    [Fact]
    public void Absent_fields_take_the_gltf_defaults()
    {
        var m = PbrMaterial.FromGltf(Gltf("{}"))!;
        Assert.Equal(Rgba.White, m.BaseColor);
        Assert.Equal(1f, m.Metallic);
        Assert.Equal(1f, m.Roughness);
        Assert.Equal(new Rgba(0, 0, 0, 1), m.Emissive);
        Assert.Equal(PbrAlphaMode.Opaque, m.AlphaMode);
        Assert.Equal(0.5f, m.AlphaCutoff);
        Assert.False(m.DoubleSided);
        Assert.Empty(m.Textures());
        foreach (var s in PbrMaterial.Slots) Assert.Equal(TextureTransform.Identity, m.Transform(s));
        Assert.Equal(new PbrMaterial(), m);   // value equality: one engine material per resolved material
    }

    [Fact]
    public void A_slots_khr_texture_transform_is_read()
    {
        var m = PbrMaterial.FromGltf(Gltf("{\"normalTexture\":{\"index\":2,\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.25,0.5],\"scale\":[2,3],\"rotation\":1.5}}}}"))!;
        Assert.Equal(new TextureTransform(0.25f, 0.5f, 2, 3, 1.5f), m.NormalTransform);
        Assert.Equal(TextureTransform.Identity, m.BaseColorTransform);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"materials\":[]}")]
    [InlineData("{\"materials\":[1]}")]
    public void A_document_without_a_material_is_refused(string json) => Assert.Null(PbrMaterial.FromGltf(json));

    [Fact]
    public void A_texture_index_out_of_range_or_a_uri_that_is_not_an_id_leaves_the_slot_empty()
    {
        var m = PbrMaterial.FromGltf("{\"images\":[{\"uri\":\"picture.png\"}],\"textures\":[{\"source\":0}],\"materials\":[{\"normalTexture\":{\"index\":0},\"emissiveTexture\":{\"index\":7}}]}")!;
        Assert.Empty(m.Textures());
    }

    [Fact]
    public void An_override_texture_replaces_the_slot_the_clear_id_empties_it_and_an_empty_id_keeps_it()
    {
        var m = PbrMaterial.FromGltf(Gltf(Full))!;
        var o = m.WithOverride("{\"tex\":[\"" + Other + "\",\"ffffffff-ffff-ffff-ffff-ffffffffffff\",\"00000000-0000-0000-0000-000000000000\",null]}");
        Assert.Equal(Other, o.BaseColorTexture);
        Assert.Equal(Guid.Empty, o.NormalTexture);
        Assert.Equal(Orm, o.MetallicRoughnessTexture);
        Assert.Equal(Glow, o.EmissiveTexture);
    }

    [Fact]
    public void Override_factors_and_flags_replace_the_assets_when_present()
    {
        var m = PbrMaterial.FromGltf(Gltf(Full))!;
        var o = m.WithOverride("{\"bc\":[1,0,0,1],\"ec\":[0,0,0],\"mf\":1.0,\"rf\":0.0,\"am\":0,\"ac\":0.5,\"ds\":false}");
        Assert.Equal(new Rgba(1, 0, 0, 1), o.BaseColor);
        Assert.Equal(new Rgba(0, 0, 0, 1), o.Emissive);
        Assert.Equal(1f, o.Metallic);
        Assert.Equal(0f, o.Roughness);
        Assert.Equal(PbrAlphaMode.Opaque, o.AlphaMode);
        Assert.Equal(0.5f, o.AlphaCutoff);
        Assert.False(o.DoubleSided);
        Assert.Equal(m.Textures(), o.Textures());   // no "tex": the asset's textures stay
    }

    [Fact]
    public void An_override_transform_replaces_only_the_parts_that_are_not_the_default()
    {
        var m = PbrMaterial.FromGltf(Gltf("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.1,0.2],\"scale\":[4,4],\"rotation\":0.3}}}}}"))!;
        // region A's shape: a rotation of 90 degrees, offset 0 and scale 1, on the base colour slot only
        var o = m.WithOverride("{\"ti\":[{\"o\":[0,0],\"s\":[1,1],\"r\":1.5707964},{},null]}");
        Assert.Equal(new TextureTransform(0.1f, 0.2f, 4, 4, 1.5707964f), o.BaseColorTransform);
        Assert.Equal(TextureTransform.Identity, o.NormalTransform);
        var p = m.WithOverride("{\"ti\":[{\"o\":[0.5,0],\"s\":[2,1]}]}");
        Assert.Equal(new TextureTransform(0.5f, 0, 2, 1, 0.3f), p.BaseColorTransform);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("{\"tex\":\"x\",\"bc\":[1,1],\"am\":7,\"ti\":{}}")]
    public void An_empty_or_malformed_override_changes_nothing(string? json)
    {
        var m = PbrMaterial.FromGltf(Gltf(Full))!;
        Assert.Equal(m, m.WithOverride(json));
    }
}

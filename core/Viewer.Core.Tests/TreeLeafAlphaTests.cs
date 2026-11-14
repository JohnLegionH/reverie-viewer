// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A tree's leaves are cut out, not blended: the official viewer's tree shader drops every pixel whose alpha is under 0.5 (the minimum alpha
/// its tree pass sets), and draws the rest solid.
/// </summary>
public class TreeLeafAlphaTests
{
    private static readonly SimRef Home = new(256000UL << 32 | 256000UL, "Home");

    [Theory]
    [InlineData(AlphaClass.Binary)]
    [InlineData(AlphaClass.Blended)]
    public void A_tree_face_with_an_alpha_texture_is_masked_at_half_not_blended(AlphaClass texture)
    {
        var look = FaceLooks.Legacy(TreeMesh.LeafMaterial, 1f, texture);
        Assert.Equal(FaceAlpha.Mask, look.Alpha);
        Assert.InRange(look.Cutoff, 0.495f, 0.505f);
    }

    [Fact]
    public void A_tree_face_with_a_solid_texture_is_opaque()
        => Assert.Equal(FaceAlpha.Opaque, FaceLooks.Legacy(TreeMesh.LeafMaterial, 1f, AlphaClass.Opaque).Alpha);

    [Fact]
    public void The_tree_prim_names_the_leaf_material_on_its_one_face()
    {
        var prim = TreeBuilder.Build(new TreeData(7, Guid.NewGuid(), 1, System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, new(4, 4, 4)))[0];
        Assert.NotNull(prim.Materials);
        Assert.Equal(TreeMesh.LeafMaterialId, prim.Materials![0].MaterialId);
    }

    [Fact]
    public void A_world_that_takes_a_tree_knows_its_material()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Home, new RegionInfo("Home", 1000, 1000, 256, 256, true));
        w.ApplyTree(Home, new TreeData(7, Guid.NewGuid(), 1, System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, new(4, 4, 4)));
        var m = w.LegacyMaterial(TreeMesh.LeafMaterialId);
        Assert.NotNull(m);
        Assert.Equal(LegacyAlphaMode.Mask, m!.DiffuseAlphaMode);
    }

    [Fact]
    public void A_tree_face_has_no_specular_highlight_as_the_official_tree_shader_has_none()
    {
        var look = FaceLooks.Legacy(TreeMesh.LeafMaterial, 1f, AlphaClass.Binary);
        Assert.Equal(new Rgba(0, 0, 0, 0), look.SpecularColour);
    }

    [Fact]
    public void Any_other_material_without_a_specular_map_takes_the_faces_shiny_level()
    {
        var other = new LegacyMaterial(Guid.NewGuid(), default, default, new Rgba(0.2f, 0.2f, 0.2f, 1f), 51, 0, LegacyAlphaMode.Mask, 128);
        Assert.Equal(new Rgba(0.5f, 0.5f, 0.5f, 0.5f), FaceLooks.Legacy(other, 1f, AlphaClass.Binary, 2).SpecularColour);
        Assert.Equal(new Rgba(0f, 0f, 0f, 0f), FaceLooks.Legacy(other, 1f, AlphaClass.Binary).SpecularColour);
    }
}

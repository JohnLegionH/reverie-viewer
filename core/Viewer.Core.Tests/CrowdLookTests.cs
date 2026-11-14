// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The realistic crowd's looks and bake pictures: made from the seed alone, a mix of shapes, bakes at the server sizes, alpha bakes
/// that hide exactly the regions they are made for. Pure; parallel (no shared state).</summary>
public class CrowdLookTests
{
    private const int Seed = 8;

    [Fact]
    public void The_same_seed_gives_the_same_crowd_and_another_seed_a_different_one()
    {
        var a = CrowdLooks.MakeAll(Seed, 40);
        var b = CrowdLooks.MakeAll(Seed, 40);
        Assert.Equal(40, a.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Male, b[i].Male);
            Assert.Equal(a[i].MeshBody, b[i].MeshBody);
            Assert.True(a[i].Params.SameAs(b[i].Params), $"avatar {i}: the shape differs between two runs of one seed");
            Assert.Equal(a[i].Bakes, b[i].Bakes);
        }
        var c = CrowdLooks.MakeAll(Seed + 1, 40);
        Assert.Contains(Enumerable.Range(0, 40), i => !a[i].Params.SameAs(c[i].Params));
        Assert.Contains(Enumerable.Range(0, 40), i => a[i].Bakes[0].Id != c[i].Bakes[0].Id);
    }

    [Fact]
    public void Every_avatar_has_a_shape_a_mix_of_sexes_and_sliders_that_vary()
    {
        var looks = CrowdLooks.MakeAll(Seed, 100);
        Assert.All(looks, l =>
        {
            Assert.True(l.Params.Count >= 20, $"avatar {l.Index}: only {l.Params.Count} shape parameters");
            Assert.True(l.Params.TryGet(VisualParamSet.MaleParamId, out var male));
            Assert.Equal(l.Male, male > 127);
            Assert.Equal(l.Params.WireOrder.OrderBy(x => x), l.Params.WireOrder);   // ascending id on the wire
            Assert.Equal(l.Params.Raw.Length, l.Params.WireOrder.Length);
        });
        Assert.InRange(looks.Count(l => l.Male), 30, 70);
        foreach (int id in new[] { 33, 637, 649, 682 })   // height, body fat, torso muscles, head size
        {
            var values = looks.Select(l => l.Params.TryGet(id, out var v) ? (int)v : -1).Distinct().Count();
            Assert.True(values >= 20, $"slider {id}: only {values} different values in 100 avatars");
        }
        Assert.All(looks.Where(l => l.Male), l => Assert.False(l.Params.TryGet(105, out _)));   // no breast slider on a male shape
    }

    [Fact]
    public void Bakes_come_at_the_server_sizes_under_their_own_ids()
    {
        var looks = CrowdLooks.MakeAll(Seed, 60);
        Assert.All(looks, l =>
        {
            Assert.Equal(new[] { "head", "upper", "lower", "eyes", "hair" }, l.Bakes.Select(b => b.Slot));
            Assert.All(l.Bakes, b => Assert.Equal(b.Slot switch { "eyes" => 256, "hair" => 512, _ => 1024 }, b.Size));
            Assert.All(l.Bakes, b => Assert.InRange(b.Variant, 0, CrowdLooks.PictureVariants - 1));
        });
        var ids = looks.SelectMany(l => l.Bakes).Select(b => b.Id).ToList();
        Assert.Equal(60 * 5, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void About_half_wear_a_mesh_body_and_its_alpha_bakes_hide_the_regions_the_body_covers()
    {
        var looks = CrowdLooks.MakeAll(Seed, 100);
        Assert.InRange(looks.Count(l => l.MeshBody), 30, 70);
        foreach (var l in looks)
        {
            if (l.MeshBody)
            {
                Assert.True(l.Bakes.First(b => b.Slot == "upper").Hides);
                Assert.True(l.Bakes.First(b => b.Slot == "lower").Hides);
                Assert.False(l.Bakes.First(b => b.Slot == "eyes").Hides);
                Assert.False(l.Bakes.First(b => b.Slot == "hair").Hides);
                Assert.Contains("upper", l.HiddenSlots);
                Assert.Contains("lower", l.HiddenSlots);
            }
            else
            {
                Assert.DoesNotContain(l.Bakes, b => b.Hides);
                Assert.Empty(l.HiddenSlots);
            }
            Assert.Equal(l.AlphaSlots.Union(l.MeshBodySlots).OrderBy(s => s), l.HiddenSlots.OrderBy(s => s));
        }
        Assert.Contains(looks, l => l.MeshBody && l.AlphaSlots.Contains("head"));   // some hide the head by alpha alone
        Assert.Contains(looks, l => l.MeshBody && !l.AlphaSlots.Contains("head"));
    }

    [Fact]
    public void The_appearance_message_carries_the_shape_and_each_bake_on_its_face()
    {
        var look = CrowdLooks.Make(Seed, 3);
        var agent = Guid.NewGuid();
        var data = CrowdLooks.Appearance(agent, look);
        Assert.Equal(agent, data.AgentId);
        Assert.Same(look.Params, data.VisualParams);
        Assert.Equal(45, data.FaceTextures.Length);
        for (int bt = 0; bt < AppearanceSlots.Count; bt++)
        {
            var bake = look.Bakes.FirstOrDefault(b => b.Slot == AppearanceSlots.SlotOf(bt));
            Assert.Equal(bake?.Id ?? Guid.Empty, data.FaceTextures[AppearanceSlots.FaceOf(bt)]);
        }
    }

    [Theory]
    [InlineData("eyes", 256, false)]
    [InlineData("hair", 512, false)]
    [InlineData("upper", 1024, true)]
    public void A_bake_picture_decodes_at_its_size_and_is_opaque_or_wholly_transparent(string slot, int size, bool hides)
    {
        var bytes = CrowdBakeImages.Make(Seed, slot, size, 1, hides);
        Assert.NotEmpty(bytes);
        var r = LmvTexture.Decode(bytes);
        Assert.NotNull(r.Texture);
        Assert.Equal(size, r.Texture!.Width);
        Assert.Equal(size, r.Texture.Height);
        Assert.Equal(hides, BakeAlpha.HidesRegion(r.Texture.Rgba));
        if (!hides) Assert.All(Enumerable.Range(0, 64), i => Assert.True(BakeAlpha.IsDrawn(r.Texture.Rgba[i * 4 + 3])));
        // real detail: the picture is not a flat colour
        Assert.True(Enumerable.Range(0, 400).Select(i => (int)r.Texture.Rgba[i * 4]).Distinct().Count() > 40);
    }

    [Fact]
    public void A_picture_is_the_same_for_the_same_seed_and_differs_between_variants()
    {
        var a = CrowdBakeImages.Make(Seed, "eyes", 256, 0, false);
        var b = CrowdBakeImages.Make(Seed, "eyes", 256, 0, false);
        var c = CrowdBakeImages.Make(Seed, "eyes", 256, 1, false);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void The_pool_holds_every_picture_the_looks_need()
    {
        var looks = CrowdLooks.MakeAll(Seed, 6);
        Assert.Equal(6, looks.Length);
        var pool = CrowdBakeImages.MakePool(Seed, looks);
        Assert.NotEmpty(pool);
        Assert.All(pool.Values, bytes => Assert.NotEmpty(bytes));
        foreach (var b in looks.SelectMany(l => l.Bakes)) Assert.True(pool.ContainsKey((b.Slot, b.Variant, b.Hides)), $"no picture for {b.Slot} variant {b.Variant} hides {b.Hides}");
    }
}

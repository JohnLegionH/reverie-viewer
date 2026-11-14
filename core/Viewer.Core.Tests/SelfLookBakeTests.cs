// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// How an avatar's bakes reach its body and the meshes it wears, in rules that need no engine: the alpha cut of the body's bakes,
/// bake on mesh (a face naming one of the protocol's bake placeholder ids shows the wearer's own bake for that slot, and the
/// default texture while that bake has not arrived, never plain white), and which body parts a worn bake face hides. The
/// viewer's own avatar goes through the same appearance path as every other avatar. Pure and synthetic, parallel (no process-wide state).
/// </summary>
public class SelfLookBakeTests
{
    private static readonly Guid Plywood = Guid.Parse("89556747-24cb-43ed-920b-47caed15465f");

    // ---------------- the alpha cut ----------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]    // 50/255 = 0.196, below the cut
    [InlineData(51, true)]     // 51/255 = 0.2 exactly: kept (the cut discards only what is below it)
    [InlineData(255, true)]
    public void A_pixel_is_drawn_when_its_alpha_is_not_below_one_fifth(byte alpha, bool drawn) =>
        Assert.Equal(drawn, BakeAlpha.IsDrawn(alpha));

    [Fact]
    public void A_fully_transparent_bake_hides_its_region()
    {
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 200; rgba[i + 1] = 150; rgba[i + 2] = 120; rgba[i + 3] = 0; }
        Assert.True(BakeAlpha.HidesRegion(rgba));
    }

    [Fact]
    public void A_bake_with_one_drawn_pixel_does_not_hide_its_region()
    {
        var rgba = new byte[4 * 4 * 4];
        rgba[3 * 4 + 3] = 255;
        Assert.False(BakeAlpha.HidesRegion(rgba));
    }

    [Fact]
    public void An_opaque_bake_and_a_bake_without_an_alpha_plane_hide_nothing()
    {
        var opaque = new byte[16];
        for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        Assert.False(BakeAlpha.HidesRegion(opaque));
        Assert.False(BakeAlpha.HidesRegion(ReadOnlySpan<byte>.Empty));   // nothing decoded yet: nothing is hidden by it
    }

    // ---------------- bake on mesh ----------------

    public static TheoryData<int, string> Slots() => new()
    {
        { 0, "head" }, { 1, "upper" }, { 2, "lower" }, { 3, "eyes" }, { 4, "skirt" }, { 5, "hair" },
        { 6, "leftarm" }, { 7, "leftleg" }, { 8, "aux1" }, { 9, "aux2" }, { 10, "aux3" },
    };

    [Theory]
    [MemberData(nameof(Slots))]
    public void Every_placeholder_names_the_slot_of_its_bake_type(int bakeType, string slot)
    {
        Guid placeholder = BakeChannels.IdOf(slot);
        Assert.True(BakeChannels.IsBakeId(placeholder));
        Assert.Equal(slot, BakeChannels.SlotOf(placeholder));
        Assert.Equal(AppearanceSlots.SlotOf(bakeType), slot);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void A_face_naming_a_placeholder_draws_the_wearers_bake_for_that_slot(int bakeType, string slot)
    {
        var bake = Guid.NewGuid();
        var other = Guid.NewGuid();
        var bakes = new Dictionary<string, Guid> { [slot] = bake, ["aux3"] = other };
        if (slot == "aux3") bakes["aux3"] = bake;
        var t = BakeOnMesh.Resolve(BakeChannels.IdOf(slot), bakes);
        Assert.Equal(BomKind.Bake, t.Kind);
        Assert.Equal(bake, t.Id);
        Assert.Equal(slot, t.Slot);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void A_bake_that_is_not_there_draws_the_default_texture_never_white(int bakeType, string slot)
    {
        var t = BakeOnMesh.Resolve(BakeChannels.IdOf(slot), new Dictionary<string, Guid>());
        Assert.Equal(BomKind.Fallback, t.Kind);
        Assert.Equal(Plywood, t.Id);
        Assert.Equal(slot, t.Slot);
        Assert.Equal(Plywood, BakeOnMesh.FallbackTexture);
    }

    [Fact]
    public void An_ordinary_texture_is_left_alone()
    {
        var id = Guid.NewGuid();
        var t = BakeOnMesh.Resolve(id, new Dictionary<string, Guid> { ["head"] = Guid.NewGuid() });
        Assert.Equal(BomKind.NotBake, t.Kind);
        Assert.Equal(id, t.Id);
    }

    [Fact]
    public void An_empty_bake_id_counts_as_not_there()
    {
        var t = BakeOnMesh.Resolve(BakeChannels.IdOf("hair"), new Dictionary<string, Guid> { ["hair"] = Guid.Empty });
        Assert.Equal(BomKind.Fallback, t.Kind);
    }

    // ---------------- what a worn bake face hides ----------------

    [Fact]
    public void A_worn_face_naming_a_body_bake_hides_that_regions_body_parts()
    {
        var hidden = BodyVisibility.HiddenSlots([Guid.NewGuid(), BakeChannels.IdOf("upper"), BakeChannels.IdOf("eyes")]);
        Assert.Equal(new[] { "eyes", "upper" }, hidden.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void The_extra_slots_hide_no_body_part()
    {
        var hidden = BodyVisibility.HiddenSlots([BakeChannels.IdOf("leftarm"), BakeChannels.IdOf("leftleg"), BakeChannels.IdOf("aux1"), BakeChannels.IdOf("aux2"), BakeChannels.IdOf("aux3")]);
        Assert.Empty(hidden);
    }

    [Fact]
    public void Nothing_worn_hides_nothing() => Assert.Empty(BodyVisibility.HiddenSlots([]));

    // ---------------- the viewer's own avatar is an avatar like the rest ----------------

    [Fact]
    public void The_own_avatar_and_another_take_their_bakes_the_same_way()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        var sim = new object();
        world.ChooseAppearanceMode("Test Region", serverBaked: true);
        ImmutableArray<Guid> Faces(Guid head, Guid upper)
        {
            var f = new Guid[45];
            f[AppearanceSlots.FaceOf(0)] = head;
            f[AppearanceSlots.FaceOf(1)] = upper;
            return [.. f];
        }
        var (h1, u1, h2, u2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var parms = new VisualParamSet(new byte[218], ImmutableArray<int>.Empty);
        world.ApplyAppearance(sim, new AvatarAppearanceData(self, Faces(h1, u1), parms, 1, 12), self);
        world.ApplyAppearance(sim, new AvatarAppearanceData(other, Faces(h2, u2), parms, 1, 7), self);
        var mine = world.Appearance(self)!;
        var theirs = world.Appearance(other)!;
        Assert.True(mine.IsSelf);
        Assert.False(theirs.IsSelf);
        Assert.Equal(h1, mine.Bakes["head"]);
        Assert.Equal(u2, theirs.Bakes["upper"]);
        Assert.Equal(mine.Bakes.Keys.OrderBy(k => k), theirs.Bakes.Keys.OrderBy(k => k));
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The appearances another avatar's region sends one after another, applied as the official viewer applies them
/// (LLVOAvatar::processAvatarAppearance and applyParsedAppearanceMessage, llvoavatar.cpp): the latest bakes are the ones drawn, a baked
/// texture the message leaves undefined (no id, or the viewer's default texture ids, isTextureDefined at llvoavatar.cpp:11727) does not
/// overwrite a valid one for a body slot, and the placeholder ids are not bakes. The skirt, left arm, left leg and the extra slots
/// are not kept (llvoavatar.cpp:9804-9806). Pure, runs in parallel.
/// </summary>
public class AppearanceUpdateTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555"), Other = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly Guid DefaultTexture = Guid.Parse("d2114404-dd59-4a4d-8e6c-49359e91bbf0"), DefaultAvatarTexture = Guid.Parse("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");

    private static Guid Of(IReadOnlyDictionary<string, Guid> bakes, string slot) => bakes.TryGetValue(slot, out var id) ? id : Guid.Empty;

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private static WorldState NewWorld()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Sim, Region);
        return w;
    }

    private static AvatarAppearanceData Message(Guid agent, int cof, params (int BakeType, Guid Id)[] bakes)
    {
        var faces = new Guid[45];
        foreach (var (bt, id) in bakes) faces[AppearanceSlots.FaceOf(bt)] = id;
        return new AvatarAppearanceData(agent, faces.ToImmutableArray(), new VisualParamSet(new byte[] { 1, 2, 3 }, ImmutableArray.Create(1, 2, 4)), 1, cof);
    }

    [Fact]
    public void A_rebaked_appearance_replaces_the_first_one_whole()
    {
        var w = NewWorld();
        w.ApplyAppearance(Sim, Message(Other, 0), Self);                                                  // the pre-rebake message: no bakes
        w.ApplyAppearance(Sim, Message(Other, 7, (0, G(1)), (1, G(2)), (2, G(3)), (3, G(4)), (5, G(5))), Self);
        var bakes = w.Appearance(Other)!.Bakes;
        Assert.Equal(G(1), Of(bakes, "head"));
        Assert.Equal(G(2), Of(bakes, "upper"));
        Assert.Equal(G(3), Of(bakes, "lower"));
        Assert.Equal(G(4), Of(bakes, "eyes"));
        Assert.Equal(G(5), Of(bakes, "hair"));
    }

    [Fact]
    public void A_later_appearance_with_new_bake_ids_shows_the_new_ones()
    {
        var w = NewWorld();
        w.ApplyAppearance(Sim, Message(Other, 7, (1, G(2)), (2, G(3))), Self);
        w.ApplyAppearance(Sim, Message(Other, 8, (1, G(20)), (2, G(30))), Self);
        var bakes = w.Appearance(Other)!.Bakes;
        Assert.Equal(G(20), Of(bakes, "upper"));
        Assert.Equal(G(30), Of(bakes, "lower"));
    }

    [Theory]
    [InlineData(false)]   // no id at all
    [InlineData(true)]    // the viewer's default texture id
    public void A_body_bake_a_later_appearance_leaves_undefined_keeps_the_last_valid_one(bool placeholder)
    {
        var w = NewWorld();
        w.ApplyAppearance(Sim, Message(Other, 7, (0, G(1)), (1, G(2)), (2, G(3)), (3, G(4)), (5, G(5))), Self);
        var next = placeholder ? Message(Other, 8, (0, DefaultTexture), (1, DefaultAvatarTexture), (2, G(33))) : Message(Other, 8, (2, G(33)));
        w.ApplyAppearance(Sim, next, Self);
        var bakes = w.Appearance(Other)!.Bakes;
        Assert.Equal(G(1), Of(bakes, "head"));
        Assert.Equal(G(2), Of(bakes, "upper"));
        Assert.Equal(G(33), Of(bakes, "lower"));
        Assert.Equal(G(4), Of(bakes, "eyes"));
        Assert.Equal(G(5), Of(bakes, "hair"));
    }

    [Fact]
    public void The_skirt_and_the_extra_slots_are_not_kept_when_a_later_appearance_leaves_them_undefined()
    {
        var w = NewWorld();
        w.ApplyAppearance(Sim, Message(Other, 7, (4, G(40)), (6, G(41)), (7, G(42)), (8, G(43)), (1, G(2))), Self);
        w.ApplyAppearance(Sim, Message(Other, 8, (1, G(2))), Self);
        var bakes = w.Appearance(Other)!.Bakes;
        Assert.False(bakes.ContainsKey("skirt"));
        Assert.False(bakes.ContainsKey("leftarm"));
        Assert.False(bakes.ContainsKey("leftleg"));
        Assert.False(bakes.ContainsKey("aux1"));
    }

    [Fact]
    public void A_default_texture_id_is_not_a_bake()
    {
        var w = NewWorld();
        w.ApplyAppearance(Sim, Message(Other, 7, (0, DefaultTexture), (1, DefaultAvatarTexture), (4, DefaultTexture)), Self);
        Assert.Empty(w.Appearance(Other)!.Bakes);
    }
}

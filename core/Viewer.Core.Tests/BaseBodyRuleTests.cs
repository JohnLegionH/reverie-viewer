// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// When the system body's parts are drawn, and with what (<see cref="BaseBodyRule"/>): the official viewer's isTextureVisible, bake on mesh, the 0.2
/// alpha cutoff and the last-known-good bake; another avatar's part is never drawn with the default skin. Pure, runs in parallel.
/// </summary>
public class BaseBodyRuleTests
{
    private static readonly Guid Bake = Guid.Parse("0b3c3f6e-5d7a-4c55-9d0e-6a1f2b3c4d5e");

    private static BodyPartState Other(Guid? bake = null, bool worn = false, bool alpha = false, bool ready = false, bool lastGood = false) =>
        new(false, bake, worn, alpha, ready, lastGood);

    [Fact]
    public void Another_avatars_part_with_no_bake_defined_is_not_drawn()
    {
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other()));
    }

    [Fact]
    public void The_own_avatars_part_with_no_bake_keeps_the_default_skin()
    {
        Assert.Equal(BodyPartDraw.DefaultSkin, BaseBodyRule.Decide(new BodyPartState(true, null, false, false, false, false)));
    }

    [Fact]
    public void The_invisible_texture_hides_the_part_for_every_avatar()
    {
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other(BaseBodyRule.Invisible, ready: true)));
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(new BodyPartState(true, BaseBodyRule.Invisible, false, false, true, false)));
    }

    [Fact]
    public void A_worn_mesh_naming_the_bake_hides_the_part_even_with_its_bake_ready()
    {
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other(Bake, worn: true, ready: true)));
    }

    [Fact]
    public void A_bake_whose_alpha_hides_everything_hides_the_part()
    {
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other(Bake, alpha: true, ready: true)));
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other(Bake, alpha: true, lastGood: true)));
    }

    [Fact]
    public void A_ready_bake_is_drawn()
    {
        Assert.Equal(BodyPartDraw.Bake, BaseBodyRule.Decide(Other(Bake, ready: true)));
        Assert.Equal(BodyPartDraw.Bake, BaseBodyRule.Decide(Other(Bake, ready: true, lastGood: true)));
    }

    [Fact]
    public void While_a_new_bake_arrives_the_last_good_one_stays()
    {
        Assert.Equal(BodyPartDraw.LastGood, BaseBodyRule.Decide(Other(Bake, lastGood: true)));
    }

    [Fact]
    public void Another_avatars_part_whose_bake_is_not_here_yet_and_had_none_before_is_not_drawn()
    {
        Assert.Equal(BodyPartDraw.Hidden, BaseBodyRule.Decide(Other(Bake)));
    }

    [Fact]
    public void The_own_avatars_part_waiting_for_its_first_bake_keeps_the_default_skin()
    {
        Assert.Equal(BodyPartDraw.DefaultSkin, BaseBodyRule.Decide(new BodyPartState(true, Bake, false, false, false, false)));
    }
}

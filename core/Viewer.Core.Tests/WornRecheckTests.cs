// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What a worn mesh hides of the body is worked out again on every trigger of the official viewer's LLVOAvatar::updateMeshVisibility: a piece attached,
/// a piece detached, an appearance message applied, the overall appearance changed (llvoavatar.cpp 2309, 7583, 7887, 9939, 11523), and a worn
/// object's face texture changing to or from a baked texture id (llviewerobject.cpp updateAvatarMeshVisibility, 5203). Pure, runs in parallel.
/// </summary>
public class WornRecheckTests
{
    private static readonly Guid Plain = Guid.Parse("11111111-2222-3333-4444-555555555555"), OtherPlain = Guid.Parse("66666666-2222-3333-4444-555555555555");
    private static readonly Guid Baked = BakeChannels.IdOf("upper");

    [Fact]
    public void Nothing_is_marked_until_a_trigger_happens()
    {
        var r = new WornRecheck();
        var (everyone, avatars) = r.Take();
        Assert.False(everyone);
        Assert.Empty(avatars);
    }

    [Fact]
    public void A_piece_attached_marks_its_wearer()
    {
        var r = new WornRecheck();
        r.Attached(7);
        var (everyone, avatars) = r.Take();
        Assert.False(everyone);
        Assert.Equal([7u], avatars);
    }

    [Fact]
    public void A_piece_detached_marks_its_wearer_when_known_and_everyone_when_not()
    {
        var known = new WornRecheck();
        known.Detached(7);
        Assert.Equal([7u], known.Take().Avatars);
        var unknown = new WornRecheck();
        unknown.Detached(null);
        Assert.True(unknown.Take().Everyone);
    }

    [Fact]
    public void An_appearance_message_applied_marks_its_avatar()
    {
        var r = new WornRecheck();
        r.AppearanceApplied(9);
        Assert.Equal([9u], r.Take().Avatars);
    }

    [Fact]
    public void A_change_of_the_overall_appearance_marks_its_avatar()
    {
        var r = new WornRecheck();
        r.OverallAppearanceChanged(11);
        Assert.Equal([11u], r.Take().Avatars);
    }

    [Fact]
    public void A_face_texture_changing_to_a_baked_id_marks_the_wearer()
    {
        var r = new WornRecheck();
        r.FacesChanged(7, [Plain], [Baked]);
        Assert.Equal([7u], r.Take().Avatars);
    }

    [Fact]
    public void A_face_texture_changing_from_a_baked_id_marks_the_wearer()
    {
        var r = new WornRecheck();
        r.FacesChanged(7, [Baked, Plain], [Plain, OtherPlain]);
        Assert.Equal([7u], r.Take().Avatars);
    }

    [Fact]
    public void A_face_texture_changing_between_two_ordinary_ids_marks_nobody()
    {
        var r = new WornRecheck();
        r.FacesChanged(7, [Plain], [OtherPlain]);
        Assert.Empty(r.Take().Avatars);
    }

    [Fact]
    public void A_baked_id_that_stays_on_the_faces_marks_nobody()
    {
        var r = new WornRecheck();
        r.FacesChanged(7, [Baked, Plain], [Baked, OtherPlain]);
        Assert.Empty(r.Take().Avatars);
    }

    [Fact]
    public void Taking_the_marks_clears_them()
    {
        var r = new WornRecheck();
        r.Attached(7);
        r.Detached(null);
        r.Take();
        var (everyone, avatars) = r.Take();
        Assert.False(everyone);
        Assert.Empty(avatars);
    }

    [Fact]
    public void Several_triggers_for_one_avatar_mark_it_once_and_other_avatars_stay_separate()
    {
        var r = new WornRecheck();
        r.Attached(7);
        r.AppearanceApplied(7);
        r.Attached(8);
        Assert.Equal([7u, 8u], r.Take().Avatars.Order().ToArray());
    }
}

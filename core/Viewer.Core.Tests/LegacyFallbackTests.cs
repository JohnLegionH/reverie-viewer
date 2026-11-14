// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A PBR face shows its legacy texture until its base colour is on screen, with no fetch of its own.</summary>
public class LegacyFallbackTests
{
    private const long MiB = 1024 * 1024;

    private static ResidencyCamera Cam() =>
        new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureUser Face(uint id, int face = 0) => new(TextureUserKind.Face, id, face);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static readonly Bounds Ahead = new(new(10, 0, 0), 2f);

    [Fact]
    public void A_legacy_texture_already_on_screen_is_shown_until_the_base_colour_is()
    {
        Assert.Equal((PbrFaceDraw.Legacy, false), LegacyFallback.Choose(enabled: true, switched: false, baseColourOnScreen: false, legacyOnScreen: true, legacyWantedElsewhere: true));
        Assert.Equal((PbrFaceDraw.Legacy, false), LegacyFallback.Choose(enabled: true, switched: false, baseColourOnScreen: false, legacyOnScreen: true, legacyWantedElsewhere: false));
    }

    [Fact]
    public void A_legacy_texture_another_face_has_asked_for_is_waited_for_not_requested()
    {
        // drawn waiting now, presented again when the other face's request brings it in
        Assert.Equal((PbrFaceDraw.Waiting, true), LegacyFallback.Choose(enabled: true, switched: false, baseColourOnScreen: false, legacyOnScreen: false, legacyWantedElsewhere: true));
    }

    [Fact]
    public void A_legacy_texture_nobody_else_wants_is_neither_shown_nor_waited_for()
    {
        Assert.Equal((PbrFaceDraw.Waiting, false), LegacyFallback.Choose(enabled: true, switched: false, baseColourOnScreen: false, legacyOnScreen: false, legacyWantedElsewhere: false));
    }

    [Fact]
    public void The_base_colour_on_screen_switches_the_face_to_its_pbr_material_once_and_for_good()
    {
        Assert.Equal((PbrFaceDraw.Pbr, false), LegacyFallback.Choose(enabled: true, switched: false, baseColourOnScreen: true, legacyOnScreen: true, legacyWantedElsewhere: true));
        // switched before: the base colour's image gone again (evicted) does not bring the legacy texture back
        Assert.Equal((PbrFaceDraw.Pbr, false), LegacyFallback.Choose(enabled: true, switched: true, baseColourOnScreen: false, legacyOnScreen: true, legacyWantedElsewhere: true));
    }

    [Fact]
    public void With_the_fallback_off_a_pbr_face_always_draws_its_pbr_material()
    {
        Assert.Equal((PbrFaceDraw.Pbr, false), LegacyFallback.Choose(enabled: false, switched: false, baseColourOnScreen: false, legacyOnScreen: true, legacyWantedElsewhere: true));
    }

    [Fact]
    public void Another_user_of_the_legacy_texture_is_found_and_the_face_own_users_are_not_counted()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        r.SetUser(Face(1, 0), G(1), Ahead, use: Viewer.Core.Net.TextureFetchClass.PbrBaseColour);
        Assert.False(r.HasUserOtherThan(G(2), u => u.Id == 1));   // nobody wants the legacy texture
        r.SetUser(Face(1, 1), G(2), Ahead);                          // the same object's other face draws it
        Assert.True(r.HasUserOtherThan(G(2), u => u.Id == 1 && u.Face == 0));
        Assert.False(r.HasUserOtherThan(G(2), u => u.Id == 1));
        r.SetUser(Face(2, 0), G(2), Ahead);
        Assert.True(r.HasUserOtherThan(G(2), u => u.Id == 1));
        r.RemoveUser(Face(2, 0));
        r.RemoveUser(Face(1, 1));
        Assert.False(r.HasUserOtherThan(G(2), _ => false));
    }

    [Fact]
    public void A_face_showing_its_legacy_texture_has_its_first_image_then_and_its_own_image_later()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        r.SetUser(Face(1), G(1), Ahead, use: Viewer.Core.Net.TextureFetchClass.PbrBaseColour);   // the PBR base colour
        r.SetUser(Face(2), G(2), Ahead);                                                         // a plain face with the legacy texture
        r.Update(Cam(), TestClock.Frames(0));
        Guid Fallback(TextureUser u) => u == Face(1) ? G(2) : Guid.Empty;
        r.ObserveFaceImages(faces, 10, Fallback);
        r.Uploaded(G(2), MiB, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(1));
        r.ObserveFaceImages(faces, 20, Fallback);
        r.Uploaded(G(1), MiB, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(2));
        r.ObserveFaceImages(faces, 30, Fallback);
        var s = faces.Summary();
        Assert.Equal(2, s.Shown);
        Assert.Equal(new FaceFirstImage(1, 0, G(1), 10, 20), s.Faces.Single(f => f.Object == 1));
        Assert.Equal(new FaceFallbackImage(1, 0, G(2), 30), s.Fallbacks.Single());
        Assert.Equal(new FaceFirstImage(2, 0, G(2), 10, 20), s.Faces.Single(f => f.Object == 2));
    }

    [Fact]
    public void A_fallback_whose_image_is_not_resident_is_not_an_image()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        r.SetUser(Face(1), G(1), Ahead, use: Viewer.Core.Net.TextureFetchClass.PbrBaseColour);
        r.Update(Cam(), TestClock.Frames(0));
        r.ObserveFaceImages(faces, 10, _ => G(2));
        var s = faces.Summary();
        Assert.Equal(0, s.Shown);
        Assert.Empty(s.Fallbacks);
    }
}

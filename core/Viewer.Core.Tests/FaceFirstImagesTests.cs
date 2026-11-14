// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Each face's first image over a path (measurement).</summary>
public class FaceFirstImagesTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>At the origin looking east (+X), z up, 60 degrees, 16:9, 360 px high.</summary>
    private static ResidencyCamera Cam(Vector3? forward = null) =>
        new(Vector3.Zero, Vector3.Normalize(forward ?? Vector3.UnitX), Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureUser Face(uint id, int face = 0) => new(TextureUserKind.Face, id, face);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static readonly Bounds Ahead = new(new(10, 0, 0), 2f);
    private static readonly Bounds Behind = new(new(-10, 0, 0), 2f);

    [Fact]
    public void A_face_shows_its_image_when_its_texture_is_resident_while_in_view()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        r.SetUser(Face(1), G(1), Ahead);
        r.Update(Cam(), TestClock.Frames(0));
        r.ObserveFaceImages(faces, 10);
        r.Uploaded(G(1), MiB, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(1));
        r.ObserveFaceImages(faces, 25);
        r.ObserveFaceImages(faces, 40);
        var s = faces.Summary();
        Assert.Equal(1, s.InView);
        Assert.Equal(1, s.Shown);
        Assert.Equal(new FaceFirstImage(1, 0, G(1), 10, 25), s.Faces.Single());
        Assert.Equal(25, s.P50);
        Assert.Equal(25, s.At90PctOfInView);
    }

    [Fact]
    public void A_face_out_of_view_is_not_counted_until_it_comes_into_view()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        r.SetUser(Face(1), G(1), Behind);
        r.Uploaded(G(1), MiB, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(0));
        r.Update(Cam(), TestClock.Frames(0));
        r.ObserveFaceImages(faces, 10);
        Assert.Equal(0, faces.InView);
        // the camera turns round: the face comes into view already showing its image
        r.Update(Cam(-Vector3.UnitX), TestClock.Frames(1));
        r.ObserveFaceImages(faces, 30);
        Assert.Equal(new FaceFirstImage(1, 0, G(1), 30, 30), faces.Summary().Faces.Single());
    }

    [Fact]
    public void Terrain_users_and_faces_without_a_texture_are_left_out()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        r.SetUser(new TextureUser(TextureUserKind.Terrain, 0, 0), G(1), Ahead);
        r.SetUser(Face(2), Guid.Empty, Ahead);
        r.SetUser(Face(3), G(3), Ahead);
        r.Update(Cam(), TestClock.Frames(0));
        r.ObserveFaceImages(faces, 5);
        Assert.Equal([3u], faces.Summary().Faces.Select(f => f.Object));
    }

    [Fact]
    public void Detail_maps_are_not_a_face_s_own_image()
    {
        var r = new TextureResidency(new() { BudgetBytes = 64 * MiB });
        var faces = new FaceFirstImages();
        // a PBR face: its base colour is its own image; its normal map (another user of the same face) is a detail map
        r.SetUser(Face(1), G(1), Ahead);
        r.SetUser(Face(1, 256), G(2), Ahead, use: Viewer.Core.Net.TextureFetchClass.Detail);
        r.Uploaded(G(2), MiB, PlaceholderColour.Of(100, 110, 120, 255), TestClock.Frames(0));
        r.Update(Cam(), TestClock.Frames(0));
        r.ObserveFaceImages(faces, 10);
        Assert.Equal(new FaceFirstImage(1, 0, G(1), 10, null), faces.Summary().Faces.Single());
    }

    [Fact]
    public void A_face_counts_the_texture_it_draws_when_its_image_arrives()
    {
        var faces = new FaceFirstImages();
        faces.Observe(Face(1), G(1), shown: false, 10);
        // the face's material changes before its first texture arrived: its image is the new texture's
        faces.Observe(Face(1), G(2), shown: false, 20);
        faces.Observe(Face(1), G(2), shown: true, 30);
        // later changes do not move its first image
        faces.Observe(Face(1), G(3), shown: false, 40);
        Assert.Equal(new FaceFirstImage(1, 0, G(2), 10, 30), faces.Summary().Faces.Single());
    }

    [Fact]
    public void The_90_percent_time_is_over_every_face_in_view_and_null_when_fewer_show()
    {
        var faces = new FaceFirstImages();
        for (int i = 0; i < 10; i++) faces.Observe(Face((uint)i), G(i + 1), shown: i < 9, 100 + i);
        var s = faces.Summary();
        Assert.Equal(10, s.InView);
        Assert.Equal(9, s.Shown);
        Assert.Equal(104, s.P50);   // the 5th of the 9 shown
        Assert.Equal(108, s.P90);
        Assert.Equal(108, s.At90PctOfInView);   // 9 of 10
        faces.Observe(Face(10), G(20), shown: false, 200);
        Assert.Null(faces.Summary().At90PctOfInView);   // 9 of 11 never reaches 90 %
    }
}

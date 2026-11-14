// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An avatar's bakes are sized by the avatar's pixel area, as the official viewer sizes them: a far avatar's bakes come down to a lower
/// resolution and come back up as it nears. Expected values are worked here from the rule's arithmetic (llviewerobject.cpp setPixelAreaAndAngle,
/// llvoavatar.cpp addBakedTextureStats), not read from the code under test. Pure; parallel.</summary>
public class AvatarBakeSizingTests
{
    private static readonly Vector3 Size = new(0.45f, 0.6f, 1.9f);
    private const float Height = 1080, Fov = MathF.PI / 3, Aspect = 16f / 9;

    /// <summary>The code works in single precision; agreement to one part in a hundred thousand.</summary>
    private static void Close(double expected, double actual) => Assert.InRange(actual, expected * 0.99999, expected * 1.00001);

    private static double Expected(float distance)
    {
        double range = distance - 0.45 / 2;
        double ppm = Height / (2 * Math.Tan(Fov / 2)) / range;
        return Math.Min(ppm * 1.9 * (ppm * 0.6), Height * Height * Aspect);
    }

    [Theory]
    [InlineData(1048576.0, 0)]   // as many texels as the full picture: the full picture
    [InlineData(262144.0, 1)]
    [InlineData(65536.0, 2)]
    [InlineData(16384.0, 3)]
    [InlineData(4096.0, 4)]
    [InlineData(1024.0, 5)]      // the coarsest a 1024 picture has (six levels, 32 across)
    [InlineData(10.0, 5)]        // 10 square pixels or fewer: the coarsest
    [InlineData(0.0, 5)]         // not in view: the coarsest
    [InlineData(3000000.0, 0)]   // more than the picture has: the full picture
    [InlineData(500000.0, 0)]    // between: log4(1048576 / 500000) = 0.53, rounded down (the coarsest level with at least as many texels)
    [InlineData(100000.0, 1)]    // log4(10.5) = 1.69
    [InlineData(20000.0, 2)]     // log4(52.4) = 2.86
    [InlineData(2000.0, 4)]      // log4(524) = 4.5
    public void A_claim_wants_the_level_with_at_least_as_many_texels(double claim, int level) =>
        Assert.Equal(level, AvatarBakeSizing.WantedLevel(1024, 1024, 6, claim));

    [Fact]
    public void A_picture_with_one_level_is_always_level_zero() => Assert.Equal(0, AvatarBakeSizing.WantedLevel(1024, 1024, 1, 5));

    [Theory]
    [InlineData(2f)]
    [InlineData(6f)]
    [InlineData(10f)]
    [InlineData(25f)]
    [InlineData(60f)]
    public void The_pixel_area_follows_the_official_formula(float distance)
    {
        var camera = new Vector3(100, 100, 21);
        var centre = camera + new Vector3(distance, 0, 0);
        Close(Expected(distance), AvatarBakeSizing.PixelArea(camera, centre, Size, Height, Fov, Aspect));
    }

    [Fact]
    public void An_avatar_the_camera_is_inside_fills_the_screen()
    {
        var p = new Vector3(5, 5, 20);
        Close(Height * Height * Aspect, AvatarBakeSizing.PixelArea(p, p, Size, Height, Fov, Aspect));
        Assert.Equal(0, AvatarBakeSizing.WantedLevel(1024, 1024, 6, AvatarBakeSizing.PixelArea(p, p, Size, Height, Fov, Aspect)));
    }

    [Fact]
    public void A_near_avatar_wants_the_full_bake_and_a_far_one_a_coarser_one_that_comes_back_as_it_nears()
    {
        var camera = new Vector3(100, 100, 21);
        int At(float d) => AvatarBakeSizing.WantedLevel(1024, 1024, 6, Expected(d));
        Assert.Equal(0, At(2f));
        Assert.Equal(3, At(10f));    // worked by hand: 10 m is about 10,400 square pixels, a 128 picture
        Assert.True(At(25f) > At(10f));
        Assert.Equal(5, At(200f));
        // nearer never wants a coarser level than farther
        int previous = int.MaxValue;
        for (float d = 200f; d >= 1f; d -= 0.5f)
        {
            int level = At(d);
            Assert.True(level <= previous, $"at {d} m the level went from {previous} up to {level}");
            previous = level;
        }
        Assert.Equal(0, At(1f));
        _ = camera;
    }

    [Fact]
    public void An_avatar_out_of_view_claims_nothing_and_wants_the_coarsest()
    {
        var camera = new ResidencyCamera(new Vector3(100, 100, 21), Vector3.UnitX, Vector3.UnitZ, Fov, Aspect, 0.2f, 512f, 1080);
        Assert.True(AvatarBakeSizing.ClaimFor(camera, new Vector3(110, 100, 21), Size) > 0);          // ahead: in view
        Assert.Equal(0, AvatarBakeSizing.ClaimFor(camera, new Vector3(90, 100, 21), Size));         // behind the camera
        Assert.Equal(0, AvatarBakeSizing.ClaimFor(camera, new Vector3(110, 160, 21), Size));        // far to the side
        Assert.Equal(5, AvatarBakeSizing.WantedLevel(1024, 1024, 6, AvatarBakeSizing.ClaimFor(camera, new Vector3(90, 100, 21), Size)));
    }
}

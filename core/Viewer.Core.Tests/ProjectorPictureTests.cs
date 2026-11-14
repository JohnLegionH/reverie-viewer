// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The picture a projector's spot light is given (Docs/PROJECTOR-SPEC.md): the window inside the canvas, the blur by focus,
/// the edge fade, the alpha and the ambiance.</summary>
public class ProjectorPictureTests
{
    private static Beam BeamOf(float fov = 1f, float aspect = 1f, float focus = 0f, float ambiance = 0f, float near = 0.5f, float far = 20.5f) =>
        new(new Vector3(0, 0, 0), near, far, fov, aspect, focus, ambiance);

    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        return px;
    }

    private static (byte R, byte G, byte B) At(byte[] canvas, int x, int y, int size = ProjectorPicture.Size) =>
        (canvas[(y * size + x) * 4], canvas[(y * size + x) * 4 + 1], canvas[(y * size + x) * 4 + 2]);

    [Fact]
    public void A_square_window_fills_the_canvas_over_root_two_and_sits_in_the_middle()
    {
        var (x, y, w, h) = ProjectorPicture.Window(BeamOf(aspect: 1f));
        Assert.Equal(181, w);   // 256 / sqrt(2)
        Assert.Equal(181, h);
        Assert.Equal(37, x);
        Assert.Equal(37, y);
    }

    [Fact]
    public void A_wide_window_is_wider_than_it_is_tall_and_the_canvas_still_holds_it()
    {
        var (x, y, w, h) = ProjectorPicture.Window(BeamOf(aspect: 2f));
        Assert.Equal(229, w);   // 256 * 2 / sqrt(5)
        Assert.Equal(114, h);   // 256 * 1 / sqrt(5) = 114.5, rounded down by the 8-bit grid
        Assert.True(x >= 0 && x + w <= 256 && y >= 0 && y + h <= 256);
        Assert.Equal((256 - w) / 2, x);
    }

    [Fact]
    public void The_window_is_in_the_same_proportions_as_the_beams_picture()
    {
        var (_, _, w, h) = ProjectorPicture.Window(BeamOf(fov: 0.7f, aspect: 1.5f));
        Assert.Equal(1.5f, (float)w / h, 2);
    }

    [Theory]
    [InlineData(0f, 3)]      // focus at the face: blurred from the start, 3 of the 6 mip levels a 64-texel picture has, at the beam's middle
    [InlineData(10f, 0)]     // focus at the beam's middle: sharp there
    [InlineData(50f, 0)]     // focus beyond the beam: sharp throughout
    public void The_blur_level_follows_the_focus(float focus, int expected) =>
        Assert.Equal(expected, ProjectorPicture.BlurLevel(BeamOf(focus: focus), 64));

    [Fact]
    public void Outside_the_window_is_black_and_inside_a_sharp_white_picture_is_white()
    {
        var canvas = ProjectorPicture.Render(Solid(8, 8, 255, 255, 255), 8, 8, BeamOf(focus: 50f), 8);
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(canvas, 2, 2));   // a corner of the canvas: outside the window
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(canvas, 128, 128));
        Assert.Equal(256 * 256 * 4, canvas.Length);
        Assert.All(Enumerable.Range(0, 256 * 256), i => Assert.Equal(255, canvas[i * 4 + 3]));
    }

    [Fact]
    public void The_pictures_alpha_dims_the_light()
    {
        var half = ProjectorPicture.Render(Solid(8, 8, 255, 255, 255, 128), 8, 8, BeamOf(focus: 50f), 8);
        var none = ProjectorPicture.Render(Solid(8, 8, 255, 255, 255, 0), 8, 8, BeamOf(focus: 50f), 8);
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(none, 128, 128));
        var (r, _, _) = At(half, 128, 128);
        Assert.InRange(r, 180, 195);   // half the linear light, 0.5 -> 188 in sRGB
    }

    [Fact]
    public void A_sharp_picture_keeps_its_halves_and_its_orientation()
    {
        // red on the left half, blue on the right: left stays left
        var px = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                px[i] = (byte)(x < 4 ? 255 : 0); px[i + 2] = (byte)(x < 4 ? 0 : 255); px[i + 3] = 255;
            }
        var canvas = ProjectorPicture.Render(px, 8, 8, BeamOf(focus: 50f), 8);
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(canvas, 70, 128));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(canvas, 185, 128));
        // and the top stays the top: red on the top half, blue underneath
        var topDown = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                topDown[i] = (byte)(y < 4 ? 255 : 0); topDown[i + 2] = (byte)(y < 4 ? 0 : 255); topDown[i + 3] = 255;
            }
        var c2 = ProjectorPicture.Render(topDown, 8, 8, BeamOf(focus: 50f), 8);
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(c2, 128, 70));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(c2, 128, 185));
    }

    [Fact]
    public void A_blurred_beam_mixes_a_picture_across_its_squares_and_a_sharp_one_does_not()
    {
        var px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                bool on = (x / 2 + y / 2) % 2 == 0;   // small squares, so a blur of several levels averages them
                px[i] = px[i + 1] = px[i + 2] = (byte)(on ? 255 : 0); px[i + 3] = 255;
            }
        var sharp = ProjectorPicture.Render(px, 64, 64, BeamOf(focus: 50f), 64);
        var soft = ProjectorPicture.Render(px, 64, 64, BeamOf(focus: 0f), 64);
        // the contrast between the picture's squares, over an area in the window's middle
        int Spread(byte[] c) { var area = Enumerable.Range(100, 56).SelectMany(y => Enumerable.Range(80, 96).Select(x => (int)At(c, x, y).R)).ToList(); return area.Max() - area.Min(); }
        Assert.True(Spread(sharp) > 200, "sharp: " + Spread(sharp));
        Assert.True(Spread(soft) < Spread(sharp) / 2, $"soft {Spread(soft)} against sharp {Spread(sharp)}");
    }

    [Fact]
    public void A_blurred_picture_fades_towards_the_edge_of_its_window_and_a_sharp_one_does_not()
    {
        var white = Solid(64, 64, 255, 255, 255);
        var (wx, _, _, _) = ProjectorPicture.Window(BeamOf());
        var sharp = ProjectorPicture.Render(white, 64, 64, BeamOf(focus: 50f), 64);
        var soft = ProjectorPicture.Render(white, 64, 64, BeamOf(focus: 0f), 64);
        Assert.Equal(255, At(sharp, wx, 128).R);
        Assert.True(At(soft, wx, 128).R < 120, "soft edge: " + At(soft, wx, 128).R);
        Assert.Equal(255, At(soft, 128, 128).R);   // the middle keeps its light
    }

    [Fact]
    public void Ambiance_lights_a_picture_with_its_average_colour_inside_the_window_only()
    {
        var dark = Solid(8, 8, 0, 0, 0);
        var lit = ProjectorPicture.Render(dark, 8, 8, BeamOf(focus: 50f, ambiance: 0.5f), 8);
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(lit, 128, 128));   // the average of black is black: nothing to add
        var grey = Solid(8, 8, 188, 188, 188);                           // about half the linear light
        var amb = ProjectorPicture.Render(grey, 8, 8, BeamOf(focus: 50f, ambiance: 0.5f), 8);
        var none = ProjectorPicture.Render(grey, 8, 8, BeamOf(focus: 50f, ambiance: 0f), 8);
        Assert.True(At(amb, 128, 128).R > At(none, 128, 128).R, "ambiance adds light");
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(amb, 2, 2));        // and only inside the window
    }
}

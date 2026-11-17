// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Interface pictures are compared with their own tolerance: a changed word or a missing row must fail, while terrain and
/// world pictures keep the terrain tolerance. The two changes below are the sizes measured on real interface renders: a
/// one-word version change in the login screen's corner (140 pixels at 960x600) and a missing row in the Preferences page
/// list (604 pixels at 960x600). Both pass under the terrain tolerance, which allows 0.2 % of the picture (1,152 pixels).
/// </summary>
public class UiDiffToleranceTests
{
    private const int W = 960, H = 600;

    private static byte[] Panel()
    {
        var b = new byte[W * H * 4];
        for (int p = 0; p < W * H; p++) { b[p * 4] = 38; b[p * 4 + 1] = 41; b[p * 4 + 2] = 46; b[p * 4 + 3] = 255; }
        return b;
    }

    /// <summary>Changes <paramref name="count"/> pixels inside a block starting at (x, y) of the given width, by
    /// <paramref name="delta"/> on the channels given: the shape of text drawn or left out.</summary>
    private static byte[] Changed(byte[] g, int x, int y, int width, int count, int delta, int channels)
    {
        var b = (byte[])g.Clone();
        for (int i = 0; i < count; i++)
        {
            int o = ((y + i / width) * W + x + i % width) * 4;
            for (int c = 0; c < channels; c++) b[o + c] = (byte)Math.Clamp(b[o + c] + delta, 0, 255);
        }
        return b;
    }

    [Fact]
    public void A_changed_version_word_of_140_pixels_fails_under_the_ui_tolerance()
    {
        var g = Panel();
        var a = Changed(g, 880, 580, 10, 140, 150, 1);
        var terrain = ImageDiff.Compare(a, g, W, H, 4, DiffTolerance.Terrain);
        Assert.Equal(140, terrain.Differing);
        Assert.True(terrain.Pass, "the terrain tolerance lets it through, as it did for the interface goldens: " + terrain);
        var ui = ImageDiff.Compare(a, g, W, H, 4, DiffTolerance.Ui);
        Assert.False(ui.Pass, ui.ToString());
    }

    [Fact]
    public void A_missing_row_of_604_pixels_fails_under_the_ui_tolerance()
    {
        var g = Panel();
        var a = Changed(g, 40, 300, 151, 604, 116, 3);
        var terrain = ImageDiff.Compare(a, g, W, H, 4, DiffTolerance.Terrain);
        Assert.Equal(604, terrain.Differing);
        Assert.True(terrain.Pass, "the terrain tolerance lets it through, as it did for the interface goldens: " + terrain);
        var ui = ImageDiff.Compare(a, g, W, H, 4, DiffTolerance.Ui);
        Assert.False(ui.Pass, ui.ToString());
    }

    [Fact]
    public void The_ui_tolerance_counts_pixels_and_holds_the_measured_noise()
    {
        // Measured: three renders of every interface case on each backend gave 0 pixels over the channel threshold against
        // each other, and at most about 900 pixels moved by less than it (the world behind the in-world interface), mean error
        // at most 0.0104. The limit sits just above that: up to 10 pixels over the threshold, mean error up to 0.03.
        Assert.Equal(new DiffTolerance(ChannelThreshold: 16, MaxDifferingFraction: 0.002, MaxMeanAbsError: 0.03, MaxDifferingPixels: 10), DiffTolerance.Ui);
        var g = Panel();
        Assert.True(ImageDiff.Compare(g, g, W, H, 4, DiffTolerance.Ui).Pass);
        var wobble = Changed(g, 0, 100, 900, 900, 15, 3);           // 900 pixels moved by 15 in every channel: under the threshold
        var w = ImageDiff.Compare(wobble, g, W, H, 4, DiffTolerance.Ui);
        Assert.True(w.Pass, w.ToString());
        Assert.Equal(0, w.Differing);
        var ten = Changed(g, 500, 500, 10, 10, 200, 1);
        Assert.True(ImageDiff.Compare(ten, g, W, H, 4, DiffTolerance.Ui).Pass);
        var eleven = Changed(g, 500, 500, 11, 11, 200, 1);
        Assert.False(ImageDiff.Compare(eleven, g, W, H, 4, DiffTolerance.Ui).Pass);
        var tinted = Changed(g, 0, 0, W, W * H, 10, 1);              // the whole panel a little off: caught by the mean error
        var t = ImageDiff.Compare(tinted, g, W, H, 4, DiffTolerance.Ui);
        Assert.Equal(0, t.Differing);
        Assert.False(t.Pass, t.ToString());
    }

    [Fact]
    public void The_terrain_tolerance_is_unchanged()
    {
        Assert.Equal(new DiffTolerance(ChannelThreshold: 16, MaxDifferingFraction: 0.002, MaxMeanAbsError: 0.5), DiffTolerance.Terrain);
        Assert.Equal(int.MaxValue, DiffTolerance.Terrain.MaxDifferingPixels);
        // 0.2 % of a 960x600 picture still passes terrain: 1,152 pixels
        var g = Panel();
        Assert.True(ImageDiff.Compare(Changed(g, 0, 0, 960, 1152, 100, 1), g, W, H, 4, DiffTolerance.Terrain).Pass);
        Assert.False(ImageDiff.Compare(Changed(g, 0, 0, 960, 1153, 100, 1), g, W, H, 4, DiffTolerance.Terrain).Pass);
    }

    private static string Source(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Theory]
    [InlineData("TestRender.Ui.cs")]
    [InlineData("TestRender.UiChrome.cs")]
    [InlineData("TestRender.LoginScreen.cs")]
    [InlineData("TestRender.LoginCard.cs")]
    [InlineData("TestRender.Reverie.cs")]
    public void Every_interface_render_case_diffs_with_the_ui_tolerance(string file)
    {
        var s = Source(file);
        Assert.Contains("DiffTolerance.Ui", s);
        Assert.DoesNotContain("DiffTolerance.Terrain", s);
    }

    [Fact]
    public void The_script_dialog_case_diffs_with_the_ui_tolerance_and_terrain_cases_keep_theirs()
    {
        Assert.Contains("_dialogButtons > 0 ? DiffTolerance.Ui : DiffTolerance.Terrain", Source("TestRender.cs"));
        foreach (var world in new[] { "TestRender.Trees.cs", "TestRender.Neighbours.cs", "TestRender.Avatars.cs", "TestRender.Busy.cs", "TestRender.EditLook.cs" })
        {
            Assert.Contains("DiffTolerance.Terrain", Source(world));
            Assert.DoesNotContain("DiffTolerance.Ui", Source(world));
        }
    }
}

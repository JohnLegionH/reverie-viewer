// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed glow scene: its layout, the glow value on each panel, and the arithmetic its probes are checked against.</summary>
public class GlowZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "materials");
    private const string File_ = "glow.matzoo.json";

    [Fact]
    public void The_committed_scene_matches_its_generator_and_its_files_exist()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_GLOW_ZOO") == "1")
        {
            File.WriteAllText(Path.Combine(Dir, File_), GlowZoo.Serialise());
            File.WriteAllBytes(Path.Combine(Dir, GlowZoo.ImageFile), LmvTexture.Encode(64, 64, Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray(), false));
        }
        Assert.Equal(GlowZoo.Serialise(), File.ReadAllText(Path.Combine(Dir, File_)).Replace("\r\n", "\n"));
        var z = MaterialZoo.Parse(File.ReadAllText(Path.Combine(Dir, File_)));
        Assert.Equal(GlowZoo.Kinds.Length * GlowZoo.Glows.Length, z.Prims.Count);
        foreach (var f in z.Images.Values.Concat(z.Materials.Values)) Assert.True(File.Exists(Path.Combine(Dir, f)), f);
        Assert.Equal(GlowZoo.Glows.Length, z.RenderMaterials.Count);   // the PBR column only
    }

    [Fact]
    public void Each_panel_carries_its_rows_glow_value_and_the_control_row_has_none()
    {
        foreach (var p in GlowZoo.Make())
        {
            int row = (int)(p.LocalId - 9100) % 10;
            Assert.Equal(GlowZoo.Glows[row], p.Textures!.Default.Glow);
        }
        Assert.Equal(0f, GlowZoo.Make().First(p => p.LocalId == GlowZoo.LocalId(0, 0)).Textures!.Default.Glow);
        // the blended and fullbright panels are drawn unlit, so their colours are exact
        Assert.All(GlowZoo.Make().Where(p => p.Name.StartsWith("blended") || p.Name.StartsWith("fullbright")), p => Assert.True(p.Textures!.Default.Fullbright));
    }

    [Fact]
    public void The_grid_is_centred_on_the_camera_and_every_panel_is_in_view_with_room_for_a_halo()
    {
        const float aspect = 640f / 360f;
        foreach (var p in GlowZoo.Make())
        {
            int k = (int)(p.LocalId - 9100) / 10, r = (int)(p.LocalId - 9100) % 10;
            var (u0, v0, u1, v1) = GlowZoo.Rect(k, r, aspect);
            Assert.InRange(u0, 0.03f, 0.97f); Assert.InRange(u1, 0.03f, 0.97f);
            Assert.InRange(v0, 0.03f, 0.97f); Assert.InRange(v1, 0.03f, 0.97f);
            Assert.True(u1 > u0 && v1 > v0);
            if (GlowZoo.Kinds[k] == "pbr") continue;   // its probes sit on the emissive ring and in the hole beside it
            // the probe beside the face lies outside the panel and inside the glow's reach (about 9 of the picture's 512 pixels)
            var (_, halo) = GlowZoo.ProbePoints(k, r);
            var (hu, hv) = GlowZoo.Project(halo, aspect);
            Assert.True(hu > u1, "the halo probe is beyond the panel's right edge as seen");
            Assert.InRange(hu - u1, 0.001f, 9f / GlowBlur.Resolution);
            Assert.InRange(hv, v0, v1);
        }
        // the middle panel's centre is the middle of the view
        var (cu, cv) = GlowZoo.Project(GlowZoo.Centre(2, 1) + new Vector3(0, 0, -1.2f), aspect);
        Assert.Equal(0.5f, cu, 3);
        Assert.Equal(0.5f, cv, 3);
    }

    [Fact]
    public void With_no_glowing_panel_a_probe_shows_its_displayed_colour_and_with_one_the_halo_adds_to_it()
    {
        const float aspect = 640f / 360f;
        var sky = new[] { 0.03f, 0.04f, 0.09f };
        var none = GlowZoo.Expected([], aspect, GlowZoo.Project(GlowZoo.ProbePoints(2, 3).Halo, aspect), sky);
        Assert.Equal(new[] { 8, 10, 23 }, none);
        var lit = GlowZoo.Expected([(2, 3, new[] { 1f, 0.5f, 0.2f })], aspect, GlowZoo.Project(GlowZoo.ProbePoints(2, 3).Halo, aspect), sky);
        Assert.True(lit[0] > none[0] + 20 && lit[0] >= lit[1] && lit[1] > lit[2], $"a halo of the panel's colour: {string.Join(',', lit)}");
        // a panel half as glowy (0.5 against 1.0) gives a dimmer halo
        var half = GlowZoo.Expected([(2, 2, new[] { 1f, 0.5f, 0.2f })], aspect, GlowZoo.Project(GlowZoo.ProbePoints(2, 2).Halo, aspect), sky);
        var full = GlowZoo.Expected([(2, 3, new[] { 1f, 0.5f, 0.2f })], aspect, GlowZoo.Project(GlowZoo.ProbePoints(2, 3).Halo, aspect), sky);
        Assert.True(half[0] < full[0]);
        // against a bright sky the sum clamps to white
        var bright = GlowZoo.Expected([(2, 3, new[] { 1f, 0.5f, 0.2f })], aspect, GlowZoo.Project(GlowZoo.ProbePoints(2, 3).Halo, aspect), new[] { 0.9f, 0.9f, 0.9f });
        Assert.Equal(255, bright[0]);
    }
}

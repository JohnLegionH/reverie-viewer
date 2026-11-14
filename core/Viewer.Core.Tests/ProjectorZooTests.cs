// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed projector scene: its layout, where each lamp points, and that each beam holds what it is meant to light.</summary>
public class ProjectorZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "materials");

    [Fact]
    public void The_committed_scene_matches_its_generator_and_its_files_exist()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_PROJECTOR_ZOO") == "1")
            File.WriteAllText(Path.Combine(Dir, ProjectorZoo.Name), ProjectorZoo.Serialise());
        Assert.Equal(ProjectorZoo.Serialise(), File.ReadAllText(Path.Combine(Dir, ProjectorZoo.Name)).Replace("\r\n", "\n"));
        var z = MaterialZoo.Parse(File.ReadAllText(Path.Combine(Dir, ProjectorZoo.Name)));
        Assert.Equal(2 + ProjectorZoo.Lamps.Length + 2, z.Prims.Count);   // floor, wall, the lamps, the occluder, the glowing panel
        foreach (var f in z.Images.Values) Assert.True(File.Exists(Path.Combine(Dir, f)), f);
        // the lights and projectors are in the file and read back as written
        var lamps = z.Prims.Where(p => p.Light is not null).ToList();
        Assert.Equal(ProjectorZoo.Lamps.Length, lamps.Count);
        for (int i = 0; i < lamps.Count; i++)
        {
            Assert.Equal(ProjectorZoo.Lamps[i].Fov, lamps[i].Projector!.Value.Fov, 5);
            Assert.Equal(ProjectorZoo.Lamps[i].Focus, lamps[i].Projector!.Value.Focus, 5);
            Assert.Equal(ProjectorZoo.Lamps[i].Ambiance, lamps[i].Projector!.Value.Ambiance, 5);
            Assert.Equal(ProjectorZoo.Pattern, lamps[i].Projector!.Value.Texture);
        }
    }

    [Fact]
    public void Each_lamps_minus_Z_points_at_its_aim_point()
    {
        for (int i = 0; i < ProjectorZoo.Lamps.Length; i++)
        {
            var axis = Vector3.Transform(new Vector3(0, 0, -1), ProjectorZoo.Aim(i));
            var want = Vector3.Normalize(new Vector3(ProjectorZoo.AimAt.X, ProjectorZoo.Lamps[i].Y, ProjectorZoo.AimAt.Z) - ProjectorZoo.LampPosition(i));
            Assert.True(Vector3.Distance(axis, want) < 1e-4f, $"lamp {i}: {axis} against {want}");
        }
    }

    // the picture's window: its height is the field of view, its width the prim's X over its Y
    private static bool InBeam(int lamp, Vector3 world) => ProjectorZoo.Window(lamp, world).Inside;

    [Fact]
    public void Every_lamps_beam_holds_wall_and_floor_the_beam_is_meant_to_light()
    {
        for (int i = 0; i < ProjectorZoo.Lamps.Length; i++)
        {
            float y = ProjectorZoo.Lamps[i].Y;
            Assert.True(InBeam(i, new Vector3(ProjectorZoo.WallX, y, ProjectorZoo.FloorZ + 1.5f)), $"lamp {i}: the wall below the lamp");
            Assert.True(InBeam(i, new Vector3(ProjectorZoo.WallX - 2f, y, ProjectorZoo.FloorZ)), $"lamp {i}: the floor before the wall");
            Assert.False(InBeam(i, new Vector3(ProjectorZoo.WallX, y, ProjectorZoo.FloorZ + 9f)), $"lamp {i}: not the top of the wall");
        }
    }

    [Fact]
    public void A_lamps_beam_does_not_reach_the_next_lamps_patch_of_wall()
    {
        for (int i = 0; i < ProjectorZoo.Lamps.Length - 1; i++)
        {
            // the wall at the next lamp's height, where its picture is brightest
            var next = new Vector3(ProjectorZoo.WallX, ProjectorZoo.Lamps[i + 1].Y, ProjectorZoo.FloorZ + 1.5f);
            Assert.False(InBeam(i, next), $"lamp {i} reaches lamp {i + 1}'s wall");
            Assert.False(InBeam(i + 1, new Vector3(ProjectorZoo.WallX, ProjectorZoo.Lamps[i].Y, ProjectorZoo.FloorZ + 1.5f)), $"lamp {i + 1} reaches lamp {i}'s wall");
        }
    }

    [Fact]
    public void The_glowing_panel_is_in_the_wide_sharp_beam_and_in_no_other()
    {
        var p = ProjectorZoo.GlowPanel;
        Assert.True(InBeam(2, p));
        Assert.False(InBeam(0, p));
        Assert.False(InBeam(1, p));
        Assert.False(InBeam(3, p));
        Assert.Equal(1f, ProjectorZoo.Make().First(x => x.LocalId == ProjectorZoo.GlowPanelId).Textures!.Default.Glow);
    }

    [Fact]
    public void The_glowing_panel_lies_well_inside_its_beam_corners_included()
    {
        var p = ProjectorZoo.GlowPanel;
        // the panel is 1 m square in the wall's plane (Y and Z); every corner stays within 70 % of the window's half-size
        foreach (var (dy, dz) in new[] { (0f, 0f), (-0.5f, -0.5f), (0.5f, -0.5f), (-0.5f, 0.5f), (0.5f, 0.5f) })
        {
            var (inside, edge) = ProjectorZoo.Window(2, p + new Vector3(0, dy, dz));
            Assert.True(inside && edge <= 0.7f, $"panel point ({dy}, {dz}): {edge}");
        }
    }

    [Fact]
    public void Each_probe_is_inside_its_lamps_beam_or_outside_every_beam_by_a_clear_margin()
    {
        var probes = ProjectorZoo.Probes();
        Assert.Equal(4 * ProjectorZoo.Lamps.Length, probes.Count);
        foreach (var pr in probes)
        {
            if (pr.Inside)
            {
                var (inside, edge) = ProjectorZoo.Window(pr.Lamp, pr.Point);
                Assert.True(inside && edge <= 0.8f, $"{pr.Name}: {edge}");
            }
            else
            {
                // outside every lamp's window, and outside by at least 20 % of the nearest window's half-size
                for (int i = 0; i < ProjectorZoo.Lamps.Length; i++)
                {
                    var (inside, edge) = ProjectorZoo.Window(i, pr.Point);
                    Assert.False(inside || edge < 1.2f, $"{pr.Name} against lamp {i}: {edge}");
                }
            }
        }
    }

    [Fact]
    public void Every_lamp_and_the_glow_panel_are_in_view_and_the_wide_beams_wall_patches_fit()
    {
        const float aspect = 640f / 360f;
        foreach (var p in ProjectorZoo.Make().Where(p => p.LocalId >= ProjectorZoo.LampId(0) || p.LocalId == ProjectorZoo.GlowPanelId))
        {
            var (u, v) = ProjectorZoo.Project(p.Position, aspect);
            Assert.InRange(u, 0.02f, 0.98f);
            Assert.InRange(v, 0.02f, 0.98f);
        }
    }
}

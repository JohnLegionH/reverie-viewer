// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed layered-transparency scenes (our own image, encoded with LibreMetaverse's encoder), their layout
/// and the blend arithmetic their probes are checked against.</summary>
public class LayeredZooTests
{
    private static string Dir => Path.Combine(TestPaths.RepoRoot, "fixtures", "materials");
    private const string Main = "layered-transparency.texzoo.json", Water = "layered-water.texzoo.json";

    [Fact]
    public void The_committed_scenes_match_their_generator_and_the_image_decodes_white()
    {
        if (Environment.GetEnvironmentVariable("NV_WRITE_LAYERED_ZOO") == "1")
        {
            File.WriteAllText(Path.Combine(Dir, Main), LayeredZoo.Serialise(LayeredZoo.Make(), water: false));
            File.WriteAllText(Path.Combine(Dir, Water), LayeredZoo.Serialise(LayeredZoo.MakeWater(), water: true));
            File.WriteAllBytes(Path.Combine(Dir, LayeredZoo.ImageFile), LmvTexture.Encode(LayeredZoo.ImageSize, LayeredZoo.ImageSize, LayeredZoo.Pixels(), false));
        }
        Assert.Equal(LayeredZoo.Serialise(LayeredZoo.Make(), false), File.ReadAllText(Path.Combine(Dir, Main)).Replace("\r\n", "\n"));
        Assert.Equal(LayeredZoo.Serialise(LayeredZoo.MakeWater(), true), File.ReadAllText(Path.Combine(Dir, Water)).Replace("\r\n", "\n"));
        var (prims, images, _) = TextureZoo.Parse(File.ReadAllText(Path.Combine(Dir, Main)));
        Assert.Equal(8, prims.Count);
        Assert.Single(images);
        var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(Dir, LayeredZoo.ImageFile)));
        Assert.True(d.Ok, d.Refusal);
        // tinted faces show their colour times the decoded white (the encoder is lossy: every texel decodes alike)
        Assert.All(Enumerable.Range(0, d.Texture!.Rgba.Length).Where(i => i % 4 != 3), i => Assert.Equal(LayeredZoo.Texel8, d.Texture.Rgba[i]));
        Assert.Equal(AlphaClass.Opaque, d.Texture.Alpha);           // blended only through the colour's alpha
    }

    [Fact]
    public void The_tube_is_magenta_outside_and_cyan_inside()
    {
        // the outer surface faces away from the tube's axis (the prim's x axis), the inner one toward it
        var faces = PrimGeometry.Build(LayeredZoo.Tube);
        static float Radial(FaceGeometry f) => Enumerable.Range(0, f.Positions.Length / 3).Average(i => f.Positions[i * 3 + 1] * f.Normals[i * 3 + 1] + f.Positions[i * 3 + 2] * f.Normals[i * 3 + 2]);
        Assert.True(Radial(faces.Single(f => f.Face == LayeredZoo.TubeOuterFace)) > 0.4f);
        Assert.True(Radial(faces.Single(f => f.Face == LayeredZoo.TubeInnerFace)) < -0.2f);
        var tube = LayeredZoo.Make().Single(p => p.Name == "tube");
        Assert.Equal(LayeredZoo.Cyan, tube.Colors[LayeredZoo.TubeInnerFace]);
        Assert.All(faces.Where(f => f.Face != LayeredZoo.TubeInnerFace), f => Assert.Equal(LayeredZoo.Magenta, tube.Colors[f.Face]));
    }

    [Fact]
    public void Each_probe_meets_the_faces_its_expected_colour_names_in_the_order_the_viewer_draws_them()
    {
        // case 1: along "near_over_far" the far pane (13 m along the view) is met behind the near pane (10 m); the near
        // pane's centre is the farther one from the eye, so ordering by centre distance draws it first (wrong)
        var p = LayeredZoo.Probes.Single(x => x.Name == "near_over_far");
        var dir = LayeredZoo.Direction(p);
        Vector3 At(float depth) => LayeredZoo.Eye + dir * (depth / dir.X);
        var near = LayeredZoo.Make().Single(x => x.Name == "near-wide");
        var far = LayeredZoo.Make().Single(x => x.Name == "far-ahead");
        Assert.InRange(At(10).Y, near.Position.Y - near.Scale.Y / 2, near.Position.Y + near.Scale.Y / 2);
        Assert.InRange(At(13).Y, far.Position.Y - far.Scale.Y / 2, far.Position.Y + far.Scale.Y / 2);
        Assert.True(Vector3.Distance(near.Position, LayeredZoo.Eye) > Vector3.Distance(far.Position, LayeredZoo.Eye));
        // the expected colours of the two orders differ well beyond the tolerance in every exact case
        var right = LayeredZoo.Srgb8(LayeredZoo.Over(LayeredZoo.Red, LayeredZoo.Over(LayeredZoo.Blue, LayeredZoo.Linear(LayeredZoo.Grey))));
        var wrong = LayeredZoo.Srgb8(LayeredZoo.Over(LayeredZoo.Blue, LayeredZoo.Over(LayeredZoo.Red, LayeredZoo.Linear(LayeredZoo.Grey))));
        Assert.True(Math.Abs(right[0] - wrong[0]) > 10 * LayeredZoo.Tolerance);
        Assert.Equal(new[] { 196, 65, 150 }, right);
    }

    [Fact]
    public void The_water_s_effect_is_read_from_its_references()
    {
        var m = new Dictionary<string, Vector3>
        {
            ["above_ref"] = new(0.2f, 0.2f, 0.2f), ["black_ref"] = new(0.01f, 0.05f, 0.08f), ["white_ref"] = new(0.31f, 0.35f, 0.38f),
            ["under_black_ref"] = new(0.01f, 0.05f, 0.08f), ["under_white_ref"] = new(0.11f, 0.15f, 0.18f),
        };
        var e = LayeredZoo.Expected(water: true, m);
        Near(new Vector3(LayeredZoo.Linear(LayeredZoo.Red).X * 0.5f + 0.1f, 0.1f, 0.1f), e["crossing_above"]);              // red over the measured wall
        Near(new Vector3(0.16f, 0.05f, 0.08f), e["crossing_below_black"]);    // water over (red over black)
        Near(new Vector3(0.06f, 0.15f, 0.13f), e["under_white"]);             // water over (green over white): half the red and blue lost
    }

    private static void Near(Vector3 want, Vector3 got)
    {
        Assert.True(Vector3.Distance(want, got) < 1e-5f, $"expected {want}, got {got}");
    }
}

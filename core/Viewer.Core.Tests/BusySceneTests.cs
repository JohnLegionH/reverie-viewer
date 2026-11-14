// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The synthetic busy view: its switch text, its counts, that it is the same every time, and the eye's fixed path.</summary>
public class BusySceneTests
{
    [Fact]
    public void The_switch_text_gives_the_four_numbers_in_any_order()
    {
        Assert.Equal(new BusySpec(500, 20, 12, 4), BusyScene.Parse("objects:500,glow:20,lights:12,projectors:4"));
        Assert.Equal(new BusySpec(500, 20, 12, 4), BusyScene.Parse("projectors:4,lights:12,glow:20,objects:500"));
        Assert.Equal(new BusySpec(10, 0, 0, 0), BusyScene.Parse("objects:10"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("objects:-1")]
    [InlineData("objects:10,glow:101")]
    [InlineData("objects:10,lights:2,projectors:3")]   // more projectors than lights
    [InlineData("objects:10,colour:3")]
    [InlineData("objects:ten")]
    public void A_switch_that_cannot_be_a_scene_is_refused(string text) =>
        Assert.Throws<ArgumentException>(() => BusyScene.Parse(text));

    [Theory]
    [InlineData(40, 25, 6, 2)]
    [InlineData(1000, 10, 40, 8)]
    [InlineData(7, 100, 0, 0)]
    public void The_scene_holds_exactly_the_counts_asked_for(int objects, int glow, int lights, int projectors)
    {
        var spec = new BusySpec(objects, glow, lights, projectors);
        var counts = BusyScene.Count(BusyScene.Make(spec));
        Assert.Equal(objects, counts.Objects);
        Assert.Equal((int)Math.Round(objects * glow / 100.0), counts.Glowing);
        Assert.Equal(lights, counts.Lights);
        Assert.Equal(projectors, counts.Projectors);
    }

    [Fact]
    public void Every_projector_has_a_picture_and_every_other_light_has_none()
    {
        var prims = BusyScene.Make(new BusySpec(50, 10, 10, 3));
        var lamps = prims.Where(p => p.Light is not null).ToList();
        Assert.Equal(10, lamps.Count);
        Assert.Equal(3, lamps.Count(p => p.Projector is not null));
        Assert.All(lamps.Where(p => p.Projector is not null), p => Assert.Equal(ProjectorZoo.Pattern, p.Projector!.Value.Texture));
    }

    [Fact]
    public void Local_ids_are_unique_and_every_object_stands_inside_the_region()
    {
        var prims = BusyScene.Make(new BusySpec(300, 30, 20, 5));
        Assert.Equal(prims.Count, prims.Select(p => p.LocalId).Distinct().Count());
        Assert.All(prims, p => { Assert.InRange(p.Position.X, 0f, 256f); Assert.InRange(p.Position.Y, 0f, 256f); });
    }

    [Fact]
    public void The_same_seed_gives_the_same_scene_and_another_seed_a_different_one()
    {
        var spec = new BusySpec(200, 20, 10, 4);
        Assert.Equal(BusyScene.Make(spec, 8).Select(p => (p.LocalId, p.Position)), BusyScene.Make(spec, 8).Select(p => (p.LocalId, p.Position)));
        Assert.NotEqual(BusyScene.Make(spec, 8).Select(p => p.Position), BusyScene.Make(spec, 9).Select(p => p.Position));
    }

    [Fact]
    public void The_glowing_objects_are_spread_not_the_first_ones()
    {
        var prims = BusyScene.Make(new BusySpec(400, 25, 0, 0));
        var objects = prims.Where(p => p.Name == "busy-object").ToList();
        var glowing = objects.Select((p, i) => (p, i)).Where(x => x.p.Textures!.Default.Glow > 0f).Select(x => x.i).ToList();
        Assert.Equal(100, glowing.Count);
        Assert.True(glowing.Max() > 300 && glowing.Min() < 100, "the glowing objects are drawn from the whole list");
    }

    [Fact]
    public void The_eye_follows_one_fixed_closed_path_and_keeps_moving()
    {
        var (a, ta) = BusyScene.EyeAt(3.5);
        var (b, tb) = BusyScene.EyeAt(3.5);
        Assert.Equal((a, ta), (b, tb));
        Assert.Equal(BusyScene.EyeAt(0).Eye.X, BusyScene.EyeAt(BusyScene.PathSeconds).Eye.X, 3);
        Assert.Equal(BusyScene.EyeAt(0).Eye.Y, BusyScene.EyeAt(BusyScene.PathSeconds).Eye.Y, 3);
        Assert.True(Vector3.Distance(BusyScene.EyeAt(1).Eye, BusyScene.EyeAt(1.1).Eye) > 0.1f, "the eye moves between frames");
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Residency;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which objects the glow picture draws as possible hiders of a glowing object (<see cref="GlowOccluders"/>). The expected
/// answers are worked from the geometry (a ray from the eye to a point of the glowing object passes through the hider's
/// sphere), not from the code under test.
/// </summary>
public class GlowOccludersTests
{
    private static readonly Vector3 Eye = Vector3.Zero;
    private static readonly Bounds Target = new(new Vector3(0, 0, -20), 1f);

    [Fact]
    public void An_object_in_front_of_the_glowing_one_can_hide_it()
    {
        Assert.True(GlowOccluders.CanHide(Eye, Target, new Bounds(new Vector3(0, 0, -10), 1f)));
    }

    [Fact]
    public void An_object_behind_the_glowing_one_cannot()
    {
        Assert.False(GlowOccluders.CanHide(Eye, Target, new Bounds(new Vector3(0, 0, -40), 1f)));
    }

    [Fact]
    public void An_object_off_to_the_side_cannot()
    {
        Assert.False(GlowOccluders.CanHide(Eye, Target, new Bounds(new Vector3(15, 0, -10), 1f)));
    }

    [Fact]
    public void An_object_the_eye_is_inside_can()
    {
        Assert.True(GlowOccluders.CanHide(Eye, Target, new Bounds(new Vector3(0, 1, 0), 5f)));
    }

    [Fact]
    public void An_object_touching_the_glowing_one_from_the_front_can()
    {
        Assert.True(GlowOccluders.CanHide(Eye, Target, new Bounds(new Vector3(0, 0, -17.5f), 2f)));
    }

    [Fact]
    public void An_object_can_hide_when_the_eye_is_inside_the_glowing_ones_sphere()
    {
        var around = new Bounds(new Vector3(0, 0, 0), 10f);
        Assert.True(GlowOccluders.CanHide(Eye, around, new Bounds(new Vector3(0, 0, -5), 1f)));
    }

    [Fact]
    public void Select_marks_an_object_that_can_hide_any_of_the_glowing_ones()
    {
        var targets = new[] { Target, new Bounds(new Vector3(30, 0, 0), 1f) };
        var candidates = new[]
        {
            new Bounds(new Vector3(0, 0, -10), 1f),    // in front of the first
            new Bounds(new Vector3(15, 0, 0), 1f),     // in front of the second
            new Bounds(new Vector3(-30, 0, 0), 1f),    // in front of neither
            new Bounds(new Vector3(0, 0, -40), 1f),    // behind the first
        };
        var selected = new bool[candidates.Length];
        selected[2] = true;   // a stale answer is overwritten
        GlowOccluders.Select(Eye, targets, candidates, selected);
        Assert.Equal(new[] { true, true, false, false }, selected);
    }

    [Fact]
    public void No_object_that_a_sight_line_to_a_glowing_one_passes_through_is_left_out()
    {
        // random scenes; for random points of a glowing sphere, every candidate sphere the eye-to-point segment crosses is chosen
        var rng = new Random(7);
        Vector3 V(float s) => new((float)(rng.NextDouble() * 2 - 1) * s, (float)(rng.NextDouble() * 2 - 1) * s, (float)(rng.NextDouble() * 2 - 1) * s);
        int crossings = 0;
        for (int scene = 0; scene < 200; scene++)
        {
            var eye = V(50);
            var target = new Bounds(V(100), 0.5f + (float)rng.NextDouble() * 5f);
            var candidates = new Bounds[60];
            for (int i = 0; i < candidates.Length; i++) candidates[i] = new Bounds(V(120), 0.3f + (float)rng.NextDouble() * 8f);
            var selected = new bool[candidates.Length];
            GlowOccluders.Select(eye, new[] { target }, candidates, selected);
            for (int s = 0; s < 40; s++)
            {
                Vector3 d;
                do { d = V(1f); } while (d.LengthSquared() > 1f);
                var p = target.Centre + d * target.Radius;
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (!SegmentHitsSphere(eye, p, candidates[i])) continue;
                    crossings++;
                    Assert.True(selected[i], $"scene {scene}: candidate {i} lies on a sight line to the glowing object but was left out");
                }
            }
        }
        Assert.True(crossings > 200, $"the scenes had only {crossings} crossings; the check proves nothing");
    }

    [Fact]
    public void Far_fewer_objects_are_chosen_than_there_are_in_a_busy_scene()
    {
        // a few hundred objects spread over a region and one glowing object: only a small share can be in front of it
        var rng = new Random(11);
        var eye = new Vector3(128, 128, 30);
        var target = new Bounds(new Vector3(200, 60, 28), 2f);
        var candidates = new Bounds[400];
        for (int i = 0; i < candidates.Length; i++)
            candidates[i] = new Bounds(new Vector3((float)rng.NextDouble() * 256, (float)rng.NextDouble() * 256, 20 + (float)rng.NextDouble() * 20), 1f + (float)rng.NextDouble() * 4f);
        var selected = new bool[candidates.Length];
        GlowOccluders.Select(eye, new[] { target }, candidates, selected);
        int chosen = selected.Count(b => b);
        Assert.InRange(chosen, 1, 40);
    }

    private static bool SegmentHitsSphere(Vector3 a, Vector3 b, Bounds s)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector3.Dot(s.Centre - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector3.Distance(a + ab * t, s.Centre) <= s.Radius;
    }
}

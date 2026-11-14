// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Residency;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The object choice for the glow picture (<see cref="GlowOccluders.Select"/>) must pick the same objects as the plain
/// every-glowing-object-against-every-object rule it replaced, and must do far less work than that rule on a busy scene.
/// </summary>
public class GlowChoiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public GlowChoiceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>The rule as it was before the choice was made fast: every candidate against every glowing target, stopping at the first hit.</summary>
    private static bool[] Reference(Vector3 eye, IReadOnlyList<Bounds> targets, IReadOnlyList<Bounds> candidates)
    {
        var selected = new bool[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            bool hides = false;
            for (int t = 0; t < targets.Count && !hides; t++) hides = GlowOccluders.CanHide(eye, targets[t], candidates[i]);
            selected[i] = hides;
        }
        return selected;
    }

    private enum EyeKind { Inside, Outside, AtEdge, InsideAnObject, OnATarget }

    private static (Vector3 Eye, Bounds[] Targets, Bounds[] Candidates) Scene(Random rng, int objects, double glowFraction, EyeKind kind, bool bigOnes, bool onEyePlane)
    {
        const float Side = 256f;
        Vector3 At() => new((float)rng.NextDouble() * Side, (float)rng.NextDouble() * Side, 20f + (float)rng.NextDouble() * 30f);
        var all = new Bounds[objects];
        for (int i = 0; i < objects; i++)
        {
            float radius = 0.4f + (float)rng.NextDouble() * 2f;
            if (bigOnes && rng.NextDouble() < 0.02) radius = 5f + (float)rng.NextDouble() * 40f;
            all[i] = new Bounds(At(), radius);
        }
        Vector3 eye = kind switch
        {
            EyeKind.Inside => At(),
            EyeKind.Outside => new Vector3(-600f + (float)rng.NextDouble() * 100f, 128f, 300f),
            EyeKind.AtEdge => new Vector3(0f, (float)rng.NextDouble() * Side, 35f),
            EyeKind.InsideAnObject when objects > 0 => all[rng.Next(objects)].Centre + new Vector3(0.1f, -0.05f, 0.02f),
            EyeKind.OnATarget when objects > 0 => all[rng.Next(objects)].Centre,
            _ => At(),
        };
        if (onEyePlane)
        {
            // objects straddling the plane through the eye that the view's edge lies on: centres on the plane, radius across it
            for (int i = 0; i < objects; i += 3) all[i] = new Bounds(new Vector3(all[i].Centre.X, eye.Y, all[i].Centre.Z), all[i].Radius);
        }
        var targets = new List<Bounds>();
        for (int i = 0; i < objects; i++) if (rng.NextDouble() < glowFraction) targets.Add(all[i]);
        return (eye, targets.ToArray(), all);
    }

    [Fact]
    public void The_choice_is_the_same_set_as_the_plain_rule_in_every_random_scene()
    {
        const int Scenes = 240;
        var rng = new Random(2026);
        int selectedTotal = 0, unselectedTotal = 0, behindEye = 0;
        var kinds = Enum.GetValues<EyeKind>();
        for (int scene = 0; scene < Scenes; scene++)
        {
            int objects = scene switch { 0 => 0, 1 => 1, 2 => 6000, _ => rng.Next(0, 6001) };
            double glow = scene switch { 3 => 0.0, 4 => 0.10, _ => rng.NextDouble() * 0.10 };
            var kind = kinds[scene % kinds.Length];
            var (eye, targets, candidates) = Scene(rng, objects, glow, kind, bigOnes: scene % 4 == 1, onEyePlane: scene % 5 == 2);
            var expected = Reference(eye, targets, candidates);
            var actual = new bool[candidates.Length];
            for (int i = 0; i < actual.Length; i++) actual[i] = (scene & 1) == 0;   // stale answers of either kind are overwritten
            GlowOccluders.Select(eye, targets, candidates, actual);
            for (int i = 0; i < candidates.Length; i++)
            {
                Assert.True(expected[i] == actual[i], $"scene {scene} ({kind}, {objects} objects, {targets.Length} glowing): object {i} chosen={actual[i]}, the plain rule says {expected[i]}");
                if (expected[i]) selectedTotal++; else unselectedTotal++;
                if (candidates[i].Centre.X < eye.X) behindEye++;
            }
        }
        Assert.True(selectedTotal > 1000 && unselectedTotal > 1000, $"the scenes chose {selectedTotal} and left out {unselectedTotal} objects; the comparison proves too little");
        Assert.True(behindEye > 1000, "the scenes had few objects behind the eye");
    }

    [Fact]
    public void The_choice_is_the_same_set_when_the_scene_is_spheres_of_every_size_close_to_the_eye()
    {
        // many near, large spheres: the case where an object's direction circle is wide and most pairs overlap
        var rng = new Random(77);
        for (int scene = 0; scene < 60; scene++)
        {
            var eye = Vector3.Zero;
            int n = rng.Next(1, 400);
            var all = new Bounds[n];
            for (int i = 0; i < n; i++)
            {
                var d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1) * (float)(rng.NextDouble() * 60);
                all[i] = new Bounds(d, 0.2f + (float)rng.NextDouble() * (scene % 2 == 0 ? 3f : 30f));
            }
            var targets = all.Where(_ => rng.NextDouble() < 0.15).ToArray();
            var expected = Reference(eye, targets, all);
            var actual = new bool[n];
            GlowOccluders.Select(eye, targets, all, actual);
            for (int i = 0; i < n; i++) Assert.True(expected[i] == actual[i], $"scene {scene}: object {i} chosen={actual[i]}, the plain rule says {expected[i]}");
        }
    }

    /// <summary>The balls and the glowing ones of the busy view's layout, as the prim layer would hold them.</summary>
    private static (Bounds[] Targets, Bounds[] All) BusyBalls(int objects, int glowPercent)
    {
        var prims = BusyScene.Make(new BusySpec(objects, glowPercent, 0, 0));
        var all = new List<Bounds>();
        var targets = new List<Bounds>();
        foreach (var p in prims)
        {
            if (p.Name != BusyScene.ObjectName) continue;
            var ball = new Bounds(p.Position, p.Scale.Length() * 0.5f);   // a box's bounding sphere
            all.Add(ball);
            if (p.Textures!.Default.Glow > 0f) targets.Add(ball);
        }
        return (targets.ToArray(), all.ToArray());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(7.5)]
    [InlineData(15.0)]
    [InlineData(22.5)]
    public void On_the_busy_layout_the_choice_tests_at_most_five_percent_of_the_pairs(double seconds)
    {
        // Bound: 5 % of (glowing x objects). Only objects whose direction circle meets a glowing one's can matter, and on this layout
        // (6,000 boxes of 1-2 m spread over 200 m, the eye 70 m out and 55 m up) an object's circle of directions is small beside the
        // directions, so a fast choice tests about ten pairs per object, under 2 % of all pairs; 5 % leaves room for a layout a little more crowded in direction without letting the plain rule (71 %) through.
        var (targets, all) = BusyBalls(6000, 10);
        Assert.Equal(600, targets.Length);
        var (eye, _) = BusyScene.EyeAt(seconds);
        var selected = new bool[all.Length];
        int pairs = GlowOccluders.Select(eye, targets, all, selected);
        long allPairs = (long)targets.Length * all.Length;
        _out.WriteLine($"eye at {seconds} s: {pairs} pair tests of {allPairs} ({100.0 * pairs / allPairs:F2} %)");
        Assert.True(pairs <= allPairs * 0.05, $"{pairs} pair tests of {allPairs} ({100.0 * pairs / allPairs:F2} %), the bound is 5 %");
        var expected = Reference(eye, targets, all);
        Assert.Equal(expected, selected);
        Assert.Contains(true, selected);
    }
}

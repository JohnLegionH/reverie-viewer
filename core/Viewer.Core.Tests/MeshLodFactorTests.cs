// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The mesh level of detail against the official viewer's rule, written here from its behaviour (llvovolume.cpp
/// calcLOD and computeLODDetail, llappviewer.cpp, llvolumemgr.cpp): the LOD factor is clamped to 0.01..4; the
/// distance to the object is scaled by (1 - 0.1 x factor); inside twice the factor it is ramped down as
/// (d / ramp)^2 x ramp; it is scaled by pi / 3; distance and radius are rounded to 0.01; the factor is scaled by the
/// default field of view (60 degrees) over the camera's; tan = factor x radius / distance, rounded to 0.01; the level
/// is the first of 0, 1, 2 whose threshold (0.03, 0.06, 0.24) tan does not pass, else 3.
/// </summary>
public class MeshLodFactorTests
{
    private static float Round(float v) => MathF.Floor(v / 0.01f + 0.5f) * 0.01f;

    private static int Official(float distance, float radius, float factor, float fovDegrees = 60f)
    {
        factor = Math.Clamp(factor, 0.01f, 4f);
        if (distance <= 0f || radius <= 0f) return 3;
        distance *= 1f - factor * 0.1f;
        float ramp = factor * 2f;
        if (distance < ramp) distance = distance / ramp * (distance / ramp) * ramp;
        distance *= MathF.PI / 3f;
        float scaled = factor * (60f / fovDegrees);
        float d = Round(distance), r = Round(radius);
        if (d <= 0f) return 3;
        float tan = Round(scaled * r / d);
        float[] thresholds = [0.03f, 0.06f, 0.24f];
        for (int i = 0; i < thresholds.Length; i++)
            if (tan <= thresholds[i]) return i;
        return 3;
    }

    // radii from a 0.25 m object to a 64 m one (the mesh bias of 0.5 applied), distances from 0.1 m to 1 km
    public static TheoryData<float> Factors => new() { 2.0f, 1.25f };
    private static readonly float[] Sizes = [0.25f, 0.5f, 1f, 2f, 3.7f, 5f, 10f, 20f, 64f];

    private static IEnumerable<float> Distances()
    {
        for (float d = 0.1f; d < 1000f; d *= 1.031f) yield return d;
    }

    [Theory]
    [MemberData(nameof(Factors))]
    public void The_chosen_level_equals_the_official_rule_at_this_factor(float factor)
    {
        int compared = 0;
        var wrong = new List<string>();
        foreach (float size in Sizes)
            foreach (var scale in new[] { new Vector3(size), new Vector3(size, size * 0.2f, size * 0.5f) })
            {
                float radius = MeshLodRule.Radius(scale);
                foreach (float d in Distances())
                {
                    compared++;
                    int ours = MeshLodRule.Detail(d, radius, factor), official = Official(d, radius, factor);
                    if (ours != official && wrong.Count < 8) wrong.Add($"radius {radius:0.###} distance {d:0.###}: ours {ours}, official {official}");
                }
            }
        Assert.True(compared > 3000);
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void The_default_factor_is_the_official_highultra_level_and_keeps_detail_further_out_than_its_mid_setting()
    {
        Assert.Equal(1.75f, MeshLodRule.DefaultLodFactor);
        float radius = MeshLodRule.Radius(new Vector3(5f));   // a 5 m object
        Assert.Equal(2, MeshLodRule.Detail(120f, radius));     // its second-highest level at 120 m
        Assert.True(MeshLodRule.Detail(120f, radius) >= MeshLodRule.Detail(120f, radius, 1.25f));
        Assert.Equal(Official(120f, radius, 1.75f), MeshLodRule.Detail(120f, radius));
    }

    [Theory]
    [InlineData(0.001f)]
    [InlineData(9f)]
    public void The_factor_is_clamped_as_the_official_viewer_clamps_it(float factor)
    {
        float radius = MeshLodRule.Radius(new Vector3(2f));
        foreach (float d in Distances())
            Assert.Equal(Official(d, radius, factor), MeshLodRule.Detail(d, radius, factor));
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(90f)]
    public void A_narrower_field_of_view_keeps_detail_further_out_as_the_official_viewer_does(float fovDegrees)
    {
        float radius = MeshLodRule.Radius(new Vector3(3f));
        foreach (float d in Distances())
            Assert.Equal(Official(d, radius, 2.0f, fovDegrees), MeshLodRule.Detail(d, radius, 2.0f, fovDegrees * MathF.PI / 180f));
        Assert.True(MeshLodRule.Detail(120f, radius, 2.0f, 30f * MathF.PI / 180f) > MeshLodRule.Detail(120f, radius, 2.0f));
    }

    [Theory]
    [InlineData("1.25", 1.25f)]
    [InlineData("8", 4f)]
    [InlineData("0", 0.01f)]
    public void A_setting_is_read_in_the_invariant_culture_and_clamped(string text, float expected) =>
        Assert.Equal(expected, MeshLodRule.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("high")]
    [InlineData("NaN")]
    public void A_missing_or_unreadable_setting_gives_no_factor(string? text) => Assert.Null(MeshLodRule.Parse(text));
}

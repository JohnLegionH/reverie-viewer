// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Personal Lighting window's model: the official window's controls (floater_adjust_environment.xml and llfloaterenvironmentadjust.cpp
/// of the official viewer), their ranges and the scales between a colour swatch and the setting it edits, applied on top of the frame in
/// force for this viewer only, and put back to the region's environment by Reset. Plain .NET.
/// </summary>
public class PersonalLightingTests
{
    private static SkyFrame Base => new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f));

    [Theory]
    [InlineData(PersonalField.HazeHorizon, 0.0, 5.0)]
    [InlineData(PersonalField.HazeDensity, 0.0, 5.0)]
    [InlineData(PersonalField.CloudCoverage, 0.0, 1.0)]
    [InlineData(PersonalField.CloudScale, 0.01, 3.0)]
    [InlineData(PersonalField.SceneGamma, 0.0, 20.0)]
    [InlineData(PersonalField.GlowSize, 0.0, 1.99)]
    [InlineData(PersonalField.GlowFocus, -2.0, 2.0)]
    public void The_ranges_are_the_official_windows(PersonalField field, double min, double max)
    {
        var r = PersonalLighting.Range(field);
        Assert.Equal(min, r.Min, 6); Assert.Equal(max, r.Max, 6); Assert.Equal(0.01, r.Step, 6);
    }

    [Theory]
    [InlineData(PersonalField.Ambient, 3f)]
    [InlineData(PersonalField.SunColor, 3f)]
    [InlineData(PersonalField.BlueHorizon, 2f)]
    [InlineData(PersonalField.BlueDensity, 2f)]
    [InlineData(PersonalField.CloudColor, 1f)]
    public void A_colour_swatch_edits_its_setting_at_the_official_scale(PersonalField field, float scale)
        => Assert.Equal(scale, PersonalLighting.ColourScale(field));

    [Fact]
    public void Nothing_set_changes_nothing()
    {
        var p = new PersonalLighting();
        Assert.False(p.IsActive);
        Assert.Equal(Base, p.Apply(Base));
        Assert.Equal(1f, p.Gamma);
    }

    [Fact]
    public void A_slider_sets_its_value_in_the_frame_and_the_window_becomes_active()
    {
        var p = new PersonalLighting(); int changed = 0; p.Changed += () => changed++;
        p.SetValue(PersonalField.HazeDensity, 2.5);
        p.SetValue(PersonalField.HazeHorizon, 1.25);
        p.SetValue(PersonalField.CloudCoverage, 0.9);
        p.SetValue(PersonalField.CloudScale, 1.5);
        var f = p.Apply(Base);
        Assert.True(p.IsActive);
        Assert.Equal(4, changed);
        Assert.Equal(2.5f, f.HazeDensity); Assert.Equal(1.25f, f.HazeHorizon); Assert.Equal(0.9f, f.CloudShadow); Assert.Equal(1.5f, f.Cloud.Scale);
    }

    [Fact]
    public void Colour_swatches_are_scaled_up_into_the_settings()
    {
        var p = new PersonalLighting();
        p.SetColour(PersonalField.Ambient, new(0.5f, 0.4f, 0.2f));
        p.SetColour(PersonalField.SunColor, new(1f, 0.5f, 0.25f));
        p.SetColour(PersonalField.BlueHorizon, new(0.1f, 0.2f, 0.3f));
        p.SetColour(PersonalField.BlueDensity, new(0.25f, 0.5f, 0.75f));
        p.SetColour(PersonalField.CloudColor, new(0.3f, 0.6f, 0.9f));
        var f = p.Apply(Base);
        Assert.Equal(new Vector3(1.5f, 1.2f, 0.6f), f.Ambient);
        Assert.Equal(new Vector3(3f, 1.5f, 0.75f), f.SunColor);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), f.BlueHorizon);
        Assert.Equal(new Vector3(0.5f, 1f, 1.5f), f.BlueDensity);
        Assert.Equal(new Vector3(0.3f, 0.6f, 0.9f), f.Cloud.Color);
    }

    [Fact]
    public void The_glow_sliders_set_the_glow_as_the_official_window_does()
    {
        var p = new PersonalLighting();
        p.SetValue(PersonalField.GlowSize, 1.0);
        p.SetValue(PersonalField.GlowFocus, -1.0);
        var g = p.Apply(Base).Glow;
        Assert.Equal(20f, g.X, 3); Assert.Equal(0f, g.Y, 3); Assert.Equal(5f, g.Z, 3);
    }

    [Fact]
    public void A_value_outside_the_range_is_kept_inside_it()
    {
        var p = new PersonalLighting();
        p.SetValue(PersonalField.HazeDensity, 7);
        p.SetValue(PersonalField.CloudScale, 0);
        p.SetValue(PersonalField.SceneGamma, 30);
        p.SetValue(PersonalField.CloudCoverage, double.NaN);
        var f = p.Apply(Base);
        Assert.Equal(5f, f.HazeDensity); Assert.Equal(0.01f, f.Cloud.Scale); Assert.Equal(20f, p.Gamma);
        Assert.Equal(Base.CloudShadow, f.CloudShadow);   // not a number: ignored
    }

    [Fact]
    public void The_window_shows_the_value_in_force_and_the_swatch_scaled_down()
    {
        var p = new PersonalLighting();
        Assert.Equal(0.7, p.Value(PersonalField.HazeDensity, Base), 4);
        Assert.Equal(1.75, p.Value(PersonalField.GlowSize, Base), 4);      // the window shows 2 - glow.x / 20
        Assert.Equal(new Vector3(0.35f, 0.35f, 0.35f), p.Colour(PersonalField.Ambient, Base));
        p.SetValue(PersonalField.HazeDensity, 1.1);
        Assert.Equal(1.1, p.Value(PersonalField.HazeDensity, Base), 4);
    }

    [Fact]
    public void Reset_puts_back_what_the_frame_has_and_says_so()
    {
        var p = new PersonalLighting(); int changed = 0;
        p.SetValue(PersonalField.HazeDensity, 2.5); p.SetValue(PersonalField.SceneGamma, 3);
        p.Changed += () => changed++;
        p.Reset();
        Assert.False(p.IsActive);
        Assert.Equal(Base, p.Apply(Base));
        Assert.Equal(1f, p.Gamma);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(1f, 0.5f, 0.5f)]
    [InlineData(2f, 0.5f, 0.75f)]
    [InlineData(0.5f, 0.5f, 0.29289f)]
    [InlineData(3f, 0f, 0f)]
    [InlineData(2f, 1f, 1f)]
    public void The_gamma_curve_is_the_official_legacy_correction(float gamma, float x, float want)
        => Assert.Equal(want, PersonalLighting.GammaCurve(gamma, x), 4);
}

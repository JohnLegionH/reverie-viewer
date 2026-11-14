// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The button fill that goes with an accent a person picks on the Interface page. Picking the accent the active preset already
/// has (for Classic, the first swatch) keeps the preset's own paired fill; any other colour, a custom one included, takes the
/// derived fill. Plain .NET, no engine; every test builds its own objects, so the class runs in parallel.
/// </summary>
public class AccentFillChoiceTests
{
    private static ThemeChoice Choice(string preset, UiColour? accent) =>
        new(preset, accent, null, null, null, null, 100, false, null);

    [Fact]
    public void Classics_blue_swatch_gives_the_presets_own_paired_fill()
    {
        var t = ThemeResolver.Resolve(Choice(ThemePresets.Classic, ThemeSwatches.Accents[0].Colour), null);
        Assert.Equal(ThemeTokenTests.H("#4c9aff"), t.Accent);
        Assert.Equal(ThemeTokenTests.H("#2b6fd6"), t.AccentFill);
        Assert.Equal(ThemeTokenTests.H("#ffffff"), t.AccentFillText);
    }

    [Theory]
    [InlineData(ThemePresets.Classic)]
    [InlineData(ThemePresets.ClassicLight)]
    [InlineData(ThemePresets.PowerUser)]
    [InlineData(ThemePresets.HighContrast)]
    public void Choosing_the_accent_the_active_preset_has_keeps_that_presets_fill(string preset)
    {
        var own = ThemePresets.Get(preset);
        var t = ThemeResolver.Resolve(Choice(preset, own.Accent), null);
        Assert.Equal(own.Accent, t.Accent);
        Assert.Equal(own.AccentFill, t.AccentFill);
        Assert.Equal(own.AccentFillText, t.AccentFillText);
    }

    [Fact]
    public void The_same_accent_typed_as_an_unrounded_colour_still_counts_as_the_presets_accent()
    {
        // a colour picker returns floats that are not whole 0-255 steps; the preset's accent is one of them to the nearest step
        var own = ThemePresets.Get(ThemePresets.Classic);
        var nearly = new UiColour(own.Accent.R + 0.001f, own.Accent.G - 0.001f, own.Accent.B + 0.001f, 1f);
        var t = ThemeResolver.Resolve(Choice(ThemePresets.Classic, nearly), null);
        Assert.Equal(own.AccentFill, t.AccentFill);
    }

    [Fact]
    public void A_custom_colour_keeps_the_derived_fill()
    {
        var custom = ThemeTokenTests.H("#ff3366");
        var t = ThemeResolver.Resolve(Choice(ThemePresets.Classic, custom), null);
        var (fill, text) = AccentFill.Derive(custom);
        Assert.Equal(custom, t.Accent);
        Assert.Equal(fill, t.AccentFill);
        Assert.Equal(text, t.AccentFillText);
        Assert.NotEqual(ThemePresets.Get(ThemePresets.Classic).AccentFill, t.AccentFill);
    }

    [Fact]
    public void A_swatch_that_is_not_the_active_presets_accent_takes_the_derived_fill()
    {
        // the blue swatch on a preset whose own accent is another colour: the rule, not Classic's fill
        var blue = ThemeSwatches.Accents[0].Colour;
        var light = ThemePresets.Get(ThemePresets.ClassicLight);
        Assert.NotEqual(blue, light.Accent);
        var t = ThemeResolver.Resolve(Choice(ThemePresets.ClassicLight, blue), null);
        var (fill, _) = AccentFill.Derive(blue);
        Assert.Equal(fill, t.AccentFill);
    }
}

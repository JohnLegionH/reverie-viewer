// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface's design tokens: a dark variant (the default) and a light one, each with its own default accent and four accent
/// presets. Every text colour reads at 4.5:1 or better on every surface it is drawn on (WCAG 2), in both variants, and the text on an
/// accent fill switches to black or white when the variant's own colour would not read on the accent. Pure data; runs in parallel.
/// </summary>
public class ReverieThemeTests
{
    private static UiColour H(string hex) => ThemePack.ParseColour(hex)!.Value;

    [Theory]
    [InlineData(ReverieVariant.Dark, "#6aa5ff")]
    [InlineData(ReverieVariant.Light, "#1a64cf")]
    public void Each_variant_has_its_own_default_accent(ReverieVariant variant, string accent)
    {
        Assert.Equal(H(accent), ReverieTheme.DefaultAccent(variant));
        Assert.Equal(H(accent), ReverieTheme.Tokens(variant).Accent);
    }

    [Fact]
    public void The_four_accent_presets_are_blue_green_amber_and_pink()
    {
        Assert.Equal(["blue", "green", "amber", "pink"], ReverieTheme.AccentPresets.Select(p => p.Key).ToArray());
        Assert.Equal([H("#6aa5ff"), H("#5fe08a"), H("#f2b85c"), H("#f08fb0")], ReverieTheme.AccentPresets.Select(p => p.Colour).ToArray());
    }

    [Fact]
    public void The_dark_variant_holds_the_specified_surface_and_text_values()
    {
        var t = ReverieTheme.Tokens(ReverieVariant.Dark);
        Assert.Equal(H("#1e2128"), t["surface.base"]);
        Assert.Equal(H("#343944"), t["surface.selected"]);
        Assert.Equal(H("#eef0f4"), t["text.primary"]);
        Assert.Equal(H("#0f1420"), t["text.on-accent"]);
        Assert.Equal(H("#5fe08a"), t["status.ok"]);
    }

    [Fact]
    public void The_light_variant_holds_the_specified_surface_and_text_values()
    {
        var t = ReverieTheme.Tokens(ReverieVariant.Light);
        Assert.Equal(H("#f4f5f7"), t["surface.base"]);
        Assert.Equal(H("#1b1e24"), t["text.primary"]);
        Assert.Equal(H("#ffffff"), t["text.on-accent"]);
    }

    [Fact]
    public void Every_text_on_surface_pair_is_listed_and_covers_every_text_token_on_every_surface()
    {
        var pairs = ReverieTheme.TextPairs;
        foreach (var text in new[] { "text.primary", "text.strong", "text.secondary", "text.muted" })
            foreach (var surface in new[] { "surface.base", "surface.sunken", "surface.raised", "surface.card", "surface.selected", "surface.selected.strong" })
                Assert.Contains((text, surface), pairs);
        Assert.Contains(("warn.text", "warn.surface"), pairs);
        Assert.Contains(("text.on-accent", "accent"), pairs);
        Assert.Contains(("accent", "surface.base"), pairs);
    }

    [Theory]
    [InlineData(ReverieVariant.Dark)]
    [InlineData(ReverieVariant.Light)]
    public void Every_text_on_surface_pair_reads_at_four_and_a_half_to_one_or_better(ReverieVariant variant)
    {
        var t = ReverieTheme.Tokens(variant);
        Assert.NotEmpty(ReverieTheme.TextPairs);
        var low = ReverieTheme.TextPairs
            .Select(p => (p.Text, p.Surface, Ratio: UiColour.Contrast(t[p.Text], t[p.Surface])))
            .Where(p => p.Ratio < Readability.MinimumRatio)
            .Select(p => $"{p.Text} on {p.Surface}: {p.Ratio:F2}")
            .ToArray();
        Assert.Empty(low);
    }

    [Theory]
    [InlineData(ReverieVariant.Dark, "#6aa5ff", "#0f1420")]   // the default: the variant's own dark text reads on the light blue
    [InlineData(ReverieVariant.Light, "#1a64cf", "#ffffff")]
    [InlineData(ReverieVariant.Dark, "#1f3a8a", "#ffffff")]   // a dark custom accent: dark text would not read, so white
    [InlineData(ReverieVariant.Light, "#f2e85c", "#000000")]  // a light custom accent: white would not read, so black
    public void Text_on_the_accent_is_the_variants_own_when_it_reads_else_black_or_white_whichever_reads_better(ReverieVariant variant, string accent, string expected)
    {
        Assert.Equal(H(expected), ReverieTheme.OnAccentFor(variant, H(accent)));
        Assert.Equal(H(expected), ReverieTheme.Tokens(variant, H(accent)).OnAccent);
    }

    [Theory]
    [InlineData("#808080")]
    [InlineData("#ff0000")]
    [InlineData("#00ff00")]
    [InlineData("#7a5cff")]
    [InlineData("#f08fb0")]
    public void For_any_accent_the_text_on_it_is_the_better_reading_of_black_and_white_or_the_variants_own_reading_at_least_as_well(string accent)
    {
        foreach (var variant in new[] { ReverieVariant.Dark, ReverieVariant.Light })
        {
            var a = H(accent);
            var on = ReverieTheme.OnAccentFor(variant, a);
            double best = Math.Max(UiColour.Contrast(H("#000000"), a), UiColour.Contrast(H("#ffffff"), a));
            Assert.True(UiColour.Contrast(on, a) >= Math.Min(best, Readability.MinimumRatio), $"{accent} {variant}");
        }
    }

    [Fact]
    public void The_engine_tokens_carry_the_variants_surfaces_text_and_accent()
    {
        var r = ReverieTheme.Tokens(ReverieVariant.Dark);
        var t = ReverieTheme.ToThemeTokens(r);
        Assert.Equal(r["surface.base"], t.Bar);
        Assert.Equal(r["surface.raised"], t.Input);
        Assert.Equal(r["text.primary"], t.Text);
        Assert.Equal(r["text.secondary"], t.TextSecondary);
        Assert.Equal(r.Accent, t.Accent);
        Assert.Equal(r.Accent, t.AccentFill);
        Assert.Equal(r.OnAccent, t.AccentFillText);
        Assert.Equal(8, t.Radius);
    }

    [Fact]
    public void The_reverie_presets_resolve_and_the_default_choice_is_the_dark_variant()
    {
        Assert.Equal(ReverieTheme.DarkId, ThemeChoice.Default.Preset);
        Assert.Equal(ReverieTheme.ToThemeTokens(ReverieTheme.Tokens(ReverieVariant.Light)), ThemePresets.Get(ReverieTheme.LightId));
        var stored = ThemeChoice.Default with { Preset = ReverieTheme.LightId };
        Assert.Equal(stored, ThemeChoice.FromJson(stored.ToJson()));
    }
}

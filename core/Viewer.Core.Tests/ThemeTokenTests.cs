// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json.Nodes;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface's named colours and sizes: the four presets as data, the contrast helper, the accent-fill rule and the
/// theme file format. Plain .NET, no engine; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class ThemeTokenTests
{
    /// <summary>"#rrggbb" to a colour, written here on purpose so the tests do not lean on the code they check.</summary>
    internal static UiColour H(string hex, float a = 1f) =>
        new(Convert.ToInt32(hex[1..3], 16) / 255f, Convert.ToInt32(hex[3..5], 16) / 255f, Convert.ToInt32(hex[5..7], 16) / 255f, a);

    // ---------------- contrast ----------------

    [Fact]
    public void Contrast_of_black_on_white_is_21_and_of_a_colour_on_itself_is_1()
    {
        Assert.Equal(21.0, UiColour.Contrast(H("#000000"), H("#ffffff")), 3);
        Assert.Equal(1.0, UiColour.Contrast(H("#4c9aff"), H("#4c9aff")), 3);
    }

    [Theory]
    [InlineData("#e6e6e6", "#202226", 12.76)]   // Classic text on the panel
    [InlineData("#aeb4bc", "#202226", 7.63)]    // Classic secondary text on the panel
    [InlineData("#4c9aff", "#2a2d33", 4.85)]    // Classic accent on the bar
    [InlineData("#ffffff", "#2b6fd6", 4.83)]    // white on the Classic accent fill
    public void Contrast_matches_values_worked_out_by_hand(string a, string b, double expected) =>
        Assert.Equal(expected, UiColour.Contrast(H(a), H(b)), 2);

    // ---------------- accent fill ----------------

    [Fact]
    public void An_accent_that_white_reads_on_is_its_own_fill_with_white_text()
    {
        var (fill, text) = AccentFill.Derive(H("#2563c9"));
        Assert.Equal(H("#2563c9"), fill);
        Assert.Equal(H("#ffffff"), text);
    }

    [Theory]
    [InlineData("#ffd400")]   // yellow: white 1.4:1, dark text far above 7:1
    [InlineData("#2dd4bf")]
    [InlineData("#f59e0b")]
    [InlineData("#ffffff")]
    public void A_light_accent_is_its_own_fill_with_dark_text(string accent)
    {
        var (fill, text) = AccentFill.Derive(H(accent));
        Assert.Equal(H(accent), fill);
        Assert.Equal(H("#111111"), text);
        Assert.True(UiColour.Contrast(text, fill) >= 7.0);
    }

    [Fact]
    public void A_middle_accent_is_darkened_in_ten_percent_steps_until_white_reads_on_it()
    {
        // #4c9aff: white 2.85:1, #111111 6.6:1 (under 7). x0.9 per channel, rounded half up: 4c9aff, then (68,139,230),
        // (61,125,207), (55,113,186). White first reaches 4.5:1 there (4.96); the step before it is 4.17.
        var (fill, text) = AccentFill.Derive(H("#4c9aff"));
        Assert.Equal(H("#3771ba"), fill);
        Assert.Equal(H("#ffffff"), text);
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("#4c9aff")]
    [InlineData("#f472b6")]
    [InlineData("#00ff00")]
    [InlineData("#7a7a00")]
    public void The_fill_always_carries_its_text_readably(string accent)
    {
        var (fill, text) = AccentFill.Derive(H(accent));
        double minimum = text == H("#111111") ? 7.0 : 4.5;
        Assert.True(UiColour.Contrast(text, fill) >= minimum, $"{accent}: {UiColour.Contrast(text, fill):0.00}");
    }

    // ---------------- presets ----------------

    [Fact]
    public void The_four_presets_are_there_in_order_with_their_names()
    {
        Assert.Equal(["classic", "classic-light", "power-user", "high-contrast"], ThemePresets.Ids);
        Assert.Equal("Classic", ThemePresets.NameOf("classic"));
        Assert.Equal("Classic light", ThemePresets.NameOf("classic-light"));
        Assert.Equal("Power user", ThemePresets.NameOf("power-user"));
        Assert.Equal("High contrast", ThemePresets.NameOf("high-contrast"));
    }

    [Fact]
    public void Classic_holds_the_values_of_the_specification()
    {
        var t = ThemePresets.Get("classic");
        Assert.NotNull(t);
        Assert.Equal(H("#202226"), t.Panel with { A = 1f });
        Assert.Equal(0.9f, t.Panel.A, 2);
        Assert.Equal(H("#2a2d33"), t.Bar);
        Assert.Equal(H("#262a30"), t.Input);
        Assert.Equal(H("#3a3f46"), t.Border);
        Assert.Equal(H("#e6e6e6"), t.Text);
        Assert.Equal(H("#aeb4bc"), t.TextSecondary);
        Assert.Equal(H("#4c9aff"), t.Accent);
        Assert.Equal(H("#2b6fd6"), t.AccentFill);
        Assert.Equal(H("#ffffff"), t.AccentFillText);
        Assert.Equal(H("#c0392b"), t.DangerFill);
        Assert.Equal(H("#f0b24a"), t.Warning);
        Assert.Equal(H("#53b97b"), t.Success);
        Assert.Equal(4, t.Radius);
        Assert.Equal(1, t.BorderWidth);
        Assert.Equal((13, 11, 13), (t.FontBody, t.FontCaption, t.FontTitle));
    }

    [Fact]
    public void The_other_presets_hold_the_values_of_the_preferences_design()
    {
        var light = ThemePresets.Get("classic-light");
        Assert.Equal(H("#f4f5f7"), light.Panel with { A = 1f });
        Assert.Equal(H("#e4e7eb"), light.Bar);
        Assert.Equal(H("#ffffff"), light.Input);
        Assert.Equal(H("#c3c9d1"), light.Border);
        Assert.Equal(H("#1d2025"), light.Text);
        Assert.Equal(H("#4c535d"), light.TextSecondary);
        Assert.Equal(H("#2563c9"), light.Accent);

        var power = ThemePresets.Get("power-user");
        Assert.Equal(H("#1b1c1f"), power.Panel with { A = 1f });
        Assert.Equal(H("#232428"), power.Bar);
        Assert.Equal(H("#202125"), power.Input);
        Assert.Equal(H("#34363b"), power.Border);
        Assert.Equal(H("#e8e8e8"), power.Text);
        Assert.Equal(H("#a9adb3"), power.TextSecondary);
        Assert.Equal(H("#f59e0b"), power.Accent);
    }

    [Fact]
    public void High_contrast_is_black_with_white_text_a_yellow_accent_thick_borders_a_larger_body_and_no_transparency()
    {
        var t = ThemePresets.Get("high-contrast");
        Assert.Equal(H("#000000"), t.Panel);
        Assert.Equal(1f, t.Panel.A);
        Assert.Equal(H("#000000"), t.Bar);
        Assert.Equal(H("#ffffff"), t.Text);
        Assert.Equal(H("#ffffff"), t.TextSecondary);
        Assert.Equal(H("#ffffff"), t.Border);
        Assert.Equal(H("#ffd400"), t.Accent);
        Assert.Equal(2, t.BorderWidth);
        Assert.Equal(16, t.FontBody);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("classic-light")]
    [InlineData("power-user")]
    [InlineData("high-contrast")]
    public void Every_preset_reads_well_on_itself(string id)
    {
        var t = ThemePresets.Get(id);
        Assert.NotNull(t);
        var panel = t.Panel with { A = 1f };
        Assert.True(UiColour.Contrast(t.Text, panel) >= 4.5, "text on panel");
        Assert.True(UiColour.Contrast(t.TextSecondary, panel) >= 4.5, "secondary text on panel");
        Assert.True(UiColour.Contrast(t.Accent, t.Bar) >= 4.5, "accent on bar");
        Assert.True(UiColour.Contrast(t.AccentFillText, t.AccentFill) >= 4.5, "button text on its fill");
        Assert.True(UiColour.Contrast(t.Text, t.Input) >= 4.5, "text in a field");
    }

    [Fact]
    public void Colours_lists_every_colour_once_under_its_own_key()
    {
        var t = ThemePresets.Get("classic");
        var keys = t.Colours.Select(c => c.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains("panel", keys);
        Assert.Contains("textSecondary", keys);
        Assert.Contains("accentFill", keys);
        Assert.Contains("outline", keys);
        Assert.Contains("scrim", keys);
        Assert.Equal(21, keys.Count);
    }

    // ---------------- theme files ----------------

    [Theory]
    [InlineData("#4c9aff", 0.298f, 0.604f, 1f, 1f)]
    [InlineData("#00000080", 0f, 0f, 0f, 0.502f)]
    [InlineData("#FFD400", 1f, 0.831f, 0f, 1f)]
    public void A_colour_text_is_read(string text, float r, float g, float b, float a)
    {
        var c = ThemePack.ParseColour(text);
        Assert.NotNull(c);
        Assert.Equal(r, c.Value.R, 2);
        Assert.Equal(g, c.Value.G, 2);
        Assert.Equal(b, c.Value.B, 2);
        Assert.Equal(a, c.Value.A, 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4c9aff")]
    [InlineData("#4c9af")]
    [InlineData("#gggggg")]
    [InlineData("#4c9aff8")]
    [InlineData("red")]
    public void A_colour_text_that_is_not_a_colour_is_refused(string text) => Assert.Null(ThemePack.ParseColour(text));

    [Fact]
    public void A_colour_written_and_read_again_is_the_same_colour()
    {
        foreach (var hex in new[] { "#4c9aff", "#000000", "#ffffff", "#2b6fd6" })
        {
            var c = H(hex);
            Assert.Equal(c, ThemePack.ParseColour(ThemePack.FormatColour(c)));
        }
        var translucent = H("#202226", 230 / 255f);
        Assert.Equal(translucent, ThemePack.ParseColour(ThemePack.FormatColour(translucent)));
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("classic-light")]
    [InlineData("power-user")]
    [InlineData("high-contrast")]
    public void A_theme_written_to_a_file_and_read_back_is_the_same_theme(string id)
    {
        var original = ThemePresets.Get(id);
        Assert.NotNull(original);
        var result = ThemePack.Parse(ThemePack.Write(id, ThemePresets.NameOf(id), original));
        Assert.Null(result.Error);
        Assert.Equal(id, result.Id);
        Assert.Equal(ThemePresets.NameOf(id), result.Name);
        Assert.Equal(original, result.Tokens);
    }

    [Fact]
    public void A_theme_file_with_only_the_required_values_gets_the_rest_derived_or_defaulted()
    {
        const string json = """
            { "format": 1, "id": "mine", "name": "Mine",
              "colors": { "panel": "#101010", "bar": "#181818", "input": "#202020", "border": "#404040",
                          "text": "#eeeeee", "textSecondary": "#bbbbbb", "accent": "#4c9aff" } }
            """;
        var r = ThemePack.Parse(json);
        Assert.Null(r.Error);
        Assert.NotNull(r.Tokens);
        Assert.Equal(H("#3771ba"), r.Tokens.AccentFill);          // the rule's result, since the file names none
        Assert.Equal(H("#ffffff"), r.Tokens.AccentFillText);
        Assert.Equal(4, r.Tokens.Radius);
        Assert.Equal(13, r.Tokens.FontBody);
    }

    [Theory]
    [InlineData("not json at all", "json")]
    [InlineData("[1, 2]", "object")]
    [InlineData("""{ "format": 2, "id": "x", "name": "X", "colors": {} }""", "format")]
    [InlineData("""{ "format": 1, "id": "x", "name": "X" }""", "colors")]
    [InlineData("""{ "format": 1, "id": "x", "name": "X", "colors": { "panel": "#101010" } }""", "bar")]
    [InlineData("""{ "format": 1, "id": "x", "name": "X", "colors": { "panel": "banana", "bar": "#181818", "input": "#202020", "border": "#404040", "text": "#eeeeee", "textSecondary": "#bbbbbb", "accent": "#4c9aff" } }""", "panel")]
    [InlineData("""{ "format": 1, "id": "x", "name": "X", "colors": { "panel": "#101010", "bar": "#181818", "input": "#202020", "border": "#404040", "text": "#eeeeee", "textSecondary": "#bbbbbb", "accent": "#4c9aff" }, "metrics": { "radius": 99 } }""", "radius")]
    public void A_broken_theme_file_is_refused_with_a_message_that_names_the_problem(string json, string mentions)
    {
        var r = ThemePack.Parse(json);
        Assert.Null(r.Tokens);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Contains(mentions, r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_keys_in_a_theme_file_are_ignored()
    {
        var pack = JsonNode.Parse(ThemePack.Write("classic", "Classic", ThemePresets.Get("classic")))!.AsObject();
        pack["somethingNew"] = 5;
        pack["colors"]!.AsObject()["futureColour"] = "#123456";
        var r = ThemePack.Parse(pack.ToJsonString());
        Assert.Null(r.Error);
        Assert.Equal(ThemePresets.Get("classic"), r.Tokens);
    }
}

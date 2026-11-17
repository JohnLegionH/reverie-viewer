// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The one theme source every window takes its look from: the control states made from the tokens. A disabled control is its normal
/// face at 45 % strength in every built-in theme, high contrast included, so it reads as disabled there too (where the secondary text
/// is as bright as the text); the engine theme, the Reverie components and the windows take that strength from the one place and set
/// no disabled colour of their own; windows set no colour a theme variation already gives. The password field keeps room on its right
/// for its show button. Every member is independent, so the class runs in parallel.
/// </summary>
public class ControlStatesTests
{
    public static TheoryData<string> Themes()
    {
        var all = new TheoryData<string>();
        foreach (var id in ThemePresets.Ids)
            all.Add(id);
        all.Add(ReverieTheme.DarkId);
        all.Add(ReverieTheme.LightId);
        return all;
    }

    private static void AtStrength(UiColour normal, UiColour disabled)
    {
        Assert.Equal(normal.R, disabled.R);
        Assert.Equal(normal.G, disabled.G);
        Assert.Equal(normal.B, disabled.B);
        Assert.Equal(normal.A * 0.45f, disabled.A, 4);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_disabled_button_is_its_normal_face_border_and_text_at_45_percent_in_every_theme(string id)
    {
        var t = ThemePresets.Get(id);
        foreach (var set in new[] { ControlStates.Button(t), ControlStates.Filled(t, t.AccentFill, t.AccentFillText), ControlStates.Filled(t, t.DangerFill, t.AccentFillText) })
        {
            AtStrength(set.Normal.Fill, set.Disabled.Fill);
            AtStrength(set.Normal.Border, set.Disabled.Border);
            AtStrength(set.Normal.Text, set.Disabled.Text);
        }
        var field = ControlStates.Field(t);
        var off = ControlStates.FieldDisabled(t);
        AtStrength(field.Text, off.Text);
        AtStrength(field.Border, off.Border);
    }

    /// <summary>What a colour drawn over <paramref name="under"/> shows.</summary>
    private static UiColour Over(UiColour c, UiColour under) =>
        new(c.R * c.A + under.R * (1 - c.A), c.G * c.A + under.G * (1 - c.A), c.B * c.A + under.B * (1 - c.A));

    [Theory]
    [MemberData(nameof(Themes))]
    public void Disabled_text_looks_clearly_weaker_than_enabled_text_on_the_panel_in_every_theme(string id)
    {
        var t = ThemePresets.Get(id);
        var panel = t.Panel.Opaque();
        var enabled = Over(ControlStates.Button(t).Normal.Text, panel);
        var disabled = Over(ControlStates.Button(t).Disabled.Text, panel);
        double strong = UiColour.Contrast(enabled, panel), weak = UiColour.Contrast(disabled, panel);
        Assert.True(weak < strong / 1.8, $"{id}: disabled text contrast {weak:F2} against enabled {strong:F2}");
    }

    [Fact]
    public void In_high_contrast_the_disabled_text_is_not_the_secondary_text_colour_that_looked_enabled()
    {
        var t = ThemePresets.Get(ThemePresets.HighContrast);
        var panel = t.Panel.Opaque();
        Assert.NotEqual(Over(t.TextSecondary, panel), Over(ControlStates.Button(t).Disabled.Text, panel));
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, .. parts]));

    [Fact]
    public void The_engine_theme_and_the_Reverie_components_take_the_disabled_strength_from_the_one_source()
    {
        var files = new[]
        {
            ("ShellTheme.cs", Source("host", "Viewer.Godot", "ShellTheme.cs")),
            ("ReverieStyle.cs", Source("host", "Viewer.Godot", "Reverie", "ReverieStyle.cs")),
            ("ReverieControls.cs", Source("host", "Viewer.Godot", "Reverie", "ReverieControls.cs")),
        };
        foreach (var (name, text) in files)
            Assert.False(Regex.IsMatch(text, @"\b0\.45f?\b"), $"{name} carries its own disabled strength");
        var shell = files[0].Item2;
        Assert.DoesNotMatch(@"font_disabled_color""\s*,\s*\w+\s*,\s*muted", shell);   // the secondary colour is not a disabled colour
        Assert.Contains("ControlStates.Button(", shell);
        Assert.Contains("ControlStates.FieldDisabled(", shell);
        Assert.Contains("ControlStates.DisabledStrength", files[2].Item2);
    }

    [Fact]
    public void No_window_sets_a_colour_that_a_theme_variation_already_gives()
    {
        var godot = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        var duplicate = new Regex(@"AddThemeColorOverride\(\s*""font_color""\s*,\s*ShellTheme\.C\(\s*(_theme|UiTheme\.Spec|UiTheme\.Tokens)\.(MutedText|TextSecondary|Text|Accent|Error)\s*\)", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(godot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => duplicate.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    public void The_password_field_keeps_room_for_its_show_button_so_the_button_never_covers_text(int padding)
    {
        int margin = LoginScreenSpec.PasswordTextRightMargin(padding);
        Assert.True(margin >= LoginScreenSpec.RevealButtonWidth + LoginScreenSpec.RevealButtonInset + padding,
            $"the text stops {margin} from the right edge; the button takes {LoginScreenSpec.RevealButtonWidth + LoginScreenSpec.RevealButtonInset}");
    }
}

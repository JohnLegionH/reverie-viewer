// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Interface code carries no colour, font size or corner radius of its own: every one is a named token in the theme
/// (ThemeTokens, filled from a theme pack). A source scan over the engine host's interface files and the shell's logic
/// finds a colour made from numbers or a hex string, a numeric font size and a numeric corner radius. The 3D world's own
/// drawing (the Presentation folder), the render and S-1 test harnesses and the theme code itself are outside it. Every
/// member is independent, so the class runs in parallel.
/// </summary>
public class UiLiteralScanTests
{
    private static readonly Regex[] Patterns =
    [
        new(@"\bnew\s+Color\s*\(", RegexOptions.Compiled),                       // an engine colour from numbers or a name
        new(@"\bColor\.FromHtml\b|(?<![.\w])Colors\.\w+", RegexOptions.Compiled), // an engine colour by name
        new(@"\bnew\s+UiColour\s*\(", RegexOptions.Compiled),                    // a shell colour from numbers
        new(@"\bnew\(\s*\d*\.\d+f?\s*,\s*\d*\.\d+f?\s*,\s*\d*\.\d+f?", RegexOptions.Compiled),   // a target-typed colour from fractions
        new(@"\bDrawString\s*\(.*,\s*-1\s*,\s*\d", RegexOptions.Compiled),                      // a size passed to a drawn string
        new("\"#[0-9a-fA-F]{6,8}\"", RegexOptions.Compiled),                      // a hex colour in a string
        new(@"\bAddThemeFontSizeOverride\s*\(\s*[^,]+,\s*\d", RegexOptions.Compiled),   // a font size as a number
        new(@"\bFontSize\s*=\s*\d", RegexOptions.Compiled),
        new(@"\bSetCornerRadius\w*\s*\(\s*\d", RegexOptions.Compiled),            // a corner radius as a number
    ];

    /// <summary>The lines of <paramref name="text"/> a pattern matches, outside comments.</summary>
    internal static List<string> Hits(string text) =>
        text.Split('\n')
            .Select((l, i) => (Line: l.IndexOf("//", StringComparison.Ordinal) is var c and >= 0 ? l[..c] : l, No: i + 1))
            .Where(x => Patterns.Any(p => p.IsMatch(x.Line)))
            .Select(x => $"{x.No}: {x.Line.Trim()}")
            .ToList();

    [Theory]
    [InlineData("var c = new Color(0.1f, 0.2f, 0.3f);")]
    [InlineData("var c = new Color(\"d9b44a\");")]
    [InlineData("var c = Colors.White;")]
    [InlineData("var c = new(0.1f, 0.2f, 0.3f, 0.9f);")]
    [InlineData("DrawString(font, at, text, HorizontalAlignment.Left, -1, 14, colour);")]
    [InlineData("var c = Color.FromHtml(\"#fff\");")]
    [InlineData("var c = new UiColour(0.1f, 0.2f, 0.3f);")]
    [InlineData("var s = \"[color=#ff0000]x[/color]\"; var h = \"#ff0000\";")]
    [InlineData("label.AddThemeFontSizeOverride(\"font_size\", 14);")]
    [InlineData("var l = new Label { FontSize = 12 };")]
    [InlineData("box.SetCornerRadiusAll(6);")]
    public void The_scan_finds_each_kind_of_literal(string line) => Assert.Single(Hits(line));

    [Theory]
    [InlineData("// new Color(1, 0, 0) in a comment")]
    [InlineData("label.AddThemeFontSizeOverride(\"font_size\", size);")]
    [InlineData("box.SetCornerRadiusAll(tokens.Radius);")]
    [InlineData("var c = ShellTheme.C(tokens.Accent);")]
    [InlineData("var id = \"#channel\";")]
    [InlineData("var n = p.Colors.Default;")]
    [InlineData("DrawString(font, at, text, HorizontalAlignment.Left, -1, size, colour);")]
    public void The_scan_leaves_alone_a_token_a_variable_and_a_comment(string line) => Assert.Empty(Hits(line));

    /// <summary>Interface sources the scan covers: the engine host's top folder, minus the harnesses and the theme code.</summary>
    private static IEnumerable<string> HostFiles()
    {
        var godot = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        foreach (var f in Directory.EnumerateFiles(godot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith("TestRender", StringComparison.Ordinal) || name.StartsWith("S1Smoke", StringComparison.Ordinal)
                || name is "ShellTheme.cs" or "ReverieStyle.cs")
                continue;
            yield return f;
        }
    }

    private static IEnumerable<string> ShellFiles()
    {
        var shell = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell");
        foreach (var f in Directory.EnumerateFiles(shell, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(shell, f).Split(Path.DirectorySeparatorChar);
            if (rel.Any(p => p is "bin" or "obj")) continue;
            if (rel.Length >= 3 && rel[0] == "Ui" && rel[1] is "Theming" or "Reverie" && rel[^1] is not "TopBarModels.cs") continue;   // the token code
            if (f.EndsWith("Zoo.cs", StringComparison.Ordinal)) continue;              // test scenes: world content, not interface
            yield return f;
        }
    }

    [Fact]
    public void No_interface_source_carries_a_colour_a_font_size_or_a_corner_radius_of_its_own()
    {
        var offenders = new List<string>();
        int files = 0;
        foreach (var f in HostFiles().Concat(ShellFiles()))
        {
            files++;
            foreach (var hit in Hits(File.ReadAllText(f)))
                offenders.Add($"{Path.GetRelativePath(TestPaths.RepoRoot, f).Replace('\\', '/')}:{hit}");
        }
        Assert.True(files > 40, $"the scan saw only {files} interface source files");   // not vacuous
        Assert.Empty(offenders);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;
using static Viewer.Core.Tests.ThemeTokenTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The icons and fonts the interface carries: only the icons it uses are in the repository, each is drawn at a 1.5 px stroke in
/// the colour asked for, the fonts are real font files, and the licences and credits that come with them are there. Plain .NET;
/// every member is independent, so the class runs in parallel.
/// </summary>
public class UiAssetsTests
{
    private static string Assets => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Assets");

    private static IEnumerable<string> Constants() =>
        typeof(UiIcons).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void The_icons_in_the_repository_are_exactly_the_ones_the_interface_names()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Assets, "Icons"), "*.svg").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.NotEmpty(files);
        Assert.Equal(files, UiIcons.All.Order().ToList());
        Assert.Equal(files, Constants().Distinct().Order().ToList());   // every name is used, and none is missing
    }

    [Fact]
    public void Every_icon_is_drawn_at_a_stroke_of_one_and_a_half_in_the_colour_asked_for()
    {
        Assert.NotEmpty(UiIcons.All);
        foreach (var name in UiIcons.All)
        {
            var svg = UiAssets.IconSvg(name, H("#4c9aff"));
            Assert.NotNull(svg);
            Assert.StartsWith("<svg", svg.TrimStart());   // the file's comment header is not carried
            Assert.Contains("stroke-width=\"1.5\"", svg);
            Assert.Contains("stroke=\"#4c9aff\"", svg);
            Assert.DoesNotContain("currentColor", svg);
            Assert.DoesNotContain("stroke-width=\"2\"", svg);
        }
    }

    [Fact]
    public void An_unknown_icon_gives_nothing_and_a_file_name_cannot_reach_outside_the_icons()
    {
        Assert.Null(UiAssets.IconSvg("no-such-icon", H("#ffffff")));
        Assert.Null(UiAssets.IconSvg("../Fonts/Inter-Regular", H("#ffffff")));
        Assert.Null(UiAssets.IconSvg("", H("#ffffff")));
    }

    [Theory]
    [InlineData("Inter-Regular")]
    [InlineData("Inter-SemiBold")]
    public void The_interface_fonts_are_truetype_files_that_match_the_ones_in_the_repository(string name)
    {
        var bytes = UiAssets.Font(name);
        Assert.True(bytes.Length > 100_000);
        Assert.Equal([0x00, 0x01, 0x00, 0x00], bytes.Take(4).Select(b => (int)b).ToArray());   // a TrueType header
        Assert.Equal(File.ReadAllBytes(Path.Combine(Assets, "Fonts", name + ".ttf")), bytes);
    }

    [Fact]
    public void An_unknown_font_is_empty()
    {
        Assert.Empty(UiAssets.Font("Comic-Sans"));
        Assert.Empty(UiAssets.Font("../Icons/bell"));
    }

    [Fact]
    public void The_licences_and_credits_that_come_with_the_icons_and_the_font_are_in_the_repository()
    {
        string licences = Path.Combine(TestPaths.RepoRoot, "ThirdPartyLicenses");
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(licences, "LICENSE-TablerIcons.txt")));
        Assert.Contains("Copyright (c) 2020-2024 Paweł Kuna", File.ReadAllText(Path.Combine(licences, "LICENSE-TablerIcons.txt")));
        var ofl = File.ReadAllText(Path.Combine(licences, "LICENSE-Inter.txt"));
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", ofl);
        Assert.Contains("Copyright (c) 2016 The Inter Project Authors", ofl);
        var notices = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("## Tabler Icons (MIT)", notices);
        Assert.Contains("## Inter (SIL Open Font License 1.1)", notices);
        Assert.Contains("Copyright (c) 2020-2024 Paweł Kuna", notices);
        Assert.Contains("Copyright (c) 2016 The Inter Project Authors", notices);
    }
}

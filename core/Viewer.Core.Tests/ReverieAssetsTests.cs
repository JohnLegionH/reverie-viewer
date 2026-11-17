// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;
using static Viewer.Core.Tests.ThemeTokenTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The Reverie interface's icons (Lucide, ISC) and its second font (Noto Sans, SIL OFL 1.1): every icon the design names is carried
/// (under its Lucide name, or the nearest one where the set has none), drawn in the colour asked for at Lucide's own stroke, and the
/// licence files and notices are there. Plain .NET; every member is independent, so the class runs in parallel.
/// </summary>
public class ReverieAssetsTests
{
    private static string Assets => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Assets");

    /// <summary>The icons the design names for the dock, the top bar, windows and controls; "face-slightly-smiling" stands for "smile",
    /// which the vendored Lucide release no longer has under that name.</summary>
    public static readonly string[] Named =
    [
        "message-square", "messages-square", "users", "map-pin", "map", "compass", "package", "shirt", "user", "person-standing",
        "pencil-ruler", "plug", "bell", "arrow-left", "house", "star", "globe", "chevron-down", "face-slightly-smiling", "mic", "undo-2",
        "search", "list-filter", "folder", "trash-2", "plus", "minus", "x", "lock", "triangle-alert", "eye", "eye-off", "circle-question-mark",
    ];

    [Fact]
    public void Every_icon_the_design_names_is_carried_and_every_carried_icon_is_listed()
    {
        foreach (var name in Named)
            Assert.Contains(name, LucideIcons.All);
        var files = Directory.EnumerateFiles(Path.Combine(Assets, "Icons", "Lucide"), "*.svg").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(files, LucideIcons.All.Order().ToList());
    }

    [Fact]
    public void Every_icon_is_drawn_in_the_colour_asked_for_at_lucides_stroke()
    {
        Assert.NotEmpty(LucideIcons.All);
        foreach (var name in LucideIcons.All)
        {
            var svg = LucideIcons.Svg(name, H("#6aa5ff"));
            Assert.NotNull(svg);
            Assert.StartsWith("<svg", svg.TrimStart());
            Assert.Contains("stroke=\"#6aa5ff\"", svg);
            Assert.Contains("stroke-width=\"2\"", svg);
            Assert.DoesNotContain("currentColor", svg);
        }
    }

    [Fact]
    public void An_unknown_icon_gives_nothing_and_a_name_cannot_reach_outside_the_set()
    {
        Assert.Null(LucideIcons.Svg("no-such-icon", H("#ffffff")));
        Assert.Null(LucideIcons.Svg("../bell", H("#ffffff")));
        Assert.Null(LucideIcons.Svg("", H("#ffffff")));
    }

    [Theory]
    [InlineData("NotoSans-Regular")]
    [InlineData("NotoSans-Bold")]
    public void Noto_sans_is_carried_as_a_truetype_file(string name)
    {
        var bytes = UiAssets.Font(name);
        Assert.True(bytes.Length > 100_000);
        Assert.Equal([0x00, 0x01, 0x00, 0x00], bytes.Take(4).Select(b => (int)b).ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(Assets, "Fonts", name + ".ttf")), bytes);
    }

    [Fact]
    public void The_licences_and_notices_for_lucide_and_noto_sans_are_in_the_repository()
    {
        string licences = Path.Combine(TestPaths.RepoRoot, "ThirdPartyLicenses");
        Assert.True(File.Exists(Path.Combine(licences, "LICENSE-Lucide.txt")), "LICENSE-Lucide.txt");
        Assert.True(File.Exists(Path.Combine(licences, "LICENSE-NotoSans.txt")), "LICENSE-NotoSans.txt");
        var lucide = File.ReadAllText(Path.Combine(licences, "LICENSE-Lucide.txt"));
        Assert.Contains("ISC License", lucide);
        Assert.Contains("Lucide Icons and Contributors", lucide);
        Assert.Contains("Cole Bemis", lucide);   // the icons that came from Feather keep its MIT notice
        var noto = File.ReadAllText(Path.Combine(licences, "LICENSE-NotoSans.txt"));
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", noto);
        Assert.Contains("The Noto Project Authors", noto);
        var notices = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("## Lucide (ISC)", notices);
        Assert.Contains("## Noto Sans (SIL Open Font License 1.1)", notices);
    }
}

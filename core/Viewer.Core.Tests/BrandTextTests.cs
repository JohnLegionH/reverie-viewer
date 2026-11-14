// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The name the viewer shows for the grid in its window title and menu bar: the grid's own name from its grid info, else the viewer's plain name.</summary>
public class BrandTextTests
{
    [Fact]
    public void The_grids_name_is_shown_when_the_grid_gave_one()
    {
        Assert.Equal("Example Grid", BrandText.For("Example Grid", StringTable.English()));
        Assert.Equal("Example Grid", BrandText.For("  Example Grid ", StringTable.English()));
    }

    [Fact]
    public void Without_a_name_the_viewers_plain_name_is_shown_never_the_project_name()
    {
        var strings = StringTable.English();
        foreach (var none in new string?[] { null, "", "   " })
        {
            string text = BrandText.For(none, strings);
            Assert.Equal(strings.Get("brand.default"), text);
            Assert.DoesNotContain("Godot", text);
        }
    }

    [Fact]
    public void The_window_title_is_the_viewers_name_only_whatever_the_grid_is_called()
    {
        // as the official viewer's window title (llappviewer.cpp: APP_NAME, no grid and no debug text); the name comes from the
        // core's identity, the one place the viewer's name is kept
        Assert.Equal(Viewer.Core.Identity.ViewerIdentity.ShortName, BrandText.WindowTitle());
    }

    [Fact]
    public void Every_window_title_the_viewer_sets_comes_from_the_window_title_rule()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories))
            foreach (var line in File.ReadAllLines(file))
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"GetWindow\(\)\.Title\s*=") && !line.Contains("WindowTitleText.For("))
                    bad.Add(Path.GetFileName(file) + ": " + line.Trim());
        Assert.True(bad.Count == 0, "window titles set other than by WindowTitleText.For (the viewer's name, and once logged in the avatar's and the grid's): " + string.Join("; ", bad));
        Assert.False(StringTable.English().Has("sky.title"), "the time of day is not a title text any more");
    }

    [Fact]
    public void A_grid_info_with_a_gridname_gives_that_name_as_the_brand()
    {
        var branding = GridBranding.FromGridInfo("<gridinfo><gridname>Example Grid</gridname><login>http://example.org:8002/</login></gridinfo>");
        Assert.Equal("Example Grid", BrandText.For(branding.Name, StringTable.English()));
    }
}

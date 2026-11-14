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
    public void The_window_title_is_the_same_text()
    {
        var strings = StringTable.English();
        Assert.Equal("Example Grid", BrandText.WindowTitle("Example Grid", strings));
        Assert.Equal(strings.Get("brand.default"), BrandText.WindowTitle(null, strings));
    }

    [Fact]
    public void A_grid_info_with_a_gridname_gives_that_name_as_the_brand()
    {
        var branding = GridBranding.FromGridInfo("<gridinfo><gridname>Example Grid</gridname><login>http://example.org:8002/</login></gridinfo>");
        Assert.Equal("Example Grid", BrandText.For(branding.Name, StringTable.English()));
    }
}

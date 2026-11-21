// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Grids;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>How a grid's name is shown: the naming safety rules (a grid cannot pass itself off as Second Life or as another saved grid),
/// the host beside the name in the grid picker, and the Hypergrid visiting line. Pure values; the class runs in parallel.</summary>
public class GridLabelsTests
{
    private static GridEntry Grid(string name, string address) => new(address, name, null);

    [Theory]
    [InlineData("Second Life")]
    [InlineData("second life")]
    [InlineData("SL")]
    [InlineData("sl")]
    [InlineData("Linden Lab")]
    [InlineData("The LINDEN Arms")]
    [InlineData("Lindenwood")]
    public void A_name_that_poses_as_second_life_is_shown_under_the_grids_host(string name)
    {
        Assert.Equal("grid.example.org", GridLabels.SafeName(name, "grid.example.org", "http://grid.example.org:8002/"));
        Assert.Equal("grid.example.org:8002", GridLabels.NameOf(Grid(name, "http://grid.example.org:8002/")));
    }

    [Fact]
    public void Second_lifes_own_entry_keeps_its_name()
    {
        Assert.Equal("Second Life", GridLabels.NameOf(GridList.SecondLife));
        Assert.Equal("Second Life", GridLabels.SafeName("Second Life", "login.agni.lindenlab.com", GridList.SecondLifeLoginUri));
    }

    [Fact]
    public void Ordinary_names_pass_and_a_missing_name_shows_the_host()
    {
        Assert.Equal("Example Grid", GridLabels.SafeName("  Example Grid ", "grid.example.org"));
        Assert.Equal("grid.example.org", GridLabels.SafeName(null, "grid.example.org"));
        Assert.Equal("grid.example.org", GridLabels.SafeName("   ", "grid.example.org"));
        Assert.Equal("Slate Grid", GridLabels.SafeName("Slate Grid", "grid.example.org"));   // only SL as the whole name counts, not a name that starts with it
    }

    [Fact]
    public void The_picker_always_shows_the_host_beside_the_name()
    {
        Assert.Equal("Example Grid (grid.example.org:8002)", GridLabels.PickerLabel(Grid("Example Grid", "http://grid.example.org:8002/")));
        Assert.Equal("Example Grid (grid.example.org)", GridLabels.PickerLabel(Grid("Example Grid", "https://grid.example.org/")));
        Assert.Equal("Second Life (login.agni.lindenlab.com)", GridLabels.PickerLabel(GridList.SecondLife));
        Assert.Equal("grid.example.org", GridLabels.PickerLabel(Grid("Second Life", "https://grid.example.org/")));   // not "host (host)"
    }

    [Fact]
    public void Two_saved_grids_with_the_same_name_are_both_shown_with_their_hosts()
    {
        var a = Grid("Example Grid", "http://one.example.org:8002/");
        var b = Grid("example grid", "http://two.example.org:8002/");
        var c = Grid("Other Grid", "http://three.example.org:8002/");
        var all = new[] { a, b, c, GridList.SecondLife };
        Assert.Equal("Example Grid (one.example.org:8002)", GridLabels.Brand(a, all));
        Assert.Equal("example grid (two.example.org:8002)", GridLabels.Brand(b, all));
        Assert.Equal("Other Grid", GridLabels.Brand(c, all));
        Assert.Equal("Second Life", GridLabels.Brand(GridList.SecondLife, all));
    }

    [Fact]
    public void A_grid_posing_as_second_life_cannot_share_the_real_ones_name_either()
    {
        var fake = Grid("Second Life", "http://fake.example.org:8002/");
        var all = new[] { fake, GridList.SecondLife };
        Assert.Equal("fake.example.org:8002", GridLabels.Brand(fake, all));
        Assert.Equal("Second Life", GridLabels.Brand(GridList.SecondLife, all));
    }

    [Fact]
    public void The_visiting_line_names_the_grid_and_its_host_and_applies_the_same_safety()
    {
        var s = StringTable.English();
        Assert.Equal("Visiting Visited Grid (visited.example.net:9000)", GridLabels.VisitingLine(s, "Visited Grid", "visited.example.net:9000"));
        Assert.Equal("Visiting visited.example.net:9000 (visited.example.net:9000)", GridLabels.VisitingLine(s, "Second Life", "visited.example.net:9000"));
    }
}

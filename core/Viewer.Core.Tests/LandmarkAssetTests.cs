// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A landmark is read as OpenSim writes it: "Landmark version 2", region_id, local_pos, region_handle, and on a Hypergrid grid a
/// "gatekeeper" line naming the grid it was made on (OpenSim HGInventoryAccessModule, AssetLandmark). Its grid is shown in its
/// properties and decides where a teleport by it leads.
/// </summary>
public class LandmarkAssetTests
{
    private static readonly Guid Region = Guid.Parse("6f1f6d36-0c54-4f6f-9a43-8d7a3f0a2b11");

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_hypergrid_landmark_names_its_grid()
    {
        var lm = LandmarkAsset.Parse(Text($"Landmark version 2\nregion_id {Region}\nlocal_pos 128.5 64.25 22\nregion_handle 1099511628032000\ngatekeeper http://grid.example.org:8002\n"));
        Assert.NotNull(lm);
        Assert.Equal(Region, lm!.RegionId);
        Assert.Equal(new Vector3(128.5f, 64.25f, 22f), lm.Position);
        Assert.Equal(1099511628032000UL, lm.RegionHandle);
        Assert.Equal("http://grid.example.org:8002", lm.Gatekeeper);
        Assert.Equal("grid.example.org:8002", lm.Grid);
        Assert.Equal(1000, lm.GridX);
        Assert.Equal(1000, lm.GridY);
    }

    [Fact]
    public void A_landmark_without_a_gatekeeper_line_is_on_the_grid_it_was_made_on()
    {
        var lm = LandmarkAsset.Parse(Text($"Landmark version 2\nregion_id {Region}\nlocal_pos 1 2 3\nregion_handle 1099511628032000\n"));
        Assert.NotNull(lm);
        Assert.Null(lm!.Gatekeeper);
        Assert.Null(lm.Grid);
    }

    [Fact]
    public void Windows_line_ends_and_a_blank_gatekeeper_are_read()
    {
        var lm = LandmarkAsset.Parse(Text($"Landmark version 2\r\nregion_id {Region}\r\nlocal_pos 1 2 3\r\nregion_handle 5\r\ngatekeeper \r\n"));
        Assert.NotNull(lm);
        Assert.Null(lm!.Grid);
        Assert.Equal(5UL, lm.RegionHandle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a landmark")]
    [InlineData("Landmark version 2\nregion_id nope\nlocal_pos 1 2 3\nregion_handle 5\n")]
    [InlineData("Landmark version 2\nregion_id 6f1f6d36-0c54-4f6f-9a43-8d7a3f0a2b11\nlocal_pos x y z\nregion_handle 5\n")]
    public void Anything_else_is_not_a_landmark(string text) => Assert.Null(LandmarkAsset.Parse(Text(text)));

    [Fact]
    public void A_landmark_too_large_is_not_read() => Assert.Null(LandmarkAsset.Parse(new byte[LandmarkAsset.MaxBytes + 1]));

    [Theory]
    [InlineData("Visit Landing @ http://grid.example.org:8002", "grid.example.org:8002")]
    [InlineData("Visit Landing", null)]
    [InlineData("Shop @ the beach", null)]
    public void The_grid_an_hg_landmark_name_carries(string name, string? grid) => Assert.Equal(grid, LandmarkAsset.GridOfName(name));
}

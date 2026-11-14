// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The ground's detail textures repeat every 12 m (the official viewer's RenderTerrainScale default, settings.xml; lldrawpoolterrain.cpp sets
/// the detail scale to its reciprocal), and the pattern is offset by the region's global origin modulo the repeat (lldrawpoolterrain.cpp,
/// renderFullShaderTextures), so the same ground point gets the same pattern coordinate whichever region draws it and textures line up across borders.
/// </summary>
public class TerrainDetailScaleTests
{
    private static double Fract(double v) => v - Math.Floor(v);

    private static double PatternCoordinate(double sceneMetres, int anchorCell)
    {
        var (ox, _) = TerrainComposition.DetailOffset(anchorCell, 0);
        return Fract(sceneMetres / TerrainComposition.DetailRepeatMetres + ox);
    }

    [Fact]
    public void The_pattern_repeats_every_twelve_metres()
    {
        Assert.Equal(12f, TerrainComposition.DetailRepeatMetres);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1004)]
    [InlineData(999)]
    public void A_point_has_the_pattern_coordinate_of_its_global_position_whatever_the_scene_origin(int anchorCell)
    {
        foreach (double x in new[] { 0.0, 5.5, 255.0, 256.0, 777.25, 1023.0, 1024.0, 1500.5 })
            Assert.Equal(Fract((anchorCell * 256.0 + x) / 12.0), PatternCoordinate(x, anchorCell), 4);
    }

    [Fact]
    public void Two_regions_draw_the_same_coordinate_on_their_shared_border()
    {
        // a border point at the east edge of a 1024 m region at cell 1000 is x = 1024 in that scene; seen from the neighbour's own origin (cell 1004) it is x = 0
        Assert.Equal(PatternCoordinate(1024, 1000), PatternCoordinate(0, 1004), 4);
        Assert.Equal(PatternCoordinate(256, 1000), PatternCoordinate(0, 1001), 4);
    }

    [Fact]
    public void The_offset_is_the_origin_modulo_the_repeat_over_the_repeat()
    {
        var (x, y) = TerrainComposition.DetailOffset(1001, 1003);
        Assert.Equal(1001 * 256.0 % 12 / 12, x, 5);
        Assert.Equal(1003 * 256.0 % 12 / 12, y, 5);
    }
}

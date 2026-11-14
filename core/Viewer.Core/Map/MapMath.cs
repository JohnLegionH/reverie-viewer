// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Map;

/// <summary>One map tile: a detail level (1 to 8) and the grid cell of its south-west region (a multiple of 2^(level-1)).</summary>
public readonly record struct MapTileKey(int Level, int X, int Y);

/// <summary>
/// The map's arithmetic: which detail level a zoom needs, which tile holds a region, region handles and the tile's address.
/// Behaviour follows the official viewer's world map (its level rule, its tile grid and its file names), written from a
/// behaviour spec in our own words.
/// </summary>
public static class MapMath
{
    public const int RegionMetres = 256;
    public const int MapLevels = 8;
    public const int TileSize = 256;

    /// <summary>The detail level for a zoom (pixels per region): level n draws 2^(n-1) regions in one 256-pixel tile, so the level is
    /// floor(log2(256 / pixels) + 1), kept within 1 to 8. A zoom that is not a positive number reads as the coarsest level.</summary>
    public static int LevelFor(double pixelsPerRegion)
    {
        if (!(pixelsPerRegion > 0))
            return MapLevels;
        double level = Math.Floor(Math.Log2(RegionMetres / pixelsPerRegion) + 1);
        return (int)Math.Clamp(level, 1, MapLevels);
    }

    /// <summary>How many regions one side of a tile of this level spans.</summary>
    public static int RegionsPerTile(int level) => 1 << (Math.Clamp(level, 1, MapLevels) - 1);

    /// <summary>The tile of <paramref name="level"/> that holds the region at (<paramref name="gridX"/>, <paramref name="gridY"/>): the
    /// region's cell rounded down to a multiple of the tile's span.</summary>
    public static MapTileKey TileFor(int gridX, int gridY, int level)
    {
        level = Math.Clamp(level, 1, MapLevels);
        int span = RegionsPerTile(level);
        return new(level, gridX - Mod(gridX, span), gridY - Mod(gridY, span));
    }

    private static int Mod(int value, int span) => ((value % span) + span) % span;

    /// <summary>The region handle of the cell: its south-west corner in metres, x in the high half and y in the low half.</summary>
    public static ulong HandleOf(int gridX, int gridY) => ((ulong)(uint)(gridX * RegionMetres) << 32) | (uint)(gridY * RegionMetres);

    public static (int GridX, int GridY) CellOf(ulong handle) => ((int)((handle >> 32) / RegionMetres), (int)((handle & 0xFFFFFFFF) / RegionMetres));

    /// <summary>The address of a tile under the grid's map server, or null when the server address is not a plain http(s) address.</summary>
    public static Uri? TileUri(string? mapServer, MapTileKey key)
    {
        if (string.IsNullOrWhiteSpace(mapServer) || !Uri.TryCreate(mapServer.Trim(), UriKind.Absolute, out var baseUri))
            return null;
        if (baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            return null;
        string root = baseUri.AbsoluteUri;
        if (!root.EndsWith('/'))
            root += "/";
        return new Uri(root + $"map-{key.Level}-{key.X}-{key.Y}-objects.jpg");
    }
}

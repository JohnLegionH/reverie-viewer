// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell;

/// <summary>
/// The location bar's text, laid out as the official viewer lays out its location string (indra/newview/llagentui.cpp,
/// buildLocationString), in each of its formats. The position, where a format shows it, is rounded to the nearest metre and, while the agent moves, to every second metre (faster than a walk's lower bound of 1.5 m/s) or
/// every fourth (faster than 6 m/s), so the numbers do not flicker.
/// </summary>
/// <summary>The official location string's formats (indra/newview/llagentui.h, ELocationFormat).</summary>
public enum LocationFormat { Landmark, Normal, NormalCoords, NoCoords, NoMaturity, Full }

public static class LocationText
{
    /// <summary>The format the location bar shows: the official bar shows coordinates only when its NavBarShowCoordinates setting is
    /// on (off by default; indra/newview/lllocationinputctrl.cpp picks FULL then, otherwise NO_COORDS).</summary>
    public static LocationFormat BarFormat(bool showCoordinates) => showCoordinates ? LocationFormat.Full : LocationFormat.NoCoords;

    /// <summary>The location in one of the official formats. A parcel with a name gives "Parcel, Region" (the parcel alone for a
    /// landmark name, and the parcel with coordinates for the normal-with-coordinates format); one without gives the region alone.
    /// <paramref name="access"/> is the region's maturity text ("General", "Moderate", "Adult"); empty or null leaves out the " - "
    /// and the text.</summary>
    public static string Build(LocationFormat format, string? region, string? parcel, Vector3 position, float speed, string? access = null)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "";
        int x = (int)(position.X + 0.5f), y = (int)(position.Y + 0.5f), z = (int)(position.Z + 0.5f);
        if (speed > FlySpeed) { x -= x % 4; y -= y % 4; }
        else if (speed > WalkSpeed) { x -= x % 2; y -= y % 2; }
        string coords = $"({x}, {y}, {z})";
        string maturity = string.IsNullOrWhiteSpace(access) ? "" : " - " + access.Trim();
        region = region.Trim();
        if (string.IsNullOrWhiteSpace(parcel))
            return format switch
            {
                LocationFormat.Landmark => region.Length > 100 ? region[..100] : region,
                LocationFormat.Normal => region,
                LocationFormat.NormalCoords or LocationFormat.NoMaturity => $"{region} {coords}",
                LocationFormat.NoCoords => region + maturity,
                _ => $"{region} {coords}{maturity}",
            };
        parcel = parcel.Trim();
        return format switch
        {
            LocationFormat.Landmark => parcel.Length > 100 ? parcel[..100] : parcel,
            LocationFormat.Normal => $"{parcel}, {region}",
            LocationFormat.NormalCoords => $"{parcel} {coords}",
            LocationFormat.NoMaturity => $"{parcel}, {region} {coords}",
            LocationFormat.NoCoords => $"{parcel}, {region}{maturity}",
            _ => $"{parcel}, {region} {coords}{maturity}",
        };
    }


    private const float WalkSpeed = 1.5f, FlySpeed = 6f;
}

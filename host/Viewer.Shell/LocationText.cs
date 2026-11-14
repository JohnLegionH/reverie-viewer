// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell;

/// <summary>
/// The location bar's text, laid out as the official viewer lays out its location string (indra/newview/llagentui.cpp,
/// buildLocationString): the parcel's name when it has one, then the region, then the position in whole metres. The position is
/// rounded to the nearest metre and, while the agent moves, to every second metre (faster than a walk's lower bound of 1.5 m/s) or
/// every fourth (faster than 6 m/s), so the numbers do not flicker.
/// </summary>
public static class LocationText
{
    private const float WalkSpeed = 1.5f, FlySpeed = 6f;

    public static string Build(string? region, string? parcel, Vector3 position, float speed)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "";
        int x = (int)(position.X + 0.5f), y = (int)(position.Y + 0.5f), z = (int)(position.Z + 0.5f);
        if (speed > FlySpeed) { x -= x % 4; y -= y % 4; }
        else if (speed > WalkSpeed) { x -= x % 2; y -= y % 2; }
        string where = $"{region} ({x}, {y}, {z})";
        return string.IsNullOrWhiteSpace(parcel) ? where : $"{parcel.Trim()}, {where}";
    }
}

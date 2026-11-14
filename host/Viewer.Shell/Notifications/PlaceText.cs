// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Shell.Notifications;

/// <summary>A place as an object's message gives it: a region name and, when it gave them, a position in the region.</summary>
public sealed record ParsedPlace(string Region, System.Numerics.Vector3? Position);

/// <summary>Reads the place text an object's instant message carries ("Region Name/x/y/z"). A region name holds no slash, so the last
/// three parts are the position when all three are numbers; anything else is taken whole as a region name.</summary>
public static class PlaceText
{
    public static ParsedPlace? Parse(string? place)
    {
        string text = (place ?? "").Trim();
        if (text.Length == 0)
            return null;
        var parts = text.Split('/');
        if (parts.Length >= 4
            && float.TryParse(parts[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            && float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z))
        {
            string region = string.Join('/', parts[..^3]).Trim();
            return region.Length == 0 ? null : new ParsedPlace(region, new System.Numerics.Vector3(x, y, z));
        }
        return text.StartsWith('/') ? null : new ParsedPlace(text, null);
    }
}

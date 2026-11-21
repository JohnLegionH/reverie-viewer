// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using Viewer.Core.Actions;

namespace Viewer.Shell.Inventory;

/// <summary>
/// A new landmark of where the agent stands, as the official viewer makes one (llpanellandmarkinfo.cpp createLandmark): named for the
/// parcel when the parcel has a name, else for the region, with the place ("Region (x, y, z)") as its description, in the Landmarks
/// folder. The region writes the landmark's contents from where the agent stands; on a Hypergrid grid it adds the grid's address.
/// </summary>
public static class LandmarkMaker
{
    /// <summary>The intent, or null without a folder or a region.</summary>
    public static LandmarkCreateIntent? For(Guid folder, string region, string? parcel, Vector3 position)
    {
        if (folder == Guid.Empty || string.IsNullOrWhiteSpace(region))
            return null;
        string name = string.IsNullOrWhiteSpace(parcel) ? region.Trim() : parcel.Trim();
        if (name.Length > LandmarkCreateIntent.MaxName)
            name = name[..LandmarkCreateIntent.MaxName];
        static string R(float v) => ((int)MathF.Round(v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        string description = $"{region.Trim()} ({R(position.X)}, {R(position.Y)}, {R(position.Z)})";
        if (description.Length > LandmarkCreateIntent.MaxDescription)
            description = description[..LandmarkCreateIntent.MaxDescription];
        return new LandmarkCreateIntent(folder, name, description);
    }
}

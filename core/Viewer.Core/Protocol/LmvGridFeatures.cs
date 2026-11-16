// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;

namespace Viewer.Core.Protocol;

/// <summary>Turns the region's simulator features into plain values for <see cref="Session.GridFeatures"/>.</summary>
public static class LmvGridFeatures
{
    /// <summary>The "OpenSimExtras" map of a simulator features answer as plain values (string, long, double, bool), or null when
    /// the answer has none.</summary>
    public static IReadOnlyDictionary<string, object?>? Extras(OSD? openSimExtras)
    {
        if (openSimExtras is not OSDMap map)
            return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string key in map.Keys)
            values[key] = map[key] is not { } v ? null : v.Type switch
            {
                OSDType.String or OSDType.URI => v.AsString(),
                OSDType.Integer => (long)v.AsInteger(),
                OSDType.Real => v.AsReal(),
                OSDType.Boolean => v.AsBoolean(),
                _ => null,
            };
        return values;
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text;
using Viewer.Core.Links;

namespace Viewer.Core.Inventory;

/// <summary>
/// A landmark asset as OpenSim writes it, one "key value" line each: "Landmark version 2", region_id, local_pos (three numbers),
/// region_handle and, on a Hypergrid grid, "gatekeeper" with the address of the grid the landmark was made on (OpenSim's
/// HGInventoryAccessModule writes it, and AssetLandmark reads it as the fifth line). A teleport by a landmark that names another grid
/// is turned into a Hypergrid teleport by the region, so the viewer sends the landmark as it would any other; the grid line is what the
/// landmark's properties show, and where the viewer expects to land.
/// </summary>
/// <param name="Gatekeeper">The grid's address as written, or null when the landmark names none.</param>
public sealed record LandmarkAsset(Guid RegionId, Vector3 Position, ulong RegionHandle, string? Gatekeeper)
{
    /// <summary>The largest landmark read (the text is a few lines).</summary>
    public const int MaxBytes = 4096;

    /// <summary>The grid the landmark names ("host:port"), or null when it names none.</summary>
    public string? Grid => GridKeys.OfUri(Gatekeeper);

    public int GridX => (int)((RegionHandle >> 32) / 256);

    public int GridY => (int)((RegionHandle & 0xFFFFFFFF) / 256);

    /// <summary>Reads a landmark; null when the data is not one.</summary>
    public static LandmarkAsset? Parse(byte[]? data)
    {
        if (data is null || data.Length == 0 || data.Length > MaxBytes)
            return null;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count < 4 || !lines[0].StartsWith("Landmark version ", StringComparison.Ordinal))
            return null;
        string? Value(string key) => lines.Skip(1).FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal) || l == key) is { } l
            ? l.Length > key.Length ? l[(key.Length + 1)..].Trim() : ""
            : null;
        if (!Guid.TryParse(Value("region_id"), out var region))
            return null;
        var pos = (Value("local_pos") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pos.Length != 3)
            return null;
        var v = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(pos[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
                return null;
        if (!ulong.TryParse(Value("region_handle"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong handle))
            return null;
        string? gatekeeper = Value("gatekeeper");
        return new LandmarkAsset(region, new Vector3(v[0], v[1], v[2]), handle, string.IsNullOrWhiteSpace(gatekeeper) ? null : gatekeeper);
    }

    /// <summary>The grid a landmark's description (or name) carries: OpenSim adds " @ http://host:port" to the description of a landmark
    /// made on a Hypergrid grid (InventoryAccessModule.CreateNewInventoryItem with HGInventoryAccessModule's suffix). Null when it carries none.</summary>
    public static string? GridOfName(string? name)
    {
        if (name is null)
            return null;
        int at = name.LastIndexOf(" @ ", StringComparison.Ordinal);
        if (at < 0)
            return null;
        string tail = name[(at + 3)..].Trim();
        return tail.Contains("://", StringComparison.Ordinal) ? GridKeys.OfUri(tail) : null;
    }
}

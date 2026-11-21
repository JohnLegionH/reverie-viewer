// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>The session's current region: name, grid cell and size in metres.</summary>
public sealed record RegionInfo(string Name, int GridX, int GridY, int SizeX, int SizeY, bool SizeAuthoritative)
{
    /// <summary>The grid the region is on ("host:port"; empty when not known).</summary>
    public string Grid { get; init; } = "";
    /// <summary>The region's terrain texturing settings from its handshake (null when unknown).</summary>
    public Viewer.Core.World.RegionTerrain? Terrain { get; init; }
    /// <summary>The region's water height in metres from its handshake (null when unknown).</summary>
    public float? WaterHeight { get; init; }
    /// <summary>Whether the agent can manage this region's estate, from its handshake (false when unknown).</summary>
    public bool IsEstateManager { get; init; }
    /// <summary>The region's map tile texture from its own map block (null when unknown).</summary>
    public Guid? MapImageId { get; init; }
    /// <summary>The region's environment LLSD as served (null until it arrives, or when the grid serves none).</summary>
    public string? EnvironmentLlsd { get; init; }
    /// <summary>Why the region has no environment, when it has none.</summary>
    public string? EnvironmentMissing { get; init; }
    /// <summary>The environment LLSD of the parcel the agent stands in, as its last answer served it (null until one arrives). It is kept
    /// raw like the region's; an answer without a day cycle means the parcel has none of its own, and the region's applies.</summary>
    public string? ParcelEnvironmentLlsd { get; init; }
    /// <summary>The local id of the parcel <see cref="ParcelEnvironmentLlsd"/> is for.</summary>
    public int? ParcelEnvironmentId { get; init; }
}

/// <summary>
/// The var-region size workaround (ADR-007 patch layer), from the web gateway (web-viewer bb91ba5, AgentSession
/// CaptureRegion / OnLandPatchReceived / OnMapBlockReply). LibreMetaverse leaves Simulator.SizeX/Y at 256 for var
/// regions although the login reply carries region_size_x/y, and its fixed 16x16 patch store overflows (the adapter
/// turns StoreLandPatches off). Three sources, in rising authority:
///   1. at connect: the larger of the simulator's and the login reply's size (256 if neither says anything);
///   2. a terrain patch outside the current size grows it in 256 m steps, until
///   3. the map block for our cell arrives: its size is authoritative and patches no longer grow it.
/// </summary>
public static class RegionSize
{
    public const int Step = 256;

    public static RegionInfo AtConnect(SimRef sim, int simSizeX, int simSizeY, int loginSizeX, int loginSizeY)
    {
        int x = Math.Max(simSizeX, loginSizeX);
        int y = Math.Max(simSizeY, loginSizeY);
        return new(sim.Name, sim.GridX, sim.GridY, x > 0 ? x : Step, y > 0 ? y : Step, SizeAuthoritative: false) { Grid = sim.Grid };
    }

    /// <summary>Returns the region grown to cover patch (x, y), or the same instance if it already does or is authoritative.</summary>
    public static RegionInfo GrowForPatch(RegionInfo region, int patchX, int patchY, int patchSize)
    {
        if (region.SizeAuthoritative || patchSize <= 0)
            return region;
        int needX = RoundUp((patchX + 1) * patchSize);
        int needY = RoundUp((patchY + 1) * patchSize);
        if (needX <= region.SizeX && needY <= region.SizeY)
            return region;
        return region with { SizeX = Math.Max(region.SizeX, needX), SizeY = Math.Max(region.SizeY, needY) };
    }

    /// <summary>Applies a map block if it is our cell on our grid: its size wins (0 = a legacy 256 m region), and its name fills a name the
    /// region did not have at connect. A block from another grid's map is about another region, whatever its cell (Hypergrid).</summary>
    public static RegionInfo FromMapBlock(RegionInfo region, MapBlockSize block)
    {
        if (block.GridX != region.GridX || block.GridY != region.GridY)
            return region;
        if (block.Grid is { } grid && region.Grid.Length > 0 && !string.Equals(grid, region.Grid, StringComparison.OrdinalIgnoreCase))
            return region;
        return region with
        {
            Name = string.IsNullOrEmpty(region.Name) && !string.IsNullOrWhiteSpace(block.Name) ? block.Name : region.Name,
            SizeX = block.SizeX > 0 ? block.SizeX : Step,
            SizeY = block.SizeY > 0 ? block.SizeY : Step,
            SizeAuthoritative = true,
            MapImageId = block.MapImageId is { } m && m != Guid.Empty ? m : region.MapImageId,
        };
    }

    private static int RoundUp(int v) => (v + Step - 1) / Step * Step;
}

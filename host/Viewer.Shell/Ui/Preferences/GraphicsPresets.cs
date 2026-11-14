// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The quality values a graphics level sets: the draw distance, the largest texture and the volume level-of-detail factor.</summary>
public readonly record struct GraphicsValues(int DrawDistance, int MaxTextureResolution, float VolumeLodFactor);

/// <summary>
/// The graphics levels of the official viewer's feature table (Low, LowMid, Mid, MidHigh, High, HighUltra, Ultra), each with the values the table
/// gives for the settings this viewer has (RenderFarClip, RenderMaxTextureResolution, RenderVolumeLODFactor). A level sets the quality values only;
/// the frame-rate limit, vertical sync, backend, window mode and clouds are the person's own (the table has no clouds setting). The official viewer
/// picks the level from the graphics card's class; this viewer has no such rule yet, so a fresh install is on HighUltra, the level the official rule gives a graphics card of the top class (its bandwidth rule picks min(class, 5)). Several levels share a draw
/// distance and texture size and differ in the level-of-detail factor, so the level shown is the one stored when its values still hold.
/// </summary>
public static class GraphicsPresets
{
    public const string Low = "low", LowMid = "lowmid", Mid = "mid", MidHigh = "midhigh", High = "high", HighUltra = "highultra", Ultra = "ultra", Custom = "custom";

    /// <summary>The level a fresh install is on (and the factor a custom set of values uses).</summary>
    public const string Default = HighUltra;

    /// <summary>The levels in the official order.</summary>
    public static IReadOnlyList<string> Levels { get; } = [Low, LowMid, Mid, MidHigh, High, HighUltra, Ultra];

    private static readonly Dictionary<string, GraphicsValues> Table = new()
    {
        [Low] = new(64, 512, 1.125f),
        [LowMid] = new(96, 1024, 1.125f),
        [Mid] = new(128, 2048, 1.25f),
        [MidHigh] = new(128, 2048, 1.375f),
        [High] = new(128, 2048, 1.5f),
        [HighUltra] = new(128, 2048, 1.75f),
        [Ultra] = new(256, 2048, 2.0f),
    };

    /// <summary>The values of a level, or null for Custom or an unknown id.</summary>
    public static GraphicsValues? Values(string id) => Table.TryGetValue(id, out var v) ? v : null;

    /// <summary>The first level whose draw distance and texture size these are, or <see cref="Custom"/>.</summary>
    public static string Match(int drawDistance, int maxTexture) =>
        Levels.FirstOrDefault(l => Table[l].DrawDistance == drawDistance && Table[l].MaxTextureResolution == maxTexture) ?? Custom;

    /// <summary>The volume level-of-detail factor of a level; the default level's for Custom or an unknown id.</summary>
    public static float VolumeLodFactor(string? id) => id is not null && Table.TryGetValue(id, out var v) ? v.VolumeLodFactor : Table[Default].VolumeLodFactor;
}

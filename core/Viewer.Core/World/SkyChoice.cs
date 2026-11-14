// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>
/// The sky the viewer shows: the region's own environment (the default), or one of the built-in skies of the official viewer's
/// Environment menu (Sunrise, Midday, Midday (Legacy), Sunset, Midnight). A built-in sky is a settings asset fetched by id
/// (<see cref="KnownSkies"/>); <see cref="SkyChoices.Fraction"/> is the point of the region's own day cycle shown instead when
/// no settings asset can be fetched at all (test renders and replays).
/// </summary>
public enum SkyChoice { Region, Sunrise, Midday, Sunset, Midnight, LegacyMidday }

public static class SkyChoices
{
    /// <summary>The day fraction (0..1) a fixed choice shows; null for <see cref="SkyChoice.Region"/>.</summary>
    public static double? Fraction(SkyChoice c) => c switch
    {
        SkyChoice.Midnight => 0.0,
        SkyChoice.Sunrise => 0.25,
        SkyChoice.Midday => 0.5,
        SkyChoice.LegacyMidday => 0.5,
        SkyChoice.Sunset => 0.75,
        _ => null,
    };

    /// <summary>The next choice in the cycle Region, Sunrise, Midday, Sunset, Midnight, then Region again.</summary>
    public static SkyChoice Next(SkyChoice c) => c switch
    {
        SkyChoice.Region => SkyChoice.Sunrise,
        SkyChoice.Sunrise => SkyChoice.Midday,
        SkyChoice.Midday => SkyChoice.Sunset,
        SkyChoice.Sunset => SkyChoice.Midnight,
        _ => SkyChoice.Region,
    };

    /// <summary>Reads a choice by name (case-insensitive; "noon" is Midday).</summary>
    public static bool TryParse(string? text, out SkyChoice c)
    {
        c = SkyChoice.Region;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "region": return true;
            case "sunrise": c = SkyChoice.Sunrise; return true;
            case "midday": case "noon": c = SkyChoice.Midday; return true;
            case "legacymidday": case "legacy noon": case "legacy midday": c = SkyChoice.LegacyMidday; return true;
            case "sunset": c = SkyChoice.Sunset; return true;
            case "midnight": c = SkyChoice.Midnight; return true;
            default: return false;
        }
    }

    /// <summary>The name shown to the user.</summary>
    public static string Label(SkyChoice c) => c.ToString();
}

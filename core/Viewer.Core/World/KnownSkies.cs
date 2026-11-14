// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>
/// The settings-asset ids of the built-in skies the Environment menu loads. They are the ids the official viewer's menu uses
/// (LLEnvironment::KNOWN_SKY_SUNRISE, KNOWN_SKY_MIDDAY, KNOWN_SKY_LEGACY_MIDDAY, KNOWN_SKY_SUNSET, KNOWN_SKY_MIDNIGHT in
/// llenvironment.cpp); the menu fetches each as a settings asset by id.
/// </summary>
public static class KnownSkies
{
    public static readonly Guid Sunrise = new("01e41537-ff51-2f1f-8ef7-17e4df760bfb");
    public static readonly Guid Midday = new("c46226b4-0e43-5a56-9708-d27ca1df3292");
    public static readonly Guid LegacyMidday = new("6c83e853-e7f8-cad7-8ee6-5f31c453721c");
    public static readonly Guid Sunset = new("084e26cd-a900-28e8-08d0-64a9de5c15e2");
    public static readonly Guid Midnight = new("8a01b97a-cb20-c1ea-ac63-f7ea84ad0090");

    /// <summary>The settings asset a menu choice loads; null for the region's own environment.</summary>
    public static Guid? AssetId(SkyChoice choice) => choice switch
    {
        SkyChoice.Sunrise => Sunrise,
        SkyChoice.Midday => Midday,
        SkyChoice.LegacyMidday => LegacyMidday,
        SkyChoice.Sunset => Sunset,
        SkyChoice.Midnight => Midnight,
        _ => null,
    };
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Media;

/// <summary>A parcel's media (video or page) as the region describes it. Reading it loads nothing from the address.</summary>
/// <param name="Url">The media address as sent (empty when the parcel has none).</param>
/// <param name="TextureId">The parcel's media texture: faces that carry it show the media. Empty when the parcel names none.</param>
/// <param name="Type">The media's MIME type ("none/none" never plays in the official viewer).</param>
public sealed record ParcelMediaInfo(string Url = "", Guid TextureId = default, bool AutoScale = false, string Type = "", string Description = "",
    int Width = 0, int Height = 0, bool Loop = false)
{
    public static ParcelMediaInfo None { get; } = new();
}

/// <summary>A script's parcel media command, with the numbers the protocol uses (the LSL PARCEL_MEDIA_COMMAND_ values).</summary>
public enum ParcelMediaVerb : uint
{
    Stop = 0, Pause = 1, Play = 2, Loop = 3, Texture = 4, Url = 5, Time = 6, Agent = 7, Unload = 8, AutoAlign = 9, Type = 10, Size = 11, Description = 12, LoopSet = 13,
}

/// <summary>The media on one face of an object, as the region's ObjectMedia answer gives it.</summary>
public sealed record FaceMediaEntry(int Face, string CurrentUrl, string HomeUrl, bool AutoPlay = false, bool AutoLoop = false, bool AutoScale = false,
    bool AutoZoom = false, bool FirstClickInteract = false, int Width = 0, int Height = 0, int Controls = 0, int ControlPermissions = 0,
    int InteractPermissions = 0, bool WhitelistEnabled = false, IReadOnlyList<string>? Whitelist = null);

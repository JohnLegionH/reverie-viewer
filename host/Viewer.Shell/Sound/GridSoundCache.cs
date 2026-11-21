// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>Where the sounds a grid serves are kept on disk, apart for each grid.</summary>
public static class GridSoundCache
{
    /// <summary>The content-store kind of a grid sound, interface or world.</summary>
    public const string Kind = "sound";

    /// <summary>A name for a sound's bytes: what a player keeps its decoded stream under. One sound id holds different audio on
    /// different grids, so a decoded stream is never found by the id alone; the bytes name it, and a sound from another grid
    /// (other bytes) is decoded afresh.</summary>
    public static Guid ContentKey(byte[] bytes) => new(System.Security.Cryptography.MD5.HashData(bytes));

    /// <summary>The content-store scope of the grid a login address belongs to.</summary>
    /// <remarks>One sound id holds different audio on different grids, so a sound is cached under the grid it came from (its login
    /// address: host, port and path, case folded), never under a region name, which two grids can share.</remarks>
    public static string Scope(string loginUri)
    {
        var text = (loginUri ?? "").Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return "grid|" + uri.Host.ToLowerInvariant() + ":" + uri.Port + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return "grid|" + text.ToLowerInvariant();
    }
}

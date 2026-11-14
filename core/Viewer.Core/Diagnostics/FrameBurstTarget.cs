// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>
/// Where the development frame burst may write its pictures: only a folder it was explicitly given (the log folder variable), as a full path, and
/// never the viewer's own per-user data folder. With no folder it writes nothing and says why, once.
/// </summary>
public static class FrameBurstTarget
{
    /// <summary>The folder for burst pictures, or null with <paramref name="why"/> set. <paramref name="perUserData"/> is the viewer's own per-user
    /// data folder (never written to by a burst); null or empty when unknown.</summary>
    public static string? Resolve(string? givenFolder, string? perUserData, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(givenFolder))
        {
            why = "no folder given: set NV_LOG_DIR to a full path to save frames";
            return null;
        }
        if (!Path.IsPathFullyQualified(givenFolder))
        {
            why = "NV_LOG_DIR is not a full path";
            return null;
        }
        if (!string.IsNullOrWhiteSpace(perUserData))
        {
            string given = Path.GetFullPath(givenFolder).TrimEnd('\\', '/');
            string own = Path.GetFullPath(perUserData).TrimEnd('\\', '/');
            if (given.Equals(own, StringComparison.OrdinalIgnoreCase)
                || given.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || given.StartsWith(own + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                why = "NV_LOG_DIR is inside the viewer's own per-user folder, which a frame burst never writes to";
                return null;
            }
        }
        return givenFolder;
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>The login screen's Start at: the last location, home, or a region by name.</summary>
public enum StartKind { Last, Home, Region }

/// <summary>
/// Start at, as the three-way control shows it and as the login sends it ("last", "home" or the region's name), the same
/// choices as the official viewer's start location list (llpanellogin.cpp, start_location_combo).
/// </summary>
public static class StartChoice
{
    /// <summary>The control's state for a start text.</summary>
    public static (StartKind Kind, string? Region) Parse(string? start) => (default, null);

    /// <summary>The start text the login sends: a region with no name typed starts at the last location.</summary>
    public static string ToStart(StartKind kind, string? region) => "";

    /// <summary>Remember password is offered only while Remember name is ticked (llpanellogin.cpp, the remember_password tick is
    /// enabled only with remember_name).</summary>
    public static bool PasswordTickEnabled(bool rememberName) => false;
}

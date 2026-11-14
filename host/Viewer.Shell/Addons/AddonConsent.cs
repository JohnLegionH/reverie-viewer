// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Addons;

/// <summary>
/// The box shown when the user turns on an add-on they installed. Permissions guard what the add-on can ask the viewer
/// for, not the computer: an outside add-on is a .NET library loaded into the viewer's process and can do anything the
/// viewer can. The box says so plainly, and lists the permissions as what the add-on says it uses. Built-in add-ons come
/// with the viewer and show no box.
/// </summary>
public static class AddonConsent
{
    /// <summary>The box's text for turning an add-on on; null when no box is shown (a built-in add-on).</summary>
    public static string? Text(StringTable strings, AddonManifest manifest, bool builtIn) =>
        builtIn ? null : strings.Get("addons.confirm.text", manifest.Name, PermissionList(strings, manifest.Permissions));

    /// <summary>The add-on's permissions, one per line, in the user's words.</summary>
    public static string PermissionList(StringTable strings, IReadOnlyList<string> permissions) =>
        permissions.Count == 0 ? strings.Get("addons.permission.none")
            : string.Join("\n", permissions.Select(p => "• " + (strings.Has("addons.permission." + p) ? strings.Get("addons.permission." + p) : p)));
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using System.Runtime.Versioning;

namespace Viewer.Core;

/// <summary>Identity of the loaded core assembly (used by S-1 to prove the net10 core loads in the host).</summary>
public static class CoreInfo
{
    private static Assembly Assembly => typeof(CoreInfo).Assembly;

    public static string Version =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string TargetFramework =>
        Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";
}

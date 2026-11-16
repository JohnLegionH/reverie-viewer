// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;

namespace Viewer.Shell;

/// <summary>Stub (red run).</summary>
public static class JoltcModuleCheck
{
    public sealed record Module(string Name, string Path, string Sha256);

    public static string ExpectedFileName(string rid) => "";
    public static string ExpectedSha256(string rid) => "";
    public static bool IsJoltc(string moduleName) => false;
    public static IReadOnlyList<string> Failures(string rid, IEnumerable<Module> loaded) => ["stub"];
    public static string Rid(bool isWindows, bool isLinux, Architecture arch) => "";
}

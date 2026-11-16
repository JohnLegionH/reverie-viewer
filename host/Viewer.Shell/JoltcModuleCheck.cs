// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;

namespace Viewer.Shell;

/// <summary>
/// S-1's check of which physics library the process loaded. Each platform ships its own vendored build under
/// native/joltc/&lt;rid&gt;/: Windows the patched joltc.dll (per-system temp allocator, see that folder's README),
/// Linux the upstream libjoltc.so for its architecture. Exactly one must be loaded, and it must be that build.
/// </summary>
public static class JoltcModuleCheck
{
    public sealed record Module(string Name, string Path, string Sha256);

    // SHA-256 of native/joltc/<rid>/<file>; JoltcModuleCheckTests compares them with the vendored files.
    private static readonly Dictionary<string, (string File, string Sha256)> Vendored = new(StringComparer.Ordinal)
    {
        ["win-x64"] = ("joltc.dll", "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5"),
        ["linux-x64"] = ("libjoltc.so", "5FC051708BDD05031A816796612A2F87E17ED32CC198F195A43B2305B3D990FD"),
        ["linux-arm64"] = ("libjoltc.so", "FDE70508C826370B5CF6BB54C6EA9CEEAB0C37C826BCD71579EBB9B4AC3E0D61"),
    };

    /// <summary>The platform's folder name under native/joltc/, or "" for a platform no package is built for.</summary>
    public static string Rid(bool isWindows, bool isLinux, Architecture arch) => (isWindows, isLinux, arch) switch
    {
        (true, _, Architecture.X64) => "win-x64",
        (_, true, Architecture.X64) => "linux-x64",
        (_, true, Architecture.Arm64) => "linux-arm64",
        _ => "",
    };

    public static string CurrentRid() => Rid(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture);

    public static string ExpectedFileName(string rid) => Vendored.TryGetValue(rid, out var v) ? v.File : "";

    public static string ExpectedSha256(string rid) => Vendored.TryGetValue(rid, out var v) ? v.Sha256 : "";

    /// <summary>A loaded module is a physics library when its file name is joltc* (Windows) or libjoltc* (Linux).</summary>
    public static bool IsJoltc(string moduleName) =>
        moduleName.StartsWith("joltc", StringComparison.OrdinalIgnoreCase) || moduleName.StartsWith("libjoltc", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is wrong with the loaded physics libraries for this platform; empty when it is right.</summary>
    public static IReadOnlyList<string> Failures(string rid, IEnumerable<Module> loaded)
    {
        if (!Vendored.TryGetValue(rid, out var want))
            return [$"no vendored physics library for platform '{rid}'"];
        var list = loaded.ToList();
        if (list.Count != 1)
            return [$"expected exactly one loaded {want.File}, found {list.Count}"];
        if (!string.Equals(list[0].Sha256, want.Sha256, StringComparison.OrdinalIgnoreCase))
            return [$"loaded {list[0].Name} is not the vendored build: {list[0].Sha256}"];
        return [];
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;

namespace Viewer.Shell;

public enum StartupVerdict { Approved, UnapprovedBackend, BelowFloor, UnknownVersion }

/// <summary>The startup check's outcome: exit code, the string-table key and arguments of its message.</summary>
public sealed record StartupDecision(StartupVerdict Verdict, int ExitCode, string? MessageKey, object?[] MessageArgs, string Backend, string ApiVersion, string Parsed)
{
    public bool Approved => Verdict == StartupVerdict.Approved;

    public string Message(StringTable strings) => MessageKey is null ? "" : strings.Get(MessageKey, MessageArgs);
}

/// <summary>
/// ADR-002: the approved backends and the hardware floor, as a plain function
/// over (backend, API version) so it is testable without a GPU. Vulkan: API version &gt;= 1.3 (Godot reports
/// "major.minor.patch", e.g. "1.4.325"). Direct3D 12: feature level &gt;= 12_0 (Godot reports "12_0"). Metal 3 is recorded
/// for macOS, which is not built yet. Anything else (OpenGL above all) is refused: there is no fallback. A version that
/// cannot be read is refused too: the check never passes on a guess.
/// Exit codes: 3 = unapproved backend (the R-1 refusal, unchanged), 4 = approved backend below the floor or
/// with an unreadable version.
/// </summary>
public static class RenderFloor
{
    public const int ExitUnapprovedBackend = 3;
    public const int ExitBelowFloor = 4;

    public static readonly (int Major, int Minor) Vulkan = (1, 3);
    public static readonly (int Major, int Minor) D3D12FeatureLevel = (12, 0);

    private static readonly Regex VulkanVersion = new(@"^\s*(\d+)\.(\d+)(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex FeatureLevel = new(@"^\s*(\d+)_(\d+)\s*$", RegexOptions.Compiled);

    /// <param name="backend">RenderingServer.GetCurrentRenderingDriverName: "vulkan", "d3d12", "metal", "opengl3"...</param>
    /// <param name="apiVersion">RenderingServer.GetVideoAdapterApiVersion.</param>
    /// <param name="adapter">The GPU's name, for the message.</param>
    public static StartupDecision Check(string backend, string apiVersion, string adapter)
    {
        backend ??= "";
        apiVersion ??= "";
        switch (backend)
        {
            case "vulkan":
                return Versioned(backend, apiVersion, adapter, VulkanVersion, Vulkan, "Vulkan", (ma, mi) => $"{ma}.{mi}");
            case "d3d12":
                return Versioned(backend, apiVersion, adapter, FeatureLevel, D3D12FeatureLevel, "Direct3D 12 feature level", (ma, mi) => $"{ma}_{mi}");
            default:
                return new(StartupVerdict.UnapprovedBackend, ExitUnapprovedBackend, "startup.refused.backend", [backend], backend, apiVersion, "");
        }
    }

    private static StartupDecision Versioned(string backend, string apiVersion, string adapter, Regex pattern, (int Major, int Minor) floor,
        string apiName, Func<int, int, string> show)
    {
        var m = pattern.Match(apiVersion);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int major) || !int.TryParse(m.Groups[2].Value, out int minor))
            return new(StartupVerdict.UnknownVersion, ExitBelowFloor, "startup.refused.unknown_version", [apiName, adapter, apiVersion], backend, apiVersion, "");
        string parsed = show(major, minor);
        bool atOrAbove = major > floor.Major || (major == floor.Major && minor >= floor.Minor);
        return atOrAbove
            ? new(StartupVerdict.Approved, 0, null, [], backend, apiVersion, parsed)
            : new(StartupVerdict.BelowFloor, ExitBelowFloor, "startup.refused.floor", [adapter, apiName, parsed], backend, apiVersion, parsed);
    }
}

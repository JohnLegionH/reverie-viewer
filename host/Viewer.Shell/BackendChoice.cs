// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What this computer's graphics offer to each backend: the Vulkan API version of the adapter (for example "1.2.175") and the
/// highest Direct3D 12 feature level it supports (for example "12_0"). A null means that backend is not present or could not be read.</summary>
public sealed record DeviceReport(string Adapter, string? VulkanApi, string? D3D12FeatureLevel)
{
    /// <summary>Reads "vulkan=1.2.175;d3d12=12_0;adapter=Name" (any part may be left out): a report made up for a development run.</summary>
    public static DeviceReport? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string? vulkan = null, d3d12 = null, adapter = null;
        bool any = false;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = part[..eq].Trim().ToLowerInvariant(), value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "vulkan": vulkan = value; any = true; break;
                case "d3d12": d3d12 = value; any = true; break;
                case "adapter": adapter = value; any = true; break;
            }
        }
        return any ? new DeviceReport(adapter ?? "unknown adapter", vulkan, d3d12) : null;
    }
}

/// <summary>The backend the viewer will run with, and why.</summary>
public sealed record BackendDecision(string? Driver, bool FellBack, string Requested, string LogLine, string? MessageKey, object?[] MessageArgs, string? WhyNot = null);

/// <summary>Which rendering backend to start with: the one asked for (the saved choice, or --backend= for one run) if it meets the hardware floor,
/// else the other one if that does, else none (a plain message).</summary>
public static class BackendChoice
{
    public const string Auto = "auto", Vulkan = "vulkan", D3D12 = "d3d12";

    /// <summary>The choice for this run: a valid --backend= argument wins over the saved choice; anything else is "auto".</summary>
    public static string Requested(string? saved, IReadOnlyList<string> userArgs)
    {
        foreach (var arg in userArgs)
        {
            if (!arg.StartsWith("--backend=", StringComparison.Ordinal))
                continue;
            string value = arg["--backend=".Length..];
            if (value is Auto or Vulkan or D3D12)
                return value;
        }
        return saved is Auto or Vulkan or D3D12 ? saved : Auto;
    }

    public static string Other(string driver) => driver == Vulkan ? D3D12 : Vulkan;

    /// <summary>Holds each backend to the floor of <see cref="RenderFloor"/> and picks one. "auto" prefers Vulkan (the engine's own default), then Direct3D 12.</summary>
    public static BackendDecision Decide(string requested, DeviceReport report)
    {
        string first = requested is Vulkan or D3D12 ? requested : Vulkan;
        string second = Other(first);
        string Describe(string driver) => driver == Vulkan ? $"Vulkan {report.VulkanApi ?? "not available"}" : $"Direct3D 12 {report.D3D12FeatureLevel ?? "not available"}";
        bool Passes(string driver) => RenderFloor.Check(driver, (driver == Vulkan ? report.VulkanApi : report.D3D12FeatureLevel) ?? "", report.Adapter).Approved;

        if (Passes(first))
            return new(first, false, requested, $"backend.choice requested={requested} chosen={first}", null, []);
        if (Passes(second))
            return new(second, true, requested, $"backend.fallback requested={requested}: {Describe(first)} is below the floor, using {second} ({Describe(second)})", null, [],
                $"{Describe(first)} is below the floor");
        return new(null, false, requested, $"backend.none requested={requested}: {Describe(Vulkan)}, {Describe(D3D12)}", "startup.refused.neither",
            [report.Adapter, report.VulkanApi ?? "not available", report.D3D12FeatureLevel ?? "not available"]);
    }
}

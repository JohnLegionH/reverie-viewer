// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every shipping export a release is packed from, Windows and Linux alike, is free of the developer tools: no automation type,
/// enable string, automation script, circuit-closing call or development-only switch (the same scan as <see cref="ExportScanTests"/>,
/// whose negative control proves the scan can see them). The release packaging script (tools\package.ps1) exports each platform
/// into its own folder under one root, points NV_SHIP_EXPORTS at that root and runs this class before it packs anything; without
/// the variable the class skips with its reason.
/// </summary>
public class ShippingExportsScanTests
{
    /// <summary>The platform folders a release is made of, named as the packages name them.</summary>
    public static readonly string[] Platforms = ["windows-x86_64", "linux-x86_64", "linux-arm64"];

    private static string Root => Environment.GetEnvironmentVariable("NV_SHIP_EXPORTS") ?? "";

    /// <summary>The export's managed data folder (data_&lt;assembly&gt;_&lt;platform&gt;_&lt;arch&gt;) and its pack, or nulls when absent.</summary>
    private static (string? DataDir, string? Pck) Locate(string platform)
    {
        var dir = Path.Combine(Root, platform);
        if (!Directory.Exists(dir))
            return (null, null);
        var data = Directory.GetDirectories(dir, "data_Viewer.Godot_*");
        var pck = Path.Combine(dir, "Reverie.pck");
        return (data.Length == 1 ? data[0] : null, File.Exists(pck) ? pck : null);
    }

    [ShippingExportsFact]
    public void Every_release_platform_has_an_export_to_scan()
    {
        var missing = Platforms.Where(p => Locate(p) is not ({ } _, { } _)).ToList();
        Assert.Empty(missing);   // a scan that finds no export proves nothing
    }

    [ShippingExportsTheory]
    [InlineData("windows-x86_64")]
    [InlineData("linux-x86_64")]
    [InlineData("linux-arm64")]
    public void The_shipping_export_contains_no_developer_tools(string platform)
    {
        var (data, pck) = Locate(platform);
        Assert.NotNull(data);
        Assert.NotNull(pck);
        var f = ExportScanTests.Scan(data!, pck!);
        Assert.Empty(f.Types);
        Assert.Empty(f.Strings);
        Assert.Empty(f.Files);
        Assert.Empty(f.Calls);
        Assert.Empty(f.Switches);
    }
}

public sealed class ShippingExportsFactAttribute : FactAttribute
{
    public ShippingExportsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NV_SHIP_EXPORTS")))
            Skip = "the release exports are scanned by tools\\package.ps1 (NV_SHIP_EXPORTS names their folder), never a stale set";
    }
}

public sealed class ShippingExportsTheoryAttribute : TheoryAttribute
{
    public ShippingExportsTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NV_SHIP_EXPORTS")))
            Skip = "the release exports are scanned by tools\\package.ps1 (NV_SHIP_EXPORTS names their folder), never a stale set";
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// LibreMetaverse's own logging reaches nothing unredacted. The probe runs as a child process (the capture
/// tool's lmv-log-probe) so its real stdout and stderr are scanned; the negative control runs the same probe with
/// LibreMetaverse's default console logger and must show the raw values.
/// </summary>
public class LmvLogRouteTests
{
    private static readonly string[] Raw =
    [
        "203.0.113.13", "198.51.100.40", "192.0.2.9", "0f1e2d3c-secret-path", "/CAPS/abc/", "11111111-2222-3333-4444-555555555555",
    ];

    private static string Probe(params string[] extra)
    {
        var (exit, output) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.CaptureToolDll, "lmv-log-probe", .. extra], TimeSpan.FromSeconds(60));
        Assert.True(exit == 0, "probe exit " + exit + ": " + output);
        return output;
    }

    [Fact]
    public void Routed_library_logging_leaves_no_raw_address_capability_path_or_session_id_on_stdout_or_stderr()
    {
        string output = Probe();
        foreach (var r in Raw)
            Assert.DoesNotContain(r, output);
        Assert.Contains("lmv.info", output);                          // the messages did arrive, through the core's log
        Assert.Contains("lmv.warning", output);
        Assert.Contains("[redacted:NetworkAddress]", output);
        Assert.Contains("[redacted:path]", output);
        Assert.Contains("[redacted:SessionId]", output);
        Assert.DoesNotContain("Default console logger initialized", output);
        Assert.Contains("[CoreJ2K]", output);                          // CoreJ2K's own logger took the same route
        Assert.DoesNotContain("[WARNING]:", output);                    // its raw console format never appears
    }

    [Fact]
    public void Negative_control_the_library_default_console_logger_prints_the_raw_values()
    {
        string output = Probe("--default-console", "1");
        Assert.Contains("203.0.113.13", output);
        Assert.Contains("0f1e2d3c-secret-path", output);
    }

    [Fact]
    public void Engine_text_scrubs_addresses_but_not_clock_times()
    {
        var r = Redactor.Default;
        Assert.Equal("to [redacted:NetworkAddress] at 13:59:35", r.ScrubEngine("to 203.0.113.13:9000 at 13:59:35"));
        Assert.Equal("[redacted:NetworkAddress]", r.ScrubEngine("[fe80::1]:9000"));
        Assert.Equal("v [redacted:NetworkAddress]", r.ScrubEngine("v 2001:db8:0:0:0:ff00:42:8329"));
        // negative control: with the address kind switched off the value survives
        Assert.Contains("203.0.113.13", Redactor.WithoutForTests(SensitiveKind.NetworkAddress).ScrubEngine("to 203.0.113.13:9000"));
    }
}

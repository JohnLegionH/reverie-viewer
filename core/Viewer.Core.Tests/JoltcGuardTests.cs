// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools/assert-joltc.ps1 against the real test output (the vendored patched joltc must be the only joltc there),
/// plus proof that the guard rejects the stock native.
/// </summary>
public class JoltcGuardTests
{
    private const string StockHash = "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910";
    private readonly ITestOutputHelper _out;
    public JoltcGuardTests(ITestOutputHelper output) { _out = output; }

    private static string Guard => Path.Combine(TestPaths.RepoRoot, "tools", "assert-joltc.ps1");

    private (int ExitCode, string Output) RunGuard(string dir)
    {
        var r = ChildProcess.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Guard, "-Path", dir],
            TimeSpan.FromMinutes(2));
        _out.WriteLine(r.Output);
        return r;
    }

    [Fact]
    public void Test_output_contains_only_the_patched_joltc()
    {
        Assert.True(OperatingSystem.IsWindows(), "joltc is vendored for win-x64 only");
        var (exit, output) = RunGuard(TestPaths.TestOutputDir);
        Assert.Equal(0, exit);
        Assert.Contains("JOLTC GUARD OK", output);
    }

    [Fact]
    public void Guard_rejects_the_stock_joltc()
    {
        Assert.True(OperatingSystem.IsWindows(), "joltc is vendored for win-x64 only");
        string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } n
            ? n
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string stock = Path.Combine(nugetRoot, "joltphysics.native", "1.0.4", "runtimes", "win-x64", "native", "joltc.dll");
        Assert.True(File.Exists(stock), $"stock joltc not in the NuGet cache (restore brings it): {stock}");

        // A directory holding the patched copy AND a stock copy: one bad copy anywhere must fail the run.
        // A sibling of the test output (still under bin/), so the positive scan above never sees the stock copy.
        string dir = Path.GetFullPath(Path.Combine(TestPaths.TestOutputDir, "..", "guard-negative"));
        Directory.CreateDirectory(Path.Combine(dir, "runtimes"));
        File.Copy(Path.Combine(TestPaths.TestOutputDir, "joltc.dll"), Path.Combine(dir, "joltc.dll"), overwrite: true);
        File.Copy(stock, Path.Combine(dir, "runtimes", "joltc.dll"), overwrite: true);

        var (exit, output) = RunGuard(dir);
        Assert.Equal(1, exit);
        Assert.Contains("STOCK JoltPhysics.Native 1.0.4", output);
        Assert.Contains(StockHash, output);
    }
}

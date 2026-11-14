// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Tools/assert-no-linden.ps1 against every .NET build output in the repo (the core, this test
/// output, the lifecycle smoke, the architecture fixture), plus proof that the guard rejects each kind of Linden file.
/// The S-1 stage runs the same script on the Godot export.
/// </summary>
public class LindenGuardTests
{
    private readonly ITestOutputHelper _out;
    public LindenGuardTests(ITestOutputHelper output) { _out = output; }

    private static string Guard => Path.Combine(TestPaths.RepoRoot, "tools", "assert-no-linden.ps1");

    private (int ExitCode, string Output) RunGuard(params string[] dirs)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Guard };
        foreach (var d in dirs)
        {
            args.Add("-Path");
            args.Add(d);
        }
        var r = ChildProcess.Run("powershell.exe", args.ToArray(), TimeSpan.FromMinutes(2));
        _out.WriteLine(r.Output);
        return r;
    }

    [Fact]
    public void No_build_output_carries_Linden_content()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        foreach (var dir in new[] { TestPaths.CoreOutputDir, TestPaths.TestOutputDir, TestPaths.LifecycleSmokeDir, TestPaths.ArchFixtureDir })
        {
            var (exit, output) = RunGuard(dir);
            Assert.True(exit == 0, $"Linden content in {dir}:\n{output}");
            Assert.Contains("LINDEN GUARD OK", output);
        }
    }

    [Theory]
    [InlineData("linden-dir", "linden/character/attentions.xml")]
    [InlineData("lad", "data/avatar_lad.xml")]
    [InlineData("skeleton", "avatar_skeleton.xml")]
    [InlineData("llm", "meshes/avatar_eye.llm")]
    public void Guard_rejects_each_kind_of_Linden_content(string name, string relative)
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        // A sibling of the test output (still under bin/), so the positive scan above never sees these files.
        string dir = Path.GetFullPath(Path.Combine(TestPaths.TestOutputDir, "..", "linden-guard-negative", name));
        string file = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "negative control");
        File.WriteAllText(Path.Combine(dir, "Viewer.Core.dll.stub"), "an ordinary file beside it");

        var (exit, output) = RunGuard(dir);
        Assert.Equal(1, exit);
        Assert.Contains("LINDEN GUARD FAIL", output);
    }

    /// <summary>The shipped build carries the avatar data folder on purpose: the guard skips a folder named with -Allow
    /// and still fails on Linden data anywhere else.</summary>
    [Fact]
    public void Guard_allows_only_the_named_avatar_data_folder()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        string dir = Path.GetFullPath(Path.Combine(TestPaths.TestOutputDir, "..", "linden-guard-negative", "allowed-only"));
        string allowed = Path.Combine(dir, "character");
        Directory.CreateDirectory(allowed);
        File.WriteAllText(Path.Combine(allowed, "avatar_lad.xml"), "shipped on purpose");
        File.WriteAllText(Path.Combine(allowed, "avatar_eye.llm"), "shipped on purpose");
        File.WriteAllText(Path.Combine(dir, "Viewer.Core.dll.stub"), "an ordinary file beside it");

        // two folders in one argument, as S-1 passes them (powershell -File passes no arrays)
        var (exitAllowed, outAllowed) = RunGuardArgs("-Path", dir, "-Allow", allowed + ";" + Path.Combine(dir, "dev", "character"));
        Assert.True(exitAllowed == 0, outAllowed);
        Assert.Contains("LINDEN GUARD OK", outAllowed);

        var (exitPlain, outPlain) = RunGuardArgs("-Path", dir);
        Assert.Equal(1, exitPlain);
        Assert.Contains("LINDEN GUARD FAIL", outPlain);

        // a second folder of its own (the first must hold only the allowed folder, whatever earlier runs left)
        string dir2 = Path.GetFullPath(Path.Combine(TestPaths.TestOutputDir, "..", "linden-guard-negative", "allowed-and-stray"));
        string allowed2 = Path.Combine(dir2, "character");
        Directory.CreateDirectory(allowed2);
        File.WriteAllText(Path.Combine(allowed2, "avatar_lad.xml"), "shipped on purpose");
        string stray = Path.Combine(dir2, "elsewhere");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "avatar_skeleton.xml"), "not in the allowed folder");
        var (exitStray, outStray) = RunGuardArgs("-Path", dir2, "-Allow", allowed2);
        Assert.Equal(1, exitStray);
        Assert.Contains("avatar_skeleton.xml", outStray);
    }

    private (int ExitCode, string Output) RunGuardArgs(params string[] extra)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Guard };
        args.AddRange(extra);
        var r = ChildProcess.Run("powershell.exe", args.ToArray(), TimeSpan.FromMinutes(2));
        _out.WriteLine(r.Output);
        return r;
    }

    [Fact]
    public void Guard_fails_on_an_empty_directory()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard runs under Windows PowerShell");
        string dir = Path.GetFullPath(Path.Combine(TestPaths.TestOutputDir, "..", "linden-guard-negative", "empty"));
        Directory.CreateDirectory(dir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));

        var (exit, output) = RunGuard(dir);
        Assert.Equal(1, exit);
        Assert.Contains("checks nothing", output);
    }
}

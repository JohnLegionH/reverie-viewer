// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// S-1's check (c), which physics library is loaded, for each platform the packages are built for: Windows loads
/// joltc.dll (the patched build), Linux loads libjoltc.so (the vendored build for its architecture).
/// </summary>
public class JoltcModuleCheckTests
{
    private static string Vendored(string rid, string file) => Path.Combine(TestPaths.RepoRoot, "native", "joltc", rid, file);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData("win-x64", "joltc.dll")]
    [InlineData("linux-x64", "libjoltc.so")]
    [InlineData("linux-arm64", "libjoltc.so")]
    public void Each_platform_expects_its_vendored_library_by_name_and_hash(string rid, string file)
    {
        Assert.Equal(file, JoltcModuleCheck.ExpectedFileName(rid));
        Assert.Equal(Sha(Vendored(rid, file)), JoltcModuleCheck.ExpectedSha256(rid), ignoreCase: true);
    }

    [Theory]
    [InlineData("libjoltc.so", true)]
    [InlineData("joltc.dll", true)]
    [InlineData("JOLTC.DLL", true)]
    [InlineData("libjolt.so", false)]
    [InlineData("kernel32.dll", false)]
    public void A_module_is_a_physics_library_by_its_name(string name, bool expected)
    {
        Assert.Equal(expected, JoltcModuleCheck.IsJoltc(name));
    }

    [Theory]
    [InlineData("linux-x64", "libjoltc.so")]
    [InlineData("linux-arm64", "libjoltc.so")]
    [InlineData("win-x64", "joltc.dll")]
    public void The_vendored_library_loaded_once_passes(string rid, string file)
    {
        var loaded = new[] { new JoltcModuleCheck.Module(file, "/opt/r/" + file, Sha(Vendored(rid, file))) };
        Assert.Empty(JoltcModuleCheck.Failures(rid, loaded));
    }

    [Fact]
    public void None_loaded_fails_and_names_the_file_it_looked_for()
    {
        var f = Assert.Single(JoltcModuleCheck.Failures("linux-x64", []));
        Assert.Contains("libjoltc.so", f);
        Assert.Contains("found 0", f);
    }

    [Fact]
    public void Another_build_fails()
    {
        var loaded = new[] { new JoltcModuleCheck.Module("libjoltc.so", "/opt/r/libjoltc.so", new string('0', 64)) };
        var f = Assert.Single(JoltcModuleCheck.Failures("linux-x64", loaded));
        Assert.Contains("not the vendored build", f);
    }

    [Fact]
    public void Two_copies_fail()
    {
        string sha = Sha(Vendored("win-x64", "joltc.dll"));
        var loaded = new[] { new JoltcModuleCheck.Module("joltc.dll", "/opt/a/joltc.dll", sha), new JoltcModuleCheck.Module("joltc.dll", "/opt/b/joltc.dll", sha) };
        Assert.Contains("found 2", Assert.Single(JoltcModuleCheck.Failures("win-x64", loaded)));
    }

    [Theory]
    [InlineData(true, false, Architecture.X64, "win-x64")]
    [InlineData(false, true, Architecture.X64, "linux-x64")]
    [InlineData(false, true, Architecture.Arm64, "linux-arm64")]
    public void The_platform_is_named_as_the_vendored_folders_are(bool windows, bool linux, Architecture arch, string rid)
    {
        Assert.Equal(rid, JoltcModuleCheck.Rid(windows, linux, arch));
    }
}

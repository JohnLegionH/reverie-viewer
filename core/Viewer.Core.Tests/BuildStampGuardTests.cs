// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>The commit the fake exports of the render script tests are stamped with, and the one those runs are told is under test.</summary>
internal static class FakeExport
{
    public const string Commit = "1111111111111111111111111111111111111111";
}

/// <summary>
/// tools\render.ps1 draws against the exported build in host\Viewer.Godot\build. tools\s1.ps1 writes build-stamp.txt
/// ("commit=&lt;sha&gt;") beside the export, and render.ps1 refuses to render when that stamp is missing or is not the commit under
/// test, so a render can never silently draw a build made from other code. -AllowStaleBuild is the explicit override, and it
/// logs what it overrode. The check runs a copy of the tools folder whose godot.ps1 is a stand-in that starts nothing and records
/// each call. Each test builds its own temp folder, so the tests are independent and run in parallel.
/// </summary>
public sealed class BuildStampGuardTests
{
    private readonly ITestOutputHelper _out;
    public BuildStampGuardTests(ITestOutputHelper output) { _out = output; }

    private const string UnderTest = FakeExport.Commit;
    private const string Older = "2222222222222222222222222222222222222222";

    private sealed record Outcome(int ExitCode, string Output, string[] Launches);

    /// <param name="stamp">The text of build-stamp.txt, or null for an export with no stamp.</param>
    private Outcome Run(string? stamp, params string[] extraArgs)
    {
        string root = TestTemp.NewFolder("build-stamp");
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "tools"), "*.ps1"))
            File.Copy(f, Path.Combine(tools, Path.GetFileName(f)));
        string build = Path.Combine(root, "host", "Viewer.Godot", "build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "Reverie.console.exe"), "");   // present, never started
        Directory.CreateDirectory(Path.Combine(build, "data_Viewer.Godot_windows_x86_64"));
        File.WriteAllText(Path.Combine(build, "data_Viewer.Godot_windows_x86_64", "Viewer.Godot.dll"), "");
        if (stamp != null) File.WriteAllText(Path.Combine(build, "build-stamp.txt"), stamp);
        string local = Path.Combine(root, "fixtures-local");
        Directory.CreateDirectory(Path.Combine(local, "assets"));
        string characters = Path.Combine(root, "characters");
        Directory.CreateDirectory(characters);
        string launches = Path.Combine(root, "launches.txt");
        string stand = string.Join("\r\n",
            "param([Parameter(ValueFromRemainingArguments)]$rest)",
            "Add-Content -LiteralPath '" + launches.Replace("'", "''") + "' -Value ($rest -join ' ')",
            "exit 1") + "\r\n";
        File.WriteAllText(Path.Combine(tools, "godot.ps1"), stand);
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools, "render.ps1"),
            "-AvatarsOnly", "-Backends", "vulkan", "-ExpectedCommit", UnderTest };
        args.AddRange(extraArgs);
        var env = new Dictionary<string, string?>
        {
            ["CLAIM_TOKEN"] = null,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_CHARACTER_DIR"] = characters,
            ["NV_FIXTURES_LOCAL"] = local,
            ["NV_TEMP_ROOT"] = temp,
        };
        var (code, output) = ChildProcess.Run("powershell.exe", args, TimeSpan.FromSeconds(90), env);
        string[] calls = File.Exists(launches) ? File.ReadAllLines(launches) : [];
        _out.WriteLine($"render.ps1 {string.Join(' ', extraArgs)} (stamp {stamp ?? "none"}): exit {code}, {calls.Length} launches\n{output}");
        return new Outcome(code, output, calls);
    }

    [Fact]
    public void A_stale_stamp_refuses_the_render_and_names_both_commits()
    {
        var r = Run($"commit={Older}\n");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("REFUSED", r.Output);
        Assert.Contains(Older, r.Output);
        Assert.Contains(UnderTest, r.Output);
        Assert.Empty(r.Launches);   // nothing was drawn
    }

    [Fact]
    public void A_missing_stamp_refuses_the_render()
    {
        var r = Run(null);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("REFUSED", r.Output);
        Assert.Contains("no build-stamp.txt", r.Output);
        Assert.Empty(r.Launches);
    }

    [Fact]
    public void A_matching_stamp_lets_the_render_start()
    {
        var r = Run($"commit={UnderTest}\n");
        Assert.DoesNotContain("REFUSED", r.Output);
        Assert.NotEmpty(r.Launches);   // the stand-in launcher was called: the render got past the guard
    }

    [Fact]
    public void The_explicit_override_renders_a_stale_build_and_logs_it()
    {
        var r = Run($"commit={Older}\n", "-AllowStaleBuild");
        Assert.DoesNotContain("REFUSED", r.Output);
        Assert.Contains("-AllowStaleBuild", r.Output);
        Assert.Contains(Older, r.Output);
        Assert.NotEmpty(r.Launches);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\render.ps1 -Backends names the rendering backends a run uses. The avatar attach and rigged blocks used to loop over both
/// backends whatever it said, so a one-backend run still started the other backend's jobs. The check runs a copy of the tools
/// folder whose godot.ps1 is a stand-in that starts nothing: every engine job the run asks for goes to the stand-in, fails there, and
/// is reported by case name, so the names list exactly the cases (and so the backends) the run asked for.
/// The copy lives in a temp folder of its own, so the tests are independent of each other and run in parallel.
/// </summary>
public sealed class RenderBackendsTests
{
    private readonly ITestOutputHelper _out;
    public RenderBackendsTests(ITestOutputHelper output) { _out = output; }

    /// <summary>Runs the avatar cases of render.ps1 and returns the case names it reported.</summary>
    private string[] RunAvatarCases(string? backend)
    {
        string root = TestTemp.NewFolder("render-backends");
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "tools"), "*.ps1"))
            File.Copy(f, Path.Combine(tools, Path.GetFileName(f)));
        string build = Path.Combine(root, "host", "Viewer.Godot", "build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "Reverie.console.exe"), "");   // present, never started
        string local = Path.Combine(root, "fixtures-local");
        Directory.CreateDirectory(Path.Combine(local, "assets"));
        File.WriteAllText(Path.Combine(local, "assets", "6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0.anim"), "");   // the avatar cases look for this file; with it they run, and every run goes to the stand-in launcher
        string characters = Path.Combine(root, "characters");
        Directory.CreateDirectory(characters);
        string launches = Path.Combine(root, "launches.txt");
        string stand = string.Join("\r\n",
            "param([Parameter(ValueFromRemainingArguments)]$rest)",
            "Add-Content -LiteralPath '" + launches.Replace("'", "''") + "' -Value ($rest -join ' ')",
            "exit 1") + "\r\n";
        File.WriteAllText(Path.Combine(tools, "godot.ps1"), stand);   // the stand-in launcher: records the call, starts nothing
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools, "render.ps1"), "-AvatarsOnly" };
        if (backend is not null) { args.Add("-Backends"); args.Add(backend); }   // no switch at all means the default, both
        var env = new Dictionary<string, string?>
        {
            ["CLAIM_TOKEN"] = null,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_CHARACTER_DIR"] = characters,
            ["NV_FIXTURES_LOCAL"] = local,
            ["NV_TEMP_ROOT"] = temp,
        };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(3), env);
        var names = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("Render.")).Select(l => l[(l.IndexOf("Render.", StringComparison.Ordinal) + 7)..].Split(' ')[0]).ToArray();
        _out.WriteLine($"-Backends {backend ?? "<default>"}: exit {r.ExitCode}, {names.Length} cases\n{string.Join('\n', names)}");
        return names;
    }

    private static bool Is(string name, string baseName, string backend) => name == $"{baseName}_{backend}";

    private static readonly string[] BackendLoopedCases = ["avatars_attach", "avatars_rigged", "avatars_rigged_stress", "avatars_rigged_stall", "avatars_rigged_blend"];

    [Fact]
    public void A_vulkan_only_run_starts_no_job_for_d3d12()
    {
        var jobs = RunAvatarCases("vulkan");
        // the positive state first: the Vulkan attach and rigged jobs are in the run
        foreach (var name in BackendLoopedCases)
            Assert.Contains(jobs, j => Is(j, name, "vulkan"));
        Assert.DoesNotContain(jobs, j => j.Contains("d3d12", StringComparison.Ordinal));
    }

    [Fact]
    public void A_d3d12_only_run_starts_no_attach_or_rigged_job_for_vulkan_that_loops_over_backends()
    {
        var jobs = RunAvatarCases("d3d12");
        foreach (var name in BackendLoopedCases)
        {
            Assert.Contains(jobs, j => Is(j, name, "d3d12"));
            Assert.DoesNotContain(jobs, j => Is(j, name, "vulkan"));
        }
    }

    [Fact]
    public void The_default_still_starts_both_backends()
    {
        var jobs = RunAvatarCases(null);
        foreach (var name in BackendLoopedCases)
        {
            Assert.Contains(jobs, j => Is(j, name, "vulkan"));
            Assert.Contains(jobs, j => Is(j, name, "d3d12"));
        }
    }
}

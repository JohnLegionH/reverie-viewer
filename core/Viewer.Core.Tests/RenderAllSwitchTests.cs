// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\render.ps1 used to run the whole render set when no section switch was given, so a call meant for one section could start
/// everything. The full set now needs -All; with no section switch the script lists its sections and exits without rendering.
/// The check runs a copy of the tools folder whose godot.ps1 is a stand-in that starts nothing and records each call.
/// The copy lives in a temp folder of its own, so the tests are independent and run in parallel.
/// </summary>
public sealed class RenderAllSwitchTests
{
    private readonly ITestOutputHelper _out;
    public RenderAllSwitchTests(ITestOutputHelper output) { _out = output; }

    private sealed record Outcome(int ExitCode, string Output, bool TimedOut, string[] Launches);

    private Outcome Run(TimeSpan timeout, params string[] scriptArgs)
    {
        string root = TestTemp.NewFolder("render-all");
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "tools"), "*.ps1"))
            File.Copy(f, Path.Combine(tools, Path.GetFileName(f)));
        string build = Path.Combine(root, "host", "Viewer.Godot", "build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "Reverie.console.exe"), "");   // present, never started
        // a complete export: the data folder with the host assembly beside the executable (render.ps1 starts nothing without it)
        Directory.CreateDirectory(Path.Combine(build, "data_Viewer.Godot_windows_x86_64"));
        File.WriteAllText(Path.Combine(build, "data_Viewer.Godot_windows_x86_64", "Viewer.Godot.dll"), "");
        string local = Path.Combine(root, "fixtures-local");
        Directory.CreateDirectory(Path.Combine(local, "assets"));
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

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools, "render.ps1") };
        args.AddRange(scriptArgs);
        var env = new Dictionary<string, string?>
        {
            ["CLAIM_TOKEN"] = null,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_CHARACTER_DIR"] = characters,
            ["NV_FIXTURES_LOCAL"] = local,
            ["NV_TEMP_ROOT"] = temp,
        };
        int code; string output; bool timedOut = false;
        try { (code, output) = ChildProcess.Run("powershell.exe", args, timeout, env); }
        catch (TimeoutException e) { code = -1; output = e.Message; timedOut = true; }   // killed: it was rendering
        string[] calls = File.Exists(launches) ? File.ReadAllLines(launches) : [];
        _out.WriteLine($"render.ps1 {string.Join(' ', scriptArgs)}: exit {code}, timed out {timedOut}, {calls.Length} launches\n{output}");
        return new Outcome(code, output, timedOut, calls);
    }

    [Fact]
    public void With_no_section_switch_the_script_lists_its_sections_and_renders_nothing()
    {
        var r = Run(TimeSpan.FromSeconds(60));
        Assert.False(r.TimedOut, "the script kept running with no section switch");
        Assert.Equal(2, r.ExitCode);   // nothing was rendered: never a silent pass
        Assert.Contains("-All", r.Output);
        Assert.Contains("-AvatarsOnly", r.Output);
        Assert.Contains("-GlowOnly", r.Output);
        Assert.Contains("-Residency", r.Output);
        Assert.Empty(r.Launches);
    }

    [Theory]
    [InlineData("-Propose")]
    [InlineData("-DiagDir")]
    public void Switches_that_name_no_section_still_only_list_the_sections(string option)
    {
        string[] args = option == "-DiagDir" ? ["-DiagDir", Path.Combine(TestTemp.NewFolder("render-all-diag"), "diag")] : [option];
        var r = Run(TimeSpan.FromSeconds(60), args);
        Assert.False(r.TimedOut, "the script kept running with no section switch");
        Assert.Equal(2, r.ExitCode);   // nothing was rendered: never a silent pass
        Assert.Contains("-All", r.Output);
        Assert.Empty(r.Launches);
    }

    [Fact]
    public void A_backends_choice_alone_is_not_a_section()
    {
        var r = Run(TimeSpan.FromSeconds(60), "-Backends", "vulkan");
        Assert.False(r.TimedOut, "the script kept running with no section switch");
        Assert.Equal(2, r.ExitCode);   // nothing was rendered: never a silent pass
        Assert.Contains("-All", r.Output);
        Assert.Empty(r.Launches);
    }

    [Fact]
    public void OnlyCases_alone_is_not_a_section_and_fails_rendering_nothing()
    {
        var r = Run(TimeSpan.FromSeconds(60), "-OnlyCases", "ui_", "-Backends", "vulkan");
        Assert.False(r.TimedOut);
        Assert.Equal(2, r.ExitCode);
        Assert.Empty(r.Launches);
    }

    [Fact]
    public void A_section_whose_filter_picks_no_case_fails_because_it_rendered_none()
    {
        var r = Run(TimeSpan.FromMinutes(3), "-AvatarsOnly", "-Backends", "vulkan", "-OnlyCases", "no-case-has-this-tag");
        Assert.False(r.TimedOut);
        Assert.Empty(r.Launches);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("RENDERED NO CASE", r.Output);
    }

    [Fact]
    public void A_section_switch_runs_that_section_without_all()
    {
        var r = Run(TimeSpan.FromMinutes(3), "-AvatarsOnly", "-Backends", "vulkan");
        Assert.False(r.TimedOut);
        Assert.NotEmpty(r.Launches);
        Assert.DoesNotContain("Sections of render.ps1", r.Output);
    }

    [Fact]
    public void All_starts_the_full_set()
    {
        // the full set against the stand-in is hundreds of failing jobs: the run is cut off once it has plainly started
        var r = Run(TimeSpan.FromSeconds(25), "-All", "-Backends", "vulkan");
        Assert.NotEmpty(r.Launches);
        Assert.DoesNotContain("Sections of render.ps1", r.Output);
    }
}

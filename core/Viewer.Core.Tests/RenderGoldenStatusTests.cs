// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\render.ps1 compares a picture with its golden at every window size, says plainly which cases have no golden, and never
/// writes a proposed picture into the committed goldens. The check runs a copy of the tools folder whose godot.ps1 is a stand-in
/// viewer: it writes the result file and the picture a real run would write, and says the picture matches its golden only when the
/// golden file holds the text "match". Each test builds its own temp folder (its own copy of the tools, fixtures and output), so the
/// tests are independent and run in parallel.
/// </summary>
public sealed class RenderGoldenStatusTests
{
    private readonly ITestOutputHelper _out;
    public RenderGoldenStatusTests(ITestOutputHelper output) { _out = output; }

    private sealed record Outcome(int ExitCode, string Output, string Root, string[] Launches)
    {
        public string FixturesUi => Path.Combine(Root, "fixtures", "ui");
    }

    private static string Golden(string pct, string size) => $"golden-uichrome-{pct}-{size}-vulkan.png";

    /// <param name="goldens">Golden file name to its text ("match" matches the stand-in's picture, anything else does not).</param>
    /// <param name="extra">Further render.ps1 arguments.</param>
    private Outcome Run(Dictionary<string, string> goldens, string only, params string[] extra)
    {
        string root = TestTemp.NewFolder("render-golden");
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "tools"), "*.ps1"))
            File.Copy(f, Path.Combine(tools, Path.GetFileName(f)));
        string build = Path.Combine(root, "host", "Viewer.Godot", "build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "Reverie.console.exe"), "");   // present, never started
        Directory.CreateDirectory(Path.Combine(build, "data_Viewer.Godot_windows_x86_64"));
        File.WriteAllText(Path.Combine(build, "data_Viewer.Godot_windows_x86_64", "Viewer.Godot.dll"), "");
        File.WriteAllText(Path.Combine(build, "build-stamp.txt"), $"commit={FakeExport.Commit}\n");
        string ui = Path.Combine(root, "fixtures", "ui");
        Directory.CreateDirectory(ui);
        foreach (var (name, text) in goldens) File.WriteAllText(Path.Combine(ui, name), text);
        string local = Path.Combine(root, "fixtures-local");
        Directory.CreateDirectory(Path.Combine(local, "assets"));
        string characters = Path.Combine(root, "characters");
        Directory.CreateDirectory(characters);
        string launches = Path.Combine(root, "launches.txt");
        // the stand-in viewer: reads the output folder, tag and golden from the argument line, writes <tag>.json and <tag>.png
        // like a real run, and exits 1 when a golden was given and its text is not "match"
        string stand = string.Join("\r\n",
            "param([Parameter(ValueFromRemainingArguments)]$rest)",
            "$line = $rest -join ' '",
            "Add-Content -LiteralPath '" + launches.Replace("'", "''") + "' -Value $line",
            "function Arg($n) { if ($line -match ('--' + $n + '=(?:\"([^\"]+)\"|(\\S+))')) { if ($Matches[1]) { $Matches[1] } else { $Matches[2] } } }",
            "$o = Arg 'tr-out'; $tag = Arg 'tr-tag'; $g = Arg 'tr-golden'",
            "Set-Content -LiteralPath (Join-Path $o \"$tag.png\") -Value 'picture'",
            "$j = @{ backend = @{ driver = 'vulkan' }; failures = @(); ui_chrome = @{ logical = '1x1'; dock_buttons = 1; dock_rows = 1; problems = @() } }",
            "$code = 0",
            "if ($g) { $same = (Get-Content -Raw -LiteralPath $g).Trim() -eq 'match'; $j.diff = @{ pass = $same; summary = $(if ($same) { '0 of 9 pixels differ' } else { '9 of 9 pixels differ' }) }; if (-not $same) { $code = 1 } }",
            "$j | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $o \"$tag.json\")",
            "exit $code") + "\r\n";
        File.WriteAllText(Path.Combine(tools, "godot.ps1"), stand);
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools, "render.ps1"),
            "-UiChromeOnly", "-Backends", "vulkan", "-OneProcessPerCase", "-OnlyCases", only, "-ExpectedCommit", FakeExport.Commit };
        args.AddRange(extra.Select(e => e.Replace("@REPO@", root)));
        var env = new Dictionary<string, string?>
        {
            ["CLAIM_TOKEN"] = null,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_CHARACTER_DIR"] = characters,
            ["NV_FIXTURES_LOCAL"] = null,   // unset: the run reads the fixtures-local folder of its own repository, so the local-only refusal of -Propose does not stand in for the one under test
            ["NV_TEMP_ROOT"] = temp,
        };
        var (code, output) = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), env);
        string[] calls = File.Exists(launches) ? File.ReadAllLines(launches) : [];
        _out.WriteLine($"render.ps1 {string.Join(' ', extra)} ({only}): exit {code}, {calls.Length} launches\n{output}");
        return new Outcome(code, output, root, calls);
    }

    /// <summary>The status word the run printed for one case (the text between the leading blanks and "Render.&lt;name&gt;").</summary>
    private static string StatusOf(Outcome r, string name)
    {
        var line = r.Output.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.Contains("Render." + name + " ", StringComparison.Ordinal));
        return line[..line.IndexOf("Render.", StringComparison.Ordinal)].Trim();
    }

    private const string Chrome720 = "ui_chrome_100_1280x720_vulkan";
    private const string Only720 = "uichrome-100-1280x720";

    [Fact]
    public void A_picture_at_1280x720_that_does_not_match_its_golden_fails()
    {
        var r = Run(new() { [Golden("100", "1280x720")] = "different" }, Only720);
        Assert.Equal("FAIL", StatusOf(r, Chrome720));
        Assert.Contains("failed 1, no golden 0", r.Output);   // the summary counts it as failed (a section-only run's exit code is not part of this check)
        Assert.Contains(r.Launches, l => l.Contains("--tr-golden=", StringComparison.Ordinal));   // the golden of this size was handed to the viewer
    }

    [Fact]
    public void A_picture_at_1280x720_that_matches_its_golden_passes()
    {
        var r = Run(new() { [Golden("100", "1280x720")] = "match" }, Only720);
        Assert.Equal("PASS", StatusOf(r, Chrome720));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("0 of 9 pixels differ", r.Output);   // it was compared
    }

    [Fact]
    public void A_picture_at_1920x1080_is_compared_with_its_own_golden_too()
    {
        var r = Run(new() { [Golden("100", "1920x1080")] = "different" }, "uichrome-100-1920x1080");
        Assert.Equal("FAIL", StatusOf(r, "ui_chrome_100_1920x1080_vulkan"));
    }

    [Fact]
    public void A_golden_of_another_size_is_not_used_for_this_one()
    {
        var r = Run(new() { [Golden("100", "1152x648")] = "match" }, Only720);
        Assert.Equal("NO GOLDEN", StatusOf(r, Chrome720));
    }

    [Fact]
    public void A_case_with_no_golden_ends_as_NO_GOLDEN_and_does_not_fail_the_run()
    {
        var r = Run(new(), Only720);
        Assert.Equal("NO GOLDEN", StatusOf(r, Chrome720));
        Assert.Equal(0, r.ExitCode);
        Assert.Empty(Directory.GetFiles(r.FixturesUi));   // nothing was written
        var m = Regex.Match(r.Output, @"Render summary: total (\d+), passed (\d+), failed (\d+), no golden (\d+), no result (\d+), skipped (\d+)");
        Assert.True(m.Success, "the summary gives the five counts and the total");
        Assert.Equal("0", m.Groups[2].Value);   // nothing is counted as passed that was not compared
        Assert.Equal("0", m.Groups[3].Value);
        Assert.Equal("1", m.Groups[4].Value);
        Assert.Equal("0", m.Groups[5].Value);
    }

    [Fact]
    public void FailOnNoGolden_makes_a_case_with_no_golden_fail_the_run()
    {
        var r = Run(new(), Only720, "-FailOnNoGolden");
        Assert.Equal("NO GOLDEN", StatusOf(r, Chrome720));
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void The_results_file_carries_the_new_status()
    {
        var r = Run(new(), Only720);
        string xml = File.ReadAllText(Directory.GetFiles(r.Root, "Render*.junit.xml", SearchOption.AllDirectories).First());
        Assert.Contains("nogolden=\"1\"", xml);
        Assert.Contains("name=\"" + Chrome720 + "\"", xml);
        Assert.Contains("<skipped type=\"NO GOLDEN\"", xml);   // the case is written as not run, never as a pass
        Assert.Contains("errors=\"0\"", xml);
    }

    [Fact]
    public void Propose_with_no_folder_is_refused_and_writes_nothing()
    {
        var r = Run(new(), Only720, "-Propose");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("REFUSED", r.Output);
        Assert.Contains("-ProposeDir", r.Output);
        Assert.Empty(r.Launches);
        Assert.Empty(Directory.GetFiles(r.FixturesUi));
    }

    [Theory]
    [InlineData("fixtures/ui")]
    [InlineData("fixtures/ui/../ui")]
    [InlineData("fixtures")]
    [InlineData("tools")]
    [InlineData("")]
    public void Propose_aimed_at_the_committed_goldens_or_anywhere_in_the_repository_is_refused_and_writes_nothing(string inside)
    {
        // @REPO@ stands for the repository of the run (the folder above its tools folder)
        var r = Run(new(), Only720, "-Propose", "-ProposeDir", "@REPO@/" + inside);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("REFUSED", r.Output);
        Assert.Contains("-ProposeDir", r.Output);
        Assert.Empty(r.Launches);
        Assert.Empty(Directory.GetFiles(r.FixturesUi));
    }

    [Fact]
    public void Propose_writes_the_picture_only_into_the_named_folder_outside_the_repository()
    {
        string outside = TestTemp.NewFolder("render-proposed");
        var r = Run(new(), Only720, "-Propose", "-ProposeDir", outside);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("NO GOLDEN", StatusOf(r, Chrome720));
        Assert.True(File.Exists(Path.Combine(outside, Golden("100", "1280x720"))));
        Assert.Empty(Directory.GetFiles(r.FixturesUi));   // nothing reached the committed goldens
        TestTemp.Delete(outside);
    }
}

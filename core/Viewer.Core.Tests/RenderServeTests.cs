// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The batch mode of tools\render.ps1 (tools\render-serve.ps1): the cases that can share a process run in one viewer process per backend,
/// handed over through files; a case that ends the process fails alone and the next case gets a new process; the cases that need a
/// process of their own are never batched; -OneProcessPerCase keeps the old path.
/// The checks run a copy of the tools folder whose godot.ps1 is a stand-in that plays the viewer's side of the protocol (or, called without
/// --tr-serve, a one-case process) and records every launch. The copy lives in a temp folder of its own, so the tests are independent and
/// run in parallel. NV_RENDER_TOOLS_UNDER_TEST names another tools folder to copy (the checks were first run against the earlier one).
/// </summary>
public sealed class RenderServeTests
{
    private readonly ITestOutputHelper _out;
    public RenderServeTests(ITestOutputHelper output) { _out = output; }

    private sealed record Outcome(int ExitCode, string Output, string[] Launches, string OutDir, string Junit);

    private const string StandIn = """
param([string]$Exe, [string]$AppDataDir, [string]$ArgLine, [int]$TimeoutSeconds, [string]$LogFile, [string]$ServeDir, [string]$DiagDir, [switch]$MeasureRun)
$launches = $env:FAKE_LAUNCHES
Add-Content -LiteralPath $launches -Value ("$PID $ArgLine")
$driver = if ($ArgLine -match '--rendering-driver (\S+)') { $Matches[1] } else { "?" }
function Write-Case([string]$out, [string]$tag) {
    $j = @{ backend = @{ driver = $driver }; exit_code = 0; verdict = "PASS"; failures = @(); served_by = $PID } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText((Join-Path $out "$tag.json"), $j)
}
if ($ArgLine -match '--tr-serve="?([^"\s]+)') {
    $dir = $Matches[1]
    $out = if ($ArgLine -match '--tr-out="?([^"\s]+)') { $Matches[1] } else { $dir }
    [System.IO.File]::WriteAllText((Join-Path $dir "ready"), "$PID")
    while ($true) {
        $req = Get-ChildItem -LiteralPath $dir -Filter "*.request" -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc | Select-Object -First 1
        if (-not $req) { Start-Sleep -Milliseconds 20; continue }
        if ($req.Name -eq "quit.request") { exit 0 }
        $lines = Get-Content -LiteralPath $req.FullName
        Move-Item -LiteralPath $req.FullName -Destination ($req.FullName + ".taken") -Force
        $tag = (($lines | Where-Object { $_ -like "--tr-tag=*" }) -replace '^--tr-tag=', '')
        if ($env:FAKE_CRASH_TAG -and $tag -eq $env:FAKE_CRASH_TAG) { exit 9 }   # the process ends in the middle of the case
        Add-Content -LiteralPath (Join-Path $dir "cases.txt") -Value ("$tag " + ($lines -join ' | '))
        Write-Case $out $tag
        [System.IO.File]::WriteAllText((Join-Path $dir "$tag.done"), "0")
    }
}
$out = if ($ArgLine -match '--tr-out="?([^"\s]+)') { $Matches[1] } else { "." }
$tag = if ($ArgLine -match '--tr-tag=(\S+)') { $Matches[1] } else { "render" }
Write-Case $out $tag
exit 0
""";

    private Outcome Run(Dictionary<string, string?>? extraEnv, params string[] scriptArgs)
    {
        string root = TestTemp.NewFolder("render-serve");
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        string source = Environment.GetEnvironmentVariable("NV_RENDER_TOOLS_UNDER_TEST") is { Length: > 0 } other ? other : Path.Combine(TestPaths.RepoRoot, "tools");
        foreach (var f in Directory.GetFiles(source, "*.ps1"))
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
        File.WriteAllText(Path.Combine(tools, "godot.ps1"), StandIn.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
        string temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);
        string outDir = Path.Combine(root, "out");

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools, "render.ps1"), "-OutDir", outDir };
        args.AddRange(scriptArgs);
        var env = new Dictionary<string, string?>
        {
            ["CLAIM_TOKEN"] = null,
            ["NV_CLAIM_CHECK_SCRIPT"] = null,
            ["NV_CHARACTER_DIR"] = characters,
            ["NV_FIXTURES_LOCAL"] = local,
            ["NV_TEMP_ROOT"] = temp,
            ["NV_RENDER_ONE_PER_CASE"] = null,
            ["FAKE_LAUNCHES"] = launches,
            ["FAKE_CRASH_TAG"] = null,
        };
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;
        int code; string output;
        try { (code, output) = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(4), env); }
        catch (TimeoutException e) { code = -1; output = e.Message; }
        string[] calls = File.Exists(launches) ? File.ReadAllLines(launches) : [];
        string junitPath = Path.Combine(outDir, "Render.junit.xml");
        string junit = File.Exists(junitPath) ? File.ReadAllText(junitPath) : "";
        _out.WriteLine($"render.ps1 {string.Join(' ', scriptArgs)}: exit {code}, {calls.Length} launches\n{output}");
        return new Outcome(code, output, calls, outDir, junit);
    }

    private static bool IsServe(string launch) => launch.Contains("--tr-serve=", StringComparison.Ordinal);

    /// <summary>The process that produced each case's sidecar, by tag.</summary>
    private static Dictionary<string, long> ServedBy(string outDir) =>
        !Directory.Exists(outDir) ? new() : Directory.GetFiles(outDir, "*.json").Where(f => !f.EndsWith(".junit.json")).ToDictionary(
            f => Path.GetFileNameWithoutExtension(f),
            f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("served_by").GetInt64());

    [Fact]
    public void A_section_runs_in_one_process_per_backend()
    {
        var r = Run(null, "-EditLookOnly");
        Assert.Equal(0, r.ExitCode);
        var served = r.Launches.Where(IsServe).ToArray();
        Assert.Equal(2, served.Length);   // vulkan and d3d12, started once each
        Assert.Contains(served, l => l.Contains("--rendering-driver vulkan"));
        Assert.Contains(served, l => l.Contains("--rendering-driver d3d12"));
        Assert.Equal(r.Launches.Length, served.Length);   // and nothing was started per case
        var by = ServedBy(r.OutDir);
        Assert.True(by.Count >= 3, $"expected the section's cases to leave sidecars, got {by.Count}");
        var vulkanProcesses = by.Where(kv => kv.Key.EndsWith("-vulkan")).Select(kv => kv.Value).Distinct().ToArray();
        var d3d12Processes = by.Where(kv => kv.Key.EndsWith("-d3d12")).Select(kv => kv.Value).Distinct().ToArray();
        Assert.Single(vulkanProcesses);
        Assert.Single(d3d12Processes);
        Assert.NotEqual(vulkanProcesses[0], d3d12Processes[0]);
        Assert.Contains("Render batch:", r.Output);
    }

    [Fact]
    public void A_served_case_gets_the_arguments_a_started_process_would_get()
    {
        var r = Run(null, "-EditLookOnly", "-Backends", "vulkan");
        var files = Directory.Exists(r.OutDir) ? Directory.GetFiles(r.OutDir, "cases.txt", SearchOption.AllDirectories) : [];
        Assert.Single(files);
        string text = File.ReadAllText(files[0]);
        Assert.Contains("--test-render", text);
        Assert.Contains("--tr-tag=proposal-avatars-edit-height-vulkan", text);
        Assert.Contains("--tr-out=" + r.OutDir, text);
        Assert.Contains("--tr-avatars=editlook", text);
        Assert.Contains("--tr-character=", text);
    }

    [Fact]
    public void OneProcessPerCase_keeps_the_old_path_with_a_process_for_every_case()
    {
        var r = Run(null, "-EditLookOnly", "-Backends", "vulkan", "-OneProcessPerCase");
        Assert.Equal(0, r.ExitCode);
        Assert.NotEmpty(r.Launches);
        Assert.DoesNotContain(r.Launches, IsServe);
        var by = ServedBy(r.OutDir);
        Assert.Equal(by.Count, r.Launches.Length);
    }

    [Fact]
    public void The_environment_variable_keeps_the_old_path_too()
    {
        var r = Run(new() { ["NV_RENDER_ONE_PER_CASE"] = "1" }, "-EditLookOnly", "-Backends", "vulkan");
        Assert.NotEmpty(r.Launches);
        Assert.DoesNotContain(r.Launches, IsServe);
    }

    [Fact]
    public void A_case_that_ends_the_process_fails_alone_and_the_next_case_gets_a_new_process()
    {
        var r = Run(new() { ["FAKE_CRASH_TAG"] = "proposal-avatars-edit-height-vulkan" }, "-EditLookOnly", "-Backends", "vulkan");
        var served = r.Launches.Where(IsServe).ToArray();
        Assert.Equal(2, served.Length);   // the process the crash ended, and the one the next case started
        var by = ServedBy(r.OutDir);
        Assert.False(by.ContainsKey("proposal-avatars-edit-height-vulkan"), "the case that ended its process left a sidecar");
        Assert.True(by.ContainsKey("avatars-edit-height-negative-vulkan"), "the case after the crash did not run");   // served by the process the launcher started for it
        Assert.Matches(@"FAIL\s+Render\.avatars_edit_look_vulkan", r.Output);   // the case that ended its process is reported as failed
    }

    [Theory]
    [InlineData("vulkan", "zoo-vulkan", "\"--tr-fixture=C:\\a b\\zoo.json\" \"--tr-golden=C:\\a b\\g.png\"", true)]
    [InlineData("d3d12", "avatars-vulkan", "--tr-avatars=render --tr-alter=still", true)]
    [InlineData("vulkan", "avatars-rigged-stall-vulkan", "--tr-avatars=riggedstall", false)]
    [InlineData("vulkan", "avatars-rigged-stress-vulkan", "--tr-avatars=riggedstress --tr-cycles=20", false)]
    [InlineData("vulkan", "crowd-smoke-vulkan", "--tr-avatars=crowd:25 --tr-crowd-seconds=1", false)]
    [InlineData("vulkan", "arrival-10-vulkan", "--tr-avatars=arrive:10 --tr-crowd-seconds=1", false)]
    [InlineData("vulkan", "lifecycle-server-vulkan", "--tr-fixture=synthetic-256 --tr-lifecycle=20", false)]
    [InlineData("vulkan", "warmup-vulkan", "--tr-warmup", false)]
    [InlineData("vulkan", "pipeline-gate-crowd-vulkan", "--tr-avatars=crowd:25 --tr-compile-gate", false)]
    [InlineData("vulkan", "path-vulkan", "--tr-fixture=x --tr-path-seconds=10", false)]
    [InlineData("vulkan", "mat-fallback-vulkan", "--tr-fixture=x --tr-hold-texture=00000000-0000-0000-0000-000000000000", false)]
    [InlineData("opengl3", "refuse-opengl3", "--tr-fixture=x", false)]
    [InlineData("vulkan", "sky-midday-vulkan", "--tr-fixture=x --tr-timeofday=midday", true)]
    [InlineData("vulkan", "avatars-rigged-vulkan", "--tr-avatars=rigged \"--tr-golden=C:\\g.png\"", false)]
    [InlineData("d3d12", "avatars-rigged-blend-d3d12", "--tr-avatars=riggedblend", false)]
    [InlineData("vulkan", "avatars-shape-look-vulkan", "--tr-avatars=shapelook", false)]
    [InlineData("vulkan", "avatars-rigged-textures-vulkan", "--tr-avatars=riggedtex \"--tr-textures=C:\\t\"", true)]
    [InlineData("vulkan", "avatars-live-vulkan", "--tr-avatars=live", true)]
    public void Which_cases_share_a_process(string driver, string tag, string extra, bool served)
    {
        var r = RunFunction($"$d = Test-CaseServable '{driver}' '{tag}' '{extra.Replace("'", "''")}' $false $false; Write-Output ('SERVE=' + $d.Serve)");
        Assert.Contains("SERVE=" + served, r);
    }

    [Fact]
    public void A_measuring_run_and_a_run_expected_to_be_stopped_are_never_batched()
    {
        Assert.Contains("SERVE=False", RunFunction("$d = Test-CaseServable 'vulkan' 'zoo-vulkan' '--tr-fixture=x' $true $false; Write-Output ('SERVE=' + $d.Serve)"));
        Assert.Contains("SERVE=False", RunFunction("$d = Test-CaseServable 'vulkan' 'zoo-vulkan' '--tr-fixture=x' $false $true; Write-Output ('SERVE=' + $d.Serve)"));
    }

    [Fact]
    public void The_arguments_are_split_as_the_system_splits_a_command_line()
    {
        string r = RunFunction("$a = ConvertTo-CaseArgs '\"--tr-fixture=C:\\a b\\zoo.json\" --tr-camera=near \"--tr-golden=C:\\x\\g.png\"'; Write-Output ('COUNT=' + $a.Count); $a | ForEach-Object { Write-Output ('ARG=' + $_) }");
        Assert.Contains("COUNT=3", r);
        Assert.Contains("ARG=--tr-fixture=C:\\a b\\zoo.json", r);
        Assert.Contains("ARG=--tr-camera=near", r);
        Assert.Contains("ARG=--tr-golden=C:\\x\\g.png", r);
    }

    [Fact]
    public void SkipTiming_and_OnlyTiming_leave_out_the_other_kind_with_a_reason()
    {
        string r = RunFunction("Write-Output ('A=' + (Test-TimingCase 'avatars-rigged-stall-d3d12')); Write-Output ('B=' + (Test-TimingCase 'avatars-live-d3d12')); Write-Output ('C=' + (Test-TimingCase 'avatars-rigged-vulkan')); Write-Output ('D=' + (Test-TimingCase 'avatars-shape-look-negative-vulkan')); Write-Output ('E=' + (Test-TimingCase 'avatars-rigged-textures-vulkan'))");
        Assert.Contains("A=True", r);
        Assert.Contains("B=False", r);
        Assert.Contains("C=True", r);   // the rigged-avatar and shape-look pictures vary with timing
        Assert.Contains("D=True", r);
        Assert.Contains("E=False", r);
    }

    [Fact]
    public void The_merge_update_case_whose_limit_is_a_wall_clock_slice_is_a_timing_case()
    {
        string r = RunFunction("Write-Output ('A=' + (Test-TimingCase 'merge-update-merged-vulkan')); Write-Output ('B=' + (Test-TimingCase 'merge-update-unmerged-vulkan')); Write-Output ('C=' + (Test-TimingCase 'merge-picture-merged-vulkan'))");
        Assert.Contains("A=True", r);   // its pass needs the main thread's longest merge call within the 4 ms slice
        Assert.Contains("B=True", r);   // its control render is part of the same case
        Assert.Contains("C=False", r);  // the picture comparison has no time limit
    }

    private string RunFunction(string body)
    {
        string root = TestTemp.NewFolder("render-serve-fn");
        string source = Environment.GetEnvironmentVariable("NV_RENDER_TOOLS_UNDER_TEST") is { Length: > 0 } other ? other : Path.Combine(TestPaths.RepoRoot, "tools");
        string script = Path.Combine(root, "fn.ps1");
        File.WriteAllText(script, ". '" + Path.Combine(source, "render-serve.ps1").Replace("'", "''") + "'\r\n" + body + "\r\n");
        var (code, output) = ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script], TimeSpan.FromMinutes(1));
        _out.WriteLine(output);
        return output;
    }
}

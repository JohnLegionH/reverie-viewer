// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\crash-trim.ps1 runs at the end of a render run. A crash dump is large (the whole process), its note is a few lines; a run in which
/// every case passed keeps each crash's note (.txt) and puts the dump (.dmp) aside, a run with a failing case keeps both. Nothing is deleted:
/// the dumps go through a move-aside tool. These tests run the script on small folders with a stand-in for that tool.
/// tools\render.ps1 must call it only for a run that is not one backend of a parallel run, and say all cases passed only for exit code 0.
/// </summary>
public sealed class CrashTrimScriptTests
{
    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "crash-trim.ps1");

    private sealed class Rig
    {
        public string Root = TestTemp.NewFolder("crash-trim");
        public string Crash => Path.Combine(Root, "crash");
        public string Aside => Path.Combine(Root, "aside");
        public string Mover => Path.Combine(Root, "mover.ps1");
        public DateTime RunStart = DateTime.Now.AddMinutes(-5);

        public Rig(bool createCrashFolder = true)
        {
            if (createCrashFolder) Directory.CreateDirectory(Crash);
            File.WriteAllText(Mover, """
                param([string]$Label, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Paths)
                foreach ($p in $Paths) {
                    $rel = $p.Substring(3)
                    $to = Join-Path $env:ASIDE_ROOT $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
                    Move-Item -LiteralPath $p -Destination $to
                }
                """);
        }

        public string Add(string name, bool beforeTheRun = false)
        {
            string p = Path.Combine(Crash, name);
            File.WriteAllText(p, "content of " + name);
            File.SetLastWriteTime(p, beforeTheRun ? RunStart.AddHours(-1) : RunStart.AddMinutes(1));
            return p;
        }

        public (int ExitCode, string Output) Trim(bool allPassed, bool withTool = true)
        {
            var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-CrashDir", Crash, "-Since", RunStart.ToString("o") };
            if (allPassed) args.Add("-AllPassed");
            if (withTool) { args.Add("-MoveAsideTool"); args.Add(Mover); }
            return ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), new Dictionary<string, string?> { ["ASIDE_ROOT"] = Aside, ["NV_MOVE_ASIDE_TOOL"] = null });
        }
    }

    [Fact]
    public void A_run_where_every_case_passed_keeps_each_crashs_note_and_puts_its_dump_aside()
    {
        var r = new Rig();
        string note1 = r.Add("crash-101.txt"), dump1 = r.Add("crash-101.dmp"), note2 = r.Add("crash-102.txt"), dump2 = r.Add("crash-102.dmp"), dotnet = r.Add("dotnet-103.dmp");
        var run = r.Trim(allPassed: true);
        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(note1) && File.Exists(note2), run.Output);
        Assert.False(File.Exists(dump1) || File.Exists(dump2) || File.Exists(dotnet), run.Output);
        var moved = Directory.GetFiles(r.Aside, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "crash-101.dmp", "crash-102.dmp", "dotnet-103.dmp" }, moved);
    }

    [Fact]
    public void A_run_with_a_failing_case_keeps_every_note_and_every_dump()
    {
        var r = new Rig();
        string note = r.Add("crash-101.txt"), dump = r.Add("crash-101.dmp"), dotnet = r.Add("dotnet-103.dmp");
        var run = r.Trim(allPassed: false);
        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(note) && File.Exists(dump) && File.Exists(dotnet), run.Output);
        Assert.False(Directory.Exists(r.Aside));
    }

    [Fact]
    public void A_dump_from_before_the_run_is_left_alone_even_when_every_case_passed()
    {
        var r = new Rig();
        string old = r.Add("crash-7.dmp", beforeTheRun: true), fresh = r.Add("crash-8.dmp");
        var run = r.Trim(allPassed: true);
        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(old), run.Output);
        Assert.False(File.Exists(fresh), run.Output);
    }

    [Fact]
    public void Without_a_move_aside_tool_nothing_is_deleted_and_the_output_says_so()
    {
        var r = new Rig();
        string dump = r.Add("crash-101.dmp");
        var run = r.Trim(allPassed: true, withTool: false);
        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(dump), run.Output);
        Assert.Contains("no move-aside tool", run.Output);
    }

    [Fact]
    public void A_missing_crash_folder_is_not_an_error()
    {
        var r = new Rig(createCrashFolder: false);
        var run = r.Trim(allPassed: true);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void The_render_script_trims_only_after_a_run_that_is_not_one_backend_of_a_parallel_run_and_says_passed_only_for_exit_code_0()
    {
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "render.ps1"));
        Assert.Contains("crash-trim.ps1", text);
        // the single-process path and the parallel parent both call it; a child of a parallel run (NV_RENDER_JUNIT_NAME set) does not
        Assert.Contains("function Invoke-CrashTrim", text);
        Assert.Contains("-not $env:NV_RENDER_JUNIT_NAME", text);
        Assert.Matches(@"Invoke-CrashTrim\s+\(\$final -eq 0 -and", text);
        Assert.Matches(@"Invoke-CrashTrim\s+\(\$code -eq 0 -and", text);
    }
}

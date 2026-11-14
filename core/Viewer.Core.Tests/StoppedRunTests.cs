// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A stopped run stops its whole process tree (tools\job-guard.ps1, used by the entry scripts), and no script under tools\ takes
/// a claim in the lane log. The scripts and the job object are Windows facilities, so the process tests return at once elsewhere.
/// </summary>
public class StoppedRunTests : IDisposable
{
    private readonly string _dir = TestTemp.NewFolder("stopped-run");
    private readonly List<int> _pids = new();

    public void Dispose()
    {
        // a test never leaves a process behind, whatever it found: the processes it recorded are ended
        foreach (int pid in _pids)
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        try { TestTemp.Delete(_dir); } catch (IOException) { }
    }

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string Tool(string name) => Path.Combine(TestPaths.RepoRoot, "tools", name);

    // The stand-in for the viewer: starts a helper of its own (a grandchild of godot.ps1) and records both process ids, then waits.
    private string WriteProgram()
    {
        string path = Path.Combine(_dir, "program.ps1");
        File.WriteAllText(path, string.Join("\n",
            "param([string]$PidFile)",
            "$psi = New-Object System.Diagnostics.ProcessStartInfo",
            "$psi.FileName = (Get-Command powershell.exe).Source",
            "$psi.Arguments = '-NoProfile -Command Start-Sleep -Seconds 600'",
            "$psi.UseShellExecute = $false",
            "$helper = [System.Diagnostics.Process]::Start($psi)",
            "Set-Content -LiteralPath $PidFile -Value \"$PID $($helper.Id)\"",
            "Start-Sleep -Seconds 600"));
        return path;
    }

    private Task<string>? _output;

    private Process StartGodotScript(string program, string pidFile, int timeoutSeconds, IReadOnlyDictionary<string, string?>? environment = null, params string[] extraArguments)
    {
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Tool("godot.ps1"), "-Exe", WindowsPowerShell,
                     "-AppDataDir", Path.Combine(_dir, "appdata"), "-DiagDir", Path.Combine(_dir, "diag"), "-TimeoutSeconds", timeoutSeconds.ToString(),
                     "-ArgLine", $"-NoProfile -ExecutionPolicy Bypass -File \"{program}\" \"{pidFile}\"" }.Concat(extraArguments))
            psi.ArgumentList.Add(a);
        // these tests are about stopping a run, not about claims: the claim guard is off for the script they start
        psi.Environment.Remove("NV_CLAIM_CHECK_SCRIPT");
        foreach (var (k, v) in environment ?? new Dictionary<string, string?>())
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        var p = Process.Start(psi)!;
        _output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        _pids.Add(p.Id);
        return p;
    }

    // A stand-in for dotnet-dump: it records that it was asked for a dump and writes none (the run logs "no dump" for it).
    private string WriteDumpTool(string markerFile)
    {
        string path = Path.Combine(_dir, "dump-tool.cmd");
        File.WriteAllText(path, $"@echo asked> \"{markerFile}\"\r\n@exit /b 0\r\n");
        return path;
    }

    private int[] WaitForProgramPids(string pidFile)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    var ids = File.ReadAllText(pidFile).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    if (ids.Length == 2) { _pids.AddRange(ids); return ids; }
                }
                catch (IOException) { }
                catch (FormatException) { }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("the stand-in program never recorded its process ids");
    }

    private static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static bool AllGone(IEnumerable<int> pids, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            if (!pids.Any(Running)) return true;
            Thread.Sleep(100);
        }
        return !pids.Any(Running);
    }

    [Fact]
    public void Stopping_only_the_script_ends_the_program_and_its_helper_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pidFile = Path.Combine(_dir, "pids.txt");
        using var script = StartGodotScript(WriteProgram(), pidFile, timeoutSeconds: 600);
        int[] program = WaitForProgramPids(pidFile);
        Assert.All(program, pid => Assert.True(Running(pid)));

        script.Kill(entireProcessTree: false);   // the one process named, as a stop of a wrapper does

        Assert.True(AllGone(program, TimeSpan.FromSeconds(60)), "the program or its helper was still running 60 s after the script was stopped");
    }

    [Fact]
    public void A_run_that_overruns_its_timeout_is_ended_with_everything_it_started()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pidFile = Path.Combine(_dir, "pids.txt");
        using var script = StartGodotScript(WriteProgram(), pidFile, timeoutSeconds: 6);
        int[] program = WaitForProgramPids(pidFile);

        Assert.True(script.WaitForExit(TimeSpan.FromSeconds(90)), "godot.ps1 did not end after its timeout");
        Assert.Equal(124, script.ExitCode);
        Assert.True(AllGone(program, TimeSpan.FromSeconds(60)), "the program or its helper was still running after the timeout");
    }

    [Fact]
    public void A_run_that_is_expected_to_end_by_timeout_takes_no_dump()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pidFile = Path.Combine(_dir, "pids.txt");
        string marker = Path.Combine(_dir, "dump-asked.txt");
        using var script = StartGodotScript(WriteProgram(), pidFile, timeoutSeconds: 6, null, "-DotnetDump", WriteDumpTool(marker), "-ExpectTimeout");

        Assert.True(script.WaitForExit(TimeSpan.FromSeconds(90)), "godot.ps1 did not end after its timeout");
        Assert.Equal(124, script.ExitCode);
        int[] program = WaitForProgramPids(pidFile);
        Assert.True(AllGone(program, TimeSpan.FromSeconds(60)), "the program or its helper was still running after the timeout");
        Assert.Contains("expected to end by timeout", _output!.Result);
        Assert.False(File.Exists(marker), "a dump was asked for although the run was expected to end by timeout");
    }

    [Fact]
    public void A_run_that_times_out_unexpectedly_still_takes_the_dump()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pidFile = Path.Combine(_dir, "pids.txt");
        string marker = Path.Combine(_dir, "dump-asked.txt");
        using var script = StartGodotScript(WriteProgram(), pidFile, timeoutSeconds: 6, null, "-DotnetDump", WriteDumpTool(marker));
        int[] program = WaitForProgramPids(pidFile);

        Assert.True(script.WaitForExit(TimeSpan.FromSeconds(90)), "godot.ps1 did not end after its timeout");
        Assert.Equal(124, script.ExitCode);
        Assert.True(AllGone(program, TimeSpan.FromSeconds(60)), "the program or its helper was still running after the timeout");
        Assert.True(File.Exists(marker), "no dump was asked for although the timeout was unexpected");
    }

    [Fact]
    public void The_automation_check_that_ends_by_timeout_says_so_to_godot_ps1()
    {
        // off_by_default starts the viewer without --automation and lets the run time out, so its start asks for no dump
        string text = File.ReadAllText(Tool("automation.ps1"));
        var start = Regex.Match(text, @"\$v = Start-Viewer ""[^""]*"" \d+[^\r\n]*\r?\n\s*Start-Sleep -Seconds 9");
        Assert.True(start.Success, "the off_by_default start was not found in automation.ps1");
        Assert.Contains("-ExpectTimeout", start.Value);
    }

    [Fact]
    public void Without_the_guard_stopping_only_the_script_leaves_the_program_running()
    {
        // the negative control: it proves the first test can fail, so its pass means the guard did the work
        if (!OperatingSystem.IsWindows()) return;
        string pidFile = Path.Combine(_dir, "pids.txt");
        using var script = StartGodotScript(WriteProgram(), pidFile, timeoutSeconds: 600, new Dictionary<string, string?> { ["NV_NO_JOB_GUARD"] = "1" });
        int[] program = WaitForProgramPids(pidFile);

        script.Kill(entireProcessTree: false);

        Assert.False(AllGone(program, TimeSpan.FromSeconds(5)), "the program ended with the script although the guard was off");
    }

    [Fact]
    public void No_script_under_tools_takes_a_claim()
    {
        // a claim belongs to whoever starts the whole run; a script it starts must not take one of its own
        var scripts = Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, "tools"), "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(scripts);
        // Only tools\claim-guard.ps1 runs the claim check script, and only to check a claim the caller holds (read only); no script takes,
        // renews or releases one.
        var claiming = scripts.Where(f => Regex.IsMatch(File.ReadAllText(f), @"NV_CLAIM_CHECK_SCRIPT", RegexOptions.IgnoreCase))
            .Select(Path.GetFileName).Where(n => !string.Equals(n, "claim-guard.ps1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(claiming);
        string guard = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "claim-guard.ps1"));
        Assert.DoesNotMatch(@"(?i)""\$NV_CLAIM_CHECK_UNIX""\s+(acquire|release|renew|force-release)", guard);
        Assert.Contains("check \"$CLAIM_TOKEN\"", guard);
    }
}

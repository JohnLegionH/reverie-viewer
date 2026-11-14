// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every program tools\godot.ps1 runs leaves one line in run-exits.tsv in its diagnostics folder: when it ended, its exit
/// code, how long it ran, whether crash-watch was on and which crash files it left. A report or a later session reads the
/// exit code from there whether or not crash capture was on. The script is a Windows facility, so the tests return at once elsewhere.
/// </summary>
public class RunExitRecordTests : IDisposable
{
    private readonly string _dir = TestTemp.NewFolder("run-exits");

    public void Dispose()
    {
        try { TestTemp.Delete(_dir); } catch (IOException) { }
    }

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private int RunGodotScript(string programArguments, string crashDumps, int timeoutSeconds = 120)
    {
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestPaths.RepoRoot, "tools", "godot.ps1"), "-Exe", WindowsPowerShell,
                     "-AppDataDir", Path.Combine(_dir, "appdata"), "-DiagDir", Path.Combine(_dir, "diag"), "-TimeoutSeconds", timeoutSeconds.ToString(),
                     "-ArgLine", programArguments })
            psi.ArgumentList.Add(a);
        psi.Environment["NV_CRASH_DUMPS"] = crashDumps;
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(180)), "godot.ps1 did not end");
        return p.ExitCode;
    }

    private string[][] Rows()
    {
        string file = Path.Combine(_dir, "diag", "run-exits.tsv");
        Assert.True(File.Exists(file), "no run-exits.tsv in the diagnostics folder");
        return File.ReadAllLines(file).Select(l => l.Split('\t')).ToArray();
    }

    [Fact]
    public void A_run_with_crash_capture_off_records_its_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(7, RunGodotScript("-NoProfile -Command exit 7", crashDumps: "0"));
        var rows = Rows();
        Assert.Equal(new[] { "utc", "exit", "seconds", "pid", "crash_watch", "crash_files", "args" }, rows[0]);
        Assert.Single(rows.Skip(1));
        Assert.Equal("7", rows[1][1]);
        Assert.Equal("off", rows[1][4]);
    }

    [Fact]
    public void A_run_with_crash_capture_on_records_its_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(7, RunGodotScript("-NoProfile -Command exit 7", crashDumps: "1"));
        var rows = Rows();
        Assert.Single(rows.Skip(1));
        Assert.Equal("7", rows[1][1]);
        Assert.Equal("on", rows[1][4]);
    }

    [Fact]
    public void A_run_stopped_by_its_timeout_records_exit_124()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(124, RunGodotScript("-NoProfile -Command Start-Sleep -Seconds 600", crashDumps: "0", timeoutSeconds: 4));
        var rows = Rows();
        Assert.Single(rows.Skip(1));
        Assert.Equal("124", rows[1][1]);
    }

    [Fact]
    public void Runs_add_lines_and_keep_the_earlier_ones()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunGodotScript("-NoProfile -Command exit 3", crashDumps: "0");
        RunGodotScript("-NoProfile -Command exit 0", crashDumps: "0");
        var rows = Rows();
        Assert.Equal(new[] { "3", "0" }, rows.Skip(1).Select(r => r[1]).ToArray());
    }
}

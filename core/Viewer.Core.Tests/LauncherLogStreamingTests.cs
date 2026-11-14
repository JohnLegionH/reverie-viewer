// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A measuring run of tools\godot.ps1 with a log file writes the program's output to that file as it arrives, and keeps only the last part in
/// memory. A long run prints hundreds of thousands of engine lines; holding them all in the launcher's memory until the run ends made the
/// launcher process itself grow without bound. The script is a Windows facility, so the tests return at once elsewhere.
/// </summary>
public class LauncherLogStreamingTests : IDisposable
{
    private readonly string _dir = TestTemp.NewFolder("launcher-stream");

    public void Dispose()
    {
        try { TestTemp.Delete(_dir); } catch (IOException) { }
    }

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private Process StartLauncher(string programArguments, string logFile)
    {
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestPaths.RepoRoot, "tools", "godot.ps1"), "-Exe", WindowsPowerShell,
                     "-AppDataDir", Path.Combine(_dir, "appdata"), "-DiagDir", Path.Combine(_dir, "diag"), "-TimeoutSeconds", "900", "-MeasureRun",
                     "-LogFile", logFile, "-ArgLine", programArguments })
            psi.ArgumentList.Add(a);
        psi.Environment["NV_CRASH_DUMPS"] = "0";
        return Process.Start(psi)!;
    }

    [Fact]
    public void A_measuring_run_writes_the_log_while_the_program_still_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        string log = Path.Combine(_dir, "run.log");
        using var p = StartLauncher("-NoProfile -Command [Console]::Out.WriteLine('first-line-marker'); [Console]::Out.Flush(); Start-Sleep -Seconds 300", log);   // ended by the test as soon as the line is seen
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        bool seen = false;
        var deadline = DateTime.UtcNow.AddSeconds(240);
        while (DateTime.UtcNow < deadline && !p.HasExited)
        {
            try
            {
                if (File.Exists(log))
                {
                    using var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // the launcher still has the file open for writing
                    using var r = new StreamReader(s);
                    seen = r.ReadToEnd().Contains("first-line-marker");
                }
            }
            catch (IOException) { }
            if (seen) break;
            Thread.Sleep(250);
        }
        bool stillRunning = !p.HasExited;
        if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        Assert.True(stillRunning, "the launcher ended before the check");
        Assert.True(seen, "the program's first line was not in the log file while the program was still running");
    }

    [Fact]
    public void The_log_file_holds_every_line_of_a_long_run_and_the_launcher_prints_the_last_part()
    {
        if (!OperatingSystem.IsWindows()) return;
        string log = Path.Combine(_dir, "long.log");
        using var p = StartLauncher("-NoProfile -Command 1..60000 | ForEach-Object { 'engine line ' + $_ + ' ' + ('x' * 60) }", log);
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(1200)), "godot.ps1 did not end");
        string text = File.ReadAllText(log);
        Assert.Contains("engine line 1 ", text);
        Assert.Contains("engine line 30000 ", text);
        Assert.Contains("engine line 60000 ", text);
        Assert.Contains("engine line 60000 ", stdout.Result);
        Assert.Contains("GODOT: exit=0", stdout.Result);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Tools\temp-root.ps1. With NV_TEMP_ROOT set, test.ps1 and render.ps1 point
/// TEMP and TMP at a folder under it before they start anything, so every process they start (dotnet, its workload logs,
/// the test host's temp paths, Godot) writes its temporary files there; unset, TEMP and TMP are left as they are.
/// Run through Windows PowerShell with a real child process, the way the scripts use it.
/// </summary>
public sealed class TempRootTests
{
    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "temp-root.ps1");

    /// <summary>Dot-sources the helper, calls Use-TempRoot, then a CHILD process prints the TEMP and TMP it inherited.</summary>
    private static (string Temp, string Tmp, string Output) Run(string? root)
    {
        string command = $". '{Script}'; Use-TempRoot 'test' | Out-Null; " +
                         "& powershell -NoProfile -NonInteractive -Command '$env:TEMP; $env:TMP'";   // what the child inherited (Windows' temp path is TMP, then TEMP)
        var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        if (root is null) psi.Environment.Remove("NV_TEMP_ROOT");
        else psi.Environment["NV_TEMP_ROOT"] = root;
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), "powershell did not exit within 60 s");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(p.ExitCode == 0 && lines.Length >= 2, $"exit {p.ExitCode}: {output}");
        return (lines[^2], lines[^1], output);
    }

    [WindowsFact]
    public void With_NV_TEMP_ROOT_set_every_child_process_gets_TEMP_and_TMP_under_it()
    {
        string root = TestTemp.NewFolder("temproot");
        try
        {
            var (temp, tmp, output) = Run(root);
            string want = Path.Combine(root, "test");
            Assert.True(Directory.Exists(want), output);
            Assert.Equal(Path.GetFullPath(want).TrimEnd('\\'), Path.GetFullPath(temp).TrimEnd('\\'));
            Assert.Equal(Path.GetFullPath(want).TrimEnd('\\'), Path.GetFullPath(tmp).TrimEnd('\\'));
        }
        finally { TestTemp.Delete(root); }
    }

    [WindowsFact]
    public void With_NV_TEMP_ROOT_unset_TEMP_and_TMP_are_unchanged()
    {
        var (temp, tmp, _) = Run(null);
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP")!).TrimEnd('\\'), Path.GetFullPath(temp).TrimEnd('\\'));
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("TMP")!).TrimEnd('\\'), Path.GetFullPath(tmp).TrimEnd('\\'));
    }

    [Theory]
    [InlineData("test.ps1")]
    [InlineData("render.ps1")]
    public void The_suite_scripts_set_the_temp_root_before_they_start_anything(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(TestPaths.RepoRoot, "tools", name));
        int use = Array.FindIndex(lines, l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal) && l.Contains("Use-TempRoot ", StringComparison.Ordinal));
        int dot = Array.FindIndex(lines, l => l.Contains("temp-root.ps1", StringComparison.Ordinal) && l.TrimStart().StartsWith(". ", StringComparison.Ordinal));
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("& ", StringComparison.Ordinal) || l.Contains("= & ", StringComparison.Ordinal));
        Assert.True(dot >= 0, $"{name} does not dot-source tools\\temp-root.ps1");
        Assert.True(use > dot, $"{name} does not call Use-TempRoot after loading it");
        Assert.True(start < 0 || use < start, $"{name} starts a process (line {start + 1}) before Use-TempRoot (line {use + 1})");
    }
}

/// <summary>A [Fact] that is a real SKIP, with its reason, off Windows (the scripts run under Windows PowerShell).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "tools\\temp-root.ps1 runs under Windows PowerShell; not available on this OS";
    }
}

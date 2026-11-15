// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\window-record.ps1 watches, beside the checks, that no window a check opens is on a monitor or in front. It opens no
/// window of its own and starts no program. A Windows facility: the tests return at once elsewhere.
/// </summary>
public sealed class WindowRecordTests
{
    private static (int Code, string Output, string Tsv) Run(params string[] scriptArgs)
    {
        string dir = TestTemp.NewFolder("window-record");
        string tsv = Path.Combine(dir, "windows.tsv");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-File", Path.Combine(TestPaths.RepoRoot, "tools", "window-record.ps1"), "-Out", tsv };
        args.AddRange(scriptArgs);
        var (code, output) = ChildProcess.Run("powershell.exe", args, TimeSpan.FromSeconds(90), new Dictionary<string, string?>());
        return (code, output, File.Exists(tsv) ? File.ReadAllText(tsv) : "");
    }

    [Fact]
    public void With_no_run_program_up_every_sample_is_clean_and_it_passes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string nowhere = TestTemp.NewFolder("window-record-nothing-runs-here");
        var r = Run("-PathPrefix", nowhere, "-Seconds", "2", "-IntervalMs", "100");
        Assert.Equal(0, r.Code);
        Assert.Matches(@"window-record: \d+ samples, 0 with a run window up, 0 with a run window on a monitor or in front", r.Output);
        string[] rows = r.Tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("utc\tfg_pid\tfg_name\tfg_is_run\trun_on_monitor\trun_windows", rows[0].TrimStart('﻿'));
        Assert.True(rows.Length >= 3, "it sampled more than once in two seconds");
        Assert.All(rows.Skip(1), row => Assert.Equal("False\tFalse", string.Join("\t", row.Split('\t')[3..5])));
    }

    [Fact]
    public void Bad_arguments_are_refused_rather_than_read_as_a_pass()
    {
        if (!OperatingSystem.IsWindows()) return;
        var r = Run("-PathPrefix", TestTemp.NewFolder("window-record-bad"), "-Seconds", "0");
        Assert.Equal(2, r.Code);
    }

    [Fact]
    public void It_starts_no_program_and_opens_no_window()
    {
        string script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "window-record.ps1"));
        Assert.DoesNotMatch(@"Start-Process|Process\]::Start|godot\.ps1|System\.Windows\.Forms|SetForegroundWindow|ShowWindow", script);
    }
}

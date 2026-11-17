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
    private static string ScriptPath => Path.Combine(TestPaths.RepoRoot, "tools", "window-record.ps1");

    private static (int Code, string Output, string Tsv) Run(params string[] scriptArgs)
    {
        string dir = TestTemp.NewFolder("window-record");
        string tsv = Path.Combine(dir, "windows.tsv");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-File", ScriptPath, "-Out", tsv };
        args.AddRange(scriptArgs);
        var (code, output) = ChildProcess.Run("powershell.exe", args, TimeSpan.FromSeconds(90), new Dictionary<string, string?>());
        return (code, output, File.Exists(tsv) ? File.ReadAllText(tsv) : "");
    }

    [Fact]
    public void With_no_run_program_up_every_sample_is_clean_and_it_passes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string nowhere = TestTemp.NewFolder("window-record-nothing-runs-here");
        // it stops after three samples, however long a busy machine takes to give them (the time limit is only a cap)
        var r = Run("-PathPrefix", nowhere, "-Samples", "3", "-Seconds", "30", "-IntervalMs", "100");
        Assert.Equal(0, r.Code);
        Assert.Matches(@"window-record: 3 samples, 0 with a run window up, 0 with a run window on a monitor or in front, \d+ taken while Windows was locked", r.Output);
        string[] rows = r.Tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("utc\tfg_pid\tfg_name\tfg_is_run\trun_on_monitor\trun_windows\tlocked", rows[0].TrimStart('﻿').TrimEnd('\r'));
        Assert.Equal(4, rows.Length);
        Assert.All(rows.Skip(1), row => Assert.Equal("False\tFalse", string.Join("\t", row.Split('\t')[3..5])));
        Assert.All(rows.Skip(1), row => Assert.Matches("^(True|False)$", row.TrimEnd('\r').Split('\t')[6]));
    }

    /// <summary>A recording in the form window-record.ps1 wrote before it marked the lock: the lock screen is known by the program in
    /// front (LockApp, the Windows lock screen).</summary>
    private static string Recording(params string[] rows)
    {
        string file = Path.Combine(TestTemp.NewFolder("window-record-replay"), "windows.tsv");
        File.WriteAllText(file, "utc\tfg_pid\tfg_name\tfg_is_run\trun_on_monitor\trun_windows\n" + string.Join("\n", rows) + "\n");
        return file;
    }

    private static (int Code, string Output) Replay(string file) =>
        ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-File", ScriptPath, "-Out", file, "-Replay"], TimeSpan.FromSeconds(90), new Dictionary<string, string?>());

    [Fact]
    public void Samples_taken_while_Windows_is_locked_are_counted_apart_and_do_not_fail_the_run()
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Recording(
            "10:00:00.000\t4100\texplorer\tFalse\tFalse\t",
            "10:00:00.250\t5200\tLockApp\tFalse\tTrue\t6100:shown:0,0,3840,2162:ON",
            "10:00:00.500\t5200\tLockApp\tFalse\tTrue\t6100:shown:0,0,3840,2162:ON",
            "10:00:00.750\t5200\tLockApp\tFalse\tFalse\t6100:shown:-32000,-32000,-31000,-31000:off");
        var (code, output) = Replay(file);
        Assert.Equal(0, code);
        Assert.Contains("window-record: 4 samples, 3 with a run window up, 0 with a run window on a monitor or in front, 3 taken while Windows was locked (2 of them with a run window on a monitor or in front, not counted)", output);
    }

    [Fact]
    public void A_run_window_on_a_monitor_while_Windows_is_unlocked_still_fails_the_run()
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Recording(
            "10:00:00.000\t5200\tLockApp\tFalse\tTrue\t6100:shown:0,0,3840,2162:ON",
            "10:00:00.250\t4100\texplorer\tFalse\tTrue\t6100:shown:0,0,1152,648:ON");
        var (code, output) = Replay(file);
        Assert.Equal(1, code);
        Assert.Contains("window-record: 2 samples, 2 with a run window up, 1 with a run window on a monitor or in front, 1 taken while Windows was locked (1 of them with a run window on a monitor or in front, not counted)", output);
        Assert.Contains("explorer", output);
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
        string script = File.ReadAllText(ScriptPath);
        Assert.DoesNotMatch(@"Start-Process|Process\]::Start|godot\.ps1|System\.Windows\.Forms|SetForegroundWindow|ShowWindow", script);
    }
}

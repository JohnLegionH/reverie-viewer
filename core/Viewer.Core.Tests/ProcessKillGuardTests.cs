// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Several people run test viewers on one machine, so a script may stop only the processes it started, by process id.
/// Stopping by image name would end someone else's run. These tests scan every script under tools\ and require that:
/// nothing stops a process by name, and the one place that runs taskkill is tools\own-processes.ps1, which stops a
/// process tree by id, tolerates a tree that already ended (taskkill prints an error for it, which under a script's
/// stop-on-error setting would end the whole run with no summary line) and records the ids it stopped.
/// Scripts only read files, so these tests share nothing and run in parallel.
/// </summary>
public sealed class ProcessKillGuardTests
{
    private static readonly string ToolsDir = Path.Combine(TestPaths.RepoRoot, "tools");
    private const string Helper = "own-processes.ps1";

    private static IEnumerable<(string Name, string[] Lines)> Scripts() =>
        Directory.EnumerateFiles(ToolsDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetFileName(f), File.ReadAllLines(f)));

    /// <summary>Code lines only: a comment may name the commands it avoids.</summary>
    internal static IEnumerable<string> CodeLines(string[] lines) =>
        lines.Select(l => l.TrimStart()).Where(l => !l.StartsWith('#'));

    private static readonly Regex ByName = new(
        @"taskkill[^\n]*/IM\b|Stop-Process[^\n]*-Name\b|\bpkill\b|\bkillall\b|Get-Process\b[^|\n]*\|\s*(?:Where-Object[^|\n]*\|\s*)?Stop-Process|Stop-Process[^\n]*-InputObject|\bGet-Process\s+-Name\b[^|\n]*\|\s*Stop",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static IEnumerable<string> ByNameHits(string name, string[] lines) =>
        CodeLines(lines).Where(l => ByName.IsMatch(l)).Select(l => name + ": " + l);

    internal static IEnumerable<string> TaskkillOutsideHelper(string name, string[] lines) =>
        name.Equals(Helper, StringComparison.OrdinalIgnoreCase)
            ? []
            : CodeLines(lines).Where(l => l.Contains("taskkill", StringComparison.OrdinalIgnoreCase)).Select(l => name + ": " + l);

    [Fact]
    public void No_script_stops_a_process_by_name()
    {
        var hits = Scripts().SelectMany(s => ByNameHits(s.Name, s.Lines)).ToList();
        Assert.True(hits.Count == 0, "kill by name: " + string.Join(" | ", hits));
    }

    [Fact]
    public void The_scan_catches_a_kill_by_name_and_passes_a_kill_by_id()
    {
        string[] bad =
        [
            "taskkill /F /IM Reverie.exe", "taskkill.exe /T /F /IM godot.exe", "Stop-Process -Name Viewer -Force", "pkill Viewer", "killall godot",
            "Get-Process Viewer | Stop-Process", "Get-Process -Name Viewer | Stop-Process -Force", "Get-Process | Where-Object { $_.Name -eq 'x' } | Stop-Process",
        ];
        foreach (string line in bad) Assert.NotEmpty(ByNameHits("t.ps1", [line]));
        string[] good = ["& taskkill.exe /T /F /PID $id", "Stop-Process -Id $p.Id -Force", "# taskkill /IM is never used"];
        foreach (string line in good) Assert.Empty(ByNameHits("t.ps1", [line]));
    }

    [Fact]
    public void Only_the_helper_runs_taskkill()
    {
        var hits = Scripts().SelectMany(s => TaskkillOutsideHelper(s.Name, s.Lines)).ToList();
        Assert.True(hits.Count == 0, "taskkill outside " + Helper + ": " + string.Join(" | ", hits));
    }

    [Fact]
    public void The_helper_stops_by_id_tolerates_an_ended_tree_and_records_the_ids()
    {
        string path = Path.Combine(ToolsDir, Helper);
        Assert.True(File.Exists(path), "tools\\" + Helper + " is missing");
        string text = File.ReadAllText(path);
        Assert.Contains("/PID", text);
        Assert.DoesNotContain("/IM", text);
        Assert.Contains("function Stop-OwnedTree", text);
        Assert.Contains("NV_STOPPED_PIDS_FILE", text);
        // an ended tree is not an error: the stop-on-error setting is lifted around the native command
        Assert.Matches(@"ErrorActionPreference\s*=\s*""Continue""", text);
    }

    [Fact]
    public void Every_script_that_stops_a_tree_loads_the_helper()
    {
        var missing = Scripts()
            .Where(s => !s.Name.Equals(Helper, StringComparison.OrdinalIgnoreCase)
                        && CodeLines(s.Lines).Any(l => l.Contains("Stop-OwnedTree")))
            .Where(s => s.Name != "render-serve.ps1")   // dot-sourced by render.ps1, which loads the helper
            .Where(s => !CodeLines(s.Lines).Any(l => l.Contains(Helper)))
            .Select(s => s.Name).ToList();
        Assert.Empty(missing);
        Assert.Contains(Scripts(), s => s.Name == "render.ps1" && CodeLines(s.Lines).Any(l => l.Contains(Helper)));
    }
}

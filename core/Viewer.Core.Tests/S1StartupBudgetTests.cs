// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The S-1 stage starts the exported viewer several times and checks what each start logged and how it ended. Those checks are about
/// what a start does, not how fast it is, so a start may take as long as a busy machine needs: every launch the script makes is cut
/// off only at one shared startup budget, never at a shorter private limit (a start that took more than 120 s beside other builds
/// was cut off and reported as a failure of the check). The tests read the script; they start nothing.
/// </summary>
public sealed class S1StartupBudgetTests
{
    private static string Script => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "s1.ps1"));

    [Fact]
    public void The_startup_budget_is_defined_once_and_is_generous()
    {
        var m = Regex.Match(Script, @"\$startupBudgetSeconds\s*=\s*(\d+)");
        Assert.True(m.Success, "tools\\s1.ps1 defines no $startupBudgetSeconds");
        Assert.True(int.Parse(m.Groups[1].Value) >= 300, $"the startup budget is {m.Groups[1].Value} s");
    }

    [Fact]
    public void Every_launch_of_the_exported_viewer_is_cut_off_only_at_the_startup_budget()
    {
        var bad = new List<string>();
        foreach (var line in Script.Split('\n'))
        {
            if (!line.Contains("-TimeoutSeconds")) continue;
            if (line.Contains("-Exe $GodotExe")) continue;   // the editor's own import and export steps have their own limit
            var m = Regex.Match(line, @"-TimeoutSeconds\s+(\S+)");
            string v = m.Groups[1].Value;
            if (v == "$startupBudgetSeconds") continue;
            bad.Add(line.Trim());
        }
        Assert.True(bad.Count == 0, "launches with a private time limit:\n" + string.Join("\n", bad));
    }
}

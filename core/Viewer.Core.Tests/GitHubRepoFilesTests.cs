// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The repository's GitHub files: every workflow uses actions at a major version that runs on Node.js 24 (GitHub retires the
/// Node.js 20 runtime for actions), pinned by major tag, and the issue chooser keeps blank issues off and carries no link of its
/// own for security reports (GitHub adds its "Report a vulnerability" entry while private vulnerability reporting is on).
/// </summary>
public class GitHubRepoFilesTests
{
    private static string GitHubDir => Path.Combine(TestPaths.RepoRoot, ".github");

    // the first major version of each action that runs on Node.js 24
    private static readonly Dictionary<string, int> FirstNode24Major = new()
    {
        ["actions/checkout"] = 5,
        ["actions/upload-artifact"] = 5,
    };

    [Fact]
    public void Every_action_runs_on_Node_24_and_is_pinned_by_major_tag()
    {
        var uses = Directory.GetFiles(Path.Combine(GitHubDir, "workflows"), "*.yml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"uses:\s*([^\s@]+)@(\S+)").Select(m => (Action: m.Groups[1].Value, Ref: m.Groups[2].Value)))
            .ToList();
        Assert.NotEmpty(uses);
        foreach (var (action, reference) in uses)
        {
            Assert.True(FirstNode24Major.ContainsKey(action), $"{action}: not in the list of actions checked for Node.js 24; add it");
            var m = Regex.Match(reference, @"^v(\d+)$");
            Assert.True(m.Success, $"{action}@{reference}: pin by major tag (vN), as the repository does");
            Assert.True(int.Parse(m.Groups[1].Value) >= FirstNode24Major[action], $"{action}@{reference} runs on Node.js 20");
        }
    }

    [Fact]
    public void The_issue_chooser_keeps_blank_issues_off_and_has_no_security_link_of_its_own()
    {
        var config = File.ReadAllText(Path.Combine(GitHubDir, "ISSUE_TEMPLATE", "config.yml"));
        Assert.Matches(new Regex(@"^blank_issues_enabled:\s*false\s*$", RegexOptions.Multiline), config);
        Assert.DoesNotContain("security/advisories", config, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"^contact_links:", RegexOptions.Multiline), config);
    }
}

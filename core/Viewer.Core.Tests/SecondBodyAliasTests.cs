// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text;
using System.Text.RegularExpressions;
using Viewer.Automation;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The live scripts and tools name only this project's own test accounts: tester1 (the viewer's own login) and
/// viewer1 (the second avatar a live check drives beside it) and truly (a test avatar with a worn outfit that a live check observes). The other aliases in the shared accounts file belong to
/// other test clients, which may be logged in with them at any moment; one account logged in twice throws the other
/// session out. A tracked script or tool under tools/ or host/ that names one of them fails here. A live check may
/// still be handed one of them by an avatar claim from the test-account pool file, which keeps the two clients apart;
/// the automation's allowed aliases are that file's (AccountPoolTests), not a list in the code.
/// </summary>
public class SecondBodyAliasTests
{
    /// <summary>Aliases in the shared accounts file that belong to other test clients.</summary>
    private static readonly string[] OtherAliases = ["tester2", "jolt1", "jolt2"];

    private static readonly Regex Named = new(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", OtherAliases) + @")(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] Scanned = ["tools/", "host/"];
    private static readonly string[] BinaryExtensions = [".png", ".gz", ".llmesh", ".bin", ".j2c", ".j2k", ".dll", ".pck", ".exe", ".ico", ".ttf", ".otf"];

    internal static List<string> Scan(string path, string text)
    {
        var found = new List<string>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in Named.Matches(lines[i]))
                found.Add($"{path}:{i + 1} names {m.Value}");
        return found;
    }

    [Fact]
    public void The_scan_finds_another_clients_alias_and_passes_the_projects_own()
    {
        Assert.Single(Scan("tools/x.ps1", "$a = \"tester2\""));
        Assert.Single(Scan("tools/x.yaml", "avatars: [JOLT1]"));
        Assert.Single(Scan("host/x.cs", "login(jolt2);"));
        Assert.Empty(Scan("tools/x.ps1", "$a = \"tester1\"; $b = \"viewer1\""));
        Assert.Empty(Scan("tools/x.ps1", "tester20 xtester2 tester2x"));   // only the whole alias
    }

    [Fact]
    public void Live_scripts_and_tools_name_no_other_clients_test_account()
    {
        var (exit, listing) = ChildProcess.Run("git", ["-C", TestPaths.RepoRoot, "ls-files", "--", "tools", "host"], TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        var findings = new List<string>();
        int scanned = 0;
        foreach (var f in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = f.Replace('\\', '/');
            if (!Scanned.Any(path.StartsWith) || BinaryExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            var full = Path.Combine(TestPaths.RepoRoot, f);
            if (!File.Exists(full)) continue;
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
            findings.AddRange(Scan(path, Encoding.UTF8.GetString(bytes)));
            scanned++;
        }
        Assert.True(scanned > 50, $"only {scanned} tracked files under tools/ and host/");
        Assert.True(findings.Count == 0, $"{findings.Count} findings:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Automation_logins_have_no_list_of_aliases_in_the_code()
    {
        Assert.Null(typeof(AccountsFile).GetField("Approved"));
        Assert.Empty(typeof(AccountsFile).GetFields().Where(f => f.FieldType == typeof(string[])));
    }
}

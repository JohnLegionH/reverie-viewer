// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Test code deletes only through <see cref="TestTemp"/>, and only paths TestTemp handed out
/// in the same run. A source scan proves no other test file calls a delete; negative controls show the scan and the
/// helper each refuse what they should.
/// </summary>
public class TempHygieneTests
{
    /// <summary>A call of delete method (File, Directory, FileInfo, DirectoryInfo, async or not) not made
    /// on TestTemp, or a shell remove. Built from pieces so this file's own source does not match.</summary>
    private static readonly Regex DeleteCall = new(@"(?<!\bTestTemp\.)\b" + "Del" + @"ete(?:Async)?\s*\(|" + "Remove" + @"-Item|\b" + "rm" + @"dir\b", RegexOptions.Compiled);

    private const string HelperFile = "core/Viewer.Core.Tests/TestTemp.cs";

    /// <summary>Test code: the test projects, plus the Godot host's test-only entry points.</summary>
    private static IEnumerable<string> TestSources()
    {
        string root = TestPaths.RepoRoot;
        foreach (var project in new[] { "core/Viewer.Core.Tests", "core/Viewer.Core.LifecycleSmoke", "core/Viewer.Core.ArchFixture" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!rel.Contains("/bin/") && !rel.Contains("/obj/"))
                    yield return rel;
            }
        yield return "host/Viewer.Godot/TestRender.cs";
        yield return "host/Viewer.Godot/S1Smoke.cs";
    }

    internal static IEnumerable<string> Scan(string rel, string text)
    {
        if (rel == HelperFile)
            yield break;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (DeleteCall.IsMatch(lines[i]))
                yield return $"{rel}:{i + 1}: {lines[i].Trim()}";
    }

    [Fact]
    public void No_test_code_deletes_outside_the_helper()
    {
        var sources = TestSources().ToList();
        Assert.Contains(sources, s => s.EndsWith("ContentStoreTests.cs"));   // the scan sees the test project
        var hits = sources.SelectMany(rel => Scan(rel, File.ReadAllText(Path.Combine(TestPaths.RepoRoot, rel)))).ToList();
        Assert.True(hits.Count == 0, "deletes outside TestTemp:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Temp_paths_in_test_code_come_only_from_the_helper()
    {
        var temp = new Regex(@"\bGetTemp(Path|FileName)\s*\(");
        var hits = TestSources().Where(rel => rel != HelperFile && !rel.EndsWith("/TempHygieneTests.cs"))
            .Where(rel => temp.IsMatch(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, rel)))).ToList();
        Assert.True(hits.Count == 0, "temp paths made outside TestTemp:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Negative_control_the_scan_flags_each_kind_of_delete()
    {
        string file = "File", dir = "Directory", del = ".Del" + "ete(";   // split so this file does not match itself
        Assert.NotEmpty(Scan("x.cs", $"finally {{ {file}{del}path); }}"));
        Assert.NotEmpty(Scan("x.cs", $"{dir}{del}root, true);"));
        Assert.NotEmpty(Scan("x.cs", $"new {file}Info(p){del});"));
        Assert.NotEmpty(Scan("x.cs", $"new {dir}Info(p){del}true);"));
        Assert.NotEmpty(Scan("x.cs", "Remove" + "-Item x"));
        Assert.Empty(Scan("x.cs", "TestTemp" + del + "path);"));
        Assert.Empty(Scan(HelperFile, $"{file}{del}full);"));   // only the helper itself is exempt
    }

    [Fact]
    public void The_helper_refuses_paths_it_did_not_create_in_this_run()
    {
        string outside = Path.Combine(Path.GetTempPath(), TestTemp.Prefix + "never-recorded-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => TestTemp.Delete(outside));
        Assert.Throws<InvalidOperationException>(() => TestTemp.Delete(Path.Combine(TestPaths.RepoRoot, ".gitignore")));
        Assert.Throws<InvalidOperationException>(() => TestTemp.Delete(Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => TestTemp.Delete(""));
        Assert.Throws<ArgumentException>(() => TestTemp.NewFolderPath("*"));
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, ".gitignore")));

        // a file inside a recorded folder is not itself recorded: only the unique folder as a whole may go
        string folder = TestTemp.NewFolder("hygiene");
        string inner = Path.Combine(folder, "a.txt");
        File.WriteAllText(inner, "x");
        Assert.Throws<InvalidOperationException>(() => TestTemp.Delete(inner));
        Assert.True(File.Exists(inner));
        TestTemp.Delete(folder);
        Assert.False(Directory.Exists(folder));
        Assert.Throws<InvalidOperationException>(() => TestTemp.Delete(folder));   // deleted once, then no longer recorded

        string file = TestTemp.NewFilePath("hygiene", ".txt");
        File.WriteAllText(file, "x");
        TestTemp.Delete(file);
        Assert.False(File.Exists(file));
    }
}

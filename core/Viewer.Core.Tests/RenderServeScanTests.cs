// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The batch mode of the render harness runs many cases in one process, so every process-wide test switch a case sets must be put back
/// before the next case (TestRender.Serve.cs, CaptureProcessDefaults). This scan reads the harness's sources and fails when a case
/// assigns a static member of one of the viewer's own types, or declares a static field of its own, that the list does not name:
/// a switch left set would reach the cases after it in the same process.
/// </summary>
public sealed class RenderServeScanTests
{
    private static string HostFolder => Environment.GetEnvironmentVariable("NV_RENDER_HOST_UNDER_TEST") is { Length: > 0 } other ? other : Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");

    private static IEnumerable<string> HarnessFiles() =>
        Directory.GetFiles(HostFolder, "TestRender*.cs").Where(f => !f.EndsWith("TestRender.Serve.cs", StringComparison.Ordinal));

    private static string ServeSource() => File.Exists(Path.Combine(HostFolder, "TestRender.Serve.cs")) ? File.ReadAllText(Path.Combine(HostFolder, "TestRender.Serve.cs")) : "";

    private static HashSet<string> TypeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var decl = new Regex(@"\b(?:class|struct|record|enum)\s+([A-Z]\w+)", RegexOptions.Compiled);
        foreach (var folder in new[] { HostFolder, Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell") })
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
                    foreach (Match m in decl.Matches(File.ReadAllText(f))) names.Add(m.Groups[1].Value);
        return names;
    }

    [Fact]
    public void The_reset_list_exists_and_names_switches()
    {
        string serve = ServeSource();
        Assert.Contains("CaptureProcessDefaults", serve);
        Assert.True(Regex.Matches(serve, @"Keep(?:<\w+>)?\(""[\w.]+""").Count >= 25, "the list of process-wide test switches is missing or short");
    }

    [Fact]
    public void Every_static_member_a_case_assigns_is_put_back_between_cases()
    {
        var types = TypeNames();
        types.ExceptWith(new[] { "TestRender" });   // its own statics are scanned below
        var assign = new Regex(@"\b([A-Z]\w+)\.([A-Z]\w+)\s*(?:=|\+=|\|=)(?![=>])", RegexOptions.Compiled);
        string serve = ServeSource();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in HarnessFiles())
            foreach (Match m in assign.Matches(File.ReadAllText(file)))
            {
                string type = m.Groups[1].Value, member = m.Groups[2].Value, name = type + "." + member;
                if (!types.Contains(type)) continue;   // an engine type, a local value or an instance property
                if (!serve.Contains("\"" + name + "\"", StringComparison.Ordinal)) missing.Add(name + " (" + Path.GetFileName(file) + ")");
            }
        Assert.True(missing.Count == 0, "static members a case assigns that the reset list does not name: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_static_field_the_harness_declares_is_put_back_between_cases()
    {
        var field = new Regex(@"^\s*(?:private|internal|public)?\s*static\s+(?!readonly\b|async\b|extern\b|class\b)[\w<>\[\]?,.() ]+?\s+(s_\w+)\b", RegexOptions.Compiled | RegexOptions.Multiline);
        string serve = ServeSource();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in HarnessFiles())
            foreach (Match m in field.Matches(File.ReadAllText(file)))
            {
                string line = m.Value;
                if (line.Contains('(')) continue;   // a method
                if (line.Contains("=>")) continue;   // an expression-bodied property over a field
                if (!serve.Contains("TestRender." + m.Groups[1].Value, StringComparison.Ordinal)) missing.Add(m.Groups[1].Value + " (" + Path.GetFileName(file) + ")");
            }
        Assert.True(missing.Count == 0, "static fields of the harness that the reset list does not name: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_readonly_static_collections_the_harness_keeps_are_emptied_between_cases()
    {
        string serve = ServeSource();
        foreach (var file in HarnessFiles())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"static readonly [\w.<>, ]*(?:Dictionary|List|HashSet|Queue|Stack|Bag)\w*<[^>]*>\s+(s_\w+)"))
                Assert.True(serve.Contains(m.Groups[1].Value + ".Clear()", StringComparison.Ordinal), m.Groups[1].Value + " is a static collection of the harness that is not emptied between cases");
    }
}

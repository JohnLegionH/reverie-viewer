// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// No interface code talks to the protocol library or to the transport's command side (ADR-005, ADR-015). The host's
/// projects (the engine host, the shell logic, the automation server) reach the grid through the core's session and its
/// action pipeline only. A source scan covers the engine host, which this test project does not build; the compiled
/// shell assembly is checked in ArchitectureTests. Every member is independent, so the class runs in parallel.
/// </summary>
public class HostLibraryScanTests
{
    private static readonly Regex Forbidden = new(@"\bLibreMetaverse\b|\bICommandTransport\b|\bTransportCommand\b|\bISessionTransport\b|\bLmvTransport\b", RegexOptions.Compiled);

    /// <summary>The lines of <paramref name="text"/> that name a forbidden type, outside comments.</summary>
    internal static List<string> Hits(string text) =>
        text.Split('\n')
            .Select((l, i) => (Line: l.IndexOf("//", StringComparison.Ordinal) is var c and >= 0 ? l[..c] : l, No: i + 1))
            .Where(x => Forbidden.IsMatch(x.Line))
            .Select(x => $"{x.No}: {x.Line.Trim()}")
            .ToList();

    [Fact]
    public void The_scan_finds_each_forbidden_name_and_ignores_comments()
    {
        Assert.Single(Hits("using LibreMetaverse;"));
        Assert.Single(Hits("void F(ICommandTransport t) { }"));
        Assert.Single(Hits("var c = new TransportCommand();"));
        Assert.Single(Hits("ISessionTransport t = null;"));
        Assert.Empty(Hits("// uses LibreMetaverse and ICommandTransport in a comment"));
        Assert.Empty(Hits("session.Actions.Submit(new SayIntent(\"hi\"));"));
    }

    [Fact]
    public void No_host_source_names_the_library_or_the_transport_interfaces()
    {
        var offenders = new List<string>();
        int files = 0;
        foreach (var project in new[] { "Viewer.Godot", "Viewer.Shell", "Viewer.Automation" })
        {
            var dir = Path.Combine(TestPaths.RepoRoot, "host", project);
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj" or ".godot" or "build")) continue;
                files++;
                foreach (var hit in Hits(File.ReadAllText(f)))
                    offenders.Add($"{project}/{string.Join('/', parts)}:{hit}");
            }
        }
        Assert.True(files > 50, $"the scan saw only {files} host source files");   // not vacuous
        Assert.Empty(offenders);
    }
}

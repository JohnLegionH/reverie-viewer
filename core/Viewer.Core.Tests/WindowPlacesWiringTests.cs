// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every window of the viewer remembers its place through one mechanism: the shell's floater manager, given the settings-backed
/// places by the main window. The engine host cannot run in the unit tests, so the wiring is checked in the source: the main shell is
/// built with the remembered places and saves them when the viewer closes; every window is added as a floater; and no code makes a
/// free-standing engine window of its own, which would bypass the remembered places. The operating system's own file and
/// confirmation dialogs are the only exceptions, listed here with the file that uses each.
/// </summary>
public class WindowPlacesWiringTests
{
    private static string Host => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");

    private static IEnumerable<(string File, string Text)> HostSources() =>
        Directory.EnumerateFiles(Host, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".godot" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetRelativePath(Host, f).Replace('\\', '/'), File.ReadAllText(f)));

    [Fact]
    public void The_main_shell_is_built_with_the_remembered_places_and_saves_them_on_close()
    {
        string main = File.ReadAllText(Path.Combine(Host, "Main.cs"));
        Assert.Matches(@"new ShellRoot\(new ShellController\(strings, RequestClose, new SettingsFloaterPlaces\(startSettings\)\)\)", main);
        string all = string.Concat(HostSources().Where(s => s.File.StartsWith("Main", StringComparison.Ordinal)).Select(s => s.Text));
        Assert.Matches(@"Floaters\.SavePlaces\(\)", all);
    }

    // system dialogs the engine draws for the operating system's file choosers and one yes/no question; none is a viewer window
    private static readonly Dictionary<string, string> AllowedDialogs = new()
    {
        ["BuildPanels.cs"] = "FileDialog",
        ["ModelPanels.cs"] = "FileDialog",
        ["PreferencesPanels.cs"] = "FileDialog",
        ["PreferencePages.cs"] = "FileDialog",
        ["AddonPanels.cs"] = "ConfirmationDialog",
    };

    [Fact]
    public void No_window_is_made_outside_the_floater_manager()
    {
        var bad = new List<string>();
        foreach (var (file, text) in HostSources())
        {
            if (file.StartsWith("TestRender", StringComparison.Ordinal) || file.StartsWith("S1Smoke", StringComparison.Ordinal))
                continue;   // test scenes draw their own pictures
            foreach (Match m in Regex.Matches(text, @"(?:new\s+|:\s*)(Window|AcceptDialog|ConfirmationDialog|FileDialog)\b(?!\.)"))
            {
                string kind = m.Groups[1].Value;
                if (!(AllowedDialogs.TryGetValue(Path.GetFileName(file), out var allowed) && allowed == kind))
                    bad.Add($"{file}: {kind}");
            }
        }
        Assert.True(bad.Count == 0, "windows made outside the floater manager: " + string.Join("; ", bad));
    }

    [Fact]
    public void Every_window_the_viewer_adds_goes_through_AddFloater_with_a_spec()
    {
        var calls = HostSources().SelectMany(s => Regex.Matches(s.Text, @"\.AddFloater\(([^,]*)").Select(m => (s.File, Arg: m.Groups[1].Value.Trim()))).ToList();
        Assert.True(calls.Count >= 20, $"only {calls.Count} AddFloater calls found");
        Assert.All(calls, c => Assert.True(c.Arg.StartsWith("new FloaterSpec(", StringComparison.Ordinal) || c.Arg == "spec", $"{c.File}: {c.Arg}"));
    }
}

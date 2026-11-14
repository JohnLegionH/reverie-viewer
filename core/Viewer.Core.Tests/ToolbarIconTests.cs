// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every toolbar button has an icon from the icon set and says which window it opens, so the toolbar can draw the icon above
/// its label and show the button as active while that window is open. A source scan of the engine host's button
/// registrations checks both. Plain .NET; every member is independent, so the class runs in parallel.
/// </summary>
public class ToolbarIconTests
{
    [Fact]
    public void A_toolbar_button_carries_its_icon_and_its_window()
    {
        var b = new ToolbarButton("toolbar_chat", "toolbar.chat", "floater.chat", UiIcons.Chat, "nearby_chat");
        Assert.Equal(UiIcons.Chat, b.Icon);
        Assert.Equal("nearby_chat", b.FloaterId);
        var plain = new ToolbarButton("x", "toolbar.x", "c");
        Assert.Null(plain.Icon);
        Assert.Null(plain.FloaterId);
    }

    [Fact]
    public void Every_toolbar_button_the_host_registers_names_an_icon_and_a_window()
    {
        var registrations = new List<string>();
        var godot = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        foreach (var f in Directory.EnumerateFiles(godot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(f).StartsWith("TestRender", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("S1Smoke", StringComparison.Ordinal))
                continue;
            foreach (var line in File.ReadAllLines(f))
                if (line.Contains("new ToolbarButton(", StringComparison.Ordinal))
                    registrations.Add(line.Trim());
        }
        Assert.True(registrations.Count >= 8, $"found only {registrations.Count} registrations");   // not vacuous
        var bad = registrations.Where(r => !Regex.IsMatch(r, @"UiIcons\.\w+\s*,\s*\w+")).ToList();
        Assert.Empty(bad);
    }
}

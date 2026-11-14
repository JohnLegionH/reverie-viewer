// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The right-click menu never keeps the keyboard, as the official viewer's menus do not (llmenugl.cpp): after a choice, the arrow keys,
/// Page Up and Home reach the avatar without Escape. The engine host cannot run in the unit tests, so the wiring is checked in the
/// source: the menu's entries are made without keyboard focus, closing the menu releases the focus if anything in it holds it, and the
/// host releases the focus when a sit is sent. The live check (tools/sit-box-live.ps1) presses the keys after a menu sit.
/// </summary>
public class MenuKeyboardWiringTests
{
    private static string Host => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");

    private static string MenuClass()
    {
        string text = File.ReadAllText(Path.Combine(Host, "InteractPanels.cs"));
        int start = text.IndexOf("public partial class ContextMenuPanel", StringComparison.Ordinal);
        Assert.True(start >= 0, "ContextMenuPanel not found");
        int end = text.IndexOf("public static class InteractFeature", start, StringComparison.Ordinal);
        return end > start ? text[start..end] : text[start..];
    }

    [Fact]
    public void Every_entry_of_the_menu_is_made_without_keyboard_focus()
    {
        var buttons = Regex.Matches(MenuClass(), @"new Button \{[^}]*\}");
        Assert.NotEmpty(buttons);
        foreach (Match b in buttons)
            Assert.Contains("FocusMode = FocusModeEnum.None", b.Value);
    }

    [Fact]
    public void Closing_the_menu_releases_the_keyboard_held_inside_it()
    {
        var close = Regex.Match(MenuClass(), @"public void Close\(\)\s*\{(?<body>[\s\S]*?)\n    \}");
        Assert.True(close.Success, "Close not found");
        Assert.Matches(@"GuiGetFocusOwner\(\)[\s\S]*IsAncestorOf\([\s\S]*GuiReleaseFocus\(\)", close.Groups["body"].Value);
    }

    [Fact]
    public void The_host_releases_the_keyboard_when_a_sit_is_sent()
    {
        string interact = File.ReadAllText(Path.Combine(Host, "Main.Interact.cs"));
        Assert.Matches(@"\.SitSent \+= \(\) => GetViewport\(\)\.GuiReleaseFocus\(\)", interact);
    }
}

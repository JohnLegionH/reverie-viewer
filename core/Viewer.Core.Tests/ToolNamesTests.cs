// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Build;

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// One name per tool: the top-row tool that drags objects (the official LLToolGrab, Ctrl+2, which the official window and menu call Move)
/// is Grab in the Build window and Grab Tool in the Build menu, while Edit's Move handles keep their name; every tool and mode button's
/// tooltip gives its shortcut, read from the menu's own shortcuts. Strings only; the class runs in parallel.
/// </summary>
public class ToolNamesTests
{
    private static readonly StringTable En = StringTable.English();

    private static MenuModel Menus()
    {
        var menus = new MenuModel();
        BuildMenu.Install(menus, () => true);
        BuildMenu.InstallEditing(menus, () => true, () => null, () => false);
        return menus;
    }

    private static string Chord(string shortcut) => string.Join("+", shortcut.Split('|').Select(p => p switch
    {
        "control" => "Ctrl",
        "shift" => "Shift",
        "alt" => "Alt",
        _ => p,
    }));

    [Fact]
    public void The_grab_tool_is_Grab_in_the_window_and_Grab_Tool_in_the_menu_and_Edits_Move_keeps_its_name()
    {
        Assert.Equal("Grab", En.Get("edit.tool.move"));
        Assert.Equal("Grab Tool", En.Get("menu.build.tool.move"));
        Assert.Equal("Grab (Ctrl+2). Called Move Tool in the official viewer.", En.Get("edit.tool.move.tip"));
        Assert.Equal("Move", En.Get("edit.manip.move"));
    }

    [Theory]
    [InlineData("edit.tool.focus", BuildMenu.FocusId)]
    [InlineData("edit.tool.move", BuildMenu.MoveId)]
    [InlineData("edit.tool.edit", BuildMenu.EditId)]
    [InlineData("edit.tool.create", BuildMenu.CreateId)]
    public void Each_tools_tooltip_names_it_and_gives_its_menu_shortcut(string key, string entryId)
    {
        var entry = Menus().Menus.SelectMany(m => m.Entries).Single(e => e.Id == entryId);
        Assert.NotNull(entry.Shortcut);
        Assert.True(En.Has(key + ".tip"), key + ".tip");
        string tip = En.Get(key + ".tip");
        Assert.StartsWith(En.Get(key) + " (" + Chord(entry.Shortcut!) + ")", tip);
    }

    [Theory]
    [InlineData("edit.manip.move", "")]
    [InlineData("edit.manip.rotate", "Ctrl")]
    [InlineData("edit.manip.scale", "Ctrl+Shift")]
    [InlineData("edit.manip.face", "")]
    public void Each_edit_modes_tooltip_names_it_and_gives_its_key_or_says_it_has_none(string key, string held)
    {
        Assert.True(En.Has(key + ".tip"), key + ".tip");
        string tip = En.Get(key + ".tip");
        Assert.StartsWith(En.Get(key), tip);
        if (held.Length > 0)
            Assert.Contains("(hold " + held + ")", tip);
        else
            Assert.Contains("(no shortcut)", tip);
    }
}

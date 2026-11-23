// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Ui;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The Land tool is not built, so no route reaches it: the tool call, a name or number typed into the automation, and the
/// official Ctrl+5 shortcut leave the Build window in the mode it was in. No engine; each test makes its own controller.</summary>
public class BuildLandRouteTests
{
    [Fact]
    public void Selecting_the_land_tool_changes_nothing()
    {
        var rig = new BuildFeaturesTabTests.Rig();
        rig.C.SelectTool(BuildTool.Create);
        int changes = 0;
        rig.C.Changed += () => changes++;
        rig.C.SelectTool(BuildTool.Land);
        Assert.Equal(BuildTool.Create, rig.C.Tool);
        Assert.True(rig.C.BuildMode);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Selecting_the_land_tool_with_build_mode_off_does_not_turn_build_mode_on()
    {
        var rig = new BuildFeaturesTabTests.Rig();
        rig.C.CloseBuild();
        rig.C.SelectTool(BuildTool.Land);
        Assert.False(rig.C.BuildMode);
        Assert.NotEqual(BuildTool.Land, rig.C.Tool);
    }

    [Theory]
    [InlineData("land")]
    [InlineData("Land")]
    [InlineData("4")]
    [InlineData("99")]
    [InlineData("")]
    [InlineData(null)]
    public void Typed_text_never_names_the_land_tool_or_an_unknown_one(string? text)
    {
        Assert.False(EditController.TryParseTool(text, out _));
    }

    [Theory]
    [InlineData("focus", BuildTool.Focus)]
    [InlineData("Move", BuildTool.Move)]
    [InlineData("edit", BuildTool.Edit)]
    [InlineData("create", BuildTool.Create)]
    public void Typed_text_still_names_the_tools_that_exist(string text, BuildTool expected)
    {
        Assert.True(EditController.TryParseTool(text, out var tool));
        Assert.Equal(expected, tool);
    }

    [Fact]
    public void Ctrl_5_is_still_listed_as_not_built_and_no_menu_entry_takes_it()
    {
        Assert.Contains(ViewerShortcuts.NotBuilt, n => n.Shortcut == "control|5");
        Assert.DoesNotContain("control|5", ViewerShortcuts.ByEntry.Values);
    }

    [Fact]
    public void The_build_window_has_no_land_row_and_the_tool_stays_in_the_enum_for_later()
    {
        Assert.DoesNotContain(BuildLayout.Rows, r => r.Id == "tool_land");
        Assert.True(Enum.IsDefined(BuildTool.Land));
        Assert.False(EditController.LandToolBuilt);
    }
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// One look for choices: the login screen's Remember name and Remember password are plain tick boxes with their labels (the check box
/// look the restyled windows use), and every set of choices that switches a view or picks one option is the segmented control on its
/// track, as Inventory's tabs are: the Interface page's theme presets, Conversations' tabs and the login screen's Start at. The engine
/// controls cannot be built outside the engine, so these read the window sources; the render cases show the result. File reads; runs in parallel.
/// </summary>
public class ChoiceLookTests
{
    private static string Host(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Fact]
    public void Login_remember_name_and_remember_password_are_plain_tick_boxes()
    {
        string src = Host("LoginScreen.cs");
        Assert.Contains("_remember.ThemeTypeVariation = ReverieStyle.Check", src);
        Assert.Contains("_rememberPassword.ThemeTypeVariation = ReverieStyle.Check", src);
    }

    [Fact]
    public void Login_start_at_is_the_segmented_control_sized_as_Inventory_tabs()
    {
        string src = Host("LoginScreen.cs");
        Assert.Contains("new RvSegmented(", src);
        Assert.Contains("_start.SizeFlagsHorizontal = SizeFlags.ShrinkBegin", src);
        Assert.Contains("RvSegmented.Compact(_start)", src);
    }

    [Fact]
    public void The_theme_presets_are_segments_on_a_track()
    {
        string src = Host("PreferencesPanels.cs");
        Assert.Contains("Name = \"presets_track\", ThemeTypeVariation = ReverieStyle.Track", src);
        Assert.Contains("ReverieStyle.SegmentSelected : ReverieStyle.Segment", src);
    }

    [Fact]
    public void Conversations_tabs_are_the_segmented_control_on_a_track()
    {
        string src = Host("ChatPanels.cs");
        Assert.Contains("Name = \"tabs_track\", ThemeTypeVariation = ReverieStyle.Track", src);
        Assert.Contains("ThemeTypeVariation = ReverieStyle.TabBar", src);
    }
}

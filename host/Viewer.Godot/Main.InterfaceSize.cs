// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>The interface size command of the development automation: sets the size as Preferences > Interface does (75 to 200 percent) and
    /// answers the size in force, so a live check can look at a window at 100, 150 and 200 percent.</summary>
    public object InterfaceSizeCall(int percent)
    {
        UiTheme.Apply(UiTheme.Tokens, percent);
        return new { percent = UiTheme.ScalePercent };
    }
}

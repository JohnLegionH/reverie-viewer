// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What a window mode asks of the window: fullscreen, and no border.</summary>
public readonly record struct DisplayPlan(bool Fullscreen, bool Borderless)
{
    /// <summary>Whether the window is placed over the whole of the screen it is on: a borderless window covers its screen (the window
    /// system's own fullscreen already does).</summary>
    public bool FillsScreen => Borderless;

    /// <summary>The plan for a saved window mode: fullscreen when none is saved (a normal launch opens full screen), windowed for an unknown one.</summary>
    public static DisplayPlan For(string? mode) => mode switch
    {
        null or "fullscreen" => new DisplayPlan(true, false),
        "borderless" => new DisplayPlan(false, true),
        _ => new DisplayPlan(false, false),
    };
}

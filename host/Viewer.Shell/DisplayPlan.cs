// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>How the window opens: maximized, as it was left, or full screen.</summary>
public enum WindowStartMode { Maximized, Last, Fullscreen }

/// <summary>What the startup window choice asks of the window. The full screen is the engine's borderless one, so Alt+Tab and the Windows key keep working.</summary>
public readonly record struct DisplayPlan(WindowStartMode Start)
{
    public bool Fullscreen => Start == WindowStartMode.Fullscreen;

    /// <summary>Whether the window's size, place and screen are kept between runs (every choice but full screen).</summary>
    public bool Remembers => !Fullscreen;

    /// <summary>The plan for a saved choice: maximized when none is saved (or it is not understood). "windowed" and "borderless" are the
    /// values earlier versions kept: the window as it was left, and the full screen.</summary>
    /// <summary>Rewrites a value earlier versions kept ("windowed", "borderless") as the choice it stands for, so Preferences shows what the window does. True when it wrote.</summary>
    public static bool Migrate(SettingsService settings)
    {
        string? now = settings.GetText(Settings.WindowModeKey) switch { "windowed" => "last", "borderless" => "fullscreen", _ => null };
        return now is not null && settings.SetText(Settings.WindowModeKey, now);
    }

    public static DisplayPlan For(string? saved) => new(saved switch
    {
        "last" or "windowed" => WindowStartMode.Last,
        "fullscreen" or "borderless" => WindowStartMode.Fullscreen,
        _ => WindowStartMode.Maximized,
    });
}

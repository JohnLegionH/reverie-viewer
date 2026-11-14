// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The operating system's display scale for the screen the viewer's window is on, in percent. A normal run reads it from the
/// screen's dots per inch; the test harness leaves the reading at 100 so its pictures do not depend on the machine's display.
/// </summary>
public static class DisplayScaleProbe
{
    /// <summary>The reading, in percent; 100 until <see cref="UseScreenOf"/> is called.</summary>
    public static Func<int> Reading { get; private set; } = () => 100;

    public static void UseScreenOf(Window window) =>
        Reading = () => DisplayScale.PercentForDpi(DisplayServer.ScreenGetDpi(window.CurrentScreen));
}

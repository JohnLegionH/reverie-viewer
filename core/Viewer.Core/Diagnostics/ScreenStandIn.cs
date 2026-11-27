// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>
/// The size a window drawn off every monitor takes in place of being maximized or made full screen, which would put it on a
/// monitor. Maximized is the monitor's work area less the title bar; full screen is the whole monitor.
/// </summary>
public static class ScreenStandIn
{
    public static (int Width, int Height) Size(string step, (int Width, int Height) screen, (int Width, int Height) workArea, int captionHeight) =>
        step == "fullscreen" ? screen : (workArea.Width, workArea.Height - captionHeight);
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Decides when a window that follows a busy model redraws: at once for what the user did, never while the window is hidden,
/// and no more than once per gap for changes that arrive from the network (a background fetch of a large inventory delivers
/// several answers a second, and a redraw of thousands of rows should not follow each one).
/// </summary>
public sealed class RedrawGate(long minGapMs)
{
    private long _lastDraw = long.MinValue / 2;

    /// <summary>True when the window should redraw now.</summary>
    public bool Due(long nowMs, bool userRequested, bool modelChanged, bool visible)
    {
        if (!visible)
            return false;
        return userRequested || (modelChanged && nowMs - _lastDraw >= minGapMs);
    }

    /// <summary>Records that a redraw happened.</summary>
    public void Drew(long nowMs) => _lastDraw = nowMs;
}

// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>
/// Tells a test run whose window has stopped drawing from one that is only slow. The engine keeps calling the run's
/// per-frame callback while it sleeps between iterations, but a window that cannot draw (minimised, hidden, its swap
/// chain gone) never emits the frame-drawn signal the run waits on, so the run would wait for its whole timeout. The
/// watchdog counts both: it reports a stall only when no frame has been drawn for <see cref="StallSeconds"/> while the
/// callback ran at least <see cref="MinProcessCalls"/> times in that time. A long step that blocks the main thread stops
/// the callback too, so it never counts as a stall. It does not start until the first frame has been drawn.
/// Time is passed in (Stopwatch ticks) so tests need no clock. Single thread (the engine's main thread).
/// </summary>
public sealed class DrawWatchdog
{
    /// <summary>Seconds without a drawn frame, with the loop running, before the run is called not drawing.</summary>
    public const double StallSeconds = 10;

    /// <summary>Per-frame callbacks that must have run since the last drawn frame (a loop that is running, not blocked).</summary>
    public const int MinProcessCalls = 200;

    private readonly long _stallTicks;
    private long _lastDrawnTicks;
    private long _processSinceDrawn;

    public DrawWatchdog(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _stallTicks = (long)(StallSeconds * ticksPerSecond);
    }

    /// <summary>Frames drawn so far.</summary>
    public long Drawn { get; private set; }

    /// <summary>Per-frame callbacks so far.</summary>
    public long Processed { get; private set; }

    /// <summary>A frame was drawn.</summary>
    public void FrameDrawn(long nowTicks)
    {
        Drawn++;
        _lastDrawnTicks = nowTicks;
        _processSinceDrawn = 0;
    }

    /// <summary>The per-frame callback ran; true when the run has stopped drawing.</summary>
    public bool FrameProcessed(long nowTicks)
    {
        Processed++;
        _processSinceDrawn++;
        return Drawn > 0 && nowTicks - _lastDrawnTicks >= _stallTicks && _processSinceDrawn >= MinProcessCalls;
    }

    /// <summary>Seconds since the last drawn frame (0 before the first).</summary>
    public double SecondsSinceDrawn(long nowTicks, long ticksPerSecond) =>
        Drawn == 0 ? 0 : (double)(nowTicks - _lastDrawnTicks) / ticksPerSecond;
}

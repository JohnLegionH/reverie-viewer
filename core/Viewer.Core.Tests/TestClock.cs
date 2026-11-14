// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Tests;

/// <summary>
/// Explicit times for the residency manager's clock. <see cref="At"/> is frame <c>f</c> of a run at
/// <c>fps</c> frames per second, in whole ticks; <see cref="Frames"/> converts an frame count to its time at 60 fps
/// (60 frames = exactly 1 s), so the frame-based tests keep their meaning.
/// </summary>
public static class TestClock
{
    public static TimeSpan At(long frame, double fps) => TimeSpan.FromTicks((long)Math.Floor(frame * (double)TimeSpan.TicksPerSecond / fps));
    public static TimeSpan Frames(long frames) => TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / 60);
}

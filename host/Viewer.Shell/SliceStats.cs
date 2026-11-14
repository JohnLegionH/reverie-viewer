// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The lengths of a run's main-thread slices (a frame's script work, or the presentation frame inside it): how many there were, how many went
/// over a limit, the time they add up to, and the longest since the last <see cref="ResetMax"/>. Measurement only; main thread.
/// </summary>
public sealed class SliceStats
{
    public SliceStats(double limitMs = 4.0) { LimitMs = limitMs; }

    public double LimitMs { get; }
    public long Count { get; private set; }
    public long Over { get; private set; }
    public double MaxMs { get; private set; }
    public double TotalMs { get; private set; }

    public void Add(double ms)
    {
        Count++;
        TotalMs += ms;
        if (ms > LimitMs) Over++;
        if (ms > MaxMs) MaxMs = ms;
    }

    /// <summary>Starts the longest slice again from nothing; the counts keep adding up.</summary>
    public void ResetMax() => MaxMs = 0;
}

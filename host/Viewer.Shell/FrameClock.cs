// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// One time for a whole frame, as the official viewer has one frame time (gFrameTimeSeconds, set once at the start of LLAppViewer::idle, before the
/// network step and the objects' idleUpdate). Every avatar's motion in a frame is timed on it, so the place a body is drawn at and the place the
/// camera then centres on are the same (llvoavatar.cpp updateCharacter: gAgent.setPositionAgent(getRenderPosition())). Before the first
/// <see cref="Latch"/> it reads the source directly. Main thread.
/// </summary>
public sealed class FrameClock(Func<double> source)
{
    private double _now = double.NaN;

    /// <summary>The frame's time: the source's reading at the last <see cref="Latch"/>.</summary>
    public double Now => double.IsNaN(_now) ? source() : _now;

    /// <summary>Starts a frame: its time is the source's reading now.</summary>
    public void Latch() => _now = source();
}

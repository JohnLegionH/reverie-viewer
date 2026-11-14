// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Adds up the engine's measured GPU and render-thread CPU time of every frame once it has been switched on (development builds only), so
/// two runs can be compared by the work the graphics card and the render thread did instead of by how long a frame took on a machine that
/// is also busy with other work. Main thread only: <see cref="Frame"/> is called once per frame and <see cref="Enable"/> from the automation reading.
/// </summary>
internal static class FrameMeter
{
    private static Rid _viewport;
    private static bool _on;

    /// <summary>The main thread's slices in processor time (its cycle counter): a whole frame's script work, and the presentation frame inside it.</summary>
    public static SliceStats Process { get; } = new(4.0);
    public static SliceStats Presenter { get; } = new(4.0);

    /// <summary>The cycle counter now, or 0 while the meter is off (pass it to <see cref="End"/>).</summary>
    public static long Begin() => _on ? CpuMeter.ThreadCycles() : 0;

    public static void End(long began, SliceStats into)
    {
        if (began != 0 && _on) into.Add((CpuMeter.ThreadCycles() - began) / CpuMeter.CyclesPerMs);
    }

    /// <summary>The longest slice starts again from nothing (the counts keep adding up).</summary>
    public static void ResetLongest()
    {
        Process.ResetMax();
        Presenter.ResetMax();
    }

    public static long Frames { get; private set; }
    public static double GpuMs { get; private set; }
    public static double RenderCpuMs { get; private set; }

    public static void Enable(Node anyNode)
    {
        if (_on) return;
        _ = CpuMeter.CyclesPerMs;   // calibrated now, not inside a frame
        _viewport = anyNode.GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        _on = true;
    }

    public static void Frame()
    {
        if (!_on) return;
        GpuMs += RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        RenderCpuMs += RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
        Frames++;
    }
}
#endif

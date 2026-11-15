// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

// Where a test or measurement run's viewer window goes. The same file is compiled by tools\godot.ps1 (Windows
// PowerShell 5.1 builds it with Add-Type, so it sticks to C# 5) and linked into the unit tests, so the placement
// the scripts use is the placement the tests check.
//
// A test window is a quarter of the area of the monitor it is placed on (half the width, half the height of that
// monitor's work area, the screen minus the taskbar), in the bottom-right corner of it. The monitor is the one that
// holds the window the person at the machine is working in (the foreground window) when the run starts, so the test
// window opens on the screen that person is looking at. The picture is not made smaller: the engine keeps drawing
// at the usual size and scales it for display. Measuring runs get the same window as every other test run; what they
// measure is the scene, drawn at the same size either way. The window never takes focus and sits at the bottom of the
// stacking order (tools\godot.ps1 and host\Viewer.Godot\TestWindow.cs do that; this file only decides where and how big).

using System;
using System.Text.RegularExpressions;

namespace NvTools
{
    /// <summary>A rectangle in virtual-desktop pixels (the origin of a monitor to the left of or above the primary one is negative).</summary>
    public sealed class PixelRect
    {
        public readonly int X, Y, Width, Height;

        public PixelRect(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public int Right { get { return X + Width; } }
        public int Bottom { get { return Y + Height; } }

        public bool Contains(PixelRect inner)
        {
            return inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;
        }

        /// <summary>The area both rectangles cover (0 when they do not meet).</summary>
        public long OverlapArea(PixelRect other)
        {
            long w = (long)Math.Min(Right, other.Right) - Math.Max(X, other.X);
            long h = (long)Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        public override string ToString()
        {
            return Width + "x" + Height + "+" + X + "+" + Y;
        }
    }

    /// <summary>Which window a run opens.</summary>
    public enum TestWindowKind
    {
        /// <summary>Not placed: the editor, the export, a headless run, a normal start, or a run asked for the usual full-size window.</summary>
        Untouched,
        /// <summary>A quarter of the monitor's area in its bottom-right corner, without focus and at the bottom of the stacking order
        /// (only when asked for: NV_TEST_WINDOW_ONSCREEN=1).</summary>
        Corner,
        /// <summary>The usual drawing size, placed beyond the right edge of every monitor, without focus and at the bottom of the stacking order.</summary>
        OffScreen,
    }

    public static class TestWindowPlacement
    {
        /// <summary>The window a plain start opens (the engine's default window size, the project sets none). A test window is told
        /// to keep drawing at this size.</summary>
        public const int BaseWidth = 1152, BaseHeight = 648;

        /// <summary>
        /// Which window a run gets. Only runs of the test harness that open a window (<c>--test-render</c>, <c>--s1-*</c>,
        /// not <c>--headless</c>) are placed. <paramref name="fullSize"/> (-FullSizeWindow, or NV_TEST_WINDOW_FULL=1) keeps the
        /// usual window untouched for any of them. Every other test or measuring run gets the corner window.
        /// </summary>
        public static TestWindowKind KindFor(string argLine, bool fullSize)
        {
            if (argLine == null) argLine = "";
            if (!Regex.IsMatch(argLine, "--test-render|--s1-") || Regex.IsMatch(argLine, "--headless")) return TestWindowKind.Untouched;
            return fullSize ? TestWindowKind.Untouched : TestWindowKind.Corner;
        }

        /// <summary>Which window a run gets (stub).</summary>
        public static TestWindowKind KindFor(string argLine, bool fullSize, bool exportedViewer, bool onScreen)
        {
            return KindFor(argLine, fullSize);
        }

        /// <summary>A window off every monitor (stub).</summary>
        public static PixelRect OffScreen(PixelRect[] screens, int width, int height)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        /// <summary>
        /// The test window for <paramref name="workAreas"/>[<paramref name="monitor"/>]: half the width and half the height of that
        /// work area (a quarter of its area, never less than 1 pixel each way), in its bottom-right corner. A monitor index outside
        /// the list falls back to the first entry.
        /// </summary>
        public static PixelRect Place(PixelRect[] workAreas, int monitor)
        {
            if (workAreas == null || workAreas.Length == 0) throw new ArgumentException("at least one monitor work area is needed", "workAreas");
            PixelRect area = workAreas[monitor >= 0 && monitor < workAreas.Length ? monitor : 0];
            if (area.Width < 1 || area.Height < 1) throw new ArgumentException("a monitor work area must have a positive size", "workAreas");
            int w = Math.Max(1, area.Width / 2), h = Math.Max(1, area.Height / 2);
            return new PixelRect(area.Right - w, area.Bottom - h, w, h);
        }

        /// <summary>
        /// The monitor to place a test window on: the one the foreground window covers most (by area), or the first one holding its
        /// centre when it covers none, or <paramref name="fallback"/> (the primary monitor) when there is no foreground window or it
        /// is nowhere on a monitor. <paramref name="monitors"/> are the monitors' full bounds.
        /// </summary>
        public static int MonitorOf(PixelRect[] monitors, PixelRect foreground, int fallback)
        {
            if (monitors == null || monitors.Length == 0) throw new ArgumentException("at least one monitor is needed", "monitors");
            int safe = fallback >= 0 && fallback < monitors.Length ? fallback : 0;
            if (foreground == null || foreground.Width < 1 || foreground.Height < 1) return safe;
            int best = -1; long bestArea = 0;
            for (int i = 0; i < monitors.Length; i++)
            {
                long a = monitors[i].OverlapArea(foreground);
                if (a > bestArea) { best = i; bestArea = a; }
            }
            if (best >= 0) return best;
            int cx = foreground.X + foreground.Width / 2, cy = foreground.Y + foreground.Height / 2;
            for (int i = 0; i < monitors.Length; i++)
                if (cx >= monitors[i].X && cx < monitors[i].Right && cy >= monitors[i].Y && cy < monitors[i].Bottom) return i;
            return safe;
        }

        /// <summary>
        /// The top-left corner of all the monitors together. The engine counts window positions from this corner
        /// (a monitor to the left of or above the primary one does not give negative positions), so a position on the
        /// desktop is turned into the engine's by subtracting it.
        /// </summary>
        public static PixelRect Origin(PixelRect[] screens)
        {
            if (screens == null || screens.Length == 0) throw new ArgumentException("at least one monitor is needed", "screens");
            int x = int.MaxValue, y = int.MaxValue;
            foreach (PixelRect s in screens) { x = Math.Min(x, s.X); y = Math.Min(y, s.Y); }
            return new PixelRect(x, y, 0, 0);
        }

        /// <summary>
        /// The engine arguments that open the window at <paramref name="window"/> (desktop pixels): "--resolution WxH
        /// --position X,Y", with the position counted from <see cref="Origin"/> of <paramref name="screens"/> (each
        /// monitor's full bounds, not its work area).
        /// </summary>
        public static string EngineArguments(PixelRect window, PixelRect[] screens)
        {
            PixelRect origin = Origin(screens);
            return "--resolution " + window.Width + "x" + window.Height + " --position " + (window.X - origin.X) + "," + (window.Y - origin.Y);
        }
    }

    /// <summary>
    /// Whether a run goes through crash-watch.ps1 (a small debugger of our own, so a native crash leaves a dump). Runs of
    /// the test harness (<c>--test-render</c>, <c>--s1-*</c>) have it on unless it is switched off; the editor, the export and
    /// a normal start are not touched. Lives in this file so tools\godot.ps1 and the unit tests use one decision.
    /// </summary>
    public static class CrashWatchPolicy
    {
        /// <summary>
        /// A measuring run (its timings are the result) keeps crash-watch off when this is true, because the debugger slows
        /// every exception down. Set from the measured cost of crash-watch on the render stage.
        /// </summary>
        public const bool MeasuringRunsKeepItOff = false;

        /// <summary>
        /// <paramref name="forcedOn"/> (-CrashDumps, or NV_CRASH_DUMPS=1) turns it on for any program; <paramref name="forcedOff"/>
        /// (-NoCrashDumps, or NV_CRASH_DUMPS=0) turns it off for any run and wins over forcedOn. Otherwise it is on for a
        /// harness run, except a measuring run while <see cref="MeasuringRunsKeepItOff"/> is true.
        /// </summary>
        public static bool Enabled(string argLine, bool forcedOn, bool forcedOff, bool measuring)
        {
            if (forcedOff) return false;
            if (forcedOn) return true;
            if (argLine == null) argLine = "";
            if (!Regex.IsMatch(argLine, "--test-render|--s1-")) return false;
            return !(measuring && MeasuringRunsKeepItOff);
        }
    }
}

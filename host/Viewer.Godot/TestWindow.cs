// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;

namespace Viewer.Godot;

/// <summary>
/// The window of a test or measurement run (<c>--test-render</c>, <c>--s1-*</c>). tools\godot.ps1 opens it small, in the
/// corner of a monitor (tools\TestWindowPlacement.cs), and passes <c>--nv-test-window=WxH</c>, the size a plain start
/// opens. Here the window is told to keep drawing at that full size and scale the picture down for display, and to not
/// take focus, so a run does not cover or interrupt what the person at the machine is doing. A measuring run's window
/// already has that full size (same corner, same argument): it is drawn as is, with no scaling, and only the no-focus
/// flag is set. A start without the argument (every normal start) is not touched.
/// </summary>
public static class TestWindow
{
    public const string Argument = "--nv-test-window=";

    /// <summary>The place, in desktop pixels, off every monitor, a minimised test window is brought back to (tools\godot.ps1 opens a
    /// test window minimised: the engine keeps a new window at least a third on a monitor whatever position it is given).</summary>
    public const string AtArgument = "--nv-test-window-at=";

    /// <summary>The off-screen place the argument names, or null when it is absent or malformed.</summary>
    public static Vector2I? OffScreenAt(IEnumerable<string> userArgs)
    {
        string? value = userArgs.FirstOrDefault(a => a.StartsWith(AtArgument, System.StringComparison.Ordinal))?[AtArgument.Length..];
        var parts = value?.Split(',');
        if (parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y))
            return new Vector2I(x, y);
        return null;
    }

    /// <summary>Where a minimised test window is brought back to; null brings it back to its own place.</summary>
    public static Vector2I? OffScreenTarget { get; private set; }

    /// <summary>The full drawing size the argument names, or null when it is absent or malformed.</summary>
    public static Vector2I? FullSize(IEnumerable<string> userArgs)
    {
        string? value = userArgs.FirstOrDefault(a => a.StartsWith(Argument, System.StringComparison.Ordinal))?[Argument.Length..];
        var parts = value?.Split('x');
        if (parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            && w > 0 && h > 0)
            return new Vector2I(w, h);
        return null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Whether the operating system shows the main window (WS_VISIBLE), which the engine's own visible flag does not
    /// say; null off Windows, headless, or without a native handle.</summary>
    public static bool? OsVisible()
    {
        if (!OperatingSystem.IsWindows() || DisplayServer.GetName() == "headless") return null;
        long handle = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, (int)DisplayServer.MainWindowId);
        return handle == 0 ? null : IsWindowVisible((IntPtr)handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    private const int SwShow = 5;   // SW_SHOW: shown in its place and activated
    private const int SwMostRecentNoActivate = 4;   // SW_SHOWNOACTIVATE: a minimised window is brought back to its most recent size and place, and not activated
    private const int SwShowNoActivate = 8;   // SW_SHOWNA: shown in its place, focus and the active window untouched
    private const int SwShowMinNoActivate = 7;   // SW_SHOWMINNOACTIVE: shown minimised, the active window untouched

    /// <summary>Shows the main window as it is, without activating it (Windows only).</summary>
    public static void ShowWithoutActivating() => Show(SwShowNoActivate);

    private static void Show(int command)
    {
        if (!OperatingSystem.IsWindows() || DisplayServer.GetName() == "headless") return;
        long handle = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, (int)DisplayServer.MainWindowId);
        if (handle != 0) ShowWindow((IntPtr)handle, command);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndBottom = (IntPtr)1;   // HWND_BOTTOM: below every other window
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;

    private static IntPtr MainHandle()
    {
        if (!OperatingSystem.IsWindows() || DisplayServer.GetName() == "headless") return IntPtr.Zero;
        return (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, (int)DisplayServer.MainWindowId);
    }

    /// <summary>Whether the operating system has the main window minimised; false off Windows, headless, or without a native handle.</summary>
    public static bool IsMinimized() => MainHandle() is var h && h != IntPtr.Zero && IsIconic(h);

    /// <summary>Puts the main window at the bottom of the stacking order without activating it (Windows only).</summary>
    public static void SendToBottom()
    {
        if (MainHandle() is var h && h != IntPtr.Zero) SetWindowPos(h, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

    private const int GwlExStyle = -20, WsExLayered = 0x00080000, WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 2;

    /// <summary>
    /// Brings a minimised main window back at <paramref name="at"/> (desktop pixels, off every monitor) without it ever being seen or
    /// activated: the system restores a window to where it was first placed, which the engine keeps partly on a monitor, so the window is
    /// transparent while it comes back, is moved off, and is opaque again. It stays at the bottom of the stacking order and is never made
    /// the active window (WS_EX_NOACTIVATE).
    /// </summary>
    public static void RestoreOffScreen(Vector2I at)
    {
        if (MainHandle() is var h && h != IntPtr.Zero)
        {
            int ex = GetWindowLong(h, GwlExStyle);
            SetWindowLong(h, GwlExStyle, ex | WsExLayered | WsExNoActivate);
            SetLayeredWindowAttributes(h, 0, 0, LwaAlpha);
            ShowWindow(h, SwMostRecentNoActivate);
            SetWindowPos(h, HwndBottom, at.X, at.Y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            SetWindowLong(h, GwlExStyle, (ex | WsExNoActivate) & ~WsExLayered);
        }
    }

    /// <summary>
    /// Brings a minimised main window back to its place without activating it, then puts it at the bottom of the stacking order.
    /// The engine's own restore (<c>WindowSetMode(Windowed)</c>) and SW_RESTORE both activate the window, which would take focus from
    /// the person at the machine.
    /// </summary>
    public static void RestoreWithoutActivating()
    {
        if (OffScreenTarget is { } at)
        {
            RestoreOffScreen(at);   // a test window off every monitor goes back there, never onto a monitor
            return;
        }
        if (MainHandle() is var h && h != IntPtr.Zero)
        {
            ShowWindow(h, SwMostRecentNoActivate);
            SetWindowPos(h, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    /// <summary>
    /// Keeps a test or measuring run's window where the person at the machine wants it: at the bottom of the stacking order, and, if it
    /// gets minimised (the person did it, or the system), brought back at once without taking focus. A minimised window does not draw, so a
    /// run that lost its window for a while has frames that say nothing about the scene: <see cref="TakeMark"/> tells a measuring loop,
    /// once per frame it records, whether that frame is one of them (the frame the window came back in and the two after it), so it can
    /// leave them out of its figures.
    /// </summary>
    public sealed class Keeper
    {
        private const long BottomEveryMs = 1000;
        private long _lastBottom;
        private int _marksTaken, _tail;
        private readonly long _frequency;

        public Keeper(long frequency) { _frequency = frequency; }

        /// <summary>False keeps a minimised window minimised (the test of the not-drawing handling needs a window that stays down).</summary>
        public bool RestoreEnabled { get; set; } = true;

        /// <summary>How many times the window was found minimised.</summary>
        public int Minimized { get; private set; }

        /// <summary>How many frames <see cref="TakeMark"/> has marked.</summary>
        public int MarkedFrames { get; private set; }

        /// <summary>Called every frame from the main loop.</summary>
        public void Tick(long now)
        {
            if (IsMinimized())
            {
                Minimized++;
                if (RestoreEnabled) RestoreWithoutActivating();
                _lastBottom = now;
                return;
            }
            if ((now - _lastBottom) * 1000 / _frequency >= BottomEveryMs)
            {
                _lastBottom = now;
                SendToBottom();
            }
        }

        /// <summary>Whether the frame being recorded now is one the window was not drawing in or just came back in. Call once per recorded frame.</summary>
        public bool TakeMark()
        {
            bool marked;
            if (Minimized != _marksTaken) { _marksTaken = Minimized; _tail = 2; marked = true; }
            else if (_tail > 0) { _tail--; marked = true; }
            else marked = false;
            if (marked) MarkedFrames++;
            return marked;
        }
    }

    /// <summary>
    /// A normal start ignores a no-focus setting it finds on its window. The test runs' launcher gives its window that
    /// setting through an <c>override.cfg</c> beside the executable (read by the engine before any code runs); if such a
    /// file is left in a build folder, a normal start of that build would never take focus. A start that is not a test
    /// run clears the flag, and shows the window if clearing it hid it. Returns whether it cleared the flag.
    /// </summary>
    public static bool ClearStrayNoFocus(Window window, IEnumerable<string> userArgs)
    {
        var args = userArgs.ToList();
        if (args.Contains("--test-render") || args.Any(a => a.StartsWith("--s1-", StringComparison.Ordinal)) || FullSize(args) is not null)
            return false;
        if (!window.Unfocusable)
            return false;
        window.Unfocusable = false;
        if (OsVisible() == false) Show(IsMinimized() ? SwShowMinNoActivate : SwShow);   // changing the flag rewrites the window style and can hide the window; a start that opened minimised stays so, not activated
        return true;
    }

    /// <summary>Applies the argument to <paramref name="window"/> (does nothing without it) and describes the window for the run's sidecar.</summary>
    public static Dictionary<string, object?> Apply(Window window, IEnumerable<string> userArgs)
    {
        var full = FullSize(userArgs);
        // a test window that opened minimised to stay off every monitor is brought back there before its size is read
        if (full is not null && OffScreenAt(userArgs) is { } offAt)
        {
            OffScreenTarget = offAt;
            if (IsMinimized())
                RestoreOffScreen(offAt);
        }
        if (full is { } size)
        {
            if (window.Size != size)   // a measuring run's window already has the full size: drawn as is, not scaled
            {
                window.ContentScaleSize = size;
                window.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
                window.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
            }
            // Window.FLAG_NO_FOCUS. The launcher's override.cfg usually set it already when the window was made; setting it on a
            // shown window rewrites the window style without WS_VISIBLE and the window goes hidden, so it is set only when it is
            // not set, and a window that went hidden is shown again without being activated.
            if (!window.Unfocusable)
            {
                window.Unfocusable = true;
                if (OsVisible() == false) ShowWithoutActivating();
            }
            SendToBottom();   // a window that has just opened is on top of the others; this one is not to be
        }
        var at = window.Position;
        var actual = window.Size;
        return new Dictionary<string, object?>
        {
            ["test_window"] = full is not null,
            ["size"] = $"{actual.X}x{actual.Y}",
            ["position"] = $"{at.X},{at.Y}",
            ["screen"] = window.CurrentScreen,
            ["drawn_at"] = full is { } f ? $"{f.X}x{f.Y}" : $"{actual.X}x{actual.Y}",
            ["unfocusable"] = window.Unfocusable,
        };
    }
}

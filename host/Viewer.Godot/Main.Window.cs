// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The main window's mode: how it opens (the startup choice: Maximized window, Last size and position, or Full screen), the window's size,
/// place and screen kept between runs (<see cref="WindowMemory"/>), and the Full screen and Minimize commands of the Me menu.
/// A window mode keeps the system title bar with its minimize, maximize and close boxes and the taskbar button; full screen is the engine's
/// borderless full screen (not the exclusive one), so Alt+Tab and the Windows key keep working. All of it applies in a normal run only: a
/// test, render or automation launch places its own small window and never reaches this code (see <see cref="DisplayChosenOnCommandLine"/>).
/// </summary>
public partial class Main
{
    private SettingsService? _windowSettings;
    private bool _windowSeenMaximized;
    private WindowRect _windowSeenRect, _windowSeenScreen;
    private bool _windowPending;
    private ulong _windowChangedAtMs;
    private Window.ModeEnum _modeBeforeFullscreen = Window.ModeEnum.Maximized;

    /// <summary>The screens' whole areas, the primary screen first.</summary>
    private static List<WindowRect> ScreenRects()
    {
        var screens = new List<WindowRect>();
        int primary = DisplayServer.GetPrimaryScreen();
        for (int i = 0; i < DisplayServer.GetScreenCount(); i++)
        {
            var at = DisplayServer.ScreenGetPosition(i);
            var size = DisplayServer.ScreenGetSize(i);
            var rect = new WindowRect(at.X, at.Y, size.X, size.Y);
            if (i == primary)
                screens.Insert(0, rect);
            else
                screens.Add(rect);
        }
        return screens;
    }

    private static WindowRect ScreenOf(Window window)
    {
        var at = DisplayServer.ScreenGetPosition(window.CurrentScreen);
        var size = DisplayServer.ScreenGetSize(window.CurrentScreen);
        return new WindowRect(at.X, at.Y, size.X, size.Y);
    }

    private static WindowRect PlaceOf(Window window) => new(window.Position.X, window.Position.Y, window.Size.X, window.Size.Y);

    /// <summary>Opens the window as the startup choice says, in _Ready, so before the first frame is drawn and the window never shows another
    /// mode first. A start that opened minimized stays so: maximizing it would put it on a monitor and make it the active window.</summary>
    private void ApplyStartupWindow(SettingsService settings)
    {
        if (!_interactive || DisplayChosenOnCommandLine())
            return;
        var window = GetWindow();
        if (window.Mode == Window.ModeEnum.Minimized)
            return;
        var plan = DisplayPlan.For(settings.GetText(Settings.WindowModeKey));
        bool maximized = false;
        if (plan.Fullscreen)
            window.Mode = Window.ModeEnum.Fullscreen;
        else
        {
            var start = WindowMemory.Plan(settings, ScreenRects(), plan.Start);
            if (window.Mode != Window.ModeEnum.Windowed)
                window.Mode = Window.ModeEnum.Windowed;   // the place and size can only be set on a window that is not maximized
            if (start.Rect is { } rect)
            {
                window.Position = new Vector2I(rect.X, rect.Y);
                window.Size = new Vector2I(rect.Width, rect.Height);
            }
            if (start.Maximized)
                window.Mode = Window.ModeEnum.Maximized;
            maximized = start.Maximized;
        }
        _windowSettings = settings;
        _windowSeenMaximized = window.Mode == Window.ModeEnum.Maximized;
        _windowSeenRect = PlaceOf(window);
        _windowSeenScreen = ScreenOf(window);
        _windowPending = !settings.Contains(WindowMemory.MaximizedKey);   // a first run is kept at once
        Log.Info("startup.window", CoreLog.F("choice", plan.Start.ToString()), CoreLog.F("maximized", maximized),
            CoreLog.F("fullscreen", plan.Fullscreen), CoreLog.F("frames-drawn", Engine.GetFramesDrawn()),
            CoreLog.F("screen", _windowSeenScreen.ToString()), CoreLog.F("place", _windowSeenRect.ToString()), CoreLog.F("primary", ScreenRects()[0].ToString()));
    }

    /// <summary>The choice changed in Preferences: full screen is entered or left now; a window choice leaves the window as it is.</summary>
    private void ApplyWindowChoice(SettingsService settings)
    {
        if (!_interactive || DisplayChosenOnCommandLine())
            return;
        var window = GetWindow();
        if (window.Mode == Window.ModeEnum.Minimized)
            return;
        bool wantsFullscreen = DisplayPlan.For(settings.GetText(Settings.WindowModeKey)).Fullscreen;
        if (wantsFullscreen != (window.Mode == Window.ModeEnum.Fullscreen))
            ToggleFullscreen();
    }

    /// <summary>Switches between full screen and a window (Me > Full screen, Ctrl+Alt+F). Leaving full screen goes back to what the window was
    /// before it, or maximized when the viewer opened in full screen. It is not the startup choice, which stays as it was set.</summary>
    internal void ToggleFullscreen()
    {
        var window = GetWindow();
        if (window.Mode == Window.ModeEnum.Fullscreen)
        {
            window.Mode = _modeBeforeFullscreen;
            return;
        }
        _modeBeforeFullscreen = window.Mode == Window.ModeEnum.Windowed ? Window.ModeEnum.Windowed : Window.ModeEnum.Maximized;
        window.Mode = Window.ModeEnum.Fullscreen;
    }

    /// <summary>Me > Minimize: the window goes to the taskbar; its button brings it back.</summary>
    internal void MinimizeWindow() => GetWindow().Mode = Window.ModeEnum.Minimized;

    /// <summary>Keeps the window's size, place, screen and maximized state once it has stopped changing for half a second (and at once on close).
    /// Full screen and minimized windows are not kept: their size and place are not the person's choice.</summary>
    private void UpdateWindowMemory(bool now = false)
    {
        if (_windowSettings is not { } settings)
            return;
        var window = GetWindow();
        if (window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen or Window.ModeEnum.Minimized)
            return;
        bool maximized = window.Mode == Window.ModeEnum.Maximized;
        var rect = PlaceOf(window);
        var screen = ScreenOf(window);
        if (maximized != _windowSeenMaximized || screen != _windowSeenScreen || (!maximized && rect != _windowSeenRect))
        {
            _windowSeenMaximized = maximized;
            _windowSeenScreen = screen;
            _windowSeenRect = rect;
            _windowPending = true;
            _windowChangedAtMs = Time.GetTicksMsec();
        }
        if (_windowPending && (now || Time.GetTicksMsec() - _windowChangedAtMs >= 500))
        {
            _windowPending = false;
            WindowMemory.Record(settings, maximized, rect, screen);
        }
    }
}

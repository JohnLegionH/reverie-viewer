// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>A window's place and size in screen pixels.</summary>
public readonly record struct WindowRect(int X, int Y, int Width, int Height);

/// <summary>How the window should open: maximized, or at a place and size.</summary>
public readonly record struct WindowStart(bool Maximized, WindowRect? Rect);

/// <summary>
/// The window's size, place and maximized state, kept between runs the way the official viewer keeps them (WindowWidth, WindowHeight,
/// WindowX, WindowY and WindowMaximized in its settings; indra/newview/llviewerwindow.cpp, LLViewerWindow::reshape, and
/// llappviewer.cpp, LLAppViewer::initWindow and the saving of the window place at exit): the size and place are kept while the window
/// is not maximized, and the maximized state always. The official defaults are a 1,024 by 738 window at 10, 10. The official viewer
/// opens maximized on a first run; so does this one, and a first run is a settings file that has no maximized entry yet.
/// The screen the window is on is kept too. A kept screen that is gone, or a kept place that no screen shows (a screen was unplugged),
/// would leave the window out of reach, so it opens maximized on the primary screen; the official source does not check this, the
/// operating system's window code does there.
/// </summary>
public static class WindowMemory
{
    public const string MaximizedKey = "window_maximized", XKey = "window_x", YKey = "window_y", WidthKey = "window_width", HeightKey = "window_height",
        MonitorXKey = "window_monitor_x", MonitorYKey = "window_monitor_y";

    /// <summary>The official defaults (settings.xml: WindowX, WindowY, WindowWidth, WindowHeight).</summary>
    public const int DefaultX = 10, DefaultY = 10, DefaultWidth = 1024, DefaultHeight = 738;

    /// <summary>A kept size smaller than this is taken for a damaged setting.</summary>
    public const int MinWidth = 320, MinHeight = 240;

    // the part of the title bar that has to show on a screen for the window to be movable
    private const int ReachableWidth = 64, ReachableHeight = 16, TitleBarHeight = 32;

    /// <summary>
    /// How the window opens, for the startup choice. <paramref name="screens"/> are the screens' whole areas, the primary screen first.
    /// Maximized window: always maximized. Last size and position: as it was left. In both, the window opens on the screen it was kept on;
    /// when that screen is gone, or a window left small was kept at a place no screen shows, it opens maximized on the primary screen.
    /// </summary>
    public static WindowStart Plan(SettingsService settings, IReadOnlyList<WindowRect> screens, WindowStartMode mode)
    {
        bool kept = settings.Contains(MaximizedKey) ? settings.Get(Settings.WindowMaximized) : true;
        bool maximized = mode != WindowStartMode.Last || kept;
        int w = settings.Get(Settings.WindowWidth), h = settings.Get(Settings.WindowHeight);
        bool sizeKept = w >= MinWidth && h >= MinHeight;
        var wanted = sizeKept
            ? new WindowRect(settings.Get(Settings.WindowX), settings.Get(Settings.WindowY), w, h)
            : new WindowRect(DefaultX, DefaultY, DefaultWidth, DefaultHeight);
        if (screens.Count == 0)
            return new WindowStart(maximized, wanted);
        var primary = screens[0];
        var home = HomeOf(wanted, screens);
        if (!maximized)
            return home is { } here ? new WindowStart(false, CutTo(wanted, here)) : new WindowStart(true, DefaultOn(primary, wanted));
        WindowRect target;
        if (settings.Contains(MonitorXKey) && settings.Contains(MonitorYKey))
        {
            int mx = settings.Get(Settings.WindowMonitorX), my = settings.Get(Settings.WindowMonitorY);
            target = screens.FirstOrDefault(s => s.X == mx && s.Y == my) is { Width: > 0 } found ? found : primary;
        }
        else
            target = home ?? primary;
        return new WindowStart(true, Shows(wanted, target) ? CutTo(wanted, target) : DefaultOn(target, wanted));
    }

    /// <summary>Keeps what the settings hold. True when something was written; false when it all was kept already.
    /// While maximized only the maximized state and the screen are kept (the size and place stay those of the window before it was maximized).
    /// <paramref name="monitor"/> is the whole area of the screen the window is on, when known.</summary>
    public static bool Record(SettingsService settings, bool maximized, WindowRect rect, WindowRect? monitor = null)
    {
        bool wrote = false;
        if (!settings.Contains(MaximizedKey) || settings.Get(Settings.WindowMaximized) != maximized)
        {
            settings.Store(Settings.WindowMaximized, maximized);
            wrote = true;
        }
        if (monitor is { } screen)
        {
            wrote |= Keep(settings, Settings.WindowMonitorX, screen.X);
            wrote |= Keep(settings, Settings.WindowMonitorY, screen.Y);
        }
        if (maximized)
            return wrote;
        wrote |= Keep(settings, Settings.WindowX, rect.X);
        wrote |= Keep(settings, Settings.WindowY, rect.Y);
        wrote |= Keep(settings, Settings.WindowWidth, rect.Width);
        wrote |= Keep(settings, Settings.WindowHeight, rect.Height);
        return wrote;
    }

    private static bool Keep(SettingsService settings, Setting<int> setting, int value)
    {
        if (settings.Contains(setting.Key) && settings.Get(setting) == value)
            return false;
        settings.Store(setting, value);
        return true;
    }

    /// <summary>The screen that shows the window's title bar well enough to move it (the one showing most of it), or null when none does.</summary>
    private static WindowRect? HomeOf(WindowRect wanted, IReadOnlyList<WindowRect> screens)
    {
        var strip = new WindowRect(wanted.X, wanted.Y, wanted.Width, TitleBarHeight);
        WindowRect? home = null;
        long best = 0;
        foreach (var screen in screens)
        {
            var (ow, oh) = Overlap(strip, screen);
            if (ow >= ReachableWidth && oh >= ReachableHeight && (long)ow * oh > best)
            {
                best = (long)ow * oh;
                home = screen;
            }
        }
        return home;
    }

    private static bool Shows(WindowRect wanted, WindowRect screen) => HomeOf(wanted, [screen]) is not null;

    private static WindowRect CutTo(WindowRect wanted, WindowRect screen) =>
        new(wanted.X, wanted.Y, Math.Min(wanted.Width, screen.Width), Math.Min(wanted.Height, screen.Height));

    /// <summary>The official default place and the kept size (cut to the screen), on a screen: where a window is put when it has no place that shows.</summary>
    private static WindowRect DefaultOn(WindowRect screen, WindowRect wanted) =>
        new(screen.X + DefaultX, screen.Y + DefaultY, Math.Min(wanted.Width, screen.Width), Math.Min(wanted.Height, screen.Height));

    private static (int Width, int Height) Overlap(WindowRect a, WindowRect b)
    {
        int w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        int h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return (Math.Max(0, w), Math.Max(0, h));
    }
}

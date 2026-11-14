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
/// A kept place that no screen shows (a screen was unplugged) would leave the window out of reach, so the official default place
/// replaces it; the official source does not check this, the operating system's window code does there.
/// </summary>
public static class WindowMemory
{
    public const string MaximizedKey = "window_maximized", XKey = "window_x", YKey = "window_y", WidthKey = "window_width", HeightKey = "window_height";

    /// <summary>The official defaults (settings.xml: WindowX, WindowY, WindowWidth, WindowHeight).</summary>
    public const int DefaultX = 10, DefaultY = 10, DefaultWidth = 1024, DefaultHeight = 738;

    /// <summary>A kept size smaller than this is taken for a damaged setting.</summary>
    public const int MinWidth = 320, MinHeight = 240;

    // the part of the title bar that has to show on a screen for the window to be movable
    private const int ReachableWidth = 64, ReachableHeight = 16, TitleBarHeight = 32;

    public static WindowStart Plan(SettingsService settings, IReadOnlyList<WindowRect> screens)
    {
        bool maximized = settings.Contains(MaximizedKey) ? settings.Get(Settings.WindowMaximized) : true;
        int w = settings.Get(Settings.WindowWidth), h = settings.Get(Settings.WindowHeight);
        bool sizeKept = w >= MinWidth && h >= MinHeight;
        var wanted = sizeKept
            ? new WindowRect(settings.Get(Settings.WindowX), settings.Get(Settings.WindowY), w, h)
            : new WindowRect(DefaultX, DefaultY, DefaultWidth, DefaultHeight);
        return new WindowStart(maximized, screens.Count == 0 ? wanted : Fit(wanted, screens));
    }

    /// <summary>Keeps what the settings hold. True when something was written; false when it all was kept already.
    /// While maximized only the maximized state is kept (the size and place stay those of the window before it was maximized).</summary>
    public static bool Record(SettingsService settings, bool maximized, WindowRect rect)
    {
        bool wrote = false;
        if (!settings.Contains(MaximizedKey) || settings.Get(Settings.WindowMaximized) != maximized)
        {
            settings.Store(Settings.WindowMaximized, maximized);
            wrote = true;
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

    private static WindowRect Fit(WindowRect wanted, IReadOnlyList<WindowRect> screens)
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
        var primary = screens[0];
        var onto = home ?? primary;
        int width = Math.Min(wanted.Width, onto.Width), height = Math.Min(wanted.Height, onto.Height);
        return home is null
            ? new WindowRect(primary.X + DefaultX, primary.Y + DefaultY, width, height)
            : new WindowRect(wanted.X, wanted.Y, width, height);
    }

    private static (int Width, int Height) Overlap(WindowRect a, WindowRect b)
    {
        int w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        int h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return (Math.Max(0, w), Math.Max(0, h));
    }
}

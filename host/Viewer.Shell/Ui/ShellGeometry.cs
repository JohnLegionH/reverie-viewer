// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>A size in logical pixels. Plain numbers, so the shell's layout is tested without the engine.</summary>
public readonly record struct UiSize(float W, float H);

/// <summary>A rectangle in logical pixels, origin at the window's top-left.</summary>
public readonly record struct UiRect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;

    /// <summary>True when <paramref name="inner"/> lies wholly inside this rectangle.</summary>
    public bool Encloses(UiRect inner) => inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;

    public bool Overlaps(UiRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>The shell frame's fixed measures.</summary>
/// <param name="MenuBarHeight">The menu bar's height along the top.</param>
/// <param name="ToolbarHeight">The toolbar's height along the bottom, when it has any button.</param>
/// <param name="MinWindow">The smallest window (in pixels at 100 %) the interface is designed for. The layout itself works at any size.</param>
/// <param name="NavBarHeight">The navigation bar's height under the menu bar, when it is shown.</param>
/// <param name="FavouritesHeight">The favourites strip's height under the navigation bar, when it is shown.</param>
/// <param name="ChatBarHeight">The chat bar's height above the toolbar, when it is shown.</param>
public sealed record ShellMetrics(float MenuBarHeight, float ToolbarHeight, UiSize MinWindow, float NavBarHeight = 32, float FavouritesHeight = 22, float ChatBarHeight = 34)
{
    public static ShellMetrics Default { get; } = new(26, 44, new UiSize(640, 480));
}

/// <summary>The heights the bars need for their content, as the drawing side measures them (a bar's content can be taller than the design height).</summary>
public readonly record struct BarHeights(float Menu, float Nav, float Chat, float Toolbar);

/// <summary>Which optional rows of the frame are shown. A row that is not shown takes no room.</summary>
public readonly record struct ChromeRows(bool Toolbar, bool NavBar = false, bool Favourites = false, bool ChatBar = false);

/// <summary>Where each region of the window frame goes.</summary>
/// <param name="Window">The window as laid out (never lower than the fixed rows themselves).</param>
/// <param name="MenuBar">Along the top, full width.</param>
/// <param name="Toolbar">Along the bottom, full width; zero-high when no button is registered.</param>
/// <param name="Main">Everything between: the login screen, the world, and where floaters live.</param>
/// <param name="NavBar">Under the menu bar; zero-high when not shown.</param>
/// <param name="Favourites">Under the navigation bar; zero-high when not shown.</param>
/// <param name="ChatBar">Above the toolbar; zero-high when not shown.</param>
public sealed record ShellRegions(UiRect Window, UiRect MenuBar, UiRect Toolbar, UiRect Main, UiRect NavBar = default, UiRect Favourites = default, UiRect ChatBar = default);

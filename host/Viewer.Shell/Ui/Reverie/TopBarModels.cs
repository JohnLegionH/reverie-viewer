// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell;

/// <summary>The interface size: one scale for the whole interface, applied once at the root.</summary>
public static class InterfaceSize
{
    /// <summary>The range the interface size takes, in percent.</summary>
    public const int MinPercent = 75, MaxPercent = 200;

    /// <summary>The default for a screen: its display scale (dots per inch over 96), within the range. No reading (0 or less) is 100.</summary>
    public static int DefaultFor(float dpi) => 0;

    /// <summary>The person's stored size when there is one (within the range), else the screen's default.</summary>
    public static int Effective(int? stored, float dpi) => 0;
}

/// <summary>Follows the screen the window is on: a move to a screen with another scale gives a new size, unless the person set their own.</summary>
public sealed class InterfaceSizeTracker(int? stored)
{
    public int? Stored { get; private set; } = stored;

    /// <summary>The size in force (0 before the first reading).</summary>
    public int Current { get; private set; }

    /// <summary>A reading of the window's screen and its dots per inch. True when the size in force changed.</summary>
    public bool Observe(int screen, float dpi) => false;

    /// <summary>The person sets their own size (null: follow the screen again). True when the size in force changed.</summary>
    public bool SetStored(int? percent) => false;
}

/// <summary>
/// Which menu of the menu bar is open and which title has the keyboard. A menu opens on a click only; once one is open, the pointer
/// moving onto another title opens that one instead (as the official viewer's menu bar does); with nothing open, moving over the
/// titles opens nothing. Left and Right move between titles and carry an open menu along.
/// </summary>
public sealed class MenuBarState(int titles)
{
    public int Titles { get; } = titles;

    /// <summary>The open menu, or null.</summary>
    public int? Open { get; private set; }

    /// <summary>The title that has the keyboard.</summary>
    public int Focused { get; private set; }

    /// <summary>A click on a title: opens its menu, or closes it when it is the one open.</summary>
    public void Click(int title) { }

    /// <summary>The pointer is over a title.</summary>
    public void Hover(int title) { }

    /// <summary>Left (-1) or Right (+1), wrapping round the ends.</summary>
    public void Arrow(int step) { }

    /// <summary>Enter, Space or Down on the focused title: opens its menu.</summary>
    public void Activate() { }

    /// <summary>Escape, a click outside, or an entry chosen.</summary>
    public void Close() { }
}

/// <summary>What the location field shows in display mode: the parcel (only when it has a name of its own), the region once, and the coordinates.</summary>
public sealed record LocationParts(string? Parcel, string Region, string Coordinates);

/// <summary>A place typed into the location field: a region name, with a position when one was given.</summary>
public sealed record LocationAddress(string Region, Vector3? Position);

public static class LocationField
{
    /// <summary>The display-mode parts, or null without a region.</summary>
    public static LocationParts? Display(string? region, string? parcel, Vector3 position, float speed) => new(null, "", "");

    /// <summary>Reads "Region name" or "Region name/x/y/z" (y and z may be left out). Null when the text is no place.</summary>
    public static LocationAddress? Parse(string? text) => new("", null);
}

/// <summary>The top bar's rows: the three zones in one row while they fit, else the location zone on its own row under the menus.</summary>
public static class TopBarLayout
{
    /// <summary>The bar's height for one row, and the gap between zones (logical pixels).</summary>
    public const float RowHeight = 38, ZoneGap = 12;

    /// <summary>True when the location zone goes to its own row.</summary>
    public static bool LocationOnOwnRow(float width, float left, float centre, float right) => false;

    /// <summary>The bar's height for the width.</summary>
    public static float Height(float width, float left, float centre, float right) => 0;
}

/// <summary>The operating system's window title.</summary>
public static class WindowTitleText
{
    /// <summary>"Reverie Viewer" before login; "Reverie Viewer - avatar - grid" once logged in.</summary>
    public static string For(StringTable strings, string? avatarName, string? gridName) => "";
}

/// <summary>Where the avatar was before each teleport, newest last, for the Back button.</summary>
public sealed class TeleportHistory
{
    public const int Capacity = 20;

    public int Count => 0;

    /// <summary>The avatar left <paramref name="from"/> by teleport.</summary>
    public void Departed(LocationAddress from) { }

    /// <summary>The place Back goes to (and removes it), or null when there is none.</summary>
    public LocationAddress? Back() => null;

    public LocationAddress? Peek => null;
}

/// <summary>What Check for updates found.</summary>
public enum UpdateOutcome { UpToDate, Available, CouldNotCheck }

public sealed record UpdateResult(UpdateOutcome Outcome, string? Version = null, string? Url = null);

/// <summary>
/// Check for updates, run only when the person asks: one request for the project's latest release, its version compared with this
/// build's. No background checks.
/// </summary>
public static class UpdateCheck
{
    public const string LatestReleaseApi = "https://api.github.com/repos/JohnLegionH/reverie-viewer/releases/latest";

    /// <summary>Reads the answer to the request: its status code and body.</summary>
    public static UpdateResult Interpret(int status, string? body, string currentVersion) => new(UpdateOutcome.UpToDate);

    /// <summary>Asks through <paramref name="get"/> (url to status and body; it may throw) and reads the answer.</summary>
    public static Task<UpdateResult> RunAsync(Func<string, Task<(int Status, string? Body)>> get, string currentVersion) =>
        Task.FromResult(new UpdateResult(UpdateOutcome.UpToDate));

    /// <summary>Compares two versions ("v0.2.0", "0.1.0 (early preview)"): below 0, 0 or above 0. Null when either is not a version.</summary>
    public static int? Compare(string a, string b) => 0;
}

/// <summary>A window's remembered place and size inside the viewer, kept per window and pulled back on-screen.</summary>
public readonly record struct ChromeRect(float X, float Y, float W, float H);

public static class WindowChromePlace
{
    /// <summary>How much of the title bar must stay on screen to be grabbed.</summary>
    public const float Reachable = 48, TitleBarHeight = 42;

    /// <summary>Where a window opens: its remembered rectangle pulled on-screen, else its default size centred.</summary>
    public static ChromeRect Open(ChromeRect? remembered, float defaultW, float defaultH, float minW, float minH, float viewW, float viewH) => default;

    /// <summary>A rectangle pulled back so its whole title bar is inside the view, and no larger than the view.</summary>
    public static ChromeRect PullOnScreen(ChromeRect r, float minW, float minH, float viewW, float viewH) => default;

    /// <summary>The remembered rectangle as settings text, and read back (null when the text is not one).</summary>
    public static string Write(ChromeRect r) => "";

    public static ChromeRect? Read(string? text) => null;
}

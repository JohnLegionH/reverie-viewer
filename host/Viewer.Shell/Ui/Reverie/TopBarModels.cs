// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Viewer.Shell;

/// <summary>The interface size: one scale for the whole interface, applied once at the root.</summary>
public static class InterfaceSize
{
    /// <summary>The range the interface size takes, in percent.</summary>
    public const int MinPercent = 75, MaxPercent = 200;

    /// <summary>The default for a screen: its display scale (dots per inch over 96), within the range. No reading (0 or less) is 100.</summary>
    public static int DefaultFor(float dpi) =>
        dpi > 0 ? Math.Clamp((int)MathF.Round(dpi * 100f / 96f), MinPercent, MaxPercent) : 100;

    /// <summary>The person's stored size when there is one (within the range), else the screen's default.</summary>
    public static int Effective(int? stored, float dpi) =>
        stored is { } s ? Math.Clamp(s, MinPercent, MaxPercent) : DefaultFor(dpi);
}

/// <summary>Follows the screen the window is on: a move to a screen with another scale gives a new size, unless the person set their own.</summary>
public sealed class InterfaceSizeTracker(int? stored)
{
    private float _dpi;

    public int? Stored { get; private set; } = stored;

    /// <summary>The size in force (0 before the first reading).</summary>
    public int Current { get; private set; }

    /// <summary>A reading of the window's screen and its dots per inch. True when the size in force changed.</summary>
    public bool Observe(int screen, float dpi)
    {
        _dpi = dpi;
        return Settle();
    }

    /// <summary>The person sets their own size (null: follow the screen again). True when the size in force changed.</summary>
    public bool SetStored(int? percent)
    {
        Stored = percent;
        return Settle();
    }

    private bool Settle()
    {
        int next = InterfaceSize.Effective(Stored, _dpi);
        if (next == Current)
            return false;
        Current = next;
        return true;
    }
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
    public void Click(int title)
    {
        if (title < 0 || title >= Titles)
            return;
        Focused = title;
        Open = Open == title ? null : title;
    }

    /// <summary>The pointer is over a title.</summary>
    public void Hover(int title)
    {
        if (Open is null || title < 0 || title >= Titles)
            return;
        Focused = title;
        Open = title;
    }

    /// <summary>Left (-1) or Right (+1), wrapping round the ends.</summary>
    public void Arrow(int step)
    {
        if (Titles == 0)
            return;
        Focused = ((Focused + Math.Sign(step)) % Titles + Titles) % Titles;
        if (Open is not null)
            Open = Focused;
    }

    /// <summary>Enter, Space or Down on the focused title: opens its menu.</summary>
    public void Activate()
    {
        if (Titles > 0)
            Open = Focused;
    }

    /// <summary>Escape, a click outside, or an entry chosen.</summary>
    public void Close() => Open = null;
}

/// <summary>What the location field shows in display mode: the parcel (only when it has a name of its own), the region once, and the coordinates.</summary>
public sealed record LocationParts(string? Parcel, string Region, string Coordinates);

/// <summary>A place typed into the location field: a region name, with a position when one was given.</summary>
public sealed record LocationAddress(string Region, Vector3? Position);

public static class LocationField
{
    /// <summary>The display-mode parts, or null without a region. The position is rounded as the location bar rounds it (<see cref="LocationText"/>).</summary>
    public static LocationParts? Display(string? region, string? parcel, Vector3 position, float speed)
    {
        if (string.IsNullOrWhiteSpace(region))
            return null;
        string text = LocationText.Build(region, null, position, speed);
        int open = text.LastIndexOf(" (", StringComparison.Ordinal);
        string coords = text[(open + 2)..^1];
        string name = region.Trim();
        string? own = string.IsNullOrWhiteSpace(parcel) || string.Equals(parcel.Trim(), name, StringComparison.OrdinalIgnoreCase) ? null : parcel.Trim();
        return new LocationParts(own, name, coords);
    }

    /// <summary>Reads "Region name" or "Region name/x/y/z" (y and z may be left out), also as a secondlife:// or hop:// address. Null
    /// when the text is no place: empty, no region, a coordinate that is not a number or is off the region (x and y 0 to 256, z 0 to
    /// 4096), or more than three coordinates.</summary>
    public static LocationAddress? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string t = text.Trim();
        if (t.StartsWith("hop://", StringComparison.OrdinalIgnoreCase))
        {
            int slash = t.IndexOf('/', 6);   // the grid's host and port come first
            if (slash < 0)
                return null;
            t = t[(slash + 1)..];
        }
        else if (t.StartsWith("secondlife://", StringComparison.OrdinalIgnoreCase))
            t = t["secondlife://".Length..];
        string[] parts = t.Split('/');
        if (parts.Length > 4)
            return null;
        string region = Uri.UnescapeDataString(parts[0]).Trim();
        if (region.Length == 0)
            return null;
        if (parts.Length == 1)
            return new LocationAddress(region, null);
        var numbers = new float[3];
        float[] limits = [256, 256, 4096];
        for (int i = 1; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || v < 0 || v > limits[i - 1])
                return null;
            numbers[i - 1] = v;
        }
        return new LocationAddress(region, new Vector3(numbers[0], numbers[1], numbers[2]));
    }
}

/// <summary>The top bar's rows: the three zones in one row while they fit, else the location zone on its own row under the menus.</summary>
public static class TopBarLayout
{
    /// <summary>The bar's height for one row, and the gap between zones (logical pixels).</summary>
    public const float RowHeight = 38, ZoneGap = 12;

    /// <summary>True when the location zone goes to its own row.</summary>
    public static bool LocationOnOwnRow(float width, float left, float centre, float right) =>
        left + centre + right + 2 * ZoneGap > width;

    /// <summary>The bar's height for the width.</summary>
    public static float Height(float width, float left, float centre, float right) =>
        LocationOnOwnRow(width, left, centre, right) ? 2 * RowHeight : RowHeight;
}

/// <summary>The operating system's window title.</summary>
public static class WindowTitleText
{
    /// <summary>"Reverie Viewer" before login; "Reverie Viewer - avatar - grid" once logged in. Control characters become spaces.</summary>
    public static string For(StringTable strings, string? avatarName, string? gridName)
    {
        string product = strings.Get("brand.product");
        string avatar = Clean(avatarName);
        if (avatar.Length == 0)
            return product;
        string grid = Clean(gridName);
        return grid.Length == 0 ? $"{product} - {avatar}" : $"{product} - {avatar} - {grid}";
    }

    private static string Clean(string? text) =>
        string.Join(' ', new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Where the avatar was before each teleport, newest last, for the Back button.</summary>
public sealed class TeleportHistory
{
    public const int Capacity = 20;

    private readonly List<LocationAddress> _places = new();

    public int Count => _places.Count;

    /// <summary>Raised when a place is added or taken.</summary>
    public event Action? Changed;

    /// <summary>The avatar left <paramref name="from"/> by teleport.</summary>
    public void Departed(LocationAddress from)
    {
        _places.Add(from);
        if (_places.Count > Capacity)
            _places.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>The place Back goes to (and removes it), or null when there is none.</summary>
    public LocationAddress? Back()
    {
        if (_places.Count == 0)
            return null;
        var last = _places[^1];
        _places.RemoveAt(_places.Count - 1);
        Changed?.Invoke();
        return last;
    }

    public LocationAddress? Peek => _places.Count == 0 ? null : _places[^1];
}

/// <summary>What Check for updates found.</summary>
public enum UpdateOutcome { UpToDate, Available, CouldNotCheck }

public sealed record UpdateResult(UpdateOutcome Outcome, string? Version = null, string? Url = null);

/// <summary>
/// Check for updates, run only when the person asks: one request for the project's latest release (GitHub's releases API), its
/// version compared with this build's. No background checks.
/// </summary>
public static class UpdateCheck
{
    public const string LatestReleaseApi = "https://api.github.com/repos/JohnLegionH/reverie-viewer/releases/latest";

    /// <summary>Reads the answer to the request: its status code and body.</summary>
    public static UpdateResult Interpret(int status, string? body, string currentVersion)
    {
        if (status != 200 || body is null)
            return new(UpdateOutcome.CouldNotCheck);
        JsonObject? o;
        try { o = JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return new(UpdateOutcome.CouldNotCheck); }
        string? tag = o?["tag_name"] is JsonValue tv && tv.TryGetValue(out string? s) ? s : null;
        string? url = o?["html_url"] is JsonValue uv && uv.TryGetValue(out string? u) ? u : null;
        if (tag is null || Compare(tag, currentVersion) is not { } order)
            return new(UpdateOutcome.CouldNotCheck);
        return order > 0 ? new(UpdateOutcome.Available, Numbers(tag), url) : new(UpdateOutcome.UpToDate, Numbers(tag), url);
    }

    /// <summary>Asks through <paramref name="get"/> (url to status and body; it may throw) and reads the answer.</summary>
    public static async Task<UpdateResult> RunAsync(Func<string, Task<(int Status, string? Body)>> get, string currentVersion)
    {
        try
        {
            var (status, body) = await get(LatestReleaseApi).ConfigureAwait(false);
            return Interpret(status, body, currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(UpdateOutcome.CouldNotCheck);
        }
    }

    /// <summary>Compares two versions ("v0.2.0", "0.1.0 (early preview)"): below 0, 0 or above 0. Null when either is not a version.
    /// Missing parts count as 0.</summary>
    public static int? Compare(string a, string b)
    {
        if (Parts(a) is not { } x || Parts(b) is not { } y)
            return null;
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
                return c;
        }
        return 0;
    }

    private static string Numbers(string version) => string.Join('.', Parts(version)!);

    private static int[]? Parts(string version)
    {
        string t = version.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
            t = t[1..];
        int end = 0;
        while (end < t.Length && (char.IsAsciiDigit(t[end]) || t[end] == '.'))
            end++;
        var pieces = t[..end].Split('.');
        if (end == 0 || pieces.Any(p => p.Length == 0))
            return null;
        return pieces.Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1).ToArray() is var n2 && n2.All(n => n >= 0) ? n2 : null;
    }
}

/// <summary>A window's remembered place and size inside the viewer, kept per window and pulled back on-screen.</summary>
public readonly record struct ChromeRect(float X, float Y, float W, float H);

public static class WindowChromePlace
{
    /// <summary>The title bar's height (the part that must stay on screen to be grabbed).</summary>
    public const float TitleBarHeight = 42;

    /// <summary>Where a window opens: its remembered rectangle pulled on-screen, else its default size centred.</summary>
    public static ChromeRect Open(ChromeRect? remembered, float defaultW, float defaultH, float minW, float minH, float viewW, float viewH)
    {
        if (remembered is { } r)
            return PullOnScreen(r, minW, minH, viewW, viewH);
        float w = Math.Min(Math.Max(defaultW, minW), viewW), h = Math.Min(Math.Max(defaultH, minH), viewH);
        return new ChromeRect(MathF.Floor((viewW - w) / 2), MathF.Floor((viewH - h) / 2), w, h);
    }

    /// <summary>A rectangle pulled back so its whole title bar is inside the view, no smaller than the minimum and no larger than the view.</summary>
    public static ChromeRect PullOnScreen(ChromeRect r, float minW, float minH, float viewW, float viewH)
    {
        float w = Math.Min(Math.Max(r.W, minW), viewW), h = Math.Min(Math.Max(r.H, minH), viewH);
        float x = Math.Clamp(r.X, 0, Math.Max(0, viewW - w));
        float y = Math.Clamp(r.Y, 0, Math.Max(0, viewH - TitleBarHeight));
        return new ChromeRect(x, y, w, h);
    }

    /// <summary>The remembered rectangle as settings text, and read back (null when the text is not one).</summary>
    public static string Write(ChromeRect r) =>
        string.Join(',', new[] { r.X, r.Y, r.W, r.H }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    public static ChromeRect? Read(string? text)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length != 4)
            return null;
        var v = new float[4];
        for (int i = 0; i < 4; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
                return null;
        return v[2] > 0 && v[3] > 0 ? new ChromeRect(v[0], v[1], v[2], v[3]) : null;
    }
}

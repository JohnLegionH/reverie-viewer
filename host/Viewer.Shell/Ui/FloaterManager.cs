// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The edges of a window a resize drag moves (a corner is two of them).</summary>
[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>A floating panel the shell can open and close: its id, the string-table key of its title, and its sizes.</summary>
public sealed record FloaterSpec(string Id, string TitleKey, UiSize DefaultSize, UiSize MinSize, bool Resizable = true);

/// <summary>One open floater: where it is, its place in the stacking order (higher is nearer), and whether it has the focus.</summary>
public sealed record FloaterState(string Id, UiRect Rect, int Z, bool Focused);

/// <summary>Where floaters were last placed. The shell keeps them in memory; a settings service can keep them between runs.</summary>
public interface IFloaterPlaces
{
    UiRect? Get(string id);
    void Set(string id, UiRect rect);
    /// <summary>Writes the places changed since the last call where they are kept between runs (nothing for places kept in memory).</summary>
    void Flush() { }
    /// <summary>True only the first time it is asked for a window, ever (kept between runs where places are): the one look at
    /// whether a remembered place still holds the window's old default size, which is then dropped so the new size shows.</summary>
    bool TakeSizeCheck(string id) => false;
}

/// <summary>Places kept in the settings file between runs, one text value per window ("x,y,w,h"), as the official viewer keeps each
/// floater's rectangle in a setting of its own.</summary>
public sealed class SettingsFloaterPlaces(SettingsService settings) : IFloaterPlaces
{
    private readonly Dictionary<string, UiRect> _changed = new();

    /// <summary>The settings key of a window's place.</summary>
    public static string KeyOf(string id) => "floater_" + id + "_rect";

    public UiRect? Get(string id)
    {
        if (_changed.TryGetValue(id, out var held))
            return held;
        return Parse(settings.GetText(KeyOf(id)));
    }

    /// <summary>Held in memory until <see cref="Flush"/>, so a drag does not write the settings file on every step.</summary>
    public void Set(string id, UiRect rect) => _changed[id] = rect;

    public void Flush()
    {
        foreach (var (id, rect) in _changed)
            settings.SetText(KeyOf(id), Format(rect));
        _changed.Clear();
    }

    /// <summary>The windows already given the one-time size check, kept as one comma-separated setting.</summary>
    public const string SizeCheckKey = "floater_sizes_checked";

    public bool TakeSizeCheck(string id)
    {
        var done = (settings.GetText(SizeCheckKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (Array.IndexOf(done, id) >= 0)
            return false;
        settings.SetText(SizeCheckKey, string.Join(",", done.Append(id)));
        return true;
    }

    private static string Format(UiRect r) => string.Join(",", new[] { r.X, r.Y, r.W, r.H }.Select(v => MathF.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>"x,y,w,h" with finite numbers, else null (a damaged value is ignored and the window opens at its default place).</summary>
    private static UiRect? Parse(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 4 })
            return null;
        var v = new float[4];
        for (int i = 0; i < 4; i++)
            if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
                return null;
        return new UiRect(v[0], v[1], v[2], v[3]);
    }
}

/// <summary>Places kept for the life of the object.</summary>
public sealed class MemoryFloaterPlaces : IFloaterPlaces
{
    private readonly Dictionary<string, UiRect> _places = new();

    public UiRect? Get(string id)
    {
        lock (_places) return _places.TryGetValue(id, out var r) ? r : null;
    }

    public void Set(string id, UiRect rect)
    {
        lock (_places) _places[id] = rect;
    }
}

/// <summary>
/// How panels open and close: a registry of floaters, which are open, their stacking order and focus, where each sits,
/// and keeping every one inside the main view. Behaviour: opening raises and focuses; a floater opens where it was last
/// put, else cascaded from the top-left of the main view so each title bar shows; it never leaves the main view or goes
/// below its minimum size (a main view smaller than the minimum wins); closing hands the focus to the next one down.
/// Used from the engine's main thread only.
/// </summary>
public sealed class FloaterManager
{
    private const float FirstOffset = 40, CascadeStep = 28;

    private readonly IFloaterPlaces _places;
    private readonly Dictionary<string, FloaterSpec> _specs = new();
    private readonly List<FloaterSpec> _specOrder = new();
    private readonly List<(string Id, UiRect Rect)> _open = new();   // bottom to top
    private readonly Dictionary<string, UiRect> _wanted = new();      // where the user put each open window, before it was fitted to the main view
    private UiRect _bounds = new(0, 0, 1280, 720);

    /// <summary>A main view with no size is a minimised window; floaters are not fitted to it.</summary>
    private const float SmallestBounds = 1;

    public FloaterManager(IFloaterPlaces? places = null) => _places = places ?? new MemoryFloaterPlaces();

    /// <summary>The largest share of the main view a window takes when it opens for the first time (its own size or its content's).</summary>
    public const float FirstOpenShare = 0.85f;

    /// <summary>
    /// The size a window opens at when it has no remembered place: the larger of its own default and the official viewer's window it
    /// stands for (<see cref="OfficialFloaterSizes"/>) in each direction, no more than <see cref="FirstOpenShare"/> of the main view,
    /// and never below the window's minimum (a main view smaller than that minimum wins, as everywhere).
    /// </summary>
    public UiSize FirstOpenSize(FloaterSpec spec)
    {
        var official = OfficialFloaterSizes.Of(spec.Id);
        float w = Math.Max(spec.DefaultSize.W, official?.W ?? 0), h = Math.Max(spec.DefaultSize.H, official?.H ?? 0);
        w = Math.Max(Math.Min(w, _bounds.W * FirstOpenShare), spec.MinSize.W);
        h = Math.Max(Math.Min(h, _bounds.H * FirstOpenShare), spec.MinSize.H);
        return new UiSize(MathF.Round(w), MathF.Round(h));
    }

    /// <summary>Raised after any change a view must redraw for (open, close, move, resize, focus, bounds).</summary>
    public event Action? Changed;

    /// <summary>Raised when a closed floater opens (not when an open one is brought to the front).</summary>
    public event Action<string>? Opened;

    /// <summary>Raised when an open floater closes.</summary>
    public event Action<string>? Closed;

    public void Register(FloaterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_specs.TryAdd(spec.Id, spec))
            throw new ArgumentException($"floater '{spec.Id}' is already registered", nameof(spec));
        _specOrder.Add(spec);
    }

    public IReadOnlyList<FloaterSpec> Specs => _specOrder.ToArray();
    public FloaterSpec? SpecOf(string id) => _specs.GetValueOrDefault(id);

    /// <summary>The area floaters live in, as last set.</summary>
    public UiRect Bounds => _bounds;

    /// <summary>The area floaters live in (the main region). Floaters are kept inside it.</summary>
    public void SetBounds(UiRect bounds)
    {
        if (bounds == _bounds || bounds.W < SmallestBounds || bounds.H < SmallestBounds)
            return;
        _bounds = bounds;
        // fitted again from where the user put each one, so a main view that was briefly small gives the windows their size back
        for (int i = 0; i < _open.Count; i++)
            _open[i] = (_open[i].Id, Clamp(_specs[_open[i].Id], _wanted.GetValueOrDefault(_open[i].Id, _open[i].Rect)));
        Changed?.Invoke();
    }

    public bool Open(string id)
    {
        if (!_specs.TryGetValue(id, out var spec))
            return false;
        int at = IndexOf(id);
        if (at >= 0)
            return Raise(at);
        var remembered = _places.Get(id);
        // once per window: a place that still has the old default size was never resized, so it gives way to the new first-open size
        if (_places.TakeSizeCheck(id) && remembered is { } old && old.W == spec.DefaultSize.W && old.H == spec.DefaultSize.H && FirstOpenSize(spec) != spec.DefaultSize)
            remembered = null;
        var size = FirstOpenSize(spec);
        var wanted = remembered
            ?? new UiRect(_bounds.X + FirstOffset + CascadeStep * _open.Count, _bounds.Y + FirstOffset + CascadeStep * _open.Count, size.W, size.H);
        var rect = Clamp(spec, wanted);
        if (remembered is null)
            _firstOpening.Add(id);
        else
            _firstOpening.Remove(id);
        _open.Add((id, rect));
        _wanted[id] = rect;
        Changed?.Invoke();
        Opened?.Invoke(id);
        return true;
    }

    private readonly Dictionary<string, Func<bool>> _closeGuards = new();

    /// <summary>
    /// Lets a floater refuse to close: <paramref name="mayClose"/> is asked whenever a close is requested (the close button, a toggle,
    /// closing the focused one) and a false answer keeps the floater open (it is expected to ask the person what to do and close
    /// the floater itself afterwards, with <c>Close(id, force: true)</c>). Null removes the guard.
    /// </summary>
    public void SetCloseGuard(string id, Func<bool>? mayClose)
    {
        if (mayClose is null)
            _closeGuards.Remove(id);
        else
            _closeGuards[id] = mayClose;
    }

    public bool Close(string id) => Close(id, force: false);

    /// <summary>Closes a floater; <paramref name="force"/> skips its close guard.</summary>
    public bool Close(string id, bool force)
    {
        int at = IndexOf(id);
        if (at < 0)
            return false;
        if (!force && _closeGuards.TryGetValue(id, out var mayClose) && !mayClose())
            return false;
        _places.Set(id, _wanted.GetValueOrDefault(id, _open[at].Rect));
        _places.Flush();
        _wanted.Remove(id);
        _open.RemoveAt(at);
        Changed?.Invoke();
        Closed?.Invoke(id);
        return true;
    }

    /// <summary>Opens a closed floater or closes an open one; true when it is open afterwards.</summary>
    public bool Toggle(string id)
    {
        if (!_specs.ContainsKey(id))
            return false;
        if (IsOpen(id))
        {
            Close(id);
            return IsOpen(id);   // still open when its close guard said no
        }
        return Open(id);
    }

    /// <summary>What a toolbar button does to its window, as the official viewer's toolbar does (LLFloaterReg::toggleInstanceOrBringToFront):
    /// a closed window opens, an open window that is not in front is brought to the front, and the one in front closes. True when the window
    /// is open afterwards.</summary>
    public bool ToggleOrBringToFront(string id)
    {
        if (!_specs.ContainsKey(id))
            return false;
        if (IsOpen(id) && FocusedId != id)
            return Focus(id);
        return Toggle(id);
    }

    public bool IsOpen(string id) => IndexOf(id) >= 0;

    public bool Focus(string id)
    {
        int at = IndexOf(id);
        return at >= 0 && Raise(at);
    }

    public string? FocusedId => _open.Count == 0 ? null : _open[^1].Id;

    /// <summary>Closes the focused floater; false when none is open.</summary>
    public bool CloseFocused() => FocusedId is { } id && Close(id);

    public bool Move(string id, float x, float y)
    {
        int at = IndexOf(id);
        if (at < 0)
            return false;
        var r = _open[at].Rect;
        Replace(at, Clamp(_specs[id], r with { X = x, Y = y }));
        return true;
    }

    public bool Resize(string id, float w, float h)
    {
        int at = IndexOf(id);
        if (at < 0 || !_specs[id].Resizable)
            return false;
        var r = _open[at].Rect;
        Replace(at, Clamp(_specs[id], r with { W = w, H = h }));
        return true;
    }

    /// <summary>
    /// Resizes by dragging edges: <paramref name="start"/> is the window's rectangle when the drag began and (dx, dy) the pointer's
    /// travel since. A dragged edge moves with the pointer (a larger drag makes the window larger, a smaller one smaller), the
    /// opposite edge stays put, and the result keeps at least the window's minimum (or the main view where that is smaller) and stays
    /// inside the main view.
    /// </summary>
    public bool ResizeBy(string id, ResizeEdges edges, UiRect start, float dx, float dy)
    {
        int at = IndexOf(id);
        if (at < 0 || !_specs[id].Resizable)
            return false;
        var spec = _specs[id];
        float minW = Math.Min(spec.MinSize.W, _bounds.W), minH = Math.Min(spec.MinSize.H, _bounds.H);
        float left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        if (edges.HasFlag(ResizeEdges.Right))
            right = Math.Min(Math.Max(right + dx, left + minW), _bounds.Right);
        else if (edges.HasFlag(ResizeEdges.Left))
            left = Math.Min(Math.Max(left + dx, _bounds.X), right - minW);
        if (edges.HasFlag(ResizeEdges.Bottom))
            bottom = Math.Min(Math.Max(bottom + dy, top + minH), _bounds.Bottom);
        else if (edges.HasFlag(ResizeEdges.Top))
            top = Math.Min(Math.Max(top + dy, _bounds.Y), bottom - minH);
        Replace(at, Clamp(spec, new UiRect(left, top, right - left, bottom - top)));
        return true;
    }

    // windows opened at their first-open size (no remembered place) since they last opened
    private readonly HashSet<string> _firstOpening = new();

    /// <summary>Whether an open window opened at its first-open size, with no remembered place.</summary>
    public bool OpenedAtFirstSize(string id) => IndexOf(id) >= 0 && _firstOpening.Contains(id);

    /// <summary>
    /// Makes a window opened at its first-open size at least as large as its content needs, up to <see cref="FirstOpenShare"/> of
    /// the main view (never smaller than it was); true when that changed it. A window that opened at a remembered place keeps the
    /// size the person gave it. The shell asks once, when a window opens, so a later drag can make the window smaller than its
    /// content (which then scrolls).
    /// </summary>
    public bool FitToContent(string id, float width, float height)
    {
        int at = IndexOf(id);
        if (at < 0 || !_firstOpening.Contains(id))
            return false;
        var r = _open[at].Rect;
        float w = Math.Max(r.W, Math.Min(width, MathF.Round(_bounds.W * FirstOpenShare)));
        float h = Math.Max(r.H, Math.Min(height, MathF.Round(_bounds.H * FirstOpenShare)));
        var fitted = Clamp(_specs[id], r with { W = w, H = h });
        if (fitted == r)
            return false;
        Replace(at, fitted);
        return true;
    }

    /// <summary>Keeps where every open window is now (the viewer calls this as it closes).</summary>
    public void SavePlaces()
    {
        foreach (var (id, rect) in _open)
            _places.Set(id, _wanted.GetValueOrDefault(id, rect));
        _places.Flush();
    }

    public UiRect? RectOf(string id) => IndexOf(id) is var at and >= 0 ? _open[at].Rect : null;

    /// <summary>The open floaters, bottom to top.</summary>
    public IReadOnlyList<FloaterState> OpenFloaters =>
        _open.Select((f, i) => new FloaterState(f.Id, f.Rect, i, i == _open.Count - 1)).ToArray();

    private int IndexOf(string id) => _open.FindIndex(f => f.Id == id);

    /// <summary>Brings the floater at <paramref name="at"/> to the top; true either way (it is open).</summary>
    private bool Raise(int at)
    {
        if (at == _open.Count - 1)
            return true;
        var f = _open[at];
        _open.RemoveAt(at);
        _open.Add(f);
        Changed?.Invoke();
        return true;
    }

    private void Replace(int at, UiRect rect)
    {
        if (_open[at].Rect == rect)
            return;
        _open[at] = (_open[at].Id, rect);
        _wanted[_open[at].Id] = rect;
        Changed?.Invoke();
    }

    /// <summary>The rectangle made to fit: no smaller than the floater's minimum and no larger than the main view, then
    /// moved inside it. Where the main view is smaller than the minimum, the main view wins.</summary>
    private UiRect Clamp(FloaterSpec spec, UiRect r)
    {
        float w = Math.Min(Math.Max(r.W, spec.MinSize.W), _bounds.W);
        float h = Math.Min(Math.Max(r.H, spec.MinSize.H), _bounds.H);
        float x = Math.Min(Math.Max(r.X, _bounds.X), _bounds.Right - w);
        float y = Math.Min(Math.Max(r.Y, _bounds.Y), _bounds.Bottom - h);
        return new UiRect(x, y, w, h);
    }
}

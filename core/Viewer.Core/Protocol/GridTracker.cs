// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Links;

namespace Viewer.Core.Protocol;

/// <summary>
/// Which grid the agent is on, as a grid key ("host:port", <see cref="GridKeys"/>). Two grids have their own maps, so a region is known
/// by its grid and its handle together, and every simulator reference the transport makes carries the grid it was made on.
/// A teleport toward another grid (a Hypergrid address, going home, a landmark that names its grid) moves the current grid when it is
/// sent, so the region it lands in is made with that grid; a teleport that fails or is cancelled moves it back. When a region says which
/// grid it is on (its features' GridURL, OpenSim's SimulatorFeatures), that is taken as the truth. Thread-safe.
/// </summary>
public sealed class GridTracker(string home)
{
    private readonly object _lock = new();
    private readonly HashSet<string> _homeNames = new(StringComparer.OrdinalIgnoreCase) { home };
    private string _current = home;
    private string? _before;
    private bool _heardHome;
    private string? _lastHeard;

    /// <summary>The grid the agent logged in to.</summary>
    public string Home { get; } = home;

    /// <summary>The grid the agent is on (or teleporting toward).</summary>
    public string Current { get { lock (_lock) return _current; } }

    /// <summary>Whether the agent is on a grid other than its home grid.</summary>
    public bool Visiting => !string.Equals(Current, Home, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="key"/> names the home grid (its login address, or the address its own regions gave).</summary>
    public bool IsHome(string? key)
    {
        if (key is null)
            return false;
        lock (_lock) return _homeNames.Contains(key);
    }

    /// <summary>A teleport was sent toward <paramref name="destination"/> (a grid key), or inside the current grid (null).</summary>
    public void Leaving(string? destination)
    {
        lock (_lock)
        {
            if (destination is null)
                return;
            if (_homeNames.Contains(destination))
                destination = Home;
            if (string.Equals(destination, _current, StringComparison.OrdinalIgnoreCase))
                return;
            _before ??= _current;
            _current = destination;
        }
    }

    /// <summary>The teleport failed or was cancelled: the agent stays on the grid it was on.</summary>
    public void Failed()
    {
        lock (_lock)
        {
            if (_before is { } b)
                _current = b;
            _before = null;
        }
    }

    /// <summary>The teleport ended in an arrival.</summary>
    public void Arrived()
    {
        lock (_lock) _before = null;
    }

    /// <summary>The agent's region says which grid it is on (its grid or gatekeeper address). The first one heard at home is another name
    /// for the home grid (a grid's gatekeeper host can differ from its login host); later, a region that names another grid moves the
    /// current grid there (a teleport whose destination was not known when it was sent, such as an offer). An empty or unreadable
    /// address, or one heard while a teleport is on its way, changes nothing.</summary>
    public void Learned(string? gridUri)
    {
        if (GridKeys.OfUri(gridUri) is not { } key)
            return;
        lock (_lock)
        {
            if (_before is not null || string.Equals(key, _lastHeard, StringComparison.OrdinalIgnoreCase))
                return;
            _lastHeard = key;
            if (!_heardHome && string.Equals(_current, Home, StringComparison.OrdinalIgnoreCase))
            {
                _heardHome = true;
                _homeNames.Add(key);
                return;
            }
            _current = _homeNames.Contains(key) ? Home : key;
        }
    }
}

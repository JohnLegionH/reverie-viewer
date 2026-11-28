// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Social;

/// <summary>
/// When the Nearby list is read again: at once when it comes on screen, then once a second. A click holds the next read back for a
/// moment, so the rows do not change places between the two clicks of a double-click.
/// </summary>
public sealed class NearbyRefresh
{
    private const double Interval = 1.0, ClickHold = 0.6;
    private double _wait;

    public bool Due(double delta)
    {
        _wait -= delta;
        if (_wait > 0) return false;
        _wait = Interval;
        return true;
    }

    public void Clicked() => _wait = Math.Max(_wait, ClickHold);

    public void Hidden() => _wait = 0;
}

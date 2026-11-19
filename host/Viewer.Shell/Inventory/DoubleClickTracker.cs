// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>
/// Tells a double-click on a row of a list from two single clicks, by the row's id and not by its place: a list that is drawn again between
/// the two clicks (the contents of an object are read again a moment after a new script is made) loses the engine's own double-click, but
/// the row is still the same row. A click on the same row within <paramref name="windowMilliseconds"/> of the first is the double-click.
/// </summary>
public sealed class DoubleClickTracker(Func<long> nowMilliseconds, long windowMilliseconds = 500)
{
    private Guid _row;
    private long _at;

    /// <summary>A click on <paramref name="row"/>; true when it completes a double-click (the next click then starts again).</summary>
    public bool Click(Guid row)
    {
        long now = nowMilliseconds();
        if (row != Guid.Empty && row == _row && now - _at <= windowMilliseconds)
        {
            _row = Guid.Empty;
            return true;
        }
        _row = row;
        _at = now;
        return false;
    }

    /// <summary>Forgets the click (a click somewhere else, or the list was emptied).</summary>
    public void Reset() => _row = Guid.Empty;
}

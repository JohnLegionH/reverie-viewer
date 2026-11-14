// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Protocol;

/// <summary>
/// The teleport by region name that is still finding its region. The map is asked which region the name is, which takes seconds on a slow
/// grid, and nothing is sent to the grid until the answer comes, so the grid's own cancel message has nothing to stop. Cancelling during
/// that wait stops the teleport before it is sent; a new teleport by name stops the one before it. One at a time.
/// </summary>
internal sealed class PendingNamedTeleport
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;

    /// <summary>Starts a wait: the token ends when the teleport is cancelled, another begins, or <paramref name="outer"/> ends.</summary>
    public CancellationToken Begin(CancellationToken outer)
    {
        CancellationTokenSource? previous;
        var next = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_lock)
        {
            previous = _current;
            _current = next;
        }
        previous?.Cancel();
        return next.Token;
    }

    /// <summary>Stops the pending teleport; false when none is waiting.</summary>
    public bool Cancel()
    {
        CancellationTokenSource? current;
        lock (_lock)
        {
            current = _current;
            _current = null;
        }
        if (current is null)
            return false;
        current.Cancel();
        return true;
    }

    /// <summary>The teleport behind <paramref name="token"/> found its region (or gave up): it can no longer be cancelled here.</summary>
    public void End(CancellationToken token)
    {
        lock (_lock)
        {
            if (_current is not null && _current.Token == token)
                _current = null;
        }
    }
}

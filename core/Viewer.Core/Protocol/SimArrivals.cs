// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Protocol;

/// <summary>
/// Which simulators the adapter has announced as connected. The library raises its connect event only the first time it opens a
/// circuit to a simulator; a teleport back to a simulator it still holds only switches the current one. This tells the two apart:
/// a switch to a simulator announced before is a new arrival the core has not been told of.
/// </summary>
internal sealed class SimArrivals<T> where T : class
{
    private readonly HashSet<T> _announced = new(ReferenceEqualityComparer.Instance);

    /// <summary>The adapter raised the connect event for <paramref name="sim"/>.</summary>
    public void Announced(T sim)
    {
        lock (_announced) _announced.Add(sim);
    }

    /// <summary>The library's current simulator changed to <paramref name="current"/>: true when it was announced before, so the
    /// connect event will not follow and the adapter has to raise it again.</summary>
    public bool IsReArrival(T? current)
    {
        if (current is null)
            return false;
        lock (_announced) return _announced.Contains(current);
    }

    /// <summary>The library dropped <paramref name="sim"/>.</summary>
    public void Forget(T sim)
    {
        lock (_announced) _announced.Remove(sim);
    }

    public void Clear()
    {
        lock (_announced) _announced.Clear();
    }
}

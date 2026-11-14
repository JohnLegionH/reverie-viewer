// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The objects waiting to be presented, nearest first. Each object gets a ring number when it is added (<c>bucketOf</c>: smaller is
/// sooner; negative for what must go before everything, such as what an avatar wears), and <see cref="Nearest"/> gives the lowest ring,
/// the lowest id within it. <see cref="Rebucket"/> numbers every waiting object again when the camera has moved on.
/// The official viewer likewise rebuilds the nearest, largest spatial groups first (pipeline.cpp updateGeom; llspatialpartition.cpp getUpdateUrgency).
/// </summary>
public sealed class NearestFirstSet(Func<uint, int> bucketOf)
{
    private readonly SortedSet<(int Ring, uint Id)> _order = new();
    private readonly Dictionary<uint, int> _ringOf = new();

    public int Count => _order.Count;

    public bool Contains(uint id) => _ringOf.ContainsKey(id);

    public bool Add(uint id)
    {
        if (_ringOf.ContainsKey(id))
            return false;
        int ring = bucketOf(id);
        _ringOf[id] = ring;
        _order.Add((ring, id));
        return true;
    }

    public bool Remove(uint id)
    {
        if (!_ringOf.Remove(id, out int ring))
            return false;
        _order.Remove((ring, id));
        return true;
    }

    public void Clear()
    {
        _order.Clear();
        _ringOf.Clear();
    }

    /// <summary>The next object to present; the set must not be empty.</summary>
    public uint Nearest => _order.Min.Id;

    /// <summary>Numbers every waiting object again (the camera moved).</summary>
    public void Rebucket()
    {
        var ids = _ringOf.Keys.ToArray();
        _order.Clear();
        foreach (var id in ids)
        {
            int ring = bucketOf(id);
            _ringOf[id] = ring;
            _order.Add((ring, id));
        }
    }
}

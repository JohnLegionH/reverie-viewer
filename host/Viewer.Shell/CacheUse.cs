// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Which cached assets something still uses. Each user (an avatar presenter, a prim layer) reports the ids it uses now, replacing what it
/// reported before, and says when it is gone; an id is in use while any user lists it. Main thread only.
/// </summary>
public sealed class UseLedger
{
    private readonly Dictionary<object, HashSet<Guid>> _by = new(ReferenceEqualityComparer.Instance);

    /// <summary>What <paramref name="user"/> uses now (replaces its earlier report).</summary>
    public void Report(object user, IEnumerable<Guid> ids) => _by[user] = [.. ids];

    /// <summary><paramref name="user"/> is gone: its ids are no longer in use on its account.</summary>
    public void Release(object user) => _by.Remove(user);

    public bool InUse(Guid id)
    {
        foreach (var set in _by.Values)
            if (set.Contains(id)) return true;
        return false;
    }

    public int Users => _by.Count;
}

/// <summary>
/// The schedule that drops material data nobody uses (behaviour taken from the official viewer's llgltfmateriallist.cpp flushMaterials, in our own
/// words): each call looks at the larger of <see cref="MinScan"/> entries and a twentieth of them, continuing where the last call stopped; an entry
/// in use is marked active; an entry not in use that was active is marked inactive and given <see cref="InactiveSeconds"/> to be used again; an
/// entry not in use that was already inactive and whose time has passed is dropped. New entries count as active.
/// </summary>
public sealed class MaterialFlush
{
    public const int MinScan = 32;
    public const double InactiveSeconds = 30;

    private readonly Dictionary<Guid, (bool Active, double Expires)> _state = new();
    private Guid? _last;

    /// <summary>The entries to drop now. <paramref name="sortedKeys"/> is every cached id, in ascending order.</summary>
    public IReadOnlyList<Guid> Scan(double now, IReadOnlyList<Guid> sortedKeys, Func<Guid, bool> inUse)
    {
        var drop = new List<Guid>();
        int n = sortedKeys.Count;
        if (n == 0)
        {
            _last = null;
            return drop;
        }
        int look = Math.Min(Math.Max(MinScan, n / 20), n);
        int at = 0;
        if (_last is { } last)
        {
            at = FirstAfter(sortedKeys, last);
            if (at >= n) at = 0;   // the end of the list was reached: round again from the start
        }
        Guid examined = default;
        for (int i = 0; i < look; i++)
        {
            if (at >= n) at = 0;
            var key = sortedKeys[at++];
            examined = key;
            var (active, expires) = _state.TryGetValue(key, out var st) ? st : (true, 0.0);
            if (inUse(key))
                _state[key] = (true, expires);
            else if (!active && now > expires)
            {
                drop.Add(key);
                _state.Remove(key);
            }
            else if (active)
                _state[key] = (false, now + InactiveSeconds);
        }
        _last = examined;
        // entries that are gone from the cache are forgotten
        if (_state.Count > n * 2 + MinScan)
        {
            var present = sortedKeys.ToHashSet();
            foreach (var k in _state.Keys.Where(k => !present.Contains(k)).ToList()) _state.Remove(k);
        }
        return drop;
    }

    private static int FirstAfter(IReadOnlyList<Guid> sorted, Guid key)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].CompareTo(key) <= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

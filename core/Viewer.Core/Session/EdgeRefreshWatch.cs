// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// When a region's terrain changes at its edge, the regions next to it build their border normals again. The terrain of a new neighbour
/// arrives over several seconds, so a rebuild at every patch would redo the same chunks again and again: this reports a region as
/// changed once its patch count has been still for <see cref="Quiet"/>, or after <see cref="MaxWait"/> of constant change, and a region
/// that has gone (it no longer shows in the counts) at once. The first call only takes the baseline. Pure: the caller gives the time.
/// </summary>
public sealed class EdgeRefreshWatch<TKey> where TKey : notnull
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(4);

    private readonly Dictionary<TKey, int> _last = new();
    private readonly Dictionary<TKey, (DateTime First, DateTime Last)> _pending = new();
    private bool _baselined;

    /// <summary>The regions whose edges changed since the last call that reported them, given every region's current patch count.</summary>
    public IReadOnlyList<TKey> Poll(IReadOnlyDictionary<TKey, int> counts, DateTime now)
    {
        var report = new List<TKey>();
        if (!_baselined)
        {
            foreach (var (k, v) in counts) _last[k] = v;
            _baselined = true;
            return report;
        }
        foreach (var (k, v) in counts)
        {
            if (_last.TryGetValue(k, out var old) && old == v)
                continue;
            _last[k] = v;
            _pending[k] = _pending.TryGetValue(k, out var p) ? (p.First, now) : (now, now);
        }
        foreach (var k in _last.Keys.Where(k => !counts.ContainsKey(k)).ToList())
        {
            _last.Remove(k);
            _pending.Remove(k);
            report.Add(k);
        }
        foreach (var (k, p) in _pending.ToList())
            if (now - p.Last >= Quiet || now - p.First >= MaxWait)
            {
                report.Add(k);
                _pending.Remove(k);
            }
        return report;
    }
}

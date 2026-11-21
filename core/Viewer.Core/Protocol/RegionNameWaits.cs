// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Map;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Callers waiting for the grid to say which region has a given name. A name is asked of the map; the answers are map blocks, and the
/// one whose name is exactly the one asked for completes the wait with that region's handle. A block that echoes the name with the
/// no-region access level (255) is the grid's "there is none": it ends the wait at once with nothing. Names the map has already told
/// of (area replies included) are answered without asking again.
/// </summary>
internal sealed class RegionNameWaits
{
    /// <summary>The access level a map block carries for a cell, or a searched name, that has no region.</summary>
    public const int NoRegion = 255;

    private const int MaxKnown = 2048;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<TaskCompletionSource<ulong?>>> _waits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for the region called <paramref name="name"/> (case ignored); null when the grid says there is none, when no answer names it
    /// within <paramref name="timeout"/>, or the caller cancels. A name already answered returns at once.</summary>
    /// <param name="fresh">Ask afresh even when the name was answered before (a Hypergrid link gets a new handle at each lookup).</param>
    public Task<ulong?> WaitAsync(string name, TimeSpan timeout, CancellationToken ct, bool fresh = false)
    {
        var tcs = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!fresh && _known.TryGetValue(name, out var handle))
                return Task.FromResult<ulong?>(handle);
            if (!_waits.TryGetValue(name, out var list))
                _waits[name] = list = new();
            list.Add(tcs);
        }
        return FinishAsync(name, tcs, timeout, ct);
    }

    private async Task<ulong?> FinishAsync(string name, TaskCompletionSource<ulong?> tcs, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await using var _ = limit.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                if (_waits.TryGetValue(name, out var list))
                {
                    list.Remove(tcs);
                    if (list.Count == 0)
                        _waits.Remove(name);
                }
        }
    }

    /// <summary>Map blocks arrived: completes the waits they answer and remembers the names they give. A block with no name (a cell with no
    /// region) answers nothing.</summary>
    public void Offer(IEnumerable<MapBlockSize> blocks)
    {
        foreach (var b in blocks)
        {
            if (string.IsNullOrEmpty(b.Name))
                continue;
            ulong? handle = b.Access == NoRegion ? null : MapMath.HandleOf(b.GridX, b.GridY);
            List<TaskCompletionSource<ulong?>> answered;
            lock (_lock)
            {
                if (handle is { } h)
                {
                    if (_known.Count >= MaxKnown && !_known.ContainsKey(b.Name))
                        _known.Clear();   // a long session that has met this many names starts the list again
                    _known[b.Name] = h;
                }
                else
                    _known.Remove(b.Name);
                answered = _waits.TryGetValue(b.Name, out var list) ? list.ToList() : [];
            }
            foreach (var w in answered)
                w.TrySetResult(handle);
        }
    }
}

/// <summary>
/// What the grid said while map lookups by name were running: OpenSim tells why a lookup found nothing in an alert (MapSearchModule,
/// "No regions found with that name."), and a teleport whose lookup failed shows those words rather than a generic reason. Only alerts
/// that arrive while a lookup runs count; blank ones are ignored. Thread-safe.
/// </summary>
internal sealed class LookupRefusals
{
    private readonly object _lock = new();
    private int _open;
    private long _count;
    private string? _last;

    /// <summary>A lookup starts; the ticket is handed back to <see cref="End"/>.</summary>
    public long Begin()
    {
        lock (_lock)
        {
            _open++;
            return _count;
        }
    }

    /// <summary>The grid said something (an alert).</summary>
    public void Heard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (_lock)
        {
            if (_open == 0)
                return;
            _last = text.Trim();
            _count++;
        }
    }

    /// <summary>The lookup ended: the last words the grid said since it began, or null.</summary>
    public string? End(long ticket)
    {
        lock (_lock)
        {
            if (_open > 0)
                _open--;
            return _count > ticket ? _last : null;
        }
    }
}

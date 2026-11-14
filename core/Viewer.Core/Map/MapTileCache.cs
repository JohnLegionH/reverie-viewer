// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;

namespace Viewer.Core.Map;

public enum MapTileState { None, Loading, Ready, Missing, Failed }

/// <summary>
/// The map tiles fetched so far: fetched on demand through <c>fetch</c>, each tile once while it is in flight, at most
/// <see cref="MaxInFlight"/> at a time (the rest wait for the next ask), a tile that failed not asked for again before
/// <see cref="RetryAfter"/>, a tile the server does not have not before <see cref="MissingRetryAfter"/>, and the
/// pictures kept within a byte budget, the longest unused let go first. Any thread.
/// </summary>
public sealed class MapTileCache : IDisposable
{
    public const int MaxInFlight = 4;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MissingRetryAfter = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public MapTileState State;
        public byte[]? Bytes;
        public DateTimeOffset RetryAt;
        public long LastUse;
    }

    private readonly Func<MapTileKey, CancellationToken, Task<AssetFetchResult>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private readonly Dictionary<MapTileKey, Entry> _tiles = new();
    private readonly CancellationTokenSource _life = new();
    private long _bytes, _useClock;
    private int _inFlight, _version, _fetches, _kept;
    private bool _disposed;

    public MapTileCache(Func<MapTileKey, CancellationToken, Task<AssetFetchResult>> fetch, Func<DateTimeOffset>? clock = null, long maxBytes = 32L * 1024 * 1024)
    {
        _fetch = fetch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxBytes = maxBytes;
    }

    /// <summary>Raised (on a worker thread) when a tile arrived or failed.</summary>
    public event Action? Changed;

    /// <summary>Changes whenever a tile arrives, is let go or fails; the interface redraws when it moved.</summary>
    public int Version { get { lock (_lock) return _version; } }

    public int InFlight { get { lock (_lock) return _inFlight; } }
    public int Count { get { lock (_lock) return _kept; } }
    public long Bytes { get { lock (_lock) return _bytes; } }

    /// <summary>How many fetches were started in all.</summary>
    public int Fetches { get { lock (_lock) return _fetches; } }

    /// <summary>The state of a tile, starting its fetch when it is wanted, free to be fetched and a slot is open.</summary>
    public MapTileState Request(MapTileKey key)
    {
        bool start = false;
        MapTileState state;
        lock (_lock)
        {
            if (_disposed)
                return MapTileState.None;
            _tiles.TryGetValue(key, out var entry);
            var now = _clock();
            bool free = entry is null || entry.State == MapTileState.None
                || (entry.State is MapTileState.Failed or MapTileState.Missing && now >= entry.RetryAt);
            if (free && _inFlight < MaxInFlight)
            {
                entry ??= _tiles[key] = new Entry();
                entry.State = MapTileState.Loading;
                entry.Bytes = null;
                _inFlight++;
                _fetches++;
                start = true;
            }
            if (entry is { State: MapTileState.Ready })
                entry.LastUse = ++_useClock;
            state = entry?.State ?? MapTileState.None;
            if (!start && entry is { State: MapTileState.None })
                _tiles.Remove(key);
        }
        if (!start)
            return state;
        _ = RunAsync(key);
        // a fetch that ended at once (it threw, or its answer was ready) has already settled the tile
        lock (_lock)
            return _tiles.TryGetValue(key, out var settled) ? settled.State : MapTileState.None;
    }

    public bool TryGet(MapTileKey key, out byte[] bytes)
    {
        lock (_lock)
        {
            if (_tiles.TryGetValue(key, out var entry) && entry.State == MapTileState.Ready && entry.Bytes is { } b)
            {
                entry.LastUse = ++_useClock;
                bytes = b;
                return true;
            }
        }
        bytes = [];
        return false;
    }

    private async Task RunAsync(MapTileKey key)
    {
        AssetFetchResult result;
        try
        {
            result = await _fetch(key, _life.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = ex is OperationCanceledException && _life.IsCancellationRequested
                ? AssetFetchResult.Of(AssetFetchStatus.Cancelled)
                : AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
        bool changed;
        lock (_lock)
        {
            _inFlight--;
            if (_disposed)
                return;
            changed = Finish(key, result);
            if (changed)
                _version++;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Records a fetch's end under the lock. True when something a viewer could draw or say changed.</summary>
    private bool Finish(MapTileKey key, AssetFetchResult result)
    {
        if (!_tiles.TryGetValue(key, out var entry))
            return false;
        var now = _clock();
        switch (result.Status)
        {
            case AssetFetchStatus.Ok when result.Bytes.Length > 0 && result.Bytes.Length <= _maxBytes:
                entry.State = MapTileState.Ready;
                entry.Bytes = result.Bytes.ToArray();
                entry.LastUse = ++_useClock;
                _bytes += entry.Bytes.Length;
                _kept++;
                Trim(key);
                return true;
            case AssetFetchStatus.NotFound:
                entry.State = MapTileState.Missing;
                entry.RetryAt = now + MissingRetryAfter;
                return true;
            case AssetFetchStatus.Cancelled:
                entry.State = MapTileState.None;   // nothing is known: free to be asked for again
                _tiles.Remove(key);
                return false;
            default:
                entry.State = MapTileState.Failed;
                entry.RetryAt = now + RetryAfter;
                return true;
        }
    }

    /// <summary>Lets go of the longest unused pictures until the rest fit the budget; the tile that just arrived goes last.</summary>
    private void Trim(MapTileKey keep)
    {
        while (_bytes > _maxBytes)
        {
            MapTileKey? oldest = null;
            long least = long.MaxValue;
            foreach (var (k, e) in _tiles)
                if (e.State == MapTileState.Ready && !k.Equals(keep) && e.LastUse < least)
                {
                    least = e.LastUse;
                    oldest = k;
                }
            if (oldest is not { } drop)
                break;
            _bytes -= _tiles[drop].Bytes!.Length;
            _kept--;
            _tiles.Remove(drop);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _tiles.Clear();
            _bytes = 0;
            _kept = 0;
        }
        _life.Cancel();
        _life.Dispose();
    }
}

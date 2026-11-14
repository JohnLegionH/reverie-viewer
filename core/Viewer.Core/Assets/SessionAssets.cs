// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Assets;

public enum AssetFetchStatus { Ok, NotFound, Denied, Failed, Cancelled }

public sealed record AssetFetchResult(AssetFetchStatus Status, ReadOnlyMemory<byte> Bytes)
{
    /// <summary>The bytes are the start of the file only (a partial fetch); never stored as the asset.</summary>
    public bool Partial { get; init; }

    /// <summary>Where the bytes came from when not the transport: "shared" (the
    /// process's shared asset store) or "content" (the content store); null: the transport (the network).</summary>
    public string? Source { get; init; }

    public static AssetFetchResult Ok(ReadOnlyMemory<byte> bytes) => new(AssetFetchStatus.Ok, bytes);
    public static AssetFetchResult Of(AssetFetchStatus status) => new(status, ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// Asset bytes shared by every session in the process, keyed by asset id (hazard 2, ADR-006: shared bytes are
/// separate from permission to serve them). Holds only successfully fetched, immutable bytes: never a failure, never
/// anything about who fetched them. It is reached only through a <see cref="SessionAssets"/>, which authorises first.
/// An instance, owned by the host; never static. (The on-disk content store with its budget is.)
/// Bounded (<see cref="MaxBytes"/>): at the bound the least recently used files are let go; the
/// next request for one goes to the session's fetcher, which reads the content store first (the live viewer's textures
/// and meshes), so it is not fetched from the network again. Partial fetches never enter it (PartialTextureFetcher has
/// its own bound).
/// </summary>
public sealed class SharedAssetStore
{
    /// <summary>128 MiB for the complete files of every kind
    /// (textures and meshes share it, least recently used first). Measured: the region A v5 scene's whole content held 60.4 MB
    /// (140 files, meshes most of it), fetched in part 50.9 MB, region B 0.1 MB, so an ordinary region never reaches it; a
    /// larger region, or many region changes, is bounded, and a file let go comes back from the encrypted content store.</summary>
    public const long MaxBytes = 128L << 20;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, (ReadOnlyMemory<byte> Bytes, LinkedListNode<Guid> Use)> _entries = new();
    private readonly LinkedList<Guid> _uses = new();   // least recently used first
    private long _held, _peak, _letGo, _tooLarge;

    /// <param name="maxBytes">The bound (default <see cref="MaxBytes"/>; tests and the render harness pass others).</param>
    public SharedAssetStore(long? maxBytes = null)
    {
        Bound = maxBytes ?? MaxBytes;
        if (Bound < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    /// <summary>The most bytes of complete files held at once.</summary>
    public long Bound { get; }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>The bytes of the files held now.</summary>
    public long Bytes { get { lock (_lock) return _held; } }

    public SharedAssetStoreStats Stats { get { lock (_lock) return new(_entries.Count, _held, _peak, _letGo, _tooLarge); } }

    public bool Contains(Guid id) { lock (_lock) return _entries.ContainsKey(id); }

    internal bool TryGet(Guid id, out ReadOnlyMemory<byte> bytes)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var e)) { bytes = default; return false; }
            _uses.Remove(e.Use);
            _uses.AddLast(e.Use);   // used now: the last to go
            bytes = e.Bytes;
            return true;
        }
    }

    /// <summary>Holds a complete file (the first one for an id stays). At the bound the least recently used are let go: their
    /// bytes are only dropped, never reused or written, so a reader holding them is unaffected. A file larger than the
    /// bound is not held at all.</summary>
    internal void Put(Guid id, ReadOnlyMemory<byte> bytes)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(id)) return;
            if (bytes.Length > Bound) { _tooLarge++; return; }
            _entries[id] = (bytes, _uses.AddLast(id));
            _held += bytes.Length;
            while (_held > Bound && _uses.First is { } oldest)
            {
                _held -= _entries[oldest.Value].Bytes.Length;
                _entries.Remove(oldest.Value);
                _uses.RemoveFirst();
                _letGo++;
            }
            if (_held > _peak) _peak = _held;
        }
    }
}

/// <summary>Entries and bytes held now, the most bytes held at once, entries let go at the bound, and
/// files not held because they were larger than the bound.</summary>
public sealed record SharedAssetStoreStats(int Entries, long Bytes, long PeakBytes, long LetGo, long TooLarge);

/// <summary>
/// Remembered failures, for ONE session (the gateway's NegativeCache was keyed by asset id alone and shared, so one
/// session's failure was served to every other for 60 s: hazard 2b). Not thread-safe by itself; SessionAssets locks.
/// </summary>
internal sealed class NegativeCache(TimeSpan ttl, Func<DateTimeOffset> clock)
{
    private readonly Dictionary<Guid, (AssetFetchStatus Status, DateTimeOffset Until)> _entries = new();

    public bool TryGet(Guid id, out AssetFetchStatus status)
    {
        status = default;
        if (!_entries.TryGetValue(id, out var e))
            return false;
        if (clock() >= e.Until)
        {
            _entries.Remove(id);
            return false;
        }
        status = e.Status;
        return true;
    }

    public void Add(Guid id, AssetFetchStatus status) => _entries[id] = (status, clock() + ttl);

    public int Count => _entries.Count;

    public void Clear() => _entries.Clear();
}

/// <summary>
/// One session's view of assets (ADR-006, hazard 2):
///   (a) every request is authorised for THIS session before anything else, so a hit in the shared store never bypasses
///       authorisation;
///   (b) failures are remembered per session only: another session's failed fetch is never served here;
///   (c) in-flight fetches are joined only within the session, through its own fetcher (its own agent), so another
///       session's disconnect cannot fail this one's request;
///   (d) disposal cancels every in-flight fetch of this session and forgets its failures.
/// The authorisation decision is the session's (on OpenSim, texture and mesh GETs are not permission-gated, so a host
/// may allow them; that is a per-grid choice made explicitly, not a property of the cache).
/// </summary>
public sealed class SessionAssets : IDisposable
{
    public static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromSeconds(60);

    private readonly SharedAssetStore _store;
    private readonly Func<Guid, bool> _authorize;
    private readonly Func<Guid, CancellationToken, Task<AssetFetchResult>> _fetch;
    private readonly NegativeCache _negatives;
    private readonly Dictionary<Guid, Task<AssetFetchResult>> _inFlight = new();
    private readonly Func<Guid, int, bool, CancellationToken, Task<AssetFetchResult>>? _fetchLevel;
    private readonly Dictionary<(Guid, int, bool), Task<AssetFetchResult>> _levelsInFlight = new();
    private readonly CancellationTokenSource _life = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <param name="store">The process's shared byte store.</param>
    /// <param name="authorize">This session's decision for an asset id (true: this session may have it).</param>
    /// <param name="fetch">Fetches through this session's own transport.</param>
    /// <param name="fetchLevel">: optional; fetches only the bytes a discard level needs (level, "more":
    /// the level's estimate was not enough, fetch the rest). Its result may be <see cref="AssetFetchResult.Partial"/>.</param>
    public SessionAssets(SharedAssetStore store, Func<Guid, bool> authorize, Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch,
        TimeSpan? negativeTtl = null, Func<DateTimeOffset>? clock = null, Func<Guid, int, bool, CancellationToken, Task<AssetFetchResult>>? fetchLevel = null)
        : this(store, authorize, fetch, new NegativeCache(negativeTtl ?? DefaultNegativeTtl, clock ?? (() => DateTimeOffset.UtcNow)))
    {
        _fetchLevel = fetchLevel;
    }

    /// <summary>Whether <see cref="GetLevelAsync"/> fetches in part (otherwise it is <see cref="GetAsync"/>).</summary>
    public bool FetchesLevels => _fetchLevel is not null;

    /// <summary>Tests only: lets a negative control hand two sessions the same negative cache to prove the test sees it.</summary>
    internal SessionAssets(SharedAssetStore store, Func<Guid, bool> authorize, Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch, NegativeCache negatives)
    {
        _store = store;
        _authorize = authorize;
        _fetch = fetch;
        _negatives = negatives;
    }

    public int RememberedFailures { get { lock (_lock) return _negatives.Count; } }

    public async Task<AssetFetchResult> GetAsync(Guid id, CancellationToken ct)
    {
        Task<AssetFetchResult> task;
        lock (_lock)
        {
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
        if (!_authorize(id))
            return AssetFetchResult.Of(AssetFetchStatus.Denied);
        if (_store.TryGet(id, out var hit))
            return new AssetFetchResult(AssetFetchStatus.Ok, hit) { Source = "shared" };
        lock (_lock)
        {
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
            if (_negatives.TryGet(id, out var remembered))
                return AssetFetchResult.Of(remembered);
            if (!_inFlight.TryGetValue(id, out task!))
                _inFlight[id] = task = FetchAsync(id);
        }
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
    }

    /// <summary>
    /// The bytes discard level <paramref name="level"/> needs, with the same rules as
    /// <see cref="GetAsync"/>: authorised for this session first; a complete asset in the shared store is served whole;
    /// failures are remembered per session; in-flight requests are joined within the session (per id, level and
    /// <paramref name="more"/>); disposal cancels. A partial result is handed back but never put in the shared store (only
    /// complete files are shared). Without a level fetcher this is <see cref="GetAsync"/>.
    /// </summary>
    public async Task<AssetFetchResult> GetLevelAsync(Guid id, int level, bool more, CancellationToken ct)
    {
        if (_fetchLevel is null)
            return await GetAsync(id, ct).ConfigureAwait(false);
        Task<AssetFetchResult> task;
        lock (_lock)
        {
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
        if (!_authorize(id))
            return AssetFetchResult.Of(AssetFetchStatus.Denied);
        if (_store.TryGet(id, out var hit))
            return AssetFetchResult.Ok(hit);
        var key = (id, level, more);
        lock (_lock)
        {
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
            if (_negatives.TryGet(id, out var remembered))
                return AssetFetchResult.Of(remembered);
            if (!_levelsInFlight.TryGetValue(key, out task!))
                _levelsInFlight[key] = task = FetchLevelAsync(key);
        }
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
    }

    private async Task<AssetFetchResult> FetchLevelAsync((Guid Id, int Level, bool More) key)
    {
        AssetFetchResult r;
        try
        {
            // on the thread pool whatever the caller's context (see FetchAsync)
            r = await Task.Run(() => _fetchLevel!(key.Id, key.Level, key.More, _life.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            r = AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
        catch (Exception)
        {
            r = AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
        lock (_lock)
        {
            _levelsInFlight.Remove(key);
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
            if (r.Status is AssetFetchStatus.NotFound or AssetFetchStatus.Failed)
                _negatives.Add(key.Id, r.Status);
        }
        if (r.Status == AssetFetchStatus.Ok && !r.Partial)
            _store.Put(key.Id, r.Bytes);
        return r;
    }

    private async Task<AssetFetchResult> FetchAsync(Guid id)
    {
        AssetFetchResult r;
        try
        {
            // the fetch runs on the thread pool whatever the caller's context. Task.Yield
            // resumed on the caller's synchronization context, so a fetch started from the engine's main thread (the mesh
            // source) ran the content store's read, decrypt, encrypt and write there. The fetch is engine-free; it is also
            // started here inside the session's lock, so nothing of it may run on the caller's thread.
            r = await Task.Run(() => _fetch(id, _life.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            r = AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
        catch (Exception)
        {
            r = AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
        lock (_lock)
        {
            _inFlight.Remove(id);
            if (_disposed)
                return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
            if (r.Status is AssetFetchStatus.NotFound or AssetFetchStatus.Failed)
                _negatives.Add(id, r.Status);
        }
        if (r.Status == AssetFetchStatus.Ok)
            _store.Put(id, r.Bytes);
        return r;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _negatives.Clear();
            _inFlight.Clear();
            _levelsInFlight.Clear();
        }
        _life.Cancel();
        _life.Dispose();
    }
}

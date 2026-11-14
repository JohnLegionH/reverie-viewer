// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using Viewer.Core.Imaging;
using Viewer.Core.Net;

namespace Viewer.Core.Assets;

/// <summary>A transport that can fetch part of a texture: a range GET on the grid's texture capability.
/// The answer is checked by <see cref="RangeHttp"/>.</summary>
public interface ITextureRangeTransport
{
    Task<RangeAnswer> GetTextureRangeAsync(Guid id, long from, long? to, int capBytes, CancellationToken ct);

    /// <summary>Optional; every texture request this transport sends is timed into it.</summary>
    TextureTimeline? Timeline { get; set; }

    /// <summary>Optional; each texture's fetch class, the order its requests wait in (a transport without request slots
    /// of its own ignores it).</summary>
    TextureFetchClasses? FetchClasses { get => null; set { } }
}

/// <summary>What the partial fetcher did, for the harness and the live check (counts since it was made).</summary>
public sealed record PartialFetchStats(long RangeRequests, long BytesFetched, long WholeFallbacks, long WholeFallbackBytes, long Completed, long ServedPartial, long FromStore);

/// <summary>
/// Fetches only the bytes a texture's discard level needs.
///   - The first request (<see cref="TextureByteRange.FirstRequest"/>, the level's bytes for a
///     1024x1024 texture) gets the header (and the file's total size from the Content-Range) and usually the level; when
///     the header says the level needs more (<see cref="TextureByteRange.BytesFor"/>), the rest follows; refining asks for the rest
///     from where the last request stopped and appends. Full resolution, or "more" (the level's bytes did not decode),
///     asks for the rest of the file.
///   - Only an exact answer is used (<see cref="RangeHttp"/>): a 206 whose Content-Range matches, or a 200 taken as the
///     whole file. Anything else is a failed partial fetch: ONE whole-file fetch through <c>whole</c> (the transport's
///     ordinary texture fetch) follows; if that fails too, the texture fails. Never a loop.
///   - With a <see cref="ContentView"/>, the bytes so far are kept as a partial entry (encrypted like every entry,
///     surviving a restart), extended by later requests, and a complete file becomes an ordinary entry. A partial entry is
///     never served as complete.
///   - Byte caps: the whole-file limit (<see cref="J2kLimits.MaxBytes"/>) bounds every answer; cancellation (the token:
///     the session's dispose, and the transport's logout / disconnect / region change) stops every request.
/// One request per texture at a time (per-texture lock), so two levels of one texture never race to extend it.
/// The bytes held in memory for partly fetched textures stay within <see cref="HeldBound"/>
/// (<see cref="MaxHeldBytes"/>), the least recently used let go first; the content store's partial entry is the copy that
/// stays.
/// </summary>
public sealed class PartialTextureFetcher
{
    private readonly Func<Guid, long, long?, int, CancellationToken, Task<RangeAnswer>> _range;
    private readonly Func<Guid, CancellationToken, Task<AssetFetchResult>> _whole;
    private readonly ContentView? _store;
    private readonly string _kind;
    private readonly int _cap;
    private readonly Func<int, long?> _firstRequest;
    private readonly Dictionary<Guid, (byte[] Bytes, long Total, LinkedListNode<Guid> Use)> _partial = new();
    private readonly LinkedList<Guid> _uses = new();   // least recently used first
    private readonly object _heldLock = new();
    private long _held, _peakHeld, _evicted;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private long _requests, _bytes, _fallbacks, _fallbackBytes, _completed, _servedPartial, _fromStore;
    private readonly ConcurrentDictionary<string, long> _answers = new(), _fallbackReasons = new();
    /// <summary>Distinct fallback reasons kept (a bound on what a hostile server can make us hold); the rest are "other".</summary>
    private const int MaxReasons = 32;

    /// <param name="firstRequest">: the bytes of a texture's first request for a level, before anything
    /// is known about it (null: the whole file). Default <see cref="TextureByteRange.FirstRequest"/>; the render harness passes
    /// the candidates it measures.</param>
    public PartialTextureFetcher(Func<Guid, long, long?, int, CancellationToken, Task<RangeAnswer>> range,
        Func<Guid, CancellationToken, Task<AssetFetchResult>> whole, ContentView? store = null, string kind = "texture", int? capBytes = null,
        Func<int, long?>? firstRequest = null, long? maxHeldBytes = null)
    {
        HeldBound = maxHeldBytes ?? MaxHeldBytes;
        _range = range;
        _whole = whole;
        _store = store;
        _kind = kind;
        _cap = capBytes ?? J2kLimits.Default.MaxBytes;
        _firstRequest = firstRequest ?? TextureByteRange.FirstRequest;
    }

    /// <summary>The most bytes of partly fetched textures held in
    /// memory: 32 MiB, about ten times the most the region A v5 camera path held (3.0 MiB for 60 textures, measured). At the
    /// bound the least recently used are let go: their encrypted partial entry in the content store stays, so the next
    /// request reads it back and continues where it stopped; with no store, the texture starts again from its first byte.
    /// A texture larger than the bound is not held in memory at all.</summary>
    public const long MaxHeldBytes = 32L << 20;
    public long HeldBound { get; }

    /// <summary>Textures let go from memory at the bound.</summary>
    public long EvictedFromMemory { get { lock (_heldLock) return _evicted; } }

    /// <summary>The bytes of partly fetched textures held in memory now, and the most held at once.</summary>
    public long HeldBytes { get { lock (_heldLock) return _held; } }
    public long PeakHeldBytes { get { lock (_heldLock) return _peakHeld; } }

    public PartialFetchStats Stats => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _bytes), Interlocked.Read(ref _fallbacks),
        Interlocked.Read(ref _fallbackBytes), Interlocked.Read(ref _completed), Interlocked.Read(ref _servedPartial), Interlocked.Read(ref _fromStore));

    /// <summary>Range answers by kind: "206" (a Content-Range that matches), "200" (the whole
    /// file), "404", "other" (anything else: a failed partial fetch).</summary>
    public IReadOnlyDictionary<string, long> Answers => new Dictionary<string, long>(_answers);

    /// <summary>Whole-file fallbacks by reason ("first: " or "refine: ", then the answer's reason with its
    /// byte offsets left out, so equal reasons group).</summary>
    public IReadOnlyDictionary<string, long> FallbackReasons => new Dictionary<string, long>(_fallbackReasons);

    /// <summary>For <see cref="SessionAssets"/>' level fetcher: the bytes level <paramref name="level"/> needs (the whole file
    /// for level 0 or <paramref name="more"/>). The result is <see cref="AssetFetchResult.Partial"/> unless the file is complete.</summary>
    public async Task<AssetFetchResult> FetchLevelAsync(Guid id, int level, bool more, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchLockedAsync(id, level, more, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AssetFetchResult> FetchLockedAsync(Guid id, int level, bool more, CancellationToken ct)
    {
        if (_store is not null && _store.TryGet(_kind, id, out var stored))
        {
            Interlocked.Increment(ref _fromStore);
            return AssetFetchResult.Ok(stored);
        }
        var have = Held(id);
        if (have.Bytes is null && _store is not null && _store.TryGetPartial(_kind, id, out var sb, out var st) && st is long known)
        {
            have = (sb, known);
            Hold(id, have);
            Interlocked.Increment(ref _fromStore);
        }
        bool wantWhole = level <= 0 || more;
        if (have.Bytes is null)
        {
            // The first request: sized by the first-request rule for this level (or the whole file, when that is what is wanted)
            long? firstBytes = wantWhole ? null : _firstRequest(level);
            var first = await RequestAsync(id, 0, firstBytes is long fb ? Math.Max(fb, TextureByteRange.FirstChunk) - 1 : null, ct).ConfigureAwait(false);
            if (first.Kind == RangeAnswerKind.NotFound) return AssetFetchResult.Of(AssetFetchStatus.NotFound);
            if (first.Kind == RangeAnswerKind.Whole) return Complete(id, first.Body!);
            if (first.Kind != RangeAnswerKind.Part) return await FallbackAsync(id, "first", first, ct).ConfigureAwait(false);
            have = (first.Body!, first.Total!.Value);
            if (have.Bytes.Length >= have.Total) return Complete(id, have.Bytes);
            Keep(id, have);
        }
        long target = have.Total;   // the whole file
        if (!wantWhole)
        {
            // With the main header in hand, the layer rule applies (TextureByteRange.MaxLayers); otherwise the size alone
            if (J2kGuard.Validate(have.Bytes, null, out var header, allowTruncated: true) is null)
                target = Math.Min(have.Total, TextureByteRange.BytesFor(header!, level) ?? have.Total);
            else if (TrySize(have.Bytes, out int w, out int h))
                target = Math.Min(have.Total, TextureByteRange.BytesFor(w, h, level) ?? have.Total);
            else target = have.Total;   // no readable SIZ: nothing to estimate from
        }
        if (have.Bytes.Length >= target)
            return Served(have);
        // Refine: the rest from where the last request stopped (to the level's estimate, or the end of the file)
        long from = have.Bytes.Length;
        var next = await RequestAsync(id, from, target >= have.Total ? null : target - 1, ct).ConfigureAwait(false);
        if (next.Kind == RangeAnswerKind.Whole) return Complete(id, next.Body!);
        if (next.Kind != RangeAnswerKind.Part || next.Total != have.Total) return await FallbackAsync(id, "refine", next, ct).ConfigureAwait(false);
        var joined = new byte[have.Bytes.Length + next.Body!.Length];
        have.Bytes.CopyTo(joined, 0);
        next.Body.CopyTo(joined, have.Bytes.Length);
        if (joined.Length >= have.Total) return Complete(id, joined);
        have = (joined, have.Total);
        Keep(id, have);
        return Served(have);
    }

    private async Task<RangeAnswer> RequestAsync(Guid id, long from, long? to, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var a = await _range(id, from, to, _cap, ct).ConfigureAwait(false);
        Interlocked.Add(ref _bytes, a.BytesRead);
        _answers.AddOrUpdate(a.Kind switch { RangeAnswerKind.Part => "206", RangeAnswerKind.Whole => "200", RangeAnswerKind.NotFound => "404", _ => "other" }, 1, (_, n) => n + 1);
        return a;
    }

    /// <summary>A failed partial fetch: one whole-file fetch, then done (success or failure).</summary>
    private async Task<AssetFetchResult> FallbackAsync(Guid id, string stage, RangeAnswer answer, CancellationToken ct)
    {
        Interlocked.Increment(ref _fallbacks);
        string why = answer.Kind == RangeAnswerKind.Part ? "206 for another file size" : answer.Why ?? answer.Kind.ToString();
        why = System.Text.RegularExpressions.Regex.Replace(why, @"[0-9]+-[0-9]*(/[0-9]+)?", "A-B/T");   // byte ranges
        why = stage + ": " + System.Text.RegularExpressions.Regex.Replace(why, @"[0-9]{4,}", "N");       // sizes (status codes stay)
        if (answer.Status is int status && !why.Contains("answered", StringComparison.Ordinal)) why += " (status " + status + ")";
        if (!_fallbackReasons.ContainsKey(why) && _fallbackReasons.Count >= MaxReasons) why = stage + ": other";
        _fallbackReasons.AddOrUpdate(why, 1, (_, n) => n + 1);
        Drop(id);
        var r = await _whole(id, ct).ConfigureAwait(false);
        if (r.Status != AssetFetchStatus.Ok) return r;
        Interlocked.Add(ref _fallbackBytes, r.Bytes.Length);
        if (r.Bytes.Length > _cap) return AssetFetchResult.Of(AssetFetchStatus.Failed);
        return Complete(id, r.Bytes.ToArray());
    }

    private AssetFetchResult Complete(Guid id, byte[] bytes)
    {
        Interlocked.Increment(ref _completed);
        Drop(id);
        _store?.Complete(_kind, id, bytes);
        return AssetFetchResult.Ok(bytes);
    }

    private void Keep(Guid id, (byte[] Bytes, long Total) have)
    {
        Hold(id, have);
        _store?.PutPartial(_kind, id, have.Bytes, have.Total);
    }

    /// <summary>What is held in memory for the texture (null bytes when nothing), marked as just used.</summary>
    private (byte[] Bytes, long Total) Held(Guid id)
    {
        lock (_heldLock)
        {
            if (!_partial.TryGetValue(id, out var e)) return default;
            _uses.Remove(e.Use);
            _uses.AddLast(e.Use);
            return (e.Bytes, e.Total);
        }
    }

    /// <summary>Holds a texture's bytes so far in memory (replacing what was held for it), within <see cref="HeldBound"/>:
    /// the least recently used are let go first; one larger than the bound is not held.</summary>
    private void Hold(Guid id, (byte[] Bytes, long Total) have)
    {
        lock (_heldLock)
        {
            DropLocked(id);
            if (have.Bytes.Length > HeldBound) return;
            while (_held + have.Bytes.Length > HeldBound && _uses.First is { } oldest)
            {
                DropLocked(oldest.Value);
                _evicted++;
            }
            _partial[id] = (have.Bytes, have.Total, _uses.AddLast(id));
            _held += have.Bytes.Length;
            if (_held > _peakHeld) _peakHeld = _held;
        }
    }

    private void Drop(Guid id)
    {
        lock (_heldLock) DropLocked(id);
    }

    private void DropLocked(Guid id)
    {
        if (!_partial.Remove(id, out var old)) return;
        _uses.Remove(old.Use);
        _held -= old.Bytes.Length;
    }

    private AssetFetchResult Served((byte[] Bytes, long Total) have)
    {
        Interlocked.Increment(ref _servedPartial);
        return new AssetFetchResult(AssetFetchStatus.Ok, have.Bytes) { Partial = true };
    }

    /// <summary>The image size from the SIZ segment (SOC, SIZ first); false when it is not all there.</summary>
    internal static bool TrySize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        if (d.Length < 6 + 2 + 16 || d[0] != 0xFF || d[1] != 0x4F || d[2] != 0xFF || d[3] != 0x51) return false;
        long xs = BinaryPrimitives.ReadUInt32BigEndian(d[8..]), ys = BinaryPrimitives.ReadUInt32BigEndian(d[12..]);
        long xo = BinaryPrimitives.ReadUInt32BigEndian(d[16..]), yo = BinaryPrimitives.ReadUInt32BigEndian(d[20..]);
        if (xs <= xo || ys <= yo || xs - xo > 65535 || ys - yo > 65535) return false;
        width = (int)(xs - xo);
        height = (int)(ys - yo);
        return true;
    }
}

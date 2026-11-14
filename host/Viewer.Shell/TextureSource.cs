// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Diagnostics;
using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;

namespace Viewer.Shell;

/// <summary>
/// The presentation's non-blocking view of one session's textures. Bytes come through that session's
/// <see cref="SessionAssets"/> (authorisation first; the caller may plug the content store in via ContentView.Caching),
/// decoding is the guarded decoder (<see cref="LmvTexture"/>), off the main thread. Pending until done; arrivals are
/// queued for the main thread. One decode per asset per source (per session) and per requested level.
/// <see cref="TryGetLevel"/> decodes a discard level (CoreJ2K's reduced-resolution decode of the whole codestream)
/// and builds its mipmap chain in the same background task, so the main thread only uploads.
/// When the session fetches in part (<see cref="SessionAssets.FetchesLevels"/>), a level asks only
/// for the bytes it needs and is decoded from that prefix; if the prefix does not decode, more is fetched (the next finer
/// level's range, then the next, up to the whole file) and the level decoded again until it has its image
/// (<see cref="MoreFetches"/> and <see cref="PrefixFailures"/> count how often and why). A fetch cancelled by the transport
/// (logout, disconnect, region change) is not remembered: the next request tries again.
/// Decodes run on the texture decode threads (<see cref="DecodeScheduler"/>, by default the
/// process-wide <see cref="DecodeScheduler.Textures"/>), each with a deadline; a decode past it fails its level (the
/// texture keeps its placeholder) and is not retried until the texture is released.
/// </summary>
public sealed class TextureSource(SessionAssets assets, DecodeScheduler? decoder = null) : IDisposable
{
    private readonly DecodeScheduler _decoder = decoder ?? DecodeScheduler.Textures;
    private readonly ConcurrentDictionary<(Guid Id, int Level, bool Mips), (DecodedTexture? Texture, string? Why, object? Prepared)> _done = new();
    private readonly HashSet<(Guid, int, bool)> _started = new();
    // levels released while their decode was still running: dropped when they land (and their keys forgotten on the next call from the owning thread)
    private readonly ConcurrentDictionary<(Guid, int, bool), byte> _abandoned = new();
    private readonly ConcurrentQueue<(Guid, int, bool)> _forgotten = new();
    private readonly ConcurrentQueue<Guid> _arrived = new();
    private readonly CancellationTokenSource _life = new();
    private int _pending, _prefixDecodes, _moreFetches;
    private long _decodes, _decodeAllocated;
    private readonly ConcurrentDictionary<Guid, (long Asked, long FirstImage)> _firstImage = new();
    private readonly ConcurrentDictionary<string, long> _refusals = new();
    private readonly ConcurrentDictionary<string, long> _prefixFailures = new();
    private readonly ConcurrentDictionary<Guid, byte> _prefixLogged = new();

    /// <summary>Levels that ended without an image, by reason (the guard's or decoder's
    /// refusal, a deadline overrun, or the fetch's status); cancellations are not counted.</summary>
    public IReadOnlyDictionary<string, long> Refusals() => new Dictionary<string, long>(_refusals);

    /// <summary>Levels whose partial codestream did not decode, by the first refusal's reason (the guard's or the
    /// decoder's, for example CoreJ2K's IndexOutOfRangeException when the bytes end inside a packet header). Each one
    /// then fetched more of the file (<see cref="MoreFetches"/>).</summary>
    public IReadOnlyDictionary<string, long> PrefixFailures() => new Dictionary<string, long>(_prefixFailures);

    /// <summary>Optional; a texture whose partial codestream did not decode is logged once (its first such level),
    /// with the reason, how many more fetches it took and whether the level got its image.</summary>
    public CoreLog? Log { get; set; }

    /// <summary>Tests only: a decoder fault on partial input. Given the bytes of a partial codestream, a refusal reason
    /// makes its decode fail with that reason (the decoder is not called); null decodes as usual.</summary>
    internal Func<int, string?>? PrefixDecodeFault { get; set; }

    /// <summary>Every texture requested from this source (for the layer counts of the live check).</summary>
    public IReadOnlyCollection<Guid> Requested() => _firstImage.Keys.ToList();

    /// <summary>Optional; told when each texture is asked for, its fetch starts and ends, its decode is
    /// queued, starts and ends (the first time each happens), and when the host uploads it (<see cref="NoteUploaded"/>).</summary>
    public TextureTimeline? Timeline { get; set; }

    /// <summary>Each texture's fetch class (base colour or detail map), written by the residency manager and read by the
    /// fetches this source's assets make (the order their requests wait in).</summary>
    public Viewer.Core.Net.TextureFetchClasses FetchClasses { get; init; } = new();

    /// <summary>The host uploaded the texture (its first upload is its time to the screen).</summary>
    public void NoteUploaded(Guid id) => Timeline?.Mark(id, TextureStage.Uploaded);

    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Bytes of decoded pixels (RGBA and mipmap chains) this source still holds for levels nobody has released: none once every level that has been
    /// asked for is on the GPU and released.</summary>
    public long HeldPixelBytes => _done.Values.Sum(r => r.Texture is { } t ? (long)t.Rgba.Length + (t.Mips?.Length ?? 0) : 0L);

    /// <summary>GPU levels (<see cref="TryGetLevel(Guid, int, out DecodedTexture?)"/>) decoded and not released: each holds its pixels, or the engine image prepared
    /// off the main thread, on the CPU side until the host releases it once the level is on the GPU.</summary>
    public int HeldLevels => _done.Count(kv => kv.Key.Mips && kv.Value.Texture is not null);

    /// <summary>Which levels <see cref="HeldLevels"/> counts, for a report that has to say whose they are.</summary>
    public (Guid Id, int Level)[] HeldLevelKeys() => _done.Where(kv => kv.Key.Mips && kv.Value.Texture is not null).Select(kv => (kv.Key.Id, kv.Key.Level)).ToArray();
    /// <summary>Decodes that produced an image, and the managed bytes those decodes
    /// allocated on their decode thread (the decoder's own and ours).</summary>
    public long Decodes => Interlocked.Read(ref _decodes);
    public long DecodeAllocatedBytes => Interlocked.Read(ref _decodeAllocated);
    /// <summary>Levels decoded from a partial codestream.</summary>
    public int PrefixDecodes => Volatile.Read(ref _prefixDecodes);
    /// <summary>Levels whose partial codestream did not decode, so more of the file was fetched.</summary>
    public int MoreFetches => Volatile.Read(ref _moreFetches);

    /// <summary>Per texture, milliseconds from its first request to its first decoded image
    /// (textures still without one are left out).</summary>
    public IReadOnlyList<double> FirstImageMs() => _firstImage.Values.Where(v => v.FirstImage > 0)
        .Select(v => (v.FirstImage - v.Asked) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToList();

    /// <summary>Optional work run on the decode's background task for every GPU level (TryGetLevel)
    /// once it is decoded, so it stays off the main thread: the host builds its engine image there.
    /// when it returns an object, that object holds the image, so the level handed back afterwards carries no pixels
    /// (empty <see cref="DecodedTexture.Rgba"/>, no mip chain): its buffers went back to the decode pool. What it returns is
    /// handed back by <see cref="TryGetLevel(Guid, int, out DecodedTexture?, out object?)"/> and disposed (when
    /// IDisposable) when the level is released. A throw leaves the level without it (the host then prepares it itself).</summary>
    public Func<DecodedTexture, object?>? Prepare { get; set; }

    /// <summary>The full-resolution image (sculpt maps, terrain layers): level 0, no mipmaps.</summary>
    public MeshAvailability TryGet(Guid id, out DecodedTexture? texture) => Get((id, 0, false), out texture);

    /// <summary>Discard level <paramref name="level"/> (clamped to the coarsest the codestream has, so
    /// int.MaxValue asks for the coarsest) with its mipmap chain, for the GPU.</summary>
    public MeshAvailability TryGetLevel(Guid id, int level, out DecodedTexture? texture) => Get((id, level, true), out texture, out _);

    /// <summary>As <see cref="TryGetLevel(Guid, int, out DecodedTexture?)"/>, with what <see cref="Prepare"/>
    /// made for it off the main thread (null when there is no Prepare or it failed).</summary>
    public MeshAvailability TryGetLevel(Guid id, int level, out DecodedTexture? texture, out object? prepared) => Get((id, level, true), out texture, out prepared);

    private MeshAvailability Get((Guid Id, int Level, bool Mips) key, out DecodedTexture? texture) => Get(key, out texture, out _);

    private MeshAvailability Get((Guid Id, int Level, bool Mips) key, out DecodedTexture? texture, out object? prepared)
    {
        texture = null;
        prepared = null;
        while (_forgotten.TryDequeue(out var gone)) _started.Remove(gone);
        if (_done.TryGetValue(key, out var r))
        {
            // cancelled by the transport, not by our own dispose: forget it and ask again
            if (r.Texture is null && r.Why == nameof(AssetFetchStatus.Cancelled) && !_life.IsCancellationRequested && _done.TryRemove(key, out _))
                _started.Remove(key);
            else
            {
                texture = r.Texture;
                prepared = r.Prepared;
                return texture is null ? MeshAvailability.Unavailable : MeshAvailability.Ready;
            }
        }
        if (_started.Add(key))
        {
            _firstImage.TryAdd(key.Id, (System.Diagnostics.Stopwatch.GetTimestamp(), 0));
            var timeline = Timeline;
            timeline?.Mark(key.Id, TextureStage.Asked);
            Interlocked.Increment(ref _pending);
            var prepare = Prepare;
            _ = Task.Run(async () =>
            {
                (DecodedTexture?, string?) result;
                object? made = null;
                try
                {
                    timeline?.Mark(key.Id, TextureStage.FetchStarted);
                    var f = await assets.GetLevelAsync(key.Id, key.Level, more: false, _life.Token).ConfigureAwait(false);
                    if (f.Status == AssetFetchStatus.Ok) timeline?.Mark(key.Id, TextureStage.Fetched);
                    if (f.Status == AssetFetchStatus.Ok) AllocationMeter.Shared.Large(f.Bytes.Length, AllocKind.TextureDecode);   // the copy below
                    result = f.Status == AssetFetchStatus.Ok ? await DecodeAsync(key, f.Bytes.ToArray(), f.Partial).ConfigureAwait(false) : (null, f.Status.ToString());
                    if (f.Status == AssetFetchStatus.Ok && f.Partial)
                    {
                        if (result.Item1 is not null) Interlocked.Increment(ref _prefixDecodes);
                        else if (result.Item2 is not (DecodeDeadline or DecodingStopped))   // an overrun is never retried
                            result = await FetchMoreAsync(key, f.Bytes, result.Item2).ConfigureAwait(false);
                    }
                }
                catch (Exception e) { result = (null, e.GetType().Name); }
                if (result.Item1 is null && result.Item2 is { } why && why != nameof(AssetFetchStatus.Cancelled) && (_refusals.ContainsKey(why) || _refusals.Count < 64))
                    _refusals.AddOrUpdate(why, 1, (_, n) => n + 1);
                if (result.Item1 is not null && _firstImage.TryGetValue(key.Id, out var fi) && fi.FirstImage == 0)
                    _firstImage.TryUpdate(key.Id, (fi.Asked, System.Diagnostics.Stopwatch.GetTimestamp()), fi);
                if (key.Mips && result.Item1 is { } decoded && prepare is not null)
                {
                    var alloc = AllocationMeter.Shared.Begin(AllocKind.TexturePrepare);   // (measurement only)
                    try { made = prepare(decoded); } catch (Exception) { made = null; }
                    finally { alloc.End(); }
                    if (made is not null)
                    {
                        // the engine image holds its own copy now, so the level keeps its size, level
                        // and alpha class but not its pixels: the RGBA and the mip chain go back to the decode pool
                        result = (decoded with { Rgba = [], Mips = null }, result.Item2);
                        decoded.ReleasePixels();
                    }
                }
                // a prepared object is never lost. A decode restarted after a Release may finish second: the
                // first stays (another thread may be using its object) and the duplicate is disposed; a decode finishing
                // after Dispose disposes its own.
                if (!_done.TryAdd(key, (result.Item1, result.Item2, made)))
                    (made as IDisposable)?.Dispose();
                if (_abandoned.TryRemove(key, out _))
                {
                    // released while it ran: nobody wants it now
                    if (_done.TryRemove(key, out var unwanted)) (unwanted.Prepared as IDisposable)?.Dispose();
                    _forgotten.Enqueue(key);
                }
                if (_life.IsCancellationRequested && _done.TryRemove(key, out var late))
                    (late.Prepared as IDisposable)?.Dispose();
                Interlocked.Decrement(ref _pending);
                _arrived.Enqueue(key.Id);
            });
        }
        else _abandoned.TryRemove(key, out _);   // asked for again while it still runs: it is kept for this asker
        return MeshAvailability.Pending;
    }

    /// <summary>
    /// The level's partial codestream did not decode: fetch more and decode again, finer level by finer level (each
    /// level's range, which the fetcher continues from where it stopped), up to the whole file, so a level is never left
    /// without an image while the whole file decodes. A step that brings no new bytes is skipped without decoding;
    /// without a readable header the next step is the whole file. Counted by the first reason, logged once per texture.
    /// </summary>
    private async Task<(DecodedTexture?, string?)> FetchMoreAsync((Guid Id, int Level, bool Mips) key, ReadOnlyMemory<byte> prefix, string? why)
    {
        Interlocked.Increment(ref _moreFetches);
        string reason = why ?? "unknown";
        if (_prefixFailures.ContainsKey(reason) || _prefixFailures.Count < 64)
            _prefixFailures.AddOrUpdate(reason, 1, (_, n) => n + 1);
        // the coarsest level the codestream has bounds the steps (a request for int.MaxValue asks for the coarsest)
        int from = J2kGuard.Validate(prefix.Span, null, out var header, allowTruncated: true) is null
            ? Math.Min(key.Level, TextureLevels.Count(header!.Width, header.Height, header.DecompositionLevels) - 1)
            : 1;
        long tried = prefix.Length;
        int fetches = 0;
        (DecodedTexture?, string?) result = (null, why);
        for (int step = from - 1; ; step--)
        {
            bool whole = step <= 0;
            var f = await assets.GetLevelAsync(key.Id, whole ? key.Level : step, more: whole, _life.Token).ConfigureAwait(false);
            fetches++;
            if (f.Status != AssetFetchStatus.Ok) { result = (null, f.Status.ToString()); break; }
            if (f.Partial && !whole && f.Bytes.Length <= tried) continue;   // nothing new to decode
            tried = f.Bytes.Length;
            result = await DecodeAsync(key, f.Bytes.ToArray(), f.Partial).ConfigureAwait(false);
            if (result.Item1 is not null || !f.Partial || whole || result.Item2 is DecodeDeadline or DecodingStopped) break;
        }
        if (Log is { } log && _prefixLogged.TryAdd(key.Id, 0))
            log.Warn("texture.prefix.undecodable", CoreLog.F("asset", key.Id), CoreLog.F("level", key.Level), CoreLog.F("reason", reason),
                CoreLog.F("fetches", fetches), CoreLog.F("bytes", tried), CoreLog.F("outcome", result.Item1 is not null ? "image" : result.Item2));
        return result;
    }

    /// <summary>The decode on a decode thread, with its deadline; an overrun or a stopped decoder is a refusal.</summary>
    private async Task<(DecodedTexture?, string?)> DecodeAsync((Guid Id, int Level, bool Mips) key, byte[] b, bool prefix)
    {
        var timeline = Timeline;
        timeline?.Mark(key.Id, TextureStage.DecodeQueued);
        try
        {
            return await _decoder.Run(() =>
            {
                timeline?.Mark(key.Id, TextureStage.DecodeStarted);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var r = Decode(b, key.Level, key.Mips, prefix);
                if (r.Item1 is not null)
                {
                    Interlocked.Add(ref _decodeAllocated, GC.GetAllocatedBytesForCurrentThread() - allocated);
                    Interlocked.Increment(ref _decodes);
                    timeline?.Mark(key.Id, TextureStage.Decoded);
                }
                return r;
            }, key.Id.ToString()).ConfigureAwait(false);
        }
        catch (DecodeDeadlineException) { return (null, DecodeDeadline); }
        catch (DecodeStoppedException) { return (null, DecodingStopped); }
    }

    /// <summary>The refusal reasons of a decode that overran its deadline, or found decoding stopped.</summary>
    public const string DecodeDeadline = "decode deadline", DecodingStopped = "decoding stopped";

    /// <summary>Why a texture level has no image (null while pending or when it has one).</summary>
    public string? WhyUnavailable(Guid id, int level) => _done.TryGetValue((id, level, true), out var r) && r.Texture is null ? r.Why : null;

    private (DecodedTexture?, string?) Decode(byte[] b, int level, bool mips, bool prefix)
    {
        if (prefix && PrefixDecodeFault?.Invoke(b.Length) is { } fault) return (null, fault);
        var d = prefix ? LmvTexture.DecodeLevel(b, level, mips, prefix: true)
            : level == 0 && !mips ? LmvTexture.Decode(b) : LmvTexture.DecodeLevel(b, level, mips);
        return (d.Texture, d.Refusal);
    }

    /// <summary>The asset service said the texture does not exist (not refused, not failed): a sculpt shows its stand-in.</summary>
    public bool IsMissing(Guid id) => _done.TryGetValue((id, 0, false), out var r) && r.Texture is null && r.Why == nameof(AssetFetchStatus.NotFound);

    public bool TryTakeArrival(out Guid id) => _arrived.TryDequeue(out id);

    /// <summary>Forgets every decoded copy of the texture (an evicted texture); the next request fetches it again
    /// through the session's asset access, which reads the local content store before the network.</summary>
    public void Release(Guid id)
    {
        foreach (var key in _done.Keys.Where(k => k.Id == id).ToList())
            if (_done.TryRemove(key, out var r))
            {
                _started.Remove(key);
                (r.Prepared as IDisposable)?.Dispose();
            }
    }

    /// <summary>Forgets one level's decoded copy once it is on the GPU (the CPU keeps no copy of an uploaded level).</summary>
    public void ReleaseLevel(Guid id, int level)
    {
        while (_forgotten.TryDequeue(out var gone)) _started.Remove(gone);
        var key = (id, level, true);
        if (_done.TryRemove(key, out var r))
        {
            _started.Remove(key);
            (r.Prepared as IDisposable)?.Dispose();
            return;
        }
        if (!_started.Contains(key)) return;
        // still being decoded: it is dropped when it lands (unless it is asked for again first)
        _abandoned[key] = 0;
        if (_done.TryRemove(key, out r))
        {
            _abandoned.TryRemove(key, out _);
            _started.Remove(key);
            (r.Prepared as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        foreach (var r in _done.Values) (r.Prepared as IDisposable)?.Dispose();
        _done.Clear();
        assets.Dispose();
    }
}

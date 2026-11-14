// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;

namespace Viewer.Core.Assets;

/// <summary>The stages of a texture's way to the screen, each recorded the first time it happens.</summary>
/// <see cref="Wanted"/>: a face first needed it (before the residency granted it); <see cref="Asked"/>: the grant's request.
public enum TextureStage { Asked, FetchStarted, Fetched, DecodeQueued, DecodeStarted, Decoded, Uploaded, Wanted }

/// <summary>A distribution in milliseconds: how many, the median, the 95th percentile (nearest rank) and the largest.</summary>
public sealed record Dist(int Count, double P50, double P95, double Max)
{
    public static Dist Of(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return new(0, 0, 0, 0);
        double Rank(double p) => v[Math.Clamp((int)Math.Ceiling(p * v.Length) - 1, 0, v.Length - 1)];
        return new(v.Length, Math.Round(Rank(0.5), 1), Math.Round(Rank(0.95), 1), Math.Round(v[^1], 1));
    }
}

/// <summary>What <see cref="TextureTimeline.Summary"/> reports (milliseconds).</summary>
/// <param name="Stages">Per texture: "grant-wait" (a face wanted it -> the residency granted it), "queue" (asked -> fetch started: our own queueing), "fetch" (fetch started -> the first
/// bytes handed back, every request that took), "decode-queue" (queued -> a decode thread took it), "decode", "upload-wait"
/// (decoded -> uploaded by the main thread), "first-image" (asked -> decoded), "to-screen" (asked -> uploaded).</param>
/// <param name="Http">Per request: "headers" (sent -> response headers; includes any queue inside the HTTP client),
/// "body" (headers -> the end of the body), "total".</param>
/// <param name="RequestsBeforeFirstImage">How many textures needed n requests before their first image (n -> textures).</param>
/// <param name="InFlightPerSecond">The most requests in flight during each second since the first request.</param>
public sealed record TimelineSummary(int Textures, IReadOnlyDictionary<string, Dist> Stages, int Requests, long Bytes,
    IReadOnlyDictionary<string, long> Statuses, IReadOnlyDictionary<string, Dist> Http, int MaxInFlight,
    IReadOnlyList<int> InFlightPerSecond, IReadOnlyDictionary<int, int> RequestsBeforeFirstImage)
{
    /// <summary>The summary as log fields (valid log names).</summary>
    public IEnumerable<LogField> LogFields()
    {
        static string D(Dist d) => $"{d.Count}:{d.P50}/{d.P95}/{d.Max}";   // count:p50/p95/max ms
        yield return CoreLog.F("textures", Textures);
        yield return CoreLog.F("grant-wait-ms", D(Stages["grant-wait"]));
        yield return CoreLog.F("queue-ms", D(Stages["queue"]));
        yield return CoreLog.F("fetch-ms", D(Stages["fetch"]));
        yield return CoreLog.F("decode-queue-ms", D(Stages["decode-queue"]));
        yield return CoreLog.F("decode-ms", D(Stages["decode"]));
        yield return CoreLog.F("upload-wait-ms", D(Stages["upload-wait"]));
        yield return CoreLog.F("first-image-ms", D(Stages["first-image"]));
        yield return CoreLog.F("to-screen-ms", D(Stages["to-screen"]));
        yield return CoreLog.F("requests", Requests);
        yield return CoreLog.F("bytes", Bytes);
        yield return CoreLog.F("http-headers-ms", D(Http["headers"]));
        yield return CoreLog.F("http-body-ms", D(Http["body"]));
        yield return CoreLog.F("http-total-ms", D(Http["total"]));
        yield return CoreLog.F("max-in-flight", MaxInFlight);
    }
}

/// <summary>
/// Where a texture's seconds go. Per texture, the first time each <see cref="TextureStage"/> happens;
/// per HTTP request, the times to its response headers and to the end of its body, with bytes and status; and the
/// requests in flight over time. Measurement only: nothing reads it to decide anything. Bounded: at most
/// <see cref="MaxTextures"/> textures and <see cref="MaxRequests"/> requests are kept (the rest are counted, not kept).
/// Thread-safe; one per session (never static).
/// </summary>
public sealed class TextureTimeline
{
    public const int MaxTextures = 20_000, MaxRequests = 100_000;
    private const long Unset = long.MinValue;

    private readonly Func<long> _clock;
    private readonly double _ticksPerMs;
    private readonly ConcurrentDictionary<Guid, long[]> _stages = new();
    private readonly object _lock = new();
    private readonly List<Request> _requests = new();
    private readonly List<(long At, int InFlight)> _inFlight = new();
    private long _first = -1;
    private int _now, _max;

    public TextureTimeline() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    /// <param name="clock">Ticks (tests pass a controllable one).</param>
    public TextureTimeline(Func<long> clock, long ticksPerSecond)
    {
        _clock = clock;
        _ticksPerMs = ticksPerSecond / 1000.0;
    }

    public int InFlight { get { lock (_lock) return _now; } }

    /// <summary>The first time <paramref name="stage"/> happens for <paramref name="id"/> (later ones are ignored).</summary>
    public void Mark(Guid id, TextureStage stage)
    {
        long t = _clock();
        if (!_stages.TryGetValue(id, out var s))
        {
            if (_stages.Count >= MaxTextures) return;
            s = _stages.GetOrAdd(id, _ => Enumerable.Repeat(Unset, 8).ToArray());
        }
        Interlocked.CompareExchange(ref s[(int)stage], t, Unset);
    }

    /// <summary>An HTTP request is sent now; the answer's <see cref="IHttpTiming.Headers"/> and <see cref="IHttpTiming.Done"/> close it.</summary>
    public IHttpTiming BeginRequest(Guid id, string kind)
    {
        var r = new Request(this, id, kind, _clock());
        lock (_lock)
        {
            if (_first < 0) _first = r.Start;
            if (_requests.Count < MaxRequests) _requests.Add(r);
            _now++;
            _max = Math.Max(_max, _now);
            _inFlight.Add((r.Start, _now));
        }
        return r;
    }

    private void Ended(Request r)
    {
        lock (_lock)
        {
            _now--;
            _inFlight.Add((r.End, _now));
        }
    }

    private double Ms(long from, long to) => (to - from) / _ticksPerMs;

    /// <summary>The timeline's clock now, to time stages from (<see cref="Since"/>).</summary>
    public long Now => _clock();

    /// <summary>Per texture, milliseconds from <paramref name="from"/> (a <see cref="Now"/>)
    /// to its first ask, first decoded image and first upload; null for a stage not reached, negative for one before it.</summary>
    public IReadOnlyList<(Guid Id, double? AskedMs, double? DecodedMs, double? UploadedMs)> Since(long from)
    {
        double? At(long[] s, TextureStage stage) => s[(int)stage] == Unset ? null : Ms(from, s[(int)stage]);
        return _stages.ToArray().Select(kv => (kv.Key, At(kv.Value, TextureStage.Asked), At(kv.Value, TextureStage.Decoded), At(kv.Value, TextureStage.Uploaded))).ToList();
    }

    public TimelineSummary Summary()
    {
        var stages = _stages.ToArray();
        IEnumerable<double> Between(TextureStage a, TextureStage b) =>
            stages.Select(kv => kv.Value).Where(s => s[(int)a] != Unset && s[(int)b] != Unset).Select(s => Ms(s[(int)a], s[(int)b]));
        var dists = new Dictionary<string, Dist>
        {
            ["grant-wait"] = Dist.Of(Between(TextureStage.Wanted, TextureStage.Asked)),
            ["queue"] = Dist.Of(Between(TextureStage.Asked, TextureStage.FetchStarted)),
            ["fetch"] = Dist.Of(Between(TextureStage.FetchStarted, TextureStage.Fetched)),
            ["decode-queue"] = Dist.Of(Between(TextureStage.DecodeQueued, TextureStage.DecodeStarted)),
            ["decode"] = Dist.Of(Between(TextureStage.DecodeStarted, TextureStage.Decoded)),
            ["upload-wait"] = Dist.Of(Between(TextureStage.Decoded, TextureStage.Uploaded)),
            ["first-image"] = Dist.Of(Between(TextureStage.Asked, TextureStage.Decoded)),
            ["to-screen"] = Dist.Of(Between(TextureStage.Asked, TextureStage.Uploaded)),
        };
        Request[] reqs;
        (long At, int InFlight)[] flight;
        int max;
        long first;
        lock (_lock)
        {
            reqs = _requests.ToArray();
            flight = _inFlight.ToArray();
            max = _max;
            first = _first;
        }
        var done = reqs.Where(r => r.End >= 0).ToArray();
        var statuses = done.GroupBy(r => r.Status?.ToString() ?? "none").ToDictionary(g => g.Key, g => (long)g.Count());
        var http = new Dictionary<string, Dist>
        {
            ["headers"] = Dist.Of(done.Where(r => r.HeadersAt >= 0).Select(r => Ms(r.Start, r.HeadersAt))),
            ["body"] = Dist.Of(done.Where(r => r.HeadersAt >= 0).Select(r => Ms(r.HeadersAt, r.End))),
            ["total"] = Dist.Of(done.Select(r => Ms(r.Start, r.End))),
        };
        var perSecond = new List<int>();
        int level = 0;
        foreach (var (at, n) in flight.OrderBy(f => f.At))
        {
            int sec = (int)(Ms(first, at) / 1000);
            while (perSecond.Count <= sec) perSecond.Add(level);
            level = n;
            perSecond[sec] = Math.Max(perSecond[sec], n);
        }
        var before = new Dictionary<int, int>();
        foreach (var (id, s) in stages)
        {
            long decoded = s[(int)TextureStage.Decoded];
            if (decoded == Unset) continue;
            int n = reqs.Count(r => r.Id == id && r.Start <= decoded);
            before[n] = before.GetValueOrDefault(n) + 1;
        }
        return new TimelineSummary(stages.Length, dists, done.Length, done.Sum(r => r.Bytes), statuses, http, max, perSecond, before);
    }

    private sealed class Request(TextureTimeline owner, Guid id, string kind, long start) : IHttpTiming
    {
        public readonly Guid Id = id;
        public readonly string Kind = kind;
        public readonly long Start = start;
        public long HeadersAt = -1, End = -1, Bytes;
        public int? Status;
        private int _done;

        public void Headers(int status)
        {
            if (HeadersAt >= 0) return;
            Status = status;
            HeadersAt = owner._clock();
        }

        public void Done(long bytes)
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            Bytes = bytes;
            End = owner._clock();
            owner.Ended(this);
        }
    }
}
